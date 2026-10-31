using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.ProjectInspection;

/// <summary>
/// Extension point for language/framework detection. Implementations must be purely static: they read files and
/// never execute project code or build tooling.
/// </summary>
public interface IProjectInspector
{
    string Id { get; }
    string DisplayName { get; }

    /// <summary>Confidence that this inspector applies (0 = not applicable). The highest score wins.</summary>
    int Match(SourceIndex index);

    Task<ProjectAnalysis> InspectAsync(SourceIndex index, ToolchainReport? toolchains, CancellationToken cancellationToken);
}
