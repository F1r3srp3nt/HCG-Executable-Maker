namespace HCG.ExecutableMaker.Domain;

/// <summary>How the macOS .app (and every container that wraps it: ZIP, DMG, .pkg) is signed.</summary>
public enum MacSigningMode
{
    /// <summary>Ad-hoc signature (<c>codesign --sign -</c>): runs on this Mac; other Macs show a Gatekeeper warning. No certificate needed.</summary>
    AdHoc,
    /// <summary>Direct distribution outside the Mac App Store: Developer ID Application certificate, hardened runtime, secure timestamp; notarization afterwards.</summary>
    DeveloperId,
}

/// <summary>Apple certificate types relevant to macOS distribution, recognised by the identity's common name.</summary>
public enum AppleCertificateKind
{
    /// <summary>"Developer ID Application: Name (TEAMID)" — signs apps for direct distribution (notarization).</summary>
    DeveloperIdApplication,
    /// <summary>"Developer ID Installer: Name (TEAMID)" — signs .pkg installers for direct distribution.</summary>
    DeveloperIdInstaller,
    /// <summary>"Apple Distribution: Name (TEAMID)" — signs apps for the App Store.</summary>
    AppleDistribution,
    /// <summary>"3rd Party Mac Developer Application: Name (TEAMID)" (Mac App Distribution, older certificate type) — signs Mac App Store apps.</summary>
    MacAppDistribution,
    /// <summary>"3rd Party Mac Developer Installer: Name (TEAMID)" / "Mac Installer Distribution" — signs the Mac App Store .pkg.</summary>
    MacInstallerDistribution,
    /// <summary>"Apple Development:" / "Mac Developer:" — development only; not for distribution.</summary>
    Development,
    Other,
}

/// <summary>A valid signing identity in the user's keychains (name and SHA-1 only; private keys never leave the keychain).</summary>
public sealed record SigningIdentity(string Sha1, string Name, AppleCertificateKind Kind, string? TeamId)
{
    public bool IsInstaller => Kind is AppleCertificateKind.DeveloperIdInstaller or AppleCertificateKind.MacInstallerDistribution;
    public bool SignsMacAppStoreApps => Kind is AppleCertificateKind.AppleDistribution or AppleCertificateKind.MacAppDistribution;
    public string Display => $"{Name} — {Sha1[..Math.Min(8, Sha1.Length)]}…";
}

public enum MacProfileKind
{
    Development,
    DeveloperId,
    MacAppStore,
    Unknown,
}

/// <summary>A macOS provisioning profile (*.provisionprofile) on this Mac.</summary>
public sealed record MacProvisioningProfile
{
    public required string Path { get; init; }
    public string? Name { get; init; }
    public string? Uuid { get; init; }
    public string? TeamId { get; init; }
    public string? TeamName { get; init; }
    /// <summary>The application-identifier entitlement, e.g. "ABCDE12345.com.example.app" or "ABCDE12345.*".</summary>
    public string? ApplicationIdentifier { get; init; }
    public DateTimeOffset? Expiration { get; init; }
    public MacProfileKind Kind { get; init; } = MacProfileKind.Unknown;
    public IReadOnlyList<string> Platforms { get; init; } = [];
    /// <summary>Entitlement keys the profile authorises (values are not needed by HCG).</summary>
    public IReadOnlyList<string> EntitlementKeys { get; init; } = [];
    /// <summary>SHA-1 fingerprints of the certificates the profile allows to sign.</summary>
    public IReadOnlyList<string> CertificateSha1s { get; init; } = [];

    public bool IsExpired(DateTimeOffset now) => Expiration is { } e && e <= now;

    /// <summary>Whether the profile's App ID covers <paramref name="bundleId"/> (exact or wildcard).</summary>
    public bool Matches(string bundleId)
    {
        if (ApplicationIdentifier is not { } appId || TeamId is null || !appId.StartsWith(TeamId + ".", StringComparison.Ordinal)) return false;
        var pattern = appId[(TeamId.Length + 1)..];
        return pattern == "*" || pattern == bundleId
               || (pattern.EndsWith(".*", StringComparison.Ordinal) && bundleId.StartsWith(pattern[..^1], StringComparison.Ordinal));
    }

    public string Display => $"{Name ?? System.IO.Path.GetFileName(Path)} ({Kind}, {ApplicationIdentifier}, expires {Expiration:yyyy-MM-dd})";
}

/// <summary>Signing material found on this Mac. Detection is read-only.</summary>
public sealed record MacSigningInventory
{
    public IReadOnlyList<SigningIdentity> Identities { get; init; } = [];
    public IReadOnlyList<MacProvisioningProfile> Profiles { get; init; } = [];
    /// <summary>Why detection failed or was incomplete (e.g. not a Mac, security tool error).</summary>
    public string? Problem { get; init; }
    public DateTimeOffset DetectedAt { get; init; }

    public IEnumerable<SigningIdentity> OfKind(params AppleCertificateKind[] kinds) => Identities.Where(i => kinds.Contains(i.Kind));
    public SigningIdentity? Find(string? sha1) => sha1 is null ? null : Identities.FirstOrDefault(i => string.Equals(i.Sha1, sha1, StringComparison.OrdinalIgnoreCase));
}

/// <summary>What a source finding means for the App Sandbox / hardened runtime.</summary>
public enum EntitlementEvidenceKind
{
    /// <summary>The feature works once the listed entitlement is granted.</summary>
    RequiresEntitlement,
    /// <summary>Works differently in the sandbox; the user must review (no entitlement is added automatically).</summary>
    Review,
    /// <summary>Not possible in a Mac App Store (sandboxed) app.</summary>
    SandboxBlocker,
}

/// <summary>One piece of evidence from the sources (read as text) about sandbox / hardened-runtime needs.</summary>
/// <param name="Capability">Short name, e.g. "Outgoing network connections".</param>
/// <param name="SandboxEntitlement">Entitlement needed in the App Sandbox (Mac App Store), if any.</param>
/// <param name="HardenedRuntimeEntitlement">Entitlement needed under the hardened runtime (Developer ID), if any.</param>
/// <param name="Evidence">"file: matched code".</param>
public sealed record EntitlementEvidence(
    string Capability,
    EntitlementEvidenceKind Kind,
    string? SandboxEntitlement,
    string? HardenedRuntimeEntitlement,
    string Evidence,
    string Explanation);

/// <summary>Direct distribution / Mac App Store settings, persisted with the build.</summary>
public sealed record MacDistributionOptions
{
    public MacSigningMode SigningMode { get; init; } = MacSigningMode.AdHoc;
    /// <summary>SHA-1 of the Developer ID Application identity (required for <see cref="MacSigningMode.DeveloperId"/>).</summary>
    public string? DeveloperIdApplicationSha1 { get; init; }
    /// <summary>SHA-1 of the installer identity for the .pkg; null = unsigned .pkg for local testing.</summary>
    public string? PkgInstallerSha1 { get; init; }
    /// <summary>Common name of that identity (productbuild --sign takes the identity name).</summary>
    public string? PkgInstallerName { get; init; }
    /// <summary>Entitlements the user confirmed for the Developer ID signature (boolean true entitlements only).</summary>
    public IReadOnlyList<string> DeveloperIdEntitlements { get; init; } = [];

    /// <summary>Mac App Store: Apple Distribution (or 3rd Party Mac Developer Application) identity SHA-1.</summary>
    public string? AppStoreApplicationSha1 { get; init; }
    /// <summary>Mac App Store: Mac Installer Distribution identity SHA-1.</summary>
    public string? AppStoreInstallerSha1 { get; init; }
    public string? AppStoreInstallerName { get; init; }
    /// <summary>Mac App Store provisioning profile (*.provisionprofile) to embed.</summary>
    public string? AppStoreProfilePath { get; init; }
    /// <summary>Sandbox entitlements the user confirmed (com.apple.security.app-sandbox is always included).</summary>
    public IReadOnlyList<string> AppStoreEntitlements { get; init; } = [];

    /// <summary>The user reviewed and confirmed the entitlements shown (required before any distribution signing).</summary>
    public bool EntitlementsConfirmed { get; init; }
}

/// <summary>Credentials references for notarization / App Store Connect validation. Only names and IDs; secrets stay in the keychain.</summary>
public sealed record AppleCredentialReferences
{
    /// <summary>notarytool keychain profile (xcrun notarytool store-credentials &lt;name&gt;).</summary>
    public string? NotaryKeychainProfile { get; init; }
    /// <summary>App Store Connect API key ID (the .p8 stays in ~/.appstoreconnect/private_keys).</summary>
    public string? AppStoreConnectKeyId { get; init; }
    public string? AppStoreConnectIssuerId { get; init; }
}
