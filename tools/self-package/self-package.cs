#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property PublishAot=false
#:project ../../src/HCG.ExecutableMaker.Infrastructure/HCG.ExecutableMaker.Infrastructure.csproj
#:project ../../src/HCG.ExecutableMaker.ProjectInspection/HCG.ExecutableMaker.ProjectInspection.csproj
#:project ../../src/HCG.ExecutableMaker.BuildEngine/HCG.ExecutableMaker.BuildEngine.csproj
// Dogfooding: HCG Executable Maker builds and packages ITSELF through its own engine
// (import → static inspection → dotnet restore/build/publish → packaging → verification → checksums).
//
// Usage:
//   dotnet run --no-cache tools/self-package/self-package.cs -- <repo-root> <output-dir> [--targets osx-arm64,win-x64,...]
//                                                    [--version 0.1.0] [--dist <dir>] [--installer]
//
// <output-dir> must be outside the repository (the repository is what gets imported).
// --no-cache matters: file-based apps do not notice changes in #:project sources and would run a stale engine.
// --dist collects the distributable files (.zip/.dmg) into one flat folder with release-friendly names.
// Formats are chosen per target from PackagingCatalog: macOS → .app (+ .dmg when this host can make one),
// Windows/Linux → portable ZIP; --installer adds a Windows Setup.exe (Inno Setup; Windows host with ISCC required —
// the build fails with the reason instead of silently skipping it). Exit code is non-zero if any build fails.
using HCG.ExecutableMaker.BuildEngine;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Infrastructure.Import;
using HCG.ExecutableMaker.Infrastructure.Persistence;
using HCG.ExecutableMaker.Infrastructure.Processes;
using HCG.ExecutableMaker.Infrastructure.Toolchains;
using HCG.ExecutableMaker.Packaging;
using HCG.ExecutableMaker.Packaging.Packagers;
using HCG.ExecutableMaker.ProjectInspection;
using HCG.ExecutableMaker.ProjectInspection.DotNet;
using Microsoft.Extensions.Logging.Abstractions;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: self-package.cs <repo-root> <output-dir> [--targets rid,rid] [--version x.y.z] [--dist dir]");
    return 2;
}
var repo = Path.GetFullPath(args[0]);
// The build runs on an imported copy without .git: pass the commit so the app can show exactly which build it is.
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HCG_SOURCE_REVISION")))
{
    try
    {
        var git = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git") { ArgumentList = { "-C", repo, "rev-parse", "--short=12", "HEAD" }, RedirectStandardOutput = true, UseShellExecute = false })!;
        var commit = git.StandardOutput.ReadToEnd().Trim();
        git.WaitForExit();
        var status = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git") { ArgumentList = { "-C", repo, "status", "--porcelain", "--untracked-files=no" }, RedirectStandardOutput = true, UseShellExecute = false })!;
        var dirty = status.StandardOutput.ReadToEnd().Trim().Length > 0;
        status.WaitForExit();
        if (git.ExitCode == 0 && commit.Length > 0) Environment.SetEnvironmentVariable("HCG_SOURCE_REVISION", commit + (dirty ? "+uncommitted" : ""));
    }
    catch (System.ComponentModel.Win32Exception) { /* git not installed: the app reports commit "unknown" */ }
}
var output = Path.GetFullPath(args[1]);
string? Option(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
var targets = (Option("--targets") ?? "osx-arm64,osx-x64,win-x64").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
var version = Option("--version") ?? "0.1.0";
var dist = Option("--dist") is { } d ? Path.GetFullPath(d) : null;
var installer = args.Contains("--installer");
if (PathGuard.IsWithin(repo, output) || (dist is not null && PathGuard.IsWithin(repo, dist)))
{
    Console.Error.WriteLine("Choose output and dist directories outside the repository (they would be imported on the next run).");
    return 2;
}

var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "hcg-selfpack-" + Guid.NewGuid().ToString("N")[..8]));
var db = new SqliteDatabase(paths);
var runner = new ProcessRunner();
var toolchains = new ToolchainService(new ToolchainDetector(runner, new ExecutableLocator(), paths, TimeProvider.System));
var projects = new ProjectService(new ProjectImporter(paths, TimeProvider.System),
    new ProjectInspectionService([new DotNetProjectInspector(TimeProvider.System)], TimeProvider.System),
    new SqliteProjectRepository(db), new SqliteSettingsStore(db), toolchains, paths, TimeProvider.System, NullLogger<ProjectService>.Instance);
var builds = new BuildService(new LocalProcessBuildWorker(runner), toolchains, new SqliteBuildRepository(db),
    new PackagingRegistry([new PortableFolderPackager(), new PortableZipPackager(), new MacAppBundlePackager(), new DmgPackager(runner), new InnoSetupPackager(runner)]),
    paths, TimeProvider.System);

var report = await toolchains.GetAsync();
Console.WriteLine($"Host: {report.Host.DisplayName} · .NET SDK {report.Find(ToolchainIds.DotNetSdk)?.Version ?? "missing"}");
var import = await projects.ImportDirectoryAsync(repo, null, CancellationToken.None);
var analysis = import.Project.Analysis!;
Console.WriteLine($"Imported {import.FileCount} files. Primary: {analysis.PrimaryProjectPath} ({analysis.ApplicationType}, {analysis.Support})");
if (analysis.Support != SupportLevel.Supported || analysis.PrimaryProjectPath is null)
{
    Console.Error.WriteLine("Inspection did not report a supported application: " + analysis.SupportSummary);
    return 1;
}
var icon = Path.Combine(import.Project.SourceDirectory, "src", "HCG.ExecutableMaker.App", "Assets", "Branding", "hcg-app-icon.png");
if (dist is not null) Directory.CreateDirectory(dist);

var failures = 0;
foreach (var rid in targets)
{
    var (platform, arch) = ParseRid(rid);
    var options = PackagingCatalog.Evaluate(new PackagingContext(platform, report.Host, report, IsGuiApplication: true));
    PackagingFormat[] wanted = platform == TargetPlatform.MacOS ? [PackagingFormat.MacAppBundle, PackagingFormat.MacDmg] : [PackagingFormat.PortableZip];
    var formats = wanted.Where(f => options.Any(o => o.Format == f && o.IsAvailable)).ToList();
    if (installer && platform == TargetPlatform.Windows)
    {
        var setup = options.First(o => o.Format == PackagingFormat.WindowsInstallerExe);
        if (!setup.IsAvailable)
        {
            Console.Error.WriteLine($"{rid}: Windows installer requested but unavailable — {setup.Reason}");
            failures++;
            continue;
        }
        formats.Add(PackagingFormat.WindowsInstallerExe);
    }
    var settings = new BuildSettings
    {
        ProjectRelativePath = analysis.PrimaryProjectPath,
        Platform = platform,
        Architecture = arch,
        OutputDirectory = output,
        Identity = new AppIdentity
        {
            ApplicationName = "HCG Executable Maker", Publisher = "Hakmi Capital Group", Version = version,
            ApplicationId = "com.hakmicapital.executablemaker", IconSourcePath = File.Exists(icon) ? icon : null,
            Description = "One project in. A distributable application out.",
        },
        PackagingFormats = formats,
    };
    Console.WriteLine($"── {rid}: {string.Join(", ", formats.Select(f => f.DisplayName()))}");
    var build = await builds.RunAsync(new BuildRequest
    {
        Project = import.Project, Settings = settings, TrustAcknowledged = true, AllowOverwriteOutput = true, AssemblyName = "HCGExecutableMaker",
    }, new ConsoleObserver(), CancellationToken.None);
    Console.WriteLine($"{rid}: {build.Status} {build.FailureReason} in {build.Duration}");
    foreach (var art in build.Artifacts)
    {
        Console.WriteLine($"  {art.Path}  {art.SizeBytes / 1048576.0:F1} MB  sha256={art.Sha256}  {art.Verification}");
        foreach (var c in art.Checks.Where(c => c.Status != VerificationStatus.Passed))
            Console.WriteLine($"     - {c.Name}: {c.Status} — {c.Detail}");
    }
    if (build.Status != BuildStatus.Succeeded) { failures++; continue; }
    if (dist is not null) await CollectAsync(build, settings, dist);
}
return failures == 0 ? 0 : 1;

async Task CollectAsync(BuildRecord build, BuildSettings settings, string distDir)
{
    var baseName = $"HCG-Executable-Maker-{settings.Identity.Version}-{settings.RuntimeIdentifier}";
    foreach (var art in build.Artifacts.Where(a => a.Kind == ArtifactKind.File))
    {
        var suffix = art.Path.EndsWith(".app.zip", StringComparison.Ordinal) ? ".app.zip"
            : art.Path.EndsWith("-Setup.exe", StringComparison.Ordinal) ? "-Setup.exe" : Path.GetExtension(art.Path);
        File.Copy(art.Path, Path.Combine(distDir, baseName + suffix), overwrite: true);
    }
    // On a Mac host the bundle packager emits no transfer ZIP; create one with ditto (preserves permissions and xattrs).
    if (OperatingSystem.IsMacOS() && build.Artifacts.FirstOrDefault(a => a.Format == PackagingFormat.MacAppBundle && a.Kind == ArtifactKind.Directory) is { } app)
    {
        var zip = Path.Combine(distDir, baseName + ".app.zip");
        var result = await runner.RunAsync(new ProcessSpec { Executable = "/usr/bin/ditto", Arguments = ["-c", "-k", "--keepParent", app.Path, zip] },
            (_, line) => Console.WriteLine(line), CancellationToken.None);
        if (!result.Succeeded) throw new InvalidOperationException($"ditto failed with exit code {result.ExitCode}");
    }
}

static (TargetPlatform, TargetArchitecture) ParseRid(string rid)
{
    var parts = rid.Split('-');
    var platform = parts[0] switch
    {
        "osx" => TargetPlatform.MacOS,
        "win" => TargetPlatform.Windows,
        "linux" => TargetPlatform.Linux,
        _ => throw new ArgumentException($"Unsupported runtime identifier '{rid}'."),
    };
    var arch = parts.Length > 1 && parts[1] == "arm64" ? TargetArchitecture.Arm64 : TargetArchitecture.X64;
    return (platform, arch);
}

sealed class ConsoleObserver : IBuildObserver
{
    public void OnStage(BuildStage stage, string description) => Console.WriteLine($"   [{stage}] {description}");
    public void OnLog(BuildLogLine line)
    {
        if (line.Kind is BuildLogKind.Error or BuildLogKind.Warning) Console.WriteLine($"   {line.Kind.ToString().ToUpperInvariant()}: {line.Text}");
    }
}
