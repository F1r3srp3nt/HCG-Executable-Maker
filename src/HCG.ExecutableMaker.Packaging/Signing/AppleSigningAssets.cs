using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.Packaging.Signing;

/// <summary>Everything HCG knows about signing assets, from this Mac and (optionally) the HCG account's metadata.</summary>
public sealed record SigningAssetInventory
{
    public IReadOnlyList<AppleCertificateInfo> Certificates { get; init; } = [];
    public IReadOnlyList<AppleProvisioningProfileInfo> Profiles { get; init; } = [];
    public IReadOnlyList<(string Path, string Problem)> UnreadableProfiles { get; init; } = [];
    public string? KeychainProblem { get; init; }
    public DateTimeOffset ReadAt { get; init; }

    public AppleCertificateInfo? Certificate(string? sha1) =>
        sha1 is null ? null : Certificates.FirstOrDefault(c => string.Equals(c.Sha1, sha1, StringComparison.OrdinalIgnoreCase));

    public AppleProvisioningProfileInfo? Profile(string? uuid) =>
        uuid is null ? null : Profiles.FirstOrDefault(p => string.Equals(p.Uuid, uuid, StringComparison.OrdinalIgnoreCase));
}

/// <summary>What deleting or removing something actually does. Shown before the user confirms.</summary>
public static class SigningRemovalEffects
{
    public const string RemoveAccountRecord =
        "Removes HCG's account record of this asset (name, fingerprint, team, expiry). The certificate and private key stay in this Mac's keychain, " +
        "copies on other Macs are untouched, and the certificate remains valid with Apple.";

    public const string DeleteLocalIdentity =
        "Deletes the certificate AND its private key from this Mac's keychain. Unless you have another copy (a .p12 backup or another Mac), " +
        "you can no longer sign with it — Apple does not keep private keys. The certificate stays valid with Apple and on other Macs.";

    public const string DeleteLocalCertificate =
        "Deletes the certificate from this Mac's keychain (it has no private key here). The certificate stays valid with Apple and on other Macs.";

    public const string RemoveLocalProfile =
        "Deletes the installed profile file from this Mac. The profile stays on the Apple Developer website and can be downloaded again.";

    public const string RevokeWithApple =
        "Revoking happens only on the Apple Developer website (Certificates, Identifiers & Profiles) or in Xcode by an account holder or admin. " +
        "It invalidates the certificate everywhere, breaks provisioning profiles that include it, and for Developer ID can stop already-distributed " +
        "software from launching. HCG never revokes certificates.";

    public const string RevokeUrl = "https://developer.apple.com/account/resources/certificates/list";
}

/// <summary>
/// Local Keychain detection and import, provisioning profile management, and the explicit removal operations.
/// Private keys go only into the keychain. Passwords are used in memory to open a .p12 and are not kept, logged or passed
/// to any process.
/// </summary>
public sealed class AppleSigningAssetService(IAppleKeychain keychain, ProvisioningProfileStore profiles, TimeProvider time)
{
    public const int MaxCertificateFileBytes = 256 * 1024;

    public bool KeychainSupported => keychain.IsSupported;

    public async Task<SigningAssetInventory> ReadAsync(IReadOnlyList<AccountSigningAssetRecord>? account, string? thisDeviceId, CancellationToken cancellationToken)
    {
        var listing = await keychain.ListAsync(cancellationToken).ConfigureAwait(false);
        var withKey = listing.Identities.ToDictionary(i => i.Sha1, StringComparer.OrdinalIgnoreCase);
        var certs = new List<AppleCertificateInfo>();
        foreach (var der in listing.Certificates)
        {
            if (AppleCertificates.TryDescribe(der, null) is not { } c || !AppleCertificates.IsAppleDeveloperCertificate(c)) continue;
            if (certs.Any(x => x.Sha1 == c.Sha1)) continue;
            withKey.TryGetValue(c.Sha1, out var identity);
            certs.Add(c with
            {
                HasPrivateKey = identity is not null,
                TrustProblem = identity?.Problem,
                Revocation = identity?.Problem?.Contains("REVOKED", StringComparison.OrdinalIgnoreCase) == true ? RevocationStatus.ReportedRevoked : RevocationStatus.NotChecked,
            });
        }
        var profileListing = profiles.List();
        var profs = profileListing.Profiles.ToList();
        if (account is { Count: > 0 }) Merge(certs, profs, account, thisDeviceId);
        return new SigningAssetInventory
        {
            Certificates = certs.OrderBy(c => c.Kind).ThenBy(c => c.CommonName, StringComparer.OrdinalIgnoreCase).ToList(),
            Profiles = profs,
            UnreadableProfiles = profileListing.Unreadable,
            KeychainProblem = listing.Problem,
            ReadAt = time.GetUtcNow(),
        };
    }

    /// <summary>
    /// Adds account knowledge without ever making an asset look usable: account-only assets get no private key and no
    /// local path, so their readiness is <see cref="SigningReadiness.NotOnThisMac"/>.
    /// </summary>
    private static void Merge(List<AppleCertificateInfo> certs, List<AppleProvisioningProfileInfo> profs, IReadOnlyList<AccountSigningAssetRecord> account, string? thisDevice)
    {
        foreach (var r in account)
        {
            var elsewhere = r.DeviceId is not null && !string.Equals(r.DeviceId, thisDevice, StringComparison.OrdinalIgnoreCase);
            var flags = SigningAssetLocation.HcgAccountMetadata | (elsewhere ? SigningAssetLocation.AnotherMac : SigningAssetLocation.None);
            if (r.AssetType == "certificate" && r.Sha1 is { } sha)
            {
                var i = certs.FindIndex(c => string.Equals(c.Sha1, sha, StringComparison.OrdinalIgnoreCase));
                if (i >= 0) certs[i] = certs[i] with { Location = certs[i].Location | flags, RecordedOnDevice = certs[i].RecordedOnDevice ?? (elsewhere ? r.DeviceLabel : null) };
                else certs.Add(new AppleCertificateInfo
                {
                    Sha1 = sha.ToUpperInvariant(), Sha256 = r.Sha256?.ToUpperInvariant() ?? "", CommonName = r.DisplayName ?? sha,
                    Kind = Enum.TryParse<AppleCertificateKind>(r.CertificateKind, out var k) ? k : AppleCertificateKind.Other,
                    TeamId = r.TeamId, NotAfter = r.ExpiresAt ?? DateTimeOffset.MinValue, HasPrivateKey = null,
                    Location = flags, RecordedOnDevice = r.DeviceLabel,
                });
            }
            else if (r.AssetType == "profile" && r.ProfileUuid is { } uuid)
            {
                var i = profs.FindIndex(p => string.Equals(p.Uuid, uuid, StringComparison.OrdinalIgnoreCase));
                if (i >= 0) profs[i] = profs[i] with { Location = profs[i].Location | flags, RecordedOnDevice = profs[i].RecordedOnDevice ?? (elsewhere ? r.DeviceLabel : null) };
                else profs.Add(new AppleProvisioningProfileInfo
                {
                    Uuid = uuid.ToUpperInvariant(), Name = r.DisplayName ?? uuid, TeamIds = r.TeamId is null ? [] : [r.TeamId],
                    Kind = Enum.TryParse<AppleProfileKind>(r.ProfileKind, out var pk) ? pk : AppleProfileKind.Unknown,
                    ApplicationIdentifier = r.TeamId is not null && r.BundleIdPattern is not null ? r.TeamId + "." + r.BundleIdPattern : null,
                    Platforms = r.Platforms, Expiration = r.ExpiresAt, Path = null, Signature = ProfileSignatureStatus.Invalid,
                    Location = flags, RecordedOnDevice = r.DeviceLabel,
                });
            }
        }
    }

    /// <summary>
    /// Imports a password-protected .p12 into this Mac's keychain. <paramref name="password"/> null means the user
    /// cancelled the password prompt. The password is checked against the file's integrity MAC before anything is
    /// written; it is never logged, stored or passed to a process.
    /// </summary>
    public async Task<SigningImportResult> ImportPkcs12Async(byte[] data, string? password, CancellationToken cancellationToken)
    {
        if (password is null) return new(SigningImportStatus.Cancelled, "Import cancelled. Nothing was changed.");
        if (cancellationToken.IsCancellationRequested) return new(SigningImportStatus.Cancelled, "Import cancelled. Nothing was changed.");
        if (data.Length == 0 || data.Length > MaxCertificateFileBytes) return new(SigningImportStatus.Invalid, "This is not a .p12 file HCG can import (empty or larger than 256 KB).");

        Pkcs12Info info;
        try { info = Pkcs12Info.Decode(data, out _, skipCopy: true); }
        catch (CryptographicException) { return new(SigningImportStatus.Invalid, "This file is not a valid .p12 (PKCS #12) export.") { Remedy = "In Keychain Access, select the certificate together with its private key and choose File › Export Items… › Personal Information Exchange (.p12)." }; }
        if (info.IntegrityMode == Pkcs12IntegrityMode.Password && !info.VerifyMac(password))
            return new(SigningImportStatus.WrongPassword, "The password does not open this .p12. Nothing was changed.") { Remedy = "Enter the password that was set when the .p12 was exported." };

        X509Certificate2Collection collection;
        try
        {
            // Exportable only for the in-memory copy that is handed to the keychain; the key is not written anywhere else.
            collection = X509CertificateLoader.LoadPkcs12Collection(data, password, X509KeyStorageFlags.Exportable, Pkcs12LoaderLimits.Defaults);
        }
        catch (CryptographicException ex) { return new(SigningImportStatus.Invalid, "The .p12 could not be opened: " + ex.Message); }

        try
        {
            var described = collection.Select(c => (Cert: c, Info: AppleCertificates.Describe(c, c.HasPrivateKey))).ToList();
            var candidates = described.Where(d => AppleCertificates.IsAppleDeveloperCertificate(d.Info)).ToList();
            if (candidates.Count == 0)
                return new(SigningImportStatus.Unsupported, "The .p12 contains no Apple developer certificate (Apple Development, Apple Distribution, Developer ID …). Nothing was changed.");
            var pick = candidates.FirstOrDefault(d => d.Cert.HasPrivateKey);
            if (pick.Cert is null)
            {
                // A .p12 without the key: behave like a .cer import, with the same explanation.
                return await ImportCertificateCoreAsync(candidates[0].Cert, candidates[0].Info, cancellationToken).ConfigureAwait(false);
            }
            var now = time.GetUtcNow();
            if (pick.Info.IsExpired(now))
                return new(SigningImportStatus.Expired, $"{pick.Info.CommonName} expired on {pick.Info.NotAfter:yyyy-MM-dd}; an expired certificate cannot sign. Nothing was changed.") { Certificate = pick.Info, Remedy = "Create a new certificate in Xcode › Settings › Accounts › Manage Certificates or at developer.apple.com." };

            var listing = await keychain.ListAsync(cancellationToken).ConfigureAwait(false);
            if (listing.Identities.Any(i => string.Equals(i.Sha1, pick.Info.Sha1, StringComparison.OrdinalIgnoreCase)))
                return new(SigningImportStatus.Duplicate, $"{pick.Info.CommonName} (SHA-1 {pick.Info.ShortSha1}…) and its private key are already in this Mac's keychain. Nothing was changed.") { Certificate = pick.Info with { HasPrivateKey = true } };
            if (cancellationToken.IsCancellationRequested) return new(SigningImportStatus.Cancelled, "Import cancelled. Nothing was changed.");

            var write = await keychain.AddAsync(pick.Cert, cancellationToken).ConfigureAwait(false);
            if (!write.Succeeded)
                return write.UserDenied
                    ? new(SigningImportStatus.Cancelled, "The keychain prompt was declined. Nothing was imported.")
                    : new(SigningImportStatus.KeychainError, write.Problem ?? "The keychain refused the import.") { Remedy = "Unlock the login keychain in Keychain Access and try again." };
            return new(SigningImportStatus.Imported,
                $"Imported {pick.Info.CommonName} with its private key into this Mac's login keychain. The first time codesign uses the key, macOS asks whether to allow it.")
            { Certificate = pick.Info with { HasPrivateKey = true } };
        }
        finally
        {
            foreach (var c in collection) c.Dispose();
        }
    }

    /// <summary>Imports a .cer (DER or PEM). Imported certificate-only; the explanation says how to get the private key.</summary>
    public async Task<SigningImportResult> ImportCertificateAsync(byte[] data, CancellationToken cancellationToken)
    {
        if (data.Length == 0 || data.Length > MaxCertificateFileBytes) return new(SigningImportStatus.Invalid, "This is not a certificate file HCG can import.");
        var der = data;
        if (data.Length > 10 && data[0] == (byte)'-') der = AppleCertificates.ParsePem(System.Text.Encoding.ASCII.GetString(data)).FirstOrDefault() ?? [];
        X509Certificate2 cert;
        try { cert = X509CertificateLoader.LoadCertificate(der); }
        catch (CryptographicException) { return new(SigningImportStatus.Invalid, "This file is not a certificate (.cer, DER or PEM)."); }
        using (cert)
        {
            var info = AppleCertificates.Describe(cert, null);
            if (!AppleCertificates.IsAppleDeveloperCertificate(info))
                return new(SigningImportStatus.Unsupported, $"{info.CommonName} is not an Apple developer certificate. Nothing was changed.");
            var now = time.GetUtcNow();
            if (info.IsExpired(now))
                return new(SigningImportStatus.Expired, $"{info.CommonName} expired on {info.NotAfter:yyyy-MM-dd}. Nothing was changed.") { Certificate = info };
            return await ImportCertificateCoreAsync(cert, info, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<SigningImportResult> ImportCertificateCoreAsync(X509Certificate2 cert, AppleCertificateInfo info, CancellationToken cancellationToken)
    {
        const string why = "A certificate alone cannot sign: the private key was created on the Mac that requested the certificate and never leaves it unless exported.";
        const string remedy = "On the Mac that created the certificate, export it from Keychain Access as a .p12 (certificate + private key) and import that file here. " +
                              "If no Mac has the key any more, create a new certificate (Xcode › Settings › Accounts › Manage Certificates); revoking the old one is a separate decision on developer.apple.com.";
        var listing = await keychain.ListAsync(cancellationToken).ConfigureAwait(false);
        var hasKey = listing.Identities.Any(i => string.Equals(i.Sha1, info.Sha1, StringComparison.OrdinalIgnoreCase));
        var present = listing.Certificates.Any(d => string.Equals(Convert.ToHexString(SHA1.HashData(d)), info.Sha1, StringComparison.OrdinalIgnoreCase));
        if (present || hasKey)
            return new(SigningImportStatus.Duplicate, $"{info.CommonName} (SHA-1 {info.ShortSha1}…) is already in this Mac's keychain{(hasKey ? " with its private key" : " (without a private key)")}. Nothing was changed.")
            { Certificate = info with { HasPrivateKey = hasKey }, Remedy = hasKey ? null : remedy };
        using var publicOnly = X509CertificateLoader.LoadCertificate(cert.RawData);
        var write = await keychain.AddAsync(publicOnly, cancellationToken).ConfigureAwait(false);
        if (!write.Succeeded)
            return write.UserDenied ? new(SigningImportStatus.Cancelled, "The keychain prompt was declined. Nothing was imported.")
                : new(SigningImportStatus.KeychainError, write.Problem ?? "The keychain refused the import.");
        return new(SigningImportStatus.ImportedCertificateOnly, $"Imported {info.CommonName} without a private key. {why}") { Certificate = info with { HasPrivateKey = false }, Remedy = remedy };
    }

    public SigningImportResult ImportProfile(string path) => profiles.Import(path, time.GetUtcNow());

    /// <summary>Deletes the certificate (and its private key, if present) from this Mac only. See <see cref="SigningRemovalEffects"/>.</summary>
    public Task<KeychainWriteResult> DeleteLocalAsync(AppleCertificateInfo certificate, CancellationToken cancellationToken) =>
        certificate.HasPrivateKey == true
            ? keychain.DeleteIdentityAsync(certificate.Sha1, cancellationToken)
            : keychain.DeleteCertificateAsync(certificate.Sha1, cancellationToken);

    public int RemoveLocalProfile(string uuid) => profiles.Remove(uuid);
}
