using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.ProjectInspection.Swift;

/// <summary>
/// Whether a Swift package can be built into a macOS app on THIS host, and if not, exactly why and how to fix it.
/// Distinguishes "supported project, missing toolchain" from "needs another host" from "unsupported project".
/// </summary>
public static class SwiftCapabilities
{
    public sealed record Assessment(SupportLevel Support, string Summary, IReadOnlyList<string> Remediation, IReadOnlyList<ToolchainRequirement> Requirements);

    public const string DeveloperDirHint = "Install Xcode from the Mac App Store and run: sudo xcode-select --switch /Applications/Xcode.app/Contents/Developer (or install the Command Line Tools: xcode-select --install).";

    public static Assessment Evaluate(SwiftPackageInfo package, ToolchainReport? report)
    {
        var requirements = new List<ToolchainRequirement>();
        var fixes = new List<string>();
        if (report is null)
            return new Assessment(SupportLevel.MissingToolchain, "Toolchains have not been detected yet.", ["Open the Toolchains page and press Refresh."], requirements);

        if (report.Host.Platform != TargetPlatform.MacOS)
        {
            var swiftHere = report.Find(ToolchainIds.Swift);
            requirements.Add(new ToolchainRequirement("macos-host", "macOS build host", package.MinimumMacOSHint, false, $"This computer runs {report.Host.Platform.DisplayName()}"));
            requirements.Add(new ToolchainRequirement(ToolchainIds.XcodeCommandLineTools, "Xcode or Command Line Tools", null, false, "Only available on macOS"));
            requirements.Add(new ToolchainRequirement(ToolchainIds.Swift, "Swift compiler", package.ToolsVersion, false,
                swiftHere?.IsAvailable == true ? $"Swift {swiftHere.Version} is installed here, but it cannot build macOS SwiftUI/AppKit apps" : "Provided by Xcode on the Mac"));
            return new Assessment(SupportLevel.RequiresDifferentHost,
                $"Swift macOS apps are built on a Mac. This computer runs {report.Host.Platform.DisplayName()}; HCG does not build macOS SwiftUI/AppKit apps on {report.Host.Platform.DisplayName()}.",
                ["Import this project in HCG Executable Maker on a Mac with Xcode or the Command Line Tools, or use a macOS CI runner (docs/SWIFT-MACOS.md)."], requirements);
        }

        var devDir = report.Find(ToolchainIds.XcodeCommandLineTools);
        requirements.Add(new ToolchainRequirement(ToolchainIds.XcodeCommandLineTools, "Apple developer directory", null, devDir?.IsAvailable == true, devDir?.ExecutablePath ?? devDir?.Detail));
        if (devDir?.IsAvailable != true) fixes.Add(DeveloperDirHint);

        var swift = report.Find(ToolchainIds.Swift);
        var needed = package.ToolsVersion;
        var swiftOk = swift?.IsAvailable == true;
        var versionOk = !swiftOk || needed is null || swift!.Version is null || Compare(swift.Version, needed) >= 0;
        requirements.Add(new ToolchainRequirement(ToolchainIds.Swift, "Swift compiler", needed, swiftOk && versionOk,
            swiftOk ? $"Swift {swift!.Version}{(needed is null ? "" : $"; package requires tools version {needed}")}" : swift?.Detail));
        if (!swiftOk) fixes.Add("The Swift compiler was not found. " + DeveloperDirHint);
        else if (!versionOk)
            fixes.Add($"This package requires Swift tools version {needed}, but the selected toolchain is Swift {swift!.Version}. Install a newer Xcode and select it with: sudo xcode-select --switch /Applications/Xcode.app/Contents/Developer");

        var sdk = report.Find(ToolchainIds.MacOSSdk);
        requirements.Add(new ToolchainRequirement(ToolchainIds.MacOSSdk, "macOS SDK", package.MinimumMacOSHint, sdk?.IsAvailable == true, sdk?.Version is { } v ? $"macOS SDK {v}" : sdk?.Detail));
        if (sdk?.IsAvailable != true) fixes.Add("No macOS SDK was found (xcrun --sdk macosx --show-sdk-path failed). " + DeveloperDirHint);
        else if (package.MinimumMacOSHint is { } min && sdk.Version is { } sdkVersion && Compare(sdkVersion, min) < 0)
            fixes.Add($"The package targets macOS {min}, newer than the installed macOS SDK {sdkVersion}. Install a newer Xcode.");

        var codesign = report.Find(ToolchainIds.Codesign);
        requirements.Add(new ToolchainRequirement(ToolchainIds.Codesign, "codesign (ad-hoc signing)", null, codesign?.IsAvailable == true, codesign?.ExecutablePath));
        if (codesign?.IsAvailable != true) fixes.Add("codesign was not found: Apple Silicon Macs refuse unsigned executables. Install the Command Line Tools: xcode-select --install");

        // Test prerequisites are reported separately: they never make a buildable package "unsupported".
        var xctest = report.Find(ToolchainIds.XcTest);
        requirements.Add(new ToolchainRequirement(ToolchainIds.XcTest, "XCTest — only to run tests (HCG builds without running tests)", null,
            xctest?.IsAvailable == true, xctest?.IsAvailable == true ? xctest.ExecutablePath : (xctest?.InstallHint ?? "Not detected")));

        return fixes.Count == 0
            ? new Assessment(SupportLevel.Supported, $"Buildable on this Mac with Swift {swift!.Version}.", [], requirements)
            : new Assessment(SupportLevel.MissingToolchain, "Supported Swift package, but this Mac is missing required tools.", fixes, requirements);
    }

    /// <summary>Compares dotted versions numerically ("5.10" &gt; "5.9").</summary>
    public static int Compare(string a, string b)
    {
        static int[] Parts(string v) => v.Split('-')[0].Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
        var x = Parts(a);
        var y = Parts(b);
        for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            var c = (i < x.Length ? x[i] : 0).CompareTo(i < y.Length ? y[i] : 0);
            if (c != 0) return c;
        }
        return 0;
    }
}
