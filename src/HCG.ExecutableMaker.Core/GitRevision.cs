namespace HCG.ExecutableMaker.Core;

/// <summary>
/// The git commit a folder is checked out at, read from <c>.git</c> on disk (HEAD, loose refs, packed-refs, worktree
/// pointers). Read only; git is not run. Null when the folder is not inside a git checkout.
/// </summary>
public static class GitRevision
{
    /// <summary>e.g. "git 03bc6e55f0a7 (main)" or "git 03bc6e55f0a7 (detached)".</summary>
    public static string? Read(string directory)
    {
        try
        {
            var start = Path.GetFullPath(directory);
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var git = Path.Combine(dir.FullName, ".git");
                if (File.Exists(git) && File.ReadAllText(git).Trim() is { } pointer && pointer.StartsWith("gitdir:", StringComparison.Ordinal))
                    git = Path.GetFullPath(Path.Combine(dir.FullName, pointer[7..].Trim()));
                if (!Directory.Exists(git)) continue;
                var head = File.ReadAllText(Path.Combine(git, "HEAD")).Trim();
                if (!head.StartsWith("ref: ", StringComparison.Ordinal)) return $"git {head[..Math.Min(12, head.Length)]} (detached)";
                var refName = head[5..];
                // A linked worktree keeps its refs in the common directory.
                var common = File.Exists(Path.Combine(git, "commondir"))
                    ? Path.GetFullPath(Path.Combine(git, File.ReadAllText(Path.Combine(git, "commondir")).Trim()))
                    : git;
                string? sha = null;
                foreach (var root in new[] { git, common }.Distinct())
                {
                    var refFile = Path.Combine(root, refName);
                    if (File.Exists(refFile)) { sha = File.ReadAllText(refFile).Trim(); break; }
                    var packed = Path.Combine(root, "packed-refs");
                    if (File.Exists(packed) && File.ReadLines(packed).Select(l => l.Split(' ')).FirstOrDefault(p => p.Length == 2 && p[1] == refName) is { } hit)
                    { sha = hit[0]; break; }
                }
                var branch = refName.Replace("refs/heads/", "");
                return sha is null ? $"git {branch} (no commit)" : $"git {sha[..Math.Min(12, sha.Length)]} ({branch})";
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }
}
