using System.Text.Json;
using System.Text.Json.Nodes;
using HCG.ExecutableMaker.BuildEngine.Distribution;
using HCG.ExecutableMaker.BuildEngine.MacXcode;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Packaging.Distribution;
using HCG.ExecutableMaker.Packaging.MacDistribution;

namespace HCG.ExecutableMaker.BuildEngine.MacDistribution;

/// <summary>
/// The Release Passport build half for a macOS app archive that already exists (a ZIP of the .app, a .pkg or a .dmg):
/// the archive's SHA-256 and size, the bundle's identity, version and architectures, the code signing as the Signing
/// Doctor read it, and the source commit as Git states it. Shared by <c>--release-passport</c> and the Sign an existing
/// app page. Nothing is claimed that was not measured.
/// </summary>
public static class MacReleasePassportWriter
{
    /// <param name="ReleaseVersion">
    /// The release version to record (for example 1.0.0-beta.2) when it is more precise than CFBundleShortVersionString,
    /// which macOS allows to be numeric only. Its numeric part must equal CFBundleShortVersionString. Null = the bundle's.
    /// </param>
    public sealed record Input(
        string App, string File, MacAppFacts Facts, MacSigningDoctor.Report Signing, SourceProvenance Source,
        string Provider, string Toolchain, string? BuildNumber, string? ReleaseVersion, string IssuedBy);

    public sealed record Result(JsonObject? Passport, IReadOnlyList<string> Problems, string Sha256, long Size, string Version);

    /// <summary>
    /// Why <paramref name="releaseVersion"/> cannot describe an app whose CFBundleShortVersionString is
    /// <paramref name="shortVersion"/>, or null when it can (or when no override is given).
    /// </summary>
    public static string? VersionProblem(string? releaseVersion, string? shortVersion)
    {
        if (string.IsNullOrWhiteSpace(releaseVersion)) return null;
        var v = releaseVersion.Trim();
        if (DistributionStudio.CompareVersions(v, v) is null)
            return $"\"{v}\" is not a Semantic Version (for example 1.0.0 or 1.0.0-beta.2).";
        if (string.IsNullOrWhiteSpace(shortVersion))
            return $"The app has no CFBundleShortVersionString, so the release version {v} cannot be checked against it.";
        var core = v.Split('-', '+')[0];
        if (Normalize(core) != Normalize(shortVersion))
            return $"The release version {v} does not match the app: its CFBundleShortVersionString is {shortVersion}, and the numeric part of the release version ({core}) must equal it.";
        return null;
    }

    private static string Normalize(string numeric)
    {
        var parts = numeric.Trim().Split('.').Select(p => int.TryParse(p, out var n) ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : p).ToList();
        while (parts.Count < 3) parts.Add("0");
        return string.Join('.', parts);
    }

    /// <summary>The kind of archive, from its own name; empty when a passport here cannot describe it.</summary>
    public static string KindOf(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".pkg" => "installer package", ".dmg" => "disk image", ".zip" => "zip of the .app", _ => "",
    };

    public static async Task<Result> CreateAsync(Input input, CancellationToken ct)
    {
        var facts = input.Facts;
        var problems = new List<string>();
        var kind = KindOf(input.File);
        if (kind.Length == 0) problems.Add($"{Path.GetFileName(input.File)}: a Release Passport here describes a .zip, .pkg or .dmg of the app.");
        if (facts.Problem is { } fp) problems.Add($"{Path.GetFileName(input.App)}: {fp}");
        if (VersionProblem(input.ReleaseVersion, facts.ShortVersion) is { } vp) problems.Add(vp);
        var version = string.IsNullOrWhiteSpace(input.ReleaseVersion) ? facts.ShortVersion ?? "0" : input.ReleaseVersion.Trim();
        if (problems.Count > 0 || !File.Exists(input.File)) return new(null, problems.Count > 0 ? problems : [$"{input.File} does not exist."], "", 0, version);

        var sha = await Checksums.Sha256FileAsync(input.File, ct).ConfigureAwait(false);
        var size = new FileInfo(input.File).Length;
        var signing = input.Signing;
        var checks = new List<(string, string, string)>
        {
            ("archive", "Passed", $"{Path.GetFileName(input.File)}: {size:N0} bytes, SHA-256 {sha}"),
            ("bundle identity", facts.BundleIdentifier is null ? "Failed" : "Passed", $"{facts.BundleIdentifier} {facts.ShortVersion} ({facts.BuildVersion})"),
            ("architectures", facts.Architectures.Count > 0 ? "Passed" : "Failed", string.Join(", ", facts.Architectures)),
            ("code signature", signing.SignatureValid ? "Passed" : "PassedWithWarnings", $"{signing.Status}; Gatekeeper {signing.Gatekeeper}; {signing.Notarization}"),
        };
        if (!string.IsNullOrWhiteSpace(input.ReleaseVersion))
            checks.Add(("release version", "Passed", $"{version}: its numeric part equals CFBundleShortVersionString {facts.ShortVersion}"));
        var passport = ReleasePassport.ForArtifact(new ReleasePassport.ArtifactFacts(
            Platform: "macos", Architecture: MacXcodePassport.ArchitectureName(facts.Architectures), Package: facts.BundleIdentifier ?? facts.Label,
            Version: version, BuildNumber: input.BuildNumber ?? facts.BuildVersion, ProductName: facts.Label,
            FileName: Path.GetFileName(input.File), Sha256: sha, Size: size, ArtifactKind: kind,
            Source: input.Source, Provider: input.Provider, Toolchain: input.Toolchain, BuiltAt: DateTimeOffset.UtcNow,
            StaticInspectionState: checks.Any(c => c.Item2 == "Failed") ? "failed" : checks.Any(c => c.Item2 == "PassedWithWarnings") ? "passed-with-warnings" : "passed",
            StaticInspectionNote: $"{checks.Count} checks of the {kind} by Executable Maker (archive, identity, architectures, signature as built)",
            Checks: checks, Tests: null, CodeSigning: MacXcodePassport.CodeSigning(signing, Path.GetFileName(input.App)),
            IssuedBy: input.IssuedBy));
        if (ReleasePassport.ValidateBuildHalf(passport) is { Count: > 0 } invalid) return new(null, invalid.ToList(), sha, size, version);
        return new(passport, [], sha, size, version);
    }

    /// <summary>Writes the passport next to the file (<c>&lt;file&gt;.release-passport.json</c>) unless another path is given.</summary>
    public static async Task<string> WriteAsync(JsonObject passport, string file, string? output, CancellationToken ct)
    {
        var path = output ?? file + ".release-passport.json";
        await File.WriteAllTextAsync(path, passport.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", ct).ConfigureAwait(false);
        return path;
    }
}
