using System.Buffers.Binary;

namespace HCG.ExecutableMaker.Packaging.Verification;

/// <summary>Minimal Mach-O reader: architecture slices and the deployment target (minimum macOS) each slice was linked for.</summary>
public static class MachO
{
    public sealed record Slice(string Architecture, string? MinimumMacOS);

    private const uint LcVersionMinMacOSX = 0x24;
    private const uint LcBuildVersion = 0x32;
    private const uint PlatformMacOS = 1;

    /// <summary>Slices of a thin or universal Mach-O file; empty when the file is not Mach-O.</summary>
    public static IReadOnlyList<Slice> ReadSlices(string path)
    {
        using var fs = File.OpenRead(path);
        var head = new byte[(int)Math.Min(4096, fs.Length)];
        fs.ReadExactly(head);
        if (head.Length < 8) return [];
        var magicBe = BinaryPrimitives.ReadUInt32BigEndian(head);
        if (magicBe is 0xCAFEBABE or 0xCAFEBABF)
        {
            var count = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(4));
            var entry = magicBe == 0xCAFEBABE ? 20 : 32;
            if (count is 0 or > 16 || 8 + count * entry > head.Length) return [];
            var slices = new List<Slice>();
            for (var i = 0; i < (int)count; i++)
            {
                var e = head.AsSpan(8 + i * entry);
                var offset = magicBe == 0xCAFEBABE ? BinaryPrimitives.ReadUInt32BigEndian(e[8..]) : (long)BinaryPrimitives.ReadUInt64BigEndian(e[8..]);
                slices.Add(ReadThin(fs, offset) ?? new Slice(Cpu(BinaryPrimitives.ReadUInt32BigEndian(e)), null));
            }
            return slices;
        }
        return ReadThin(fs, 0) is { } thin ? [thin] : [];
    }

    private static Slice? ReadThin(FileStream fs, long offset)
    {
        if (offset < 0 || offset + 32 > fs.Length) return null;
        fs.Position = offset;
        var header = new byte[32];
        fs.ReadExactly(header);
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (magic is not (0xFEEDFACF or 0xFEEDFACE)) return null;
        var cpu = Cpu(BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)));
        var ncmds = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16));
        var sizeofcmds = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20));
        var headerSize = magic == 0xFEEDFACF ? 32 : 28;
        if (sizeofcmds > 1024 * 1024 || ncmds > 4096 || offset + headerSize + sizeofcmds > fs.Length) return new Slice(cpu, null);
        fs.Position = offset + headerSize;
        var cmds = new byte[sizeofcmds];
        fs.ReadExactly(cmds);
        var pos = 0;
        for (var i = 0; i < ncmds && pos + 8 <= cmds.Length; i++)
        {
            var cmd = BinaryPrimitives.ReadUInt32LittleEndian(cmds.AsSpan(pos));
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(cmds.AsSpan(pos + 4));
            if (size < 8 || pos + size > cmds.Length) break;
            if (cmd == LcBuildVersion && size >= 24 && BinaryPrimitives.ReadUInt32LittleEndian(cmds.AsSpan(pos + 8)) == PlatformMacOS)
                return new Slice(cpu, Version(BinaryPrimitives.ReadUInt32LittleEndian(cmds.AsSpan(pos + 12))));
            if (cmd == LcVersionMinMacOSX && size >= 16)
                return new Slice(cpu, Version(BinaryPrimitives.ReadUInt32LittleEndian(cmds.AsSpan(pos + 8))));
            pos += size;
        }
        return new Slice(cpu, null);
    }

    /// <summary>Mach-O packs versions as xxxx.yy.zz in nibbles: 0x000E0000 → "14.0".</summary>
    public static string Version(uint v)
    {
        var major = v >> 16;
        var minor = (v >> 8) & 0xFF;
        var patch = v & 0xFF;
        return patch == 0 ? $"{major}.{minor}" : $"{major}.{minor}.{patch}";
    }

    internal static string Cpu(uint cpu) => cpu switch
    {
        0x0100000C => "arm64", 0x01000007 => "x86_64", 0x0000000C => "armv7", 0x00000007 => "i386", _ => $"cpu 0x{cpu:x}",
    };

    /// <summary>Architecture names a build for <paramref name="arch"/> must contain.</summary>
    public static IReadOnlyList<string> ExpectedSlices(Domain.TargetArchitecture arch) => arch switch
    {
        Domain.TargetArchitecture.Arm64 => ["arm64"],
        Domain.TargetArchitecture.X64 => ["x86_64"],
        _ => ["arm64", "x86_64"],
    };
}
