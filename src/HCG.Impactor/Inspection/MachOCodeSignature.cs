using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using HCG.ExecutableMaker.Packaging.Mobile;
using HCG.ExecutableMaker.Packaging.Signing;
using HCG.ExecutableMaker.Packaging.Verification;
using HCG.Impactor.Model;

namespace HCG.Impactor.Inspection;

/// <summary>
/// Reads a Mach-O binary's slices (architecture, platform, minimum OS, FairPlay encryption) and its embedded code
/// signature (LC_CODE_SIGNATURE → SuperBlob: CodeDirectory, entitlements, CMS signature). Portable: pure parsing,
/// no Apple tools. Bounds-checked; a malformed signature yields <see cref="CodeSignatureInfo.Problem"/>, not an exception.
/// </summary>
public static class MachOCodeSignature
{
    private const uint FatMagic = 0xCAFEBABE, FatMagic64 = 0xCAFEBABF;
    private const uint Magic64 = 0xFEEDFACF, Magic32 = 0xFEEDFACE;
    private const uint LcCodeSignature = 0x1D, LcEncryptionInfo = 0x21, LcEncryptionInfo64 = 0x2C;
    private const uint SuperBlobMagic = 0xFADE0CC0, CodeDirectoryMagic = 0xFADE0C02, EntitlementsMagic = 0xFADE7171, BlobWrapperMagic = 0xFADE0B01;
    private const uint CdFlagAdHoc = 0x2;
    private const int MaxSignatureBytes = 16 * 1024 * 1024;

    private sealed record RawSlice(long Offset, long Length, bool Encrypted, long SigOffset, int SigSize);

    /// <summary>Reads slices and the signature of the binary at <paramref name="path"/>. Non-Mach-O files give no slices.</summary>
    public static (IReadOnlyList<BinarySlice> Slices, CodeSignatureInfo Signature) Read(string path)
    {
        IReadOnlyList<AppleMachO.Slice> described;
        try { described = AppleMachO.Read(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ([], CodeSignatureInfo.None with { Problem = "The binary could not be read: " + ex.Message }); }

        List<RawSlice> raw;
        using (var fs = File.OpenRead(path)) raw = RawSlices(fs);
        var slices = new List<BinarySlice>();
        for (var i = 0; i < described.Count; i++)
        {
            var r = i < raw.Count ? raw[i] : null;
            slices.Add(new BinarySlice(described[i].Architecture, described[i].Platform, described[i].MinimumOS, r?.Encrypted ?? false, r is { SigSize: > 0 }));
        }

        // Prefer the arm64 slice's signature (what a current iPhone/iPad runs); fall back to the first signed slice.
        var signed = raw.Select((r, i) => (r, arch: i < described.Count ? described[i].Architecture : "")).Where(x => x.r.SigSize > 0).ToList();
        var pick = signed.FirstOrDefault(x => x.arch == "arm64");
        if (pick.r is null) pick = signed.FirstOrDefault();
        if (pick.r is null) return (slices, CodeSignatureInfo.None);
        using var stream = File.OpenRead(path);
        return (slices, ReadSignature(stream, pick.r));
    }

    /// <summary>True when the file starts with a Mach-O or fat magic number.</summary>
    public static bool IsMachO(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            if (fs.Length < 4) return false;
            Span<byte> head = stackalloc byte[4];
            fs.ReadExactly(head);
            var be = BinaryPrimitives.ReadUInt32BigEndian(head);
            var le = BinaryPrimitives.ReadUInt32LittleEndian(head);
            return be is FatMagic or FatMagic64 || le is Magic64 or Magic32;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static List<RawSlice> RawSlices(FileStream fs)
    {
        var list = new List<RawSlice>();
        if (fs.Length < 32) return list;
        var head = new byte[(int)Math.Min(4096, fs.Length)];
        fs.Position = 0;
        fs.ReadExactly(head);
        var be = BinaryPrimitives.ReadUInt32BigEndian(head);
        if (be is FatMagic or FatMagic64)
        {
            var count = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(4));
            var entry = be == FatMagic ? 20 : 32;
            if (count is 0 or > 16 || 8 + count * entry > head.Length) return list;
            for (var i = 0; i < (int)count; i++)
            {
                var e = head.AsSpan(8 + i * entry);
                long offset = be == FatMagic ? BinaryPrimitives.ReadUInt32BigEndian(e[8..]) : (long)BinaryPrimitives.ReadUInt64BigEndian(e[8..]);
                long size = be == FatMagic ? BinaryPrimitives.ReadUInt32BigEndian(e[12..]) : (long)BinaryPrimitives.ReadUInt64BigEndian(e[16..]);
                if (Thin(fs, offset, size) is { } s) list.Add(s);
            }
            return list;
        }
        if (Thin(fs, 0, fs.Length) is { } thin) list.Add(thin);
        return list;
    }

    private static RawSlice? Thin(FileStream fs, long offset, long length)
    {
        if (offset < 0 || offset + 32 > fs.Length) return null;
        fs.Position = offset;
        var header = new byte[32];
        fs.ReadExactly(header);
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (magic is not (Magic64 or Magic32)) return null;
        var ncmds = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16));
        var sizeofcmds = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20));
        var headerSize = magic == Magic64 ? 32 : 28;
        if (sizeofcmds > 4 * 1024 * 1024 || ncmds > 8192 || offset + headerSize + sizeofcmds > fs.Length) return new RawSlice(offset, length, false, 0, 0);
        fs.Position = offset + headerSize;
        var cmds = new byte[sizeofcmds];
        fs.ReadExactly(cmds);
        bool encrypted = false;
        long sigOff = 0;
        var sigSize = 0;
        var pos = 0;
        for (var i = 0; i < ncmds && pos + 8 <= cmds.Length; i++)
        {
            var cmd = BinaryPrimitives.ReadUInt32LittleEndian(cmds.AsSpan(pos));
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(cmds.AsSpan(pos + 4));
            if (size < 8 || pos + size > cmds.Length) break;
            if ((cmd == LcEncryptionInfo || cmd == LcEncryptionInfo64) && size >= 20)
                encrypted |= BinaryPrimitives.ReadUInt32LittleEndian(cmds.AsSpan(pos + 16)) != 0; // cryptid
            else if (cmd == LcCodeSignature && size >= 16)
            {
                sigOff = BinaryPrimitives.ReadUInt32LittleEndian(cmds.AsSpan(pos + 8));
                sigSize = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(cmds.AsSpan(pos + 12)));
            }
            pos += size;
        }
        return new RawSlice(offset, length, encrypted, sigOff, sigSize);
    }

    private static CodeSignatureInfo ReadSignature(FileStream fs, RawSlice slice)
    {
        if (slice.SigSize > MaxSignatureBytes) return new CodeSignatureInfo { Present = true, Problem = "The code signature is larger than any valid signature (16 MB)." };
        var start = slice.Offset + slice.SigOffset;
        if (start < 0 || start + slice.SigSize > fs.Length) return new CodeSignatureInfo { Present = true, Problem = "The code signature points outside the file (the binary is truncated)." };
        var blob = new byte[slice.SigSize];
        fs.Position = start;
        fs.ReadExactly(blob);
        return ParseSuperBlob(blob);
    }

    /// <summary>Parses an embedded-signature SuperBlob. Exposed for tests (synthetic signatures).</summary>
    internal static CodeSignatureInfo ParseSuperBlob(byte[] blob)
    {
        if (blob.Length < 12 || BinaryPrimitives.ReadUInt32BigEndian(blob) != SuperBlobMagic)
            return new CodeSignatureInfo { Present = true, Problem = "The code signature is not a valid embedded signature (bad SuperBlob)." };
        var count = BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(8));
        if (count > 64 || 12 + count * 8 > blob.Length) return new CodeSignatureInfo { Present = true, Problem = "The code signature's index is malformed." };

        string? identifier = null, team = null, hashType = null;
        var adhoc = false;
        var bestHash = -1;
        IReadOnlyDictionary<string, object> rawEnt = new Dictionary<string, object>();
        AppleCertificateInfoHolder? cms = null;
        string? problem = null;
        for (var i = 0; i < count; i++)
        {
            var type = BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(12 + i * 8));
            var off = (int)BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(16 + i * 8));
            if (off < 0 || off + 8 > blob.Length) { problem = "A code signature entry points outside the signature."; continue; }
            var magic = BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(off));
            var len = (int)BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(off + 4));
            if (len < 8 || off + len > blob.Length) { problem = "A code signature entry is truncated."; continue; }
            var body = blob.AsSpan(off, len);
            if (magic == CodeDirectoryMagic && (type == 0 || type is >= 0x1000 and < 0x1005))
            {
                var cd = CodeDirectory(body);
                if (cd.HashRank > bestHash) { bestHash = cd.HashRank; hashType = cd.HashName; }
                identifier ??= cd.Identifier;
                team ??= cd.Team;
                adhoc |= cd.AdHoc;
            }
            else if (magic == EntitlementsMagic && type == 5)
            {
                try { rawEnt = PlistReader.ParseDictionary(body[8..].ToArray()); }
                catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException or FormatException) { problem = "The embedded entitlements are not a valid property list."; }
            }
            else if (magic == BlobWrapperMagic && type == 0x10000 && len > 8)
                cms = Cms(body[8..].ToArray(), ref problem);
        }
        var entitlementsText = rawEnt.ToDictionary(kv => kv.Key, kv => AppleCertificates.RenderValue(kv.Value), StringComparer.Ordinal);
        return new CodeSignatureInfo
        {
            Present = true,
            AdHoc = adhoc || cms is null,
            Identifier = identifier,
            TeamId = team,
            HashType = hashType,
            Signer = cms?.Leaf,
            Chain = cms?.Chain ?? [],
            SigningTime = cms?.SigningTime,
            Entitlements = entitlementsText,
            RawEntitlements = rawEnt,
            Problem = problem,
        };
    }

    private sealed record CdFacts(string? Identifier, string? Team, bool AdHoc, int HashRank, string? HashName);

    private static CdFacts CodeDirectory(ReadOnlySpan<byte> cd)
    {
        if (cd.Length < 44) return new(null, null, false, -1, null);
        var version = BinaryPrimitives.ReadUInt32BigEndian(cd[8..]);
        var flags = BinaryPrimitives.ReadUInt32BigEndian(cd[12..]);
        var identOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(cd[20..]);
        var hashType = cd[37];
        string? team = null;
        if (version >= 0x20200 && cd.Length >= 52)
        {
            var teamOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(cd[48..]);
            if (teamOffset > 0) team = CString(cd, teamOffset);
        }
        var (rank, name) = hashType switch { 1 => (1, "SHA-1"), 2 => (3, "SHA-256"), 3 => (2, "SHA-256 (truncated)"), 4 => (4, "SHA-384"), _ => (0, $"type {hashType}") };
        return new(CString(cd, identOffset), team, (flags & CdFlagAdHoc) != 0, rank, name);
    }

    private static string? CString(ReadOnlySpan<byte> data, int offset)
    {
        if (offset <= 0 || offset >= data.Length) return null;
        var end = data[offset..].IndexOf((byte)0);
        if (end <= 0 || end > 1024) return null;
        return System.Text.Encoding.UTF8.GetString(data.Slice(offset, end));
    }

    private sealed record AppleCertificateInfoHolder(HCG.ExecutableMaker.Domain.AppleCertificateInfo? Leaf, IReadOnlyList<string> Chain, DateTimeOffset? SigningTime);

    private static AppleCertificateInfoHolder? Cms(byte[] data, ref string? problem)
    {
        try
        {
            var cms = new SignedCms(new ContentInfo([]), detached: true);
            cms.Decode(data);
            if (cms.SignerInfos.Count == 0) return null;
            var signer = cms.SignerInfos[0];
            var leaf = signer.Certificate;
            var chain = new List<string>();
            var all = cms.Certificates.Cast<X509Certificate2>().ToList();
            for (var c = leaf; c is not null && chain.Count < 8;)
            {
                chain.Add(c.GetNameInfo(X509NameType.SimpleName, false));
                if (c.SubjectName.RawData.AsSpan().SequenceEqual(c.IssuerName.RawData)) break;
                c = all.FirstOrDefault(x => x.SubjectName.RawData.AsSpan().SequenceEqual(c.IssuerName.RawData) && !ReferenceEquals(x, c));
            }
            DateTimeOffset? time = null;
            foreach (var attr in signer.SignedAttributes)
                if (attr.Oid?.Value == "1.2.840.113549.1.9.5" && attr.Values.Count > 0)
                    time = new Pkcs9SigningTime(attr.Values[0].RawData).SigningTime;
            return new AppleCertificateInfoHolder(leaf is null ? null : AppleCertificates.Describe(leaf, null), chain, time);
        }
        catch (CryptographicException)
        {
            problem = "The code signature's CMS (certificate) part could not be decoded.";
            return null;
        }
    }
}
