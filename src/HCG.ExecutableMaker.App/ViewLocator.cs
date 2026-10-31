using Avalonia.Controls;
using Avalonia.Controls.Templates;
using HCG.ExecutableMaker.App.ViewModels;
using HCG.ExecutableMaker.App.Views.Pages;

namespace HCG.ExecutableMaker.App;

/// <summary>Explicit view-model → view map (no reflection, trimming-safe).</summary>
public sealed class ViewLocator : IDataTemplate
{
    private static readonly Dictionary<Type, Func<Control>> Map = new()
    {
        [typeof(DashboardViewModel)] = () => new DashboardView(),
        [typeof(BuildWizardViewModel)] = () => new BuildWizardView(),
        [typeof(ProjectsViewModel)] = () => new ProjectsView(),
        [typeof(BuildHistoryViewModel)] = () => new BuildHistoryView(),
        [typeof(ArtifactsViewModel)] = () => new ArtifactsView(),
        [typeof(ToolchainsViewModel)] = () => new ToolchainsView(),
        [typeof(SigningViewModel)] = () => new SigningView(),
        [typeof(SettingsViewModel)] = () => new SettingsView(),
    };

    public Control? Build(object? param)
    {
        if (param is null) return null;
        return Map.TryGetValue(param.GetType(), out var factory) ? factory() : new TextBlock { Text = "No view for " + param.GetType().Name };
    }

    public bool Match(object? data) => data is PageViewModel;
}
