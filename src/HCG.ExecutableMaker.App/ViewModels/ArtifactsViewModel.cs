using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HCG.ExecutableMaker.App.Services;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.App.ViewModels;

public sealed partial class ArtifactsViewModel(
    IBuildRepository builds,
    IDialogService dialogs,
    IPlatformShell shell,
    ArtifactExporter exporter) : PageViewModel
{
    public override string Title => "Artifacts";
    public override string Subtitle => "Generated applications and packages, with SHA-256 checksums and verification results.";

    public ObservableCollection<ArtifactItem> Artifacts { get; } = [];
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private string _totalSize = "—";

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        Artifacts.Clear();
        var all = await builds.ListBuildsWithArtifactsAsync();
        foreach (var (artifact, build) in all) Artifacts.Add(new ArtifactItem(artifact, build));
        IsEmpty = Artifacts.Count == 0;
        TotalSize = Format.Bytes(Artifacts.Where(a => a.Exists).Sum(a => a.Artifact.SizeBytes));
    }

    [RelayCommand]
    private async Task OpenFolderAsync(ArtifactItem item)
    {
        if (!await shell.OpenFolderAsync(item.Path))
            await dialogs.AlertAsync("Not found", $"This artifact no longer exists on disk:\n{item.Path}");
    }

    [RelayCommand] private async Task CopyPathAsync(ArtifactItem item) => await shell.CopyAsync(item.Path);
    [RelayCommand] private async Task CopyChecksumAsync(ArtifactItem item) => await shell.CopyAsync($"{item.Sha256}  {item.Name}");
    [RelayCommand] private async Task ExportAsync(ArtifactItem item) => await exporter.ExportAsync([item.Artifact]);
}

public static class BuildRepositoryExtensions
{
    public static async Task<IReadOnlyList<(ArtifactRecord Artifact, BuildRecord Build)>> ListBuildsWithArtifactsAsync(this IBuildRepository repo)
    {
        var list = await repo.ListAsync();
        return list.SelectMany(b => b.Artifacts.Select(a => (a, b))).OrderByDescending(x => x.a.CreatedAt).ToList();
    }
}
