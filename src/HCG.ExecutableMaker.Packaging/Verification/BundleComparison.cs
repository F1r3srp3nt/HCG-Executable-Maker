using System.Security.Cryptography;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.Packaging.Verification;

/// <summary>
/// Compares an .app with a copy of it (inside a mounted DMG, an expanded .pkg payload or an installed location):
/// every file present, byte-identical (SHA-256, which covers resources and the code signature in _CodeSignature and the
/// Mach-O), with the same Unix permissions, and the same symbolic links. Tool-free, so it also runs in tests.
/// </summary>
public static class BundleComparison
{
    public static VerificationCheck Compare(string original, string copy, string checkName, string where)
    {
        if (!Directory.Exists(copy))
            return new VerificationCheck(checkName, VerificationStatus.Failed, $"{Path.GetFileName(original)} is missing from {where}.");
        var a = Snapshot(original);
        var b = Snapshot(copy);
        var missing = a.Keys.Except(b.Keys).Order(StringComparer.Ordinal).ToList();
        var extra = b.Keys.Except(a.Keys).Order(StringComparer.Ordinal).ToList();
        var changed = a.Keys.Intersect(b.Keys).Where(k => a[k] != b[k]).Order(StringComparer.Ordinal).ToList();
        if (missing.Count == 0 && extra.Count == 0 && changed.Count == 0)
            return new VerificationCheck(checkName, VerificationStatus.Passed,
                $"{a.Count} entries in {where} are identical to the built .app (content, permissions, signature and resources).");
        static string Few(IEnumerable<string> xs) => string.Join(", ", xs.Take(5)) + (xs.Count() > 5 ? ", …" : "");
        return new VerificationCheck(checkName, VerificationStatus.Failed,
            $"{where} differs from the built .app." +
            (missing.Count > 0 ? $" Missing: {Few(missing)}." : "") +
            (extra.Count > 0 ? $" Extra: {Few(extra)}." : "") +
            (changed.Count > 0 ? $" Changed: {Few(changed)}." : ""));
    }

    /// <summary>relative path → "kind:mode:sha256" (or "link:target").</summary>
    public static Dictionary<string, string> Snapshot(string root)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var stack = new Stack<string>([root]);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
            {
                var rel = Path.GetRelativePath(root, entry.FullName).Replace('\\', '/');
                if (entry.LinkTarget is { } target) { map[rel] = "link:" + target; continue; }
                var mode = OperatingSystem.IsWindows() ? "" : Convert.ToString((int)entry.UnixFileMode, 8);
                if (entry is DirectoryInfo d) { map[rel] = "dir:" + mode; stack.Push(d.FullName); continue; }
                using var s = File.OpenRead(entry.FullName);
                map[rel] = $"file:{mode}:{Convert.ToHexString(SHA256.HashData(s))}";
            }
        }
        return map;
    }
}
