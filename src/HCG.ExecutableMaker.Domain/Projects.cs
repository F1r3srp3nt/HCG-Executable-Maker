namespace HCG.ExecutableMaker.Domain;

public enum ProjectSourceKind
{
    ZipArchive,
    Directory,
    /// <summary>A built mobile artifact (.apk/.aab/.ipa) copied into a workspace for inspection or conversion.</summary>
    Artifact,
}

/// <summary>A project imported into an isolated HCG workspace.</summary>
public sealed class ProjectRecord
{
    public required Guid Id { get; init; }
    public required string Name { get; set; }
    public required ProjectSourceKind SourceKind { get; init; }

    /// <summary>Where the user's original ZIP or directory lives. Never modified or deleted by HCG.</summary>
    public required string SourceLocation { get; init; }

    /// <summary>Isolated workspace root owned by HCG (contains <c>source/</c> and, for ZIP imports, <c>original/</c>).</summary>
    public required string WorkspacePath { get; init; }

    /// <summary>Copy of the original archive preserved inside the workspace, if any.</summary>
    public string? PreservedArchivePath { get; init; }

    public required DateTimeOffset ImportedAt { get; init; }

    /// <summary>
    /// What the copy was made from: the original folder's git commit at import or refresh (e.g. "git 03bc6e55f0a7 (main)"),
    /// or the ZIP's SHA-256. Null for projects imported before HCG recorded it.
    /// </summary>
    public string? SourceRevision { get; set; }

    /// <summary>SHA-256 over every copied file (relative path + content hash), so a copy can be compared with its origin.</summary>
    public string? SourceFingerprint { get; set; }

    /// <summary>When the copy was last replaced with the original's current files ("Refresh from source").</summary>
    public DateTimeOffset? RefreshedAt { get; set; }
    public DateTimeOffset? LastOpenedAt { get; set; }

    /// <summary>Most recent analysis, serialized as JSON by the persistence layer.</summary>
    public ProjectAnalysis? Analysis { get; set; }

    public string SourceDirectory => Path.Combine(WorkspacePath, "source");
}

/// <summary>HCG's copy of a project compared with the folder or ZIP it was imported from.</summary>
public sealed record SourceDrift
{
    /// <summary>False when the original folder or ZIP no longer exists (or cannot be read).</summary>
    public required bool OriginAvailable { get; init; }
    public string? OriginProblem { get; init; }
    /// <summary>The original's revision now (git commit or ZIP hash).</summary>
    public string? OriginRevision { get; init; }
    public string? OriginFingerprint { get; init; }
    public required string SnapshotFingerprint { get; init; }
    public IReadOnlyList<string> Added { get; init; } = [];
    public IReadOnlyList<string> Removed { get; init; } = [];
    public IReadOnlyList<string> Changed { get; init; } = [];
    public bool IsCurrent => OriginAvailable && Added.Count == 0 && Removed.Count == 0 && Changed.Count == 0;
    public int DifferenceCount => Added.Count + Removed.Count + Changed.Count;

    public static bool IsProjectDefinition(string path) =>
        path.Contains(".xcodeproj/", StringComparison.Ordinal) || path.Contains(".xcworkspace/", StringComparison.Ordinal) ||
        path.EndsWith("Package.swift", StringComparison.Ordinal) || path.EndsWith(".csproj", StringComparison.Ordinal) ||
        path.EndsWith(".gradle", StringComparison.Ordinal) || path.EndsWith(".gradle.kts", StringComparison.Ordinal) ||
        path.EndsWith("Podfile", StringComparison.Ordinal) || path.EndsWith(".xcconfig", StringComparison.Ordinal) ||
        path.EndsWith("Makefile", StringComparison.Ordinal) || path.EndsWith("package.json", StringComparison.Ordinal);

    public string Describe(int max = 8)
    {
        if (!OriginAvailable) return $"The original cannot be compared: {OriginProblem}";
        if (IsCurrent) return "HCG's copy matches the original's current files.";
        // Files that define what is built (schemes, project files, manifests) are named first, then everything else.
        var all = Added.Select(f => (f, "new ")).Concat(Removed.Select(f => (f, "removed "))).Concat(Changed.Select(f => (f, "changed ")));
        var sample = all.OrderBy(x => IsProjectDefinition(x.f) ? 0 : 1).Select(x => x.Item2 + x.f).Take(max);
        return $"The original changed since HCG copied it: {Changed.Count} changed, {Added.Count} new, {Removed.Count} removed ({string.Join(", ", sample)}{(DifferenceCount > max ? ", …" : "")}).";
    }
}
