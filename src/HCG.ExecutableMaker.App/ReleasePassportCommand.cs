using System.Security.Cryptography;
using HCG.ExecutableMaker.BuildEngine.MacXcode;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Infrastructure.Processes;
using HCG.ExecutableMaker.Packaging.Distribution;
using HCG.ExecutableMaker.Packaging.MacDistribution;

namespace HCG.ExecutableMaker.App;

/// <summary>
/// <c>HCGExecutableMaker --release-passport --app &lt;X.app&gt; --file &lt;X.app.zip&gt; [--source-repo &lt;dir&gt;]
/// [--provider executable-maker-local|github-actions] [--toolchain "…"] [--out &lt;file&gt;]</c>: the Release Passport build
/// half for a macOS app archive that already exists (e.g. Maker's own packaged app). Facts are measured: the archive's
/// SHA-256 and size, the bundle's identity, version and architectures, the code signing as the Signing Doctor reads it
/// (macOS only), and the source commit as Git states it. Nothing is claimed that was not measured.
/// </summary>
internal static class ReleasePassportCommand
{
    public static int Run(string[] args)
    {
        string? Arg(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
        var app = Arg("--app") is { } a ? Path.GetFullPath(a) : null;
        var file = Arg("--file") is { } f ? Path.GetFullPath(f) : null;
        if (app is null || file is null || !Directory.Exists(app) || !File.Exists(file))
        {
            Console.Error.WriteLine("--release-passport needs --app <bundle.app> and --file <archive of it>.");
            return 2;
        }
        if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("The macOS Signing Doctor runs on macOS only; no passport was written."); return 2; }
        var runner = new ProcessRunner();
        var facts = MacAppFacts.Read(app);
        if (facts.Problem is { } problem) { Console.Error.WriteLine($"{Path.GetFileName(app)}: {problem}"); return 6; }
        var signing = new MacSigningDoctor(runner).InspectAsync(app, CancellationToken.None).GetAwaiter().GetResult();
        var source = Arg("--source-repo") is { } repo
            ? SourceProvenance.ReadAsync(runner, "git", Path.GetFullPath(repo), CancellationToken.None).GetAwaiter().GetResult()
            : SourceProvenance.Unknown;
        string sha;
        using (var s = File.OpenRead(file)) sha = Convert.ToHexStringLower(SHA256.HashData(s));
        var size = new FileInfo(file).Length;
        var checks = new List<(string, string, string)>
        {
            ("archive", "Passed", $"{Path.GetFileName(file)}: {size:N0} bytes, SHA-256 {sha}"),
            ("bundle identity", facts.BundleIdentifier is null ? "Failed" : "Passed", $"{facts.BundleIdentifier} {facts.ShortVersion} ({facts.BuildVersion})"),
            ("architectures", facts.Architectures.Count > 0 ? "Passed" : "Failed", string.Join(", ", facts.Architectures)),
            ("code signature", signing.SignatureValid ? "Passed" : "PassedWithWarnings", $"{signing.Status}; Gatekeeper {signing.Gatekeeper}; {signing.Notarization}"),
        };
        var passport = ReleasePassport.ForArtifact(new ReleasePassport.ArtifactFacts(
            Platform: "macos", Architecture: MacXcodePassport.ArchitectureName(facts.Architectures), Package: facts.BundleIdentifier ?? facts.Label,
            Version: facts.ShortVersion ?? "0", BuildNumber: Arg("--build-number") ?? facts.BuildVersion, ProductName: facts.Label,
            FileName: Path.GetFileName(file), Sha256: sha, Size: size, ArtifactKind: "zip of the .app",
            Source: source, Provider: Arg("--provider") ?? "executable-maker-local",
            Toolchain: Arg("--toolchain") ?? "HCG Executable Maker " + HostInfo.ProductVersion, BuiltAt: DateTimeOffset.UtcNow,
            StaticInspectionState: checks.Any(c => c.Item2 == "Failed") ? "failed" : checks.Any(c => c.Item2 == "PassedWithWarnings") ? "passed-with-warnings" : "passed",
            StaticInspectionNote: $"{checks.Count} checks of the zip of the .app by Executable Maker (archive, identity, architectures, signature as built)",
            Checks: checks, Tests: null, CodeSigning: MacXcodePassport.CodeSigning(signing, Path.GetFileName(app)),
            IssuedBy: "HCG Executable Maker " + HostInfo.ProductVersion));
        if (ReleasePassport.ValidateBuildHalf(passport) is { Count: > 0 } problems) { Console.Error.WriteLine("Not written: " + string.Join("; ", problems)); return 6; }
        var output = Arg("--out") ?? file + ".release-passport.json";
        File.WriteAllText(output, passport.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Release Passport: {output}");
        Console.WriteLine($"  {Path.GetFileName(file)} · SHA-256 {sha} · {size:N0} bytes · {facts.BundleIdentifier} {facts.ShortVersion} · {string.Join("/", facts.Architectures)}");
        Console.WriteLine($"  code signing: {signing.Status} ({MacSigningDoctor.Describe(signing.SignatureKind)}); Gatekeeper {signing.Gatekeeper}; {signing.Notarization}");
        Console.WriteLine($"  source: {source.Commit ?? "unknown"}{(source.Dirty == true ? " (uncommitted changes)" : "")}");
        return 0;
    }
}
