using HCG.ExecutableMaker.BuildEngine;
using HCG.ExecutableMaker.BuildEngine.Electron;
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
using HCG.ExecutableMaker.ProjectInspection.Ecosystems;
using HCG.ExecutableMaker.ProjectInspection.Node;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace HCG.ExecutableMaker.Tests;

/// <summary>Runs only when HCG_ELECTRON_PROJECT points at an Electron project working copy (e.g. Presentation Studio).</summary>
public sealed class ElectronProjectFactAttribute : FactAttribute
{
    public ElectronProjectFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("HCG_ELECTRON_PROJECT") is not { Length: > 0 } dir || !Directory.Exists(dir))
            Skip = "Set HCG_ELECTRON_PROJECT to an Electron project folder to run the real npm + electron-builder build.";
    }
}

/// <summary>
/// The real pipeline for an Electron app: directory import → static inspection → npm ci → the project's compile
/// script → electron-builder → artifact discovery → verification. Network access to the npm registry and Electron's
/// download host is required.
/// </summary>
[Trait("Category", "Integration")]
public class ElectronEndToEndTests(ITestOutputHelper output) : IDisposable
{
    private readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    private sealed class Observer(ITestOutputHelper output, List<string> stages) : IBuildObserver
    {
        public void OnStage(BuildStage stage, string description) { stages.Add($"{stage}: {description}"); output.WriteLine($"== {stage}: {description}"); }
        public void OnLog(BuildLogLine line)
        {
            if (line.Kind is not BuildLogKind.Output) output.WriteLine($"[{line.Kind}] {line.Text}");
        }
    }

    [ElectronProjectFact]
    public async Task Imports_inspects_builds_and_verifies_the_electron_app()
    {
        var source = Environment.GetEnvironmentVariable("HCG_ELECTRON_PROJECT")!;
        var paths = new AppPaths(_t.Combine("app"));
        var db = new SqliteDatabase(paths);
        var runner = new ProcessRunner();
        var toolchains = new ToolchainService(new ToolchainDetector(runner, new ExecutableLocator(), paths, TimeProvider.System));
        var projectService = new ProjectService(new ProjectImporter(paths, TimeProvider.System),
            new ProjectInspectionService(new IProjectInspector[] { new DotNetProjectInspector(TimeProvider.System) }.Concat(EcosystemSignatureInspector.CreateDefaults(TimeProvider.System)), TimeProvider.System),
            new SqliteProjectRepository(db), new SqliteSettingsStore(db), toolchains, paths, TimeProvider.System, NullLogger<ProjectService>.Instance);
        var buildService = new BuildService(new LocalProcessBuildWorker(runner), toolchains, new SqliteBuildRepository(db),
            new PackagingRegistry([new ElectronAppBundlePackager(runner), new ElectronZipPackager(runner), new ElectronDmgPackager(runner), new ElectronFolderPackager(runner),
                new PortableFolderPackager(), new PortableZipPackager(), new MacAppBundlePackager(), new DmgPackager(runner)]),
            paths, TimeProvider.System);

        var import = await projectService.ImportDirectoryAsync(source, null, CancellationToken.None);
        var analysis = import.Project.Analysis!;
        output.WriteLine($"Inspection: {analysis.InspectorId} {analysis.Support} — {analysis.SupportSummary}");
        foreach (var d in analysis.Diagnostics) output.WriteLine($"  {d.Severity}: {d.Message}");
        Assert.Equal("node", analysis.InspectorId);
        Assert.Equal(SupportLevel.Supported, analysis.Support);
        var project = analysis.Node!.Projects.Single(p => p.ManifestPath == analysis.PrimaryProjectPath);

        var report = await toolchains.GetAsync(cancellationToken: CancellationToken.None);
        var combo = NodeCapabilities.Combinations(report.Host).First(c => c.Architecture == report.Host.Architecture || report.Host.Platform == TargetPlatform.MacOS);
        IReadOnlyList<PackagingFormat> formats = report.Host.Platform == TargetPlatform.MacOS
            ? [PackagingFormat.MacAppBundle, PackagingFormat.PortableZip, PackagingFormat.MacDmg]
            : [PackagingFormat.PortableFolder];
        var settings = ElectronBuildDefaults.Settings(project, combo, formats, Environment.GetEnvironmentVariable("HCG_ELECTRON_OUT") is { Length: > 0 } outDir ? outDir : _t.Combine("out"));
        output.WriteLine($"Following script: {settings.Electron!.PackagingScript}; compile: {string.Join(", ", settings.Electron.CompileSteps.Select(c => c.Display))}; targets: {string.Join(" ", settings.Electron.Targets)}");

        var stages = new List<string>();
        var build = await buildService.RunAsync(new BuildRequest { Project = import.Project, Settings = settings, TrustAcknowledged = true }, new Observer(output, stages), CancellationToken.None);
        output.WriteLine($"Build: {build.Status} {build.FailureReason} ({build.Duration})");
        foreach (var (k, v) in build.ToolchainVersions) output.WriteLine($"  {k} = {v}");
        foreach (var a in build.Artifacts)
        {
            output.WriteLine($"  {a.Format} {a.Path} {a.SizeBytes:N0} bytes {a.Verification}");
            foreach (var c in a.Checks) output.WriteLine($"     - {c.Name}: {c.Status} {c.Detail}");
        }
        Assert.True(build.Status == BuildStatus.Succeeded, build.FailureReason);
        Assert.Contains(stages, s => s.StartsWith("Restoring: Install dependencies", StringComparison.Ordinal));
        Assert.Contains(stages, s => s.StartsWith("Compiling: Compile: npm run", StringComparison.Ordinal));
        Assert.Contains(stages, s => s.StartsWith("Publishing: Package with electron-builder", StringComparison.Ordinal));
        Assert.All(build.Artifacts, a => Assert.NotEqual(VerificationStatus.Failed, a.Verification));
        if (report.Host.Platform == TargetPlatform.MacOS)
            Assert.Contains(build.Artifacts, a => a.Format == PackagingFormat.MacAppBundle && a.Path.EndsWith($"{settings.Electron.ProductName}.app", StringComparison.Ordinal));
        // The imported source is untouched by electron-builder: its output went to HCG's build folder.
        Assert.False(Directory.Exists(Path.Combine(import.Project.SourceDirectory, project.Directory, settings.Electron.OutputDirectory)));

        // For follow-up checks (launch, the project's own packaged-app test): write where the artifacts are.
        if (Environment.GetEnvironmentVariable("HCG_ELECTRON_RESULT") is { Length: > 0 } resultFile)
            await File.WriteAllLinesAsync(resultFile, build.Artifacts.Select(a => $"{a.Format}={a.Path}"));
    }
}
