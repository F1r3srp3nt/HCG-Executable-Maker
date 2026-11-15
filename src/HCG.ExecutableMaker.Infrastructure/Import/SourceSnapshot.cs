using System.Security.Cryptography;
using System.Text;

namespace HCG.ExecutableMaker.Infrastructure.Import;

/// <summary>Content hashes of a project tree, so HCG's copy can be compared with the folder it was made from.</summary>
public static class SourceSnapshot
{
    /// <summary>Relative path ("/"-separated) → SHA-256 of every file under <paramref name="root"/>, skipping the folders an import skips.</summary>
    public static IReadOnlyDictionary<string, string> HashTree(string root, CancellationToken ct)
    {
        var files = new List<(string, string)>();
        if (!Directory.Exists(root)) return new Dictionary<string, string>();
        var stack = new Stack<string>([root]);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            foreach (var d in Directory.EnumerateDirectories(dir))
            {
                var info = new DirectoryInfo(d);
                if (info.LinkTarget is not null || ProjectImporter.ExcludedDirectoryNames.Contains(info.Name)) continue;
                stack.Push(d);
            }
            foreach (var f in Directory.EnumerateFiles(dir))
                if (new FileInfo(f).LinkTarget is null) files.Add((f, Path.GetRelativePath(root, f)));
        }
        return HashFiles(files, ct);
    }

    public static IReadOnlyDictionary<string, string> HashFiles(IEnumerable<(string Path, string Relative)> files, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, rel) in files)
        {
            ct.ThrowIfCancellationRequested();
            using var stream = File.OpenRead(path);
            result[rel.Replace('\\', '/')] = Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        return result;
    }

    /// <summary>SHA-256 over the sorted "path NUL hash" lines; the first 16 hex digits are shown.</summary>
    public static string Fingerprint(IReadOnlyDictionary<string, string> hashes)
    {
        var sb = new StringBuilder();
        foreach (var (rel, hash) in hashes.OrderBy(kv => kv.Key, StringComparer.Ordinal)) sb.Append(rel).Append('\0').Append(hash).Append('\n');
        return $"{hashes.Count} files · sha256 {Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16]}";
    }

    public static string ZipRevision(string zipPath)
    {
        using var stream = File.OpenRead(zipPath);
        return $"ZIP {Path.GetFileName(zipPath)} sha256 {Convert.ToHexStringLower(SHA256.HashData(stream))[..16]}";
    }
}
