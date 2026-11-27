using System.Text.Json;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.ProjectInspection.Swift;

namespace HCG.ExecutableMaker.ProjectInspection.MacXcode;

/// <summary>
/// Preflight for a native macOS app built with Xcode on THIS computer: a Mac, a full Xcode selected (the Command Line
/// Tools have no xcodebuild for app projects), xcodebuild, a macOS SDK at least as new as the deployment target, and
/// the architectures that SDK can build. Each missing item is named with the exact fix. A non-Mac host is reported as
/// "needs a Mac": nothing is ever built or verified there.
/// </summary>
public static class MacXcodeCapabilities
{
    public sealed record Assessment(
        SupportLevel Support,
        string Summary,
        IReadOnlyList<string> Remediation,
        IReadOnlyList<ToolchainRequirement> Requirements,
        IReadOnlyList<MacXcodeArchitecture> Architectures,
        string? Xcodebuild,
        string? Note);

    public const string XcodeHint = "Install Xcode from the Mac App Store (the Command Line Tools alone cannot build Xcode app projects), open it once, then choose it on HCG's Toolchains page (Select Xcode…) or run: sudo xcode-select --switch /Applications/Xcode.app/Contents/Developer";

    public static Assessment Evaluate(MacXcodeTargetInfo target, ToolchainReport? report)
    {
        var requirements = new List<ToolchainRequirement>();
        var fixes = new List<string>();
        if (report is null)
            return new Assessment(SupportLevel.MissingToolchain, "Toolchains have not been detected yet.", ["Open the Toolchains page and press Refresh."], requirements, [], null, null);

        var deployment = target.DeploymentTarget;
        if (report.Host.Platform != TargetPlatform.MacOS)
        {
            requirements.Add(new ToolchainRequirement("macos-host", "Mac build host", deployment is null ? null : $"macOS SDK {deployment}+", false,
                $"This computer runs {report.Host.Platform.DisplayName()}. xcodebuild, the macOS SDK and codesign exist only on macOS."));
            requirements.Add(new ToolchainRequirement("xcode", "Xcode (xcodebuild)", null, false, "Only available on a Mac"));
            return new Assessment(SupportLevel.RequiresDifferentHost,
                $"Native macOS apps are built with Xcode on a Mac. This computer runs {report.Host.Platform.DisplayName()}, so nothing is built or verified here; no remote Mac build host is configured (HCG does not offer one in this version).",
                ["Import this project in HCG Executable Maker on a Mac with Xcode" + (deployment is null ? "" : $" and the macOS {deployment} SDK or later") + "."],
                requirements, [], null, null);
        }

        // Full Xcode, selected for HCG (DEVELOPER_DIR) or system-wide.
        var xcode = report.Xcode;
        var xcodeReady = xcode?.State == XcodeState.Ready;
        requirements.Add(new ToolchainRequirement("xcode", "Xcode", null, xcodeReady,
            xcode is null ? "Not detected" : xcode.VerifiedVersion is { } v ? $"Xcode {v}{(xcode.VerifiedBuild is { } b ? $" ({b})" : "")} — {xcode.ActiveDeveloperDirectory}" : xcode.Diagnosis));
        if (!xcodeReady)
            fixes.Add((xcode?.CorrectiveAction is { Length: > 0 } action ? action + " " : "") + (xcode?.IsCommandLineToolsActive == true
                ? "The Command Line Tools are selected; building an Xcode app project needs the full Xcode. " + XcodeHint
                : XcodeHint));

        string? xcodebuild = null;
        if (xcode?.ActiveDeveloperDirectory is { Length: > 0 } dev && !xcode.IsCommandLineToolsActive)
        {
            var candidate = Path.Combine(dev, "usr", "bin", "xcodebuild");
            if (File.Exists(candidate)) xcodebuild = candidate;
        }
        requirements.Add(new ToolchainRequirement("xcodebuild", "xcodebuild", null, xcodebuild is not null, xcodebuild ?? "Not found in the selected Xcode"));
        if (xcodebuild is null && xcodeReady) fixes.Add("xcodebuild was not found in the selected Xcode (" + xcode!.ActiveDeveloperDirectory + "). Reinstall Xcode or select another one on the Toolchains page.");

        // macOS SDK ≥ MACOSX_DEPLOYMENT_TARGET.
        var sdk = report.Find(ToolchainIds.MacOSSdk);
        var sdkVersion = sdk is { IsAvailable: true } ? sdk.Version : null;
        var sdkOk = sdkVersion is not null && (deployment is null || SwiftCapabilities.Compare(sdkVersion, deployment) >= 0);
        requirements.Add(new ToolchainRequirement(ToolchainIds.MacOSSdk, "macOS SDK", deployment, sdkOk,
            sdkVersion is null ? sdk?.Detail ?? "Not found" : $"macOS SDK {sdkVersion}{(deployment is null ? "" : $"; the project's deployment target is macOS {deployment}")}"));
        if (sdkVersion is null)
            fixes.Add("No macOS SDK was found in the selected developer directory" + (sdk?.Detail is { Length: > 0 } d ? $" ({d})" : "") + ". " + XcodeHint);
        else if (!sdkOk)
            fixes.Add($"This project requires the macOS {deployment} SDK or later (MACOSX_DEPLOYMENT_TARGET = {deployment}); the selected Xcode has the macOS {sdkVersion} SDK. " +
                      $"Install an Xcode that includes the macOS {deployment} SDK (Xcode {Major(deployment!)} or later) and select it on the Toolchains page.");

        // Architectures the SDK can build.
        var archs = sdk?.ExecutablePath is { } sdkPath ? SdkArchitectures(sdkPath) : null;
        var offered = new List<MacXcodeArchitecture>();
        var hasArm = archs is null || archs.Contains("arm64");
        var hasIntel = archs is null || archs.Contains("x86_64");
        if (hasArm) offered.Add(MacXcodeArchitecture.AppleSilicon);
        if (hasArm && hasIntel) offered.Add(MacXcodeArchitecture.Universal);
        if (hasIntel) offered.Add(MacXcodeArchitecture.Intel);
        requirements.Add(new ToolchainRequirement("macos-archs", "Architectures", null, offered.Count > 0,
            archs is null ? "arm64 and x86_64 assumed (the SDK's SDKSettings.json did not list them); the built binary is checked either way"
                          : $"The SDK builds {string.Join(", ", archs.Where(a => a is "arm64" or "x86_64"))}"));

        var codesign = report.Find(ToolchainIds.Codesign);
        requirements.Add(new ToolchainRequirement(ToolchainIds.Codesign, "codesign (verify / sign)", null, codesign?.IsAvailable == true, codesign?.ExecutablePath ?? codesign?.Detail));
        if (codesign?.IsAvailable != true) fixes.Add("codesign was not found. It comes with Xcode: " + XcodeHint);

        // This Mac can build for a newer macOS than it runs, but not launch the result.
        string? note = null;
        if (deployment is not null && report.Find(ToolchainIds.MacOSVersion) is { IsAvailable: true, Version: { } macOS } && SwiftCapabilities.Compare(macOS, deployment) < 0)
            note = $"This Mac runs macOS {macOS}; the app requires macOS {deployment} or later, so it builds here but cannot be launched on this Mac.";

        if (fixes.Count > 0)
            return new Assessment(SupportLevel.MissingToolchain, "Native macOS app, but this Mac is missing what xcodebuild needs.", fixes, requirements, offered, xcodebuild, note);
        return new Assessment(SupportLevel.Supported,
            $"Buildable on this Mac with Xcode {xcode!.VerifiedVersion} and the macOS {sdkVersion} SDK.", [], requirements, offered, xcodebuild, note);
    }

    /// <summary>The default architecture: this Mac's own (Apple silicon on an M-series Mac), when the SDK builds it.</summary>
    public static MacXcodeArchitecture DefaultArchitecture(HostEnvironment host, IReadOnlyList<MacXcodeArchitecture> offered)
    {
        var own = host.Architecture == TargetArchitecture.X64 ? MacXcodeArchitecture.Intel : MacXcodeArchitecture.AppleSilicon;
        return offered.Count == 0 || offered.Contains(own) ? own : offered[0];
    }

    /// <summary>"arm64", "x86_64", … from the SDK's SDKSettings.json (SupportedTargets.macosx.Archs); null when unreadable.</summary>
    public static IReadOnlyList<string>? SdkArchitectures(string sdkPath)
    {
        try
        {
            var file = Path.Combine(sdkPath, "SDKSettings.json");
            if (!File.Exists(file)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.TryGetProperty("SupportedTargets", out var targets) && targets.TryGetProperty("macosx", out var mac)
                && mac.TryGetProperty("Archs", out var archs) && archs.ValueKind == JsonValueKind.Array)
                return archs.EnumerateArray().Select(a => a.GetString()).OfType<string>().ToList();
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static string Major(string version) => version.Split('.')[0];
}
