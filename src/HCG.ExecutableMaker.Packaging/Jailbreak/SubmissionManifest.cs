using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.Mobile;

namespace HCG.ExecutableMaker.Packaging.Jailbreak;

/// <summary>
/// HCG Repo Distribution v2 submission manifest, format <c>hcg.repo.artifact-submission</c> version 1
/// (HCG Repo docs/DISTRIBUTION_V2.md, docs/schemas/artifact-submission-v1.schema.json; tools/hcgrepo/submission.py is
/// authoritative). Every field is a claim HCG Repo re-checks against the package itself. Maker writes only what it
/// measured: the provider is <c>executable-maker-local</c> for a local build (no CI run fields at all) and
/// <c>github-actions</c> for a CI build with its run, artifact and run repository.
/// </summary>
public static partial class SubmissionManifest
{
    public const string Format = "hcg.repo.artifact-submission";
    public const int FormatVersion = 1;
    public const string FileName = "submission.json";
    private static readonly string[] ScriptNames = ["preinst", "postinst", "prerm", "postrm", "config"];

    public sealed record Result(JsonObject? Manifest, IReadOnlyList<string> Problems)
    {
        public bool Complete => Manifest is not null && Problems.Count == 0;
    }

    public static Result Build(RootlessAppPackageSpec spec, IosAppBundle app, RootlessAppDebValidator.Result validation, PackageProvenance p,
        HandoffContext? context, DateTimeOffset packagedAt, string debName, RootlessLifecycleSimulator.Result? lifecycle = null,
        IReadOnlyList<string>? signedFiles = null)
    {
        var f = validation.Facts;
        var github = p.CiRunId is > 0 && p.Local is null;
        var toolchain = string.Join(", ", new[] { p.Local?.Toolchain ?? LocalBuild.ToolchainFrom(app) ?? p.BuiltWith, p.Local?.BuiltBy ?? p.PackagedBy }
            .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct());
        var checks = new JsonArray(validation.Checks.Select(c => (JsonNode?)new JsonObject { ["check"] = c.Name, ["status"] = c.Status.ToString(), ["detail"] = c.Detail })
            .Concat((lifecycle?.Checks ?? []).Select(c => (JsonNode?)new JsonObject { ["check"] = "Lifecycle · " + c.Name, ["status"] = c.Status.ToString(), ["detail"] = c.Detail })).ToArray());
        var build = new JsonObject
        {
            ["provider"] = github ? "github-actions" : "executable-maker-local",
            ["toolchain"] = toolchain,
            ["builtAt"] = (p.Local?.BuiltAt ?? packagedAt).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
        };
        if (github)
        {
            build["runId"] = p.CiRunId;
            build["runUrl"] = p.CiRunUrl;
            build["artifactName"] = p.CiArtifact;
            build["runRepository"] = p.ImportRepository;
            build["runCommit"] = p.ImportCommit;
        }
        var m = new JsonObject
        {
            ["format"] = Format, ["formatVersion"] = FormatVersion,
            ["package"] = new JsonObject { ["identifier"] = spec.PackageId, ["version"] = spec.Version, ["architecture"] = RootlessAppPackager.Architecture, ["name"] = spec.Name },
            ["artifact"] = new JsonObject { ["filename"] = debName, ["sha256"] = f.Sha256, ["size"] = f.Size },
            ["compatibility"] = new JsonObject
            {
                ["minimumOS"] = f.MinimumOS, ["environment"] = "rootless",
                // Named jailbreaks are claims of tested compatibility; no device test is recorded, so none are claimed.
                ["jailbreaks"] = new JsonArray(),
                ["deviceFamilies"] = new JsonArray(app.DeviceFamilies.Select(d => (JsonNode?)d).ToArray()),
            },
            ["source"] = new JsonObject
            {
                ["repository"] = p.Repository, ["commit"] = p.Commit?.ToLowerInvariant(), ["dirty"] = p.SourceDirty,
                ["branch"] = context?.Revision?.Branch,
            },
            ["build"] = build,
            ["dependencies"] = new JsonArray(DependencyNames(f.Control.GetValueOrDefault("Depends")).Select(d => (JsonNode?)d).ToArray()),
            ["maintainerScripts"] = new JsonObject { ["declared"] = new JsonArray(f.Scripts.Keys.Where(ScriptNames.Contains).OrderBy(s => s, StringComparer.Ordinal).Select(s => (JsonNode?)s).ToArray()) },
            ["validation"] = new JsonObject
            {
                ["tool"] = p.PackagedBy ?? "HCG Executable Maker", ["checks"] = checks, ["passed"] = validation.Passed && lifecycle?.Passed != false,
                ["lifecycle"] = lifecycle is null ? "not simulated" : lifecycle.Summary, ["deviceTested"] = false,
            },
            ["channel"] = "candidate",
            ["origin"] = new JsonObject
            {
                ["kind"] = "executable-maker-unleashify",
                ["bundleIdentifier"] = f.BundleId, ["installPath"] = f.AppPath,
                ["targetDevices"] = new JsonArray((context?.Devices ?? []).Select(d => (JsonNode?)new JsonObject
                {
                    ["name"] = d.Device.Name, ["ios"] = d.Device.IosVersion, ["compatible"] = d.Compatible, ["tested"] = false,
                }).ToArray()),
                ["sourceVisibility"] = p.Local?.SourceVisibility,
                ["host"] = p.Local?.Host,
            },
        };
        if (Signing(f, signedFiles) is { } signing) m["signing"] = signing;
        var problems = Validate(m);
        return new(m, problems);
    }

    /// <summary>
    /// The <c>signing</c> claim: <c>ldid</c> with the files Maker pseudo-signed (as package paths), and the exact
    /// entitlements of every binary that carries any. Files that arrived signed (e.g. a NuGet framework's ad hoc
    /// signature) are not listed as signed by Maker. Omitted when Maker signed nothing: then it has no method to claim.
    /// </summary>
    public static JsonObject? Signing(RootlessAppDebValidator.Facts f, IReadOnlyList<string>? signedFiles)
    {
        if (signedFiles is not { Count: > 0 } || f.AppPath is null) return null;
        var prefix = f.AppPath.TrimStart('/') + "/";
        var ents = new JsonObject();
        foreach (var (path, dict) in EntitlementsByPath(f)) ents[path] = (JsonObject)ToJson(dict)!;
        return new JsonObject
        {
            ["method"] = "ldid",
            ["signedFiles"] = new JsonArray(signedFiles.Order(StringComparer.Ordinal).Select(r => (JsonNode?)(prefix + r)).ToArray()),
            ["entitlements"] = ents,
        };
    }

    /// <summary>
    /// Package path (no leading slash, as HCG Repo names binaries) → entitlements, for every binary that carries any and
    /// whose entitlements JSON and TOML can state exactly (plist data and dates cannot; such a binary is left out, and
    /// HCG Repo's own review then asks for its entitlements).
    /// </summary>
    public static IReadOnlyDictionary<string, Dictionary<string, object>> EntitlementsByPath(RootlessAppDebValidator.Facts f)
    {
        var d = new SortedDictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
        if (f.AppPath is null) return d;
        foreach (var (rel, xml) in f.Entitlements)
            try
            {
                var dict = PlistReader.ParseDictionary(System.Text.Encoding.UTF8.GetBytes(xml));
                if (ToJson(dict) is not null) d[f.AppPath.TrimStart('/') + "/" + rel] = dict;
            }
            catch (Exception e) when (e is FormatException or InvalidDataException or InvalidCastException or System.Xml.XmlException) { }
        return d;
    }

    /// <summary>A property-list value as JSON; null when it has no exact JSON form (data, date).</summary>
    public static JsonNode? ToJson(object v)
    {
        switch (v)
        {
            case bool b: return JsonValue.Create(b);
            case string s: return JsonValue.Create(s);
            case long l: return JsonValue.Create(l);
            case double x: return JsonValue.Create(x);
            case List<object> a:
                var arr = new JsonArray();
                foreach (var item in a) { if (ToJson(item) is not { } j) return null; arr.Add(j); }
                return arr;
            case Dictionary<string, object> o:
                var obj = new JsonObject();
                foreach (var (k, item) in o.OrderBy(kv => kv.Key, StringComparer.Ordinal)) { if (ToJson(item) is not { } j) return null; obj[k] = j; }
                return obj;
            default: return null;
        }
    }

    /// <summary>A property-list value as a TOML value (inline tables for dictionaries), for package.toml [review.entitlements].</summary>
    public static string ToToml(object v) => v switch
    {
        bool b => b ? "true" : "false",
        string s => TomlString(s),
        long l => l.ToString(System.Globalization.CultureInfo.InvariantCulture),
        double x => x.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        List<object> a => "[" + string.Join(", ", a.Select(ToToml)) + "]",
        Dictionary<string, object> o => "{ " + string.Join(", ", o.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{TomlString(kv.Key)} = {ToToml(kv.Value)}")) + " }",
        _ => throw new InvalidDataException("no TOML form for " + v.GetType().Name),
    };

    /// <summary>A TOML basic string (JSON's escapes are valid TOML escapes).</summary>
    public static string TomlString(string s) => JsonSerializer.Serialize(s, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    /// <summary>Package names in a Depends field (alternatives included), as HCG Repo compares them.</summary>
    public static IReadOnlyList<string> DependencyNames(string? depends) =>
        (depends ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(g => g.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(a => a.Split([' ', '(', ':'], 2)[0].Trim()).Where(n => n.Length > 0).Distinct().ToList();

    [GeneratedRegex(@"^[0-9a-f]{64}$")] private static partial Regex Sha256Re();
    [GeneratedRegex(@"^[0-9a-f]{40}$")] private static partial Regex Sha40Re();
    [GeneratedRegex(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")] private static partial Regex RepoRe();
    [GeneratedRegex(@"^\d+(\.\d+){0,2}$")] private static partial Regex OsRe();

    /// <summary>The rules of HCG Repo's <c>validate_manifest</c> (format, provenance, channel); empty when the manifest is well formed.</summary>
    public static IReadOnlyList<string> Validate(JsonObject m)
    {
        var p = new List<string>();
        string? S(string path) => Node(m, path) is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        if (S("format") != Format) p.Add($"format must be {Format}");
        if (Node(m, "formatVersion")?.GetValue<int>() != FormatVersion) p.Add($"formatVersion must be {FormatVersion}");
        if (!RootlessAppPackager.PackageIdRegex().IsMatch(S("package.identifier") ?? "")) p.Add("package.identifier must be a Debian package name");
        if (!RootlessAppPackager.VersionRegex().IsMatch(S("package.version") ?? "")) p.Add("package.version must be a valid Debian version");
        if (!Sha256Re().IsMatch(S("artifact.sha256") ?? "")) p.Add("artifact.sha256 must be 64 lowercase hex characters");
        if (Node(m, "artifact.size") is not JsonValue size || !size.TryGetValue<long>(out var n) || n <= 0) p.Add("artifact.size must be a positive integer");
        if (S("artifact.filename") is not { } fn || !fn.EndsWith(".deb", StringComparison.Ordinal) || fn.Contains('/') || fn.Contains('\\') || fn.StartsWith('.')) p.Add("artifact.filename must be a plain *.deb file name");
        if (!OsRe().IsMatch(S("compatibility.minimumOS") ?? "")) p.Add("compatibility.minimumOS is required (e.g. 17.0)");
        if (S("compatibility.environment") != "rootless" || S("package.architecture") != RootlessAppPackager.Architecture) p.Add("a rootless package is iphoneos-arm64");
        if (!RepoRe().IsMatch(S("source.repository") ?? "")) p.Add("source.repository must be OWNER/NAME");
        if (!Sha40Re().IsMatch(S("source.commit") ?? "")) p.Add("source.commit must be the full 40-character commit the package was built from");
        if (Node(m, "source.dirty") is not JsonValue dirty || !dirty.TryGetValue<bool>(out var d) || d) p.Add("source.dirty must be false (built from a clean commit)");
        var provider = S("build.provider");
        if (string.IsNullOrWhiteSpace(S("build.toolchain"))) p.Add("build.toolchain is required");
        if (!DateTimeOffset.TryParse(S("build.builtAt"), out _)) p.Add("build.builtAt must be an ISO 8601 time");
        if (provider is "github-actions" or "github-actions-private")
        {
            if (Node(m, "build.runId") is not JsonValue r || !r.TryGetValue<long>(out var id) || id <= 0) p.Add("build.runId is required for a GitHub build");
            if (string.IsNullOrEmpty(S("build.artifactName"))) p.Add("build.artifactName is required for a GitHub build");
            if (!RepoRe().IsMatch(S("build.runRepository") ?? "")) p.Add("build.runRepository is required for a GitHub build");
        }
        else if (provider is "executable-maker-local")
        {
            foreach (var k in new[] { "runId", "runUrl", "artifactName", "runRepository" })
                if (Node(m, "build." + k) is not null) p.Add($"build.{k} is only for GitHub Actions builds; a local build has no CI run");
        }
        else p.Add("build.provider must be executable-maker-local or github-actions");
        if (Node(m, "maintainerScripts.declared") is not JsonArray) p.Add("maintainerScripts.declared is required");
        if (Node(m, "signing") is JsonObject sig)
        {
            if (S("signing.method") is not ("ldid" or "apple-development" or "apple-distribution" or "unsigned")) p.Add("signing.method must be ldid, apple-development, apple-distribution or unsigned");
            if (sig["signedFiles"] is not null and not JsonArray) p.Add("signing.signedFiles must be a list of paths inside the package");
            if (sig["entitlements"] is not null and not JsonObject || (sig["entitlements"] is JsonObject eo && eo.Any(kv => kv.Value is not JsonObject)))
                p.Add("signing.entitlements must map a path inside the package to its entitlements dictionary");
        }
        if (S("channel") != "candidate") p.Add("channel must be candidate");
        return p;
    }

    private static JsonNode? Node(JsonNode root, string dotted)
    {
        JsonNode? cur = root;
        foreach (var part in dotted.Split('.')) cur = cur is JsonObject o && o.TryGetPropertyValue(part, out var next) ? next : null;
        return cur;
    }

    /// <summary>Readable UTF-8 JSON (non-ASCII text such as "·" or "×" is written as is; it is a file, never embedded in HTML).</summary>
    public static string Serialize(JsonObject m) =>
        m.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";
}
