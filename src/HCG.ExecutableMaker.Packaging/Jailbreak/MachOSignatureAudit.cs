using System.Buffers.Binary;
using System.Security.Cryptography;

namespace HCG.ExecutableMaker.Packaging.Jailbreak;

/// <summary>
/// Reads and verifies the embedded code signature of every slice of a Mach-O, independently of Apple's tools: each
/// CodeDirectory's page hashes are recomputed over the actual bytes, and its special slots (Info.plist, requirements,
/// CodeResources, entitlements) are compared with the files and blobs they bind. Used to tell a valid signature from
/// one that no longer matches its binary (changed after signing), and to classify the signer (ldid-style pseudo
/// signature, ad hoc, certificate). It changes nothing.
/// </summary>
public static class MachOSignatureAudit
{
    public enum SignerKind { None, AdHoc, Certificate, Unreadable }

    public sealed record CodeDirectory(
        int BlobSlot, uint Version, uint Flags, string HashType, int PageSize, int CodeSlots, int BadPages, bool Verifiable,
        string Identifier, string? TeamId, IReadOnlyDictionary<string, string> SpecialSlots, string CdHash, ulong ExecSegFlags)
    {
        public bool AdHocFlag => (Flags & 0x2) != 0;
        public bool LinkerSigned => (Flags & 0x20000) != 0;
        public bool HardenedRuntime => (Flags & 0x10000) != 0;
        public bool LibraryValidation => (Flags & 0x200) != 0;
        public bool MainBinary => (ExecSegFlags & 0x1) != 0;
    }

    public sealed record Slice(string Architecture, SignerKind Signer, IReadOnlyList<CodeDirectory> CodeDirectories, bool HasEntitlementsBlob, bool HasDerEntitlements, string? Problem)
    {
        public int BadPages => CodeDirectories.Sum(c => c.BadPages);
        public bool Verified => Problem is null && CodeDirectories.Count > 0 && CodeDirectories.All(c => c.Verifiable && c.BadPages == 0);
        public bool HardenedRuntime => CodeDirectories.Any(c => c.HardenedRuntime);
        public bool LibraryValidation => CodeDirectories.Any(c => c.LibraryValidation);
        public string? TeamId => CodeDirectories.Select(c => c.TeamId).FirstOrDefault(t => t is { Length: > 0 });

        /// <summary>"ldid-style" (no CMS, no ad hoc flag, SHA-1 + SHA-256 directories), "ad hoc", "certificate-signed".</summary>
        public string SignerText => Signer switch
        {
            SignerKind.None => "unsigned",
            SignerKind.Certificate => $"certificate-signed{(TeamId is { } t ? $" (team {t})" : "")}",
            SignerKind.AdHoc when CodeDirectories.Any(c => c.LinkerSigned) => "ad hoc (linker-signed)",
            SignerKind.AdHoc when CodeDirectories.All(c => !c.AdHocFlag) => "ad hoc, ldid-style",
            SignerKind.AdHoc => "ad hoc",
            _ => "unreadable",
        };
    }

    /// <param name="bundleDir">The bundle whose Info.plist and _CodeSignature/CodeResources the signature binds.</param>
    public static IReadOnlyList<Slice> Audit(string path, string bundleDir)
    {
        var data = File.ReadAllBytes(path);
        var magic = BinaryPrimitives.ReadUInt32BigEndian(data);
        if (magic is 0xCAFEBABE or 0xCAFEBABF)
        {
            var n = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4));
            var slices = new List<Slice>();
            for (var i = 0; i < n && i < 16; i++)
            {
                long off, size;
                if (magic == 0xCAFEBABE)
                {
                    off = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8 + 20 * i + 8));
                    size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8 + 20 * i + 12));
                }
                else
                {
                    off = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(8 + 32 * i + 8));
                    size = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(8 + 32 * i + 16));
                }
                slices.Add(off + size <= data.Length ? AuditSlice(data, (int)off, (int)size, bundleDir) : new("?", SignerKind.Unreadable, [], false, false, "slice beyond the end of the file"));
            }
            return slices;
        }
        return [AuditSlice(data, 0, data.Length, bundleDir)];
    }

    private static Slice AuditSlice(byte[] d, int b, int size, string bundleDir)
    {
        try
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(b)) != 0xFEEDFACF) return new("?", SignerKind.Unreadable, [], false, false, "not a 64-bit Mach-O slice");
            var cpu = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(b + 4));
            var sub = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(b + 8)) & 0xFF;
            var arch = cpu switch { 0x0100000C => sub == 2 ? "arm64e" : "arm64", 0x01000007 => "x86_64", _ => $"cpu {cpu:x}" };
            var ncmds = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(b + 16));
            var off = b + 32;
            int? sigOff = null, sigSize = null;
            for (var i = 0; i < ncmds; i++)
            {
                var cmd = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(off));
                var cmdSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(off + 4));
                if (cmd == 0x1D) { sigOff = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(off + 8)); sigSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(off + 12)); }
                if (cmdSize <= 0) break;
                off += cmdSize;
            }
            if (sigOff is null) return new(arch, SignerKind.None, [], false, false, null);
            var sb = b + sigOff.Value;
            if (sb + 12 > d.Length || BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(sb)) != 0xFADE0CC0)
                return new(arch, SignerKind.Unreadable, [], false, false, "LC_CODE_SIGNATURE does not point to an embedded signature");
            var count = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(sb + 8));
            var blobs = new Dictionary<uint, byte[]>();
            for (var i = 0; i < count && i < 64; i++)
            {
                var type = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(sb + 12 + 8 * i));
                var bo = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(sb + 12 + 8 * i + 4));
                var len = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(sb + bo + 4));
                blobs[type] = d.AsSpan(sb + bo, len).ToArray();
            }
            var cms = blobs.GetValueOrDefault(0x10000u);
            var signer = cms is { Length: > 8 } ? SignerKind.Certificate : SignerKind.AdHoc;
            var info = Path.Combine(bundleDir, "Info.plist");
            var resources = Path.Combine(bundleDir, "_CodeSignature", "CodeResources");
            var cds = new List<CodeDirectory>();
            foreach (var (slot, cd) in blobs.Where(kv => kv.Key == 0 || kv.Key is >= 0x1000 and < 0x1005).OrderBy(kv => kv.Key))
                cds.Add(ReadCodeDirectory((int)slot, cd, d, b, sigOff.Value, blobs, info, resources));
            return new(arch, signer, cds, blobs.ContainsKey(5), blobs.ContainsKey(7), cds.Count == 0 ? "the signature has no CodeDirectory" : null);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or ArgumentException)
        {
            return new("?", SignerKind.Unreadable, [], false, false, "malformed signature: " + ex.Message);
        }
    }

    private static CodeDirectory ReadCodeDirectory(int slot, byte[] cd, byte[] d, int b, int sigOff, Dictionary<uint, byte[]> blobs, string info, string resources)
    {
        uint U(int o) => BinaryPrimitives.ReadUInt32BigEndian(cd.AsSpan(o));
        var version = U(8);
        var flags = U(12);
        var hashOffset = (int)U(16);
        var identOffset = (int)U(20);
        var nSpecial = (int)U(24);
        var nCode = (int)U(28);
        var codeLimit = (long)U(32);
        int hashSize = cd[36], hashType = cd[37], pageLog = cd[39];
        string Str(int o) { var e = o; while (e < cd.Length && cd[e] != 0) e++; return System.Text.Encoding.UTF8.GetString(cd, o, e - o); }
        var ident = identOffset > 0 && identOffset < cd.Length ? Str(identOffset) : "";
        string? team = null;
        if (version >= 0x20200 && cd.Length >= 0x34 && U(0x30) is var to and > 0 && to < cd.Length) team = Str((int)to);
        ulong execFlags = version >= 0x20400 && cd.Length >= 0x58 ? BinaryPrimitives.ReadUInt64BigEndian(cd.AsSpan(0x50)) : 0;
        var page = pageLog > 0 ? 1 << pageLog : 0;
        var name = hashType switch { 1 => "SHA-1", 2 => "SHA-256", 3 => "SHA-256 (truncated)", 4 => "SHA-384", _ => $"type {hashType}" };
        byte[] H(ReadOnlySpan<byte> x) => hashType switch
        {
            1 => SHA1.HashData(x),
            2 or 3 => SHA256.HashData(x),
            4 => SHA384.HashData(x),
            _ => [],
        };
        var verifiable = hashType is >= 1 and <= 4 && nCode > 0 && page > 0 && hashOffset + nCode * hashSize <= cd.Length;
        var bad = 0;
        if (verifiable)
            for (var i = 0; i < nCode; i++)
            {
                var start = b + (long)i * page;
                var end = b + Math.Min((long)(i + 1) * page, codeLimit);
                if (end > d.Length || start > end) { bad++; continue; }
                if (!H(d.AsSpan((int)start, (int)(end - start))).AsSpan(0, hashSize).SequenceEqual(cd.AsSpan(hashOffset + i * hashSize, hashSize))) bad++;
            }
        var specials = new Dictionary<string, string>();
        string[] names = ["", "Info.plist", "Requirements", "CodeResources", "Application", "Entitlements", "-", "DER entitlements"];
        for (var s = 1; s <= Math.Min(nSpecial, 7) && hashOffset - s * hashSize >= 0 && hashSize > 0; s++)
        {
            var want = cd.AsSpan(hashOffset - s * hashSize, hashSize);
            if (!want.ContainsAnyExcept((byte)0)) continue;
            byte[]? source = s switch
            {
                1 => File.Exists(info) ? File.ReadAllBytes(info) : null,
                2 => blobs.GetValueOrDefault(2u),
                3 => File.Exists(resources) ? File.ReadAllBytes(resources) : null,
                5 => blobs.GetValueOrDefault(5u),
                7 => blobs.GetValueOrDefault(7u),
                _ => null,
            };
            specials[names[s]] = source is null ? "bound, but missing" : H(source).AsSpan(0, hashSize).SequenceEqual(want) ? "matches" : "DOES NOT MATCH";
        }
        var cdHash = Convert.ToHexStringLower((hashType == 1 ? SHA1.HashData(cd) : hashType == 4 ? SHA384.HashData(cd) : SHA256.HashData(cd)).AsSpan(0, 20));
        _ = sigOff;
        return new(slot, version, flags, name, page, nCode, bad, verifiable, ident, team, specials, cdHash, execFlags);
    }
}
