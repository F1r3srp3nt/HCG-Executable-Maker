using System.Text.RegularExpressions;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.ProjectInspection.Swift;

public static partial class SwiftPackageScanner
{
    private sealed record EntitlementRule(string Capability, EntitlementEvidenceKind Kind, string? Sandbox, string? HardenedRuntime, string Explanation, Regex Pattern);

    /// <summary>
    /// Source evidence for App Sandbox (Mac App Store) and hardened-runtime (Developer ID) entitlements. Keys verified
    /// against Apple's entitlement reference (App Sandbox: Network / Hardware / App Data / File Access; Hardened Runtime:
    /// Resource Access). HCG proposes entitlements from evidence only; the user confirms them before anything is signed.
    /// </summary>
    private static readonly EntitlementRule[] EntitlementRules =
    [
        new("Outgoing network connections", EntitlementEvidenceKind.RequiresEntitlement, "com.apple.security.network.client", null,
            "Sandboxed apps need this to open outgoing connections; the hardened runtime needs no entitlement for it.",
            new Regex(@"\bURLSession\b|\bNWConnection\b|\bURLRequest\b|\bWebSocketTask\b|\bWKWebView\b", RegexOptions.Compiled)),
        new("Incoming network connections", EntitlementEvidenceKind.RequiresEntitlement, "com.apple.security.network.server", null,
            "Sandboxed apps need this to listen for connections.",
            new Regex(@"\bNWListener\b|\bbind\s*\(\s*\w+\s*,|\blisten\s*\(\s*\w+\s*,", RegexOptions.Compiled)),
        new("Microphone", EntitlementEvidenceKind.RequiresEntitlement, "com.apple.security.device.microphone", "com.apple.security.device.audio-input",
            "Recording audio needs the sandbox microphone entitlement (Mac App Store) or the hardened-runtime audio-input entitlement (Developer ID).",
            new Regex(@"AVCaptureDevice\.(?:requestAccess|authorizationStatus)\(\s*for:\s*\.audio|AVAudioRecorder\b|requestRecordPermission|\.inputNode\b|AVCaptureDevice\.default\([^)]*\.audio", RegexOptions.Compiled)),
        new("Camera", EntitlementEvidenceKind.RequiresEntitlement, "com.apple.security.device.camera", "com.apple.security.device.camera",
            "Capturing video or photos needs the camera entitlement.",
            new Regex(@"AVCaptureDevice\.(?:requestAccess|authorizationStatus)\(\s*for:\s*\.video|AVCaptureDevice\.default\([^)]*\.video", RegexOptions.Compiled)),
        new("Location", EntitlementEvidenceKind.RequiresEntitlement, "com.apple.security.personal-information.location", "com.apple.security.personal-information.location",
            "Location Services need the location entitlement.", new Regex(@"\bCLLocationManager\b", RegexOptions.Compiled)),
        new("Contacts", EntitlementEvidenceKind.RequiresEntitlement, "com.apple.security.personal-information.addressbook", "com.apple.security.personal-information.addressbook",
            "Reading contacts needs the address-book entitlement.", new Regex(@"\bCNContactStore\b", RegexOptions.Compiled)),
        new("Calendars", EntitlementEvidenceKind.RequiresEntitlement, "com.apple.security.personal-information.calendars", "com.apple.security.personal-information.calendars",
            "Calendar access needs the calendars entitlement.", new Regex(@"\bEKEventStore\b", RegexOptions.Compiled)),
        new("Photos library", EntitlementEvidenceKind.RequiresEntitlement, "com.apple.security.personal-information.photos-library", "com.apple.security.personal-information.photos-library",
            "Photos access needs the photos-library entitlement.", new Regex(@"\bPHPhotoLibrary\b", RegexOptions.Compiled)),
        new("Bluetooth", EntitlementEvidenceKind.RequiresEntitlement, "com.apple.security.device.bluetooth", null,
            "Sandboxed apps need this to use Bluetooth.", new Regex(@"\bCBCentralManager\b|\bCBPeripheralManager\b", RegexOptions.Compiled)),
        new("USB devices", EntitlementEvidenceKind.RequiresEntitlement, "com.apple.security.device.usb", null,
            "Sandboxed apps need this to talk to USB devices.", new Regex(@"\bIOServiceMatching\b|\bIOUSB\w+", RegexOptions.Compiled)),
        new("Printing", EntitlementEvidenceKind.RequiresEntitlement, "com.apple.security.print", null,
            "Sandboxed apps need this to print.", new Regex(@"\bNSPrintOperation\b|\bNSPrintInfo\b", RegexOptions.Compiled)),
        new("Files the user saves", EntitlementEvidenceKind.RequiresEntitlement, "com.apple.security.files.user-selected.read-write", null,
            "Sandboxed apps can write only to files the user picks in a Save or Open panel, and only with this entitlement.",
            new Regex(@"\bNSSavePanel\b|\.fileExporter\s*\(|\.fileMover\s*\(", RegexOptions.Compiled)),
        new("Files the user opens", EntitlementEvidenceKind.RequiresEntitlement, "com.apple.security.files.user-selected.read-only", null,
            "Sandboxed apps can read files the user picks in an Open panel (or drops on the app) with this entitlement.",
            new Regex(@"\bNSOpenPanel\b|\.fileImporter\s*\(|\.dropDestination\s*\(|\.onDrop\s*\(", RegexOptions.Compiled)),
        new("Downloads folder", EntitlementEvidenceKind.RequiresEntitlement, "com.apple.security.files.downloads.read-write", null,
            "Sandboxed apps reach ~/Downloads only with this entitlement.", new Regex(@"\.downloadsDirectory\b", RegexOptions.Compiled)),
        new("Apple Events (automation)", EntitlementEvidenceKind.Review, null, "com.apple.security.automation.apple-events",
            "Developer ID: the hardened runtime needs the apple-events entitlement. Mac App Store: sending Apple Events from the sandbox needs a scripting-targets or temporary-exception entitlement that App Review must approve; HCG does not add one.",
            new Regex(@"\bNSAppleScript\b|\bAEDeterminePermissionToAutomateTarget\b|\bNSAppleEventDescriptor\b|\bSBApplication\b", RegexOptions.Compiled)),
        new("Running other programs", EntitlementEvidenceKind.Review, null, null,
            "In the sandbox, child processes inherit the sandbox: tools outside the app bundle (e.g. /usr/bin/…) or that write outside the container usually fail. Review before submitting to the Mac App Store.",
            new Regex(@"\bProcess\s*\(\s*\)|\bNSTask\b|\bposix_spawn\b|\.executableURL\s*=", RegexOptions.Compiled)),
        new("Fixed paths outside the container", EntitlementEvidenceKind.Review, null, null,
            "In the sandbox the home directory is the app's container; hard-coded paths such as /Users/… or ~/Library/… point elsewhere or are denied.",
            new Regex("\"/Users/|\"~/|\"/Library/|NSHomeDirectory\\s*\\(\\)|homeDirectoryForCurrentUser", RegexOptions.Compiled)),
        new("Accessibility control of other apps", EntitlementEvidenceKind.SandboxBlocker, null, null,
            "Controlling other apps through the Accessibility API (AXUIElement / AXIsProcessTrusted) is not available to sandboxed Mac App Store apps.",
            new Regex(@"\bAXIsProcessTrusted\w*\b|\bAXUIElement\w*\b", RegexOptions.Compiled)),
        new("Privileged helper / authorization", EntitlementEvidenceKind.SandboxBlocker, null, null,
            "Installing privileged helpers or running code as root (SMJobBless, AuthorizationExecuteWithPrivileges) is not allowed for Mac App Store apps (App Review Guidelines 2.4.5).",
            new Regex(@"\bSMJobBless\b|\bAuthorizationExecuteWithPrivileges\b|\bSMAppService\.daemon\b", RegexOptions.Compiled)),
        new("Own update mechanism", EntitlementEvidenceKind.SandboxBlocker, null, null,
            "Mac App Store apps may be updated only through the Mac App Store (App Review Guidelines 2.4.5(vii)); a built-in updater such as Sparkle must not be in the store build.",
            new Regex(@"\bSPUStandardUpdaterController\b|\bSPUUpdater\b|\bSUUpdater\b|import\s+Sparkle\b", RegexOptions.Compiled)),
    ];
}
