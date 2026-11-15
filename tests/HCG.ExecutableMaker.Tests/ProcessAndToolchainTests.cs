using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Infrastructure.Processes;
using HCG.ExecutableMaker.Infrastructure.Toolchains;

namespace HCG.ExecutableMaker.Tests;

public class ProcessRunnerTests
{
    private readonly ProcessRunner _runner = new();

    /// <summary>Standard Unix tools live in /bin on Linux but some only in /usr/bin on macOS.</summary>
    private static string Tool(string name) =>
        new[] { "/bin/" + name, "/usr/bin/" + name }.FirstOrDefault(File.Exists) ?? throw new FileNotFoundException(name);

    [UnixFact]
    public async Task Passes_arguments_verbatim_without_a_shell()
    {
        var lines = new List<string>();
        var r = await _runner.RunAsync(new ProcessSpec { Executable = Tool("echo"), Arguments = ["a b; $(whoami) `id` && rm -rf /tmp/nothing"] },
            (_, l) => lines.Add(l), CancellationToken.None);
        Assert.True(r.Succeeded);
        Assert.Equal(["a b; $(whoami) `id` && rm -rf /tmp/nothing"], lines);
    }

    [UnixFact]
    public async Task Reports_exit_code() =>
        Assert.Equal(1, (await _runner.RunAsync(new ProcessSpec { Executable = Tool("false") }, null, CancellationToken.None)).ExitCode);

    [UnixFact]
    public async Task Cancellation_kills_process_promptly()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await _runner.RunAsync(new ProcessSpec { Executable = Tool("sleep"), Arguments = ["30"] }, null, cts.Token);
        Assert.True(r.Cancelled);
        Assert.False(r.TimedOut);
        Assert.False(r.Succeeded);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
    }

    [UnixFact]
    public async Task Timeout_kills_process()
    {
        var r = await _runner.RunAsync(new ProcessSpec { Executable = Tool("sleep"), Arguments = ["30"], Timeout = TimeSpan.FromMilliseconds(300) }, null, CancellationToken.None);
        Assert.True(r.TimedOut);
        Assert.False(r.Cancelled);
    }

    [UnixFact]
    public async Task Uses_only_the_given_environment()
    {
        var lines = new List<string>();
        await _runner.RunAsync(new ProcessSpec { Executable = Tool("env"), Environment = new Dictionary<string, string> { ["ONLY_ME"] = "1" } },
            (_, l) => lines.Add(l), CancellationToken.None);
        Assert.Equal(["ONLY_ME=1"], lines);
    }

    /// <summary>
    /// CI (port-ios, runs 90 and 92): the HelloMac port's compile stage went silent and the job died ~45 min after compile
    /// started — the Simulator build's 45-minute timeout. Process.WaitForExitAsync waits for the output PIPES to close, so a
    /// command that finished but left a background process holding them (xcodebuild's build service) was held until its
    /// timeout and reported as timed out. Reproduced here with a real shell whose background child keeps stdout open.
    /// </summary>
    [UnixFact]
    public async Task A_finished_command_whose_background_child_keeps_the_output_open_is_not_held_until_its_timeout()
    {
        var runner = new ProcessRunner(outputGrace: TimeSpan.FromSeconds(2));
        {
            var lines = new List<string>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = await runner.RunAsync(new ProcessSpec
            {
                Executable = "/bin/sh", Arguments = ["-c", "sleep 30 & echo built; exit 0"], Timeout = TimeSpan.FromMinutes(5),
            }, (_, l) => { lock (lines) lines.Add(l); }, CancellationToken.None);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"held for {sw.Elapsed}");
            Assert.True(r.Succeeded, r.ToString());
            Assert.False(r.TimedOut);
            Assert.True(r.OutputLeftOpen);
            Assert.Contains("built", lines);
            Assert.Contains(lines, l => l.StartsWith("HCG: sh exited with code 0", StringComparison.Ordinal) && l.Contains("still holds its output open", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// CI run 101 (port-ios, repeat 2): a probe timed out, its tree kill left processes STOPPED (state T, re-parented to
    /// launchd) and the UI driver in the same process group died of SIGHUP. .NET's Process.Kill(entireProcessTree: true)
    /// sends SIGSTOP then SIGKILL to each process (strace); HCG's tree kill sends only SIGKILL, to the whole tree collected
    /// first. After a timeout, nothing of the tree is left alive — stopped or running.
    /// </summary>
    [LinuxFact]
    public async Task A_timed_out_command_and_everything_it_started_are_killed_and_nothing_is_left_stopped()
    {
        var marker = "7" + Random.Shared.Next(100000, 999999);   // a unique sleep duration identifies this tree's processes
        var r = await _runner.RunAsync(new ProcessSpec
        {
            Executable = "/bin/sh", Arguments = ["-c", $"sh -c 'sleep {marker}; :' & sh -c 'sleep {marker}; :' & sleep {marker}"], Timeout = TimeSpan.FromSeconds(1),
        }, null, CancellationToken.None);
        Assert.True(r.TimedOut);
        await Task.Delay(500);
        var alive = Directory.GetDirectories("/proc").Select(d => Path.GetFileName(d)).Where(n => n.All(char.IsDigit))
            .Select(pid => (Cmd: Read($"/proc/{pid}/cmdline").Replace('\0', ' '), State: Read($"/proc/{pid}/stat")))
            .Where(p => p.Cmd.Contains(marker, StringComparison.Ordinal))
            .Select(p => (p.Cmd, State: p.State.Length > 0 ? p.State[(p.State.LastIndexOf(')') + 2)..].Split(' ')[0] : "?"))
            .Where(p => p.State != "Z")   // zombies are dead (killed, waiting to be reaped)
            .ToList();
        Assert.True(alive.Count == 0, "left alive: " + string.Join("; ", alive.Select(a => $"{a.State} {a.Cmd}")));

        static string Read(string path) { try { return File.ReadAllText(path); } catch (IOException) { return ""; } catch (UnauthorizedAccessException) { return ""; } }
    }

    [UnixFact]
    public async Task A_command_that_really_hangs_still_times_out_and_is_killed()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await _runner.RunAsync(new ProcessSpec { Executable = "/bin/sh", Arguments = ["-c", "sleep 60"], Timeout = TimeSpan.FromSeconds(2) }, null, CancellationToken.None);
        Assert.True(r.TimedOut);
        Assert.False(r.Succeeded);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"took {sw.Elapsed}");
    }

    [Fact]
    public async Task Rejects_relative_or_missing_executables()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _runner.RunAsync(new ProcessSpec { Executable = "dotnet" }, null, CancellationToken.None));
        await Assert.ThrowsAsync<FileNotFoundException>(() => _runner.RunAsync(new ProcessSpec { Executable = Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid()) }, null, CancellationToken.None));
    }
}

public class ToolchainTests
{
    [Fact]
    public void Parses_dotnet_list_sdks()
    {
        var sdks = ToolchainDetector.ParseDotNetSdkList("8.0.404 [/usr/share/dotnet/sdk]\n10.0.114 [/usr/lib/dotnet/sdk]\n11.0.100-preview.1.25 [/x]\ngarbage\n");
        Assert.Equal(["8.0.404", "10.0.114", "11.0.100-preview.1.25"], sdks.Select(s => s.Raw));
    }

    [Fact]
    public void Locator_searches_path_and_candidates()
    {
        var custom = OperatingSystem.IsWindows() ? @"D:\custom\dotnet.exe" : "/opt/custom/dotnet";
        var existing = new HashSet<string> { custom, Path.Combine("/fake/bin", "git") };
        var locator = new ExecutableLocator(n => n == "PATH" ? "/fake/bin" : null, existing.Contains);
        if (!OperatingSystem.IsWindows()) Assert.Equal(Path.Combine("/fake/bin", "git"), locator.Find("git"));
        Assert.Equal(custom, locator.Find("dotnet", [custom]));
        Assert.Null(locator.Find("definitely-not-a-tool-xyz"));
    }

    [RequiresDotNetFact]
    public async Task Detects_real_toolchains_on_this_host()
    {
        using var t = new TempDir();
        var detector = new ToolchainDetector(new ProcessRunner(), new ExecutableLocator(), new AppPaths(t.Path), TimeProvider.System);
        var report = await detector.DetectAsync(CancellationToken.None);
        var sdk = report.Find(ToolchainIds.DotNetSdk)!;
        Assert.Equal(ToolchainStatus.Installed, sdk.Status);
        Assert.NotNull(sdk.Version);
        Assert.True(File.Exists(sdk.ExecutablePath));
        Assert.Equal(HostInfo.CurrentPlatform, report.Host.Platform);
        // Tools for other host OSes are reported as unsupported, never as installed or missing.
        var foreign = HostInfo.CurrentPlatform == TargetPlatform.Windows ? ToolchainIds.Hdiutil : ToolchainIds.SignTool;
        Assert.Equal(ToolchainStatus.UnsupportedOnHost, report.Find(foreign)!.Status);
        Assert.NotNull(report.AvailableDiskBytes);
    }
}
