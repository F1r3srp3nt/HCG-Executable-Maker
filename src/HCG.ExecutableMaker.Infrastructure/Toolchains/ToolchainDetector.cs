using System.Text.RegularExpressions;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HCG.ExecutableMaker.Infrastructure.Toolchains;

/// <summary>
/// Detects installed development tools by locating executables and running harmless version probes.
/// Never installs anything.
/// </summary>
public sealed partial class ToolchainDetector(
    IProcessRunner runner,
    IExecutableLocator locator,
    IAppPaths paths,
    TimeProvider time,
    ILogger<ToolchainDetector>? logger = null,
    ISettingsStore? settings = null) : IToolchainDetector
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);
    private readonly ILogger _logger = logger ?? NullLogger<ToolchainDetector>.Instance;

    /// <summary>Overridable for tests.</summary>
    public HostEnvironment Host { get; init; } = HostInfo.Detect();

    public async Task<ToolchainReport> DetectAsync(CancellationToken cancellationToken)
    {
        var tasks = new List<Task<ToolchainInfo>>
        {
            DetectDotNetAsync(cancellationToken),
            DetectGitAsync(cancellationToken),
        };
        tasks.AddRange(MacTools(cancellationToken));
        tasks.AddRange(SwiftTools(cancellationToken));
        tasks.AddRange(WindowsTools(cancellationToken));
        var saved = settings is null ? new AppSettings() : await settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        var xcodeTask = Host.Platform == TargetPlatform.MacOS ? DetectXcodeAsync(cancellationToken) : null;
        var mobileTask = DetectMobileAsync(saved, xcodeTask, cancellationToken);
        var nodeTask = new NodeDiscovery(runner).DiscoverAsync(Host.Platform, cancellationToken);
        var mobile = await mobileTask.ConfigureAwait(false);
        var node = await nodeTask.ConfigureAwait(false);
        var results = (await Task.WhenAll(tasks).ConfigureAwait(false)).Concat(mobile.Rows).Concat(NodeDiscovery.Rows(node)).ToList();
        XcodeStatus? xcode = null;
        if (xcodeTask is not null)
        {
            xcode = await xcodeTask.ConfigureAwait(false);
            results.Add(XcodeToolchainInfo(xcode));
        }
        var (free, diskPath) = GetDiskSpace();
        return new ToolchainReport(Host, results, free, diskPath, time.GetUtcNow())
        {
            Xcode = xcode, Java = mobile.Java, AndroidSdk = mobile.Android, AppleIdentities = mobile.Identities, IosProfiles = mobile.Profiles,
            NodeInstallations = node.Installations,
        };
    }

    // ---------- Cross-platform ----------

    private async Task<ToolchainInfo> DetectDotNetAsync(CancellationToken ct)
    {
        const string hint = "Install the .NET 10 SDK from https://dotnet.microsoft.com/download (or your OS package manager), then press Refresh.";
        var exe = locator.Find("dotnet", ExecutableLocator.DotNetCandidates());
        if (exe is null)
            return Missing(ToolchainIds.DotNetSdk, ".NET SDK", "Build", hint);
        try
        {
            var (result, stdout, stderr) = await runner.CaptureAsync(new ProcessSpec { Executable = exe, Arguments = ["--list-sdks"], Timeout = ProbeTimeout, Environment = ProbeEnvironment() }, ct).ConfigureAwait(false);
            var sdks = ParseDotNetSdkList(stdout);
            if (!result.Succeeded || sdks.Count == 0)
            {
                return new ToolchainInfo
                {
                    Id = ToolchainIds.DotNetSdk, DisplayName = ".NET SDK", Category = "Build",
                    Status = sdks.Count == 0 && result.Succeeded ? ToolchainStatus.Missing : ToolchainStatus.ValidationFailed,
                    ExecutablePath = exe,
                    Detail = sdks.Count == 0 ? "The dotnet host was found, but no SDKs are installed (runtime only)." : FirstLine(stderr),
                    InstallHint = hint,
                };
            }
            var ordered = sdks.OrderByDescending(v => v.Version).ToList();
            return new ToolchainInfo
            {
                Id = ToolchainIds.DotNetSdk, DisplayName = ".NET SDK", Category = "Build", Status = ToolchainStatus.Installed,
                Version = ordered[0].Raw, ExecutablePath = exe,
                AdditionalVersions = ordered.Select(v => v.Raw).ToList(),
                Detail = $"{ordered.Count} SDK{(ordered.Count == 1 ? "" : "s")} installed",
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "dotnet probe failed");
            return Failed(ToolchainIds.DotNetSdk, ".NET SDK", "Build", exe, ex.Message, hint);
        }
    }

    public sealed record SdkVersion(string Raw, Version Version);

    /// <summary>Parses <c>dotnet --list-sdks</c> output, e.g. "10.0.114 [/usr/lib/dotnet/sdk]".</summary>
    public static IReadOnlyList<SdkVersion> ParseDotNetSdkList(string output)
    {
        var list = new List<SdkVersion>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var m = SdkLineRegex().Match(line);
            if (!m.Success) continue;
            var raw = m.Groups[1].Value;
            var numeric = raw.Split('-')[0];
            if (Version.TryParse(numeric, out var v)) list.Add(new SdkVersion(raw, v));
        }
        return list;
    }

    [GeneratedRegex(@"^(\d+\.\d+\.\d+(?:-[\w.]+)?)\s+\[")]
    private static partial Regex SdkLineRegex();

    private async Task<ToolchainInfo> DetectGitAsync(CancellationToken ct)
    {
        var hint = Host.Platform switch
        {
            TargetPlatform.MacOS => "Install Git via Xcode command-line tools (xcode-select --install) or Homebrew.",
            TargetPlatform.Windows => "Install Git for Windows from https://git-scm.com/download/win.",
            _ => "Install Git with your distribution's package manager.",
        };
        var exe = locator.Find("git");
        if (exe is null) return Missing(ToolchainIds.Git, "Git", "Source control", hint);
        return await ProbeVersionAsync(ToolchainIds.Git, "Git", "Source control", exe, ["--version"], GitVersionRegex(), hint, ct).ConfigureAwait(false);
    }

    [GeneratedRegex(@"git version (\S+)")]
    private static partial Regex GitVersionRegex();

    // ---------- macOS ----------

    private IEnumerable<Task<ToolchainInfo>> MacTools(CancellationToken ct)
    {
        const string cat = "macOS";
        if (Host.Platform != TargetPlatform.MacOS)
        {
            foreach (var (id, name) in new[]
            {
                (ToolchainIds.XcodeCommandLineTools, "Xcode Command Line Tools"), (ToolchainIds.Xcode, "Xcode"),
                (ToolchainIds.Codesign, "codesign"), (ToolchainIds.Hdiutil, "hdiutil (DMG)"),
                (ToolchainIds.Notarytool, "notarytool"), (ToolchainIds.Productbuild, "productbuild (PKG)"),
            })
            {
                yield return Task.FromResult(Unsupported(id, name, cat, "Only available on macOS hosts."));
            }
            yield break;
        }

        yield return DetectXcodeSelectAsync(ct);
        // Xcode itself: DetectXcodeAsync (discovery + diagnosis), added in DetectAsync.
        yield return Task.FromResult(Presence(ToolchainIds.Codesign, "codesign", cat, "/usr/bin/codesign", "Install Xcode command-line tools: xcode-select --install"));
        yield return Task.FromResult(Presence(ToolchainIds.Hdiutil, "hdiutil (DMG)", cat, "/usr/bin/hdiutil", "hdiutil ships with macOS."));
        yield return DetectNotarytoolAsync(ct);
        yield return Task.FromResult(Presence(ToolchainIds.Productbuild, "productbuild (PKG)", cat, "/usr/bin/productbuild", "Install Xcode command-line tools: xcode-select --install"));
    }

    private async Task<ToolchainInfo> DetectXcodeSelectAsync(CancellationToken ct)
    {
        const string hint = "Run: xcode-select --install";
        var exe = "/usr/bin/xcode-select";
        if (!File.Exists(exe)) return Missing(ToolchainIds.XcodeCommandLineTools, "Xcode Command Line Tools", "macOS", hint);
        try
        {
            var dir = await ActiveDeveloperDirectoryAsync(ct).ConfigureAwait(false) ?? "";
            return Directory.Exists(dir)
                ? new ToolchainInfo { Id = ToolchainIds.XcodeCommandLineTools, DisplayName = "Xcode Command Line Tools", Category = "macOS", Status = ToolchainStatus.Installed, ExecutablePath = dir, Version = DescribeDeveloperDirectory(dir).Split(' ')[0], Detail = "Selected developer directory: " + DescribeDeveloperDirectory(dir) }
                : Missing(ToolchainIds.XcodeCommandLineTools, "Xcode Command Line Tools", "macOS", hint);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failed(ToolchainIds.XcodeCommandLineTools, "Xcode Command Line Tools", "macOS", exe, ex.Message, hint);
        }
    }

    private async Task<ToolchainInfo> DetectNotarytoolAsync(CancellationToken ct)
    {
        const string hint = "notarytool ships with Xcode 13+. Install Xcode and select it with xcode-select.";
        const string xcrun = "/usr/bin/xcrun";
        if (!File.Exists(xcrun)) return Missing(ToolchainIds.Notarytool, "notarytool", "macOS", hint);
        try
        {
            var (r, stdout, _) = await runner.CaptureAsync(new ProcessSpec { Executable = xcrun, Arguments = ["--find", "notarytool"], Timeout = ProbeTimeout, Environment = ProbeEnvironment() }, ct).ConfigureAwait(false);
            var path = stdout.Trim();
            return r.Succeeded && File.Exists(path)
                ? new ToolchainInfo { Id = ToolchainIds.Notarytool, DisplayName = "notarytool", Category = "macOS", Status = ToolchainStatus.Installed, ExecutablePath = path }
                : Missing(ToolchainIds.Notarytool, "notarytool", "macOS", hint);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failed(ToolchainIds.Notarytool, "notarytool", "macOS", xcrun, ex.Message, hint);
        }
    }

    // ---------- Windows ----------

    private IEnumerable<Task<ToolchainInfo>> WindowsTools(CancellationToken ct)
    {
        const string cat = "Windows";
        if (Host.Platform != TargetPlatform.Windows)
        {
            foreach (var (id, name) in new[]
            {
                (ToolchainIds.WindowsSdk, "Windows SDK"), (ToolchainIds.SignTool, "signtool"), (ToolchainIds.MsBuild, "MSBuild (Visual Studio)"),
                (ToolchainIds.MakeAppx, "MakeAppx (MSIX)"), (ToolchainIds.WixToolset, "WiX Toolset (MSI)"), (ToolchainIds.InnoSetup, "Inno Setup (installer .exe)"),
            })
            {
                yield return Task.FromResult(Unsupported(id, name, cat, "Only available on Windows hosts."));
            }
            yield break;
        }

        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var kitsBin = Path.Combine(pf86, "Windows Kits", "10", "bin");
        var arch = HostInfo.CurrentArchitecture == TargetArchitecture.Arm64 ? "arm64" : "x64";
        var sdkVersions = Directory.Exists(kitsBin)
            ? Directory.GetDirectories(kitsBin, "10.*").Select(Path.GetFileName).OfType<string>().OrderByDescending(v => Version.TryParse(v, out var x) ? x : new Version()).ToList()
            : [];
        const string sdkHint = "Install the Windows 10/11 SDK via the Visual Studio Installer or https://developer.microsoft.com/windows/downloads/windows-sdk/.";
        yield return Task.FromResult(sdkVersions.Count > 0
            ? new ToolchainInfo { Id = ToolchainIds.WindowsSdk, DisplayName = "Windows SDK", Category = cat, Status = ToolchainStatus.Installed, Version = sdkVersions[0], ExecutablePath = kitsBin, AdditionalVersions = sdkVersions }
            : Missing(ToolchainIds.WindowsSdk, "Windows SDK", cat, sdkHint));
        string? FindKitTool(string exe) => sdkVersions.Select(v => Path.Combine(kitsBin, v, arch, exe)).FirstOrDefault(File.Exists);
        var signtool = FindKitTool("signtool.exe");
        yield return Task.FromResult(signtool is null ? Missing(ToolchainIds.SignTool, "signtool", cat, sdkHint)
            : new ToolchainInfo { Id = ToolchainIds.SignTool, DisplayName = "signtool", Category = cat, Status = ToolchainStatus.Installed, ExecutablePath = signtool, Version = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(signtool))) });
        var makeappx = FindKitTool("makeappx.exe");
        yield return Task.FromResult(makeappx is null ? Missing(ToolchainIds.MakeAppx, "MakeAppx (MSIX)", cat, sdkHint)
            : new ToolchainInfo { Id = ToolchainIds.MakeAppx, DisplayName = "MakeAppx (MSIX)", Category = cat, Status = ToolchainStatus.Installed, ExecutablePath = makeappx });

        yield return DetectMsBuildAsync(pf86, ct);

        var wix = locator.Find("wix", [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools", "wix.exe")]);
        yield return wix is null
            ? Task.FromResult(Missing(ToolchainIds.WixToolset, "WiX Toolset (MSI)", cat, "Install WiX v5+: dotnet tool install --global wix"))
            : ProbeVersionAsync(ToolchainIds.WixToolset, "WiX Toolset (MSI)", cat, wix, ["--version"], AnyVersionRegex(), null, ct);

        var iscc = locator.Find("ISCC", [
            Path.Combine(pf86, "Inno Setup 6", "ISCC.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Inno Setup 6", "ISCC.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Inno Setup 6", "ISCC.exe"),
        ]);
        yield return Task.FromResult(iscc is null
            ? Missing(ToolchainIds.InnoSetup, "Inno Setup (installer .exe)", cat, "Install Inno Setup 6 from https://jrsoftware.org/isinfo.php.")
            : InnoSetupInfo(iscc, cat));
    }

    /// <summary>Reads ISCC's file version (no process is started). Inno Setup 5 is reported but not accepted.</summary>
    private static ToolchainInfo InnoSetupInfo(string iscc, string cat)
    {
        var raw = System.Diagnostics.FileVersionInfo.GetVersionInfo(iscc).ProductVersion?.Trim();
        var version = raw is null ? null : AnyVersionRegex().Match(raw) is { Success: true } m ? m.Value : raw;
        var major = version is not null && int.TryParse(version.Split('.')[0], out var mj) ? mj : 0;
        return new ToolchainInfo
        {
            Id = ToolchainIds.InnoSetup, DisplayName = "Inno Setup (installer .exe)", Category = cat, ExecutablePath = iscc, Version = version,
            Status = major is 0 or >= 6 ? ToolchainStatus.Installed : ToolchainStatus.ValidationFailed,
            Detail = major is > 0 and < 6 ? $"Inno Setup {version} is too old; version 6 is required." : "ISCC.exe",
            InstallHint = "Install Inno Setup 6 from https://jrsoftware.org/isinfo.php.",
        };
    }

    private async Task<ToolchainInfo> DetectMsBuildAsync(string pf86, CancellationToken ct)
    {
        const string hint = "Optional: the .NET SDK includes MSBuild. Visual Studio Build Tools are only needed for some Windows packaging scenarios.";
        var vswhere = Path.Combine(pf86, "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(vswhere)) return Missing(ToolchainIds.MsBuild, "MSBuild (Visual Studio)", "Windows", hint);
        try
        {
            var (r, stdout, _) = await runner.CaptureAsync(new ProcessSpec
            {
                Executable = vswhere,
                Arguments = ["-latest", "-products", "*", "-requires", "Microsoft.Component.MSBuild", "-find", @"MSBuild\**\Bin\MSBuild.exe"],
                Timeout = ProbeTimeout,
            }, ct).ConfigureAwait(false);
            var path = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            return r.Succeeded && path is not null && File.Exists(path)
                ? new ToolchainInfo { Id = ToolchainIds.MsBuild, DisplayName = "MSBuild (Visual Studio)", Category = "Windows", Status = ToolchainStatus.Installed, ExecutablePath = path }
                : Missing(ToolchainIds.MsBuild, "MSBuild (Visual Studio)", "Windows", hint);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failed(ToolchainIds.MsBuild, "MSBuild (Visual Studio)", "Windows", vswhere, ex.Message, hint);
        }
    }

    [GeneratedRegex(@"(\d+\.\d+[\w.+-]*)")]
    private static partial Regex AnyVersionRegex();

    // ---------- Helpers ----------

    private async Task<ToolchainInfo> ProbeVersionAsync(string id, string name, string category, string exe, string[] args, Regex versionRegex, string? hint, CancellationToken ct)
    {
        try
        {
            var (r, stdout, stderr) = await runner.CaptureAsync(new ProcessSpec { Executable = exe, Arguments = args, Timeout = ProbeTimeout, Environment = ProbeEnvironment() }, ct).ConfigureAwait(false);
            var text = stdout + "\n" + stderr;
            var m = versionRegex.Match(text);
            if (!r.Succeeded)
                return Failed(id, name, category, exe, $"Version probe exited with code {r.ExitCode}: {FirstLine(text)}", hint);
            return new ToolchainInfo { Id = id, DisplayName = name, Category = category, Status = ToolchainStatus.Installed, ExecutablePath = exe, Version = m.Success ? m.Groups[1].Value : null };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failed(id, name, category, exe, ex.Message, hint);
        }
    }

    private Task<ToolchainInfo> ProbeVersionOrPresenceAsync(string id, string name, string category, string exe, string[] args, Regex regex, string hint, CancellationToken ct) =>
        File.Exists(exe) ? ProbeVersionAsync(id, name, category, exe, args, regex, hint, ct) : Task.FromResult(Missing(id, name, category, hint));

    private static ToolchainInfo Presence(string id, string name, string category, string path, string hint) =>
        File.Exists(path)
            ? new ToolchainInfo { Id = id, DisplayName = name, Category = category, Status = ToolchainStatus.Installed, ExecutablePath = path }
            : Missing(id, name, category, hint);

    private static ToolchainInfo Missing(string id, string name, string category, string? hint) =>
        new() { Id = id, DisplayName = name, Category = category, Status = ToolchainStatus.Missing, InstallHint = hint };

    private static ToolchainInfo Unsupported(string id, string name, string category, string detail) =>
        new() { Id = id, DisplayName = name, Category = category, Status = ToolchainStatus.UnsupportedOnHost, Detail = detail };

    private static ToolchainInfo Failed(string id, string name, string category, string exe, string detail, string? hint) =>
        new() { Id = id, DisplayName = name, Category = category, Status = ToolchainStatus.ValidationFailed, ExecutablePath = exe, Detail = detail, InstallHint = hint };

    private static string FirstLine(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";

    private static IReadOnlyDictionary<string, string> ProbeEnvironment()
    {
        var env = new Dictionary<string, string>();
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            if (e.Key is string k && e.Value is string v) env[k] = v;
        env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        env["DOTNET_NOLOGO"] = "1";
        env["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        return env;
    }

    private (long? Free, string? Path) GetDiskSpace()
    {
        try
        {
            var root = Path.GetFullPath(paths.Root);
            var drive = DriveInfo.GetDrives()
                .Where(d => d.IsReady && PathGuard.IsWithin(d.RootDirectory.FullName, root))
                .OrderByDescending(d => d.RootDirectory.FullName.Length)
                .FirstOrDefault();
            return drive is null ? (null, null) : (drive.AvailableFreeSpace, drive.RootDirectory.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, null);
        }
    }
}
