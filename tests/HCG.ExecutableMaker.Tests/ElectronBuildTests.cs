using System.Buffers.Binary;
using System.Text;
using HCG.ExecutableMaker.BuildEngine;
using HCG.ExecutableMaker.BuildEngine.Electron;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Infrastructure.Persistence;
using HCG.ExecutableMaker.Packaging;
using HCG.ExecutableMaker.Packaging.Packagers;
using HCG.ExecutableMaker.Packaging.Verification;
using HCG.ExecutableMaker.ProjectInspection;
using HCG.ExecutableMaker.ProjectInspection.Node;
using HCG.ExecutableMaker.Security;

namespace HCG.ExecutableMaker.Tests;

/// <summary>Electron build planning, artifact discovery/verification and BuildService orchestration with a scripted worker.</summary>
public class ElectronBuildTests : IDisposable
{
    private readonly TempDir _t = new();
    private readonly AppPaths _paths;
    private readonly SqliteBuildRepository _builds;
    private readonly ProjectRecord _project;
    private static readonly HostEnvironment Mac = new(TargetPlatform.MacOS, TargetArchitecture.Arm64, "macOS", ".NET", "mac");
    private static readonly HostEnvironment Linux = new(TargetPlatform.Linux, TargetArchitecture.X64, "Linux", ".NET", "linux");
    private static readonly NodeInstallation Node = new("/opt/node22/bin/node", "22.14.0", "PATH", "/opt/node22/lib/node_modules/npm/bin/npm-cli.js", "10.9.2");

    public ElectronBuildTests()
    {
        _paths = new AppPaths(_t.Combine("app"));
        _paths.EnsureCreated();
        var db = new SqliteDatabase(_paths);
        _builds = new SqliteBuildRepository(db);
        var ws = Path.Combine(_paths.WorkspacesDirectory, "p1");
        var src = Path.Combine(ws, "source", "apps", "studio");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "package.json"), File.ReadAllText(Path.Combine(Fixtures.RepoRoot, "tests", "Fixtures", "ElectronConfigs", "presentation-studio-desktop", "package.json")));
        File.WriteAllText(Path.Combine(src, "electron-builder.yml"), File.ReadAllText(Path.Combine(Fixtures.RepoRoot, "tests", "Fixtures", "ElectronConfigs", "presentation-studio-desktop", "electron-builder.yml")));
        File.WriteAllText(Path.Combine(src, "package-lock.json"), "{}");
        _project = new ProjectRecord { Id = Guid.NewGuid(), Name = "Studio", SourceKind = ProjectSourceKind.Directory, SourceLocation = "/orig", WorkspacePath = ws, ImportedAt = DateTimeOffset.UtcNow };
        new SqliteProjectRepository(db).UpsertAsync(_project).GetAwaiter().GetResult();
    }

    public void Dispose() => _t.Dispose();

    private string ProjectDir => Path.Combine(_project.SourceDirectory, "apps", "studio");

    private NodeProjectInfo Inspected() =>
        NodeProjectScanner.Scan(SourceIndex.Build(_project.SourceDirectory)).Projects.Single();

    private BuildSettings Settings(HostEnvironment host, params PackagingFormat[] formats)
    {
        var combo = NodeCapabilities.Combinations(host).First();
        return ElectronBuildDefaults.Settings(Inspected(), combo, formats.Length == 0 ? [PackagingFormat.MacAppBundle] : formats, _t.Combine("out"));
    }

    [Fact]
    public void Defaults_follow_the_projects_mac_packaging_script_and_configuration()
    {
        var s = Settings(Mac, PackagingFormat.MacAppBundle, PackagingFormat.PortableZip, PackagingFormat.MacDmg);
        var e = s.Electron!;
        Assert.Equal("apps/studio", e.ProjectDirectory);
        Assert.Equal("desktop:dist:mac", e.PackagingScript);
        Assert.Equal("desktop:build", Assert.Single(e.CompileSteps).NpmScript);
        Assert.Equal(["zip", "dmg"], e.Targets); // the .app is built on the way to zip/dmg
        Assert.Equal("mac", e.Platform);
        Assert.True(e.UseLockfile);
        Assert.Equal("Presentation Studio", e.ProductName);
        Assert.Equal("release", e.OutputDirectory);
        Assert.Equal("com.oslosprint.presentationstudio", s.Identity.ApplicationId);
        Assert.Equal("0.1.0", s.Identity.Version);
        Assert.Equal(TargetArchitecture.Arm64, s.Architecture);
        Assert.Equal(["dir"], ElectronBuildDefaults.Targets([PackagingFormat.MacAppBundle]));
        // On Linux the script without a platform flag is followed (desktop:pack → electron-builder --dir).
        Assert.Equal("desktop:pack", Settings(Linux, PackagingFormat.PortableFolder).Electron!.PackagingScript);
    }

    [UnixFact]
    public void Plan_has_install_compile_and_package_stages_as_argument_lists()
    {
        var s = Settings(Mac, PackagingFormat.MacAppBundle);
        var env = ElectronBuildPlanner.Environment(new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin", ["HOME"] = "/Users/me" }, Node);
        var steps = ElectronBuildPlanner.Plan(new ElectronBuildPlanner.PlanInput(s, _project.SourceDirectory, Node, "/hcg/builds/1/electron-out", env));
        Assert.Equal([BuildStage.Restoring, BuildStage.Compiling, BuildStage.Publishing], steps.Select(x => x.Stage));
        Assert.All(steps, x => Assert.Equal(Node.NodePath, x.Process.Executable));
        Assert.All(steps, x => Assert.Equal(ProjectDir, x.Process.WorkingDirectory));
        Assert.Equal([Node.NpmCli!, "ci", "--no-audit", "--no-fund"], steps[0].Process.Arguments);
        Assert.Equal([Node.NpmCli!, "run", "desktop:build"], steps[1].Process.Arguments);
        var builder = steps[2].Process.Arguments;
        Assert.EndsWith(Path.Combine("node_modules", "electron-builder", "cli.js"), builder[0]);
        Assert.Equal(["--mac", "dir", "--arm64", "--publish", "never", "--config.directories.output=/hcg/builds/1/electron-out"], builder.Skip(1));
        // The main entry is produced by the compile step: checked before electron-builder runs.
        Assert.Contains(steps[2].RequiredFiles, r => r.Path.EndsWith(Path.Combine("desktop-dist", "main.mjs")) && r.Message.Contains("was not produced"));
        // The inspected Node is first on PATH (Finder launches have a minimal PATH); npm is quiet and never audits/funds.
        Assert.StartsWith("/opt/node22/bin" + Path.PathSeparator, steps[0].Process.Environment!["PATH"]);
        Assert.Equal("false", steps[0].Process.Environment!["npm_config_update_notifier"]);
    }

    [Theory]
    [InlineData("CSC_LINK")]
    [InlineData("CSC_KEY_PASSWORD")]
    [InlineData("WIN_CSC_LINK")]
    [InlineData("GH_TOKEN")]
    [InlineData("GITHUB_BASE_REF")]
    public void Electron_builder_signing_and_publishing_secrets_never_reach_the_build(string name) =>
        Assert.True(BuildEnvironmentPolicy.IsSensitive(name));

    [Fact]
    public void Validation_offers_only_implemented_host_target_combinations()
    {
        var mac = Settings(Mac, PackagingFormat.MacAppBundle);
        ElectronBuildPlanner.Validate(mac, Mac, _project.SourceDirectory);
        var windowsHost = new HostEnvironment(TargetPlatform.Windows, TargetArchitecture.X64, "Windows", ".NET", "pc");
        Assert.Contains("does not package Electron apps on Windows", Assert.Throws<BuildValidationException>(() => ElectronBuildPlanner.Validate(mac, windowsHost, _project.SourceDirectory)).Message);
        Assert.Contains("is not an Electron target HCG builds", Assert.Throws<BuildValidationException>(() => ElectronBuildPlanner.Validate(mac, Linux, _project.SourceDirectory)).Message);
        var badTarget = mac with { Electron = mac.Electron! with { Targets = ["pkg"] } };
        Assert.Contains("targets must be among", Assert.Throws<BuildValidationException>(() => ElectronBuildPlanner.Validate(badTarget, Mac, _project.SourceDirectory)).Message);
        var escape = mac with { Electron = mac.Electron! with { ProjectDirectory = "../outside" } };
        Assert.Throws<BuildValidationException>(() => ElectronBuildPlanner.Validate(escape, Mac, _project.SourceDirectory));
        var gone = mac with { Electron = mac.Electron! with { CompileSteps = [new ElectronCompileStep { NpmScript = "no-such-script" }] } };
        Assert.Contains("no longer has the script", Assert.Throws<BuildValidationException>(() => ElectronBuildPlanner.Validate(gone, Mac, _project.SourceDirectory)).Message);
        var injected = mac with { Electron = mac.Electron! with { CompileSteps = [new ElectronCompileStep { NpmScript = "x; rm -rf /" }] } };
        Assert.Contains("not a valid npm script name", Assert.Throws<BuildValidationException>(() => ElectronBuildPlanner.Validate(injected, Mac, _project.SourceDirectory)).Message);
    }

    [Fact]
    public void Discovery_finds_the_app_for_the_architecture_or_explains_what_is_missing()
    {
        var mac = Settings(Mac);
        var outDir = _t.Combine("eb-out");
        Assert.Contains("did not create its output folder", ElectronBuildPlanner.Discover(outDir, mac, "Presentation Studio").Problem);
        Directory.CreateDirectory(Path.Combine(outDir, "mac", "Presentation Studio.app")); // x64 folder only
        var (rel, problem) = ElectronBuildPlanner.Discover(outDir, mac, "Presentation Studio");
        Assert.Null(rel);
        Assert.Contains("no .app was found in mac-arm64/", problem);
        Directory.CreateDirectory(Path.Combine(outDir, "mac-arm64", "Presentation Studio.app"));
        Assert.Equal("mac-arm64/Presentation Studio.app", ElectronBuildPlanner.Discover(outDir, mac, "Presentation Studio").Relative);
        Assert.Equal("mac/Presentation Studio.app", ElectronBuildPlanner.Discover(outDir, mac with { Architecture = TargetArchitecture.X64 }, "Presentation Studio").Relative);

        var linux = Settings(Linux, PackagingFormat.PortableFolder);
        var unpacked = Path.Combine(outDir, "linux-unpacked");
        Directory.CreateDirectory(unpacked);
        File.WriteAllBytes(Path.Combine(unpacked, "chrome-sandbox"), Elf());
        File.WriteAllBytes(Path.Combine(unpacked, "libffmpeg.so"), Elf());
        File.WriteAllBytes(Path.Combine(unpacked, "presentation-studio"), Elf());
        Assert.Equal("linux-unpacked/presentation-studio", ElectronBuildPlanner.Discover(outDir, linux, null).Relative);
    }

    [Fact]
    public void Asar_header_is_read_and_the_packaged_main_entry_is_verified()
    {
        var resources = _t.Combine("Resources");
        Directory.CreateDirectory(resources);
        WriteAsar(Path.Combine(resources, "app.asar"), new()
        {
            ["package.json"] = """{"name":"presentation-studio","main":"desktop-dist/main.mjs"}""",
            ["desktop-dist/main.mjs"] = "export {}",
            ["dist/index.html"] = "<!doctype html>",
        });
        var archive = Asar.Open(Path.Combine(resources, "app.asar"));
        Assert.True(archive.Contains("dist/index.html"));
        Assert.False(archive.Contains("dist/missing.js"));
        Assert.Equal("export {}", Encoding.UTF8.GetString(archive.ReadFile("desktop-dist/main.mjs")!));
        var ok = ElectronPackagerBase.VerifyAppPayload(resources, new ElectronBuildOptions { ProjectDirectory = "", MainEntry = "desktop-dist/main.mjs" });
        Assert.All(ok, c => Assert.Equal(VerificationStatus.Passed, c.Status));

        WriteAsar(Path.Combine(resources, "app.asar"), new() { ["package.json"] = """{"main":"desktop-dist/main.mjs"}""" });
        var missing = ElectronPackagerBase.VerifyAppPayload(resources, new ElectronBuildOptions { ProjectDirectory = "" });
        Assert.Contains(missing, c => c.Status == VerificationStatus.Failed && c.Detail.Contains("would not start"));
        File.WriteAllText(Path.Combine(resources, "app.asar"), "not an archive");
        Assert.Contains(ElectronPackagerBase.VerifyAppPayload(resources, new ElectronBuildOptions { ProjectDirectory = "" }), c => c.Status == VerificationStatus.Failed);
    }

    [Fact]
    public void Catalog_offers_electron_builder_targets_only_where_implemented()
    {
        var report = new ToolchainReport(Mac, [new ToolchainInfo { Id = ToolchainIds.Hdiutil, DisplayName = "hdiutil", Category = "macOS", Status = ToolchainStatus.Installed, ExecutablePath = "/usr/bin/hdiutil" }], null, null, DateTimeOffset.UtcNow);
        var mac = PackagingCatalog.Evaluate(new PackagingContext(TargetPlatform.MacOS, Mac, report, true) { IsElectron = true });
        Assert.Equal([PackagingFormat.MacAppBundle, PackagingFormat.PortableZip, PackagingFormat.MacDmg], mac.Where(o => o.IsAvailable).Select(o => o.Format));
        Assert.Equal(PackagingAvailability.NotImplemented, mac.Single(o => o.Format == PackagingFormat.MacPkg).Availability);
        var linux = PackagingCatalog.Evaluate(new PackagingContext(TargetPlatform.Linux, Linux, null, true) { IsElectron = true });
        Assert.Equal([PackagingFormat.PortableFolder], linux.Where(o => o.IsAvailable).Select(o => o.Format));
        var macFromLinux = PackagingCatalog.Evaluate(new PackagingContext(TargetPlatform.MacOS, Linux, null, true) { IsElectron = true });
        Assert.All(macFromLinux, o => Assert.Equal(PackagingAvailability.RequiresDifferentHost, o.Availability));
        // The .app is always produced (and verified) for a mac Electron build; .NET packagers never handle Electron builds.
        var s = Settings(Mac, PackagingFormat.PortableZip);
        Assert.Equal([PackagingFormat.MacAppBundle, PackagingFormat.PortableZip], PackagingCatalog.WithDependencies(s.PackagingFormats, s));
        Assert.False(new MacAppBundlePackager().Handles(s));
        Assert.False(new PortableZipPackager().Handles(s));
        Assert.False(new DmgPackager(new HCG.ExecutableMaker.Infrastructure.Processes.ProcessRunner()).Handles(s));
    }

    // ---------- BuildService orchestration ----------

    private sealed class ScriptedWorker(Func<ProcessSpec, Action<OutputStream, string>, CancellationToken, Task<ProcessResult>> run) : IBuildWorker
    {
        public List<ProcessSpec> Calls { get; } = [];
        public IsolationLevel Isolation => IsolationLevel.None;
        public string Description => "test";
        public BuildEnvironmentPolicy.ScrubResult CreateEnvironment() => new(new Dictionary<string, string> { ["PATH"] = "/usr/bin" }, ["GH_TOKEN"]);
        public Task<ProcessResult> RunAsync(ProcessSpec spec, Action<OutputStream, string> onOutput, CancellationToken ct)
        {
            Calls.Add(spec);
            return run(spec, onOutput, ct);
        }
    }

    private sealed class FixedToolchains(ToolchainReport report) : IToolchainService
    {
        public ToolchainReport? Latest => report;
        public event EventHandler<ToolchainReport>? Updated { add { } remove { } }
        public Task<ToolchainReport> GetAsync(bool refresh = false, CancellationToken cancellationToken = default) => Task.FromResult(report);
    }

    private sealed class Observer : IBuildObserver
    {
        public List<string> Stages { get; } = [];
        public List<BuildLogLine> Lines { get; } = [];
        public void OnStage(BuildStage stage, string description) { lock (Stages) Stages.Add($"{stage}: {description}"); }
        public void OnLog(BuildLogLine line) { lock (Lines) Lines.Add(line); }
    }

    private BuildService Service(IBuildWorker worker, HostEnvironment host, params NodeInstallation[] nodes) =>
        new(worker, new FixedToolchains(new ToolchainReport(host, [], null, null, DateTimeOffset.UtcNow) { NodeInstallations = nodes.Length == 0 ? [Node] : nodes }), _builds,
            new PackagingRegistry([new ElectronFolderPackager(new HCG.ExecutableMaker.Infrastructure.Processes.ProcessRunner())]), _paths, TimeProvider.System);

    private BuildRequest Request(BuildSettings s) => new() { Project = _project, Settings = s, TrustAcknowledged = true };

    private static ProcessResult Exit(int code) => new(code, TimeSpan.FromMilliseconds(3), false, false);

    [Fact]
    public async Task A_failing_compile_step_fails_the_build_with_its_exit_code_and_npm_error()
    {
        var worker = new ScriptedWorker((spec, output, _) =>
        {
            if (spec.Arguments.Contains("run"))
            {
                output(OutputStream.StdErr, "npm error Lifecycle script `desktop:build` failed with error:");
                return Task.FromResult(Exit(2));
            }
            return Task.FromResult(Exit(0));
        });
        var observer = new Observer();
        var build = await Service(worker, Linux).RunAsync(Request(Settings(Linux, PackagingFormat.PortableFolder)), observer, CancellationToken.None);
        Assert.Equal(BuildStatus.Failed, build.Status);
        Assert.Equal("Compile: npm run desktop:build failed with exit code 2.", build.FailureReason);
        Assert.Equal(2, build.ExitCode);
        Assert.Contains(build.Messages, m => m.Severity == DiagnosticSeverity.Error && m.Code == "npm" && m.Text.Contains("desktop:build"));
        Assert.Equal(2, worker.Calls.Count); // electron-builder never ran
        Assert.Contains(observer.Stages, s => s.StartsWith("Restoring: Install dependencies (npm ci)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Missing_main_entry_after_compilation_is_reported_before_electron_builder_runs()
    {
        var worker = new ScriptedWorker((_, _, _) => Task.FromResult(Exit(0)));
        var build = await Service(worker, Linux).RunAsync(Request(Settings(Linux, PackagingFormat.PortableFolder)), new Observer(), CancellationToken.None);
        Assert.Equal(BuildStatus.Failed, build.Status);
        // electron-builder is checked first (not installed: the scripted npm ci installed nothing).
        Assert.Contains("electron-builder is not installed in node_modules", build.FailureReason);
        Directory.CreateDirectory(Path.Combine(ProjectDir, "node_modules", "electron-builder"));
        File.WriteAllText(Path.Combine(ProjectDir, "node_modules", "electron-builder", "cli.js"), "");
        build = await Service(worker, Linux).RunAsync(Request(Settings(Linux, PackagingFormat.PortableFolder)), new Observer(), CancellationToken.None);
        Assert.Equal("The compile step (npm run desktop:build) finished, but the main entry desktop-dist/main.mjs was not produced.", build.FailureReason);
    }

    [Fact]
    public async Task Cancellation_stops_the_build_and_is_recorded_as_cancelled()
    {
        using var cts = new CancellationTokenSource();
        var worker = new ScriptedWorker(async (spec, _, ct) =>
        {
            cts.Cancel();
            await Task.Delay(50, CancellationToken.None);
            return new ProcessResult(-1, TimeSpan.FromMilliseconds(50), Cancelled: true, TimedOut: false);
        });
        var build = await Service(worker, Linux).RunAsync(Request(Settings(Linux, PackagingFormat.PortableFolder)), new Observer(), cts.Token);
        Assert.Equal(BuildStatus.Cancelled, build.Status);
        Assert.Single(worker.Calls);
    }

    [Fact]
    public async Task No_matching_node_fails_before_any_command_runs()
    {
        var worker = new ScriptedWorker((_, _, _) => Task.FromResult(Exit(0)));
        var old = new NodeInstallation("/usr/bin/node", "20.11.0", "system", "/usr/lib/node_modules/npm/bin/npm-cli.js", "10.2.4");
        var build = await Service(worker, Linux, old).RunAsync(Request(Settings(Linux, PackagingFormat.PortableFolder)), new Observer(), CancellationToken.None);
        Assert.Equal(BuildStatus.Failed, build.Status);
        Assert.Contains("requires Node.js >=22.12", build.FailureReason);
        Assert.Empty(worker.Calls);
    }

    [Fact]
    public async Task Successful_build_discovers_and_verifies_the_unpacked_app_and_leaves_the_source_untouched()
    {
        var worker = new ScriptedWorker((spec, output, _) =>
        {
            if (spec.Arguments.Contains("ci"))
            {
                var cli = Path.Combine(ProjectDir, "node_modules", "electron-builder", "cli.js");
                Directory.CreateDirectory(Path.GetDirectoryName(cli)!);
                File.WriteAllText(cli, "");
            }
            else if (spec.Arguments.Contains("run"))
            {
                Directory.CreateDirectory(Path.Combine(ProjectDir, "desktop-dist"));
                File.WriteAllText(Path.Combine(ProjectDir, "desktop-dist", "main.mjs"), "");
            }
            else
            {
                var outArg = spec.Arguments.Single(a => a.StartsWith("--config.directories.output=", StringComparison.Ordinal));
                var unpacked = Path.Combine(outArg[(outArg.IndexOf('=') + 1)..], "linux-unpacked");
                Directory.CreateDirectory(Path.Combine(unpacked, "resources"));
                var exe = Path.Combine(unpacked, "presentation-studio");
                File.WriteAllBytes(exe, Elf());
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(exe, (UnixFileMode)0b111_101_101);
                WriteAsar(Path.Combine(unpacked, "resources", "app.asar"), new()
                {
                    ["package.json"] = """{"main":"desktop-dist/main.mjs"}""", ["desktop-dist/main.mjs"] = "export {}",
                });
                output(OutputStream.StdErr, "  • packaging       platform=linux arch=x64 electron=45.2.3 appOutDir=linux-unpacked");
            }
            return Task.FromResult(Exit(0));
        });
        var observer = new Observer();
        var build = await Service(worker, Linux).RunAsync(Request(Settings(Linux, PackagingFormat.PortableFolder)), observer, CancellationToken.None);
        Assert.True(build.Status == BuildStatus.Succeeded, build.FailureReason);
        var artifact = Assert.Single(build.Artifacts);
        Assert.Equal(PackagingFormat.PortableFolder, artifact.Format);
        Assert.True(File.Exists(Path.Combine(artifact.Path, "presentation-studio")));
        Assert.Contains(artifact.Checks, c => c.Name == "App archive" && c.Status == VerificationStatus.Passed);
        Assert.Contains(artifact.Checks, c => c.Name == "Executable format" && c.Status == VerificationStatus.Passed);
        // electron-builder's progress on stderr is output, not an error.
        Assert.DoesNotContain(observer.Lines, l => l.Kind == BuildLogKind.Error);
        Assert.Equal(["Restoring", "Compiling", "Publishing"], observer.Stages.Select(s => s[..s.IndexOf(':')]).Where(s => s is "Restoring" or "Compiling" or "Publishing").Distinct());
        // The project's own output folder (release/) was never created in the imported source.
        Assert.False(Directory.Exists(Path.Combine(ProjectDir, "release")));
    }

    /// <summary>Minimal ELF64 x86-64 executable header.</summary>
    private static byte[] Elf()
    {
        var b = new byte[64];
        b[0] = 0x7F; b[1] = (byte)'E'; b[2] = (byte)'L'; b[3] = (byte)'F'; b[4] = 2; b[5] = 1; b[6] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(16), 2);    // ET_EXEC
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(18), 0x3E); // x86-64
        return b;
    }

    /// <summary>Writes an asar archive (pickle header + JSON file tree + concatenated file data) as Electron's asar tool does.</summary>
    internal static void WriteAsar(string path, Dictionary<string, string> files)
    {
        var root = new Dictionary<string, object>();
        var data = new MemoryStream();
        foreach (var (name, content) in files)
        {
            var parts = name.Split('/');
            var node = root;
            foreach (var dir in parts[..^1])
            {
                if (!node.TryGetValue(dir, out var child)) node[dir] = child = new Dictionary<string, object> { ["files"] = new Dictionary<string, object>() };
                node = (Dictionary<string, object>)((Dictionary<string, object>)child)["files"];
            }
            var bytes = Encoding.UTF8.GetBytes(content);
            node[parts[^1]] = new Dictionary<string, object> { ["size"] = bytes.Length, ["offset"] = data.Length.ToString() };
            data.Write(bytes);
        }
        var json = Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["files"] = root }));
        var padded = (json.Length + 3) / 4 * 4;
        var headerPickle = new byte[8 + padded];
        BinaryPrimitives.WriteUInt32LittleEndian(headerPickle, (uint)(4 + padded));
        BinaryPrimitives.WriteUInt32LittleEndian(headerPickle.AsSpan(4), (uint)json.Length);
        json.CopyTo(headerPickle, 8);
        using var fs = File.Create(path);
        var size = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(size, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(size.AsSpan(4), (uint)headerPickle.Length);
        fs.Write(size);
        fs.Write(headerPickle);
        data.Position = 0;
        data.CopyTo(fs);
    }
}
