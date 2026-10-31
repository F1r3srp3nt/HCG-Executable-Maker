using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.App.Services;

/// <summary>Copies artifacts to a user-chosen folder. Existing files are only replaced after confirmation.</summary>
public sealed class ArtifactExporter(IPlatformShell shell, IDialogService dialogs)
{
    public async Task<bool> ExportAsync(IReadOnlyList<ArtifactRecord> artifacts)
    {
        var existing = artifacts.Where(a => a.Kind == ArtifactKind.Directory ? Directory.Exists(a.Path) : File.Exists(a.Path)).ToList();
        if (existing.Count == 0)
        {
            await dialogs.AlertAsync("Nothing to export", "The selected artifacts no longer exist on disk.");
            return false;
        }
        var target = await shell.PickFolderAsync("Export artifacts to…");
        if (target is null) return false;

        var conflicts = existing.Select(a => Path.Combine(target, a.FileName)).Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        if (conflicts.Count > 0 && !await dialogs.ConfirmAsync("Replace existing items?",
                "These items already exist in the destination and will be replaced:\n\n" + string.Join("\n", conflicts.Select(Path.GetFileName)),
                "Replace", destructive: true))
            return false;

        try
        {
            await Task.Run(() =>
            {
                foreach (var a in existing)
                {
                    var dest = Path.Combine(target, a.FileName);
                    if (Path.GetFullPath(dest) == Path.GetFullPath(a.Path)) continue;
                    if (a.Kind == ArtifactKind.File) File.Copy(a.Path, dest, overwrite: true);
                    else
                    {
                        if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
                        CopyDirectory(a.Path, dest);
                    }
                }
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await dialogs.AlertAsync("Export failed", ex.Message);
            return false;
        }
        await dialogs.AlertAsync("Export complete", $"Exported {existing.Count} artifact(s) to:\n{target}");
        await shell.OpenFolderAsync(target);
        return true;
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(dest, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, File.GetUnixFileMode(file));
        }
    }
}
