using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HCG.ExecutableMaker.BuildEngine.Signing;
using HCG.ExecutableMaker.Domain;

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
}
