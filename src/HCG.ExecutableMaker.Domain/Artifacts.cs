namespace HCG.ExecutableMaker.Domain;

public enum ArtifactKind
{
    Directory,
    File,
}

public enum VerificationStatus
{
    Passed,
    PassedWithWarnings,
    Failed,
    /// <summary>Could not be verified on this host (e.g. launch test for macOS on Linux).</summary>
    NotVerifiable,
}

public sealed record VerificationCheck(string Name, VerificationStatus Status, string Detail);

public sealed class ArtifactRecord
{
    public required Guid Id { get; init; }
    public required Guid BuildId { get; init; }
    public required PackagingFormat Format { get; init; }
    public required ArtifactKind Kind { get; init; }
    public required string Path { get; init; }
    public required long SizeBytes { get; init; }
    /// <summary>SHA-256 of the file, or of a deterministic manifest of the directory's files.</summary>
    public required string Sha256 { get; init; }
    public required VerificationStatus Verification { get; init; }
    public IReadOnlyList<VerificationCheck> Checks { get; init; } = [];
    public required DateTimeOffset CreatedAt { get; init; }

    public string FileName => System.IO.Path.GetFileName(Path.TrimEnd('/', '\\'));
}
