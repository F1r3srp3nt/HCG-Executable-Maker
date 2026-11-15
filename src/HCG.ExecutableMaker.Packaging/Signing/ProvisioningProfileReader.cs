using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.Mobile;

namespace HCG.ExecutableMaker.Packaging.Signing;

/// <summary>
/// Reads a provisioning profile (.mobileprovision / .provisionprofile) from its CMS envelope: the signature is checked
/// against the contents and the signer chain is built to a trusted root before any field is believed. Everything shown
/// (type, team, App ID, devices, entitlements, certificates) comes from the verified plist, never from the file name.
/// </summary>
public static class ProvisioningProfileReader
{
    public const int MaxBytes = 1024 * 1024;

    public sealed record Result(AppleProvisioningProfileInfo? Profile, string? Problem);

    /// <param name="trustedRoots">
    /// Roots to trust instead of the system store (tests use a synthetic root). Null = the system trust store, which on a
    /// Mac includes Apple's root certificates.
    /// </param>
    public static Result Read(byte[] data, string? path, X509Certificate2Collection? trustedRoots = null)
    {
        if (data.Length == 0) return new(null, "The file is empty.");
        if (data.Length > MaxBytes) return new(null, "The file is larger than a provisioning profile can be (1 MB).");
        var cms = new SignedCms();
        try { cms.Decode(data); }
        catch (CryptographicException) { return new(null, "This is not a provisioning profile: the file is not a signed (CMS) envelope."); }
        if (cms.SignerInfos.Count == 0) return new(null, "The profile carries no signature.");
        try { cms.CheckSignature(verifySignatureOnly: true); }
        catch (CryptographicException) { return new(null, "The profile's signature does not match its contents (the file was altered or is damaged)."); }

        var signer = cms.SignerInfos[0].Certificate;
        var status = ChainsToTrustedRoot(signer, cms.Certificates, trustedRoots) ? ProfileSignatureStatus.VerifiedApple : ProfileSignatureStatus.IntactUntrustedSigner;

        Dictionary<string, object> plist;
        try { plist = PlistReader.ParseDictionary(cms.ContentInfo.Content); }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException or FormatException)
        {
            return new(null, "The profile's contents are not a valid property list.");
        }

        var uuid = plist.GetValueOrDefault("UUID") as string;
        var name = plist.GetValueOrDefault("Name") as string;
        var teams = (plist.GetValueOrDefault("TeamIdentifier") as List<object>)?.OfType<string>().ToList() ?? [];
        var expiration = Date(plist.GetValueOrDefault("ExpirationDate"));
        if (plist.GetValueOrDefault("Entitlements") is not Dictionary<string, object> ent || uuid is null || !Guid.TryParse(uuid, out _)
            || name is null || teams.Count == 0 || expiration is null)
            return new(null, "The profile is missing required fields (UUID, Name, TeamIdentifier, ExpirationDate or Entitlements).");

        var appId = ent.GetValueOrDefault("application-identifier") as string ?? ent.GetValueOrDefault("com.apple.application-identifier") as string;
        var platforms = (plist.GetValueOrDefault("Platform") as List<object>)?.OfType<string>().ToList() ?? [];
        var devices = (plist.GetValueOrDefault("ProvisionedDevices") as List<object>)?.OfType<string>().ToList() ?? [];
        var allDevices = plist.GetValueOrDefault("ProvisionsAllDevices") is true;
        var certs = (plist.GetValueOrDefault("DeveloperCertificates") as List<object> ?? []).OfType<byte[]>().ToList();
        var certInfos = certs.Select(c => AppleCertificates.TryDescribe(c, null)).OfType<AppleCertificateInfo>().ToList();
        var entitlements = ent.ToDictionary(kv => kv.Key, kv => AppleCertificates.RenderValue(kv.Value), StringComparer.Ordinal);
        var getTaskAllow = ent.GetValueOrDefault("get-task-allow") is true || ent.GetValueOrDefault("com.apple.security.get-task-allow") is true;
        var isMac = platforms.Contains(ApplePlatformNames.MacOs);

        var kind = allDevices
                ? (isMac || certInfos.Any(c => c.Kind == AppleCertificateKind.DeveloperIdApplication) ? AppleProfileKind.DeveloperId : AppleProfileKind.Enterprise)
            : plist.ContainsKey("ProvisionedDevices")
                ? (getTaskAllow || certInfos.Any(c => c.Kind == AppleCertificateKind.Development) ? AppleProfileKind.Development : AppleProfileKind.AdHoc)
            : certInfos.Count > 0 && certInfos.All(c => c.Kind == AppleCertificateKind.Development) ? AppleProfileKind.Development
            : AppleProfileKind.AppStore;

        return new(new AppleProvisioningProfileInfo
        {
            Path = path,
            Uuid = uuid.ToUpperInvariant(),
            Name = name,
            AppIdName = plist.GetValueOrDefault("AppIDName") as string,
            TeamIds = teams,
            TeamName = plist.GetValueOrDefault("TeamName") as string,
            ApplicationIdentifier = appId,
            Platforms = platforms,
            CreationDate = Date(plist.GetValueOrDefault("CreationDate")),
            Expiration = expiration,
            Kind = kind,
            Entitlements = entitlements,
            ProvisionedDevices = devices,
            ProvisionsAllDevices = allDevices,
            CertificateSha1s = certs.Select(c => Convert.ToHexString(SHA1.HashData(c))).ToList(),
            IsXcodeManaged = plist.GetValueOrDefault("IsXcodeManaged") is true,
            Signature = status,
            SignerSubject = signer?.Subject,
        }, null);
    }

    public static Result ReadFile(string path, X509Certificate2Collection? trustedRoots = null)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return new(null, "The file does not exist.");
            if (info.Length > MaxBytes) return new(null, "The file is larger than a provisioning profile can be (1 MB).");
            return Read(File.ReadAllBytes(path), path, trustedRoots);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(null, "The file could not be read: " + ex.Message); }
    }

    private static DateTimeOffset? Date(object? value) =>
        value is DateTime dt && dt != DateTime.MinValue ? new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)) : null;

    /// <summary>
    /// Builds the signer's chain to a trusted root. Time validity is not required: Apple re-signs profiles rarely and the
    /// profile's own ExpirationDate decides its validity. Revocation is not checked here (that needs Apple's services).
    /// </summary>
    private static bool ChainsToTrustedRoot(X509Certificate2? signer, X509Certificate2Collection extra, X509Certificate2Collection? trustedRoots)
    {
        if (signer is null) return false;
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreNotTimeValid;
        chain.ChainPolicy.ExtraStore.AddRange(extra);
        if (trustedRoots is not null)
        {
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.AddRange(trustedRoots);
        }
        try
        {
            if (!chain.Build(signer)) return false;
            // With the system store, require an Apple root: any other trusted root (e.g. a corporate proxy CA) is not Apple.
            var root = chain.ChainElements[^1].Certificate;
            return trustedRoots is not null || root.Subject.Contains("O=Apple Inc.", StringComparison.Ordinal);
        }
        catch (CryptographicException) { return false; }
    }
}
