namespace HCG.ExecutableMaker.Domain;

// HCG Signing Vault — local Apple signing assets, project signing configuration and pre-build validation.
// Nothing in this file holds a secret: private keys stay in the macOS Keychain, .p12 passwords are used once in memory
// and dropped, and App Store Connect .p8 keys are referenced, never stored here.

/// <summary>Where a signing asset is. A flag set: a certificate can be on this Mac and backed up at the same time.</summary>
[Flags]
public enum SigningAssetLocation
{
    None = 0,
    /// <summary>The certificate (and maybe its private key) is in this Mac's keychain, or the profile is installed here.</summary>
    ThisMac = 1,
    /// <summary>Non-secret metadata is recorded in the HCG account (never the private key; see the vault design).</summary>
    HcgAccountMetadata = 2,
    /// <summary>The HCG account records the asset on another of the user's Macs.</summary>
    AnotherMac = 4,
}

/// <summary>Whether this Mac can sign with an identity right now.</summary>
public enum SigningReadiness
{
    /// <summary>Certificate and matching private key are in this Mac's keychain, and the certificate is in its validity period.</summary>
    Ready,
    /// <summary>Only the certificate is here; its private key is not in any keychain on this Mac.</summary>
    PrivateKeyMissing,
    Expired,
    NotYetValid,
    /// <summary>macOS trust evaluation reported the certificate as revoked (from <c>security find-identity</c>).</summary>
    ReportedRevoked,
    /// <summary>macOS reported another trust problem (e.g. the Apple intermediate certificate is missing).</summary>
    TrustProblem,
    /// <summary>Not on this Mac at all (known only from the HCG account). Never ready for signing.</summary>
    NotOnThisMac,
}

/// <summary>Revocation is checked by Apple's services; HCG only reports what was actually verified.</summary>
public enum RevocationStatus
{
    /// <summary>No revocation check was made or its result is unknown. Never shown as "not revoked".</summary>
    NotChecked,
    /// <summary>macOS trust evaluation on this Mac reported the certificate revoked.</summary>
    ReportedRevoked,
}

/// <summary>An Apple developer certificate, with or without its private key. Fingerprints are upper-case hex.</summary>
public sealed record AppleCertificateInfo
{
    public required string Sha1 { get; init; }
    public required string Sha256 { get; init; }
    public required string CommonName { get; init; }
    public AppleCertificateKind Kind { get; init; } = AppleCertificateKind.Other;
    /// <summary>Apple Team ID (the subject's OU), e.g. "ABCDE12345".</summary>
    public string? TeamId { get; init; }
    /// <summary>The subject's O (team or person name).</summary>
    public string? TeamName { get; init; }
    public string? IssuerCommonName { get; init; }
    public string? SerialNumber { get; init; }
    public DateTimeOffset NotBefore { get; init; }
    public DateTimeOffset NotAfter { get; init; }
    /// <summary>Whether a matching private key is in a keychain on this Mac. Null when unknown (e.g. account-only metadata).</summary>
    public bool? HasPrivateKey { get; init; }
    public RevocationStatus Revocation { get; init; } = RevocationStatus.NotChecked;
    /// <summary>Problem macOS reported for the identity (e.g. CSSMERR_TP_NOT_TRUSTED), when any.</summary>
    public string? TrustProblem { get; init; }
    public SigningAssetLocation Location { get; init; } = SigningAssetLocation.ThisMac;
    /// <summary>For assets known from the HCG account: the label of the Mac that recorded it.</summary>
    public string? RecordedOnDevice { get; init; }

    public bool IsExpired(DateTimeOffset now) => NotAfter <= now;
    public bool IsInstaller => Kind is AppleCertificateKind.DeveloperIdInstaller or AppleCertificateKind.MacInstallerDistribution;

    /// <summary>Readiness is decided from this Mac's keychain only — never from the HCG account.</summary>
    public SigningReadiness Readiness(DateTimeOffset now) =>
        !Location.HasFlag(SigningAssetLocation.ThisMac) ? SigningReadiness.NotOnThisMac
        : Revocation == RevocationStatus.ReportedRevoked ? SigningReadiness.ReportedRevoked
        : NotAfter <= now ? SigningReadiness.Expired
        : NotBefore > now ? SigningReadiness.NotYetValid
        : HasPrivateKey != true ? SigningReadiness.PrivateKeyMissing
        : TrustProblem is not null ? SigningReadiness.TrustProblem
        : SigningReadiness.Ready;

    public string ShortSha1 => Sha1.Length > 8 ? Sha1[..8] : Sha1;
}

/// <summary>Provisioning profile type, decided from the profile's verified contents (not its file name).</summary>
public enum AppleProfileKind
{
    /// <summary>Development: device list, get-task-allow true.</summary>
    Development,
    /// <summary>iOS Ad Hoc (release testing): device list, distribution certificate.</summary>
    AdHoc,
    /// <summary>App Store (iOS or Mac App Store): no device list, distribution certificate.</summary>
    AppStore,
    /// <summary>In-house (Apple Developer Enterprise Program): ProvisionsAllDevices.</summary>
    Enterprise,
    /// <summary>macOS Developer ID profile (for restricted entitlements outside the Mac App Store).</summary>
    DeveloperId,
    Unknown,
}

/// <summary>Apple platform names as they appear in a profile's <c>Platform</c> array.</summary>
public static class ApplePlatformNames
{
    public const string Ios = "iOS";
    public const string MacOs = "OSX";
    public const string VisionOs = "xrOS";
    public const string TvOs = "tvOS";
    public const string WatchOs = "watchOS";

    public static readonly IReadOnlyList<string> Supported = [Ios, MacOs, VisionOs, TvOs, WatchOs];

    public static string Display(string platform) => platform switch
    {
        Ios => "iOS / iPadOS",
        MacOs => "macOS",
        VisionOs => "visionOS",
        TvOs => "tvOS",
        WatchOs => "watchOS",
        _ => platform,
    };
}

/// <summary>How far a profile's CMS envelope was verified.</summary>
public enum ProfileSignatureStatus
{
    /// <summary>The CMS signature is intact and the signer chains to a trusted Apple root on this system.</summary>
    VerifiedApple,
    /// <summary>The CMS signature is intact, but the signer does not chain to a trusted root here (contents still match the signature).</summary>
    IntactUntrustedSigner,
    /// <summary>The signature does not match the contents, or the file is not a CMS envelope.</summary>
    Invalid,
}

/// <summary>A provisioning profile read from its CMS-verified contents.</summary>
public sealed record AppleProvisioningProfileInfo
{
    /// <summary>The installed file; null for a profile known only from the HCG account.</summary>
    public string? Path { get; init; }
    public required string Uuid { get; init; }
    public required string Name { get; init; }
    public string? AppIdName { get; init; }
    public IReadOnlyList<string> TeamIds { get; init; } = [];
    public string? TeamName { get; init; }
    /// <summary>The application-identifier entitlement, e.g. "ABCDE12345.com.example.app" or "ABCDE12345.*".</summary>
    public string? ApplicationIdentifier { get; init; }
    public IReadOnlyList<string> Platforms { get; init; } = [];
    public DateTimeOffset? CreationDate { get; init; }
    public DateTimeOffset? Expiration { get; init; }
    public AppleProfileKind Kind { get; init; } = AppleProfileKind.Unknown;
    /// <summary>Entitlements with their values rendered as text (booleans, strings, arrays joined with ", ").</summary>
    public IReadOnlyDictionary<string, string> Entitlements { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> ProvisionedDevices { get; init; } = [];
    public bool ProvisionsAllDevices { get; init; }
    /// <summary>SHA-1 fingerprints (upper-case hex) of the certificates the profile allows.</summary>
    public IReadOnlyList<string> CertificateSha1s { get; init; } = [];
    public bool IsXcodeManaged { get; init; }
    public ProfileSignatureStatus Signature { get; init; } = ProfileSignatureStatus.Invalid;
    public string? SignerSubject { get; init; }
    public SigningAssetLocation Location { get; init; } = SigningAssetLocation.ThisMac;
    public string? RecordedOnDevice { get; init; }

    public string? TeamId => TeamIds.FirstOrDefault();
    public bool IsExpired(DateTimeOffset now) => Expiration is { } e && e <= now;
    public bool GetTaskAllow => Entitlements.TryGetValue("get-task-allow", out var v) && v == "true";

    /// <summary>The bundle-identifier part of the App ID ("com.example.app", "com.example.*" or "*").</summary>
    public string? BundleIdPattern =>
        ApplicationIdentifier is { } a && a.IndexOf('.') is var i and > 0 ? a[(i + 1)..] : null;

    /// <summary>
    /// Whether the profile's App ID covers <paramref name="bundleId"/> under Apple's rules: an explicit App ID must equal
    /// the bundle ID; a wildcard App ID "*" covers any, and "com.example.*" covers identifiers that start with "com.example.".
    /// The App ID prefix must be the profile's team.
    /// </summary>
    public bool Matches(string bundleId)
    {
        if (ApplicationIdentifier is not { } appId || BundleIdPattern is not { } pattern) return false;
        var prefix = appId[..appId.IndexOf('.')];
        if (TeamIds.Count > 0 && !TeamIds.Contains(prefix, StringComparer.Ordinal)) return false;
        return pattern == "*" || string.Equals(pattern, bundleId, StringComparison.Ordinal)
               || (pattern.EndsWith(".*", StringComparison.Ordinal) && bundleId.StartsWith(pattern[..^1], StringComparison.Ordinal) && bundleId.Length > pattern.Length - 1);
    }

    public bool IsWildcard => BundleIdPattern is { } p && p.EndsWith('*');

    public string DistributionDescription => Kind switch
    {
        AppleProfileKind.Development => $"Development — {ProvisionedDevices.Count} registered device(s)",
        AppleProfileKind.AdHoc => $"Ad Hoc (release testing) — {ProvisionedDevices.Count} registered device(s)",
        AppleProfileKind.AppStore => Platforms.Contains(ApplePlatformNames.MacOs) ? "Mac App Store" : "App Store / TestFlight",
        AppleProfileKind.Enterprise => "In-house (Enterprise) — all devices of the organisation",
        AppleProfileKind.DeveloperId => "Developer ID (direct distribution outside the Mac App Store)",
        _ => "Unknown distribution type",
    };
}

/// <summary>An identity listed by the keychain: a certificate that has a matching private key, and what macOS said about it.</summary>
public sealed record KeychainIdentityEntry(string Sha1, string Name, string? Problem);

/// <summary>What the macOS keychain reported. Certificates are DER bytes; private keys are never read.</summary>
public sealed record KeychainListing
{
    /// <summary>Apple developer certificates in the keychain search list (with or without a private key).</summary>
    public IReadOnlyList<byte[]> Certificates { get; init; } = [];
    /// <summary>Identities (certificate + private key), including invalid ones with the problem macOS reported.</summary>
    public IReadOnlyList<KeychainIdentityEntry> Identities { get; init; } = [];
    public string? Problem { get; init; }
}

public enum SigningImportStatus
{
    Imported,
    /// <summary>The certificate was imported, but its private key is not on this Mac (a .cer, or a .p12 without a key).</summary>
    ImportedCertificateOnly,
    /// <summary>Already on this Mac (same SHA-1 / profile UUID). Nothing was changed.</summary>
    Duplicate,
    WrongPassword,
    Cancelled,
    /// <summary>The file is not a valid certificate / .p12 / provisioning profile.</summary>
    Invalid,
    Expired,
    /// <summary>Valid, but not something HCG imports (e.g. not an Apple developer certificate, unsupported platform).</summary>
    Unsupported,
    /// <summary>The keychain refused the import (locked, access denied, user denied the prompt).</summary>
    KeychainError,
    /// <summary>Keychain operations need macOS.</summary>
    NotOnMac,
}

public sealed record SigningImportResult(SigningImportStatus Status, string Message)
{
    public AppleCertificateInfo? Certificate { get; init; }
    public AppleProvisioningProfileInfo? Profile { get; init; }
    /// <summary>What the user can do next (e.g. "Import the .p12 exported from the Mac that created the certificate").</summary>
    public string? Remedy { get; init; }
    public bool Succeeded => Status is SigningImportStatus.Imported or SigningImportStatus.ImportedCertificateOnly;
}

/// <summary>The signing routes HCG supports. Signing, notarization, uploading and publishing are separate operations.</summary>
public enum SigningRoute
{
    /// <summary>Run on this Mac or on registered development devices (Apple Development certificate).</summary>
    LocalDevelopment,
    /// <summary>iOS Ad Hoc / release testing to registered devices (Apple Distribution + Ad Hoc profile).</summary>
    IosAdHoc,
    /// <summary>iOS App Store / TestFlight build (Apple Distribution + App Store profile). Uploading is a separate step.</summary>
    IosAppStore,
    /// <summary>iOS in-house distribution (Apple Distribution of an Enterprise team + In-house profile).</summary>
    IosEnterprise,
    /// <summary>macOS direct distribution (Developer ID Application; Developer ID Installer for .pkg). Notarization is separate.</summary>
    MacDeveloperId,
    /// <summary>Mac App Store (Apple Distribution / 3rd Party Mac Developer Application + Mac Installer Distribution + Mac App Store profile).</summary>
    MacAppStore,
}

public enum SigningPlatform
{
    Ios,
    MacOs,
}

public enum ProjectSigningMode
{
    /// <summary>Xcode-managed automatic signing with the team; Apple Account authentication stays in Xcode.</summary>
    XcodeAutomatic,
    /// <summary>Manual: the user picks the identity and (where needed) the provisioning profile.</summary>
    Manual,
}

/// <summary>Where imported assets go. Importing to this Mac is the default; account backup is an explicit opt-in.</summary>
public enum SigningBackupChoice
{
    ThisMacOnly,
    /// <summary>Also record non-secret metadata in the HCG account (private keys need the encrypted vault, which is not available yet).</summary>
    HcgAccountMetadata,
}

/// <summary>Signing for one target (the app, an app extension, a watch app, a helper) with its own bundle ID.</summary>
public sealed record TargetSigningConfiguration
{
    public required string TargetName { get; init; }
    public string? BundleIdentifier { get; init; }
    /// <summary>App extension, watch app, App Clip or helper embedded in the main app.</summary>
    public bool IsEmbedded { get; init; }
    /// <summary>Manual signing: this target's provisioning profile UUID (null = none / Xcode decides).</summary>
    public string? ProfileUuid { get; init; }
    /// <summary>Entitlement keys the target requests (from its .entitlements file), with values as text.</summary>
    public IReadOnlyDictionary<string, string> Entitlements { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Non-secret signing settings for one project, persisted locally (and, if the user opts in, as account metadata).
/// Only references: team ID, certificate SHA-1, profile UUIDs. No keys, passwords or Apple Account credentials.
/// </summary>
public sealed record ProjectSigningConfiguration
{
    public required string ProjectId { get; init; }
    public SigningPlatform Platform { get; init; } = SigningPlatform.Ios;
    public ProjectSigningMode Mode { get; init; } = ProjectSigningMode.XcodeAutomatic;
    public SigningRoute Route { get; init; } = SigningRoute.LocalDevelopment;
    public string? TeamId { get; init; }
    /// <summary>Manual: SHA-1 of the application signing identity.</summary>
    public string? IdentitySha1 { get; init; }
    /// <summary>Manual, .pkg routes: SHA-1 of the installer identity (Developer ID Installer / Mac Installer Distribution).</summary>
    public string? InstallerIdentitySha1 { get; init; }
    /// <summary>Manual: the main target's profile UUID.</summary>
    public string? ProfileUuid { get; init; }
    public IReadOnlyList<TargetSigningConfiguration> Targets { get; init; } = [];
    /// <summary>Device UDIDs the user intends to install on (Development / Ad Hoc); checked against the profile.</summary>
    public IReadOnlyList<string> IntendedDevices { get; init; } = [];
    public SigningBackupChoice Backup { get; init; } = SigningBackupChoice.ThisMacOnly;
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>Non-secret account record of an asset (from the HCG account; see <c>docs/SIGNING-VAULT.md</c>).</summary>
public sealed record AccountSigningAssetRecord
{
    public required string Id { get; init; }
    /// <summary>"certificate" or "profile".</summary>
    public required string AssetType { get; init; }
    public string? Sha1 { get; init; }
    public string? Sha256 { get; init; }
    public string? ProfileUuid { get; init; }
    public string? DisplayName { get; init; }
    public string? TeamId { get; init; }
    public string? CertificateKind { get; init; }
    public string? ProfileKind { get; init; }
    public string? BundleIdPattern { get; init; }
    public IReadOnlyList<string> Platforms { get; init; } = [];
    public DateTimeOffset? ExpiresAt { get; init; }
    public string? DeviceId { get; init; }
    public string? DeviceLabel { get; init; }
}

/// <summary>Whether a check was made on this Mac, or needs Apple's services (and so was not made by HCG).</summary>
public enum SigningCheckScope
{
    Local,
    RequiresApple,
}

public enum SigningCheckOutcome
{
    Passed,
    Warning,
    Failed,
    /// <summary>Not verified (needs Apple's services, or information HCG does not have). Never counted as passed.</summary>
    NotVerified,
    /// <summary>Does not apply to this route (e.g. a profile for a Developer ID app without restricted entitlements).</summary>
    NotApplicable,
}

/// <summary>Category of a failed check — signing assets are never reported as a "missing toolchain".</summary>
public enum SigningCheckCategory
{
    Certificate,
    PrivateKey,
    Profile,
    Team,
    BundleIdentifier,
    Entitlements,
    Devices,
    Platform,
    Route,
    Tools,
}

public sealed record SigningCheck(
    string Title,
    SigningCheckCategory Category,
    SigningCheckScope Scope,
    SigningCheckOutcome Outcome,
    string Detail,
    string? Remedy = null,
    string? Target = null);

public sealed record SigningValidationReport
{
    public required ProjectSigningConfiguration Configuration { get; init; }
    public IReadOnlyList<SigningCheck> Checks { get; init; } = [];
    public DateTimeOffset CheckedAt { get; init; }

    public bool HasFailures => Checks.Any(c => c.Outcome == SigningCheckOutcome.Failed);
    public IEnumerable<SigningCheck> Failures => Checks.Where(c => c.Outcome == SigningCheckOutcome.Failed);
    public IEnumerable<SigningCheck> RequiringApple => Checks.Where(c => c.Scope == SigningCheckScope.RequiresApple);
}

/// <summary>
/// App Store Connect API key reference — separate from signing identities. An API key authorises App Store Connect API
/// requests (JWT, ES256); it is not an app-signing key and is not needed for local signing.
/// </summary>
public enum AppStoreConnectKeyType
{
    /// <summary>Team key: created by an Admin; role chosen per key; JWT uses <c>iss</c> = Issuer ID.</summary>
    Team,
    /// <summary>Individual key: tied to one user's access; no Issuer ID (JWT uses <c>sub</c> = "user"); cannot use Provisioning endpoints or notarytool.</summary>
    Individual,
}

public sealed record AppStoreConnectApiKeyReference
{
    public required string KeyId { get; init; }
    public AppStoreConnectKeyType Type { get; init; } = AppStoreConnectKeyType.Team;
    /// <summary>Required for team keys; must be absent for individual keys.</summary>
    public string? IssuerId { get; init; }
    /// <summary>Where the .p8 private key lives (a keychain item name or a file path the user chose). The key itself is never stored by HCG.</summary>
    public string? PrivateKeyLocation { get; init; }
}
