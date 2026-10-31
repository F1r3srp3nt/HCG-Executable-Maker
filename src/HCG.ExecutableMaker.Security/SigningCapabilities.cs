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
        _ => ToolsAvailable ? "Available" : "Required tools missing",
    };
}

/// <summary>
/// Reports which signing/notarization operations could exist on this host. M0.1 implements none of them:
/// every artifact HCG produces is unsigned (except the ad-hoc signature the .NET SDK itself applies to macOS apphosts).
/// </summary>
public static class SigningCapabilityCatalog
{
    public static IReadOnlyList<SigningCapability> Evaluate(ToolchainReport report)
    {
        var host = report.Host.Platform;
        SigningCapability Make(string id, string name, TargetPlatform p, string desc, string[] tools) => new(
            id, name, p, desc, tools,
            ToolsAvailable: tools.All(report.IsAvailable),
            HostSupported: host == p,
            SigningImplementationStatus.NotImplemented,
            "M0.4");

        return
        [
            Make("authenticode", "Windows Authenticode", TargetPlatform.Windows,
                "Sign .exe/.dll/.msi with a code-signing certificate via signtool.", [ToolchainIds.SignTool]),
            Make("msix-store", "Microsoft Store (MSIX)", TargetPlatform.Windows,
                "Package and sign MSIX for Partner Center submission.", [ToolchainIds.MakeAppx, ToolchainIds.SignTool]),
            Make("developer-id", "Apple Developer ID", TargetPlatform.MacOS,
                "Sign .app bundles and .dmg with a Developer ID Application certificate (hardened runtime).", [ToolchainIds.Codesign]),
            Make("notarization", "Apple notarization", TargetPlatform.MacOS,
                "Submit signed artifacts to Apple's notary service and staple the ticket.", [ToolchainIds.Notarytool]),
            Make("mac-app-store", "Mac App Store", TargetPlatform.MacOS,
                "Sandboxed, App Store–signed .pkg for App Store Connect.", [ToolchainIds.Codesign, ToolchainIds.Productbuild]),
        ];
    }
}
