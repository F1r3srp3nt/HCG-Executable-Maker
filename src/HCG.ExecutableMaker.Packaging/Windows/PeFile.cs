using System.Text;

namespace HCG.ExecutableMaker.Packaging.Windows;

/// <summary>
/// What a Windows PE file says about itself, read from its headers (no code is run). It records:
/// <list type="bullet">
/// <item>the machine type (x86, x64, ARM64, ARM) and PE32/PE32+;</item>
/// <item>the subsystem (GUI or console) and whether it is a DLL;</item>
/// <item>the certificate table (the Authenticode signature's place);</item>
/// <item>the version resource (FileVersion, ProductVersion, CompanyName, ProductName, …).</item>
/// </list>
/// An installer's bootstrapper and the program it installs are different files with different facts: an Inno Setup
/// Setup.exe is a 32-bit x86 program on every target, while the application it installs is x64 or ARM64.
/// </summary>
public sealed record PeFile
{
    public required string Path { get; init; }
    public required ushort Machine { get; init; }
    /// <summary>x86, x64, arm64, arm, or the machine type in hex when unknown.</summary>
    public string MachineName => NameOf(Machine);
    public required bool IsPe32Plus { get; init; }
    public required ushort Subsystem { get; init; }
    public bool IsGui => Subsystem == 2;
    public bool IsDll { get; init; }
    public bool IsDotNet { get; init; }
    /// <summary>File offset of the optional header's CheckSum field (excluded from the Authenticode hash).</summary>
    public required long CheckSumOffset { get; init; }
    /// <summary>File offset of data directory 4 (the certificate table entry, excluded from the Authenticode hash).</summary>
    public required long CertificateDirectoryOffset { get; init; }
    /// <summary>The certificate table: a file offset (not an RVA) and size; (0, 0) when the file carries no signature.</summary>
    public required (long Offset, int Size) CertificateTable { get; init; }
    public bool HasCertificateTable => CertificateTable.Size > 0;
    public long Length { get; init; }
    /// <summary>The version resource's string values (StringFileInfo, first table) and the fixed file/product versions.</summary>
    public IReadOnlyDictionary<string, string> Version { get; init; } = new Dictionary<string, string>();
    public string? FixedFileVersion { get; init; }
    public string? FixedProductVersion { get; init; }

    public string? ProductVersion => Version.GetValueOrDefault("ProductVersion") ?? FixedProductVersion;
    public string? FileVersion => Version.GetValueOrDefault("FileVersion") ?? FixedFileVersion;

    public static string NameOf(ushort machine) => machine switch
    {
        0x014C => "x86",
        0x8664 => "x64",
        0xAA64 => "arm64",
        0x01C4 => "arm",
        _ => $"0x{machine:X4}",
    };

    /// <summary>The PE facts, or null when the file is not a PE image (no MZ/PE header).</summary>
    public static PeFile? Read(string path)
    {
        using var fs = File.OpenRead(path);
        return Read(fs, path);
    }

    public static PeFile? Read(Stream s, string path)
    {
        var r = new BinaryReader(s, Encoding.ASCII, leaveOpen: true);
        if (s.Length < 0x40) return null;
        s.Position = 0;
        if (r.ReadUInt16() != 0x5A4D) return null;
        s.Position = 0x3C;
        var pe = r.ReadInt32();
        if (pe <= 0 || pe > s.Length - 24) return null;
        s.Position = pe;
        if (r.ReadUInt32() != 0x00004550) return null;
        var machine = r.ReadUInt16();
        var sectionCount = r.ReadUInt16();
        s.Position += 12;
        var optionalSize = r.ReadUInt16();
        var characteristics = r.ReadUInt16();
        var opt = pe + 24;
        if (opt + optionalSize > s.Length || optionalSize < 68) return null;
        s.Position = opt;
        var magic = r.ReadUInt16();
        var plus = magic == 0x20B;
        if (!plus && magic != 0x10B) return null;
        s.Position = opt + 68;
        var subsystem = r.ReadUInt16();
        var dirs = opt + (plus ? 112 : 96);
        var dirCount = (int)ReadAt(r, dirs - 4);
        (uint Rva, uint Size) Dir(int i) => i < dirCount && dirs + i * 8 + 8 <= opt + optionalSize ? (ReadAt(r, dirs + i * 8), ReadAt(r, dirs + i * 8 + 4)) : (0, 0);
        var cert = Dir(4);
        var clr = Dir(14);
        var sections = new List<(uint Va, uint VSize, uint Raw, uint RawSize)>();
        s.Position = opt + optionalSize;
        for (var i = 0; i < sectionCount && s.Position + 40 <= s.Length; i++)
        {
            s.Position += 8;
            var vsize = r.ReadUInt32();
            var va = r.ReadUInt32();
            var rawSize = r.ReadUInt32();
            var raw = r.ReadUInt32();
            s.Position += 16;
            sections.Add((va, vsize, raw, rawSize));
        }
        long? FileOffset(uint rva)
        {
            foreach (var sec in sections)
                if (rva >= sec.Va && rva < sec.Va + Math.Max(sec.VSize, sec.RawSize)) return sec.Raw + (rva - sec.Va);
            return null;
        }
        Dictionary<string, string> strings = [];
        string? fixedFile = null, fixedProduct = null;
        try
        {
            var res = Dir(2);
            if (res.Rva != 0 && FileOffset(res.Rva) is { } root && ReadVersionResource(r, root, FileOffset) is { } block)
                (strings, fixedFile, fixedProduct) = ParseVersionInfo(block);
        }
        catch (Exception ex) when (ex is EndOfStreamException or ArgumentOutOfRangeException or IOException) { }
        return new PeFile
        {
            Path = path, Machine = machine, IsPe32Plus = plus, Subsystem = subsystem, IsDll = (characteristics & 0x2000) != 0, IsDotNet = clr.Rva != 0,
            CheckSumOffset = opt + 64, CertificateDirectoryOffset = dirs + 4 * 8, CertificateTable = (cert.Rva, (int)cert.Size), Length = s.Length,
            Version = strings, FixedFileVersion = fixedFile, FixedProductVersion = fixedProduct,
        };
    }

    private static uint ReadAt(BinaryReader r, long offset) { r.BaseStream.Position = offset; return r.ReadUInt32(); }

    /// <summary>RT_VERSION (16) → first name → first language → the data bytes.</summary>
    private static byte[]? ReadVersionResource(BinaryReader r, long root, Func<uint, long?> fileOffset)
    {
        (uint Id, uint Offset, bool Dir)? Find(long dir, uint? id)
        {
            r.BaseStream.Position = dir + 12;
            var named = r.ReadUInt16();
            var ids = r.ReadUInt16();
            for (var i = 0; i < named + ids && i < 512; i++)
            {
                var name = r.ReadUInt32();
                var off = r.ReadUInt32();
                if (id is null || (name & 0x80000000) == 0 && name == id) return (name, off & 0x7FFFFFFF, (off & 0x80000000) != 0);
            }
            return null;
        }
        if (Find(root, 16) is not { Dir: true } type) return null;
        if (Find(root + type.Offset, null) is not { Dir: true } name) return null;
        if (Find(root + name.Offset, null) is not { Dir: false } lang) return null;
        r.BaseStream.Position = root + lang.Offset;
        var dataRva = r.ReadUInt32();
        var size = r.ReadUInt32();
        if (size is 0 or > 1 << 20 || fileOffset(dataRva) is not { } at) return null;
        r.BaseStream.Position = at;
        return r.ReadBytes((int)size);
    }

    /// <summary>VS_VERSIONINFO: the fixed versions and the first StringTable's values.</summary>
    internal static (Dictionary<string, string> Strings, string? FileVersion, string? ProductVersion) ParseVersionInfo(byte[] b)
    {
        var strings = new Dictionary<string, string>(StringComparer.Ordinal);
        string? fv = null, pv = null;
        var (key, valueAt, valueLength, childrenAt, end) = Block(b, 0);
        if (key != "VS_VERSION_INFO") return (strings, null, null);
        if (valueLength >= 52 && BitConverter.ToUInt32(b, valueAt) == 0xFEEF04BD)
        {
            static string V(uint ms, uint ls) => $"{ms >> 16}.{ms & 0xFFFF}.{ls >> 16}.{ls & 0xFFFF}";
            fv = V(BitConverter.ToUInt32(b, valueAt + 8), BitConverter.ToUInt32(b, valueAt + 12));
            pv = V(BitConverter.ToUInt32(b, valueAt + 16), BitConverter.ToUInt32(b, valueAt + 20));
        }
        for (var at = childrenAt; at < end;)
        {
            var child = Block(b, at);
            if (child.Key == "StringFileInfo")
                for (var t = child.ChildrenAt; t < child.End;)
                {
                    var table = Block(b, t);
                    for (var e = table.ChildrenAt; e < table.End;)
                    {
                        var entry = Block(b, e);
                        if (entry.ValueLength > 0 && entry.ValueAt + entry.ValueLength * 2 <= b.Length)
                            strings.TryAdd(entry.Key, Encoding.Unicode.GetString(b, entry.ValueAt, entry.ValueLength * 2).TrimEnd('\0').Trim());
                        if (entry.End <= e) break;
                        e = entry.End;
                    }
                    if (strings.Count > 0) break;
                    if (table.End <= t) break;
                    t = table.End;
                }
            if (child.End <= at) break;
            at = child.End;
        }
        return (strings, fv, pv);
    }

    private static (string Key, int ValueAt, int ValueLength, int ChildrenAt, int End) Block(byte[] b, int at)
    {
        var length = BitConverter.ToUInt16(b, at);
        var valueLength = BitConverter.ToUInt16(b, at + 2);
        var type = BitConverter.ToUInt16(b, at + 4);
        var k = at + 6;
        var keyEnd = k;
        while (keyEnd + 1 < b.Length && (b[keyEnd] | b[keyEnd + 1]) != 0) keyEnd += 2;
        var key = Encoding.Unicode.GetString(b, k, keyEnd - k);
        var valueAt = Align4(keyEnd + 2);
        // Text values count WCHARs, binary values count bytes.
        var valueBytes = type == 1 ? valueLength * 2 : valueLength;
        var end = Math.Min(b.Length, at + Math.Max((int)length, 6));
        return (key, valueAt, type == 1 ? valueLength : valueBytes, Math.Min(end, Align4(valueAt + valueBytes)), Align4(end));
    }

    private static int Align4(int x) => (x + 3) & ~3;
}
