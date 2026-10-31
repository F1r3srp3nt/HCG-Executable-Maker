namespace HCG.ExecutableMaker.Domain;

public enum ProjectSourceKind
{
    ZipArchive,
    Directory,
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
    public DateTimeOffset? LastOpenedAt { get; set; }

    /// <summary>Most recent analysis, serialized as JSON by the persistence layer.</summary>
    public ProjectAnalysis? Analysis { get; set; }

    public string SourceDirectory => Path.Combine(WorkspacePath, "source");
}
