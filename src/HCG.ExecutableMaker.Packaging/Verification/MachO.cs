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

/// <summary>
/// Apple Mach-O details for iOS binaries: CPU subtype (arm64 vs arm64e and whether arm64e carries the versioned
/// pointer-authentication ABI that iOS 14+ requires), file type, platform and minimum OS from LC_BUILD_VERSION /
/// LC_VERSION_MIN_IPHONEOS. Read-only; the binary is never run.
/// </summary>
public static class AppleMachO
{
    public enum FileType { Unknown, Object, Execute, Dylib, Bundle, Other }

    public sealed record Slice(string Architecture, bool PtrAuthVersioned, int? PtrAuthAbiVersion, FileType Type, string? Platform, string? MinimumOS)
    {
        public bool IsArm64e => Architecture == "arm64e";
    }

    private const uint CpuArm64 = 0x0100000C;
    private const uint SubtypeArm64e = 2;
    private const uint SubtypePtrAuthAbi = 0x80000000;
    private const uint LcBuildVersion = 0x32;
    private const uint LcVersionMinIphoneOS = 0x25;

    public static bool IsMachO(byte[] head) =>
        head.Length >= 4 && (BinaryPrimitives.ReadUInt32BigEndian(head) is 0xCAFEBABE or 0xCAFEBABF || BinaryPrimitives.ReadUInt32LittleEndian(head) is 0xFEEDFACF or 0xFEEDFACE);

    public static IReadOnlyList<Slice> Read(string path)
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
            var list = new List<Slice>();
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

    private static Slice? Thin(FileStream fs, long offset)
    {
        if (offset < 0 || offset + 32 > fs.Length) return null;
        fs.Position = offset;
        var header = new byte[32];
        fs.ReadExactly(header);
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (magic is not (0xFEEDFACF or 0xFEEDFACE)) return null;
        var cpu = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        var sub = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
        var fileType = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12)) switch { 1 => FileType.Object, 2 => FileType.Execute, 6 => FileType.Dylib, 8 => FileType.Bundle, _ => FileType.Other };
        var arm64e = cpu == CpuArm64 && (sub & 0x00FFFFFF) == SubtypeArm64e;
        var arch = arm64e ? "arm64e" : MachO.Cpu(cpu);
        var versioned = arm64e && (sub & SubtypePtrAuthAbi) != 0;
        int? abi = versioned ? (int)((sub >> 24) & 0x3F) : null;
        var ncmds = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16));
        var sizeofcmds = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20));
        var headerSize = magic == 0xFEEDFACF ? 32 : 28;
        string? platform = null, minos = null;
        if (sizeofcmds <= 1024 * 1024 && ncmds <= 4096 && offset + headerSize + sizeofcmds <= fs.Length)
        {
            fs.Position = offset + headerSize;
            var cmds = new byte[sizeofcmds];
            fs.ReadExactly(cmds);
            var pos = 0;
            for (var i = 0; i < ncmds && pos + 8 <= cmds.Length; i++)
            {
                var cmd = BinaryPrimitives.ReadUInt32LittleEndian(cmds.AsSpan(pos));
                var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(cmds.AsSpan(pos + 4));
                if (size < 8 || pos + size > cmds.Length) break;
                if (cmd == LcBuildVersion && size >= 24)
                {
                    platform = BinaryPrimitives.ReadUInt32LittleEndian(cmds.AsSpan(pos + 8)) switch
                    {
                        1 => "macOS", 2 => "iOS", 3 => "tvOS", 4 => "watchOS", 6 => "Mac Catalyst", 7 => "iOS Simulator", var p => $"platform {p}",
                    };
                    minos = MachO.Version(BinaryPrimitives.ReadUInt32LittleEndian(cmds.AsSpan(pos + 12)));
                    break;
                }
                if (cmd == LcVersionMinIphoneOS && size >= 16) { platform = "iOS"; minos = MachO.Version(BinaryPrimitives.ReadUInt32LittleEndian(cmds.AsSpan(pos + 8))); break; }
                pos += size;
            }
        }
        return new Slice(arch, versioned, abi, fileType, platform, minos);
    }
}
