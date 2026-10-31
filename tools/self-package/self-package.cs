#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property PublishAot=false
#:project ../../src/HCG.ExecutableMaker.Infrastructure/HCG.ExecutableMaker.Infrastructure.csproj
#:project ../../src/HCG.ExecutableMaker.ProjectInspection/HCG.ExecutableMaker.ProjectInspection.csproj
#:project ../../src/HCG.ExecutableMaker.BuildEngine/HCG.ExecutableMaker.BuildEngine.csproj
// Dogfooding: HCG Executable Maker builds and packages itself through its own engine.
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

// Usage: dotnet run tools/self-package/self-package.cs -- <repo-root> <output-dir outside the repo>
var repo = Path.GetFullPath(args[0]); var output = Path.GetFullPath(args[1]);
if (PathGuard.IsWithin(repo, output)) { Console.Error.WriteLine("Choose an output directory outside the repository (it would be imported on the next run)."); return 2; }
var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "hcg-selfpack-" + Guid.NewGuid().ToString("N")[..8]));
var db = new SqliteDatabase(paths);
var runner = new ProcessRunner();
var toolchains = new ToolchainService(new ToolchainDetector(runner, new ExecutableLocator(), paths, TimeProvider.System));
var projects = new ProjectService(new ProjectImporter(paths, TimeProvider.System),
    new ProjectInspectionService([new DotNetProjectInspector(TimeProvider.System)], TimeProvider.System),
    new SqliteProjectRepository(db), new SqliteSettingsStore(db), toolchains, paths, TimeProvider.System, NullLogger<ProjectService>.Instance);
var builds = new BuildService(new LocalProcessBuildWorker(runner), toolchains, new SqliteBuildRepository(db),
    new PackagingRegistry([new PortableFolderPackager(), new PortableZipPackager(), new MacAppBundlePackager()]), paths, TimeProvider.System);

var import = await projects.ImportDirectoryAsync(repo, null, CancellationToken.None);
var a = import.Project.Analysis!;
Console.WriteLine($"Imported {import.FileCount} files. Primary: {a.PrimaryProjectPath} ({a.ApplicationType}, {a.Support})");
var icon = Path.Combine(import.Project.SourceDirectory, "src/HCG.ExecutableMaker.App/Assets/Branding/hcg-app-icon.png");

foreach (var (platform, arch, formats) in new (TargetPlatform, TargetArchitecture, PackagingFormat[])[]
{
    (TargetPlatform.MacOS, TargetArchitecture.Arm64, [PackagingFormat.MacAppBundle]),
    (TargetPlatform.MacOS, TargetArchitecture.X64, [PackagingFormat.MacAppBundle]),
    (TargetPlatform.Windows, TargetArchitecture.X64, [PackagingFormat.PortableZip]),
})
{
    var settings = new BuildSettings
    {
        ProjectRelativePath = a.PrimaryProjectPath!, Platform = platform, Architecture = arch, OutputDirectory = output,
        Identity = new AppIdentity { ApplicationName = "HCG Executable Maker", Publisher = "Hakmi Capital Group", Version = "0.1.0",
            ApplicationId = "com.hakmicapital.executablemaker", IconSourcePath = icon, Description = "One project in. A distributable application out." },
        PackagingFormats = formats,
    };
    var b = await builds.RunAsync(new BuildRequest { Project = import.Project, Settings = settings, TrustAcknowledged = true, AllowOverwriteOutput = true, AssemblyName = "HCGExecutableMaker" },
        new Obs(), CancellationToken.None);
    Console.WriteLine($"{settings.RuntimeIdentifier}: {b.Status} {b.FailureReason} in {b.Duration}");
    foreach (var art in b.Artifacts)
    {
        Console.WriteLine($"  {art.Path} {art.SizeBytes / 1048576.0:F1} MB sha256={art.Sha256} {art.Verification}");
        foreach (var c in art.Checks.Where(c => c.Status != VerificationStatus.Passed)) Console.WriteLine($"     - {c.Name}: {c.Status} {c.Detail}");
    }
}

return 0;

sealed class Obs : IBuildObserver
{
    public void OnLog(BuildLogLine l) { if (l.Kind == BuildLogKind.Error) Console.WriteLine("  ! " + l.Text); }
}
