using System.IO.Compression;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HCG.ExecutableMaker.Infrastructure.Import;

/// <summary>
/// Imports ZIP archives and directories into isolated workspaces under <see cref="IAppPaths.WorkspacesDirectory"/>.
/// The import fails closed: any unsafe entry rejects the whole archive and no partial workspace is left behind.
/// Nothing from the project is executed.
/// </summary>
public sealed class ProjectImporter(IAppPaths paths, TimeProvider time, ILogger<ProjectImporter>? logger = null) : IProjectImporter
{
    private const int UnixFileTypeMask = 0xF000; // S_IFMT
    private const int UnixSymlink = 0xA000;      // S_IFLNK
    private const int UnixDirectory = 0x4000;    // S_IFDIR
    private const int UnixRegular = 0x8000;      // S_IFREG

    /// <summary>Directories never copied from a directory import (build outputs, VCS and IDE state).</summary>
    public static readonly IReadOnlySet<string> ExcludedDirectoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".hg", ".svn", ".vs", ".idea", "node_modules", "TestResults",
    };

    private readonly ILogger _logger = logger ?? NullLogger<ProjectImporter>.Instance;

    public async Task<ImportResult> ImportZipAsync(string zipPath, ImportLimits limits, IProgress<ImportProgress>? progress, CancellationToken cancellationToken)
    {
        zipPath = Path.GetFullPath(zipPath);
        if (!File.Exists(zipPath))
            throw new ImportRejectedException($"The file '{zipPath}' does not exist.");

        var id = Guid.NewGuid();
        var staging = CreateStaging(id);
        var warnings = new List<string>();
        try
        {
            progress?.Report(new ImportProgress("Validating archive", 0, null, 0, Path.GetFileName(zipPath)));
            ZipArchive archive;
            try { archive = ZipFile.OpenRead(zipPath); }
            catch (InvalidDataException ex) { throw new ImportRejectedException("The file is not a valid ZIP archive or is corrupted.", ex); }

            int fileCount;
            long totalBytes = 0;
            using (archive)
            {
                var plan = ValidateArchive(archive, limits, warnings);
                fileCount = plan.Count;
                var sourceDir = Path.Combine(staging, "source");
                Directory.CreateDirectory(sourceDir);

                var processed = 0;
                foreach (var (entry, segments, isDirectory) in plan.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var destination = PathGuard.CombineWithin(sourceDir, Path.Combine(segments));
                    try
                    {
                        Directory.CreateDirectory(isDirectory ? destination : Path.GetDirectoryName(destination)!);
                    }
                    catch (IOException ex)
                    {
                        throw new ImportRejectedException($"The archive contains conflicting file and directory entries at '{entry.FullName}'.", ex);
                    }
                    if (isDirectory) continue;
                    EnsureNoLinkedAncestor(sourceDir, destination);
                    long written;
                    try
                    {
                        written = await ExtractEntryAsync(entry, destination, limits, totalBytes, cancellationToken).ConfigureAwait(false);
                    }
                    catch (InvalidDataException ex)
                    {
                        throw new ImportRejectedException($"The archive is corrupted (entry '{entry.FullName}' could not be decompressed).", ex);
                    }
                    catch (IOException ex) when (File.Exists(destination) || Directory.Exists(destination))
                    {
                        throw new ImportRejectedException($"The archive contains conflicting entries for '{entry.FullName}'. Existing files are never overwritten.", ex);
                    }
                    totalBytes += written;
                    ApplyUnixMode(destination, entry.ExternalAttributes);
                    processed++;
                    progress?.Report(new ImportProgress("Extracting", processed, fileCount, totalBytes, entry.FullName));
                }
            }

            // Preserve the original archive alongside the extracted source.
            var originalDir = Path.Combine(staging, "original");
            Directory.CreateDirectory(originalDir);
            var preservedName = PathGuard.SanitizeFileName(Path.GetFileName(zipPath), "archive.zip");
            File.Copy(zipPath, Path.Combine(originalDir, preservedName), overwrite: false);

            var workspace = Commit(staging, id);
            var name = GuessName(Path.Combine(workspace, "source"), Path.GetFileNameWithoutExtension(zipPath));
            var project = new ProjectRecord
            {
                Id = id,
                Name = name,
                SourceKind = ProjectSourceKind.ZipArchive,
                SourceLocation = zipPath,
                WorkspacePath = workspace,
                PreservedArchivePath = Path.Combine(workspace, "original", preservedName),
                ImportedAt = time.GetUtcNow(),
            };
            _logger.LogInformation("Extracted {Count} files ({Bytes} bytes) from {Zip}", fileCount, totalBytes, zipPath);
            return new ImportResult(project, fileCount, totalBytes, warnings);
        }
        catch
        {
            TryDelete(staging);
            throw;
        }
    }

    private sealed record ArchivePlan(List<(ZipArchiveEntry Entry, string[] Segments, bool IsDirectory)> Entries, int Count);

    private static ArchivePlan ValidateArchive(ZipArchive archive, ImportLimits limits, List<string> warnings)
    {
        var entries = new List<(ZipArchiveEntry, string[], bool)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // case-insensitive: archive must extract identically on every OS
        var problems = new List<string>();
        long declaredTotal = 0;
        var fileCount = 0;
        var skippedMetadata = 0;

        IReadOnlyCollection<ZipArchiveEntry> all;
        try { all = archive.Entries; }
        catch (InvalidDataException ex) { throw new ImportRejectedException("The ZIP central directory is corrupted.", ex); }

        foreach (var entry in all)
        {
            var name = entry.FullName;
            if (name.StartsWith("__MACOSX/", StringComparison.Ordinal) || Path.GetFileName(name) == ".DS_Store")
            {
                skippedMetadata++;
                continue;
            }
            var isDirectory = name.EndsWith('/') || name.EndsWith('\\');
            if (!PathGuard.TryNormalizeArchiveEntry(name, out var segments, out var reason))
            {
                problems.Add($"'{name}': {reason}");
                continue;
            }
            var unixType = (entry.ExternalAttributes >> 16) & UnixFileTypeMask;
            if (unixType == UnixSymlink)
            {
                problems.Add($"'{name}': symbolic links are not allowed");
                continue;
            }
            if (unixType != 0 && unixType != UnixRegular && unixType != UnixDirectory)
            {
                problems.Add($"'{name}': special file type is not allowed");
                continue;
            }
            if (unixType == UnixDirectory) isDirectory = true;

            var key = string.Join('/', segments);
            if (!seen.Add(key) && !isDirectory)
            {
                problems.Add($"'{name}': duplicate entry (would overwrite another file)");
                continue;
            }
            if (isDirectory)
            {
                entries.Add((entry, segments, true));
                continue;
            }

            fileCount++;
            if (entry.Length > limits.MaxFileBytes)
                problems.Add($"'{name}': {entry.Length:N0} bytes exceeds the per-file limit of {limits.MaxFileBytes:N0} bytes");
            if (entry.CompressedLength > 0 && entry.Length > 1_048_576 && (double)entry.Length / entry.CompressedLength > limits.MaxCompressionRatio)
                problems.Add($"'{name}': suspicious compression ratio (possible decompression bomb)");
            declaredTotal += entry.Length;
            entries.Add((entry, segments, false));
        }

        if (fileCount > limits.MaxFileCount)
            throw new ImportRejectedException($"The archive contains {fileCount:N0} files, exceeding the limit of {limits.MaxFileCount:N0}.");
        if (declaredTotal > limits.MaxTotalBytes)
            throw new ImportRejectedException($"The archive expands to {declaredTotal:N0} bytes, exceeding the limit of {limits.MaxTotalBytes:N0} bytes.");
        if (problems.Count > 0)
        {
            var shown = string.Join(Environment.NewLine, problems.Take(10).Select(p => "• " + p));
            var more = problems.Count > 10 ? $"{Environment.NewLine}…and {problems.Count - 10} more." : "";
            throw new ImportRejectedException($"The archive was rejected because it contains unsafe or invalid entries:{Environment.NewLine}{shown}{more}");
        }
        if (fileCount == 0)
            throw new ImportRejectedException("The archive contains no files.");
        if (skippedMetadata > 0)
            warnings.Add($"Skipped {skippedMetadata} macOS metadata entr{(skippedMetadata == 1 ? "y" : "ies")} (__MACOSX, .DS_Store).");
        return new ArchivePlan(entries, fileCount);
    }

    private static async Task<long> ExtractEntryAsync(ZipArchiveEntry entry, string destination, ImportLimits limits, long totalSoFar, CancellationToken ct)
    {
        // Sizes in ZIP headers are attacker-controlled: enforce limits on bytes actually written.
        await using var input = entry.Open();
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        var buffer = new byte[1 << 16];
        long written = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            written += read;
            if (written > limits.MaxFileBytes)
                throw new ImportRejectedException($"'{entry.FullName}' expands beyond the per-file limit of {limits.MaxFileBytes:N0} bytes.");
            if (totalSoFar + written > limits.MaxTotalBytes)
                throw new ImportRejectedException($"The archive expands beyond the total limit of {limits.MaxTotalBytes:N0} bytes.");
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
        return written;
    }

    public async Task<ImportResult> ImportDirectoryAsync(string directory, ImportLimits limits, IProgress<ImportProgress>? progress, CancellationToken cancellationToken)
    {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!Directory.Exists(directory))
            throw new ImportRejectedException($"The directory '{directory}' does not exist.");
        if (PathGuard.IsWithin(paths.Root, directory) || PathGuard.IsWithin(directory, paths.Root))
            throw new ImportRejectedException("Cannot import a directory that overlaps HCG Executable Maker's own data directory.");
        if (Path.GetPathRoot(directory) == directory + Path.DirectorySeparatorChar || Path.GetPathRoot(directory) == directory)
            throw new ImportRejectedException("Importing an entire drive or file-system root is not allowed.");

        var id = Guid.NewGuid();
        var staging = CreateStaging(id);
        var warnings = new List<string>();
        try
        {
            progress?.Report(new ImportProgress("Scanning directory", 0, null, 0, directory));
            var plan = PlanDirectoryCopy(directory, limits, warnings, cancellationToken);
            var sourceDir = Path.Combine(staging, "source");
            Directory.CreateDirectory(sourceDir);
            long total = 0;
            var processed = 0;
            foreach (var (from, rel) in plan)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dest = PathGuard.CombineWithin(sourceDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                await using (var input = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true))
                await using (var output = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                {
                    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                    total += output.Length;
                }
                if (total > limits.MaxTotalBytes)
                    throw new ImportRejectedException($"The directory exceeds the total size limit of {limits.MaxTotalBytes:N0} bytes.");
                if (!OperatingSystem.IsWindows())
                {
                    var mode = File.GetUnixFileMode(from) & (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                        UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                    File.SetUnixFileMode(dest, mode | UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                processed++;
                progress?.Report(new ImportProgress("Copying", processed, plan.Count, total, rel));
            }

            var workspace = Commit(staging, id);
            var project = new ProjectRecord
            {
                Id = id,
                Name = Path.GetFileName(directory),
                SourceKind = ProjectSourceKind.Directory,
                SourceLocation = directory,
                WorkspacePath = workspace,
                ImportedAt = time.GetUtcNow(),
            };
            return new ImportResult(project, plan.Count, total, warnings);
        }
        catch
        {
            TryDelete(staging);
            throw;
        }
    }

    private static List<(string From, string Relative)> PlanDirectoryCopy(string root, ImportLimits limits, List<string> warnings, CancellationToken ct)
    {
        var files = new List<(string, string)>();
        var excluded = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            foreach (var info in new DirectoryInfo(dir).EnumerateFileSystemInfos())
            {
                var rel = Path.GetRelativePath(root, info.FullName);
                if (info.LinkTarget is not null)
                {
                    var target = info.ResolveLinkTarget(returnFinalTarget: true);
                    if (target is null || !PathGuard.IsWithin(root, target.FullName))
                    {
                        warnings.Add($"Rejected symbolic link that points outside the project: {rel}");
                        continue;
                    }
                    if (target is FileInfo targetFile && targetFile.Exists && info is FileInfo)
                    {
                        AddFile(targetFile.FullName, rel, targetFile.Length);
                        continue;
                    }
                    warnings.Add($"Skipped linked directory: {rel}");
                    continue;
                }
                if (info is DirectoryInfo)
                {
                    if (ExcludedDirectoryNames.Contains(info.Name)) { excluded.Add(info.Name); continue; }
                    stack.Push(info.FullName);
                }
                else if (info is FileInfo f)
                {
                    if (f.Name == ".DS_Store") continue;
                    AddFile(f.FullName, rel, f.Length);
                }
            }
        }
        if (excluded.Count > 0)
            warnings.Add($"Excluded build output and tool folders: {string.Join(", ", excluded)}.");
        if (files.Count == 0)
            throw new ImportRejectedException("The directory contains no importable files.");
        return files;

        void AddFile(string full, string rel, long length)
        {
            if (length > limits.MaxFileBytes)
                throw new ImportRejectedException($"'{rel}' ({length:N0} bytes) exceeds the per-file limit of {limits.MaxFileBytes:N0} bytes.");
            files.Add((full, rel));
            if (files.Count > limits.MaxFileCount)
                throw new ImportRejectedException($"The directory contains more than {limits.MaxFileCount:N0} files.");
        }
    }

    private string CreateStaging(Guid id)
    {
        paths.EnsureCreated();
        var staging = Path.Combine(paths.WorkspacesDirectory, $".staging-{id:N}");
        Directory.CreateDirectory(staging);
        return staging;
    }

    private string Commit(string staging, Guid id)
    {
        var final = Path.Combine(paths.WorkspacesDirectory, id.ToString("N"));
        if (Directory.Exists(final))
            throw new IOException($"Workspace '{final}' already exists; refusing to overwrite.");
        Directory.Move(staging, final);
        return final;
    }

    private static void EnsureNoLinkedAncestor(string root, string destination)
    {
        // Defense in depth: the staging tree is created by us, but verify no directory on the path is a link.
        var dir = new DirectoryInfo(Path.GetDirectoryName(destination)!);
        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        while (dir is not null && dir.FullName.Length > rootFull.Length)
        {
            if (dir.LinkTarget is not null)
                throw new ImportRejectedException($"Refusing to write through a linked directory: '{dir.FullName}'.");
            dir = dir.Parent;
        }
    }

    private static void ApplyUnixMode(string path, int externalAttributes)
    {
        if (OperatingSystem.IsWindows()) return;
        var mode = (externalAttributes >> 16) & 0x1FF;
        var result = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        if ((mode & 0x49) != 0) // any execute bit; setuid/setgid/sticky are never applied
            result |= UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        File.SetUnixFileMode(path, result);
    }

    private static string GuessName(string sourceDir, string fallback)
    {
        var entries = Directory.GetFileSystemEntries(sourceDir);
        if (entries.Length == 1 && Directory.Exists(entries[0]))
            return Path.GetFileName(entries[0]);
        return fallback;
    }

    private void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not remove staging directory {Dir}", dir);
        }
    }
}
