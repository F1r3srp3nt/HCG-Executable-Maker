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

    public async Task<ImportResult> ImportDirectoryAsync(string directory, IProgress<ImportProgress>? progress, CancellationToken cancellationToken, Action<string>? prepareCopy = null)
    {
        var limits = (await settings.LoadAsync(cancellationToken).ConfigureAwait(false)).ImportLimits;
        var result = await importer.ImportDirectoryAsync(directory, limits, progress, cancellationToken).ConfigureAwait(false);
        try
        {
            prepareCopy?.Invoke(result.Project.SourceDirectory);
            return await FinishImportAsync(result, progress, cancellationToken).ConfigureAwait(false);
        }
        catch when (prepareCopy is not null)
        {
            await RemoveProjectAsync(result.Project, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
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
    /// Replaces the HCG-owned source of an existing project with a freshly generated tree (a desktop project generated
    /// again from its web app), keeping the project record and its build history, then inspects it again. The swap is
    /// done by renaming folders inside the workspace, so an interrupted replace leaves the previous source in place.
    /// </summary>
    public async Task<ProjectAnalysis> ReplaceSourceAsync(ProjectRecord project, string newSource, CancellationToken cancellationToken)
    {
        var workspace = Path.GetFullPath(project.WorkspacePath);
        if (!PathGuard.IsWithin(paths.WorkspacesDirectory, workspace))
            throw new InvalidOperationException($"Refusing to replace '{workspace}': it is not an HCG workspace.");
        newSource = Path.GetFullPath(newSource);
        if (PathGuard.IsWithin(workspace, newSource)) throw new InvalidOperationException("The new source must not be inside the workspace it replaces.");
        var current = Path.GetFullPath(project.SourceDirectory);
        var next = Path.Combine(workspace, "source.next");
        var old = Path.Combine(workspace, "source.old");
        foreach (var d in new[] { next, old }) if (Directory.Exists(d)) Directory.Delete(d, recursive: true);
        CopyTree(newSource, next, cancellationToken);
        Directory.Move(current, old);
        try { Directory.Move(next, current); }
        catch
        {
            Directory.Move(old, current);
            throw;
        }
        try { Directory.Delete(old, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger.LogWarning(ex, "Could not remove the previous source {Old}", old); }
        logger.LogInformation("Replaced the source of project {ProjectId} from {Source}", project.Id, newSource);
        return await InspectAsync(project, cancellationToken).ConfigureAwait(false);
    }

    private static void CopyTree(string from, string to, CancellationToken ct)
    {
        Directory.CreateDirectory(to);
        foreach (var entry in new DirectoryInfo(from).EnumerateFileSystemInfos())
        {
            ct.ThrowIfCancellationRequested();
            if (entry.LinkTarget is not null) continue;
            var target = Path.Combine(to, entry.Name);
            if (entry is DirectoryInfo d) CopyTree(d.FullName, target, ct);
            else File.Copy(entry.FullName, target);
        }
    }

    /// <summary>
    /// Refresh source: copies the project again from its original folder or ZIP (or from <paramref name="newLocation"/>,
    /// same kind) with the same validation and limits as an import, then swaps it in for the old copy. The project keeps
    /// its id, build history and per-project choices (scheme, signing), and is inspected again. The original is only read.
    /// </summary>
    public async Task<SourceRefreshResult> RefreshSourceAsync(ProjectRecord project, string? newLocation, IProgress<ImportProgress>? progress, CancellationToken cancellationToken)
    {
        if (project.SourceKind == ProjectSourceKind.Artifact)
            throw new ImportRejectedException("An imported artifact has no source project to refresh. Import the artifact again instead.");
        var location = Path.GetFullPath(newLocation ?? project.SourceLocation);
        var zip = project.SourceKind == ProjectSourceKind.ZipArchive;
        if (zip ? !File.Exists(location) : !Directory.Exists(location))
            throw new ImportRejectedException(zip
                ? $"The ZIP '{location}' does not exist. Choose the current ZIP with “Refresh from…”."
                : $"The folder '{location}' does not exist. Choose the project's current folder with “Refresh from…”.");
        if (zip != location.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && newLocation is not null)
            throw new ImportRejectedException($"This project was imported from a {(zip ? "ZIP" : "folder")}; refresh it from a {(zip ? "ZIP" : "folder")}, or import the other one as a new project.");

        var before = SourceSnapshot.Fingerprint(project.SourceDirectory, cancellationToken);
        var limits = (await settings.LoadAsync(cancellationToken).ConfigureAwait(false)).ImportLimits;
        var fresh = zip
            ? await importer.ImportZipAsync(location, limits, progress, cancellationToken).ConfigureAwait(false)
            : await importer.ImportDirectoryAsync(location, limits, progress, cancellationToken).ConfigureAwait(false);
        try
        {
            await ReplaceSourceAsync(project, fresh.Project.SourceDirectory, cancellationToken).ConfigureAwait(false);
            if (zip && project.PreservedArchivePath is { } kept)
                File.Copy(location, kept, overwrite: true);
            project.SourceLocation = location;
            project.ImportedAt = time.GetUtcNow();
            await projects.UpsertAsync(project, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // The temporary import was never registered as a project; only its workspace folder exists.
            var temp = Path.GetFullPath(fresh.Project.WorkspacePath);
            if (PathGuard.IsWithin(paths.WorkspacesDirectory, temp) && Directory.Exists(temp))
                try { Directory.Delete(temp, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger.LogWarning(ex, "Could not remove the temporary import {Temp}", temp); }
        }
        var after = SourceSnapshot.Fingerprint(project.SourceDirectory, cancellationToken);
        logger.LogInformation("Refreshed the source of project {ProjectId} from {Location}: {Before} → {After}", project.Id, location, before.Sha256, after.Sha256);
        return new SourceRefreshResult(location, before.Sha256, before.Files, after.Sha256, after.Files, SourceSnapshot.Git(location).Commit, fresh.Warnings);
    }

    /// <summary>Every saved Mobile Build Studio scheme choice (project id "N" format → scheme).</summary>
    public async Task<IReadOnlyDictionary<string, string>> GetMobileSchemesAsync(CancellationToken cancellationToken = default) =>
        (await settings.LoadAsync(cancellationToken).ConfigureAwait(false)).MobileSchemes;

    /// <summary>The Xcode scheme chosen for this project in Mobile Build Studio (null: none chosen yet).</summary>
    public async Task<string?> GetMobileSchemeAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        (await settings.LoadAsync(cancellationToken).ConfigureAwait(false)).MobileSchemes.GetValueOrDefault(projectId.ToString("N"));

    public async Task SetMobileSchemeAsync(Guid projectId, string? scheme, CancellationToken cancellationToken = default)
    {
        var s = await settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        var map = new Dictionary<string, string>(s.MobileSchemes, StringComparer.Ordinal);
        if (scheme is null) map.Remove(projectId.ToString("N"));
        else map[projectId.ToString("N")] = scheme;
        await settings.SaveAsync(s with { MobileSchemes = map }, cancellationToken).ConfigureAwait(false);
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
