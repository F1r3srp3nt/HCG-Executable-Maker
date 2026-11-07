using HCG.ExecutableMaker.Domain;
using Microsoft.Extensions.Logging;

namespace HCG.ExecutableMaker.Core;

/// <summary>Import → inspect → persist workflow, and safe workspace removal.</summary>
public sealed class ProjectService(
    IProjectImporter importer,
    IProjectInspectionService inspection,
    IProjectRepository projects,
    ISettingsStore settings,
    IToolchainService toolchains,
    IAppPaths paths,
    TimeProvider time,
    ILogger<ProjectService> logger)
{
    public async Task<ImportResult> ImportZipAsync(string zipPath, IProgress<ImportProgress>? progress, CancellationToken cancellationToken)
    {
        var limits = (await settings.LoadAsync(cancellationToken).ConfigureAwait(false)).ImportLimits;
        var result = await importer.ImportZipAsync(zipPath, limits, progress, cancellationToken).ConfigureAwait(false);
        return await FinishImportAsync(result, progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ImportResult> ImportDirectoryAsync(string directory, IProgress<ImportProgress>? progress, CancellationToken cancellationToken)
    {
        var limits = (await settings.LoadAsync(cancellationToken).ConfigureAwait(false)).ImportLimits;
        var result = await importer.ImportDirectoryAsync(directory, limits, progress, cancellationToken).ConfigureAwait(false);
        return await FinishImportAsync(result, progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ImportResult> ImportArtifactAsync(string file, IProgress<ImportProgress>? progress, CancellationToken cancellationToken)
    {
        var limits = (await settings.LoadAsync(cancellationToken).ConfigureAwait(false)).ImportLimits;
        var result = await importer.ImportArtifactAsync(file, limits, progress, cancellationToken).ConfigureAwait(false);
        return await FinishImportAsync(result, progress, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ImportResult> FinishImportAsync(ImportResult result, IProgress<ImportProgress>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new ImportProgress("Inspecting project", result.FileCount, result.FileCount, result.TotalBytes, null));
        await InspectAsync(result.Project, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Imported project {ProjectId} ({Name}) from {Source}: {Files} files, {Bytes} bytes",
            result.Project.Id, result.Project.Name, result.Project.SourceLocation, result.FileCount, result.TotalBytes);
        return result;
    }

    public async Task<ProjectAnalysis> InspectAsync(ProjectRecord project, CancellationToken cancellationToken)
    {
        var report = await toolchains.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var analysis = await inspection.InspectAsync(project.SourceDirectory, report, cancellationToken).ConfigureAwait(false);
        project.Analysis = analysis;
        if (!string.IsNullOrWhiteSpace(analysis.ProjectName) && analysis.Support != SupportLevel.Unrecognized)
            project.Name = analysis.ProjectName;
        project.LastOpenedAt = time.GetUtcNow();
        await projects.UpsertAsync(project, cancellationToken).ConfigureAwait(false);
        return analysis;
    }

    /// <summary>Persists an analysis refined after import (e.g. a Swift package evaluated on this Mac after trust).</summary>
    public async Task UpdateAnalysisAsync(ProjectRecord project, ProjectAnalysis analysis, CancellationToken cancellationToken = default)
    {
        project.Analysis = analysis;
        await projects.UpsertAsync(project, cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkOpenedAsync(ProjectRecord project, CancellationToken cancellationToken = default)
    {
        project.LastOpenedAt = time.GetUtcNow();
        await projects.UpsertAsync(project, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes the HCG-owned workspace and the project record (with its build history). The user's original
    /// source (ZIP or directory) is never touched; deletion is refused if the workspace is not inside HCG's
    /// workspace directory.
    /// </summary>
    public async Task RemoveProjectAsync(ProjectRecord project, CancellationToken cancellationToken = default)
    {
        var workspace = Path.GetFullPath(project.WorkspacePath);
        if (!PathGuard.IsWithin(paths.WorkspacesDirectory, workspace) ||
            string.Equals(Path.TrimEndingDirectorySeparator(workspace), Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths.WorkspacesDirectory)), StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Refusing to delete '{workspace}': it is not an HCG workspace.");
        }
        if (PathGuard.IsWithin(workspace, project.SourceLocation) || PathGuard.IsWithin(project.SourceLocation, workspace))
            throw new InvalidOperationException("Refusing to delete: the workspace overlaps the original source location.");

        if (Directory.Exists(workspace))
            Directory.Delete(workspace, recursive: true);
        await projects.DeleteAsync(project.Id, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Removed workspace {Workspace} for project {ProjectId}", workspace, project.Id);
    }
}
