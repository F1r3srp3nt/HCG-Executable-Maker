using System.Buffers.Binary;
using System.Text;

namespace HCG.ExecutableMaker.Packaging.Jailbreak;

/// <summary>
/// Everything about one Mach-O slice that decides whether it can load on a device: architecture, platform and minimum
/// OS, the libraries it links (and how), its run-path search list, whether it carries a code signature, the
/// entitlements embedded in that signature, and whether its code is encrypted (an App Store binary). Read only.
/// </summary>
public static class MachOLinkage
{
    public enum DylibKind { Load, Weak, Reexport, Upward, Lazy }

    public sealed record Dylib(string Path, DylibKind Kind);

    public sealed record SliceInfo(
        string Architecture,
        int FileType,
        string? Platform,
        string? MinimumOS,
        IReadOnlyList<Dylib> Dylibs,
        IReadOnlyList<string> RPaths,
        bool HasCodeSignature,
        string? EntitlementsXml,
        bool Encrypted)
    {
        public bool IsExecutable => FileType == 2;
        public bool IsDylib => FileType == 6;
        public bool IsBundle => FileType == 8;
    }

    private const uint LcLoadDylib = 0xC, LcLoadWeakDylib = 0x80000018, LcReexportDylib = 0x8000001F, LcLoadUpwardDylib = 0x80000023, LcLazyLoadDylib = 0x20;
    private const uint LcRpath = 0x8000001C, LcCodeSignature = 0x1D, LcBuildVersion = 0x32, LcVersionMinIphoneOS = 0x25;
    private const uint LcEncryptionInfo = 0x21, LcEncryptionInfo64 = 0x2C;
    private const uint CsMagicEmbeddedSignature = 0xFADE0CC0, CsMagicEntitlements = 0xFADE7171, CsSlotEntitlements = 5;
    private const uint CsMagicCodeDirectory = 0xFADE0C02, CsSlotCodeDirectory = 0;
    private const int MaxSignatureBytes = 4 * 1024 * 1024;

    /// <summary>True when the file starts with a Mach-O or fat (universal) header.</summary>
    public static bool IsMachO(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            if (fs.Length < 32) return false;
            Span<byte> head = stackalloc byte[4];
            fs.ReadExactly(head);
            return BinaryPrimitives.ReadUInt32BigEndian(head) is 0xCAFEBABE or 0xCAFEBABF || BinaryPrimitives.ReadUInt32LittleEndian(head) is 0xFEEDFACF or 0xFEEDFACE;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>Every slice of a thin or fat Mach-O. An unreadable or truncated file yields no slices.</summary>
    public static IReadOnlyList<SliceInfo> Read(string path)
    {
        using var fs = File.OpenRead(path);
        if (fs.Length < 32) return [];
        var head = new byte[(int)Math.Min(4096, fs.Length)];
        fs.ReadExactly(head);
        var magicBe = BinaryPrimitives.ReadUInt32BigEndian(head);
        if (magicBe is 0xCAFEBABE or 0xCAFEBABF)
        {
            var count = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(4));
            var entry = magicBe == 0xCAFEBABE ? 20 : 32;
            if (count is 0 or > 16 || 8 + count * entry > head.Length) return [];
            var list = new List<SliceInfo>();
            for (var i = 0; i < (int)count; i++)
            {
                var e = head.AsSpan(8 + i * entry);
                var offset = magicBe == 0xCAFEBABE ? BinaryPrimitives.ReadUInt32BigEndian(e[8..]) : (long)BinaryPrimitives.ReadUInt64BigEndian(e[8..]);
                if (Thin(fs, offset) is { } s) list.Add(s);
            }
            return list;
        }
        return Thin(fs, 0) is { } thin ? [thin] : [];
    }

    private static SliceInfo? Thin(FileStream fs, long offset)
    {
        if (offset < 0 || offset + 32 > fs.Length) return null;
        fs.Position = offset;
        var header = new byte[32];
        fs.ReadExactly(header);
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (magic is not (0xFEEDFACF or 0xFEEDFACE)) return null;
        var cpu = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        var sub = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
        var fileType = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
        var ncmds = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16));
        var sizeofcmds = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20));
        var headerSize = magic == 0xFEEDFACF ? 32 : 28;
        var arch = cpu == 0x0100000C && (sub & 0x00FFFFFF) == 2 ? "arm64e" : Verification.MachO.Cpu(cpu);
        if (sizeofcmds > 4 * 1024 * 1024 || ncmds > 65536 || offset + headerSize + sizeofcmds > fs.Length) return null;

        fs.Position = offset + headerSize;
        var cmds = new byte[sizeofcmds];
        fs.ReadExactly(cmds);
        var dylibs = new List<Dylib>();
        var rpaths = new List<string>();
        string? platform = null, minos = null, entitlements = null;
        bool signed = false, encrypted = false;
        var pos = 0;
        for (var i = 0; i < ncmds && pos + 8 <= cmds.Length; i++)
        {
            var cmd = BinaryPrimitives.ReadUInt32LittleEndian(cmds.AsSpan(pos));
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(cmds.AsSpan(pos + 4));
            if (size < 8 || pos + size > cmds.Length) break;
            var c = cmds.AsSpan(pos, size);
            switch (cmd)
            {
                case LcLoadDylib or LcLoadWeakDylib or LcReexportDylib or LcLoadUpwardDylib or LcLazyLoadDylib when size >= 24:
                    if (CString(c, BinaryPrimitives.ReadUInt32LittleEndian(c[8..])) is { } dylib)
                        dylibs.Add(new(dylib, cmd switch
                        {
                            LcLoadWeakDylib => DylibKind.Weak, LcReexportDylib => DylibKind.Reexport, LcLoadUpwardDylib => DylibKind.Upward,
                            LcLazyLoadDylib => DylibKind.Lazy, _ => DylibKind.Load,
                        }));
                    break;
                case LcRpath when size >= 12:
                    if (CString(c, BinaryPrimitives.ReadUInt32LittleEndian(c[8..])) is { } rpath) rpaths.Add(rpath);
                    break;
                case LcBuildVersion when size >= 24 && platform is null:
                    platform = BinaryPrimitives.ReadUInt32LittleEndian(c[8..]) switch
                    {
                        1 => "macOS", 2 => "iOS", 3 => "tvOS", 4 => "watchOS", 6 => "Mac Catalyst", 7 => "iOS Simulator", var p => $"platform {p}",
                    };
                    minos = Verification.MachO.Version(BinaryPrimitives.ReadUInt32LittleEndian(c[12..]));
                    break;
                case LcVersionMinIphoneOS when size >= 16 && platform is null:
                    platform = "iOS";
                    minos = Verification.MachO.Version(BinaryPrimitives.ReadUInt32LittleEndian(c[8..]));
                    break;
                case LcEncryptionInfo or LcEncryptionInfo64 when size >= 20:
                    encrypted |= BinaryPrimitives.ReadUInt32LittleEndian(c[16..]) != 0;
                    break;
                case LcCodeSignature when size >= 16:
                    // Signed means a CodeDirectory is present (as HCG Repo's validator and the kernel require), not just the command.
                    (signed, entitlements) = Signature(fs, offset + BinaryPrimitives.ReadUInt32LittleEndian(c[8..]), BinaryPrimitives.ReadUInt32LittleEndian(c[12..]));
                    break;
            }
            pos += size;
        }
        return new SliceInfo(arch, fileType, platform, minos, dylibs, rpaths, signed, entitlements, encrypted);
    }

    private static string? CString(ReadOnlySpan<byte> command, uint offset)
    {
        if (offset >= command.Length) return null;
        var rest = command[(int)offset..];
        var end = rest.IndexOf((byte)0);
        var text = Encoding.UTF8.GetString(end < 0 ? rest : rest[..end]);
        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// Whether an embedded code signature carries a CodeDirectory (slot 0), and its XML entitlements blob (slot 5), if any.
    /// </summary>
    private static (bool HasCodeDirectory, string? Entitlements) Signature(FileStream fs, long start, uint length)
    {
        if (length < 12 || length > MaxSignatureBytes || start < 0 || start + length > fs.Length) return (false, null);
        var position = fs.Position;
        try
        {
            fs.Position = start;
            var blob = new byte[length];
            fs.ReadExactly(blob);
            if (BinaryPrimitives.ReadUInt32BigEndian(blob) != CsMagicEmbeddedSignature) return (false, null);
            var count = BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(8));
            bool codeDirectory = false;
            string? ents = null;
            for (var i = 0; i < Math.Min(count, 64) && 12 + (i + 1) * 8 <= blob.Length; i++)
            {
                var type = BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(12 + i * 8));
                var at = (int)BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(16 + i * 8));
                if (at < 0 || at + 8 > blob.Length) continue;
                var magic = BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(at));
                if (type == CsSlotCodeDirectory && magic == CsMagicCodeDirectory) codeDirectory = true;
                else if (type == CsSlotEntitlements && magic == CsMagicEntitlements)
                {
                    var len = (int)BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(at + 4));
                    if (len >= 8 && at + len <= blob.Length) ents = Encoding.UTF8.GetString(blob, at + 8, len - 8);
                }
            }
            return (codeDirectory, ents);
        }
        finally { fs.Position = position; }
    }
}
