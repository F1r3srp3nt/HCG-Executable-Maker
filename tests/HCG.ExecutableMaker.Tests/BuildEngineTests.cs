using HCG.ExecutableMaker.BuildEngine;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Infrastructure.Persistence;
using HCG.ExecutableMaker.Packaging;
using HCG.ExecutableMaker.Packaging.Packagers;
using HCG.ExecutableMaker.Security;

namespace HCG.ExecutableMaker.Tests;

public class BuildPlannerTests : IDisposable
{
    private readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    internal static BuildSettings Settings(string output, TargetPlatform platform = TargetPlatform.MacOS, TargetArchitecture arch = TargetArchitecture.Arm64,
        string version = "1.2.3", string appId = "com.example.app", string name = "My App", IReadOnlyList<PackagingFormat>? formats = null) => new()
    {
        ProjectRelativePath = "src/App/App.csproj",
        Platform = platform,
        Architecture = arch,
        OutputDirectory = output,
        Identity = new AppIdentity { ApplicationName = name, Version = version, ApplicationId = appId, Publisher = "Example Co" },
        PackagingFormats = formats ?? [PackagingFormat.PortableFolder],
    };

    private DotNetBuildPlanner.PlanInput Input(BuildSettings s, TargetPlatform host = TargetPlatform.Linux, string? ico = null) =>
        new(s, _t.Path, Fixtures.FakeDotNet, _t.Combine("publish"), host, ico, new Dictionary<string, string>());

    [Fact]
    public void Plans_restore_build_publish_with_structured_arguments()
    {
        _t.Write("src/App/App.csproj", "<Project/>");
        var steps = DotNetBuildPlanner.Plan(Input(Settings(_t.Combine("out"))));
        var proj = _t.Combine("src", "App", "App.csproj");

        Assert.Equal([BuildStage.Restoring, BuildStage.Compiling, BuildStage.Publishing], steps.Select(s => s.Stage));
        Assert.All(steps, s => Assert.Equal(Fixtures.FakeDotNet, s.Process.Executable));
        Assert.All(steps, s => Assert.Equal(_t.Combine("src", "App"), s.Process.WorkingDirectory));
        string[] common = ["-nologo", "--disable-build-servers", "-tl:off", "-p:UseSharedCompilation=false"];
        Assert.Equal(["restore", proj, "-r", "osx-arm64", .. common, "-p:SelfContained=true", "-p:Version=1.2.3"], steps[0].Process.Arguments);
        Assert.Equal(["build", proj, "-c", "Release", "-r", "osx-arm64", "--self-contained", "--no-restore", .. common, "-p:Version=1.2.3"], steps[1].Process.Arguments);
        Assert.Equal(["publish", proj, "-c", "Release", "-r", "osx-arm64", "--self-contained", "--no-restore", "-o", _t.Combine("publish"), .. common, "-p:Version=1.2.3"], steps[2].Process.Arguments);
    }

    [Theory]
    [InlineData(TargetPlatform.Windows, TargetArchitecture.X64, "win-x64")]
    [InlineData(TargetPlatform.Windows, TargetArchitecture.Arm64, "win-arm64")]
    [InlineData(TargetPlatform.MacOS, TargetArchitecture.X64, "osx-x64")]
    [InlineData(TargetPlatform.Linux, TargetArchitecture.Arm64, "linux-arm64")]
    public void Maps_runtime_identifiers(TargetPlatform p, TargetArchitecture a, string rid) =>
        Assert.Equal(rid, PlatformNames.ToRuntimeIdentifier(p, a));

    [Fact]
    public void Windows_target_from_non_windows_host_enables_windows_targeting_and_icon()
    {
        _t.Write("src/App/App.csproj", "<Project/>");
        var s = Settings(_t.Combine("out"), TargetPlatform.Windows, TargetArchitecture.X64) with { SingleFile = true, SelfContained = false };
        var icon = _t.Combine("icon;x.ico");
        var steps = DotNetBuildPlanner.Plan(Input(s, TargetPlatform.Linux, icon));
        var publish = steps[2].Process.Arguments;
        Assert.Contains("-p:EnableWindowsTargeting=true", publish);
        Assert.Contains("-p:ApplicationIcon=" + icon.Replace(";", "%3B"), publish);
        Assert.Contains("-p:PublishSingleFile=true", publish);
        Assert.Contains("--no-self-contained", publish);
        Assert.DoesNotContain("-p:SelfContained=true", steps[0].Process.Arguments);
    }

    [Theory]
    [InlineData("a;b", "a%3Bb")]
    [InlineData("$(Evil)", "%24(Evil)")]
    [InlineData("50%,@'*?\"", "50%25%2C%40%27%2A%3F%22")]
    [InlineData("plain value", "plain value")]
    public void Escapes_msbuild_property_values(string input, string expected) =>
        Assert.Equal(expected, DotNetBuildPlanner.EscapeMsBuild(input));

    [Theory]
    [InlineData("1.0", true)]
    [InlineData("1.2.3.4", true)]
    [InlineData("1.0.0-beta.1", true)]
    [InlineData("1", false)]
    [InlineData("1.0;rm -rf /", false)]
    [InlineData("v1.0", false)]
    public void Validates_version(string version, bool ok)
    {
        _t.Write("src/App/App.csproj", "<Project/>");
        var s = Settings(_t.Combine("out"), version: version);
        if (ok) DotNetBuildPlanner.Validate(s);
        else Assert.Throws<BuildValidationException>(() => DotNetBuildPlanner.Validate(s));
    }

    [Theory]
    [InlineData("com.example.app", true)]
    [InlineData("com.hakmi-capital.mail", true)]
    [InlineData("noperiod", false)]
    [InlineData("com..double", false)]
    [InlineData("com.example.app; evil", false)]
    public void Validates_application_id(string id, bool ok)
    {
        var s = Settings(_t.Combine("out"), appId: id);
        if (ok) DotNetBuildPlanner.Validate(s);
        else Assert.Throws<BuildValidationException>(() => DotNetBuildPlanner.Validate(s));
    }

    [Fact]
    public void Rejects_project_paths_outside_workspace()
    {
        _t.Write("src/App/App.csproj", "<Project/>");
        Assert.Throws<BuildValidationException>(() => DotNetBuildPlanner.ResolveProject(_t.Path, "../other/App.csproj"));
        Assert.Throws<BuildValidationException>(() => DotNetBuildPlanner.ResolveProject(_t.Path, "/etc/App.csproj"));
        Assert.Throws<BuildValidationException>(() => DotNetBuildPlanner.ResolveProject(_t.Path, "src/App/Program.cs"));
        Assert.Throws<BuildValidationException>(() => DotNetBuildPlanner.ResolveProject(_t.Path, "src/Missing.csproj"));
    }

    [Fact]
    public void Rejects_relative_output_and_android()
    {
        Assert.Throws<BuildValidationException>(() => DotNetBuildPlanner.Validate(Settings("relative/out")));
        Assert.Throws<BuildValidationException>(() => DotNetBuildPlanner.Validate(Settings(_t.Path, TargetPlatform.Android)));
    }
}

public class MsBuildOutputParserTests
{
    [Fact]
    public void Parses_and_deduplicates_warnings_and_errors()
    {
        var p = new MsBuildOutputParser();
        var w = p.Parse("/src/App.cs(12,5): warning CS0168: The variable 'x' is declared but never used [/src/App.csproj]");
        Assert.NotNull(w);
        Assert.Equal(DiagnosticSeverity.Warning, w!.Severity);
        Assert.Equal("CS0168", w.Code);
        Assert.Equal("/src/App.cs", w.File);
        Assert.Equal(12, w.Line);
        Assert.Equal("The variable 'x' is declared but never used", w.Text);
        Assert.Null(p.Parse("/src/App.cs(12,5): warning CS0168: The variable 'x' is declared but never used [/src/App.csproj]"));

        var e = p.Parse("/usr/lib/dotnet/sdk/10.0.114/Sdks/Microsoft.NET.Sdk/targets/Microsoft.NET.TargetFrameworkInference.targets(166,5): error NETSDK1045: The current .NET SDK does not support targeting .NET 11.0. [/src/App.csproj]");
        Assert.Equal(DiagnosticSeverity.Error, e!.Severity);
        Assert.Equal("NETSDK1045", e.Code);

        var m = p.Parse("MSBUILD : error MSB1009: Project file does not exist.");
        Assert.Equal("MSB1009", m!.Code);
        var win = p.Parse(@"C:\src\App\Program.cs(7,13): error CS0103: The name 'x' does not exist in the current context [C:\src\App\App.csproj]");
        Assert.Equal(DiagnosticSeverity.Error, win!.Severity);
        Assert.Equal("CS0103", win.Code);
        Assert.Equal(@"C:\src\App\Program.cs", win.File);
        Assert.Equal(7, win.Line);
        Assert.Null(p.Parse("  Restored /src/App.csproj (in 1.2 sec)."));
        Assert.Null(p.Parse("Build succeeded."));
    }
}

public class SecurityTests
{
    [Fact]
    public void Scrubs_secrets_from_build_environment()
    {
        var source = new System.Collections.Hashtable
        {
            ["PATH"] = "/usr/bin", ["HOME"] = "/home/u", ["GITHUB_TOKEN"] = "x", ["AWS_SECRET_ACCESS_KEY"] = "x", ["MY_API_KEY"] = "x",
            ["HCG_SIGNING_PASSWORD"] = "x", ["NUGET_API_KEY"] = "x", ["APPLE_ID"] = "x", ["SIGNING_KEY"] = "x", ["AZURE_CLIENT_ID"] = "x",
            ["KEYBOARD_LAYOUT"] = "us", ["NUGET_PACKAGES"] = "/cache", ["LANG"] = "en_US.UTF-8", ["DB_PASSWORD"] = "x", ["SSH_AUTH_SOCK"] = "x",
            ["AZDO_PAT"] = "x", ["PATCH_LEVEL"] = "3",
        };
        var r = BuildEnvironmentPolicy.CreateUntrustedBuildEnvironment(source);
        foreach (var removed in new[] { "GITHUB_TOKEN", "AWS_SECRET_ACCESS_KEY", "MY_API_KEY", "HCG_SIGNING_PASSWORD", "NUGET_API_KEY", "APPLE_ID", "SIGNING_KEY", "AZURE_CLIENT_ID", "DB_PASSWORD", "SSH_AUTH_SOCK", "AZDO_PAT" })
        {
            Assert.DoesNotContain(removed, r.Environment.Keys);
            Assert.Contains(removed, r.RemovedNames);
        }
        foreach (var kept in new[] { "PATH", "HOME", "KEYBOARD_LAYOUT", "NUGET_PACKAGES", "LANG", "PATCH_LEVEL" })
            Assert.Contains(kept, r.Environment.Keys);
        Assert.Equal("1", r.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"]);
        Assert.Equal("1", r.Environment["MSBUILDDISABLENODEREUSE"]);
    }

    [Fact]
    public void Signing_capabilities_are_never_reported_as_implemented()
    {
        var caps = SigningCapabilityCatalog.Evaluate(Fixtures.Toolchains("10.0.114"));
        Assert.NotEmpty(caps);
        Assert.All(caps, c => Assert.Equal(SigningImplementationStatus.NotImplemented, c.Implementation));
        Assert.All(caps, c => Assert.StartsWith("Not implemented", c.StatusText));
    }

    [Fact]
    public async Task Local_worker_reports_no_isolation_and_requires_explicit_environment()
    {
        var w = new LocalProcessBuildWorker(new Infrastructure.Processes.ProcessRunner());
        Assert.Equal(IsolationLevel.None, w.Isolation);
        Assert.Contains("not sandboxed", w.Description);
        await Assert.ThrowsAsync<InvalidOperationException>(() => w.RunAsync(new ProcessSpec { Executable = "/bin/true" }, (_, _) => { }, CancellationToken.None));
    }
}

/// <summary>Build orchestration with a scripted worker: result handling, cancellation, timeouts, verification.</summary>
public class BuildServiceTests : IDisposable
{
    private readonly TempDir _t = new();
    private readonly AppPaths _paths;
    private readonly SqliteDatabase _db;
    private readonly SqliteBuildRepository _builds;
    private readonly ProjectRecord _project;

    public BuildServiceTests()
    {
        _paths = new AppPaths(_t.Combine("app"));
        _paths.EnsureCreated();
        _db = new SqliteDatabase(_paths);
        _builds = new SqliteBuildRepository(_db);
        var ws = Path.Combine(_paths.WorkspacesDirectory, "p1");
        Directory.CreateDirectory(Path.Combine(ws, "source", "src", "App"));
        File.WriteAllText(Path.Combine(ws, "source", "src", "App", "App.csproj"), "<Project/>");
        _project = new ProjectRecord { Id = Guid.NewGuid(), Name = "App", SourceKind = ProjectSourceKind.Directory, SourceLocation = "/orig", WorkspacePath = ws, ImportedAt = DateTimeOffset.UtcNow };
        new SqliteProjectRepository(_db).UpsertAsync(_project).GetAwaiter().GetResult();
    }

    public void Dispose() => _t.Dispose();

    private sealed class ScriptedWorker(Func<ProcessSpec, Action<OutputStream, string>, CancellationToken, Task<ProcessResult>> run) : IBuildWorker
    {
        public List<ProcessSpec> Calls { get; } = [];
        public IsolationLevel Isolation => IsolationLevel.None;
        public string Description => "test";
        public BuildEnvironmentPolicy.ScrubResult CreateEnvironment() => new(new Dictionary<string, string> { ["PATH"] = "/usr/bin" }, ["GITHUB_TOKEN"]);
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
        public List<BuildStage> Stages { get; } = [];
        public List<BuildLogLine> Lines { get; } = [];
        public void OnStage(BuildStage stage, string description) { lock (Stages) Stages.Add(stage); }
        public void OnLog(BuildLogLine line) { lock (Lines) Lines.Add(line); }
    }

    /// <summary>Minimal Mach-O arm64 header with an LC_CODE_SIGNATURE load command.</summary>
    internal static byte[] FakeMachOArm64()
    {
        var b = new byte[64];
        BitConverter.GetBytes(0xFEEDFACFu).CopyTo(b, 0);
        BitConverter.GetBytes(0x0100000Cu).CopyTo(b, 4);
        BitConverter.GetBytes(1u).CopyTo(b, 16);   // ncmds
        BitConverter.GetBytes(0x1Du).CopyTo(b, 32); // LC_CODE_SIGNATURE
        BitConverter.GetBytes(16u).CopyTo(b, 36);
        return b;
    }

    private static Task<ProcessResult> Ok(ProcessSpec spec, Action<OutputStream, string> output, CancellationToken ct, bool writeOutput = true)
    {
        if (spec.Arguments[0] == "publish" && writeOutput)
        {
            var dir = spec.Arguments[spec.Arguments.ToList().IndexOf("-o") + 1];
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "App"), FakeMachOArm64());
            File.WriteAllText(Path.Combine(dir, "App.dll"), "il");
            File.WriteAllText(Path.Combine(dir, "App.runtimeconfig.json"), "{}");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(dir, "App"), (UnixFileMode)0b111_101_101);
        }
        output(OutputStream.StdOut, "/src/App.cs(3,1): warning CS0168: unused [/src/App.csproj]");
        return Task.FromResult(new ProcessResult(0, TimeSpan.FromMilliseconds(5), false, false));
    }

    private BuildService Service(IBuildWorker worker) => new(worker, new FixedToolchains(Fixtures.Toolchains("10.0.114")), _builds,
        new PackagingRegistry([new PortableFolderPackager(), new PortableZipPackager(), new MacAppBundlePackager()]), _paths, TimeProvider.System);

    private BuildRequest Request(BuildSettings? s = null, bool trust = true) => new()
    {
        Project = _project,
        Settings = s ?? BuildPlannerTests.Settings(_t.Combine("out"), formats: [PackagingFormat.PortableFolder, PackagingFormat.PortableZip, PackagingFormat.MacAppBundle]),
        TrustAcknowledged = trust,
        AssemblyName = "App",
    };

    [Fact]
    public async Task Refuses_build_without_trust_acknowledgement()
    {
        var worker = new ScriptedWorker((s, o, c) => Ok(s, o, c));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(worker).RunAsync(Request(trust: false), new Observer(), CancellationToken.None));
        Assert.Empty(worker.Calls);
    }

    [Fact]
    public async Task Successful_build_produces_verified_artifacts_with_checksums()
    {
        var worker = new ScriptedWorker((s, o, c) => Ok(s, o, c));
        var observer = new Observer();
        var build = await Service(worker).RunAsync(Request(), observer, CancellationToken.None);

        Assert.True(build.Status == BuildStatus.Succeeded, build.FailureReason);
        Assert.Equal(3, worker.Calls.Count);
        Assert.All(worker.Calls, c => Assert.NotNull(c.Environment));
        Assert.Equal(1, build.WarningCount);
        Assert.Contains(build.Artifacts, a => a.Format == PackagingFormat.PortableZip && a.Kind == ArtifactKind.File);
        Assert.Contains(build.Artifacts, a => a.Format == PackagingFormat.MacAppBundle && a.Path.EndsWith("My App.app"));
        Assert.All(build.Artifacts, a => Assert.Matches("^[0-9a-f]{64}$", a.Sha256));
        Assert.All(build.Artifacts, a => Assert.NotEqual(VerificationStatus.Failed, a.Verification));
        var zip = build.Artifacts.First(a => a.Format == PackagingFormat.PortableZip);
        Assert.Equal(await Checksums.Sha256FileAsync(zip.Path), zip.Sha256);
        var outRoot = Path.GetDirectoryName(zip.Path)!;
        Assert.Contains(zip.Sha256, File.ReadAllText(Path.Combine(outRoot, "SHA256SUMS.txt")));
        Assert.True(File.Exists(Path.Combine(outRoot, BuildService.OutputMarkerFile)));
        Assert.True(File.Exists(build.LogPath));
        Assert.Contains("Removed 1 sensitive environment variable", File.ReadAllText(build.LogPath!));
        Assert.Contains(BuildStage.Packaging, observer.Stages);

        var stored = await _builds.GetAsync(build.Id);
        Assert.Equal(BuildStatus.Succeeded, stored!.Status);
        Assert.Equal(build.Artifacts.Count, stored.Artifacts.Count);
        Assert.Equal("10.0.114", stored.ToolchainVersions["dotnet-sdk"]);
        Assert.NotNull(stored.EndedAt);
    }

    [Fact]
    public async Task Nonzero_exit_code_fails_build_and_stops()
    {
        var worker = new ScriptedWorker((s, o, c) =>
        {
            if (s.Arguments[0] == "build")
            {
                o(OutputStream.StdOut, "/src/App.cs(1,1): error CS1002: ; expected [/src/App.csproj]");
                return Task.FromResult(new ProcessResult(1, TimeSpan.Zero, false, false));
            }
            return Ok(s, o, c);
        });
        var build = await Service(worker).RunAsync(Request(), new Observer(), CancellationToken.None);
        Assert.Equal(BuildStatus.Failed, build.Status);
        Assert.Equal(1, build.ExitCode);
        Assert.Equal(1, build.ErrorCount);
        Assert.Contains("exit code 1", build.FailureReason);
        Assert.Equal(2, worker.Calls.Count); // publish never ran
        Assert.Empty(build.Artifacts);
    }

    private const string LockLine =
        "/x/AvaloniaBuildTasks.targets(176,5): error MSB4018: System.IO.IOException: The process cannot access the file '/x/obj/App.dll' because it is being used by another process. [/x/App.csproj]";

    [Fact]
    public async Task Transient_file_lock_is_retried_once_and_failed_attempt_diagnostics_are_discarded()
    {
        BuildService.TransientRetryDelay = TimeSpan.Zero;
        var buildAttempts = 0;
        var worker = new ScriptedWorker((s, o, c) =>
        {
            if (s.Arguments[0] == "build" && ++buildAttempts == 1)
            {
                o(OutputStream.StdOut, LockLine);
                return Task.FromResult(new ProcessResult(1, TimeSpan.Zero, false, false));
            }
            return Ok(s, o, c);
        });
        var build = await Service(worker).RunAsync(Request(), new Observer(), CancellationToken.None);

        Assert.True(build.Status == BuildStatus.Succeeded, build.FailureReason);
        Assert.Equal(2, buildAttempts);
        Assert.Equal(4, worker.Calls.Count);
        Assert.Equal(0, build.ErrorCount);
        Assert.DoesNotContain(build.Messages, m => m.Severity == DiagnosticSeverity.Error);
        Assert.Contains("Retrying once", File.ReadAllText(build.LogPath!));
    }

    [Fact]
    public async Task Persistent_file_lock_fails_after_one_retry()
    {
        BuildService.TransientRetryDelay = TimeSpan.Zero;
        var worker = new ScriptedWorker((s, o, c) =>
        {
            if (s.Arguments[0] != "build") return Ok(s, o, c);
            o(OutputStream.StdOut, LockLine);
            return Task.FromResult(new ProcessResult(1, TimeSpan.Zero, false, false));
        });
        var build = await Service(worker).RunAsync(Request(), new Observer(), CancellationToken.None);
        Assert.Equal(BuildStatus.Failed, build.Status);
        Assert.Equal(3, worker.Calls.Count); // restore + 2 build attempts
        Assert.Equal(1, build.ErrorCount);
    }

    [Theory]
    [InlineData(LockLine, true)]
    [InlineData("/src/App.cs(1,1): error CS1002: ; expected [/src/App.csproj]", false)]
    [InlineData("warning: the file is being used by another process", false)]
    public void Recognizes_only_transient_file_lock_errors(string line, bool expected) =>
        Assert.Equal(expected, BuildService.IsTransientFileLock(line));

    [Fact]
    public async Task Exit_code_zero_without_expected_output_is_a_failure()
    {
        var worker = new ScriptedWorker((s, o, c) => Ok(s, o, c, writeOutput: false));
        var build = await Service(worker).RunAsync(Request(), new Observer(), CancellationToken.None);
        Assert.Equal(BuildStatus.Failed, build.Status);
        Assert.Contains("no macOS executable was found", build.FailureReason);
    }

    [Fact]
    public async Task Wrong_architecture_output_fails_verification()
    {
        var worker = new ScriptedWorker((s, o, c) => Ok(s, o, c));
        var settings = BuildPlannerTests.Settings(_t.Combine("out"), arch: TargetArchitecture.X64, formats: [PackagingFormat.PortableFolder]);
        var build = await Service(worker).RunAsync(Request(settings), new Observer(), CancellationToken.None);
        Assert.Equal(BuildStatus.Failed, build.Status);
        Assert.Contains("verification", build.FailureReason);
        Assert.Contains(build.Artifacts.Single().Checks, c => c.Status == VerificationStatus.Failed && c.Detail.Contains("ARM64"));
    }

    [Fact]
    public async Task Cancellation_marks_build_cancelled()
    {
        using var cts = new CancellationTokenSource();
        var worker = new ScriptedWorker(async (s, o, ct) =>
        {
            if (s.Arguments[0] == "build")
            {
                cts.CancelAfter(50);
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { return new ProcessResult(-1, TimeSpan.Zero, Cancelled: true, TimedOut: false); }
            }
            return await Ok(s, o, ct);
        });
        var build = await Service(worker).RunAsync(Request(), new Observer(), cts.Token);
        Assert.Equal(BuildStatus.Cancelled, build.Status);
        Assert.Equal(BuildStatus.Cancelled, (await _builds.GetAsync(build.Id))!.Status);
        Assert.Equal(2, worker.Calls.Count);
    }

    private sealed class BlockingPackager : IPackager
    {
        public TaskCompletionSource Started { get; } = new();
        public PackagingFormat Format => PackagingFormat.PortableFolder;
        public async Task<IReadOnlyList<PackagedOutput>> PackageAsync(PackagingRequest request, IPackagingLog log, CancellationToken ct)
        {
            File.WriteAllText(Path.Combine(request.OutputRoot, "partial.bin"), "half-written");
            Started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return [];
        }
    }

    [Fact]
    public async Task Cancelling_during_packaging_removes_incomplete_output()
    {
        var packager = new BlockingPackager();
        var svc = new BuildService(new ScriptedWorker((s, o, c) => Ok(s, o, c)), new FixedToolchains(Fixtures.Toolchains("10.0.114")), _builds,
            new PackagingRegistry([packager]), _paths, TimeProvider.System);
        using var cts = new CancellationTokenSource();
        var settings = BuildPlannerTests.Settings(_t.Combine("out"));
        var run = svc.RunAsync(Request(settings), new Observer(), cts.Token);
        await packager.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(File.Exists(Path.Combine(BuildService.OutputRootFor(settings), BuildService.OutputMarkerFile)));
        cts.Cancel();
        var build = await run;

        Assert.Equal(BuildStatus.Cancelled, build.Status);
        Assert.False(Directory.Exists(BuildService.OutputRootFor(settings)));
        // A subsequent build to the same location is not blocked by leftovers.
        var again = await Service(new ScriptedWorker((s, o, c) => Ok(s, o, c))).RunAsync(Request(settings), new Observer(), CancellationToken.None);
        Assert.Equal(BuildStatus.Succeeded, again.Status);
    }

    [Fact]
    public async Task Timeout_marks_build_timed_out()
    {
        var worker = new ScriptedWorker(async (s, o, ct) =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { }
            return new ProcessResult(-1, TimeSpan.Zero, Cancelled: true, TimedOut: false);
        });
        var settings = BuildPlannerTests.Settings(_t.Combine("out")) with { Timeout = TimeSpan.FromMilliseconds(200) };
        var build = await Service(worker).RunAsync(Request(settings), new Observer(), CancellationToken.None);
        Assert.Equal(BuildStatus.TimedOut, build.Status);
    }

    [Fact]
    public async Task Missing_sdk_fails_without_running_anything()
    {
        var worker = new ScriptedWorker((s, o, c) => Ok(s, o, c));
        var svc = new BuildService(worker, new FixedToolchains(Fixtures.Toolchains()), _builds, new PackagingRegistry([]), _paths, TimeProvider.System);
        var build = await svc.RunAsync(Request(), new Observer(), CancellationToken.None);
        Assert.Equal(BuildStatus.Failed, build.Status);
        Assert.Contains(".NET SDK was not found", build.FailureReason);
        Assert.Empty(worker.Calls);
    }

    [Fact]
    public async Task Existing_output_requires_confirmation_and_foreign_folders_are_never_deleted()
    {
        var worker = new ScriptedWorker((s, o, c) => Ok(s, o, c));
        var first = await Service(worker).RunAsync(Request(), new Observer(), CancellationToken.None);
        Assert.Equal(BuildStatus.Succeeded, first.Status);

        var second = await Service(worker).RunAsync(Request(), new Observer(), CancellationToken.None);
        Assert.Equal(BuildStatus.Failed, second.Status);
        Assert.Contains("already exists", second.FailureReason);

        var third = await Service(worker).RunAsync(Request() with { AllowOverwriteOutput = true }, new Observer(), CancellationToken.None);
        Assert.Equal(BuildStatus.Succeeded, third.Status);

        var foreign = Path.Combine(_t.Combine("out"), "My App-1.2.3-osx-arm64");
        File.Delete(Path.Combine(foreign, BuildService.OutputMarkerFile));
        var fourth = await Service(worker).RunAsync(Request() with { AllowOverwriteOutput = true }, new Observer(), CancellationToken.None);
        Assert.Equal(BuildStatus.Failed, fourth.Status);
        Assert.Contains("not created by HCG", fourth.FailureReason);
        Assert.True(Directory.Exists(foreign));
    }

    [Fact]
    public void Locates_executable_by_assembly_name_or_runtimeconfig()
    {
        var dir = _t.Combine("pub");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Real.runtimeconfig.json"), "{}");
        File.WriteAllText(Path.Combine(dir, "Real.exe"), "MZ");
        Assert.Equal("Real.exe", BuildService.LocateExecutable(dir, "Wrong", TargetPlatform.Windows));
        Assert.Null(BuildService.LocateExecutable(dir, "Real", TargetPlatform.Linux));
        Assert.Null(BuildService.LocateExecutable(_t.Combine("missing"), "Real", TargetPlatform.Windows));
    }
}
