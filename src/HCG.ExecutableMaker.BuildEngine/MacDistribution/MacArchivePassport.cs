using System.Security.Cryptography;
using System.Text.Json.Nodes;
using HCG.ExecutableMaker.BuildEngine.MacXcode;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Packaging.Distribution;
using HCG.ExecutableMaker.Packaging.MacDistribution;

namespace HCG.ExecutableMaker.BuildEngine.MacDistribution;

/// <summary>
/// The Release Passport build half for a macOS distribution file (a ZIP of the .app, a .pkg or a .dmg) that exists on
/// disk: the file's SHA-256 and size, the bundle's identity, version and architectures, the code signing as the Signing
/// Doctor reads it, and the source commit as Git states it. Shared by <c>--release-passport</c> and the existing-app
/// distribution flow. Nothing is claimed that was not measured.
/// </summary>
public static class MacArchivePassport
{
    public sealed record Request
    {
        public required string App { get; init; }
        public required string File { get; init; }
        public required SourceProvenance Source { get; init; }
        public string Provider { get; init; } = "executable-maker-local";
        public string? Toolchain { get; init; }
        public string? BuildNumber { get; init; }
        /// <summary>Further measured checks to record (name, Passed/PassedWithWarnings/Failed, detail).</summary>
        public IReadOnlyList<(string Name, string State, string Detail)> ExtraChecks { get; init; } = [];
        public string? Output { get; init; }
    }

    public sealed record Result(string? Path, JsonObject? Passport, IReadOnlyList<string> Problems, MacSigningDoctor.Report? Signing, string Sha256, long Size)
    {
        public bool Written => Path is not null;
    }

    /// <summary>What the file is, from its extension; null when a passport here cannot describe it.</summary>
    public static string? KindOf(string file) => System.IO.Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".pkg" => "installer package",
        ".dmg" => "disk image",
        ".zip" => "zip of the .app",
        _ => null,
    };

    public static async Task<Result> WriteAsync(IProcessRunner runner, Request r, CancellationToken ct)
    {
        if (KindOf(r.File) is not { } kind) return new(null, null, [$"{System.IO.Path.GetFileName(r.File)}: a Release Passport here describes a .zip, .pkg or .dmg of the app."], null, "", 0);
        var facts = MacAppFacts.Read(r.App);
        if (facts.Problem is { } problem) return new(null, null, [$"{System.IO.Path.GetFileName(r.App)}: {problem}"], null, "", 0);
        var signing = await new MacSigningDoctor(runner).InspectAsync(r.App, ct).ConfigureAwait(false);
        string sha;
        await using (var s = System.IO.File.OpenRead(r.File)) sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(s, ct).ConfigureAwait(false));
        var size = new FileInfo(r.File).Length;
        var checks = new List<(string, string, string)>
        {
            ("archive", "Passed", $"{System.IO.Path.GetFileName(r.File)}: {size:N0} bytes, SHA-256 {sha}"),
            ("bundle identity", facts.BundleIdentifier is null ? "Failed" : "Passed", $"{facts.BundleIdentifier} {facts.ShortVersion} ({facts.BuildVersion})"),
            ("architectures", facts.Architectures.Count > 0 ? "Passed" : "Failed", string.Join(", ", facts.Architectures)),
            ("code signature", signing.SignatureValid ? "Passed" : "PassedWithWarnings", $"{signing.Status}; Gatekeeper {signing.Gatekeeper}; {signing.Notarization}"),
        };
        checks.AddRange(r.ExtraChecks);
        var passport = ReleasePassport.ForArtifact(new ReleasePassport.ArtifactFacts(
            Platform: "macos", Architecture: MacXcodePassport.ArchitectureName(facts.Architectures), Package: facts.BundleIdentifier ?? facts.Label,
            Version: facts.ShortVersion ?? "0", BuildNumber: r.BuildNumber ?? facts.BuildVersion, ProductName: facts.Label,
            FileName: System.IO.Path.GetFileName(r.File), Sha256: sha, Size: size, ArtifactKind: kind,
            Source: r.Source, Provider: r.Provider,
            Toolchain: r.Toolchain ?? "HCG Executable Maker " + HostInfo.ProductVersion, BuiltAt: DateTimeOffset.UtcNow,
            StaticInspectionState: checks.Any(c => c.Item2 == "Failed") ? "failed" : checks.Any(c => c.Item2 == "PassedWithWarnings") ? "passed-with-warnings" : "passed",
            StaticInspectionNote: $"{checks.Count} checks of the {kind} by Executable Maker (archive, identity, architectures, signature)",
            Checks: checks, Tests: null, CodeSigning: MacXcodePassport.CodeSigning(signing, System.IO.Path.GetFileName(r.App)),
            IssuedBy: "HCG Executable Maker " + HostInfo.ProductVersion));
        if (ReleasePassport.ValidateBuildHalf(passport) is { Count: > 0 } problems) return new(null, passport, problems, signing, sha, size);
        var output = r.Output ?? r.File + ".release-passport.json";
        await System.IO.File.WriteAllTextAsync(output, passport.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), ct).ConfigureAwait(false);
        return new(output, passport, [], signing, sha, size);
    }

    /// <summary>git on PATH (processes run by absolute path only); null when there is none, so the source stays unknown.</summary>
    public static string? FindGit() =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => System.IO.Path.Combine(d, "git")).FirstOrDefault(System.IO.File.Exists);
}
