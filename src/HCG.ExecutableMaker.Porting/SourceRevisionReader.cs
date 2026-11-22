using System.Security.Cryptography;
using System.Text;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.ProjectInspection;

namespace HCG.ExecutableMaker.Porting;

/// <summary>
/// Records what was converted: the original folder's Git revision (read from .git as files; no git command runs) and a
/// SHA-256 fingerprint of the files HCG converted, so a conversion can be reproduced from the same source.
/// </summary>
public static class SourceRevisionReader
{
    public static SourceRevision Capture(string? originalLocation, SourceIndex converted, TimeProvider time)
    {
        var (commit, branch) = originalLocation is null ? (null, null) : Git(originalLocation);
        string? original = null;
        if (originalLocation is not null && Directory.Exists(originalLocation))
            original = Fingerprint(SourceIndex.Build(originalLocation), out _);
        return new SourceRevision
        {
            Vcs = commit is null ? null : "git", Commit = commit, Branch = branch,
            ContentSha256 = Fingerprint(converted, out var count), FileCount = count, CapturedAt = time.GetUtcNow(),
            ConvertedDirectory = converted.Root, OriginalDirectory = original is null ? null : Path.GetFullPath(originalLocation!), OriginalSha256 = original,
        };
    }

    /// <summary>SHA-256 over "path\0sha256\n" for every file, in ordinal path order (independent of timestamps and OS).</summary>
    public static string Fingerprint(SourceIndex index, out int count)
    {
        using var all = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        count = 0;
        foreach (var rel in index.Files.OrderBy(f => f, StringComparer.Ordinal))
        {
            string fileHash;
            try
            {
                using var stream = File.OpenRead(index.FullPath(rel));
                fileHash = Convert.ToHexStringLower(SHA256.HashData(stream));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { fileHash = "unreadable"; }
            all.AppendData(Encoding.UTF8.GetBytes($"{rel}\0{fileHash}\n"));
            count++;
        }
        return Convert.ToHexStringLower(all.GetHashAndReset());
    }

    /// <summary>The commit and branch of the Git working copy containing <paramref name="path"/> (null when none).</summary>
    public static (string? Commit, string? Branch) Git(string path) => GitRevision.Read(path);

}
