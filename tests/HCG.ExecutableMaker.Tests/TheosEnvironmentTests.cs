using HCG.ExecutableMaker.BuildEngine;
using HCG.ExecutableMaker.BuildEngine.Native;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Infrastructure.Processes;

namespace HCG.ExecutableMaker.Tests;

/// <summary>
/// HCG Nexus PreviewHarness (user log, Theos 8167f59de02f): Maker detected ldid at /opt/homebrew/bin/ldid, but gmake's
/// recipe shell — started with the PATH of a Finder-launched app — failed "ldid -S …" with "/bin/bash: ldid: command not
/// found" (Error 127). These tests use the real make, bash and process runner with a minimal PATH and an ldid stand-in
/// in a separate directory.
/// </summary>
public sealed class TheosEnvironmentTests : IDisposable
{
    private const string FinderPath = "/usr/bin:/bin:/usr/sbin:/sbin";
    private readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    private static readonly HostEnvironment Linux = new(TargetPlatform.Linux, TargetArchitecture.X64, "Linux", ".NET", "test");

    /// <summary>An ldid stand-in in its own directory (like /opt/homebrew/bin): records each call, like ldid -S signing in place.</summary>
    private (string Ldid, string Calls) FakeLdid()
    {
        var dir = _t.Combine("homebrew", "bin");
        Directory.CreateDirectory(dir);
        var calls = _t.Combine("ldid-calls.txt");
        var ldid = Path.Combine(dir, "ldid");
        File.WriteAllText(ldid, $"#!/bin/sh\necho \"ldid $*\" >> '{calls}'\nexit 0\n");
        File.SetUnixFileMode(ldid, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return (ldid, calls);
    }

    private ToolchainReport Report(string ldid, string make = "/usr/bin/make", string perl = "/usr/bin/perl")
    {
        var theos = _t.Combine("theos");
        Directory.CreateDirectory(theos);
        ToolchainInfo Row(string id, string path) => new() { Id = id, DisplayName = id, Category = "Theos", Status = ToolchainStatus.Installed, ExecutablePath = path };
        return new ToolchainReport(Linux, [Row(ToolchainIds.Theos, theos), Row(ToolchainIds.TheosMake, make), Row(ToolchainIds.Ldid, ldid), Row(ToolchainIds.Perl, perl)], null, null, DateTimeOffset.UtcNow);
    }

    private static Dictionary<string, string> Sanitized() => new() { ["PATH"] = FinderPath, ["HOME"] = Path.GetTempPath(), ["LANG"] = "C" };

    private static async Task<(ProcessResult Result, List<string> Output)> RunAsync(ProcessSpec spec)
    {
        var lines = new List<string>();
        var r = await new LocalProcessBuildWorker(new ProcessRunner()).RunAsync(spec, (_, l) => { lock (lines) lines.Add(l); }, CancellationToken.None);
        return (r, lines);
    }

    /// <summary>A Theos-like project: the top-level make recurses (gmake[1], gmake[2]) and the innermost recipe signs with a bare "ldid -S".</summary>
    private string NestedProject()
    {
        var root = _t.Combine("project");
        _t.Write("project/Makefile", "all:\n\t$(MAKE) -C sub sign\n");
        _t.Write("project/sub/Makefile", "SHELL := /bin/bash\nsign:\n\t$(MAKE) inner\ninner:\n\tset -o pipefail; (CODESIGN_ALLOCATE=/usr/bin/true ldid -S \"binary.unsigned\")\n\t$(TARGET_CODESIGN) -S explicit.unsigned\n");
        return root;
    }

    [SkippableUnixFact]
    public async Task With_finder_PATH_the_old_environment_reproduces_command_not_found_and_the_failure_names_it_first()
    {
        var (ldid, _) = FakeLdid();
        var project = NestedProject();
        // What Maker passed before: the sanitized parent environment (launchd's PATH) plus THEOS.
        var (result, output) = await RunAsync(new ProcessSpec { Executable = "/usr/bin/make", Arguments = ["-C", project], WorkingDirectory = project, Environment = Sanitized() });
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(output, l => l.Contains("ldid: command not found", StringComparison.Ordinal));

        var parser = new NativeOutputParser();
        var messages = output.Select(parser.Parse).OfType<BuildMessage>().ToList();
        var summary = NativeFailureSummary.Describe("Theos: make package (rootless)", result.ExitCode, messages);
        Assert.Equal("ldid: command not found", summary.Primary!.Text);
        Assert.Equal(NativeOutputParser.CommandNotFoundCode, summary.Primary.Code);
        Assert.True(summary.Consequences.Count >= 2, string.Join(" | ", summary.Consequences.Select(c => c.Text)));
        Assert.All(summary.Consequences, c => Assert.Equal(NativeOutputParser.MakeCode, c.Code));
        Assert.StartsWith("Theos: make package (rootless) failed: ldid: command not found", summary.Reason);
        Assert.Contains("consequences of the first", summary.Reason);
        Assert.True(File.Exists(ldid));
    }

    [SkippableUnixFact]
    public async Task The_resolved_environment_lets_a_nested_recipe_run_the_detected_ldid_by_name_and_by_TARGET_CODESIGN()
    {
        var (ldid, calls) = FakeLdid();
        var env = TheosBuildEnvironment.Resolve(Report(ldid), Sanitized())!;
        Assert.StartsWith(FinderPath, env.Path);                          // the parent PATH is kept first …
        Assert.EndsWith(":" + Path.GetDirectoryName(ldid), env.Path);     // … and ldid's directory is appended
        Assert.Equal(ldid, env.Environment["TARGET_CODESIGN"]);
        Assert.Equal(env.Theos, env.Environment["THEOS"]);
        Assert.Equal(Sanitized()["HOME"], env.Environment["HOME"]);       // nothing else changes

        Assert.Empty(await env.VerifyInChildShellAsync(new LocalProcessBuildWorker(new ProcessRunner()), CancellationToken.None));

        var project = NestedProject();
        var (result, output) = await RunAsync(new ProcessSpec { Executable = env.Make, Arguments = ["-C", project], WorkingDirectory = project, Environment = env.Environment });
        Assert.True(result.ExitCode == 0, string.Join("\n", output));
        var signed = File.ReadAllLines(calls);
        Assert.Equal(["ldid -S binary.unsigned", "ldid -S explicit.unsigned"], signed);
    }

    [SkippableUnixFact]
    public async Task The_preflight_reports_a_tool_the_child_shell_cannot_find_before_anything_is_built()
    {
        var (ldid, _) = FakeLdid();
        var env = TheosBuildEnvironment.Resolve(Report(ldid), Sanitized())!;
        // The environment without ldid's directory — what the build got before this fix.
        var broken = env with { Environment = new Dictionary<string, string>(env.Environment) { ["PATH"] = FinderPath } };
        var problems = await broken.VerifyInChildShellAsync(new LocalProcessBuildWorker(new ProcessRunner()), CancellationToken.None);
        var problem = Assert.Single(problems);
        Assert.Contains($"ldid was detected at {ldid}, but the build's shell cannot find it on PATH ({FinderPath})", problem);
    }

    [SkippableUnixFact]
    public async Task The_deb_compressor_dm_pl_runs_by_name_is_found_in_a_package_manager_directory_and_checked()
    {
        var (ldid, _) = FakeLdid();
        var brew = _t.Combine("other-prefix", "bin");
        Directory.CreateDirectory(brew);
        var lzma = Path.Combine(brew, "lzma");
        File.WriteAllText(lzma, "#!/bin/sh\ncat\n");
        File.SetUnixFileMode(lzma, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        // An lzma the system already provides wins; only when none is reachable is the package manager's copy added.
        var systemLzma = TheosBuildEnvironment.Which("lzma", "/usr/bin:/bin");
        var env = TheosBuildEnvironment.Resolve(Report(ldid), Sanitized(), [brew])!;
        Assert.Equal(systemLzma ?? lzma, env.Lzma);
        Assert.Contains(env.RecipeCommands, c => c.Command == "lzma");
        Assert.Empty(env.Warnings);
        Assert.Empty(await env.VerifyInChildShellAsync(new LocalProcessBuildWorker(new ProcessRunner()), CancellationToken.None));
        if (systemLzma is null) Assert.EndsWith(":" + brew, env.Path);

        var none = TheosBuildEnvironment.Resolve(Report(ldid), new Dictionary<string, string> { ["PATH"] = _t.Combine("empty") }, [_t.Combine("nowhere")])!;
        if (TheosBuildEnvironment.Which("lzma", "/usr/bin:/bin:/usr/sbin:/sbin") is null)
            Assert.Contains("lzma: command not found", Assert.Single(none.Warnings));
    }

    [Fact]
    public void Path_keeps_the_parent_order_adds_system_directories_and_only_unreachable_tool_directories()
    {
        var (ldid, _) = FakeLdid();
        var path = TheosBuildEnvironment.BuildPath("/custom/bin:.:/usr/bin:", [ldid, "/usr/bin/perl", "/usr/bin/make"]);
        Assert.Equal($"/custom/bin:/usr/bin:/bin:/usr/sbin:/sbin:{Path.GetDirectoryName(ldid)}", path);   // "." and "" never added
        Assert.Equal("/usr/bin:/bin:/usr/sbin:/sbin", TheosBuildEnvironment.BuildPath(null, ["/usr/bin/perl"]));
        Assert.Null(TheosBuildEnvironment.Resolve(new ToolchainReport(Linux, [], null, null, DateTimeOffset.UtcNow), Sanitized()));
    }

    [Fact]
    public void The_build_step_runs_the_detected_make_with_the_resolved_environment()
    {
        var (ldid, _) = FakeLdid();
        var env = TheosBuildEnvironment.Resolve(Report(ldid, make: "/usr/bin/make"), Sanitized())!;
        _t.Write("p/Makefile", "include $(THEOS)/makefiles/common.mk\n");
        var prepared = new TheosBuildPlanner.Prepared(_t.Path, _t.Combine("p"), _t.Combine("packages"), false);
        var settings = new BuildSettings
        {
            ProjectRelativePath = "", Platform = TargetPlatform.IOS, Architecture = TargetArchitecture.Arm64, Configuration = BuildConfigurationKind.Release,
            OutputDirectory = _t.Path, Identity = new AppIdentity { ApplicationName = "Nexus", Version = "1.0", ApplicationId = "com.example" },
            Native = new NativeBuildOptions { Kind = NativeProjectKind.Theos, ProjectDirectory = "", Target = "package", TheosScheme = new TheosSchemeChoice { PackageScheme = "rootless", SelectorVariable = "HCG_ENV", SelectorValue = "rootless" } },
        };
        var step = TheosBuildPlanner.Plan(settings, prepared, env);
        Assert.Equal("/usr/bin/make", step.Process.Executable);
        Assert.Equal(env.Path, step.Process.Environment!["PATH"]);
        Assert.Equal(ldid, step.Process.Environment["TARGET_CODESIGN"]);
        Assert.DoesNotContain(step.Process.Arguments, a => a.StartsWith("TARGET_CODESIGN", StringComparison.Ordinal)); // environment, not command line: a project's own assignment still wins
    }

    [Fact]
    public void The_users_log_is_classified_cause_first_and_linker_warnings_stay_warnings()
    {
        // Output lines from the supplied PreviewHarness log (paths shortened).
        string[] log =
        [
            "ld: warning: -multiply_defined is obsolete",
            "ld: warning: -multiply_defined is obsolete",
            "/bin/bash: ldid: command not found",
            "gmake[2]: *** [/theos/makefiles/instance/application.mk:49: /tmp/b/PreviewHarness/.theos/obj/HCGNexusPreview.app/HCGNexusPreview] Error 127",
            "gmake[1]: *** [/theos/makefiles/instance/application.mk:41: internal-application-all_] Error 2",
            "gmake: *** [/theos/makefiles/master/rules.mk:146: HCGNexusPreview.all.application.variables] Error 2",
        ];
        var parser = new NativeOutputParser();
        var messages = log.Select(parser.Parse).OfType<BuildMessage>().ToList();
        var warning = Assert.Single(messages, m => m.Severity == DiagnosticSeverity.Warning);   // de-duplicated
        Assert.Equal("ld: warning: -multiply_defined is obsolete", warning.Text);
        var summary = NativeFailureSummary.Describe("Theos: make package (HCG_ENV=rootless (rootless))", 2, messages);
        Assert.Equal("ldid: command not found", summary.Primary!.Text);
        Assert.Equal(3, summary.Consequences.Count);
        Assert.Contains("a build recipe started a program that its shell could not find on PATH (exit 127)", summary.Reason);

        // Genuine linker failures are still errors.
        var p2 = new NativeOutputParser();
        Assert.Equal(DiagnosticSeverity.Error, p2.Parse("ld: error: undefined symbol: _HCGNexusMain")!.Severity);
        Assert.Equal(DiagnosticSeverity.Error, p2.Parse("clang: error: linker command failed with exit code 1 (use -v to see invocation)")!.Severity);
        Assert.Equal(DiagnosticSeverity.Error, p2.Parse("ld: symbol(s) not found for architecture arm64e")!.Severity);
        Assert.Equal(DiagnosticSeverity.Error, p2.Parse("Undefined symbols for architecture arm64:")!.Severity);
        Assert.Equal(DiagnosticSeverity.Warning, p2.Parse("clang: warning: argument unused during compilation: '-fobjc-arc'")!.Severity);
        Assert.Equal(DiagnosticSeverity.Error, p2.Parse("gmake[2]: ldid: No such file or directory")!.Severity);
        Assert.Equal("ldid: command not found", new NativeOutputParser().Parse("/bin/sh: line 1: ldid: command not found")!.Text);
    }

    [Fact]
    public void Without_a_recognised_cause_the_first_make_error_is_reported_and_127_is_explained()
    {
        var parser = new NativeOutputParser();
        var messages = new[] { "gmake[2]: *** [rules.mk:49: out/App] Error 127", "gmake: *** [rules.mk:146: all] Error 2" }.Select(parser.Parse).OfType<BuildMessage>().ToList();
        var summary = NativeFailureSummary.Describe("Theos", 2, messages);
        Assert.EndsWith("Error 127", summary.Primary!.Text);
        Assert.Contains("a recipe command was not found (exit 127)", summary.Reason);
        Assert.Single(summary.Consequences);
        Assert.Equal("Theos failed with exit code 2.", NativeFailureSummary.Describe("Theos", 2, []).Reason);
    }
}

/// <summary>Runs where /bin/bash, /usr/bin/make and /usr/bin/perl exist (macOS, Linux CI); skipped elsewhere.</summary>
public sealed class SkippableUnixFactAttribute : FactAttribute
{
    public SkippableUnixFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "Needs a Unix shell and make.";
        else if (!File.Exists("/bin/bash") || !File.Exists("/usr/bin/make") || !File.Exists("/usr/bin/perl")) Skip = "Needs /bin/bash, /usr/bin/make and /usr/bin/perl.";
    }
}
