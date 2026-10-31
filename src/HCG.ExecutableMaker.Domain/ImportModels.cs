namespace HCG.ExecutableMaker.Domain;

public sealed record ImportLimits
{
    public long MaxTotalBytes { get; init; } = 2L * 1024 * 1024 * 1024;
    public long MaxFileBytes { get; init; } = 1L * 1024 * 1024 * 1024;
    public int MaxFileCount { get; init; } = 50_000;
    /// <summary>Entries whose uncompressed/compressed ratio exceeds this are treated as a decompression bomb.</summary>
    public double MaxCompressionRatio { get; init; } = 200;
}

public sealed record ImportProgress(string Stage, int FilesProcessed, int? TotalFiles, long BytesWritten, string? CurrentItem)
{
    public double? Fraction => TotalFiles is > 0 ? Math.Clamp((double)FilesProcessed / TotalFiles.Value, 0, 1) : null;
}

public sealed record ImportResult(ProjectRecord Project, int FileCount, long TotalBytes, IReadOnlyList<string> Warnings);

/// <summary>Raised when an import is rejected for safety or validity reasons. No partial workspace is left behind.</summary>
public sealed class ImportRejectedException(string message, Exception? inner = null) : Exception(message, inner);
