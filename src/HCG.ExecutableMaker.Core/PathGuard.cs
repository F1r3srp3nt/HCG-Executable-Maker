using System.Runtime.InteropServices;

namespace HCG.ExecutableMaker.Core;

/// <summary>Path validation helpers used wherever untrusted names become file system paths.</summary>
public static class PathGuard
{
    private static readonly StringComparison Comparison =
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>True if <paramref name="candidate"/> resolves to <paramref name="root"/> or a location beneath it.</summary>
    public static bool IsWithin(string root, string candidate)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        if (string.Equals(fullRoot, fullCandidate, Comparison))
            return true;
        // A file-system root ("/" or "C:\") keeps its trailing separator after trimming.
        var prefix = Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        return fullCandidate.StartsWith(prefix, Comparison);
    }

    /// <summary>Combines a root with an untrusted relative path, throwing if the result escapes the root.</summary>
    public static string CombineWithin(string root, string relative)
    {
        if (string.IsNullOrEmpty(relative)) throw new ArgumentException("Path is empty.", nameof(relative));
        if (relative.Contains('\0')) throw new ArgumentException("Path contains a NUL character.", nameof(relative));
        if (Path.IsPathRooted(relative) || LooksLikeDriveOrUnc(relative))
            throw new ArgumentException($"Absolute paths are not allowed: '{relative}'.", nameof(relative));
        var combined = Path.GetFullPath(Path.Combine(root, relative));
        if (!IsWithin(root, combined))
            throw new ArgumentException($"Path escapes its root: '{relative}'.", nameof(relative));
        return combined;
    }

    /// <summary>
    /// Validates an archive entry name and returns its normalized segments. Rejects absolute paths, drive letters,
    /// UNC prefixes, '..' segments, NUL characters, and reserved names, regardless of the host OS.
    /// </summary>
    public static bool TryNormalizeArchiveEntry(string entryName, out string[] segments, out string? reason)
    {
        segments = [];
        reason = null;
        if (string.IsNullOrEmpty(entryName)) { reason = "empty entry name"; return false; }
        if (entryName.Contains('\0')) { reason = "NUL character in name"; return false; }
        var unified = entryName.Replace('\\', '/');
        if (unified.StartsWith('/') || LooksLikeDriveOrUnc(unified)) { reason = "absolute path"; return false; }
        var parts = unified.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var result = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            if (part == ".") continue;
            if (part == "..") { reason = "parent-directory traversal ('..')"; return false; }
            if (part.EndsWith(' ') || part.EndsWith('.')) { reason = $"segment '{part}' ends with a space or dot"; return false; }
            if (part.IndexOfAny([':', '*', '?', '"', '<', '>', '|']) >= 0) { reason = $"segment '{part}' contains reserved characters"; return false; }
            if (part.Any(char.IsControl)) { reason = "control characters in name"; return false; }
            result.Add(part);
        }
        if (result.Count == 0) { reason = "entry resolves to the archive root"; return false; }
        segments = [.. result];
        return true;
    }

    /// <summary>
    /// Resolves symbolic links in every existing component of <paramref name="path"/> (like realpath(3)); components
    /// that do not exist yet are appended unchanged. Builds must use real paths: the .NET SDK's standalone restore
    /// drops project references when a project is reached through a symlinked directory (e.g. macOS /var, /tmp).
    /// </summary>
    public static string GetRealPath(string path) => GetRealPath(path, depth: 0);

    private static string GetRealPath(string path, int depth)
    {
        if (depth > 40) throw new IOException($"Too many levels of symbolic links: '{path}'.");
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? "";
        var current = root;
        var parts = full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            var next = Path.Combine(current, parts[i]);
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            if (!info.Exists)
            {
                // Nothing below a missing component can be a link: append the rest verbatim.
                return Path.Combine([current, .. parts[i..]]);
            }
            if (info.LinkTarget is { } target)
            {
                // A link's target may itself pass through other links (e.g. a target under macOS /var), so resolve it fully.
                next = Path.TrimEndingDirectorySeparator(GetRealPath(Path.IsPathRooted(target) ? target : Path.Combine(current, target), depth + 1));
            }
            current = next;
        }
        return current;
    }

    /// <summary>Turns arbitrary text into a safe single file or directory name.</summary>
    public static string SanitizeFileName(string name, string fallback = "item")
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var chars = name.Trim().Select(c => invalid.Contains(c) || char.IsControl(c) ? '-' : c).ToArray();
        var s = new string(chars).Trim('.', ' ', '-');
        return string.IsNullOrEmpty(s) ? fallback : (s.Length > 100 ? s[..100] : s);
    }

    private static bool LooksLikeDriveOrUnc(string path) =>
        (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':') || path.StartsWith("//") || path.StartsWith(@"\\");
}
