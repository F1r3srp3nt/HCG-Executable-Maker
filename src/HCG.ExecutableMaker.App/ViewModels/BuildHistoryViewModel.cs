using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HCG.ExecutableMaker.App.Services;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.App.ViewModels;

public sealed partial class BuildHistoryViewModel(
    IBuildRepository builds,
    IDialogService dialogs,
    IPlatformShell shell,
    NavigationService navigation) : PageViewModel
{
    public override string Title => "Build History";
    public override string Subtitle => "Every build attempt, its configuration, output and verification results.";

    public ObservableCollection<BuildItem> Builds { get; } = [];
    [ObservableProperty] private BuildItem? _selected;
    [ObservableProperty] private bool _isEmpty;

    public bool HasSelection => Selected is not null;
    partial void OnSelectedChanged(BuildItem? value) => OnPropertyChanged(nameof(HasSelection));

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        await ReloadAsync();
        if (parameter is Guid id) Selected = Builds.FirstOrDefault(b => b.Build.Id == id);
        Selected ??= Builds.FirstOrDefault();
    }

    private async Task ReloadAsync()
    {
        var selectedId = Selected?.Build.Id;
        Builds.Clear();
        foreach (var b in await builds.ListAsync()) Builds.Add(new BuildItem(b));
        IsEmpty = Builds.Count == 0;
        Selected = Builds.FirstOrDefault(b => b.Build.Id == selectedId) ?? Builds.FirstOrDefault();
    }

    [RelayCommand]
    private async Task ViewLogAsync(BuildItem item)
    {
        if (item.Build.LogPath is { } log) await dialogs.ShowLogAsync($"Build log — {item.AppName} {item.Rid}", log);
    }

    [RelayCommand] private void Repeat(BuildItem item) => navigation.Navigate("new-build", item.Build);

    [RelayCommand]
    private async Task OpenOutputAsync(BuildItem item)
    {
        var path = item.Build.Artifacts.FirstOrDefault()?.Path ?? item.Build.Settings.OutputDirectory;
        if (!await shell.OpenFolderAsync(path))
            await dialogs.AlertAsync("Folder not found", $"The output location no longer exists:\n{path}");
    }

    [RelayCommand] private async Task CopyPathAsync(ArtifactItem artifact) => await shell.CopyAsync(artifact.Path);

    [RelayCommand]
    private async Task DeleteAsync(BuildItem item)
    {
        if (!await dialogs.ConfirmAsync("Delete build record?",
                $"Removes the history entry for {item.AppName} ({item.Rid}, {item.Started}).\n\nArtifacts on disk are not deleted.", "Delete record", true))
            return;
        await builds.DeleteAsync(item.Build.Id);
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task ClearAllAsync()
    {
        if (Builds.Count == 0) return;
        if (Builds.Any(b => b.Build.Status == BuildStatus.Running))
        {
            await dialogs.AlertAsync("Build in progress", "Wait for the running build to finish before clearing history.");
            return;
        }
        if (!await dialogs.ConfirmAsync("Clear all build history?",
                $"Deletes {Builds.Count} build record(s) from HCG's database. Artifacts on disk and imported projects are kept.", "Clear history", true))
            return;
        await builds.DeleteAllAsync();
        await ReloadAsync();
    }
}
