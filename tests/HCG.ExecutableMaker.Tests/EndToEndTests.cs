using System.IO.Compression;
using HCG.ExecutableMaker.BuildEngine;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Infrastructure.Import;
using HCG.ExecutableMaker.Infrastructure.Persistence;
using HCG.ExecutableMaker.Infrastructure.Processes;
using HCG.ExecutableMaker.Infrastructure.Toolchains;
using HCG.ExecutableMaker.Packaging;
using HCG.ExecutableMaker.Packaging.Packagers;
using HCG.ExecutableMaker.Packaging.Verification;
using HCG.ExecutableMaker.ProjectInspection;
using HCG.ExecutableMaker.ProjectInspection.DotNet;
using HCG.ExecutableMaker.ProjectInspection.Ecosystems;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace HCG.ExecutableMaker.Tests;

/// <summary>
/// Real pipeline: ZIP import → static inspection → dotnet restore/build/publish → packaging → verification → SQLite.
/// Requires a .NET 10 SDK and NuGet access (Avalonia packages). Set HCG_SKIP_INTEGRATION=1 to skip.
/// </summary>
[Trait("Category", "Integration")]
public class EndToEndTests(ITestOutputHelper output) : IDisposable
{
    private readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    private sealed class Observer(ITestOutputHelper output) : IBuildObserver
    {
        public void OnStage(BuildStage stage, string description) => output.WriteLine($"== {stage}: {description}");
        public void OnLog(BuildLogLine line) { if (line.Kind is BuildLogKind.Error or BuildLogKind.Command or BuildLogKind.Success) output.WriteLine(line.Text); }
    }

    [RequiresDotNetFact]
    public async Task Imports_zip_inspects_builds_and_packages_fixture_for_three_platforms()
    {
        var paths = new AppPaths(_t.Combine("app"));
        var db = new SqliteDatabase(paths);
        var projects = new SqliteProjectRepository(db);
        var builds = new SqliteBuildRepository(db);
        var runner = new ProcessRunner();
        var toolchains = new ToolchainService(new ToolchainDetector(runner, new ExecutableLocator(), paths, TimeProvider.System));
        var projectService = new ProjectService(new ProjectImporter(paths, TimeProvider.System),
            new ProjectInspectionService(new IProjectInspector[] { new DotNetProjectInspector(TimeProvider.System) }.Concat(EcosystemSignatureInspector.CreateDefaults(TimeProvider.System)), TimeProvider.System),
            projects, new SqliteSettingsStore(db), toolchains, paths, TimeProvider.System, NullLogger<ProjectService>.Instance);
        var buildService = new BuildService(new LocalProcessBuildWorker(runner), toolchains, builds,
            new PackagingRegistry([new PortableFolderPackager(), new PortableZipPackager(), new MacAppBundlePackager(), new DmgPackager(runner)]),
            paths, TimeProvider.System);

        // 1. Package the fixture as a ZIP (as a user would receive it) and import it.
        var zip = _t.Combine("HelloAvalonia.zip");
        ZipFile.CreateFromDirectory(Fixtures.HelloAvalonia, zip, CompressionLevel.Fastest, includeBaseDirectory: true);
        var import = await projectService.ImportZipAsync(zip, null, CancellationToken.None);
        var project = import.Project;

        // 2. Static inspection.
        var analysis = project.Analysis!;
        Assert.Equal(SupportLevel.Supported, analysis.Support);
        Assert.Equal("Avalonia desktop application", analysis.ApplicationType);
        Assert.Equal("HelloAvalonia/HelloAvalonia.csproj", analysis.PrimaryProjectPath);
        Assert.Contains("HelloAvalonia/Assets/icon.png", analysis.DetectedIcons);
        var icon = Path.Combine(project.SourceDirectory, "HelloAvalonia", "Assets", "icon.png");

        BuildSettings Settings(TargetPlatform p, TargetArchitecture a, params PackagingFormat[] formats) => new()
        {
            ProjectRelativePath = analysis.PrimaryProjectPath!,
            Platform = p,
            Architecture = a,
            OutputDirectory = _t.Combine("out"),
            Timeout = TimeSpan.FromMinutes(15),
            Identity = new AppIdentity { ApplicationName = "Hello Avalonia", Version = "1.4.2", ApplicationId = "com.hakmi.hello", Publisher = "HCG Tests", IconSourcePath = icon },
            PackagingFormats = formats,
        };

        async Task<BuildRecord> Run(BuildSettings s)
        {
            var b = await buildService.RunAsync(new BuildRequest { Project = project, Settings = s, TrustAcknowledged = true, AssemblyName = "HelloAvalonia" }, new Observer(output), CancellationToken.None);
            output.WriteLine($"Build {s.RuntimeIdentifier}: {b.Status} {b.FailureReason} ({b.Duration})");
            foreach (var a in b.Artifacts)
            {
                output.WriteLine($"  {a.Format} {a.Path} {a.SizeBytes:N0} bytes sha256={a.Sha256} {a.Verification}");
                foreach (var c in a.Checks) output.WriteLine($"     - {c.Name}: {c.Status} {c.Detail}");
            }
            Assert.True(b.Status == BuildStatus.Succeeded, $"{s.RuntimeIdentifier}: {b.FailureReason}");
            return b;
        }

        // 3. Linux (this host): portable folder + zip, verified ELF.
        var host = HostInfo.CurrentPlatform;
        var linux = await Run(Settings(TargetPlatform.Linux, TargetArchitecture.X64, PackagingFormat.PortableFolder, PackagingFormat.PortableZip));
        var linuxFolder = linux.Artifacts.Single(a => a.Format == PackagingFormat.PortableFolder).Path;
        Assert.Equal(BinaryFormat.Elf, BinaryInspector.Inspect(Path.Combine(linuxFolder, "HelloAvalonia")).Format);

        // 4. macOS arm64: .app bundle with generated .icns and Info.plist.
        var mac = await Run(Settings(TargetPlatform.MacOS, TargetArchitecture.Arm64, PackagingFormat.MacAppBundle, PackagingFormat.PortableZip));
        var bundle = mac.Artifacts.First(a => a.Format == PackagingFormat.MacAppBundle && a.Kind == ArtifactKind.Directory).Path;
        Assert.EndsWith("Hello Avalonia.app", bundle);
        var plist = InfoPlist.Read(Path.Combine(bundle, "Contents", "Info.plist"));
        Assert.Equal("com.hakmi.hello", plist["CFBundleIdentifier"]);
        Assert.Equal("1.4.2", plist["CFBundleShortVersionString"]);
        Assert.Equal("HelloAvalonia", plist["CFBundleExecutable"]);
        Assert.True(File.Exists(Path.Combine(bundle, "Contents", "Resources", "AppIcon.icns")));
        var macExe = BinaryInspector.Inspect(Path.Combine(bundle, "Contents", "MacOS", "HelloAvalonia"));
        Assert.Equal(TargetArchitecture.Arm64, macExe.Architecture);
        Assert.True(macExe.HasCodeSignature);
        if (host != TargetPlatform.MacOS)
            Assert.Contains(mac.Artifacts, a => a.Path.EndsWith(".app.zip"));

        // 5. Windows x64: portable zip with PE executable.
        var win = await Run(Settings(TargetPlatform.Windows, TargetArchitecture.X64, PackagingFormat.PortableZip));
        var winZip = win.Artifacts.Single().Path;
        using (var archive = ZipFile.OpenRead(winZip))
            Assert.NotNull(archive.GetEntry("Hello Avalonia-1.4.2-win-x64/HelloAvalonia.exe"));

        // 6. Everything is recorded in history.
        var history = await builds.ListAsync(project.Id);
        Assert.Equal(3, history.Count);
        Assert.All(history, b => Assert.Equal(BuildStatus.Succeeded, b.Status));
        var stats = await builds.GetStatisticsAsync();
        Assert.Equal(3, stats.Succeeded);
    }

    [RequiresDotNetFact]
    public async Task Real_compile_error_is_reported_as_failure()
    {
        var paths = new AppPaths(_t.Combine("app"));
        var db = new SqliteDatabase(paths);
        var runner = new ProcessRunner();
        var toolchains = new ToolchainService(new ToolchainDetector(runner, new ExecutableLocator(), paths, TimeProvider.System));
        _t.Write("Broken/Broken.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        _t.Write("Broken/Program.cs", "System.Console.WriteLine(\"missing semicolon\")");
        var projectService = new ProjectService(new ProjectImporter(paths, TimeProvider.System),
            new ProjectInspectionService([new DotNetProjectInspector(TimeProvider.System)], TimeProvider.System),
            new SqliteProjectRepository(db), new SqliteSettingsStore(db), toolchains, paths, TimeProvider.System, NullLogger<ProjectService>.Instance);
        var project = (await projectService.ImportDirectoryAsync(_t.Combine("Broken"), null, CancellationToken.None)).Project;

        var service = new BuildService(new LocalProcessBuildWorker(runner), toolchains, new SqliteBuildRepository(db),
            new PackagingRegistry([new PortableFolderPackager()]), paths, TimeProvider.System);
        var build = await service.RunAsync(new BuildRequest
        {
            Project = project,
            TrustAcknowledged = true,
            Settings = new BuildSettings
            {
                ProjectRelativePath = "Broken.csproj", Platform = TargetPlatform.Linux, Architecture = TargetArchitecture.X64,
                OutputDirectory = _t.Combine("out"), Identity = new AppIdentity { ApplicationName = "Broken", Version = "1.0.0", ApplicationId = "com.example.broken" },
            },
        }, new Observer(output), CancellationToken.None);

        Assert.Equal(BuildStatus.Failed, build.Status);
        Assert.NotEqual(0, build.ExitCode);
        Assert.Contains(build.Messages, m => m.Severity == DiagnosticSeverity.Error && m.Code == "CS1002");
        Assert.Empty(build.Artifacts);
        Assert.False(Directory.Exists(_t.Combine("out")));
    }
}
