using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace HCG.ExecutableMaker.Packaging.Windows;

/// <summary>An Authenticode signature's timestamp: an RFC 3161 token or a legacy countersignature.</summary>
public sealed record AuthenticodeTimestamp(string Kind, DateTimeOffset? Time, string? Authority, bool Verified, string? Problem);

/// <summary>
/// What a Windows PE file's Authenticode signature proves. It is verified the same way on every OS, from the file
/// alone:
/// <list type="bullet">
/// <item>the PE image digest is recomputed and compared with the signed one, so a changed file fails;</item>
/// <item>the signer's signature over the signed attributes is checked with its certificate;</item>
/// <item>the timestamp (RFC 3161 or countersignature) is checked against the signature it stamps;</item>
/// <item>the certificate chain is built for code signing with this computer's trust store.</item>
/// </list>
/// Trust is reported apart from integrity. An intact signature by a certificate this computer does not trust (a
/// self-signed test certificate, an unknown root) is <c>signed-untrusted</c>, not <c>signed</c>.
/// </summary>
public sealed record AuthenticodeReport
{
    public required string Path { get; init; }
    /// <summary>unsigned, signed, signed-untrusted, invalid, or not-pe.</summary>
    public required string Status { get; init; }
    public string? Machine { get; init; }
    public string? DigestAlgorithm { get; init; }
    public bool? DigestMatches { get; init; }
    public bool? SignatureValid { get; init; }
    public string? Signer { get; init; }
    public string? Issuer { get; init; }
    public string? Thumbprint { get; init; }
    public DateTimeOffset? NotBefore { get; init; }
    public DateTimeOffset? NotAfter { get; init; }
    public bool? ChainTrusted { get; init; }
    public string? ChainDetail { get; init; }
    public AuthenticodeTimestamp? Timestamp { get; init; }
    /// <summary>Windows' own verdict (Get-AuthenticodeSignature → WinVerifyTrust), when read on Windows.</summary>
    public string? WindowsVerdict { get; init; }
    public IReadOnlyList<string> Problems { get; init; } = [];

    public bool Signed => Status is "signed" or "signed-untrusted";

    /// <summary>HCG Store's signing status: signed only when intact and trusted; an untrusted or broken signature is invalid.</summary>
    public string StoreStatus => Status switch { "signed" => "signed", "unsigned" => "unsigned", "not-pe" => "not-checked", _ => "invalid" };

    public string Summary => Status switch
    {
        "unsigned" => "not Authenticode signed",
        "not-pe" => "not a Windows PE file",
        "invalid" => "Authenticode signature INVALID: " + string.Join("; ", Problems),
        _ => $"Authenticode {(Status == "signed" ? "signed" : "signed, NOT trusted on this computer")} by {Signer}"
             + (Timestamp is { } t ? $", timestamped {t.Time:yyyy-MM-dd HH:mm:ss}Z by {t.Authority}{(t.Verified ? "" : " (timestamp NOT verified)")}" : ", no timestamp (the signature lapses when the certificate expires)"),
    };
}

public static class Authenticode
{
    private const string SpcIndirectData = "1.3.6.1.4.1.311.2.1.4";
    private const string SignedDataOid = "1.2.840.113549.1.7.2";
    private const string MessageDigestOid = "1.2.840.113549.1.9.4";
    private const string SigningTimeOid = "1.2.840.113549.1.9.5";
    private const string CounterSignatureOid = "1.2.840.113549.1.9.6";
    private const string Rfc3161Oid = "1.3.6.1.4.1.311.3.3.1";
    private const string CodeSigningEku = "1.3.6.1.5.5.7.3.3";

    public static AuthenticodeReport Inspect(string path, bool buildChain = true)
    {
        var pe = PeFile.Read(path);
        if (pe is null) return new AuthenticodeReport { Path = path, Status = "not-pe" };
        if (!pe.HasCertificateTable) return new AuthenticodeReport { Path = path, Status = "unsigned", Machine = pe.MachineName };
        var problems = new List<string>();
        var (offset, size) = pe.CertificateTable;
        if (offset + size > pe.Length || size < 8)
            return new AuthenticodeReport { Path = path, Status = "invalid", Machine = pe.MachineName, Problems = ["the certificate table lies outside the file"] };
        byte[] table;
        using (var fs = File.OpenRead(path)) { fs.Position = offset; table = new byte[size]; fs.ReadExactly(table); }
        var length = BitConverter.ToInt32(table, 0);
        var revision = BitConverter.ToUInt16(table, 4);
        var type = BitConverter.ToUInt16(table, 6);
        if (type != 2 || length < 8 || length > size)
            return new AuthenticodeReport { Path = path, Status = "invalid", Machine = pe.MachineName, Problems = [$"the certificate table holds no PKCS#7 signature (type {type}, revision 0x{revision:X})"] };
        try
        {
            return Verify(path, pe, table.AsMemory(8, length - 8), buildChain);
        }
        catch (Exception ex) when (ex is AsnContentException or CryptographicException or FormatException or ArgumentException or IndexOutOfRangeException)
        {
            return new AuthenticodeReport { Path = path, Status = "invalid", Machine = pe.MachineName, Problems = ["the signature cannot be read: " + ex.Message] };
        }
    }

    private static AuthenticodeReport Verify(string path, PeFile pe, ReadOnlyMemory<byte> pkcs7, bool buildChain)
    {
        var problems = new List<string>();
        var sd = SignedDataParts.Read(pkcs7);
        if (sd.ContentType != SpcIndirectData) problems.Add($"the signed content is {sd.ContentType}, not Authenticode SpcIndirectDataContent");
        // SpcIndirectDataContent ::= SEQUENCE { data SpcAttributeTypeAndOptionalValue, messageDigest DigestInfo }
        var spc = new AsnReader(sd.Content, AsnEncodingRules.BER).ReadSequence();
        spc.ReadEncodedValue();
        var digestInfo = spc.ReadSequence();
        var digestOid = digestInfo.ReadSequence().ReadObjectIdentifier();
        var signedDigest = digestInfo.ReadOctetString();
        var alg = HashName(digestOid);
        var actual = ImageHash(path, pe, alg);
        var digestMatches = CryptographicOperations.FixedTimeEquals(actual, signedDigest);
        if (!digestMatches) problems.Add("the file was changed after signing: its PE image digest differs from the signed digest");

        var signer = sd.Signer;
        var cert = sd.Certificates.FirstOrDefault(signer.Matches);
        bool signatureValid = false;
        if (cert is null) problems.Add("the signer's certificate is not in the signature");
        else
        {
            // Authenticode's messageDigest covers the content's value (the SEQUENCE's contents, without its tag and length).
            var md = signer.SignedAttribute(MessageDigestOid) is { } mdv ? new AsnReader(mdv, AsnEncodingRules.BER).ReadOctetString() : null;
            var contentHash = Hash(signer.DigestAlgorithm, sd.ContentValue.Span);
            var contentHashWithHeader = Hash(signer.DigestAlgorithm, sd.Content.Span);
            if (md is null) problems.Add("the signature has no messageDigest attribute");
            else if (!md.AsSpan().SequenceEqual(contentHash) && !md.AsSpan().SequenceEqual(contentHashWithHeader)) problems.Add("the signed attributes do not match the signed content");
            else if (!(signatureValid = VerifySignature(cert, signer.DigestAlgorithm, signer.SignatureAlgorithm, signer.SignedAttributesForSignature, signer.Signature.Span)))
                problems.Add("the signer's signature does not verify with its certificate");
        }

        var timestamp = ReadTimestamp(signer, sd.Certificates);
        bool? trusted = null;
        string? chainDetail = null;
        if (cert is not null && buildChain)
        {
            using var chain = new X509Chain();
            chain.ChainPolicy.ExtraStore.AddRange(sd.Certificates.ToArray());
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid(CodeSigningEku));
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            // A timestamped signature stays valid after the certificate expires: check the chain at the signing time.
            if (timestamp is { Verified: true, Time: { } at }) chain.ChainPolicy.VerificationTime = at.UtcDateTime;
            trusted = chain.Build(cert);
            chainDetail = trusted == true
                ? $"chain to {chain.ChainElements[^1].Certificate.Subject} (code signing; revocation not checked)"
                : string.Join("; ", chain.ChainStatus.Select(s => s.StatusInformation.Trim()).Where(s => s.Length > 0).Distinct()) is { Length: > 0 } why ? why : "chain not trusted";
        }
        var status = problems.Count > 0 || !signatureValid || !digestMatches ? "invalid" : trusted == false ? "signed-untrusted" : "signed";
        return new AuthenticodeReport
        {
            Path = path, Status = status, Machine = pe.MachineName, DigestAlgorithm = alg.Name, DigestMatches = digestMatches, SignatureValid = signatureValid,
            Signer = cert?.GetNameInfo(X509NameType.SimpleName, false), Issuer = cert?.GetNameInfo(X509NameType.SimpleName, true), Thumbprint = cert?.Thumbprint,
            NotBefore = cert?.NotBefore.ToUniversalTime(), NotAfter = cert?.NotAfter.ToUniversalTime(), ChainTrusted = trusted, ChainDetail = chainDetail,
            Timestamp = timestamp, Problems = problems,
        };
    }

    /// <summary>
    /// The Authenticode PE image hash: the whole file in order, except the optional header's CheckSum, the certificate
    /// table's directory entry and the certificate table itself.
    /// </summary>
    public static byte[] ImageHash(string path, PeFile pe, HashAlgorithmName alg)
    {
        using var h = IncrementalHash.CreateHash(alg);
        using var fs = File.OpenRead(path);
        var (certAt, certSize) = pe.HasCertificateTable ? pe.CertificateTable : (pe.Length, 0);
        void Range(long from, long to)
        {
            fs.Position = from;
            var buffer = new byte[81920];
            for (var left = to - from; left > 0;)
            {
                var n = fs.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                if (n <= 0) throw new EndOfStreamException();
                h.AppendData(buffer, 0, n);
                left -= n;
            }
        }
        Range(0, pe.CheckSumOffset);
        Range(pe.CheckSumOffset + 4, pe.CertificateDirectoryOffset);
        Range(pe.CertificateDirectoryOffset + 8, certAt);
        Range(certAt + certSize, pe.Length);
        return h.GetHashAndReset();
    }

    private static AuthenticodeTimestamp? ReadTimestamp(SignerParts signer, IReadOnlyList<X509Certificate2> certificates)
    {
        if (signer.UnsignedAttribute(Rfc3161Oid) is { } token)
        {
            if (!Rfc3161TimestampToken.TryDecode(token, out var t, out _))
                return new AuthenticodeTimestamp("rfc3161", null, null, false, "the RFC 3161 timestamp token cannot be decoded");
            var ok = t.VerifySignatureForData(signer.Signature.Span, out var tsa, [.. certificates]);
            return new AuthenticodeTimestamp("rfc3161", t.TokenInfo.Timestamp.ToUniversalTime(), tsa?.GetNameInfo(X509NameType.SimpleName, false), ok,
                ok ? null : "the timestamp does not verify against this signature");
        }
        if (signer.UnsignedAttribute(CounterSignatureOid) is { } counter)
        {
            var cs = SignerParts.Read(new AsnReader(counter, AsnEncodingRules.BER));
            var time = cs.SignedAttribute(SigningTimeOid) is { } st ? ReadTime(st) : null;
            var cert = certificates.FirstOrDefault(cs.Matches);
            var md = cs.SignedAttribute(MessageDigestOid) is { } mdv ? new AsnReader(mdv, AsnEncodingRules.BER).ReadOctetString() : null;
            var ok = cert is not null && md is not null && md.AsSpan().SequenceEqual(Hash(cs.DigestAlgorithm, signer.Signature.Span))
                     && VerifySignature(cert, cs.DigestAlgorithm, cs.SignatureAlgorithm, cs.SignedAttributesForSignature, cs.Signature.Span);
            return new AuthenticodeTimestamp("countersignature", time, cert?.GetNameInfo(X509NameType.SimpleName, false), ok,
                ok ? null : "the countersignature does not verify against this signature");
        }
        return null;
    }

    private static DateTimeOffset? ReadTime(ReadOnlyMemory<byte> value)
    {
        var r = new AsnReader(value, AsnEncodingRules.BER);
        return r.PeekTag().TagValue == (int)UniversalTagNumber.UtcTime ? r.ReadUtcTime() : r.ReadGeneralizedTime();
    }

    private static bool VerifySignature(X509Certificate2 cert, HashAlgorithmName digest, string signatureOid, ReadOnlyMemory<byte> data, ReadOnlySpan<byte> signature)
    {
        if (cert.GetRSAPublicKey() is { } rsa)
            using (rsa) return rsa.VerifyData(data.Span, signature, digest, RSASignaturePadding.Pkcs1);
        if (cert.GetECDsaPublicKey() is { } ec)
            using (ec) return ec.VerifyData(data.Span, signature, digest, DSASignatureFormat.Rfc3279DerSequence);
        throw new CryptographicException($"unsupported signature algorithm {signatureOid}");
    }

    internal static HashAlgorithmName HashName(string oid) => oid switch
    {
        "2.16.840.1.101.3.4.2.1" => HashAlgorithmName.SHA256,
        "2.16.840.1.101.3.4.2.2" => HashAlgorithmName.SHA384,
        "2.16.840.1.101.3.4.2.3" => HashAlgorithmName.SHA512,
        "1.3.14.3.2.26" => HashAlgorithmName.SHA1,
        "1.2.840.113549.2.5" => HashAlgorithmName.MD5,
        _ => throw new CryptographicException($"unsupported digest algorithm {oid}"),
    };

    private static byte[] Hash(HashAlgorithmName alg, ReadOnlySpan<byte> data)
    {
        using var h = IncrementalHash.CreateHash(alg);
        h.AppendData(data);
        return h.GetHashAndReset();
    }

    /// <summary>The parts of a CMS SignedData this verifier needs, read with the ASN.1 reader (no platform APIs).</summary>
    private sealed record SignedDataParts(string ContentType, ReadOnlyMemory<byte> Content, ReadOnlyMemory<byte> ContentValue, IReadOnlyList<X509Certificate2> Certificates, SignerParts Signer)
    {
        public static SignedDataParts Read(ReadOnlyMemory<byte> der)
        {
            var ci = new AsnReader(der, AsnEncodingRules.BER).ReadSequence();
            if (ci.ReadObjectIdentifier() != SignedDataOid) throw new CryptographicException("not a PKCS#7 SignedData");
            var sd = ci.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadSequence();
            sd.ReadInteger();
            sd.ReadSetOf();
            var encap = sd.ReadSequence();
            var type = encap.ReadObjectIdentifier();
            var content = encap.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadEncodedValue();
            var inner = new AsnReader(content, AsnEncodingRules.BER);
            var value = inner.PeekContentBytes();
            var certs = new List<X509Certificate2>();
            if (sd.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0)))
            {
                var set = sd.ReadSetOf(new Asn1Tag(TagClass.ContextSpecific, 0));
                while (set.HasData)
                {
                    var one = set.ReadEncodedValue();
                    if (one.Span[0] == 0x30) certs.Add(X509CertificateLoader.LoadCertificate(one.Span));
                }
            }
            if (sd.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 1))) sd.ReadEncodedValue();
            var signers = sd.ReadSetOf();
            return new SignedDataParts(type, content, value, certs, SignerParts.Read(signers));
        }
    }

    private sealed record SignerParts(ReadOnlyMemory<byte> Sid, HashAlgorithmName DigestAlgorithm, ReadOnlyMemory<byte> SignedAttributes, string SignatureAlgorithm,
        ReadOnlyMemory<byte> Signature, IReadOnlyList<(string Oid, ReadOnlyMemory<byte> Value)> Signed, IReadOnlyList<(string Oid, ReadOnlyMemory<byte> Value)> Unsigned)
    {
        /// <summary>The signed attributes as signed: the [0] IMPLICIT SET re-tagged as a universal SET.</summary>
        public ReadOnlyMemory<byte> SignedAttributesForSignature
        {
            get { var b = SignedAttributes.ToArray(); b[0] = 0x31; return b; }
        }

        public ReadOnlyMemory<byte>? SignedAttribute(string oid) => Signed.Where(a => a.Oid == oid).Select(a => (ReadOnlyMemory<byte>?)a.Value).FirstOrDefault();
        public ReadOnlyMemory<byte>? UnsignedAttribute(string oid) => Unsigned.Where(a => a.Oid == oid).Select(a => (ReadOnlyMemory<byte>?)a.Value).FirstOrDefault();

        public bool Matches(X509Certificate2 cert)
        {
            var r = new AsnReader(Sid, AsnEncodingRules.BER);
            if (r.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0)))
            {
                var ski = r.ReadOctetString(new Asn1Tag(TagClass.ContextSpecific, 0));
                return cert.Extensions.OfType<X509SubjectKeyIdentifierExtension>().Any(e => Convert.FromHexString(e.SubjectKeyIdentifier ?? "").AsSpan().SequenceEqual(ski));
            }
            var ias = r.ReadSequence();
            var issuer = ias.ReadEncodedValue();
            var serial = ias.ReadIntegerBytes();
            return issuer.Span.SequenceEqual(cert.IssuerName.RawData) && serial.Span.SequenceEqual(cert.SerialNumberBytes.Span);
        }

        public static SignerParts Read(AsnReader set)
        {
            var si = set.ReadSequence();
            si.ReadInteger();
            var sid = si.ReadEncodedValue();
            var digest = HashName(si.ReadSequence().ReadObjectIdentifier());
            ReadOnlyMemory<byte> signedRaw = ReadOnlyMemory<byte>.Empty;
            List<(string, ReadOnlyMemory<byte>)> signed = [], unsigned = [];
            if (si.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0)))
            {
                signedRaw = si.PeekEncodedValue();
                signed = Attributes(si.ReadSetOf(new Asn1Tag(TagClass.ContextSpecific, 0)));
            }
            var sigAlg = si.ReadSequence().ReadObjectIdentifier();
            var signature = si.ReadOctetString();
            if (si.HasData && si.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 1)))
                unsigned = Attributes(si.ReadSetOf(new Asn1Tag(TagClass.ContextSpecific, 1)));
            return new SignerParts(sid, digest, signedRaw, sigAlg, signature, signed, unsigned);
        }

        private static List<(string, ReadOnlyMemory<byte>)> Attributes(AsnReader set)
        {
            var list = new List<(string, ReadOnlyMemory<byte>)>();
            while (set.HasData)
            {
                var a = set.ReadSequence();
                var oid = a.ReadObjectIdentifier();
                var values = a.ReadSetOf();
                if (values.HasData) list.Add((oid, values.ReadEncodedValue()));
            }
            return list;
        }
    }
}
