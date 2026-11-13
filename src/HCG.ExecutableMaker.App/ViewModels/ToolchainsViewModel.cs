using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HCG.ExecutableMaker.App.Services;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Infrastructure.Toolchains;

namespace HCG.ExecutableMaker.App.ViewModels;

public sealed partial class ToolchainsViewModel(IToolchainService toolchains, DeveloperDirectorySelection developerDirectory, IPlatformShell shell, ToolPathSelection toolPaths) : PageViewModel
{
    public override string Title => "Toolchains";
    public override string Subtitle => "Development tools detected on this computer. HCG never installs tools automatically.";

    public ObservableCollection<ToolchainGroup> Groups { get; } = [];
    [ObservableProperty] private string _hostName = "";
    [ObservableProperty] private string _hostDetail = "";
    [ObservableProperty] private string _runtime = "";
    [ObservableProperty] private string _diskFree = "—";
    [ObservableProperty] private string _detectedAt = "";

    // ---------- Xcode ----------
    public ObservableCollection<XcodeInstallItem> XcodeInstallations { get; } = [];
    [ObservableProperty] private bool _hasXcodePanel;
    [ObservableProperty] private StatusTag _xcodeState = new("—", "neutral");
    [ObservableProperty] private string _xcodeDiagnosis = "";
    [ObservableProperty] private string _xcodeAction = "";
    [ObservableProperty] private string _xcodeRawError = "";
    [ObservableProperty] private string _xcodeActivePath = "—";
    [ObservableProperty] private string _xcodeSource = "";
    [ObservableProperty] private string _xcodeVersion = "—";
    [ObservableProperty] private string _xcodeSdks = "—";
    [ObservableProperty] private bool _hcgSelectionActive;
    [ObservableProperty] private string _xcodeMessage = "";
    public bool HasXcodeAction => XcodeAction.Length > 0;
    public bool HasXcodeRawError => XcodeRawError.Length > 0;
    public bool HasXcodeMessage => XcodeMessage.Length > 0;
    partial void OnXcodeActionChanged(string value) => OnPropertyChanged(nameof(HasXcodeAction));
    partial void OnXcodeRawErrorChanged(string value) => OnPropertyChanged(nameof(HasXcodeRawError));
    partial void OnXcodeMessageChanged(string value) => OnPropertyChanged(nameof(HasXcodeMessage));
    private XcodeStatus? _xcode;

    public override async Task OnNavigatedToAsync(object? parameter) => Apply(await toolchains.GetAsync());

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        try { Apply(await toolchains.GetAsync(refresh: true)); }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>Selects an Xcode for HCG only (DEVELOPER_DIR for HCG's processes), then re-detects everything.</summary>
    [RelayCommand]
    private async Task SelectXcodeAsync(XcodeInstallItem? item)
    {
        if (item is null) return;
        await SelectPathAsync(item.Installation.AppPath);
    }

    [RelayCommand]
    private async Task BrowseXcodeAsync()
    {
        var path = await shell.PickApplicationAsync("Choose an Xcode application");
        if (path is not null) await SelectPathAsync(path);
    }

    private async Task SelectPathAsync(string path)
    {
        try
        {
            var x = await developerDirectory.SelectAsync(path);
            XcodeMessage = $"HCG now uses {x.Display}. The system-wide developer directory (xcode-select) was not changed.";
        }
        catch (InvalidOperationException ex) { XcodeMessage = ex.Message; return; }
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task UseSystemSettingAsync()
    {
        await developerDirectory.ClearAsync();
        XcodeMessage = "HCG uses the system setting again (xcode-select" + (string.IsNullOrEmpty(DeveloperDirectorySelection.LaunchValue) ? ")." : ", overridden by the DEVELOPER_DIR HCG was started with).");
        await RefreshAsync();
    }

    /// <summary>Opens the active Xcode (or, when the Command Line Tools are active, the newest installed Xcode).</summary>
    [RelayCommand]
    private async Task OpenXcodeAsync()
    {
        var app = _xcode?.Installed.FirstOrDefault(i => _xcode.ActiveDeveloperDirectory is { } a && i.DeveloperDirectory == a) ?? _xcode?.Installed.FirstOrDefault();
        if (app is null) { XcodeMessage = "No Xcode application was found to open. Install it from the Mac App Store: " + XcodeDiscovery.AppStoreUrl; return; }
        XcodeMessage = await shell.OpenApplicationAsync(app.AppPath)
            ? $"Opened {app.AppPath}. Finish any setup it asks for (license, components), then press Recheck."
            : $"Could not open {app.AppPath}.";
    }

    private void Apply(ToolchainReport report)
    {
        HostName = report.Host.DisplayName;
        HostDetail = report.Host.OsDescription;
        Runtime = report.Host.FrameworkDescription;
        DiskFree = report.AvailableDiskBytes is { } free ? $"{Format.Bytes(free)} free ({report.DiskPath})" : "Unavailable";
        DetectedAt = "Detected " + Format.When(report.DetectedAt);
        Groups.Clear();
        foreach (var g in ToolchainGrouping.Group(report)) Groups.Add(g);
        ApplyXcode(report.Xcode);
        ApplyJava(report);
        ApplyAndroid(report);
    }

    private void ApplyXcode(XcodeStatus? x)
    {
        _xcode = x;
        HasXcodePanel = x is not null;
        XcodeInstallations.Clear();
        if (x is null) return;
        XcodeState = new StatusTag(Label(x.State), x.State switch
        {
            Domain.XcodeState.Ready => "success",
            Domain.XcodeState.NotInstalled or Domain.XcodeState.InstalledNotSelected => "warning",
            _ => "error",
        });
        XcodeDiagnosis = x.Diagnosis;
        XcodeAction = x.CorrectiveAction;
        XcodeRawError = x.RawError ?? "";
        XcodeActivePath = x.ActiveDeveloperDirectory ?? "None";
        XcodeSource = x.Source switch
        {
            DeveloperDirectorySource.HcgSelection => "Selected in HCG — applies to HCG's builds, probes and tests only; the system setting is unchanged",
            DeveloperDirectorySource.LaunchEnvironment => "DEVELOPER_DIR from the environment HCG was started in",
            DeveloperDirectorySource.XcodeSelect => "System setting (xcode-select)",
            _ => "No developer directory",
        };
        HcgSelectionActive = x.Source == DeveloperDirectorySource.HcgSelection;
        XcodeVersion = x.VerifiedVersion is null ? "Not verified" : $"Xcode {x.VerifiedVersion}{(x.VerifiedBuild is null ? "" : $" ({x.VerifiedBuild})")} — verified with xcodebuild -version";
        XcodeSdks = x.Sdks.Count == 0 ? "None reported" : string.Join(", ", x.Sdks);
        foreach (var i in x.Installed)
            XcodeInstallations.Add(new XcodeInstallItem(i, x.ActiveDeveloperDirectory is { } a && i.DeveloperDirectory == a));
    }

    public static string Label(XcodeState s) => s switch
    {
        Domain.XcodeState.Ready => "Ready",
        Domain.XcodeState.NotInstalled => "Xcode not installed",
        Domain.XcodeState.InstalledNotSelected => "Installed, not selected",
        Domain.XcodeState.LicenseNotAccepted => "License not accepted",
        Domain.XcodeState.FirstLaunchRequired => "First-launch setup needed",
        Domain.XcodeState.SdkMissing => "SDKs missing",
        Domain.XcodeState.ValidationFailed => "Validation failed",
        _ => "Not applicable",
    };
}

public sealed class XcodeInstallItem(XcodeInstallation installation, bool isActive)
{
    public XcodeInstallation Installation { get; } = installation;
    public string Title => $"Xcode {Installation.Version ?? "?"}{(Installation.BuildVersion is null ? "" : $" ({Installation.BuildVersion})")}";
    public string Path => Installation.AppPath;
    public bool IsActive { get; } = isActive;
    public bool CanSelect => !IsActive;
}
