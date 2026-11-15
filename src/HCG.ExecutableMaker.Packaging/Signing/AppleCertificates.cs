using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.Packaging.Signing;

/// <summary>Reads Apple developer certificates (public parts only). Never touches private keys.</summary>
public static class AppleCertificates
{
    private const string OidCommonName = "2.5.4.3";
    private const string OidOrganization = "2.5.4.10";
    private const string OidOrganizationalUnit = "2.5.4.11";

    /// <summary>Issuers of Apple developer certificates (Worldwide Developer Relations and Developer ID CAs).</summary>
    private static readonly string[] AppleDeveloperIssuers = ["Apple Worldwide Developer Relations", "Developer ID Certification Authority"];

    public static AppleCertificateInfo Describe(X509Certificate2 cert, bool? hasPrivateKey)
    {
        var cn = Attribute(cert.SubjectName, OidCommonName) ?? cert.GetNameInfo(X509NameType.SimpleName, false);
        var ou = Attribute(cert.SubjectName, OidOrganizationalUnit);
        return new AppleCertificateInfo
        {
            Sha1 = Convert.ToHexString(SHA1.HashData(cert.RawData)),
            Sha256 = Convert.ToHexString(SHA256.HashData(cert.RawData)),
            CommonName = cn,
            Kind = AppleCertificateNames.Classify(cn),
            TeamId = ou is { Length: 10 } && ou.All(char.IsAsciiLetterOrDigit) ? ou : TeamFromName(cn),
            TeamName = Attribute(cert.SubjectName, OidOrganization),
            IssuerCommonName = Attribute(cert.IssuerName, OidCommonName),
            SerialNumber = cert.SerialNumber,
            NotBefore = new DateTimeOffset(cert.NotBefore.ToUniversalTime(), TimeSpan.Zero),
            NotAfter = new DateTimeOffset(cert.NotAfter.ToUniversalTime(), TimeSpan.Zero),
            HasPrivateKey = hasPrivateKey,
        };
    }

    public static AppleCertificateInfo? TryDescribe(byte[] der, bool? hasPrivateKey)
    {
        try
        {
            using var cert = X509CertificateLoader.LoadCertificate(der);
            return Describe(cert, hasPrivateKey);
        }
        catch (CryptographicException) { return null; }
    }

    /// <summary>An Apple code-signing or installer certificate (by its name or its Apple developer issuer).</summary>
    public static bool IsAppleDeveloperCertificate(AppleCertificateInfo c) =>
        c.Kind != AppleCertificateKind.Other
        || (c.IssuerCommonName is { } i && AppleDeveloperIssuers.Any(a => i.Contains(a, StringComparison.Ordinal)));

    /// <summary>"Apple Development: Jane Doe (ABCDE12345)" → "ABCDE12345" (older certificates put the team in the name).</summary>
    public static string? TeamFromName(string commonName)
    {
        var open = commonName.LastIndexOf('(');
        if (open < 0 || !commonName.EndsWith(')')) return null;
        var team = commonName[(open + 1)..^1];
        return team.Length == 10 && team.All(char.IsAsciiLetterOrDigit) ? team : null;
    }

    /// <summary>Splits PEM output (e.g. from <c>security find-certificate -p</c>) into DER certificates.</summary>
    public static IReadOnlyList<byte[]> ParsePem(string text)
    {
        var list = new List<byte[]>();
        const string begin = "-----BEGIN CERTIFICATE-----", end = "-----END CERTIFICATE-----";
        var at = 0;
        while ((at = text.IndexOf(begin, at, StringComparison.Ordinal)) >= 0)
        {
            var stop = text.IndexOf(end, at, StringComparison.Ordinal);
            if (stop < 0) break;
            var body = text[(at + begin.Length)..stop];
            try { list.Add(Convert.FromBase64String(string.Concat(body.Where(c => !char.IsWhiteSpace(c))))); }
            catch (FormatException) { }
            at = stop + end.Length;
        }
        return list;
    }

    private static string? Attribute(X500DistinguishedName name, string oid)
    {
        foreach (var rdn in name.EnumerateRelativeDistinguishedNames())
            if (rdn.GetSingleElementType().Value == oid) return rdn.GetSingleElementValue();
        return null;
    }

    /// <summary>Renders an entitlement value as text (used for display, comparison and account metadata — never secrets).</summary>
    public static string RenderValue(object? value) => value switch
    {
        null => "",
        bool b => b ? "true" : "false",
        string s => s,
        long or int or double => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "",
        IEnumerable<object> list => string.Join(", ", list.Select(RenderValue)),
        Dictionary<string, object> => "{…}",
        byte[] bytes => $"<{bytes.Length} bytes>",
        _ => value.ToString() ?? "",
    };

    internal static string Hex(byte[] bytes) => Convert.ToHexString(bytes);
    internal static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
}
