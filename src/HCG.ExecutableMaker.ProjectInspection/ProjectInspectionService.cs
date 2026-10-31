using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.ProjectInspection;

public sealed class ProjectInspectionService(IEnumerable<IProjectInspector> inspectors, TimeProvider time) : IProjectInspectionService
{
    private readonly IReadOnlyList<IProjectInspector> _inspectors = inspectors.ToList();

    public async Task<ProjectAnalysis> InspectAsync(string sourceDirectory, ToolchainReport? toolchains, CancellationToken cancellationToken)
    {
        var index = await Task.Run(() => SourceIndex.Build(sourceDirectory, cancellationToken), cancellationToken).ConfigureAwait(false);
        var ranked = _inspectors
            .Select(i => (Inspector: i, Score: i.Match(index)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ToList();

        if (ranked.Count == 0)
        {
            return new ProjectAnalysis
            {
                InspectorId = "none",
                ProjectName = new DirectoryInfo(sourceDirectory).Name,
                Language = "Unknown",
                Framework = "Unknown",
                ApplicationType = "Unknown",
                Support = SupportLevel.Unrecognized,
                SupportSummary = "No supported project files were found. M0.1 builds .NET projects (.sln, .slnx, .csproj).",
                Diagnostics = index.Files.Count == 0
                    ? [new AnalysisDiagnostic(DiagnosticSeverity.Error, "The workspace is empty.")]
                    : [new AnalysisDiagnostic(DiagnosticSeverity.Warning, $"Scanned {index.Files.Count:N0} files; no recognizable project definition found.")],
                AnalyzedAt = time.GetUtcNow(),
            };
        }

        var analysis = await ranked[0].Inspector.InspectAsync(index, toolchains, cancellationToken).ConfigureAwait(false);
        var others = ranked.Skip(1).Select(r => r.Inspector.DisplayName).Distinct().ToList();
        var extra = new List<AnalysisDiagnostic>();
        if (others.Count > 0)
            extra.Add(new AnalysisDiagnostic(DiagnosticSeverity.Info, $"Also found files for: {string.Join(", ", others)}. These are not part of this build."));
        if (index.Truncated)
            extra.Add(new AnalysisDiagnostic(DiagnosticSeverity.Warning, $"Only the first {SourceIndex.MaxFiles:N0} files were scanned."));
        return extra.Count == 0 ? analysis : analysis with { Diagnostics = [.. analysis.Diagnostics, .. extra] };
    }
}
