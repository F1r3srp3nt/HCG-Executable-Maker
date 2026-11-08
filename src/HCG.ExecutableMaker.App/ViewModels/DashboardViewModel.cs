using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HCG.ExecutableMaker.App.Services;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.App.ViewModels;

public sealed partial class DashboardViewModel(
    IProjectRepository projects,
    IBuildRepository builds,
    IToolchainService toolchains,
    NavigationService navigation) : PageViewModel
{
    public override string Title => "Command Center";
    public override string Subtitle => "Build engine status, recent work and quick actions.";

    [ObservableProperty] private int _projectCount;
    [ObservableProperty] private int _totalBuilds;
    [ObservableProperty] private int _succeeded;
    [ObservableProperty] private int _failed;
    [ObservableProperty] private string _hostName = HostInfo.Detect().DisplayName;
    [ObservableProperty] private string _hostDetail = HostInfo.Detect().OsDescription;
    [ObservableProperty] private string _diskFree = "—";
    [ObservableProperty] private string _toolchainSummary = "Detecting…";
    [ObservableProperty] private bool _hasProjects;
    [ObservableProperty] private bool _hasBuilds;

    public ObservableCollection<ProjectItem> RecentProjects { get; } = [];
    public ObservableCollection<BuildItem> RecentBuilds { get; } = [];
    public ObservableCollection<ToolchainItem> KeyToolchains { get; } = [];

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        IsBusy = true;
        try
        {
            var stats = await builds.GetStatisticsAsync();
            (ProjectCount, TotalBuilds, Succeeded, Failed) = (stats.ProjectCount, stats.TotalBuilds, stats.Succeeded, stats.Failed);
            Replace(RecentProjects, (await projects.ListAsync()).Take(5).Select(p => new ProjectItem(p)));
            Replace(RecentBuilds, (await builds.ListAsync(limit: 6)).Select(b => new BuildItem(b)));
            HasProjects = RecentProjects.Count > 0;
            HasBuilds = RecentBuilds.Count > 0;
            var report = await toolchains.GetAsync();
            HostName = report.Host.DisplayName;
            HostDetail = report.Host.OsDescription;
            DiskFree = report.AvailableDiskBytes is { } free ? $"{Format.Bytes(free)} free on {report.DiskPath}" : "Disk space unavailable";
            // Core and desktop tools always; optional ecosystems (Android, iOS, Flutter, MAUI) only once something is set up.
            static bool Core(ToolchainInfo t) => t.Category is "Build" or "Source control" or "macOS" or "Swift";
            Replace(KeyToolchains, report.Toolchains.Where(t => t.Status != ToolchainStatus.UnsupportedOnHost && (Core(t) || t.IsAvailable)).Select(t => new ToolchainItem(t)));
            string Area(string name, params string[] ids)
            {
                var rows = ids.Select(report.Find).OfType<ToolchainInfo>().Where(t => t.Status != ToolchainStatus.UnsupportedOnHost).ToList();
                if (rows.Count == 0) return "";
                return rows.All(t => t.IsAvailable) ? $"{name}: ready"
                    : rows.Any(t => t.Status is ToolchainStatus.SetupRequired or ToolchainStatus.InvalidPath or ToolchainStatus.IncompatibleVersion or ToolchainStatus.ValidationFailed) ? $"{name}: setup required"
                    : rows.Any(t => t.IsAvailable) ? $"{name}: partly set up" : $"{name}: not set up (optional)";
            }
            ToolchainSummary = string.Join(" · ", new[]
            {
                Area("Desktop", ToolchainIds.DotNetSdk),
                report.Host.Platform == TargetPlatform.MacOS ? Area("Swift/macOS", ToolchainIds.Swift, ToolchainIds.MacOSSdk) : "",
                Area("Android", ToolchainIds.Jdk, ToolchainIds.AndroidSdk),
                report.Host.Platform == TargetPlatform.MacOS ? Area("iOS", ToolchainIds.Xcode, ToolchainIds.IosSdk) : "",
                Area("Flutter", ToolchainIds.Flutter), Area(".NET MAUI", ToolchainIds.MauiWorkloads),
            }.Where(x => x.Length > 0));
        }
        finally { IsBusy = false; }
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var i in items) target.Add(i);
    }

    [RelayCommand] private void NewBuild() => navigation.Navigate("new-build", BuildWizardViewModel.ResetRequest);
    [RelayCommand] private void OpenProject() => navigation.Navigate("projects");
    [RelayCommand] private void ViewArtifacts() => navigation.Navigate("artifacts");
    [RelayCommand] private void ViewToolchains() => navigation.Navigate("toolchains");
    [RelayCommand] private void ViewHistory() => navigation.Navigate("history");
    [RelayCommand] private void OpenRecentProject(ProjectItem item) => navigation.Navigate("new-build", item.Project);
    [RelayCommand] private void OpenBuild(BuildItem item) => navigation.Navigate("history", item.Build.Id);
}
