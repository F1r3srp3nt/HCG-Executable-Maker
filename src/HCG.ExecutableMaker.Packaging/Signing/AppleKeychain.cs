using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.Packaging.Signing;

/// <summary>Outcome of a keychain write. <see cref="UserDenied"/>: the user refused a Keychain prompt.</summary>
public sealed record KeychainWriteResult(bool Succeeded, string? Problem = null, bool UserDenied = false)
{
    public static readonly KeychainWriteResult Ok = new(true);
}

/// <summary>
/// The macOS keychain as HCG uses it. Listing reads certificates and identity names only; writes add a certificate (with
/// its private key, if it has one) to the user's default keychain, or delete one identity. Nothing here grants other
/// applications access to a private key: codesign asks the user the first time it needs the key.
/// </summary>
public interface IAppleKeychain
{
    bool IsSupported { get; }
    Task<KeychainListing> ListAsync(CancellationToken cancellationToken);
    /// <summary>Adds a certificate. When <paramref name="certificate"/> carries a private key, the key is added with it.</summary>
    Task<KeychainWriteResult> AddAsync(X509Certificate2 certificate, CancellationToken cancellationToken);
    /// <summary>Deletes the certificate and its private key from this Mac's keychain (<c>security delete-identity</c>).</summary>
    Task<KeychainWriteResult> DeleteIdentityAsync(string sha1, CancellationToken cancellationToken);
    /// <summary>Deletes a certificate that has no private key on this Mac (<c>security delete-certificate</c>).</summary>
    Task<KeychainWriteResult> DeleteCertificateAsync(string sha1, CancellationToken cancellationToken);
}

/// <summary>
/// macOS implementation. Reads with <c>/usr/bin/security</c> (arguments are certificate fingerprints, never secrets) and
/// writes in-process through the system keychain API (<see cref="X509Store"/>), so a .p12 password never appears on a
/// command line, in a file or in the environment. No <c>-A</c>/<c>-T</c> access grants are made.
/// </summary>
public sealed partial class MacAppleKeychain(IProcessRunner runner, bool? supported = null) : IAppleKeychain
{
    private const string Security = "/usr/bin/security";

    /// <summary>macOS only. <c>supported</c> lets tests exercise the process invocations on other systems.</summary>
    public bool IsSupported => supported ?? OperatingSystem.IsMacOS();

    public async Task<KeychainListing> ListAsync(CancellationToken cancellationToken)
    {
        if (!IsSupported) return new KeychainListing { Problem = "The keychain is read on a Mac only." };
        var problems = new List<string>();
        IReadOnlyList<KeychainIdentityEntry> identities = [];
        IReadOnlyList<byte[]> certs = [];
        // All policies (installer identities are not code-signing identities) and without -v, so expired, untrusted
        // and revoked identities are listed with the reason macOS gives.
        var (r, stdout, stderr) = await runner.CaptureAsync(new ProcessSpec { Executable = Security, Arguments = ["find-identity"], Timeout = TimeSpan.FromSeconds(30) }, cancellationToken).ConfigureAwait(false);
        if (r.Succeeded) identities = ParseIdentities(stdout);
        else problems.Add(Describe("find-identity", r, stderr));
        var (c, pem, cerr) = await runner.CaptureAsync(new ProcessSpec { Executable = Security, Arguments = ["find-certificate", "-a", "-p"], Timeout = TimeSpan.FromSeconds(60) }, cancellationToken).ConfigureAwait(false);
        if (c.Succeeded) certs = AppleCertificates.ParsePem(pem);
        else problems.Add(Describe("find-certificate", c, cerr));
        return new KeychainListing { Identities = identities, Certificates = certs, Problem = problems.Count == 0 ? null : string.Join(" ", problems) };
    }

    public Task<KeychainWriteResult> AddAsync(X509Certificate2 certificate, CancellationToken cancellationToken)
    {
        if (!IsSupported) return Task.FromResult(new KeychainWriteResult(false, "Keychain imports need macOS."));
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            // CurrentUser/My is the user's default keychain (normally the login keychain). macOS may show its own
            // prompt (e.g. to unlock the keychain); a refusal ends up in the CryptographicException below.
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            store.Add(certificate);
            return Task.FromResult(KeychainWriteResult.Ok);
        }
        catch (CryptographicException ex)
        {
            var denied = ex.Message.Contains("denied", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("cancel", StringComparison.OrdinalIgnoreCase)
                         || ex.HResult == -128 /* errSecUserCanceled */;
            return Task.FromResult(new KeychainWriteResult(false, "The keychain refused the import: " + ex.Message, denied));
        }
    }

    public Task<KeychainWriteResult> DeleteIdentityAsync(string sha1, CancellationToken cancellationToken) => DeleteAsync("delete-identity", sha1, cancellationToken);
    public Task<KeychainWriteResult> DeleteCertificateAsync(string sha1, CancellationToken cancellationToken) => DeleteAsync("delete-certificate", sha1, cancellationToken);

    private async Task<KeychainWriteResult> DeleteAsync(string verb, string sha1, CancellationToken cancellationToken)
    {
        if (!IsSupported) return new KeychainWriteResult(false, "Keychain changes need macOS.");
        if (!Sha1Regex().IsMatch(sha1)) return new KeychainWriteResult(false, "Not a certificate fingerprint.");
        var (r, _, stderr) = await runner.CaptureAsync(new ProcessSpec { Executable = Security, Arguments = [verb, "-Z", sha1.ToUpperInvariant()], Timeout = TimeSpan.FromMinutes(2) }, cancellationToken).ConfigureAwait(false);
        return r.Succeeded ? KeychainWriteResult.Ok
            : new KeychainWriteResult(false, Describe(verb, r, stderr), stderr.Contains("cancel", StringComparison.OrdinalIgnoreCase) || stderr.Contains("denied", StringComparison.OrdinalIgnoreCase));
    }

    private static string Describe(string verb, ProcessResult r, string stderr) =>
        r.TimedOut ? $"security {verb} did not answer in time (a Keychain prompt may be waiting)."
        : $"security {verb} failed (exit {r.ExitCode}): {stderr.Trim()}";

    /// <summary>
    /// Parses the "Matching identities" section of <c>security find-identity</c> (every identity, valid or not), e.g.
    /// <c>1) 0123… "Apple Development: Jane (ABCDE12345)" (CSSMERR_TP_CERT_EXPIRED)</c>. The "Valid identities only"
    /// section repeats a subset and is ignored.
    /// </summary>
    public static IReadOnlyList<KeychainIdentityEntry> ParseIdentities(string output)
    {
        var list = new List<KeychainIdentityEntry>();
        var inValidOnly = false;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Contains("Valid identities only", StringComparison.Ordinal)) { inValidOnly = true; continue; }
            if (line.Contains("Matching identities", StringComparison.Ordinal)) { inValidOnly = false; continue; }
            if (inValidOnly) continue;
            var m = IdentityLine().Match(line);
            if (!m.Success) continue;
            var sha = m.Groups["sha"].Value.ToUpperInvariant();
            if (list.Any(i => i.Sha1 == sha)) continue;
            list.Add(new KeychainIdentityEntry(sha, m.Groups["name"].Value, m.Groups["why"].Success ? m.Groups["why"].Value : null));
        }
        return list;
    }

    [GeneratedRegex(@"^\s*\d+\)\s+(?<sha>[0-9A-Fa-f]{40})\s+""(?<name>[^""]*)""(?:\s+\((?<why>[^)]+)\))?\s*$")]
    private static partial Regex IdentityLine();

    [GeneratedRegex("^[0-9A-Fa-f]{40}$")]
    private static partial Regex Sha1Regex();
}

/// <summary>Installed provisioning profiles: list, import (verified, de-duplicated by UUID) and remove — on this Mac only.</summary>
public sealed class ProvisioningProfileStore(string installFolder, IReadOnlyList<string> readFolders, X509Certificate2Collection? trustedRoots = null)
{
    /// <summary>Xcode 16+ keeps profiles in ~/Library/Developer/Xcode/UserData/Provisioning Profiles; older Xcode in ~/Library/MobileDevice/Provisioning Profiles.</summary>
    public static ProvisioningProfileStore ForCurrentUser()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var modern = Path.Combine(home, "Library", "Developer", "Xcode", "UserData", "Provisioning Profiles");
        var legacy = Path.Combine(home, "Library", "MobileDevice", "Provisioning Profiles");
        return new ProvisioningProfileStore(modern, [modern, legacy]);
    }

    public string InstallFolder => installFolder;

    public sealed record Listing(IReadOnlyList<AppleProvisioningProfileInfo> Profiles, IReadOnlyList<(string Path, string Problem)> Unreadable);

    public Listing List()
    {
        var profiles = new List<AppleProvisioningProfileInfo>();
        var bad = new List<(string, string)>();
        foreach (var folder in readFolders.Distinct().Where(Directory.Exists))
        {
            List<string> files;
            try { files = Directory.EnumerateFiles(folder).Where(IsProfileFile).Take(500).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var f in files)
            {
                var r = ProvisioningProfileReader.ReadFile(f, trustedRoots);
                if (r.Profile is { } p) { if (profiles.All(x => x.Uuid != p.Uuid)) profiles.Add(p); }
                else bad.Add((f, r.Problem ?? "Unreadable."));
            }
        }
        return new Listing(profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList(), bad);
    }

    public static bool IsProfileFile(string path) =>
        path.EndsWith(".mobileprovision", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".provisionprofile", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Imports a profile after verifying it: Apple signature, required fields, not expired, a supported platform, and a
    /// UUID not already installed. Written as &lt;UUID&gt;.mobileprovision (or .provisionprofile for macOS), never over an existing file.
    /// </summary>
    public SigningImportResult Import(string sourcePath, DateTimeOffset now)
    {
        var read = ProvisioningProfileReader.ReadFile(sourcePath, trustedRoots);
        if (read.Profile is not { } p)
            return new(SigningImportStatus.Invalid, $"{Path.GetFileName(sourcePath)} was not imported: {read.Problem}") { Remedy = "Download the profile again from Xcode › Settings › Accounts or developer.apple.com › Profiles." };
        if (p.Signature != ProfileSignatureStatus.VerifiedApple)
            return new(SigningImportStatus.Invalid, $"{p.Name} was not imported: its signature is intact, but it is not signed by Apple (signer: {p.SignerSubject ?? "unknown"}).") { Profile = p };
        if (p.IsExpired(now))
            return new(SigningImportStatus.Expired, $"{p.Name} expired on {p.Expiration:yyyy-MM-dd} and was not imported.") { Profile = p, Remedy = "Regenerate it at developer.apple.com › Profiles (or let Xcode renew it) and import the new file." };
        if (p.Platforms.Count == 0 || !p.Platforms.Any(ApplePlatformNames.Supported.Contains))
            return new(SigningImportStatus.Unsupported, $"{p.Name} names no supported Apple platform ({string.Join(", ", p.Platforms)}).") { Profile = p };
        var existing = List().Profiles.FirstOrDefault(x => x.Uuid == p.Uuid);
        if (existing is not null)
            return new(SigningImportStatus.Duplicate, $"{p.Name} (UUID {p.Uuid}) is already installed{(existing.Path is { } ep ? " at " + ep : "")}. Nothing was changed.") { Profile = existing };

        Directory.CreateDirectory(installFolder);
        var extension = p.Platforms.Contains(ApplePlatformNames.MacOs) ? ".provisionprofile" : ".mobileprovision";
        var target = Path.Combine(installFolder, p.Uuid + extension);
        if (File.Exists(target))
            return new(SigningImportStatus.Duplicate, $"A profile file named {Path.GetFileName(target)} already exists. Nothing was changed.") { Profile = p };
        var temp = target + ".hcg-import";
        File.Copy(sourcePath, temp, overwrite: true);
        File.Move(temp, target, overwrite: false);
        return new(SigningImportStatus.Imported, $"Installed {p.Name} ({p.DistributionDescription}, expires {p.Expiration:yyyy-MM-dd}).") { Profile = p with { Path = target, Location = SigningAssetLocation.ThisMac } };
    }

    /// <summary>Deletes the installed profile file(s) with this UUID from this Mac. Apple's copy of the profile is unaffected.</summary>
    public int Remove(string uuid)
    {
        var removed = 0;
        foreach (var p in List().Profiles.Where(p => string.Equals(p.Uuid, uuid, StringComparison.OrdinalIgnoreCase) && p.Path is not null))
        {
            File.Delete(p.Path!);
            removed++;
        }
        return removed;
    }
}
