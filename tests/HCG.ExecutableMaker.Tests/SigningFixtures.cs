using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Security;
using System.Text;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.Signing;

namespace HCG.ExecutableMaker.Tests;

/// <summary>
/// Synthetic signing material for tests. Every certificate is generated in memory from a throw-away test root; nothing
/// is an Apple certificate and no real developer secret is involved.
/// </summary>
public static class SigningFixtures
{
    public const string Team = "ABCDE12345";
    public const string OtherTeam = "ZYXWV98765";
    public static readonly DateTimeOffset Now = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    private static readonly Lazy<X509Certificate2> LazyRoot = new(() =>
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=HCG Synthetic Test Root, O=HCG Tests", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        return req.CreateSelfSigned(Now.AddYears(-10), Now.AddYears(20));
    });

    public static X509Certificate2 Root => LazyRoot.Value;
    public static X509Certificate2Collection TrustedRoots => [X509CertificateLoader.LoadCertificate(Root.RawData)];

    private static readonly Lazy<X509Certificate2> LazySigner = new(() => Issue("Synthetic Provisioning Profile Signing", null, Now.AddYears(-1), Now.AddYears(5)));
    public static X509Certificate2 ProfileSigner => LazySigner.Value;

    /// <summary>A leaf certificate with its private key, issued by the test root.</summary>
    public static X509Certificate2 Issue(string commonName, string? ou, DateTimeOffset notBefore, DateTimeOffset notAfter, X509Certificate2? issuer = null)
    {
        var key = RSA.Create(2048);
        var name = new X500DistinguishedNameBuilder();
        name.AddCountryOrRegion("US");
        name.AddOrganizationName("Synthetic Test Team");
        if (ou is not null) name.AddOrganizationalUnitName(ou);
        name.AddCommonName(commonName);
        var req = new CertificateRequest(name.Build(), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        var serial = RandomNumberGenerator.GetBytes(12);
        serial[0] &= 0x7F;
        using var publicOnly = req.Create(issuer ?? Root, notBefore, notAfter, serial);
        return publicOnly.CopyWithPrivateKey(key);
    }

    public static X509Certificate2 Development(string person = "Test Person", string team = Team, DateTimeOffset? notAfter = null) =>
        Issue($"Apple Development: {person} ({team})", team, Now.AddMonths(-6), notAfter ?? Now.AddMonths(6));

    public static X509Certificate2 Distribution(string team = Team, DateTimeOffset? notAfter = null) =>
        Issue($"Apple Distribution: Synthetic Test Team ({team})", team, Now.AddMonths(-6), notAfter ?? Now.AddMonths(6));

    public static X509Certificate2 DeveloperIdApplication(string team = Team) =>
        Issue($"Developer ID Application: Synthetic Test Team ({team})", team, Now.AddMonths(-6), Now.AddYears(4));

    public static X509Certificate2 DeveloperIdInstaller(string team = Team) =>
        Issue($"Developer ID Installer: Synthetic Test Team ({team})", team, Now.AddMonths(-6), Now.AddYears(4));

    public static byte[] Pkcs12(X509Certificate2 withKey, string password) => withKey.Export(X509ContentType.Pkcs12, password);

    public static AppleCertificateInfo Info(X509Certificate2 cert, bool hasKey = true) => AppleCertificates.Describe(cert, hasKey);

    public sealed record ProfileSpec
    {
        public string Name { get; init; } = "Synthetic Test Profile";
        public string Uuid { get; init; } = Guid.NewGuid().ToString().ToUpperInvariant();
        public string Team { get; init; } = SigningFixtures.Team;
        public string AppId { get; init; } = SigningFixtures.Team + ".com.example.app";
        public string Platform { get; init; } = "iOS";
        public DateTimeOffset Expires { get; init; } = Now.AddMonths(6);
        public IReadOnlyList<string>? Devices { get; init; }
        public bool AllDevices { get; init; }
        public bool GetTaskAllow { get; init; }
        public IReadOnlyList<X509Certificate2> Certificates { get; init; } = [];
        /// <summary>Extra entitlements: bool, string or string[] values.</summary>
        public IReadOnlyDictionary<string, object> Entitlements { get; init; } = new Dictionary<string, object>();
        public bool OmitUuid { get; init; }
    }

    public static string ProfileXml(ProfileSpec s)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n<plist version=\"1.0\"><dict>\n");
        void Str(string k, string v) => sb.Append($"<key>{k}</key><string>{SecurityElement.Escape(v)}</string>\n");
        Str("AppIDName", "Synthetic App");
        sb.Append("<key>CreationDate</key><date>").Append(Now.AddDays(-10).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ")).Append("</date>\n");
        sb.Append("<key>Platform</key><array><string>").Append(s.Platform).Append("</string></array>\n");
        sb.Append("<key>DeveloperCertificates</key><array>");
        foreach (var c in s.Certificates) sb.Append("<data>").Append(Convert.ToBase64String(c.RawData)).Append("</data>");
        sb.Append("</array>\n<key>Entitlements</key><dict>\n");
        var appIdKey = s.Platform == "OSX" ? "com.apple.application-identifier" : "application-identifier";
        Str(appIdKey, s.AppId);
        Str("com.apple.developer.team-identifier", s.Team);
        sb.Append("<key>get-task-allow</key>").Append(s.GetTaskAllow ? "<true/>" : "<false/>").Append('\n');
        foreach (var (k, v) in s.Entitlements)
        {
            sb.Append("<key>").Append(k).Append("</key>");
            sb.Append(v switch
            {
                bool b => b ? "<true/>" : "<false/>",
                string str => $"<string>{SecurityElement.Escape(str)}</string>",
                string[] arr => "<array>" + string.Concat(arr.Select(a => $"<string>{SecurityElement.Escape(a)}</string>")) + "</array>",
                _ => throw new ArgumentException("value"),
            });
            sb.Append('\n');
        }
        sb.Append("</dict>\n");
        sb.Append("<key>ExpirationDate</key><date>").Append(s.Expires.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ")).Append("</date>\n");
        Str("Name", s.Name);
        if (s.Devices is { } devices)
            sb.Append("<key>ProvisionedDevices</key><array>").Append(string.Concat(devices.Select(d => $"<string>{d}</string>"))).Append("</array>\n");
        if (s.AllDevices) sb.Append("<key>ProvisionsAllDevices</key><true/>\n");
        sb.Append("<key>TeamIdentifier</key><array><string>").Append(s.Team).Append("</string></array>\n");
        Str("TeamName", "Synthetic Test Team");
        if (!s.OmitUuid) Str("UUID", s.Uuid);
        sb.Append("<key>Version</key><integer>1</integer>\n</dict></plist>\n");
        return sb.ToString();
    }

    /// <summary>A CMS-signed profile, like the ones Apple issues (signed here by the synthetic profile signer).</summary>
    public static byte[] Profile(ProfileSpec spec, X509Certificate2? signer = null) => Sign(Encoding.UTF8.GetBytes(ProfileXml(spec)), signer ?? ProfileSigner);

    public static byte[] Sign(byte[] content, X509Certificate2 signer)
    {
        var cms = new SignedCms(new ContentInfo(content), detached: false);
        cms.ComputeSignature(new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, signer) { IncludeOption = X509IncludeOption.EndCertOnly });
        return cms.Encode();
    }

    public static ProvisioningProfileStore Store(TempDir dir) =>
        new(dir.Combine("installed"), [dir.Combine("installed")], TrustedRoots);
}

public sealed class FixedTime(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>In-memory keychain: records every write; never touches a real keychain.</summary>
public sealed class FakeKeychain : IAppleKeychain
{
    public List<(byte[] Der, bool HasKey, string? Problem)> Items { get; } = [];
    public List<string> Operations { get; } = [];
    public KeychainWriteResult NextAddResult { get; set; } = KeychainWriteResult.Ok;
    public IAppleKeychain? ListFrom { get; init; }
    public bool IsSupported => true;

    public void Add(X509Certificate2 cert, bool hasKey, string? problem = null) => Items.Add((cert.RawData, hasKey, problem));

    public async Task<KeychainListing> ListAsync(CancellationToken cancellationToken)
    {
        if (ListFrom is not null) await ListFrom.ListAsync(cancellationToken);
        return new KeychainListing
        {
            Certificates = Items.Select(i => i.Der).ToList(),
            Identities = Items.Where(i => i.HasKey).Select(i =>
            {
                using var c = X509CertificateLoader.LoadCertificate(i.Der);
                return new KeychainIdentityEntry(Convert.ToHexString(SHA1.HashData(i.Der)), c.GetNameInfo(X509NameType.SimpleName, false), i.Problem);
            }).ToList(),
        };
    }

    public Task<KeychainWriteResult> AddAsync(X509Certificate2 certificate, CancellationToken cancellationToken)
    {
        Operations.Add($"add {certificate.Thumbprint} key={certificate.HasPrivateKey}");
        if (NextAddResult.Succeeded) Items.Add((certificate.RawData, certificate.HasPrivateKey, null));
        return Task.FromResult(NextAddResult);
    }

    public Task<KeychainWriteResult> DeleteIdentityAsync(string sha1, CancellationToken cancellationToken)
    {
        Operations.Add("delete-identity " + sha1);
        Items.RemoveAll(i => Convert.ToHexString(SHA1.HashData(i.Der)) == sha1);
        return Task.FromResult(KeychainWriteResult.Ok);
    }

    public Task<KeychainWriteResult> DeleteCertificateAsync(string sha1, CancellationToken cancellationToken)
    {
        Operations.Add("delete-certificate " + sha1);
        Items.RemoveAll(i => Convert.ToHexString(SHA1.HashData(i.Der)) == sha1);
        return Task.FromResult(KeychainWriteResult.Ok);
    }
}

/// <summary>Records every process HCG would start, answering with canned output.</summary>
public sealed class RecordingProcessRunner(Func<ProcessSpec, (int Exit, string Stdout, string Stderr)> answer) : IProcessRunner
{
    public List<ProcessSpec> Calls { get; } = [];

    public Task<ProcessResult> RunAsync(ProcessSpec spec, Action<OutputStream, string>? onOutput, CancellationToken cancellationToken)
    {
        Calls.Add(spec);
        var (exit, stdout, stderr) = answer(spec);
        foreach (var line in stdout.Split('\n')) onOutput?.Invoke(OutputStream.StdOut, line);
        foreach (var line in stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries)) onOutput?.Invoke(OutputStream.StdErr, line);
        return Task.FromResult(new ProcessResult(exit, TimeSpan.Zero, false, false));
    }
}
