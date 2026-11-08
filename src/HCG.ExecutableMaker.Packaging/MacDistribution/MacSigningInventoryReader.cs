using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.Mobile;

namespace HCG.ExecutableMaker.Packaging.MacDistribution;

/// <summary>
/// Reads the signing material on this Mac without changing anything: valid identities from
/// <c>security find-identity -v</c> (all policies, so installer identities are included — <c>-p codesigning</c> would
/// hide them) and macOS provisioning profiles (*.provisionprofile). Private keys and passwords are never touched.
/// </summary>
public sealed partial class MacSigningInventoryReader(IProcessRunner runner, TimeProvider time)
{
    private const int MaxProfiles = 200;
    private const int MaxProfileBytes = 1024 * 1024;

    /// <summary>Folders Xcode and the system keep provisioning profiles in.</summary>
    public static IEnumerable<string> ProfileFolders()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(home, "Library", "Developer", "Xcode", "UserData", "Provisioning Profiles");
        yield return Path.Combine(home, "Library", "MobileDevice", "Provisioning Profiles");
    }

    public async Task<MacSigningInventory> ReadAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS())
            return new MacSigningInventory { Problem = "Signing identities and provisioning profiles are read on a Mac only.", DetectedAt = time.GetUtcNow() };
        string? problem = null;
        IReadOnlyList<SigningIdentity> identities = [];
        try
        {
            var (r, stdout, stderr) = await runner.CaptureAsync(new ProcessSpec
            {
                Executable = "/usr/bin/security", Arguments = ["find-identity", "-v"], Timeout = TimeSpan.FromSeconds(30),
            }, cancellationToken).ConfigureAwait(false);
            if (r.Succeeded) identities = ParseIdentities(stdout);
            else problem = $"security find-identity -v exited with code {r.ExitCode}: {stderr.Trim()}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            problem = "Could not read the keychain identities: " + ex.Message;
        }
        var profiles = ReadProfiles(ProfileFolders());
        return new MacSigningInventory { Identities = identities, Profiles = profiles, Problem = problem, DetectedAt = time.GetUtcNow() };
    }

    public static IReadOnlyList<MacProvisioningProfile> ReadProfiles(IEnumerable<string> folders)
    {
        var list = new List<MacProvisioningProfile>();
        foreach (var folder in folders.Where(Directory.Exists))
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(folder, "*.provisionprofile").Take(MaxProfiles).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var file in files)
                if (ReadProfile(file) is { } p && list.All(x => x.Uuid is null || x.Uuid != p.Uuid)) list.Add(p);
        }
        return list.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static MacProvisioningProfile? ReadProfile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxProfileBytes) return null;
            return ParseProfile(File.ReadAllBytes(path), path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Parses the XML plist inside a CMS-signed profile. The CMS signature itself is checked by codesign when the profile is used.</summary>
    public static MacProvisioningProfile? ParseProfile(byte[] data, string path)
    {
        var text = Encoding.Latin1.GetString(data);
        var start = text.IndexOf("<?xml", StringComparison.Ordinal);
        var end = text.IndexOf("</plist>", StringComparison.Ordinal);
        if (start < 0 || end < start) return null;
        Dictionary<string, object> plist;
        try { plist = PlistReader.ParseDictionary(Encoding.Latin1.GetBytes(text[start..(end + 8)])); }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException) { return null; }

        var ent = plist.GetValueOrDefault("Entitlements") as Dictionary<string, object> ?? [];
        var appId = ent.GetValueOrDefault("com.apple.application-identifier") as string ?? ent.GetValueOrDefault("application-identifier") as string;
        var certs = (plist.GetValueOrDefault("DeveloperCertificates") as List<object> ?? []).OfType<byte[]>().ToList();
        var certKinds = new List<AppleCertificateKind>();
        var sha1s = new List<string>();
        foreach (var der in certs)
        {
            sha1s.Add(Convert.ToHexString(SHA1.HashData(der)));
            try
            {
                using var cert = X509CertificateLoader.LoadCertificate(der);
                certKinds.Add(Classify(cert.GetNameInfo(X509NameType.SimpleName, false)));
            }
            catch (CryptographicException) { }
        }
        var hasDevices = plist.ContainsKey("ProvisionedDevices");
        var allDevices = plist.GetValueOrDefault("ProvisionsAllDevices") is true;
        var kind = allDevices || certKinds.Contains(AppleCertificateKind.DeveloperIdApplication) ? MacProfileKind.DeveloperId
            : hasDevices || certKinds.Contains(AppleCertificateKind.Development) ? MacProfileKind.Development
            : certKinds.Any(k => k is AppleCertificateKind.AppleDistribution or AppleCertificateKind.MacAppDistribution) ? MacProfileKind.MacAppStore
            : MacProfileKind.Unknown;
        DateTimeOffset? expiration = plist.GetValueOrDefault("ExpirationDate") is DateTime dt ? new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)) : null;
        return new MacProvisioningProfile
        {
            Path = path,
            Name = plist.GetValueOrDefault("Name") as string,
            Uuid = plist.GetValueOrDefault("UUID") as string,
            TeamId = (plist.GetValueOrDefault("TeamIdentifier") as List<object>)?.OfType<string>().FirstOrDefault()
                     ?? ent.GetValueOrDefault("com.apple.developer.team-identifier") as string,
            TeamName = plist.GetValueOrDefault("TeamName") as string,
            ApplicationIdentifier = appId,
            Expiration = expiration,
            Kind = kind,
            Platforms = (plist.GetValueOrDefault("Platform") as List<object>)?.OfType<string>().ToList() ?? [],
            EntitlementKeys = ent.Keys.Order(StringComparer.Ordinal).ToList(),
            CertificateSha1s = sha1s,
        };
    }

    /// <summary>Valid identities (name, SHA-1, certificate type, team) from <c>security find-identity -v</c>.</summary>
    public static IReadOnlyList<SigningIdentity> ParseIdentities(string output)
    {
        var list = new List<SigningIdentity>();
        foreach (Match m in IdentityRegex().Matches(output))
        {
            var sha1 = m.Groups["sha"].Value;
            if (list.Any(i => i.Sha1 == sha1)) continue;
            var name = m.Groups["name"].Value;
            var team = TeamRegex().Match(name) is { Success: true } t ? t.Groups[1].Value : null;
            list.Add(new SigningIdentity(sha1, name, Classify(name), team));
        }
        return list;
    }

    public static AppleCertificateKind Classify(string commonName) => commonName switch
    {
        _ when commonName.StartsWith("Developer ID Application:", StringComparison.Ordinal) => AppleCertificateKind.DeveloperIdApplication,
        _ when commonName.StartsWith("Developer ID Installer:", StringComparison.Ordinal) => AppleCertificateKind.DeveloperIdInstaller,
        _ when commonName.StartsWith("Apple Distribution:", StringComparison.Ordinal) => AppleCertificateKind.AppleDistribution,
        _ when commonName.StartsWith("3rd Party Mac Developer Application:", StringComparison.Ordinal)
               || commonName.StartsWith("Mac App Distribution:", StringComparison.Ordinal) => AppleCertificateKind.MacAppDistribution,
        _ when commonName.StartsWith("3rd Party Mac Developer Installer:", StringComparison.Ordinal)
               || commonName.StartsWith("Mac Installer Distribution:", StringComparison.Ordinal) => AppleCertificateKind.MacInstallerDistribution,
        _ when commonName.StartsWith("Apple Development:", StringComparison.Ordinal)
               || commonName.StartsWith("Mac Developer:", StringComparison.Ordinal) => AppleCertificateKind.Development,
        _ => AppleCertificateKind.Other,
    };

    [GeneratedRegex(@"^\s*\d+\)\s+(?<sha>[0-9A-F]{40})\s+""(?<name>[^""]+)""", RegexOptions.Multiline)]
    private static partial Regex IdentityRegex();

    [GeneratedRegex(@"\(([A-Z0-9]{10})\)\s*$")]
    private static partial Regex TeamRegex();
}
