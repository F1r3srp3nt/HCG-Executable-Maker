using System.Buffers.Binary;
using System.IO.Compression;

namespace HCG.ExecutableMaker.Packaging.Packagers;

/// <summary>
/// Creates ZIP archives that carry explicit Unix permissions, so executables stay executable after extraction on
/// macOS/Linux even when the archive is produced on Windows.
/// </summary>
public static class UnixZip
{
    public const int RegularFile = 0x8000;
    public const int Directory = 0x4000;

    public static void Create(string zipPath, IEnumerable<(string SourcePath, string EntryName, bool Executable)> files, IEnumerable<string>? directories = null, CancellationToken ct = default)
    {
        using (var stream = new FileStream(zipPath, FileMode.CreateNew, FileAccess.ReadWrite))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var dir in directories ?? [])
            {
                var e = zip.CreateEntry(dir.TrimEnd('/') + "/");
                e.ExternalAttributes = unchecked((Directory | 0b111_101_101) << 16);
            }
            foreach (var (source, name, executable) in files)
            {
                ct.ThrowIfCancellationRequested();
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                var mode = executable ? 0b111_101_101 : 0b110_100_100; // 0755 / 0644
                entry.ExternalAttributes = unchecked((RegularFile | mode) << 16);
                entry.LastWriteTime = File.GetLastWriteTime(source);
                using var input = File.OpenRead(source);
                using var output = entry.Open();
                input.CopyTo(output);
            }
        }
        EnsureUnixMadeBy(zipPath);
    }

    /// <summary>
    /// Unzip tools only honor Unix modes when the central directory's "version made by" host is Unix (3).
    /// .NET writes the current OS there, so archives produced on Windows are patched.
    /// </summary>
    public static void EnsureUnixMadeBy(string zipPath)
    {
        var data = File.ReadAllBytes(zipPath);
        var eocd = -1;
        for (var i = data.Length - 22; i >= Math.Max(0, data.Length - 65_557); i--)
            if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i)) == 0x06054b50) { eocd = i; break; }
        if (eocd < 0) throw new InvalidDataException("End of central directory not found.");
        var count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(eocd + 10));
        var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(eocd + 16));
        var changed = false;
        for (var n = 0; n < count; n++)
        {
            if (offset + 46 > data.Length || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset)) != 0x02014b50)
                throw new InvalidDataException("Malformed central directory.");
            if (data[offset + 5] != 3) { data[offset + 5] = 3; changed = true; }
            var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 28));
            var extraLen = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 30));
            var commentLen = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 32));
            offset += 46 + nameLen + extraLen + commentLen;
        }
        if (changed) File.WriteAllBytes(zipPath, data);
    }

    /// <summary>Reads back the Unix permission bits recorded for an entry (for verification).</summary>
    public static int GetMode(ZipArchiveEntry entry) => (entry.ExternalAttributes >> 16) & 0x1FF;
}
