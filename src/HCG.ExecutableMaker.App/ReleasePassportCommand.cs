using HCG.ExecutableMaker.BuildEngine.MacDistribution;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Infrastructure.Processes;
using HCG.ExecutableMaker.Packaging.Distribution;
using HCG.ExecutableMaker.Packaging.MacDistribution;

namespace HCG.ExecutableMaker.App;

/// <summary>
/// <c>HCGExecutableMaker --release-passport --app &lt;X.app&gt; --file &lt;X.app.zip&gt; [--source-repo &lt;dir&gt;]
/// [--provider executable-maker-local|github-actions] [--toolchain "…"] [--build-number N] [--version 1.0.0-beta.2] [--out &lt;file&gt;]</c>: the Release Passport build
/// half for a macOS app archive (.zip of the .app, .pkg or .dmg) that already exists, e.g. a .NET app's distribution
/// files or Maker's own packaged app (<c>--self</c>: Maker's embedded build provenance, no git needed). Facts are measured: the archive's
/// SHA-256 and size, the bundle's identity, version and architectures, the code signing as the Signing Doctor reads it
/// (macOS only), and the source commit as Git states it. Nothing is claimed that was not measured. <c>--version</c> records
/// a pre-release version (CFBundleShortVersionString is numeric only); its numeric part must equal the bundle's.
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
        // Processes run by absolute path only: git is looked up on PATH; without it the checkout's source is unknown.
        var git = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d, "git")).FirstOrDefault(File.Exists);
        var checkout = Arg("--source-repo") is { } repo && git is not null
            ? SourceProvenance.ReadAsync(runner, git, Path.GetFullPath(repo), CancellationToken.None).GetAwaiter().GetResult()
            : SourceProvenance.Unknown;
        SourceProvenance source;
        if (args.Contains("--self"))
        {
            // Maker's own archive: the provenance embedded in THIS build, valid only for the running app's own bundle.
            if (HostInfo.AppBundlePath is not { } running || Path.GetFullPath(running).TrimEnd('/') != app.TrimEnd('/'))
            {
                Console.Error.WriteLine($"--self describes the running HCG Executable Maker only ({HostInfo.AppBundlePath ?? "not running from an .app"}), not {app}. Nothing was written.");
                return 2;
            }
            source = MakerBuildProvenance.Embedded();
            if (checkout.Commit is { } seen && source.Commit is { } embedded && seen != embedded)
            {
                Console.Error.WriteLine($"The checkout is at {seen}, but this Maker was built from {embedded}: they disagree, so no passport was written.");
                return 6;
            }
        }
        else source = checkout;
        var result = MacReleasePassportWriter.CreateAsync(new MacReleasePassportWriter.Input(
            app, file, facts, signing, source, Arg("--provider") ?? "executable-maker-local", Arg("--toolchain") ?? "HCG Executable Maker " + HostInfo.ProductVersion,
            Arg("--build-number"), Arg("--version"), "HCG Executable Maker " + HostInfo.ProductVersion), CancellationToken.None).GetAwaiter().GetResult();
        if (result.Passport is null) { Console.Error.WriteLine("Not written: " + string.Join("; ", result.Problems)); return MacReleasePassportWriter.KindOf(file).Length == 0 ? 2 : 6; }
        var output = MacReleasePassportWriter.WriteAsync(result.Passport, file, Arg("--out"), CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine($"Release Passport: {output}");
        Console.WriteLine($"  {Path.GetFileName(file)} · SHA-256 {result.Sha256} · {result.Size:N0} bytes · {facts.BundleIdentifier} {result.Version} · {string.Join("/", facts.Architectures)}");
        Console.WriteLine($"  code signing: {signing.Status} ({MacSigningDoctor.Describe(signing.SignatureKind)}); Gatekeeper {signing.Gatekeeper}; {signing.Notarization}");
        Console.WriteLine($"  source: {source.Commit ?? "unknown"}{(source.Dirty == true ? " (uncommitted changes)" : "")}");
        return 0;
    }
}
