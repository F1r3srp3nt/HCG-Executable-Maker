using System.Security.Cryptography;
using System.Text;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.Core;

/// <summary>
/// What HCG builds is a COPY taken at import time (workspace/source), not the user's folder. This identifies that copy
/// (file count + content fingerprint), the original it came from (Git commit read from .git as files — no git command
/// runs — or the ZIP's SHA-256), and whether the original has changed since, so a stale copy is visible instead of
/// silently built.
/// </summary>
public static class SourceSnapshot
{
    /// <summary>Folders never copied by a directory import, and therefore never compared (build outputs, VCS and IDE state).</summary>
    public static readonly IReadOnlySet<string> ExcludedDirectoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".hg", ".svn", ".vs", ".idea", "node_modules", "TestResults",
        // SwiftPM / Xcode generated state: build products, dependency checkouts, Xcode's package workspace.
        ".build", ".swiftpm", "DerivedData",
    };

    private const int MaxFiles = 60_000;

    /// <summary>SHA-256 over "path\0sha256\n" for every file (ordinal path order, '/' separators); independent of timestamps.</summary>
    public static (string Sha256, int Files) Fingerprint(string root, CancellationToken ct = default)
    {
        root = Path.GetFullPath(root);
        var files = new List<string>();
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0 && files.Count < MaxFiles)
        {
            ct.ThrowIfCancellationRequested();
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(stack.Pop()).EnumerateFileSystemInfos().ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var e in entries)
            {
                if (e.Name == ".DS_Store") continue;
                if (e.LinkTarget is not null)
                {
                    // The importer copies a linked file that stays inside the project as a regular file; nothing else.
                    if (e is FileInfo && e.ResolveLinkTarget(true) is FileInfo { Exists: true } t && PathGuard.IsWithin(root, t.FullName)) files.Add(e.FullName);
                    continue;
                }
                if (e is DirectoryInfo) { if (!ExcludedDirectoryNames.Contains(e.Name)) stack.Push(e.FullName); }
                else files.Add(e.FullName);
            }
        }
        using var all = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var rel in files.Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).OrderBy(f => f, StringComparer.Ordinal))
        {
            string hash;
            try { using var s = File.OpenRead(Path.Combine(root, rel)); hash = Convert.ToHexStringLower(SHA256.HashData(s)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { hash = "unreadable"; }
            all.AppendData(Encoding.UTF8.GetBytes($"{rel}\0{hash}\n"));
        }
        return (Convert.ToHexStringLower(all.GetHashAndReset()), files.Count);
    }

    public static string FileSha256(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(s));
    }

    /// <summary>Compares the imported copy with its original. Reads files only; safe to call on the UI's background thread.</summary>
    public static SourceSnapshotStatus Inspect(ProjectRecord project, CancellationToken ct = default)
    {
        var (sha, count) = Directory.Exists(project.SourceDirectory) ? Fingerprint(project.SourceDirectory, ct) : ("", 0);
        var status = new SourceSnapshotStatus { Kind = project.SourceKind, Location = project.SourceLocation, ImportedAt = project.ImportedAt, CopySha256 = sha, CopyFiles = count };
        switch (project.SourceKind)
        {
            case ProjectSourceKind.Directory when Directory.Exists(project.SourceLocation):
            {
                var (commit, branch) = Git(project.SourceLocation);
                var (osha, ocount) = Fingerprint(project.SourceLocation, ct);
                return status with { OriginalExists = true, OriginalSha256 = osha, OriginalFiles = ocount, OriginalCommit = commit, OriginalBranch = branch, MatchesOriginal = osha == sha };
            }
            case ProjectSourceKind.ZipArchive when File.Exists(project.SourceLocation):
            {
                var zip = FileSha256(project.SourceLocation);
                var kept = project.PreservedArchivePath is { } p && File.Exists(p) ? FileSha256(p) : null;
                return status with { OriginalExists = true, OriginalSha256 = zip, ImportedArchiveSha256 = kept, MatchesOriginal = kept is null ? null : kept == zip };
            }
            case ProjectSourceKind.ZipArchive:
                return status with { ImportedArchiveSha256 = project.PreservedArchivePath is { } q && File.Exists(q) ? FileSha256(q) : null };
            default:
                return status;
        }
    }

    /// <summary>The commit and branch of the Git working copy containing <paramref name="path"/> (null when none). No git process runs.</summary>
    public static (string? Commit, string? Branch) Git(string path)
    {
        try
        {
            var dir = new DirectoryInfo(Path.GetFullPath(path));
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")) && !File.Exists(Path.Combine(dir.FullName, ".git"))) dir = dir.Parent;
            if (dir is null) return (null, null);
            var gitDir = Path.Combine(dir.FullName, ".git");
            if (File.Exists(gitDir))
            {
                // Worktree/submodule: ".git" is a file "gitdir: <path>".
                var line = File.ReadAllText(gitDir).Trim();
                if (!line.StartsWith("gitdir:", StringComparison.Ordinal)) return (null, null);
                gitDir = Path.GetFullPath(Path.Combine(dir.FullName, line[7..].Trim()));
            }
            var head = File.ReadAllText(Path.Combine(gitDir, "HEAD")).Trim();
            if (!head.StartsWith("ref:", StringComparison.Ordinal)) return (IsSha(head) ? head : null, null);
            var refName = head[4..].Trim();
            var branch = refName.StartsWith("refs/heads/", StringComparison.Ordinal) ? refName["refs/heads/".Length..] : refName;
            var commonDir = File.Exists(Path.Combine(gitDir, "commondir"))
                ? Path.GetFullPath(Path.Combine(gitDir, File.ReadAllText(Path.Combine(gitDir, "commondir")).Trim())) : gitDir;
            foreach (var root in new[] { gitDir, commonDir }.Distinct())
            {
                var loose = Path.Combine(root, refName.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(loose) && File.ReadAllText(loose).Trim() is var sha && IsSha(sha)) return (sha, branch);
                var packed = Path.Combine(root, "packed-refs");
                if (File.Exists(packed))
                    foreach (var l in File.ReadLines(packed))
                        if (l.EndsWith(" " + refName, StringComparison.Ordinal) && l.Split(' ')[0] is var p && IsSha(p)) return (p, branch);
            }
            return (null, branch); // a branch without commits yet
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (null, null); }
    }

    private static bool IsSha(string s) => s.Length is 40 or 64 && s.All(Uri.IsHexDigit);
}

/// <summary>Identity of an imported copy and how it relates to its original.</summary>
public sealed record SourceSnapshotStatus
{
    public required ProjectSourceKind Kind { get; init; }
    public required string Location { get; init; }
    public DateTimeOffset ImportedAt { get; init; }
    public required string CopySha256 { get; init; }
    public int CopyFiles { get; init; }
    public bool OriginalExists { get; init; }
    /// <summary>Folder: content fingerprint computed like the copy's. ZIP: the archive file's SHA-256.</summary>
    public string? OriginalSha256 { get; init; }
    public int? OriginalFiles { get; init; }
    public string? OriginalCommit { get; init; }
    public string? OriginalBranch { get; init; }
    /// <summary>ZIP imports: SHA-256 of the archive preserved at import time.</summary>
    public string? ImportedArchiveSha256 { get; init; }
    /// <summary>True: the copy is what the original holds now. False: the original changed since import. Null: cannot tell.</summary>
    public bool? MatchesOriginal { get; init; }

    public string Describe() => (Kind, OriginalExists, MatchesOriginal) switch
    {
        (ProjectSourceKind.Artifact, _, _) => "Imported artifact (no source).",
        (_, false, _) => $"The original {(Kind == ProjectSourceKind.ZipArchive ? "ZIP" : "folder")} is no longer at {Location}. HCG builds its copy from {ImportedAt:yyyy-MM-dd HH:mm} UTC.",
        (_, true, true) => "The imported copy matches the original" + (OriginalCommit is { } c ? $" (Git {c[..12]}{(OriginalBranch is { } b ? " on " + b : "")})." : "."),
        (_, true, false) => $"The original has CHANGED since it was imported on {ImportedAt:yyyy-MM-dd HH:mm} UTC" +
                            (OriginalCommit is { } c2 ? $" (it is now at Git {c2[..12]}{(OriginalBranch is { } b2 ? " on " + b2 : "")})" : "") +
                            ". HCG still builds the old copy — use Refresh source.",
        _ => "Could not compare the imported copy with its original.",
    };
}

/// <summary>Outcome of Refresh source: where the new copy came from and the copy's fingerprint before and after.</summary>
public sealed record SourceRefreshResult(string Location, string BeforeSha256, int BeforeFiles, string AfterSha256, int AfterFiles, string? Commit, IReadOnlyList<string> Warnings)
{
    public bool Changed => BeforeSha256 != AfterSha256;
    public string Describe() => (Changed
        ? $"Source refreshed from {Location}: {BeforeFiles} → {AfterFiles} files, content {BeforeSha256[..12]} → {AfterSha256[..12]}."
        : $"Source refreshed from {Location}: unchanged ({AfterFiles} files, content {AfterSha256[..12]}).")
        + (Commit is { } c ? $" Git {c[..12]}." : "") + (Warnings.Count > 0 ? " " + string.Join(" ", Warnings.Take(3)) : "");
}
