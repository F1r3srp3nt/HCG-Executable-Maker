using System.Buffers.Binary;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.Packaging.Verification;

public enum BinaryFormat
{
    Unknown,
    PortableExecutable,
    MachO,
    MachOUniversal,
    Elf,
}

public sealed record BinaryInfo(BinaryFormat Format, TargetArchitecture? Architecture, bool HasCodeSignature)
{
    public TargetPlatform? Platform => Format switch
    {
        BinaryFormat.PortableExecutable => TargetPlatform.Windows,
        BinaryFormat.MachO or BinaryFormat.MachOUniversal => TargetPlatform.MacOS,
        BinaryFormat.Elf => TargetPlatform.Linux,
        _ => null,
    };
}

/// <summary>Reads executable headers to confirm an output actually targets the requested platform and CPU.</summary>
public static class BinaryInspector
{
    public static BinaryInfo Inspect(string path)
    {
        using var fs = File.OpenRead(path);
        var buffer = new byte[Math.Min(fs.Length, 64 * 1024)];
        fs.ReadExactly(buffer);
        return Inspect(buffer);
    }

    public static BinaryInfo Inspect(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 64 && data[0] == 'M' && data[1] == 'Z')
        {
            var peOffset = BinaryPrimitives.ReadInt32LittleEndian(data[0x3C..]);
            if (peOffset > 0 && peOffset + 6 <= data.Length && data[peOffset] == 'P' && data[peOffset + 1] == 'E' && data[peOffset + 2] == 0 && data[peOffset + 3] == 0)
            {
                var machine = BinaryPrimitives.ReadUInt16LittleEndian(data[(peOffset + 4)..]);
                TargetArchitecture? arch = machine switch { 0x8664 => TargetArchitecture.X64, 0xAA64 => TargetArchitecture.Arm64, _ => null };
                return new BinaryInfo(BinaryFormat.PortableExecutable, arch, false);
            }
        }
        if (data.Length >= 32)
        {
            var magic = BinaryPrimitives.ReadUInt32LittleEndian(data);
            if (magic == 0xFEEDFACF)
            {
                var cpu = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
                TargetArchitecture? arch = cpu switch { 0x01000007 => TargetArchitecture.X64, 0x0100000C => TargetArchitecture.Arm64, _ => null };
                return new BinaryInfo(BinaryFormat.MachO, arch, HasMachOSignature(data));
            }
            if (BinaryPrimitives.ReadUInt32BigEndian(data) == 0xCAFEBABE)
                return new BinaryInfo(BinaryFormat.MachOUniversal, null, false);
            if (data[0] == 0x7F && data[1] == 'E' && data[2] == 'L' && data[3] == 'F' && data.Length >= 20)
            {
                var machine = BinaryPrimitives.ReadUInt16LittleEndian(data[18..]);
                TargetArchitecture? arch = machine switch { 62 => TargetArchitecture.X64, 183 => TargetArchitecture.Arm64, _ => null };
                return new BinaryInfo(BinaryFormat.Elf, arch, false);
            }
        }
        return new BinaryInfo(BinaryFormat.Unknown, null, false);
    }

    private static bool HasMachOSignature(ReadOnlySpan<byte> data)
    {
        const uint LcCodeSignature = 0x1D;
        var ncmds = BinaryPrimitives.ReadUInt32LittleEndian(data[16..]);
        var offset = 32;
        for (var i = 0; i < ncmds && offset + 8 <= data.Length; i++)
        {
            var cmd = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 4)..]);
            if (cmd == LcCodeSignature) return true;
            if (size < 8) break;
            offset += (int)size;
        }
        return false;
    }

    public static VerificationCheck CheckMatchesTarget(string path, TargetPlatform platform, TargetArchitecture arch)
    {
        if (!File.Exists(path))
            return new VerificationCheck("Executable format", VerificationStatus.Failed, $"Executable not found: {Path.GetFileName(path)}");
        var info = Inspect(path);
        if (info.Platform != platform)
            return new VerificationCheck("Executable format", VerificationStatus.Failed, $"Expected a {platform.DisplayName()} executable but found {info.Format}.");
        if (info.Format == BinaryFormat.MachOUniversal || (platform == TargetPlatform.MacOS && arch == TargetArchitecture.Universal))
        {
            var slices = MachO.ReadSlices(path).Select(s => s.Architecture).ToList();
            var missing = MachO.ExpectedSlices(arch).Except(slices).ToList();
            return missing.Count == 0
                ? new VerificationCheck("Executable format", VerificationStatus.Passed, $"Mach-O {(slices.Count > 1 ? "universal " : "")}({string.Join(", ", slices)}) executable for {platform.DisplayName()}.")
                : new VerificationCheck("Executable format", VerificationStatus.Failed, $"Expected {arch.DisplayName()} ({string.Join(", ", MachO.ExpectedSlices(arch))}) but the executable contains: {(slices.Count == 0 ? "no readable slices" : string.Join(", ", slices))}.");
        }
        if (info.Architecture is { } a && a != arch)
            return new VerificationCheck("Executable format", VerificationStatus.Failed, $"Expected {arch.DisplayName()} but the executable targets {a.DisplayName()}.");
        if (info.Architecture is null)
            return new VerificationCheck("Executable format", VerificationStatus.PassedWithWarnings, $"{info.Format} executable; architecture could not be determined.");
        return new VerificationCheck("Executable format", VerificationStatus.Passed, $"{info.Format} {info.Architecture.Value.DisplayName()} executable for {platform.DisplayName()}.");
    }
}
