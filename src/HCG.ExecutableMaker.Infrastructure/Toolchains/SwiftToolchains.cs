using System.Text.RegularExpressions;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.Infrastructure.Toolchains;

/// <summary>Swift compiler, macOS SDK and macOS version probes. Detection only: nothing is installed.</summary>
public sealed partial class ToolchainDetector
{
    private const string SwiftCategory = "Swift / macOS apps";

    private IEnumerable<Task<ToolchainInfo>> SwiftTools(CancellationToken ct)
    {
        yield return DetectSwiftAsync(ct);
        if (Host.Platform != TargetPlatform.MacOS)
        {
            yield return Task.FromResult(Unsupported(ToolchainIds.MacOSSdk, "macOS SDK", SwiftCategory, "Only available on macOS hosts (it ships with Xcode / the Command Line Tools)."));
            yield return Task.FromResult(Unsupported(ToolchainIds.MacOSVersion, "macOS", SwiftCategory, $"This computer runs {Host.Platform.DisplayName()}."));
            yield break;
        }
        yield return DetectMacOSVersionAsync(ct);
        yield return DetectMacOSSdkAsync(ct);
    }

    private async Task<ToolchainInfo> DetectSwiftAsync(CancellationToken ct)
    {
        var hint = Host.Platform == TargetPlatform.MacOS
            ? "Install Xcode from the Mac App Store (or run: xcode-select --install), then select it: sudo xcode-select --switch /Applications/Xcode.app/Contents/Developer"
            : "Swift for Linux/Windows is available from https://www.swift.org/install/ — note that macOS SwiftUI/AppKit apps can only be built on a Mac.";
        var exe = Host.Platform == TargetPlatform.MacOS && File.Exists("/usr/bin/swift") ? "/usr/bin/swift" : locator.Find("swift");
        if (exe is null) return Missing(ToolchainIds.Swift, "Swift compiler", SwiftCategory, hint);
        try
        {
            var (r, stdout, stderr) = await runner.CaptureAsync(new ProcessSpec { Executable = exe, Arguments = ["--version"], Timeout = TimeSpan.FromSeconds(60), Environment = ProbeEnvironment() }, ct).ConfigureAwait(false);
            var text = stdout + "\n" + stderr;
            var version = ParseSwiftVersion(text);
            if (!r.Succeeded || version is null)
                return Failed(ToolchainIds.Swift, "Swift compiler", SwiftCategory, exe, r.Succeeded ? "swift --version printed no version." : FirstLine(text), hint);
            return new ToolchainInfo
            {
                Id = ToolchainIds.Swift, DisplayName = "Swift compiler", Category = SwiftCategory, Status = ToolchainStatus.Installed,
                Version = version, ExecutablePath = exe, Detail = FirstLine(text), InstallHint = hint,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failed(ToolchainIds.Swift, "Swift compiler", SwiftCategory, exe, ex.Message, hint);
        }
    }

    /// <summary>"Apple Swift version 6.1.2 (swiftlang-…)" / "Swift version 6.0.3 (swift-6.0.3-RELEASE)" → "6.1.2".</summary>
    public static string? ParseSwiftVersion(string output) =>
        SwiftVersionRegex().Match(output) is { Success: true } m ? m.Groups[1].Value : null;

    private async Task<ToolchainInfo> DetectMacOSVersionAsync(CancellationToken ct)
    {
        try
        {
            var (r, stdout, _) = await runner.CaptureAsync(new ProcessSpec { Executable = "/usr/bin/sw_vers", Arguments = ["-productVersion"], Timeout = ProbeTimeout, Environment = ProbeEnvironment() }, ct).ConfigureAwait(false);
            var v = stdout.Trim();
            return r.Succeeded && v.Length > 0
                ? new ToolchainInfo { Id = ToolchainIds.MacOSVersion, DisplayName = "macOS", Category = SwiftCategory, Status = ToolchainStatus.Installed, Version = v, Detail = $"macOS {v} ({Host.Architecture.DisplayName()})" }
                : Failed(ToolchainIds.MacOSVersion, "macOS", SwiftCategory, "/usr/bin/sw_vers", "sw_vers did not report a version.", null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failed(ToolchainIds.MacOSVersion, "macOS", SwiftCategory, "/usr/bin/sw_vers", ex.Message, null);
        }
    }

    private async Task<ToolchainInfo> DetectMacOSSdkAsync(CancellationToken ct)
    {
        const string hint = "Install Xcode or the Command Line Tools (xcode-select --install) and select them with xcode-select.";
        const string xcrun = "/usr/bin/xcrun";
        if (!File.Exists(xcrun)) return Missing(ToolchainIds.MacOSSdk, "macOS SDK", SwiftCategory, hint);
        try
        {
            var (rv, version, _) = await runner.CaptureAsync(new ProcessSpec { Executable = xcrun, Arguments = ["--sdk", "macosx", "--show-sdk-version"], Timeout = ProbeTimeout, Environment = ProbeEnvironment() }, ct).ConfigureAwait(false);
            var (rp, path, err) = await runner.CaptureAsync(new ProcessSpec { Executable = xcrun, Arguments = ["--sdk", "macosx", "--show-sdk-path"], Timeout = ProbeTimeout, Environment = ProbeEnvironment() }, ct).ConfigureAwait(false);
            var sdkPath = path.Trim();
            return rv.Succeeded && rp.Succeeded && Directory.Exists(sdkPath)
                ? new ToolchainInfo { Id = ToolchainIds.MacOSSdk, DisplayName = "macOS SDK", Category = SwiftCategory, Status = ToolchainStatus.Installed, Version = version.Trim(), ExecutablePath = sdkPath, Detail = sdkPath }
                : new ToolchainInfo { Id = ToolchainIds.MacOSSdk, DisplayName = "macOS SDK", Category = SwiftCategory, Status = ToolchainStatus.Missing, Detail = FirstLine(err), InstallHint = hint };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failed(ToolchainIds.MacOSSdk, "macOS SDK", SwiftCategory, xcrun, ex.Message, hint);
        }
    }

    /// <summary>Describes the selected developer directory: a full Xcode or only the Command Line Tools.</summary>
    public static string DescribeDeveloperDirectory(string dir) =>
        dir.Contains(".app/Contents/Developer", StringComparison.Ordinal) ? $"Xcode ({dir})"
        : dir.Contains("CommandLineTools", StringComparison.Ordinal) ? $"Command Line Tools only ({dir}); enough for Swift packages, Xcode is needed for iOS builds"
        : dir;

    [GeneratedRegex(@"Swift version (\d+(?:\.\d+){0,2})")]
    private static partial Regex SwiftVersionRegex();
}
