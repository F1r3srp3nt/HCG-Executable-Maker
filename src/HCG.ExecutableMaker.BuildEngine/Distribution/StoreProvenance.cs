using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HCG.ExecutableMaker.Account.Store;
using HCG.ExecutableMaker.Packaging.Distribution;

namespace HCG.ExecutableMaker.BuildEngine.Distribution;

/// <summary>
/// Ship to Store's build provenance, taken from an HCG Release Passport build half that describes the exact file. HCG
/// Store v2 records the source commit, build provider, toolchain and the uploader's validation report as REPORTED by
/// the tool, and needs them on every file before a release can be submitted.
/// <para>
/// Nothing is guessed. A passport is used only if it is a valid v1 record whose artifact SHA-256 and size are the
/// file's. Otherwise every field stays empty and the reason is noted. Only application code signing goes to HCG Store,
/// as evidence in the validation summary. Repository signing is not the Store's concern and is never sent.
/// </para>
/// </summary>
public static partial class StoreProvenance
{
    public sealed record Result(string? SourceCommit, string? BuildProvider, string? Toolchain, StoreValidationReport? ValidationReport, string? PassportPath, IReadOnlyList<string> Notes)
    {
        public static Result None(params string[] notes) => new(null, null, null, null, null, notes);
        public bool Complete => SourceCommit is not null && BuildProvider is not null && Toolchain is not null;
    }

    /// <summary>A passport next to the file: <c>&lt;file&gt;.release-passport.json</c>, else the folder's <c>release-passport.json</c> (an Unleashify handoff).</summary>
    public static string? FindPassport(string artifactPath)
    {
        foreach (var candidate in new[] { artifactPath + ".release-passport.json", Path.Combine(Path.GetDirectoryName(Path.GetFullPath(artifactPath))!, ReleasePassport.FileName) })
            if (File.Exists(candidate)) return candidate;
        return null;
    }

    public static Result ForFile(string artifactPath, string sha256, long size, string? passportPath = null)
    {
        passportPath ??= FindPassport(artifactPath);
        if (passportPath is null) return Result.None("no release passport next to the file: source commit, build provider and toolchain are not reported (HCG Store needs them before the release can be submitted)");
        JsonNode? node;
        try { node = JsonNode.Parse(File.ReadAllText(passportPath)); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException) { return Result.None($"{Path.GetFileName(passportPath)} cannot be read: {ex.Message}"); }
        return From(node, sha256, size, passportPath);
    }

    public static Result From(JsonNode? passport, string sha256, long size, string? passportPath = null)
    {
        var name = passportPath is null ? "the release passport" : Path.GetFileName(passportPath);
        if (ReleasePassport.Validate(passport) is { Count: > 0 } problems)
            return Result.None($"{name} is not used: {string.Join("; ", problems.Take(3))}");
        var p = passport!.AsObject();
        var a = p["artifact"]!;
        if (Str(a, "sha256") != sha256.ToLowerInvariant() || a["size"]?.GetValue<long>() != size)
            return Result.None($"{name} describes different bytes (SHA-256 {Str(a, "sha256")}, {a["size"]} bytes), not this file: it is not used");

        var notes = new List<string>();
        var commit = Str(p["source"], "commit");
        if (commit is not null && !CommitRe().IsMatch(commit)) { notes.Add($"source commit \"{commit}\" is not a commit hash; not reported"); commit = null; }
        var provider = Str(p["build"], "provider") switch
        {
            "executable-maker-local" => "local",
            "github-actions" or "github-actions-private" => "github-actions",
            "hcg-server-build-host" => "hcg-server-build-host",
            null => null,
            var other => Note(notes, $"build provider \"{other}\" has no HCG Store equivalent; not reported"),
        };
        var toolchain = Fit(Str(p["build"], "toolchain"), 200);
        if (commit is null) notes.Add("no source commit in the passport");
        if (toolchain is null) notes.Add("no toolchain in the passport");
        return new(commit, provider, toolchain, Report(p), passportPath, notes);
    }

    /// <summary>The uploader's validation as HCG Store records it: Maker's levels and the code signatures in the package (application code only).</summary>
    public static StoreValidationReport Report(JsonObject passport)
    {
        var levels = passport["validation"]?["levels"] as JsonObject;
        string L(string k) => Str(levels?[k], "state") ?? "not-recorded";
        var failed = (passport["validation"]?["checks"] as JsonArray ?? []).Any(c => Str(c, "status") == "Failed") || L("static_inspection") == "failed";
        var summary =
            $"HCG Release Passport v1 (build half). Static inspection {L("static_inspection")}; maintainer script review {L("maintainer_script_review")}; " +
            $"script-free lifecycle {L("script_free_lifecycle")}; device installation {L("device_installation")}; application launch {L("application_launch")}; " +
            $"runtime verification {L("runtime_verification")}. Code signing of the application code inside the package (not repository signing): " +
            $"{ReleasePassport.CodeSigningText(passport)}. SHA-256 {Str(passport["artifact"], "sha256")}.";
        var tool = Str(passport["validation"], "tool") ?? Str(passport["issuer"], "version") ?? "HCG Executable Maker";
        return new(Fit(tool, 200)!, !failed, Fit(summary, 4000)!);
    }

    private static string? Note(List<string> notes, string n) { notes.Add(n); return null; }

    /// <summary>Within HCG Store's limit: whole comma-separated parts while they fit (never a cut word).</summary>
    private static string? Fit(string? s, int max)
    {
        if (s is null || s.Length <= max) return s;
        var parts = s.Split(", ");
        var taken = parts[0].Length <= max ? parts[0] : parts[0][..(max - 1)] + "…";
        foreach (var part in parts.Skip(1)) { if (taken.Length + 2 + part.Length > max) break; taken += ", " + part; }
        return taken;
    }

    private static string? Str(JsonNode? n, string key) => n?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    [GeneratedRegex("^[a-f0-9]{7,40}$")] private static partial Regex CommitRe();
}
