using System.Text.Json;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.Core;

/// <summary>Reads a Git working copy's checked-out commit from its .git files (no git command runs).</summary>
public static class GitRevision
{
    /// <summary>The commit and branch of the Git working copy containing <paramref name="path"/> (null when none).</summary>
    public static (string? Commit, string? Branch) Read(string path)
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

/// <summary>
/// The source revision a project was imported from, recorded once at import in its workspace
/// (<c>import-revision.json</c>), and whether the imported copy is now stale.
/// </summary>
public static class ImportRevisions
{
    public const string FileName = "import-revision.json";

    public static ImportedRevision Capture(ProjectRecord project, TimeProvider time)
    {
        var now = time.GetUtcNow();
        if (project.SourceKind == ProjectSourceKind.Directory)
        {
            var (commit, branch) = GitRevision.Read(project.SourceLocation);
            return new ImportedRevision(commit is null ? "folder" : "git", commit, branch, Path.GetFullPath(project.SourceLocation), now);
        }
        if (project.SourceKind == ProjectSourceKind.ZipArchive)
        {
            // GitHub's source archives carry the commit they were made from as the ZIP comment.
            var archive = project.PreservedArchivePath is { } p && File.Exists(p) ? p : project.SourceLocation;
            string? comment = null;
            try
            {
                using var zip = System.IO.Compression.ZipFile.OpenRead(archive);
                comment = zip.Comment?.Trim();
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { }
            var commit = comment is { Length: 40 } c && c.All(Uri.IsHexDigit) ? c.ToLowerInvariant() : null;
            return new ImportedRevision(commit is null ? "zip" : "zip-comment", commit, null, Path.GetFileName(project.SourceLocation), now);
        }
        return new ImportedRevision("artifact", null, null, Path.GetFileName(project.SourceLocation), now);
    }

    public static async Task WriteAsync(ProjectRecord project, ImportedRevision revision, CancellationToken ct) =>
        await File.WriteAllTextAsync(Path.Combine(project.WorkspacePath, FileName), JsonSerializer.Serialize(revision, Json), ct).ConfigureAwait(false);

    public static ImportedRevision? Read(ProjectRecord project)
    {
        try
        {
            var file = Path.Combine(project.WorkspacePath, FileName);
            return File.Exists(file) ? JsonSerializer.Deserialize<ImportedRevision>(File.ReadAllText(file), Json) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The imported revision and, where Maker can see it, whether the source has moved on since.</summary>
    public static RevisionStatus Check(ProjectRecord project)
    {
        var recorded = Read(project);
        if (recorded is null)
            return new RevisionStatus(RevisionState.Unknown, null, null,
                "No revision was recorded (imported before Maker recorded revisions). Re-import the project to record it.");
        if (recorded.Kind is "git" or "folder")
        {
            if (!Directory.Exists(project.SourceLocation))
                return new RevisionStatus(RevisionState.Unknown, recorded, null, $"Imported from {project.SourceLocation}, which no longer exists, so Maker cannot tell whether it changed.");
            var (now, branch) = GitRevision.Read(project.SourceLocation);
            if (recorded.Commit is null)
                return new RevisionStatus(RevisionState.Unknown, recorded, now, "The folder was not a Git working copy when it was imported; Maker cannot tell whether it changed since.");
            if (now is null)
                return new RevisionStatus(RevisionState.Unknown, recorded, null, $"{project.SourceLocation} is no longer a Git working copy.");
            return now == recorded.Commit
                ? new RevisionStatus(RevisionState.Current, recorded, now, $"Up to date: the original folder is still at {Short(now)}{(branch is null ? "" : $" ({branch})")}.")
                : new RevisionStatus(RevisionState.Stale, recorded, now,
                    $"STALE: the original folder is now at {Short(now)}{(branch is null ? "" : $" ({branch})")}; the imported copy is {Short(recorded.Commit)}. Re-import before building.");
        }
        if (recorded.Kind == "zip-comment")
            return new RevisionStatus(RevisionState.FromArchive, recorded, null,
                $"Imported from a source archive of commit {Short(recorded.Commit!)}. Maker cannot see newer commits from a ZIP; re-import a fresh archive to update.");
        return new RevisionStatus(RevisionState.Unknown, recorded, null, recorded.Kind == "zip"
            ? "Imported from a ZIP without a commit (not a GitHub source archive). Maker cannot identify the revision."
            : "Imported from a built artifact: there is no source revision.");
    }

    public static string Short(string sha) => sha[..Math.Min(12, sha.Length)];

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
}
