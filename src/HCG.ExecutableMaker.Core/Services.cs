using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.Core;

/// <summary>Copies a project into an isolated workspace. Never executes or modifies the source.</summary>
public interface IProjectImporter
{
    Task<ImportResult> ImportZipAsync(string zipPath, ImportLimits limits, IProgress<ImportProgress>? progress, CancellationToken cancellationToken);
    Task<ImportResult> ImportDirectoryAsync(string directory, ImportLimits limits, IProgress<ImportProgress>? progress, CancellationToken cancellationToken);
}

/// <summary>Static analysis of a workspace. Must never execute imported code.</summary>
public interface IProjectInspectionService
{
    Task<ProjectAnalysis> InspectAsync(string sourceDirectory, ToolchainReport? toolchains, CancellationToken cancellationToken);
}

public interface IToolchainDetector
{
    Task<ToolchainReport> DetectAsync(CancellationToken cancellationToken);
}

/// <summary>Caches the most recent toolchain report so pages share a single detection pass.</summary>
public interface IToolchainService
{
    ToolchainReport? Latest { get; }
    event EventHandler<ToolchainReport>? Updated;
    Task<ToolchainReport> GetAsync(bool refresh = false, CancellationToken cancellationToken = default);
}

public sealed class ToolchainService(IToolchainDetector detector) : IToolchainService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public ToolchainReport? Latest { get; private set; }
    public event EventHandler<ToolchainReport>? Updated;

    public async Task<ToolchainReport> GetAsync(bool refresh = false, CancellationToken cancellationToken = default)
    {
        if (!refresh && Latest is { } cached) return cached;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!refresh && Latest is { } again) return again;
            var report = await detector.DetectAsync(cancellationToken).ConfigureAwait(false);
            Latest = report;
            Updated?.Invoke(this, report);
            return report;
        }
        finally { _gate.Release(); }
    }
}
