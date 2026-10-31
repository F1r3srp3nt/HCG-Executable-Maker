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
