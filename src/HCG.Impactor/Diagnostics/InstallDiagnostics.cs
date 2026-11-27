using System.Text.RegularExpressions;
using HCG.ExecutableMaker.Security;

namespace HCG.Impactor.Diagnostics;

/// <summary>The installation problems HCG Impactor recognises and explains.</summary>
public enum DiagnosisKind
{
    InvalidSignature,
    ResourcesModified,
    MissingProvisioningProfile,
    ExpiredCertificate,
    UnregisteredDevice,
    IncompatibleOsVersion,
    UnsupportedArchitecture,
    EntitlementMismatch,
    BundleIdentifierMismatch,
    DeviceLocked,
    DeveloperModeRequired,
    DeviceNotPaired,
    XcodeCompatibility,
    InsufficientStorage,
    DeviceNotFound,
    EncryptedBinary,
    ToolMissing,
    /// <summary>Xcode automatic provisioning (App ID / device / profile creation) failed.</summary>
    ProvisioningFailed,
    Cancelled,
    TimedOut,
    Unknown,
}

/// <summary>A failure translated into plain language, with the original output (redacted) kept as evidence.</summary>
public sealed record Diagnosis(DiagnosisKind Kind, string Title, string Explanation, string Remedy)
{
    /// <summary>The line of the original output that identified the problem (redacted).</summary>
    public string? Evidence { get; init; }
    /// <summary>The complete original output, redacted (credentials, UDIDs, ECIDs, serial numbers, e-mail addresses, home folders).</summary>
    public string RawRedacted { get; init; } = "";
    /// <summary>Other problems the output also mentions.</summary>
    public IReadOnlyList<DiagnosisKind> AlsoMentioned { get; init; } = [];
}

/// <summary>
/// Translates devicectl / CoreDevice / MobileDevice / codesign failures into explanations and next steps. Matching is
/// on Apple's published error texts and the MobileDevice codes Apple's tools print (0xe8008015 …). When nothing matches,
/// the result is <see cref="DiagnosisKind.Unknown"/> with the raw output — HCG never guesses a cause.
/// </summary>
public static partial class InstallDiagnostics
{
    private sealed record Rule(DiagnosisKind Kind, Regex Pattern, string Title, string Explanation, string Remedy);

    // Order matters: the most specific causes first.
    private static readonly Rule[] Rules =
    [
        new(DiagnosisKind.ResourcesModified, new(@"0xe8008017|signed resource has been added, modified, or deleted", RegexOptions.IgnoreCase),
            "Files changed after signing",
            "The app's files no longer match its signature: something was added, edited or removed after it was signed.",
            "Re-sign the app in Signing Studio (it seals the current contents), then install again."),
        new(DiagnosisKind.BundleIdentifierMismatch, new(@"MismatchedApplicationIdentifierEntitlement|application-identifier entitlement[^\n]*(?:does not match|mismatch)|upgrade's application-identifier", RegexOptions.IgnoreCase),
            "Installed copy belongs to another team or identifier",
            "An app with this bundle identifier is already on the device, signed with a different application identifier (another team or certificate). iOS refuses to replace it.",
            "Either install under a new bundle identifier (Signing Studio › Advanced), or remove the existing app on the device yourself — removing it deletes its data. HCG never removes apps."),
        new(DiagnosisKind.MissingProvisioningProfile, new(@"0xe8008015|valid provisioning profile for this executable was not found|no (?:valid )?provisioning profile|NoProvisioningProfile", RegexOptions.IgnoreCase),
            "No valid provisioning profile for this device",
            "The device did not find a provisioning profile that allows this app to run on it: the profile is missing, expired, for another App ID, or does not list this device.",
            "In Provisioning Manager check that the profile is valid, covers the bundle identifier and includes this device; register the device at developer.apple.com › Devices, regenerate the profile and sign again."),
        new(DiagnosisKind.UnregisteredDevice, new(@"(?:device|UDID)[^\n]{0,80}(?:is not|isn't|not) (?:included|registered|provisioned)|not (?:in|part of) the provisioning profile", RegexOptions.IgnoreCase),
            "Device not registered",
            "This device's UDID is not in the provisioning profile used to sign the app.",
            "Register the device at developer.apple.com › Devices (or connect it while Xcode is open), regenerate and download the profile, then sign again."),
        new(DiagnosisKind.EntitlementMismatch, new(@"0xe8008016|signed with invalid entitlements|entitlements?[^\n]{0,80}(?:not (?:allowed|permitted|supported|valid)|invalid)|EntitlementMismatch", RegexOptions.IgnoreCase),
            "Entitlements not allowed",
            "The app requests entitlements (capabilities) that its provisioning profile does not grant.",
            "Enable the capability for the App ID at developer.apple.com › Identifiers and regenerate the profile, or sign with \"Remove unsupported entitlements\"."),
        new(DiagnosisKind.ExpiredCertificate, new(@"0xe8008018|identity used to sign the executable is no longer valid|certificate (?:has )?expired|CSSMERR_TP_CERT_EXPIRED|certificate is not valid", RegexOptions.IgnoreCase),
            "Signing certificate expired or revoked",
            "The certificate that signed the app is no longer valid (expired or revoked).",
            "Create a current certificate in Xcode › Settings › Accounts › Manage Certificates, download a profile that includes it, and re-sign. HCG does not revoke or replace certificates."),
        new(DiagnosisKind.InvalidSignature, new(@"0xe8008019|0xe800801c|does not have a valid signature|No code signature found|code ?signature[^\n]{0,60}(?:invalid|not valid|verification failed)|Failed to verify code signature|ApplicationVerificationFailed|code object is not signed", RegexOptions.IgnoreCase),
            "Invalid signature",
            "The device could not verify the app's code signature (unsigned, ad hoc signed, or signed incorrectly).",
            "Sign the app in Signing Studio with an Apple Development or Apple Distribution identity and a matching profile, then install the signed IPA."),
        new(DiagnosisKind.EncryptedBinary, new(@"FairPlay|cryptid|encrypted binary", RegexOptions.IgnoreCase),
            "Encrypted App Store binary",
            "The app's code is encrypted with App Store DRM; a re-signed copy cannot run.",
            "Install it from the App Store. For your own app, export a Development or Ad Hoc IPA from the Xcode archive."),
        new(DiagnosisKind.IncompatibleOsVersion, new(@"requires a newer version of (?:iOS|iPadOS)|OSVersionTooLow|MinimumOSVersion|minimum (?:OS|iOS) version|is not compatible with this (?:iOS|device) version", RegexOptions.IgnoreCase),
            "iOS version too old",
            "The app's minimum iOS version is higher than the version the device runs.",
            "Update the device in Settings › General › Software Update, or rebuild the app with a lower deployment target."),
        new(DiagnosisKind.UnsupportedArchitecture, new(@"IncompatibleArchitecture|incompatible (?:cpu|architecture)|bad CPU type|does not support (?:the device's|this) architecture|no matching architecture", RegexOptions.IgnoreCase),
            "Unsupported architecture",
            "The app's binary does not contain code for this device's processor.",
            "Build for arm64 (Xcode's default for devices) and export the IPA again."),
        new(DiagnosisKind.DeviceLocked, new(@"0xe80000e2|device is (?:passcode )?locked|passcode protected|(?:must|needs to) be unlocked|unlock (?:the|your) device|PasswordProtected", RegexOptions.IgnoreCase),
            "Device locked",
            "The device is locked; iOS does not allow installation until it is unlocked.",
            "Unlock the device, keep the screen on, and try again."),
        new(DiagnosisKind.DeveloperModeRequired, new(@"Developer Mode (?:is )?(?:disabled|not enabled|off|required)|enable Developer Mode|developerModeStatus[^\n]{0,20}disabled", RegexOptions.IgnoreCase),
            "Developer Mode required",
            "iOS 16 and later only run development- and ad hoc-signed apps when Developer Mode is on.",
            "On the device: Settings › Privacy & Security › Developer Mode › On, restart, unlock and confirm Turn On. If the option is missing, connect the device to Xcode once."),
        new(DiagnosisKind.DeviceNotPaired, new(@"not paired|pairing (?:failed|required|is required)|InvalidHostID|trust (?:this|the) computer|untrusted host|could not establish a trusted connection", RegexOptions.IgnoreCase),
            "Device not paired",
            "The device has not trusted this Mac, or the trust was reset.",
            "Unlock the device, connect it with a cable, tap Trust and enter the passcode. If no prompt appears: Settings › General › Transfer or Reset › Reset › Reset Location & Privacy, then reconnect."),
        new(DiagnosisKind.InsufficientStorage, new(@"not enough (?:free )?(?:storage|space)|insufficient (?:storage|space)|NoSpace|ENOSPC|No space left on device", RegexOptions.IgnoreCase),
            "Not enough storage",
            "The device (or this Mac's temporary folder) does not have enough free space for the app.",
            "Free space on the device (Settings › General › iPhone Storage) and on this Mac, then try again."),
        new(DiagnosisKind.XcodeCompatibility, new(@"developer disk image|DeveloperDiskImage|device support files|not supported by this version of Xcode|(?:update|newer version of) Xcode|Xcode[^\n]{0,40}(?:too old|does not support)|CoreDevice(?:Service)?[^\n]{0,40}(?:unavailable|version)|unable to find devicectl|devicectl: command not found", RegexOptions.IgnoreCase),
            "Xcode and device are not compatible",
            "This Mac's Xcode cannot prepare the device for development (its iOS version is newer than Xcode supports, or Xcode's device services are not installed).",
            "Update Xcode, open it once to install additional components, then connect the device and wait for \"Preparing device\" to finish in Window › Devices and Simulators."),
        new(DiagnosisKind.DeviceNotFound, new(@"No devices? (?:were )?(?:found|matched)|device[^\n]{0,60}(?:not found|could not be found|was not found)|Unable to locate (?:a )?device|no device (?:was )?found matching", RegexOptions.IgnoreCase),
            "Device not found",
            "The device is not connected or not visible to this Mac's CoreDevice service.",
            "Connect the device by cable, unlock it, and refresh the Device Manager."),
    ];

    /// <summary>Diagnoses the combined output (stdout, stderr, JSON) of a failed step.</summary>
    public static Diagnosis Diagnose(string output, bool cancelled = false, bool timedOut = false, bool toolMissing = false)
    {
        var redacted = Redact(output);
        if (toolMissing)
            return new Diagnosis(DiagnosisKind.ToolMissing, "Apple tools not available",
                "devicectl (part of Xcode 15 or later) was not found, so HCG cannot reach devices from this computer.",
                OperatingSystem.IsMacOS() ? "Install Xcode from the App Store, open it once, and select it in Toolchains (xcode-select)." : "Device installation needs a Mac with Xcode. On Windows and Linux, HCG Impactor inspects IPAs and checks signing plans; install from a Mac.")
            { RawRedacted = redacted };
        if (cancelled) return new Diagnosis(DiagnosisKind.Cancelled, "Cancelled", "The operation was cancelled before it finished.", "Nothing else was changed. Start it again when ready.") { RawRedacted = redacted };

        var matches = Rules.Select(r => (r, m: r.Pattern.Match(redacted))).Where(x => x.m.Success).ToList();
        if (matches.Count == 0)
            return timedOut
                ? new Diagnosis(DiagnosisKind.TimedOut, "Timed out", "The device did not answer in time.", "Keep the device unlocked and connected, then try again. Large apps over Wi-Fi can take several minutes.") { RawRedacted = redacted }
                : new Diagnosis(DiagnosisKind.Unknown, "Installation failed", "Apple's tools reported an error HCG does not recognise. The original output is below.",
                    "Export the diagnostics and check the first error line. If it mentions signing, re-sign; if it mentions the device, reconnect and unlock it.")
                { RawRedacted = redacted, Evidence = FirstErrorLine(redacted) };
        var (rule, match) = matches[0];
        return new Diagnosis(rule.Kind, rule.Title, rule.Explanation, rule.Remedy)
        {
            Evidence = LineOf(redacted, match.Index),
            RawRedacted = redacted,
            AlsoMentioned = matches.Skip(1).Select(x => x.r.Kind).Distinct().ToList(),
        };
    }

    /// <summary>
    /// Removes credentials (via <see cref="SecretRedactor"/>) and device-identifying data: UDIDs, ECIDs, serial numbers,
    /// e-mail addresses and user names in home-folder paths. CoreDevice UUIDs are kept partially (first/last 4).
    /// </summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var s = SecretRedactor.Redact(text);
        s = UdidModern().Replace(s, m => m.Value[..4] + "…" + m.Value[^4..]);
        s = UdidLegacy().Replace(s, m => m.Value[..4] + "…" + m.Value[^4..]);
        s = Ecid().Replace(s, m => m.Groups[1].Value + "[ECID]");
        s = Serial().Replace(s, m => m.Groups[1].Value + "[SERIAL]");
        s = Email().Replace(s, "[EMAIL]");
        s = HomeMac().Replace(s, "/Users/[user]");
        s = HomeLinux().Replace(s, "/home/[user]");
        return s;
    }

    private static string LineOf(string text, int index)
    {
        var start = text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        var end = text.IndexOf('\n', index);
        var line = text[start..(end < 0 ? text.Length : end)].Trim();
        return line.Length > 300 ? line[..300] + "…" : line;
    }

    private static string? FirstErrorLine(string text) =>
        text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Contains("error", StringComparison.OrdinalIgnoreCase) || l.Contains("failed", StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"\b[0-9A-Fa-f]{8}-[0-9A-Fa-f]{16}\b")]
    private static partial Regex UdidModern();
    [GeneratedRegex(@"\b[0-9a-fA-F]{40}\b")]
    private static partial Regex UdidLegacy();
    [GeneratedRegex(@"(?i)(""?ecid""?\s*[:=]\s*""?)(?:0x)?[0-9A-Fa-f]{6,20}")]
    private static partial Regex Ecid();
    [GeneratedRegex(@"(?i)(""?serial(?:Number|_number| number)?""?\s*[:=]\s*""?)[A-Z0-9]{8,14}")]
    private static partial Regex Serial();
    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();
    [GeneratedRegex(@"/Users/[^/\s""']+")]
    private static partial Regex HomeMac();
    [GeneratedRegex(@"/home/[^/\s""']+")]
    private static partial Regex HomeLinux();
}
