using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HCG.ExecutableMaker.App.Services;
using HCG.ExecutableMaker.Core;

namespace HCG.ExecutableMaker.App.ViewModels;

public sealed partial class ProjectsViewModel(
    IProjectRepository projects,
    ProjectService projectService,
    IDialogService dialogs,
    IPlatformShell shell,
    NavigationService navigation) : PageViewModel
{
    public override string Title => "Projects";
    public override string Subtitle => "Imported projects live in isolated HCG workspaces. Your original files are never modified.";

    public ObservableCollection<ProjectItem> Projects { get; } = [];
    [ObservableProperty] private bool _isEmpty;

    public override async Task OnNavigatedToAsync(object? parameter) => await ReloadAsync();

    private async Task ReloadAsync()
    {
        Projects.Clear();
        foreach (var p in await projects.ListAsync()) Projects.Add(new ProjectItem(p));
        IsEmpty = Projects.Count == 0;
    }

    [RelayCommand] private void ImportZip() => navigation.Navigate("new-build", BuildWizardViewModel.ImportZipRequest);
    [RelayCommand] private void ImportFolder() => navigation.Navigate("new-build", BuildWizardViewModel.ImportFolderRequest);
    [RelayCommand] private void OpenInWizard(ProjectItem item) => navigation.Navigate("new-build", item.Project);

    [RelayCommand]
    private async Task ReinspectAsync(ProjectItem item)
    {
        IsBusy = true;
        try
        {
            await projectService.InspectAsync(item.Project, CancellationToken.None);
            await ReloadAsync();
        }
        catch (Exception ex) { await dialogs.AlertAsync("Inspection failed", ex.Message); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task OpenWorkspaceAsync(ProjectItem item)
    {
        if (!await shell.OpenFolderAsync(item.Project.SourceDirectory))
            await dialogs.AlertAsync("Folder not found", $"The workspace no longer exists:\n{item.Project.WorkspacePath}");
    }

    [RelayCommand]
    private async Task RemoveAsync(ProjectItem item)
    {
        var ok = await dialogs.ConfirmAsync("Remove imported project?",
            $"This deletes HCG's isolated workspace copy of “{item.Name}” and its build history:\n\n{item.Workspace}\n\n" +
            $"Your original {item.SourceKind.ToLowerInvariant()} is NOT touched:\n{item.Source}\n\nBuilt artifacts in your output folders are kept.",
            "Remove workspace", destructive: true);
        if (!ok) return;
        try
        {
            await projectService.RemoveProjectAsync(item.Project);
            await ReloadAsync();
        }
        catch (Exception ex) { await dialogs.AlertAsync("Could not remove project", ex.Message); }
    }
}
