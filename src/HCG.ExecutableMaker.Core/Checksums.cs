using System.Security.Cryptography;
using System.Text;

namespace HCG.ExecutableMaker.Core;

public static class Checksums
{
    public static async Task<string> Sha256FileAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    public static string Sha256(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>
    /// Deterministic digest of a directory: SHA-256 over sorted lines of "relative-path\tsize\tsha256".
    /// Also returns the manifest text so it can be written next to the artifact.
    /// </summary>
    public static async Task<(string Digest, string Manifest, long TotalBytes)> Sha256DirectoryAsync(string directory, CancellationToken cancellationToken = default)
    {
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(f => (Full: f, Rel: Path.GetRelativePath(directory, f).Replace('\\', '/')))
            .OrderBy(f => f.Rel, StringComparer.Ordinal)
            .ToList();
        var sb = new StringBuilder();
        long total = 0;
        foreach (var (full, rel) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(full);
            total += info.Length;
            sb.Append(rel).Append('\t').Append(info.Length).Append('\t').Append(await Sha256FileAsync(full, cancellationToken).ConfigureAwait(false)).Append('\n');
        }
        var manifest = sb.ToString();
        return (Sha256(Encoding.UTF8.GetBytes(manifest)), manifest, total);
    }
}
