using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.Security;

public enum SigningImplementationStatus
{
    NotImplemented,
    Implemented,
}

public sealed record SigningCapability(
    string Id,
    string DisplayName,
    TargetPlatform Platform,
    string Description,
    IReadOnlyList<string> RequiredToolchainIds,
    bool ToolsAvailable,
    bool HostSupported,
    SigningImplementationStatus Implementation,
    string PlannedMilestone)
{
    /// <summary>Honest one-line status for display.</summary>
    public string StatusText => Implementation switch
    {
        SigningImplementationStatus.NotImplemented when !HostSupported =>
            $"Not implemented (planned {PlannedMilestone}). Requires a {Platform.DisplayName()} host.",
        SigningImplementationStatus.NotImplemented when ToolsAvailable =>
            $"Not implemented (planned {PlannedMilestone}). Required tools detected on this host.",
        SigningImplementationStatus.NotImplemented =>
            $"Not implemented (planned {PlannedMilestone}). Required tools not detected.",
        _ when !HostSupported => $"Implemented. Requires a {Platform.DisplayName()} host.",
        _ => ToolsAvailable ? "Implemented; tools detected. Certificates, profiles and credentials are checked per build (New Build › Package › macOS distribution)." : "Implemented; required tools missing on this host.",
    };
}

/// <summary>
/// Reports which signing/notarization operations exist and whether this host has their tools. The macOS operations
/// (Developer ID and notarization for Swift and .NET apps and for already-built apps; the Mac App Store for Swift apps) are
/// implemented.
/// </summary>
public static class SigningCapabilityCatalog
{
    public static IReadOnlyList<SigningCapability> Evaluate(ToolchainReport report)
    {
        var host = report.Host.Platform;
        SigningCapability Make(string id, string name, TargetPlatform p, string desc, string[] tools, bool implemented = false) => new(
            id, name, p, desc, tools,
            ToolsAvailable: tools.All(report.IsAvailable),
            HostSupported: host == p,
            implemented ? SigningImplementationStatus.Implemented : SigningImplementationStatus.NotImplemented,
            implemented ? "" : "M0.4");

        return
        [
            Make("authenticode", "Windows Authenticode", TargetPlatform.Windows,
                "Sign .exe/.dll/.msi with a code-signing certificate via signtool.", [ToolchainIds.SignTool]),
            Make("msix-store", "Microsoft Store (MSIX)", TargetPlatform.Windows,
                "Package and sign MSIX for Partner Center submission.", [ToolchainIds.MakeAppx, ToolchainIds.SignTool]),
            Make("developer-id", "Apple Developer ID", TargetPlatform.MacOS,
                "Swift and .NET apps in a build, and any already-built .app on Sign an existing app: sign every Mach-O inside out and the .app with a Developer ID Application certificate (hardened runtime, secure timestamp, reviewed entitlements); sign the .pkg with Developer ID Installer.", [ToolchainIds.Codesign], implemented: true),
            Make("notarization", "Apple notarization", TargetPlatform.MacOS,
                "Explicit step after Developer ID signing: notarytool submit with a keychain profile, staple, Gatekeeper check (Build results › Notarize, or Sign an existing app).", [ToolchainIds.Notarytool], implemented: true),
            Make("mac-app-store", "Mac App Store", TargetPlatform.MacOS,
                "Swift apps: sandboxed, Apple Distribution signed app with embedded profile in a Mac Installer Distribution signed .pkg; validation explicit, upload manual.", [ToolchainIds.Codesign, ToolchainIds.Productbuild], implemented: true),
        ];
    }
}
