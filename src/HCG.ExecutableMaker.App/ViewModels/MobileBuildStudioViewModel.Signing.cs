using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HCG.ExecutableMaker.BuildEngine.Signing;
using HCG.ExecutableMaker.Domain;
using HCG.Impactor;
using HCG.Impactor.Devices;

namespace HCG.ExecutableMaker.App.ViewModels;

/// <summary>
/// Apple Signing Assistant in Mobile Build Studio: the build destination (Simulator, physical iPhone/iPad Debug,
/// distribution archive, TestFlight, App Store), the device family, a per-build bundle identifier, teams found in the
/// keychain, and — when a build stops for signing — Resolve Signing (Signing Vault for this project) and Retry, without
/// importing the project again.
/// </summary>
public sealed partial class MobileBuildStudioViewModel
{
    private AppleSigningAssistantService? _signingAssistant;

    public ObservableCollection<Choice<AppleDeviceFamily>> DeviceFamilies { get; } =
        [new(AppleDeviceFamily.IPhone, "iPhone"), new(AppleDeviceFamily.IPad, "iPad")];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DestinationText))] private Choice<AppleDeviceFamily>? _selectedDeviceFamily;
    public ObservableCollection<Choice<AppleBuildDestination>> StoreDestinations { get; } =
        [new(AppleBuildDestination.TestFlight, "TestFlight (App Store Connect .ipa, exported locally)"), new(AppleBuildDestination.AppStore, "App Store (App Store Connect .ipa, exported locally)")];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DestinationText))] private Choice<AppleBuildDestination>? _selectedStoreDestination;
    /// <summary>A bundle identifier for this build only (passed as a build setting; the project is not changed).</summary>
    [ObservableProperty] private string _bundleIdentifierOverride = "";
    public ObservableCollection<string> SuggestedTeams { get; } = [];

    public bool ShowDeviceFamily => SelectedOutput?.Option.Output == MobileOutput.IosDeviceApp;
    public bool ShowStoreDestination => SelectedOutput?.Option.Output == MobileOutput.IosIpa && SelectedExportMethod?.Value == IosExportMethod.AppStoreConnect;
    public bool ShowAppleOptions => Mobile?.Framework == MobileFramework.NativeIos && SelectedOutput?.Option.Output.Platform() == TargetPlatform.IOS;
    public bool HasSuggestedTeams => ShowTeam && SuggestedTeams.Count > 0;

    /// <summary>"Physical iPhone (Debug) · Debug" — what this build is for, as the signing assistant will treat it.</summary>
    public string DestinationText => CreateOptions() is { } o && o.Output.Platform() == TargetPlatform.IOS
        ? $"{AppleDestinations.Of(o).Label()} · configuration {o.Variant ?? AppleDestinations.Of(o).DefaultConfiguration()}" +
          (AppleDestinations.Of(o).IsDistribution() && (o.Variant ?? "Release").Contains("debug", StringComparison.OrdinalIgnoreCase)
              ? " — refused: distribution builds must be Release" : "")
        : "";

    private void NotifyDestination()
    {
        OnPropertyChanged(nameof(ShowStoreDestination));
        OnPropertyChanged(nameof(DestinationText));
    }

    /// <summary>Called after the output changes: device family and store destination defaults, and suggested teams.</summary>
    private void RefreshAppleOptions()
    {
        SelectedDeviceFamily ??= DeviceFamilies[0];
        SelectedStoreDestination ??= StoreDestinations[0];
        SuggestedTeams.Clear();
        var development = SelectedOutput?.Option.Output is MobileOutput.IosDeviceApp || SelectedExportMethod?.Value == IosExportMethod.Debugging;
        foreach (var team in (_report?.AppleIdentities ?? [])
                     .Where(i => i.TeamId is not null && i.Kind == (development ? AppleCertificateKind.Development : AppleCertificateKind.AppleDistribution))
                     .Select(i => i.TeamId!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            SuggestedTeams.Add(team);
        OnPropertyChanged(nameof(ShowDeviceFamily));
        OnPropertyChanged(nameof(ShowStoreDestination));
        OnPropertyChanged(nameof(ShowAppleOptions));
        OnPropertyChanged(nameof(HasSuggestedTeams));
        OnPropertyChanged(nameof(DestinationText));
    }

    /// <summary>Uses a team that has a usable identity in the keychain, with Xcode-managed (automatic) signing.</summary>
    [RelayCommand]
    private void UseTeam(string? team)
    {
        if (string.IsNullOrEmpty(team)) return;
        if (SigningModes.FirstOrDefault(m => m.Value == MobileSigningMode.AppleAutomatic) is { } automatic && SelectedSigning?.Value is not MobileSigningMode.AppleManual)
            SelectedSigning = automatic;
        DevelopmentTeam = team;
    }

    // ---- Resolve Signing after a build stopped for signing ----

    public ObservableCollection<string> SigningProblems { get; } = [];
    [ObservableProperty] private bool _hasSigningResolution;

    private void ApplySigningResolution(BuildRecord result)
    {
        SigningProblems.Clear();
        foreach (var m in result.Messages.Where(m => m.Code == AppleSigningAssistant.ResolveCode)) SigningProblems.Add(m.Text);
        HasSigningResolution = result.Status != BuildStatus.Succeeded && SigningProblems.Count > 0;
        if (HasSigningResolution) RefreshAppleOptions();
    }

    /// <summary>Opens Signing &amp; Security with this project selected (team, mode, profile, devices), keeping this build's settings.</summary>
    [RelayCommand]
    private void ResolveSigning()
    {
        if (SelectedProject is { } p) _navigation?.Navigate("security", p.Project.Id);
    }

    /// <summary>Saves a team typed or chosen here in the project's signing profile, so the next build (and the next session) reuses it.</summary>
    private async Task RememberTeamAsync(MobileBuildOptions options)
    {
        if (_signingAssistant is null || SelectedProject is null || options.DevelopmentTeam is not { Length: 10 } team) return;
        try { await _signingAssistant.RememberTeamAsync(SelectedProject.Project, team, CancellationToken.None); }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* remembering is a convenience; the build carries the team itself */ }
    }
    // ---- What the build is: source commit and compilation flags (verified before compiling) ----

    /// <summary>Commit (7+ hex characters) the build must be made from; empty = report only.</summary>
    [ObservableProperty] private string _expectedSourceCommit = "";
    /// <summary>Flags that must be active, e.g. "DEBUG, UNIMATCH_DEVELOPMENT_RUN"; empty = report only.</summary>
    [ObservableProperty] private string _expectedFlags = "";
    public ObservableCollection<string> VerificationLines { get; } = [];
    public bool HasVerification => VerificationLines.Count > 0;

    private void ApplyVerification(BuildRecord result)
    {
        VerificationLines.Clear();
        if (result.ToolchainVersions.TryGetValue("source-commit", out var commit)) VerificationLines.Add($"Source commit: {commit}");
        if (result.ToolchainVersions.TryGetValue("configuration", out var config)) VerificationLines.Add($"Configuration: {config}");
        if (result.ToolchainVersions.TryGetValue("compilation-conditions", out var flags)) VerificationLines.Add($"Compilation flags: {flags}");
        if (result.ToolchainVersions.TryGetValue("development-only-flags", out var dev)) VerificationLines.Add($"Development-only flags of this project (Debug, never Release): {dev}");
        foreach (var m in result.Messages.Where(m => m.Code == HCG.ExecutableMaker.BuildEngine.Mobile.MobileBuildService.VerifyCode)) VerificationLines.Add(m.Text);
        OnPropertyChanged(nameof(HasVerification));
        _builtApp = result.Status == BuildStatus.Succeeded
            ? result.Artifacts.FirstOrDefault(a => a.Format is PackagingFormat.IosDeviceApp)?.Path
              ?? result.Artifacts.FirstOrDefault(a => a.Format == PackagingFormat.IosIpa && result.Settings.Mobile?.ExportMethod == IosExportMethod.Debugging)?.Path
            : null;
        OnPropertyChanged(nameof(CanInstallOnDevice));
        if (CanInstallOnDevice && Devices.Count == 0) _ = RefreshDevicesAsync();
    }

    // ---- Direct installation on an authorized, connected iPhone or iPad ----

    private IDeviceService? _devices;
    private string? _builtApp;
    public ObservableCollection<DeviceChoice> Devices { get; } = [];
    [ObservableProperty] private DeviceChoice? _selectedDevice;
    [ObservableProperty] private string _deviceMessage = "";
    [ObservableProperty] private bool _isInstalling;
    /// <summary>A development-signed device build (or development .ipa) from the last build, and a device service on this Mac.</summary>
    public bool CanInstallOnDevice => _builtApp is not null && _devices?.IsAvailable == true;

    [RelayCommand]
    private async Task RefreshDevicesAsync()
    {
        if (_devices is null) return;
        DeviceMessage = "Looking for connected devices…";
        var listing = await _devices.ListAsync(CancellationToken.None);
        Devices.Clear();
        foreach (var d in listing.Devices.Where(d => d.IsPhysical)) Devices.Add(new DeviceChoice(d));
        SelectedDevice = Devices.FirstOrDefault(d => d.Ready) ?? Devices.FirstOrDefault();
        DeviceMessage = listing.Problem is { } p ? $"{p.Title}: {p.Explanation} {p.Remedy}"
            : Devices.Count == 0 ? "No iPhone or iPad is connected and paired with this Mac. Connect it, unlock it and trust this Mac, then refresh."
            : Devices.Any(d => d.Ready) ? "" : "Connected devices need pairing and Developer Mode (Settings › Privacy & Security › Developer Mode) before an app can be installed.";
    }

    /// <summary>Installs the development build on the chosen device (after confirmation) and launches it.</summary>
    [RelayCommand]
    private async Task InstallOnDeviceAsync()
    {
        if (_devices is null || _builtApp is null || SelectedDevice is not { } choice) return;
        if (!choice.Ready) { DeviceMessage = $"{choice.Device.Name} is not ready: {choice.Status}."; return; }
        if (!await _dialogs.ConfirmAsync("Install on device?",
                $"Install {Path.GetFileName(_builtApp)} on {choice.Device.Name} ({choice.Device.DisplayModel}) and open it?\n\nThis is a development build: it runs only on devices registered in its development profile.", "Install"))
            return;
        IsInstalling = true;
        try
        {
            DeviceMessage = $"Installing on {choice.Device.Name}…";
            var progress = new Progress<InstallProgress>(p => DeviceMessage = $"Installing on {choice.Device.Name}: {p.Message}{(p.Percent is { } pc ? $" ({pc}%)" : "")}");
            var installed = await _devices.InstallAsync(choice.Device, _builtApp, progress, CancellationToken.None);
            if (!installed.Succeeded)
            {
                DeviceMessage = installed.Failure is { } f ? $"Not installed — {f.Title}: {f.Explanation} {f.Remedy}" : "Not installed (devicectl reported a failure; see the log).";
                return;
            }
            DeviceMessage = $"Installed on {choice.Device.Name}.";
            if (_signingAssistant is not null && SelectedProject is { } project && choice.Device.Udid is { } udid)
                await _signingAssistant.RememberDeviceAsync(project.Project, udid, CancellationToken.None);
            if (installed.InstalledBundleIdentifier is { } id && choice.Device.CanLaunch)
            {
                var launched = await _devices.LaunchAsync(choice.Device, id, CancellationToken.None);
                DeviceMessage = launched.Succeeded ? $"Installed and opened {id} on {choice.Device.Name}." : $"Installed on {choice.Device.Name}; opening it failed — {launched.Failure?.Title}: {launched.Failure?.Remedy}";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { DeviceMessage = "Installing failed: " + ex.Message; }
        finally { IsInstalling = false; }
    }
}

public sealed class DeviceChoice(AppleDevice device)
{
    public AppleDevice Device { get; } = device;
    public bool Ready => Device.CanInstall && Device.Pairing != PairingState.Unpaired && Device.DeveloperMode != DeveloperModeState.Disabled;
    public string Status => !Device.CanInstall ? "not reachable" : Device.Pairing == PairingState.Unpaired ? "not paired with this Mac"
        : Device.DeveloperMode == DeveloperModeState.Disabled ? "Developer Mode is off" : $"{Device.Connection}, iOS {Device.OsVersion}";
    public override string ToString() => $"{Device.Name} — {Device.DisplayModel} ({Status})";
}
