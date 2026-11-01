using System.Buffers.Binary;
using System.Formats.Asn1;
using System.Security.Cryptography.X509Certificates;

namespace HCG.ExecutableMaker.Packaging.Mobile;

/// <summary>
/// Reads which Android signature schemes are present (v1 JAR, v2, v3, v3.1) and the first signer certificate.
/// This establishes presence and signer identity only; cryptographic verification is done by apksigner when available.
/// </summary>
public static class ApkSignature
{
    private const uint V2Id = 0x7109871a, V3Id = 0xf05368c0, V31Id = 0x1b93ad61;
    private static readonly byte[] Magic = "APK Sig Block 42"u8.ToArray();

    public sealed record Info(IReadOnlyList<string> Schemes, X509Certificate2? Certificate)
    {
        public bool IsSigned => Schemes.Count > 0;
        public bool IsDebugCertificate => Certificate?.Subject.Contains("CN=Android Debug", StringComparison.Ordinal) == true;
    }

    public static Info Read(Stream apk, IEnumerable<string> entryNames)
    {
        var schemes = new List<string>();
        X509Certificate2? cert = null;
        var names = entryNames.ToList();
        var hasV1 = names.Any(n => n.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".SF", StringComparison.OrdinalIgnoreCase))
                    && names.Any(n => n.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase) && (n.EndsWith(".RSA", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".DSA", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".EC", StringComparison.OrdinalIgnoreCase)));
        if (hasV1) schemes.Add("v1 (JAR)");

        foreach (var (id, value) in SigningBlock(apk))
        {
            var scheme = id switch { V2Id => "v2", V3Id => "v3", V31Id => "v3.1", _ => null };
            if (scheme is null) continue;
            schemes.Add(scheme);
            cert ??= FirstCertificate(value);
        }
        return new Info(schemes, cert);
    }

    /// <summary>
    /// Reads the v1 signer certificate from a META-INF/*.RSA|DSA|EC PKCS#7 SignedData block. The ASN.1 is walked
    /// directly (System.Formats.Asn1) so that untrusted input is never handed to a content-sniffing loader that would
    /// also accept PKCS#12.
    /// </summary>
    public static X509Certificate2? FromPkcs7(byte[] pkcs7)
    {
        try
        {
            var contentInfo = new AsnReader(pkcs7, AsnEncodingRules.BER).ReadSequence();
            if (contentInfo.ReadObjectIdentifier() != "1.2.840.113549.1.7.2") return null; // signedData
            var signedData = contentInfo.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)).ReadSequence();
            signedData.ReadInteger();                 // version
            signedData.ReadSetOf(skipSortOrderValidation: true); // digestAlgorithms
            signedData.ReadSequence();                // encapContentInfo
            if (!signedData.HasData || !signedData.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0))) return null;
            var certificates = signedData.ReadSetOf(skipSortOrderValidation: true, expectedTag: new Asn1Tag(TagClass.ContextSpecific, 0, true));
            return certificates.HasData ? X509CertificateLoader.LoadCertificate(certificates.ReadEncodedValue().Span) : null;
        }
        catch (Exception ex) when (ex is AsnContentException or System.Security.Cryptography.CryptographicException or ArgumentException) { return null; }
    }

    /// <summary>ID/value pairs of the APK Signing Block that precedes the ZIP central directory.</summary>
    public static IReadOnlyList<(uint Id, byte[] Value)> SigningBlock(Stream s)
    {
        var result = new List<(uint, byte[])>();
        var length = s.Length;
        if (length < 22) return result;
        // End of central directory: scan the last 64 KiB + 22 bytes.
        var tailSize = (int)Math.Min(length, 65_557);
        var tail = new byte[tailSize];
        s.Seek(length - tailSize, SeekOrigin.Begin);
        s.ReadExactly(tail);
        var eocd = -1;
        for (var i = tailSize - 22; i >= 0; i--)
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) == 0x06054b50) { eocd = i; break; }
        if (eocd < 0) throw new InvalidDataException("ZIP end of central directory not found.");
        long cdOffset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 16));
        if (cdOffset < 32 || cdOffset > length) return result;

        var footer = new byte[24];
        s.Seek(cdOffset - 24, SeekOrigin.Begin);
        s.ReadExactly(footer);
        if (!footer.AsSpan(8).SequenceEqual(Magic)) return result;
        var blockSize = BinaryPrimitives.ReadUInt64LittleEndian(footer);
        if (blockSize < 24 || blockSize > (ulong)cdOffset - 8 || blockSize > 64UL * 1024 * 1024) throw new InvalidDataException("APK Signing Block size is invalid.");
        var blockStart = cdOffset - (long)blockSize - 8;
        var block = new byte[(int)blockSize - 16]; // pairs only (excludes trailing size+magic)
        s.Seek(blockStart + 8, SeekOrigin.Begin);
        s.ReadExactly(block);
        var pos = 0;
        while (pos + 12 <= block.Length)
        {
            var pairLen = BinaryPrimitives.ReadUInt64LittleEndian(block.AsSpan(pos));
            if (pairLen < 4 || pairLen > (ulong)(block.Length - pos - 8)) throw new InvalidDataException("APK Signing Block entry is invalid.");
            var id = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(pos + 8));
            result.Add((id, block.AsSpan(pos + 12, (int)pairLen - 4).ToArray()));
            pos += 8 + (int)pairLen;
        }
        return result;
    }

    /// <summary>v2/v3 value: signers → signer → signed data → certificates → first certificate (all length-prefixed).</summary>
    private static X509Certificate2? FirstCertificate(byte[] value)
    {
        try
        {
            var signers = LengthPrefixed(value, 0, out _);
            var signer = LengthPrefixed(signers, 0, out _);
            var signedData = LengthPrefixed(signer, 0, out _);
            var digests = LengthPrefixed(signedData, 0, out var afterDigests);
            _ = digests;
            var certificates = LengthPrefixed(signedData, afterDigests, out _);
            var first = LengthPrefixed(certificates, 0, out _);
            return X509CertificateLoader.LoadCertificate(first);
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Security.Cryptography.CryptographicException or ArgumentException)
        {
            return null;
        }
    }

    private static byte[] LengthPrefixed(byte[] data, int offset, out int next)
    {
        if (offset + 4 > data.Length) throw new InvalidDataException("Truncated signature structure.");
        var len = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
        if (len < 0 || offset + 4 + len > data.Length) throw new InvalidDataException("Invalid signature structure length.");
        next = offset + 4 + len;
        return data.AsSpan(offset + 4, len).ToArray();
    }
}
