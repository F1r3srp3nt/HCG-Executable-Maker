using System.IO.Compression;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.Packaging.Mobile;

/// <summary>Reads every ZIP entry (bounded), recomputes CRC-32, and flags unsafe entry names. Nothing is extracted.</summary>
public static class ZipIntegrity
{
    public const long MaxTotalBytes = 8L * 1024 * 1024 * 1024;
    public const int MaxEntries = 200_000;

    public static VerificationCheck Check(ZipArchive zip, CancellationToken ct = default)
    {
        if (zip.Entries.Count > MaxEntries)
            return new VerificationCheck("Archive integrity", VerificationStatus.Failed, $"Too many entries ({zip.Entries.Count:N0}).");
        var unsafeNames = zip.Entries.Where(e => e.FullName.Length > 0 && !e.FullName.EndsWith('/') && !PathGuard.TryNormalizeArchiveEntry(e.FullName, out _, out _)).Select(e => e.FullName).Take(3).ToList();
        if (unsafeNames.Count > 0)
            return new VerificationCheck("Archive integrity", VerificationStatus.Failed, "Unsafe entry names (path traversal or absolute paths): " + string.Join(", ", unsafeNames));
        var buffer = new byte[1 << 16];
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.FullName.EndsWith('/')) continue;
            uint crc = 0xFFFFFFFF;
            try
            {
                using var s = entry.Open();
                int n;
                while ((n = s.Read(buffer)) > 0)
                {
                    total += n;
                    if (total > MaxTotalBytes) return new VerificationCheck("Archive integrity", VerificationStatus.Failed, "Archive expands beyond the inspection limit (possible decompression bomb).");
                    crc = Crc32.Update(crc, buffer.AsSpan(0, n));
                }
            }
            catch (InvalidDataException ex)
            {
                return new VerificationCheck("Archive integrity", VerificationStatus.Failed, $"'{entry.FullName}' cannot be decompressed: {ex.Message}");
            }
            if ((crc ^ 0xFFFFFFFF) != entry.Crc32)
                return new VerificationCheck("Archive integrity", VerificationStatus.Failed, $"CRC mismatch in '{entry.FullName}' (corrupted archive).");
        }
        return new VerificationCheck("Archive integrity", VerificationStatus.Passed, $"{zip.Entries.Count:N0} entries, CRC-32 verified");
    }

    /// <summary>Reads one entry fully with a size cap.</summary>
    public static byte[]? ReadEntry(ZipArchive zip, string name, int maxBytes = 16 * 1024 * 1024)
    {
        var entry = zip.GetEntry(name);
        if (entry is null) return null;
        if (entry.Length > maxBytes) throw new InvalidDataException($"'{name}' is unexpectedly large ({entry.Length:N0} bytes).");
        using var s = entry.Open();
        using var ms = new MemoryStream();
        var buffer = new byte[1 << 16];
        int n;
        while ((n = s.Read(buffer)) > 0)
        {
            ms.Write(buffer, 0, n);
            if (ms.Length > maxBytes) throw new InvalidDataException($"'{name}' expands beyond {maxBytes:N0} bytes.");
        }
        return ms.ToArray();
    }
}

internal static class Crc32
{
    private static readonly uint[] Table = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    public static uint Compute(ReadOnlySpan<byte> data) => Update(0xFFFFFFFF, data) ^ 0xFFFFFFFF;
}
