using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using HCG.ExecutableMaker.Packaging.Windows;
using Xunit;

namespace HCG.ExecutableMaker.Tests;

/// <summary>
/// Windows PE facts and Authenticode verification, on every OS. The fixtures are synthetic. The PE images are written
/// here with a real version resource. The signatures use the PKCS#7 layout signtool writes: the content is
/// [0] SpcIndirectDataContent, the messageDigest covers the content's value, and a countersignature timestamp is signed
/// by a second, throwaway certificate. Real signtool/RFC 3161 output is checked on Windows in CI against
/// Get-AuthenticodeSignature.
/// </summary>
public sealed class AuthenticodeTests : IDisposable
{
    private readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    [Fact]
    public void A_PE_reports_its_machine_and_version_resource_and_an_installer_bootstrapper_is_x86()
    {
        var app = TestPe.Write(_t.Combine("HCGMail.exe"), 0x8664, pe32Plus: true, version: ("0.1.0.0", "0.1.0-beta.1", "Hakmi Capital Group", "HCG Mail"));
        var pe = PeFile.Read(app)!;
        Assert.Equal(("x64", true, true, false), (pe.MachineName, pe.IsPe32Plus, pe.IsGui, pe.HasCertificateTable));
        Assert.Equal(("0.1.0-beta.1", "0.1.0.0", "Hakmi Capital Group", "HCG Mail"), (pe.ProductVersion, pe.FileVersion, pe.Version["CompanyName"], pe.Version["ProductName"]));
        Assert.Equal("0.1.0.0", pe.FixedFileVersion);

        var setup = PeFile.Read(TestPe.Write(_t.Combine("HCGMail-0.1.0-win-arm64-Setup.exe"), 0x014C, pe32Plus: false, version: ("6.4.0.0", "0.1.0", "Hakmi Capital Group", "HCG Mail Setup")))!;
        Assert.Equal(("x86", false), (setup.MachineName, setup.IsPe32Plus));
        Assert.Equal("arm64", PeFile.NameOf(0xAA64));
        File.WriteAllText(_t.Combine("readme.txt"), "not a program");
        Assert.Null(PeFile.Read(_t.Combine("readme.txt")));
    }

    [Fact]
    public void An_unsigned_PE_is_unsigned_and_a_non_PE_is_not_checked()
    {
        var r = Authenticode.Inspect(TestPe.Write(_t.Combine("a.exe"), 0x8664, true));
        Assert.Equal(("unsigned", "unsigned", false), (r.Status, r.StoreStatus, r.Signed));
        File.WriteAllBytes(_t.Combine("b.exe"), Encoding.ASCII.GetBytes("MZ not really"));
        Assert.Equal(("not-pe", "not-checked"), (Authenticode.Inspect(_t.Combine("b.exe")).Status, Authenticode.Inspect(_t.Combine("b.exe")).StoreStatus));
    }

    [Fact]
    public void A_valid_signature_by_an_untrusted_certificate_is_intact_but_not_signed_for_the_Store()
    {
        using var signer = TestPe.CodeSigningCertificate("CN=HCG Test Publisher");
        using var tsa = TestPe.CodeSigningCertificate("CN=HCG Test Timestamp Authority");
        var file = TestPe.Write(_t.Combine("signed.exe"), 0x8664, true, version: ("1.2.3.4", "1.2.3", "Example AB", "Example"));
        var at = new DateTimeOffset(2026, 11, 26, 9, 30, 0, TimeSpan.Zero);
        TestPe.Sign(file, signer, tsa, at);

        var r = Authenticode.Inspect(file);
        Assert.Equal((true, true, "SHA256", "HCG Test Publisher"), (r.DigestMatches!.Value, r.SignatureValid!.Value, r.DigestAlgorithm, r.Signer));
        Assert.Equal(signer.Thumbprint, r.Thumbprint);
        Assert.False(r.ChainTrusted);                                          // self-signed: not trusted by this computer
        Assert.Equal(("signed-untrusted", "invalid"), (r.Status, r.StoreStatus));
        Assert.Equal(("countersignature", at, "HCG Test Timestamp Authority", true), (r.Timestamp!.Kind, r.Timestamp.Time, r.Timestamp.Authority, r.Timestamp.Verified));
        Assert.Contains("NOT trusted on this computer", r.Summary);
        Assert.Empty(r.Problems);
        // The PE facts survive signing; the certificate table is found.
        Assert.True(PeFile.Read(file)!.HasCertificateTable);
    }

    [Fact]
    public void A_file_changed_after_signing_or_a_forged_signature_is_invalid()
    {
        using var signer = TestPe.CodeSigningCertificate("CN=HCG Test Publisher");
        var file = TestPe.Write(_t.Combine("tampered.exe"), 0x8664, true);
        TestPe.Sign(file, signer, null, null);
        Assert.Null(Authenticode.Inspect(file).Timestamp);
        Assert.Contains("no timestamp", Authenticode.Inspect(file).Summary);

        var bytes = File.ReadAllBytes(file);
        bytes[0x210] ^= 0xFF;                                                 // a byte of the section's code
        File.WriteAllBytes(file, bytes);
        var changed = Authenticode.Inspect(file);
        Assert.Equal(("invalid", false), (changed.Status, changed.DigestMatches));
        Assert.Contains(changed.Problems, p => p.Contains("changed after signing", StringComparison.Ordinal));

        var forged = TestPe.Write(_t.Combine("forged.exe"), 0x8664, true);
        TestPe.Sign(forged, signer, null, null, corruptSignature: true);
        var f = Authenticode.Inspect(forged);
        Assert.Equal(("invalid", true, false), (f.Status, f.DigestMatches, f.SignatureValid));

        // The checksum and the certificate-table entry are not covered: rewriting them keeps the signature intact.
        var ok = TestPe.Write(_t.Combine("checksum.exe"), 0x8664, true);
        TestPe.Sign(ok, signer, null, null);
        var pe = PeFile.Read(ok)!;
        var b2 = File.ReadAllBytes(ok);
        BitConverter.GetBytes(0x12345678u).CopyTo(b2, (int)pe.CheckSumOffset);
        File.WriteAllBytes(ok, b2);
        Assert.Equal((true, true), (Authenticode.Inspect(ok).DigestMatches, Authenticode.Inspect(ok).SignatureValid));
    }
}

/// <summary>Synthetic PE images and a signtool-shaped Authenticode signer, for tests only.</summary>
internal static class TestPe
{
    public static string Write(string path, ushort machine, bool pe32Plus, (string File, string Product, string Company, string Name)? version = null)
    {
        var optSize = pe32Plus ? 240 : 224;
        var rsrc = Resources(version ?? ("1.0.0.0", "1.0.0", "Example", "Example"), 0x2000);
        var file = new byte[0x200 + 0x200 + Align(rsrc.Length, 0x200)];
        void U16(int at, int v) => BitConverter.GetBytes((ushort)v).CopyTo(file, at);
        void U32(int at, long v) => BitConverter.GetBytes((uint)v).CopyTo(file, at);
        file[0] = (byte)'M'; file[1] = (byte)'Z';
        U32(0x3C, 0x40);
        Encoding.ASCII.GetBytes("PE\0\0").CopyTo(file, 0x40);
        U16(0x44, machine); U16(0x46, 2); U16(0x54, optSize); U16(0x56, 0x0022);
        var opt = 0x58;
        U16(opt, pe32Plus ? 0x20B : 0x10B);
        U32(opt + 16, 0x1000);                                                // entry point
        U32(opt + 32, 0x1000); U32(opt + 36, 0x200);                          // section / file alignment
        U32(opt + 56, 0x3000); U32(opt + 60, 0x200);                          // image size, headers size
        U16(opt + 68, 2);                                                     // GUI
        var dirs = opt + (pe32Plus ? 112 : 96);
        U32(dirs - 4, 16);
        U32(dirs + 2 * 8, 0x2000); U32(dirs + 2 * 8 + 4, rsrc.Length);       // resource table
        var sec = opt + optSize;
        Encoding.ASCII.GetBytes(".text\0\0\0").CopyTo(file, sec); U32(sec + 8, 0x200); U32(sec + 12, 0x1000); U32(sec + 16, 0x200); U32(sec + 20, 0x200);
        Encoding.ASCII.GetBytes(".rsrc\0\0\0").CopyTo(file, sec + 40); U32(sec + 48, rsrc.Length); U32(sec + 52, 0x2000); U32(sec + 56, Align(rsrc.Length, 0x200)); U32(sec + 60, 0x400);
        for (var i = 0; i < 0x200; i++) file[0x200 + i] = (byte)(0xCC ^ i);
        rsrc.CopyTo(file, 0x400);
        File.WriteAllBytes(path, file);
        return path;
    }

    private static int Align(int x, int a) => (x + a - 1) / a * a;

    /// <summary>A resource section with one RT_VERSION entry (VS_VERSIONINFO, en-US).</summary>
    private static byte[] Resources((string File, string Product, string Company, string Name) v, int rva)
    {
        var info = VersionInfo(v);
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        void Dir(uint id, uint offset) { w.Write(0u); w.Write(0u); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)1); w.Write(id); w.Write(offset); }
        Dir(16, 0x80000000 | 24);
        Dir(1, 0x80000000 | 48);
        Dir(0x409, 72);
        w.Write((uint)(rva + 88)); w.Write((uint)info.Length); w.Write(0u); w.Write(0u);
        w.Write(info);
        return ms.ToArray();
    }

    private static byte[] VersionInfo((string File, string Product, string Company, string Name) v)
    {
        static (uint, uint) Parts(string s)
        {
            var p = s.Split('.').Select(x => ushort.TryParse(x, out var n) ? n : (ushort)0).Concat(Enumerable.Repeat((ushort)0, 4)).ToArray();
            return ((uint)(p[0] << 16 | p[1]), (uint)(p[2] << 16 | p[3]));
        }
        var fixedInfo = new MemoryStream();
        var fw = new BinaryWriter(fixedInfo);
        var (fms, fls) = Parts(v.File);
        var (pms, pls) = Parts(v.Product.Split('-')[0]);
        foreach (var x in new uint[] { 0xFEEF04BD, 0x00010000, fms, fls, pms, pls, 0x3F, 0, 0x40004, 1, 0, 0, 0 }) fw.Write(x);
        var strings = new[] { ("CompanyName", v.Company), ("FileVersion", v.File), ("ProductName", v.Name), ("ProductVersion", v.Product) }
            .Select(s => Block(s.Item1, Encoding.Unicode.GetBytes(s.Item2 + "\0"), 1, (s.Item2.Length + 1), [])).ToList();
        var table = Block("040904B0", [], 1, 0, strings);
        var sfi = Block("StringFileInfo", [], 1, 0, [table]);
        return Block("VS_VERSION_INFO", fixedInfo.ToArray(), 0, 52, [sfi]);
    }

    private static byte[] Block(string key, byte[] value, ushort type, int valueLength, IReadOnlyList<byte[]> children)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write((ushort)0); w.Write((ushort)valueLength); w.Write(type);
        w.Write(Encoding.Unicode.GetBytes(key + "\0"));
        while (ms.Length % 4 != 0) w.Write((byte)0);
        w.Write(value);
        foreach (var c in children) { while (ms.Length % 4 != 0) w.Write((byte)0); w.Write(c); }
        var b = ms.ToArray();
        BitConverter.GetBytes((ushort)b.Length).CopyTo(b, 0);
        return b;
    }

    public static X509Certificate2 CodeSigningCertificate(string subject)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.3"), new Oid("1.3.6.1.5.5.7.3.8")], false));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    /// <summary>Signs like signtool: pads to 8 bytes, hashes the image, appends a WIN_CERTIFICATE with PKCS#7 SignedData.</summary>
    public static void Sign(string path, X509Certificate2 signer, X509Certificate2? tsa, DateTimeOffset? at, bool corruptSignature = false)
    {
        var bytes = File.ReadAllBytes(path).ToList();
        while (bytes.Count % 8 != 0) bytes.Add(0);
        File.WriteAllBytes(path, bytes.ToArray());
        var pe = PeFile.Read(path)!;
        var digest = Authenticode.ImageHash(path, pe, HashAlgorithmName.SHA256);

        // SpcIndirectDataContent { SpcAttributeTypeAndOptionalValue { SPC_PE_IMAGE_DATA, SpcPeImageData }, DigestInfo }
        var spc = new AsnWriter(AsnEncodingRules.DER);
        using (spc.PushSequence())
        {
            using (spc.PushSequence())
            {
                spc.WriteObjectIdentifier("1.3.6.1.4.1.311.2.1.15");
                using (spc.PushSequence()) { spc.WriteBitString([]); }
            }
            using (spc.PushSequence())
            {
                using (spc.PushSequence()) { spc.WriteObjectIdentifier("2.16.840.1.101.3.4.2.1"); spc.WriteNull(); }
                spc.WriteOctetString(digest);
            }
        }
        var spcDer = spc.Encode();
        var spcValue = new AsnReader(spcDer, AsnEncodingRules.DER).PeekContentBytes().ToArray();

        var attrs = SignedAttributes(("1.2.840.113549.1.9.3", W => W.WriteObjectIdentifier("1.3.6.1.4.1.311.2.1.4")),
            ("1.2.840.113549.1.9.4", W => W.WriteOctetString(SHA256.HashData(spcValue))));
        using var key = signer.GetRSAPrivateKey()!;
        var signature = key.SignData(attrs, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        if (corruptSignature) signature[10] ^= 0x01;

        byte[]? counter = null;
        if (tsa is not null)
        {
            var tsAttrs = SignedAttributes(("1.2.840.113549.1.9.3", W => W.WriteObjectIdentifier("1.2.840.113549.1.7.1")),
                ("1.2.840.113549.1.9.5", W => W.WriteUtcTime(at!.Value)),
                ("1.2.840.113549.1.9.4", W => W.WriteOctetString(SHA256.HashData(signature))));
            using var tk = tsa.GetRSAPrivateKey()!;
            counter = SignerInfo(tsa, tsAttrs, tk.SignData(tsAttrs, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1), null);
        }

        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        {
            w.WriteObjectIdentifier("1.2.840.113549.1.7.2");
            using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
            using (w.PushSequence())
            {
                w.WriteInteger(1);
                using (w.PushSetOf()) using (w.PushSequence()) { w.WriteObjectIdentifier("2.16.840.1.101.3.4.2.1"); w.WriteNull(); }
                using (w.PushSequence())
                {
                    w.WriteObjectIdentifier("1.3.6.1.4.1.311.2.1.4");
                    using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0))) w.WriteEncodedValue(spcDer);
                }
                using (w.PushSetOf(new Asn1Tag(TagClass.ContextSpecific, 0)))
                {
                    w.WriteEncodedValue(signer.RawData);
                    if (tsa is not null) w.WriteEncodedValue(tsa.RawData);
                }
                using (w.PushSetOf()) w.WriteEncodedValue(SignerInfo(signer, attrs, signature, counter));
            }
        }
        var pkcs7 = w.Encode();
        var certLength = 8 + pkcs7.Length;
        var padded = (certLength + 7) / 8 * 8;
        var entry = new byte[padded];
        BitConverter.GetBytes(padded).CopyTo(entry, 0);
        BitConverter.GetBytes((ushort)0x0200).CopyTo(entry, 4);
        BitConverter.GetBytes((ushort)0x0002).CopyTo(entry, 6);
        pkcs7.CopyTo(entry, 8);
        var file = File.ReadAllBytes(path);
        var offset = file.Length;
        BitConverter.GetBytes((uint)offset).CopyTo(file, (int)pe.CertificateDirectoryOffset);
        BitConverter.GetBytes((uint)padded).CopyTo(file, (int)pe.CertificateDirectoryOffset + 4);
        File.WriteAllBytes(path, [.. file, .. entry]);
    }

    /// <summary>The signed attributes as a universal SET (what is signed); stored as [0] IMPLICIT.</summary>
    private static byte[] SignedAttributes(params (string Oid, Action<AsnWriter> Value)[] attributes)
    {
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSetOf())
            foreach (var (oid, value) in attributes)
                using (w.PushSequence())
                {
                    w.WriteObjectIdentifier(oid);
                    using (w.PushSetOf()) value(w);
                }
        return w.Encode();
    }

    private static byte[] SignerInfo(X509Certificate2 cert, byte[] signedAttributes, byte[] signature, byte[]? counterSignature)
    {
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        {
            w.WriteInteger(1);
            using (w.PushSequence()) { w.WriteEncodedValue(cert.IssuerName.RawData); w.WriteIntegerUnsigned(cert.SerialNumberBytes.Span); }
            using (w.PushSequence()) { w.WriteObjectIdentifier("2.16.840.1.101.3.4.2.1"); w.WriteNull(); }
            var implicitAttrs = (byte[])signedAttributes.Clone();
            implicitAttrs[0] = 0xA0;
            w.WriteEncodedValue(implicitAttrs);
            using (w.PushSequence()) { w.WriteObjectIdentifier("1.2.840.113549.1.1.1"); w.WriteNull(); }
            w.WriteOctetString(signature);
            if (counterSignature is not null)
                using (w.PushSetOf(new Asn1Tag(TagClass.ContextSpecific, 1)))
                using (w.PushSequence())
                {
                    w.WriteObjectIdentifier("1.2.840.113549.1.9.6");
                    using (w.PushSetOf()) w.WriteEncodedValue(counterSignature);
                }
        }
        return w.Encode();
    }
}
