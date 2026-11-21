namespace HCG.ExecutableMaker.Domain;

/// <summary>
/// The source revision recorded when a project was imported. Kind: "git" (a Git working copy; Commit and Branch from
/// its HEAD), "folder" (not a Git working copy), "zip-comment" (a GitHub source archive; Commit from the ZIP comment),
/// "zip" (no commit) or "artifact" (a built file; no source).
/// </summary>
public sealed record ImportedRevision(string Kind, string? Commit, string? Branch, string? Origin, DateTimeOffset CapturedAt);

public enum RevisionState
{
    /// <summary>The original folder is still at the imported commit.</summary>
    Current,
    /// <summary>The original folder has moved to another commit: the imported copy is out of date.</summary>
    Stale,
    /// <summary>Imported from a source archive of a known commit; newer commits cannot be seen.</summary>
    FromArchive,
    Unknown,
}

public sealed record RevisionStatus(RevisionState State, ImportedRevision? Recorded, string? CurrentCommit, string Detail);
