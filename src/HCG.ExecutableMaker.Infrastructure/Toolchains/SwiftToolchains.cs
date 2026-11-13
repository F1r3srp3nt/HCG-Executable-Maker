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
            yield return Task.FromResult(Unsupported(ToolchainIds.XcTest, "XCTest (tests only)", SwiftCategory, "Only relevant on macOS."));
            yield break;
        }
        yield return DetectMacOSVersionAsync(ct);
        yield return DetectMacOSSdkAsync(ct);
        yield return DetectXcTestAsync(ct);
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
            // xcrun resolves an SDK through xcodebuild and caches the answer per developer directory; the first call after
            // another Xcode is selected can take well over 20 s on a busy Mac (CI run 77), so these probes wait longer
            // and a timeout is reported as such, never as a missing SDK.
            var timeout = TimeSpan.FromSeconds(60);
            var (rv, version, _) = await runner.CaptureAsync(new ProcessSpec { Executable = xcrun, Arguments = ["--sdk", "macosx", "--show-sdk-version"], Timeout = timeout, Environment = ProbeEnvironment() }, ct).ConfigureAwait(false);
            if (rv.TimedOut) return TimedOutSdk();
            var (rp, path, err) = await runner.CaptureAsync(new ProcessSpec { Executable = xcrun, Arguments = ["--sdk", "macosx", "--show-sdk-path"], Timeout = timeout, Environment = ProbeEnvironment() }, ct).ConfigureAwait(false);
            if (rp.TimedOut) return TimedOutSdk();
            var sdkPath = path.Trim();
            return rv.Succeeded && rp.Succeeded && Directory.Exists(sdkPath)
                ? new ToolchainInfo { Id = ToolchainIds.MacOSSdk, DisplayName = "macOS SDK", Category = SwiftCategory, Status = ToolchainStatus.Installed, Version = version.Trim(), ExecutablePath = sdkPath, Detail = sdkPath }
                : new ToolchainInfo
                {
                    Id = ToolchainIds.MacOSSdk, DisplayName = "macOS SDK", Category = SwiftCategory, Status = ToolchainStatus.Missing, InstallHint = hint,
                    Detail = FirstLine(err) is { Length: > 0 } e ? e : $"xcrun --sdk macosx --show-sdk-path exited with code {rp.ExitCode}" + (sdkPath.Length > 0 ? $" ({sdkPath} does not exist)" : "") + ".",
                };

            ToolchainInfo TimedOutSdk() => Failed(ToolchainIds.MacOSSdk, "macOS SDK", SwiftCategory, xcrun,
                $"xcrun --sdk macosx did not answer within {timeout.TotalSeconds:F0} s. After another Xcode is selected, xcrun builds its SDK cache on first use; press Refresh.", hint);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failed(ToolchainIds.MacOSSdk, "macOS SDK", SwiftCategory, xcrun, ex.Message, hint);
        }
    }

    /// <summary>
    /// XCTest in the selected developer directory. Full Xcode ships it; the Command Line Tools alone often do not, in
    /// which case `swift test` fails with "no such module 'XCTest'". Building and packaging an app never need it.
    /// </summary>
    private async Task<ToolchainInfo> DetectXcTestAsync(CancellationToken ct)
    {
        const string name = "XCTest (tests only — not needed to build)";
        const string hint = "Only needed to run the package's tests: select an installed Xcode for HCG (Toolchains › Xcode › Select Xcode) — the system setting is not changed. Building and packaging do not need XCTest.";
        try
        {
            var devDir = await ActiveDeveloperDirectoryAsync(ct).ConfigureAwait(false);
            var (rd, devOut) = (devDir is not null, devDir ?? "");
            var (rp, platformOut, _) = await runner.CaptureAsync(new ProcessSpec { Executable = "/usr/bin/xcrun", Arguments = ["--sdk", "macosx", "--show-sdk-platform-path"], Timeout = ProbeTimeout, Environment = ProbeEnvironment() }, ct).ConfigureAwait(false);
            var candidates = new List<string>();
            if (rp.Succeeded && platformOut.Trim().Length > 0) candidates.Add(Path.Combine(platformOut.Trim(), "Developer", "Library", "Frameworks", "XCTest.framework"));
            if (rd && devOut.Length > 0) candidates.Add(Path.Combine(devOut.Trim(), "Library", "Developer", "Frameworks", "XCTest.framework"));
            var found = candidates.FirstOrDefault(Directory.Exists);
            return found is not null
                ? new ToolchainInfo { Id = ToolchainIds.XcTest, DisplayName = name, Category = SwiftCategory, Status = ToolchainStatus.Installed, ExecutablePath = found, Detail = found }
                : new ToolchainInfo { Id = ToolchainIds.XcTest, DisplayName = name, Category = SwiftCategory, Status = ToolchainStatus.Missing,
                    Detail = $"Not found in {(rd ? devOut : "the selected developer directory")}", InstallHint = hint };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failed(ToolchainIds.XcTest, name, SwiftCategory, "/usr/bin/xcode-select", ex.Message, hint);
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
