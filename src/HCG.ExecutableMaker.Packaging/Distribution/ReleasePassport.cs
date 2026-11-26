using System.Text.Json.Nodes;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.Jailbreak;

namespace HCG.ExecutableMaker.Packaging.Distribution;

/// <summary>
/// HCG Release Passport (<c>hcg.release-passport</c> v1): one record per released build, shared by Executable Maker, HCG
/// Repo, HCG Store and HCG Server (HCG Repo <c>docs/RELEASE_PASSPORT.md</c>; schema pinned below).
/// <para>
/// Executable Maker writes only the <b>build half</b>: what it measured or was told by the person building, namely
/// artifact bytes, SHA-256, size, source provenance, build provider, toolchain, the code signatures inside the package,
/// entitlements and its own validation evidence. Everything a distribution authority decides is left to it: channel and
/// access, approvals, publication, device evidence and, above all, <b>repository signing</b> (the OpenPGP key over a
/// channel's <c>Release</c> metadata).
/// </para>
/// <para>
/// <c>repository_signing</c> and <c>code_signing</c> are different things and never mixed. <c>code_signing</c> is the
/// ldid or Apple signature of the Mach-O code inside the package, which Maker reads from the bytes.
/// <c>repository_signing</c> is the archive key of the channel that serves the package. It is reported only as the
/// distribution authority states it (<see cref="Reconcile"/>), and is never filled in by Maker.
/// </para>
/// </summary>
public static class ReleasePassport
{
    public const string Format = "hcg.release-passport";
    public const int FormatVersion = 1;
    public const string FileName = "release-passport.json";

    /// <summary>Where the embedded schema comes from, byte for byte.</summary>
    public static readonly (string Repository, string Commit, string Path, string Sha256) SchemaSource =
        ("F1r3srp3nt/HCG-repo", "3bf53c7bb2b6e67a2f66f3a7e1490fc0c1f41481", "docs/schemas/release-passport-v1.schema.json",
         "2eb1a9138fed6a9a349fd329c1c9206299ee8255de59a9dc447ce59b7644d65e");

    public const string CodeSigningScope = "application code inside the package (Mach-O); independent of repository signing";
    public const string RepositorySigningScope = "repository metadata (Release, InRelease, Release.gpg) of the channel that serves this package; not the application";
    public const string RepositorySigningNotFromBuilder =
        "not stated by the builder: repository signing belongs to the distribution authority (HCG Repo) and appears only in its passport";

    private static readonly Lazy<byte[]> SchemaBytes = new(() =>
    {
        using var s = typeof(ReleasePassport).Assembly.GetManifestResourceStream("release-passport-v1.schema.json")
                      ?? throw new InvalidOperationException("release-passport-v1.schema.json is not embedded");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    });

    public static byte[] SchemaFile => SchemaBytes.Value;
    public static JsonNode Schema => JsonNode.Parse(SchemaBytes.Value)!;

    public enum Compatibility { Compatible, NotAPassport, NewerVersion, InvalidVersion }

    /// <summary>
    /// Version compatibility, checked before anything else is read. A passport of a later format version is refused with
    /// a clear reason rather than read with version-1 rules. Unknown extra fields of version 1 are accepted (the schema
    /// allows them), so additions on HCG Repo's side do not break Maker.
    /// </summary>
    public static (Compatibility State, string Detail) CheckVersion(JsonNode? passport)
    {
        if (passport is not JsonObject o) return (Compatibility.NotAPassport, "not a JSON object");
        if (Str(o, "format") != Format) return (Compatibility.NotAPassport, $"format is {o["format"]?.ToJsonString() ?? "missing"}, not \"{Format}\"");
        if (o["formatVersion"] is not JsonValue v || v.GetValueKind() != System.Text.Json.JsonValueKind.Number || !v.TryGetValue<int>(out var n))
            return (Compatibility.InvalidVersion, $"formatVersion is {o["formatVersion"]?.ToJsonString() ?? "missing"}, not an integer");
        if (n > FormatVersion)
            return (Compatibility.NewerVersion, $"formatVersion {n} is newer than this HCG Executable Maker understands (version {FormatVersion}); update Executable Maker before relying on it");
        if (n < 1) return (Compatibility.InvalidVersion, $"formatVersion {n} does not exist");
        return (Compatibility.Compatible, $"{Format} v{n}");
    }

    /// <summary>Version check, then the pinned schema. Empty when the passport is a valid v1 record.</summary>
    public static IReadOnlyList<string> Validate(JsonNode? passport)
    {
        var (state, detail) = CheckVersion(passport);
        if (state != Compatibility.Compatible) return [$"version: {detail}"];
        return JsonSchemaLite.Validate(Schema, passport);
    }

    /// <summary>
    /// The schema rules plus Maker's own rules for a build half: it states no repository signing (no <c>configured</c>, no
    /// <c>fingerprint</c>), no approvals, no device evidence, no publication status and no channel assignment, because
    /// none of those are the builder's to state.
    /// </summary>
    public static IReadOnlyList<string> ValidateBuildHalf(JsonNode? passport)
    {
        var p = Validate(passport).ToList();
        if (passport is not JsonObject o) return p;
        if (o["repository_signing"] is JsonObject rs && (rs.ContainsKey("configured") || rs.ContainsKey("fingerprint")))
            p.Add("repository_signing: a build half must not state a repository signing key or its configuration; only the distribution authority can");
        if (o["approvals"] is JsonArray { Count: > 0 }) p.Add("approvals: a build half records no approvals; they are given in HCG Repo or HCG Store");
        if (o["device_evidence"] is JsonArray { Count: > 0 }) p.Add("device_evidence: device results are recorded by the distribution authority from a real test");
        if (o["publication"] is JsonObject pub && (pub.ContainsKey("record_status") || pub.ContainsKey("in_index_builds")))
            p.Add("publication: a build half has no publication status");
        if (o["distribution"] is JsonObject dist && (dist.ContainsKey("channel") || dist.ContainsKey("access")))
            p.Add("distribution: channel and access are assigned by the distribution authority");
        foreach (var level in new[] { "device_installation", "application_launch", "runtime_verification" })
            if (o["validation"]?["levels"]?[level]?["state"]?.GetValue<string>() is { } st && st != "not-recorded")
                p.Add($"validation.levels.{level}: a build half cannot record device results ({st})");
        return p;
    }

    /// <summary>
    /// The build half of an Unleashify package's passport, from the submission manifest Maker wrote for it and the
    /// validator's measurements of the exact bytes.
    /// </summary>
    public static JsonObject BuildHalf(JsonObject manifest, RootlessAppDebValidator.Result validation, RootlessLifecycleSimulator.Result? lifecycle, string issuedBy)
    {
        var f = validation.Facts;
        var pkg = manifest["package"]!;
        var build = manifest["build"]!;
        var arch = Str(pkg, "architecture")!;
        var prefix = (f.AppPath ?? "").TrimStart('/') + "/";
        var signing = manifest["signing"] as JsonObject;
        var signedByMaker = (signing?["signedFiles"] as JsonArray ?? []).Select(x => x!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        var exact = SubmissionManifest.EntitlementsByPath(f);
        var binaries = f.Binaries ?? [];

        var failed = validation.Checks.Any(c => c.Status == VerificationStatus.Failed);
        var warned = validation.Checks.Any(c => c.Status == VerificationStatus.PassedWithWarnings);
        var registration = validation.Checks.FirstOrDefault(c => c.Name == "App registration");
        JsonObject Level(string state, string note) => new() { ["state"] = state, ["by"] = "HCG Executable Maker", ["note"] = note };

        return new JsonObject
        {
            ["format"] = Format,
            ["formatVersion"] = FormatVersion,
            ["issuer"] = new JsonObject
            {
                ["part"] = "build",
                ["system"] = "HCG Executable Maker",
                ["version"] = issuedBy,
                ["note"] = "the build half: measured by the builder. Channel, access, approvals, publication, device evidence and repository signing are the distribution authority's",
            },
            ["product"] = new JsonObject { ["key"] = null, ["name"] = Str(pkg, "name"), ["store_slug"] = null, ["central_id"] = null },
            ["edition"] = null,
            ["platform"] = Platform(arch),
            ["architecture"] = arch,
            ["package"] = Str(pkg, "identifier"),
            ["version"] = Str(pkg, "version"),
            ["source"] = new JsonObject
            {
                ["repository"] = Str(manifest["source"], "repository"), ["commit"] = Str(manifest["source"], "commit"), ["branch"] = Str(manifest["source"], "branch"),
            },
            ["build"] = new JsonObject
            {
                ["provider"] = Str(build, "provider"), ["toolchain"] = Str(build, "toolchain"), ["built_at"] = Str(build, "builtAt"),
                ["run_id"] = build["runId"] is JsonValue r && r.TryGetValue<long>(out var id) ? id : null,
                ["artifact"] = Str(build, "artifactName"),
            },
            ["artifact"] = new JsonObject { ["filename"] = Str(manifest["artifact"], "filename"), ["sha256"] = f.Sha256, ["size"] = f.Size },
            ["validation"] = new JsonObject
            {
                ["levels"] = new JsonObject
                {
                    ["static_inspection"] = Level(failed ? "failed" : warned ? "passed-with-warnings" : "passed",
                        $"{validation.Checks.Count} checks of the exact bytes by Executable Maker's validator; HCG Repo inspects again independently"),
                    ["maintainer_script_review"] = f.Scripts.Count == 0
                        ? Level("not-applicable", "the package has no maintainer scripts")
                        : Level(registration?.Status == VerificationStatus.Passed ? "passed" : "failed",
                            $"read, not run: {registration?.Detail ?? "no review recorded"}"),
                    ["script_free_lifecycle"] = Level("not-run",
                        "Executable Maker does not run a script-free dpkg lifecycle (HCG Repo does). " +
                        (lifecycle is null ? "No lifecycle simulation was run." : $"Its own simulation, which runs Maker's scripts with a recording uicache in a throwaway folder: {lifecycle.Summary}.")),
                    ["device_installation"] = Level("not-recorded", "no device test is recorded by the builder"),
                    ["application_launch"] = Level("not-recorded", "no device test is recorded by the builder"),
                    ["runtime_verification"] = Level("not-recorded", "no device test is recorded by the builder"),
                },
                ["tool"] = Str(manifest["validation"], "tool"),
                ["checks"] = new JsonArray(validation.Checks.Select(c => (JsonNode?)new JsonObject { ["check"] = c.Name, ["status"] = c.Status.ToString(), ["detail"] = c.Detail })
                    .Concat((lifecycle?.Checks ?? []).Select(c => (JsonNode?)new JsonObject { ["check"] = "Lifecycle · " + c.Name, ["status"] = c.Status.ToString(), ["detail"] = c.Detail })).ToArray()),
            },
            ["device_evidence"] = new JsonArray(),
            ["approvals"] = new JsonArray(),
            ["distribution"] = new JsonObject
            {
                ["requested_channel"] = Str(manifest, "channel"),
                ["note"] = "channel and access are assigned by the distribution authority",
            },
            ["publication"] = new JsonObject { ["note"] = "not published: Executable Maker never publishes" },
            ["repository_signing"] = new JsonObject { ["scope"] = RepositorySigningScope, ["status"] = RepositorySigningNotFromBuilder },
            ["code_signing"] = new JsonObject
            {
                ["scope"] = CodeSigningScope,
                ["method_claimed"] = Str(signing, "method"),
                ["binaries"] = binaries.Count,
                ["all_signed"] = binaries.Count == 0 ? null : binaries.All(b => b.Signed),
                // HCG Repo's shape: package path → entitlement keys (sorted). The exact values follow.
                ["entitlements"] = new JsonObject(exact.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)new JsonArray(kv.Value.Keys.Order(StringComparer.Ordinal).Select(k => (JsonNode?)k).ToArray())))),
                ["entitlements_exact"] = new JsonObject(exact.Select(kv => KeyValuePair.Create(kv.Key, SubmissionManifest.ToJson(kv.Value)))),
                ["signed_by_builder"] = new JsonArray(signedByMaker.Order(StringComparer.Ordinal).Select(x => (JsonNode?)x).ToArray()),
                ["details"] = new JsonArray(binaries.Select(b => (JsonNode?)new JsonObject
                {
                    ["path"] = prefix + b.Path,
                    ["signer"] = b.Signer,
                    ["signed_by_builder"] = signedByMaker.Contains(prefix + b.Path),
                    ["verified"] = b.Verified,
                    ["page_hashes"] = b.PageHashes,
                    ["page_hashes_verified"] = b.PageHashesVerified,
                    ["identifier"] = b.Identifier,
                    ["cdhash"] = b.CdHash,
                    ["team_id"] = b.TeamId,
                    ["hardened_runtime"] = b.HardenedRuntime,
                    ["library_validation"] = b.LibraryValidation,
                    ["architectures"] = new JsonArray(b.Architectures.Select(a => (JsonNode?)a).ToArray()),
                }).ToArray()),
            },
        };
    }

    /// <summary>What a builder knows about one artifact of a build that is not a Debian package (macOS apps, installers, archives).</summary>
    public sealed record ArtifactFacts(
        string Platform, string Architecture, string Package, string Version, string? BuildNumber, string? ProductName,
        string FileName, string Sha256, long Size, string ArtifactKind,
        SourceProvenance Source, string Provider, string? Toolchain, DateTimeOffset BuiltAt,
        string StaticInspectionState, string StaticInspectionNote, IReadOnlyList<(string Check, string Status, string Detail)> Checks,
        JsonObject? Tests, JsonObject CodeSigning, string IssuedBy);

    /// <summary>
    /// The build half for any artifact: the same rules as <see cref="BuildHalf"/> (no repository signing, approvals,
    /// publication, channel or device results). Maintainer scripts and the dpkg lifecycle do not apply to these artifacts.
    /// </summary>
    public static JsonObject ForArtifact(ArtifactFacts a)
    {
        JsonObject Level(string state, string note) => new() { ["state"] = state, ["by"] = "HCG Executable Maker", ["note"] = note };
        var validation = new JsonObject
        {
            ["levels"] = new JsonObject
            {
                ["static_inspection"] = Level(a.StaticInspectionState, a.StaticInspectionNote),
                ["maintainer_script_review"] = Level("not-applicable", $"a {a.ArtifactKind} has no Debian maintainer scripts"),
                ["script_free_lifecycle"] = Level("not-applicable", "the dpkg lifecycle applies to Debian packages only"),
                ["device_installation"] = Level("not-recorded", "no installation on another machine is recorded by the builder"),
                ["application_launch"] = Level("not-recorded", "no launch is recorded by the builder"),
                ["runtime_verification"] = Level("not-recorded", "no runtime checklist is recorded by the builder"),
            },
            ["tool"] = a.IssuedBy,
            ["checks"] = new JsonArray(a.Checks.Select(c => (JsonNode?)new JsonObject { ["check"] = c.Check, ["status"] = c.Status, ["detail"] = c.Detail }).ToArray()),
        };
        if (a.Tests is not null) validation["tests"] = a.Tests;
        return new JsonObject
        {
            ["format"] = Format,
            ["formatVersion"] = FormatVersion,
            ["issuer"] = new JsonObject
            {
                ["part"] = "build", ["system"] = "HCG Executable Maker", ["version"] = a.IssuedBy,
                ["note"] = "the build half: measured by the builder. Channel, access, approvals, publication, device evidence and repository signing are the distribution authority's",
            },
            ["product"] = new JsonObject { ["key"] = null, ["name"] = a.ProductName, ["store_slug"] = null, ["central_id"] = null },
            ["edition"] = null,
            ["platform"] = a.Platform,
            ["architecture"] = a.Architecture,
            ["package"] = a.Package,
            ["version"] = a.Version,
            ["build_number"] = a.BuildNumber,
            ["source"] = new JsonObject
            {
                ["repository"] = a.Source.Repository, ["commit"] = a.Source.Commit, ["branch"] = a.Source.Branch, ["dirty"] = a.Source.Dirty, ["basis"] = a.Source.Basis,
            },
            ["build"] = new JsonObject
            {
                ["provider"] = a.Provider, ["toolchain"] = a.Toolchain, ["built_at"] = a.BuiltAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"), ["run_id"] = null, ["artifact"] = null,
            },
            ["artifact"] = new JsonObject { ["filename"] = a.FileName, ["sha256"] = a.Sha256, ["size"] = a.Size, ["kind"] = a.ArtifactKind },
            ["validation"] = validation,
            ["device_evidence"] = new JsonArray(),
            ["approvals"] = new JsonArray(),
            ["distribution"] = new JsonObject { ["note"] = "channel and access are assigned by the distribution authority" },
            ["publication"] = new JsonObject { ["note"] = "not published: Executable Maker never publishes" },
            ["repository_signing"] = new JsonObject { ["scope"] = RepositorySigningScope, ["status"] = RepositorySigningNotFromBuilder },
            ["code_signing"] = a.CodeSigning,
        };
    }

    /// <summary>The passport's platform for a Debian architecture, as HCG Repo names it (tools/hcgrepo/passport.py).</summary>
    public static string Platform(string architecture) => architecture switch
    {
        "iphoneos-arm64" => "ios-jailbreak-rootless",
        "iphoneos-arm" => "ios-jailbreak-rootful",
        "iphoneos-arm64e" => "ios-jailbreak-roothide",
        _ => architecture,
    };

    public sealed record Finding(string Level, string Field, string Message);

    /// <summary>
    /// Compares the builder's half with the distribution authority's passport for the same release. The authority's
    /// record wins for everything it decides; its <c>repository_signing</c> is passed through verbatim. A different
    /// artifact (SHA-256, size, file name) or package identity is an error: they are not the same release.
    /// </summary>
    /// <returns>The findings, and the combined record (the authority's, with only the builder fields it left empty, such as
    /// the toolchain, filled from the build half and listed under <c>filled_from_build_half</c>).</returns>
    public static (IReadOnlyList<Finding> Findings, JsonObject? Combined) Reconcile(JsonObject buildHalf, JsonObject authority)
    {
        var f = new List<Finding>();
        foreach (var (name, problems) in new[] { ("build half", Validate(buildHalf)), ("authority passport", Validate(authority)) })
            foreach (var p in problems) f.Add(new("error", name, p));
        if (f.Count > 0) return (f, null);

        void Same(string level, string field, JsonNode? a, JsonNode? b, string what)
        {
            if (!JsonNode.DeepEquals(a, b)) f.Add(new(level, field, $"{what}: the build half says {a?.ToJsonString() ?? "null"}, the distribution authority {b?.ToJsonString() ?? "null"}"));
        }
        foreach (var k in new[] { "sha256", "size", "filename" }) Same("error", "artifact." + k, buildHalf["artifact"]?[k], authority["artifact"]?[k], "different bytes");
        foreach (var k in new[] { "package", "version", "architecture" }) Same("error", k, buildHalf[k], authority[k], "different release");
        if (Str(authority["source"], "commit") is not null) Same("warning", "source.commit", buildHalf["source"]?["commit"], authority["source"]?["commit"], "source");
        Same("warning", "build.provider", buildHalf["build"]?["provider"], authority["build"]?["provider"], "build provider");

        var bc = buildHalf["code_signing"]; var ac = authority["code_signing"];
        Same("warning", "code_signing.binaries", bc?["binaries"], ac?["binaries"], "Mach-O count");
        if (ac?["all_signed"] is not null) Same("warning", "code_signing.all_signed", bc?["all_signed"], ac?["all_signed"], "signatures");
        Same("warning", "code_signing.entitlements", bc?["entitlements"], ac?["entitlements"], "entitlements");
        if (Str(ac, "method_claimed") is not null) Same("warning", "code_signing.method_claimed", bc?["method_claimed"], ac?["method_claimed"], "signing method");

        var combined = authority.DeepClone().AsObject();
        var filled = new JsonArray();
        foreach (var k in new[] { "toolchain", "built_at" })
            if (combined["build"] is JsonObject b && b[k] is null && buildHalf["build"]?[k] is { } v)
            {
                b[k] = v.DeepClone();
                filled.Add((JsonNode?)("build." + k));
            }
        if (filled.Count > 0) combined["filled_from_build_half"] = filled;
        return (f, combined);
    }

    /// <summary>Repository signing as the distribution authority states it, for display; never derived from the build half.</summary>
    public static string RepositorySigningText(JsonObject authorityPassport)
    {
        var rs = authorityPassport["repository_signing"];
        if (rs is not JsonObject o) return "not stated by the distribution authority";
        var configured = o["configured"] is JsonValue c && c.TryGetValue<bool>(out var cb) ? (cb ? "configured" : "not configured") : "configuration not stated";
        return $"{configured}; fingerprint {Str(o, "fingerprint") ?? "none"}; {Str(o, "status") ?? "no status"}";
    }

    public static string CodeSigningText(JsonNode? passport)
    {
        var cs = passport?["code_signing"];
        if (cs is null) return "not stated";
        var all = cs["all_signed"] is JsonValue a && a.TryGetValue<bool>(out var ab) ? (ab ? "all signed" : "NOT all signed") : "signing not inspected";
        var ents = cs["entitlements"] is JsonObject e && e.Count > 0 ? "; entitlements: " + string.Join("; ", e.Select(kv => $"{kv.Key} ({string.Join(", ", (kv.Value as JsonArray ?? []).Select(x => x!.GetValue<string>()))})")) : "; no entitlements";
        // The binary kind follows the passport's platform: Windows files carry PE programs (Authenticode), Apple ones Mach-O.
        var kind = Str(passport, "platform") == "windows" ? "Windows PE" : "Mach-O";
        return $"{cs["binaries"]?.ToJsonString() ?? "?"} {kind}, {all}, method {Str(cs, "method_claimed") ?? "not claimed"}{ents}";
    }

    private static string? Str(JsonNode? n, string key) => n?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
