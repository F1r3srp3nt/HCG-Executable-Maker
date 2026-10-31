using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.App.ViewModels;

public sealed partial class ToolchainsViewModel(IToolchainService toolchains) : PageViewModel
{
    public override string Title => "Toolchains";
    public override string Subtitle => "Development tools detected on this computer. HCG never installs tools automatically.";

    public ObservableCollection<ToolchainGroup> Groups { get; } = [];
    [ObservableProperty] private string _hostName = "";
    [ObservableProperty] private string _hostDetail = "";
    [ObservableProperty] private string _runtime = "";
    [ObservableProperty] private string _diskFree = "—";
    [ObservableProperty] private string _detectedAt = "";

    public override async Task OnNavigatedToAsync(object? parameter) => Apply(await toolchains.GetAsync());

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        try { Apply(await toolchains.GetAsync(refresh: true)); }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        finally { IsBusy = false; }
    }

    private void Apply(ToolchainReport report)
    {
        HostName = report.Host.DisplayName;
        HostDetail = report.Host.OsDescription;
        Runtime = report.Host.FrameworkDescription;
        DiskFree = report.AvailableDiskBytes is { } free ? $"{Format.Bytes(free)} free ({report.DiskPath})" : "Unavailable";
        DetectedAt = "Detected " + Format.When(report.DetectedAt);
        Groups.Clear();
        foreach (var g in report.Toolchains.GroupBy(t => t.Category))
            Groups.Add(new ToolchainGroup(g.Key, g.Select(t => new ToolchainItem(t)).ToList()));
    }
}
