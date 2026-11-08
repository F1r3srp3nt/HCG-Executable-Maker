using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HCG.ExecutableMaker.BuildEngine;
using HCG.ExecutableMaker.BuildEngine.MacDistribution;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging;
using HCG.ExecutableMaker.Packaging.MacDistribution;

namespace HCG.ExecutableMaker.App.ViewModels;

/// <summary>
/// macOS distribution in the New Build wizard (Package step): ad-hoc vs Developer ID signing, evidence-based
/// entitlements the user reviews and confirms, the installer (.pkg) signature, and Mac App Store preparation.
/// After a build: explicit Notarize, Test install and Validate actions. Upload and submission stay manual.
/// </summary>
public sealed partial class BuildWizardViewModel
{
    private MacSigningInventory? _signing;
    private bool _suspendDistribution;

    /// <summary>The Distribution section applies to macOS targets.</summary>
    // Electron apps are signed by electron-builder as their configuration says (mac.identity); HCG does not re-sign them.
    public bool ShowDistribution => !IsElectronBuild && (SelectedPlatform?.Value ?? TargetPlatform.Windows) == TargetPlatform.MacOS;

    // ---------- Signing mode ----------
    public IReadOnlyList<Choice<MacSigningMode>> SigningModeOptions { get; } =
    [
        new(MacSigningMode.AdHoc, "Ad hoc — runs on this Mac; other Macs show a Gatekeeper warning (no certificate)"),
        new(MacSigningMode.DeveloperId, "Developer ID — direct distribution outside the Mac App Store (then notarize)"),
    ];
    [ObservableProperty] private Choice<MacSigningMode>? _selectedSigningMode;
    [ObservableProperty] private StatusTag _developerIdState = new("—", "neutral");
    [ObservableProperty] private string _developerIdReason = "";
    [ObservableProperty] private bool _developerIdAvailable;
    public bool IsDeveloperId => SelectedSigningMode?.Value == MacSigningMode.DeveloperId;
    public ObservableCollection<IdentityChoice> DeveloperIdIdentities { get; } = [];
    [ObservableProperty] private IdentityChoice? _selectedDeveloperIdIdentity;
    public ObservableCollection<EntitlementChoice> DeveloperIdEntitlements { get; } = [];
    [ObservableProperty] private bool _hasDeveloperIdEntitlements;

    // ---------- Installer ----------
    public ObservableCollection<IdentityChoice> InstallerIdentities { get; } = [];
    [ObservableProperty] private IdentityChoice? _selectedInstallerIdentity;
    public bool ShowPkgOptions => PackagingOptions.Any(o => o.IsSelected && o.IsEnabled && o.Option.Format == PackagingFormat.MacPkg);

    // ---------- Mac App Store ----------
    public ObservableCollection<IdentityChoice> AppStoreIdentities { get; } = [];
    [ObservableProperty] private IdentityChoice? _selectedAppStoreIdentity;
    public ObservableCollection<IdentityChoice> AppStoreInstallerIdentities { get; } = [];
    [ObservableProperty] private IdentityChoice? _selectedAppStoreInstaller;
    public ObservableCollection<ProfileChoice> AppStoreProfiles { get; } = [];
    [ObservableProperty] private ProfileChoice? _selectedAppStoreProfile;
    public ObservableCollection<EntitlementChoice> AppStoreEntitlements { get; } = [];
    public ObservableCollection<string> AppStoreReviews { get; } = [];
    public ObservableCollection<string> AppStoreBlockers { get; } = [];
    [ObservableProperty] private bool _hasAppStoreReviews;
    [ObservableProperty] private bool _hasAppStoreBlockers;
    [ObservableProperty] private StatusTag _appStoreState = new("—", "neutral");
    [ObservableProperty] private string _appStoreReason = "";

    [ObservableProperty] private bool _entitlementsConfirmed;
    [ObservableProperty] private string _signingInventoryNote = "";
    public ObservableCollection<string> DistributionErrors { get; } = [];
    [ObservableProperty] private bool _hasDistributionErrors;

    partial void OnSelectedSigningModeChanged(Choice<MacSigningMode>? value) { OnPropertyChanged(nameof(IsDeveloperId)); DistributionChanged(); }
    partial void OnSelectedDeveloperIdIdentityChanged(IdentityChoice? value) => DistributionChanged();
    partial void OnSelectedInstallerIdentityChanged(IdentityChoice? value) => DistributionChanged();
    partial void OnSelectedAppStoreIdentityChanged(IdentityChoice? value) => DistributionChanged();
    partial void OnSelectedAppStoreInstallerChanged(IdentityChoice? value) => DistributionChanged();
    partial void OnSelectedAppStoreProfileChanged(ProfileChoice? value) => DistributionChanged();
    partial void OnEntitlementsConfirmedChanged(bool value) => DistributionChanged();

    /// <summary>Reads (or re-reads) the keychain identities and profiles. Read-only.</summary>
    [RelayCommand]
    private async Task RefreshSigningAsync()
    {
        if (!HostInfo.Detect().Platform.Equals(TargetPlatform.MacOS))
        {
            _signing = new MacSigningInventory { Problem = "Signing identities are read on a Mac." };
            SigningInventoryNote = "Signing certificates and profiles are read on a Mac. On this computer only the states are shown.";
        }
        else
        {
            try
            {
                _signing = await _signingReader.ReadAsync(CancellationToken.None);
                SigningInventoryNote = _signing.Problem ?? $"{_signing.Identities.Count} signing identit{(_signing.Identities.Count == 1 ? "y" : "ies")} and {_signing.Profiles.Count} macOS provisioning profile(s) found (read-only; private keys stay in the keychain).";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _signing = new MacSigningInventory { Problem = ex.Message };
                SigningInventoryNote = "Could not read signing identities: " + ex.Message;
            }
        }
        PopulateDistributionChoices(CreateDistributionOptions());
    }

    [RelayCommand]
    private async Task BrowseProvisioningProfileAsync()
    {
        if (await _shell.PickFileAsync("Choose a macOS provisioning profile", "Provisioning profiles", "*.provisionprofile") is not { } path) return;
        if (MacSigningInventoryReader.ReadProfile(path) is not { } profile)
        {
            DistributionErrors.Add($"{Path.GetFileName(path)} is not a readable macOS provisioning profile.");
            HasDistributionErrors = true;
            return;
        }
        _signing = (_signing ?? new MacSigningInventory()) with { Profiles = [.. (_signing?.Profiles ?? []).Where(p => p.Path != path), profile] };
        PopulateDistributionChoices(CreateDistributionOptions() with { AppStoreProfilePath = path });
    }

    private IReadOnlyList<EntitlementEvidence> ProductEvidence => SelectedSwiftProduct?.Facts?.EntitlementEvidence ?? [];

    private bool ProductHasResourceBundles =>
        SelectedSwiftPackage?.Evaluated?.Targets.Any(t => t.Kind is SwiftTargetKind.Regular or SwiftTargetKind.Executable && t.ResourceCount > 0) == true
        || SelectedSwiftProduct?.Facts?.HasResourceDirectory == true;

    /// <summary>Fills the pickers and entitlement lists, keeping <paramref name="keep"/>'s choices where they still exist.</summary>
    private void PopulateDistributionChoices(MacDistributionOptions keep)
    {
        _suspendDistribution = true;
        try
        {
            var inv = _signing ?? new MacSigningInventory();
            SelectedSigningMode = SigningModeOptions.FirstOrDefault(o => o.Value == keep.SigningMode) ?? SigningModeOptions[0];
            Fill(DeveloperIdIdentities, inv.OfKind(AppleCertificateKind.DeveloperIdApplication), null);
            SelectedDeveloperIdIdentity = DeveloperIdIdentities.FirstOrDefault(i => i.Sha1 == keep.DeveloperIdApplicationSha1) ?? DeveloperIdIdentities.FirstOrDefault();
            Fill(InstallerIdentities, inv.OfKind(AppleCertificateKind.DeveloperIdInstaller), "Unsigned — local testing only");
            SelectedInstallerIdentity = InstallerIdentities.FirstOrDefault(i => i.Sha1 == keep.PkgInstallerSha1) ?? InstallerIdentities[0];
            Fill(AppStoreIdentities, inv.Identities.Where(i => i.SignsMacAppStoreApps), null);
            SelectedAppStoreIdentity = AppStoreIdentities.FirstOrDefault(i => i.Sha1 == keep.AppStoreApplicationSha1) ?? AppStoreIdentities.FirstOrDefault();
            Fill(AppStoreInstallerIdentities, inv.OfKind(AppleCertificateKind.MacInstallerDistribution), null);
            SelectedAppStoreInstaller = AppStoreInstallerIdentities.FirstOrDefault(i => i.Sha1 == keep.AppStoreInstallerSha1) ?? AppStoreInstallerIdentities.FirstOrDefault();
            AppStoreProfiles.Clear();
            var bundleId = AppId.Trim();
            foreach (var p in inv.Profiles.Where(p => p.Kind == MacProfileKind.MacAppStore || p.Path == keep.AppStoreProfilePath)
                         .OrderByDescending(p => p.Matches(bundleId)).ThenByDescending(p => p.Expiration))
                AppStoreProfiles.Add(new ProfileChoice(p, p.Matches(bundleId), p.IsExpired(DateTimeOffset.UtcNow)));
            SelectedAppStoreProfile = AppStoreProfiles.FirstOrDefault(p => p.Profile.Path == keep.AppStoreProfilePath)
                                      ?? AppStoreProfiles.FirstOrDefault(p => p.MatchesBundle && !p.Expired);

            var evidence = ProductEvidence;
            FillEntitlements(DeveloperIdEntitlements, Entitlements.ForDeveloperId(evidence), keep.SigningMode == MacSigningMode.DeveloperId && keep.EntitlementsConfirmed ? keep.DeveloperIdEntitlements : null);
            HasDeveloperIdEntitlements = DeveloperIdEntitlements.Count > 0;
            var store = Entitlements.ForAppStore(evidence);
            FillEntitlements(AppStoreEntitlements, store, keep.EntitlementsConfirmed && keep.AppStoreApplicationSha1 is not null ? keep.AppStoreEntitlements : null);
            AppStoreReviews.Clear();
            foreach (var r in store.Reviews) AppStoreReviews.Add($"{r.Capability} — {r.Evidence}. {r.Explanation}");
            AppStoreBlockers.Clear();
            foreach (var b in store.Blockers) AppStoreBlockers.Add($"{b.Capability} — {b.Evidence}. {b.Explanation}");
            HasAppStoreReviews = AppStoreReviews.Count > 0;
            HasAppStoreBlockers = AppStoreBlockers.Count > 0;
            EntitlementsConfirmed = keep.EntitlementsConfirmed;
        }
        finally { _suspendDistribution = false; }
        DistributionChanged();
    }

    private static void Fill(ObservableCollection<IdentityChoice> target, IEnumerable<SigningIdentity> identities, string? noneLabel)
    {
        target.Clear();
        if (noneLabel is not null) target.Add(new IdentityChoice(null, noneLabel));
        foreach (var i in identities) target.Add(new IdentityChoice(i, i.Display));
    }

    private void FillEntitlements(ObservableCollection<EntitlementChoice> target, EntitlementPlan plan, IReadOnlyList<string>? confirmed)
    {
        foreach (var old in target) old.PropertyChanged -= OnEntitlementToggled;
        target.Clear();
        foreach (var p in plan.Proposed)
        {
            var item = new EntitlementChoice(p) { IsSelected = confirmed?.Contains(p.Key) ?? true };
            item.PropertyChanged += OnEntitlementToggled;
            target.Add(item);
        }
    }

    private void OnEntitlementToggled(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // A changed entitlement set must be reviewed again.
        if (!_suspendDistribution) EntitlementsConfirmed = false;
        DistributionChanged();
    }

    private MacDistributionOptions CreateDistributionOptions() => new()
    {
        SigningMode = IsSwiftGui ? SelectedSigningMode?.Value ?? MacSigningMode.AdHoc : MacSigningMode.AdHoc,
        DeveloperIdApplicationSha1 = SelectedDeveloperIdIdentity?.Sha1,
        PkgInstallerSha1 = IsSwiftGui ? SelectedInstallerIdentity?.Sha1 : null,
        PkgInstallerName = IsSwiftGui ? SelectedInstallerIdentity?.Identity?.Name : null,
        DeveloperIdEntitlements = DeveloperIdEntitlements.Where(e => e.IsSelected).Select(e => e.Key).ToList(),
        AppStoreApplicationSha1 = SelectedAppStoreIdentity?.Sha1,
        AppStoreInstallerSha1 = SelectedAppStoreInstaller?.Sha1,
        AppStoreInstallerName = SelectedAppStoreInstaller?.Identity?.Name,
        AppStoreProfilePath = SelectedAppStoreProfile?.Profile.Path,
        AppStoreEntitlements = AppStoreEntitlements.Where(e => e.IsSelected).Select(e => e.Key).ToList(),
        EntitlementsConfirmed = EntitlementsConfirmed,
    };

    private PackagingContext DistributionContext(PackagingContext baseContext) => baseContext with
    {
        Signing = _signing,
        Distribution = CreateDistributionOptions(),
        BundleId = AppId.Trim(),
        HasRootResourceBundles = ProductHasResourceBundles,
        Evidence = ProductEvidence,
    };

    /// <summary>Re-evaluates every capability state after a distribution choice changed (selection is kept where still possible).</summary>
    private void DistributionChanged()
    {
        // Also runs while moving from Configure to Package (PreparePackaging), so the states are right on first display.
        if (_suspendDistribution || !ShowDistribution || Step is WizardStep.Build or WizardStep.Results) return;
        var ctx = DistributionContext(BasePackagingContext());
        var (devState, devReason) = MacDistributionCatalog.DeveloperId(ctx);
        DeveloperIdAvailable = devState == PackagingAvailability.Available;
        DeveloperIdState = new StatusTag(Format.Label(devState), devState == PackagingAvailability.Available ? "success" : devState == PackagingAvailability.Failed ? "error" : "warning");
        DeveloperIdReason = devReason ?? "A Developer ID Application certificate is available.";
        var (masState, masReason) = MacDistributionCatalog.AppStore(ctx);
        AppStoreState = new StatusTag(Format.Label(masState), masState == PackagingAvailability.Available ? "success" : masState == PackagingAvailability.Failed ? "error" : "warning");
        AppStoreReason = masReason ?? "All requirements are present.";

        var selected = SelectedFormats().ToHashSet();
        foreach (var old in PackagingOptions) old.PropertyChanged -= OnPackagingOptionChanged;
        PackagingOptions.Clear();
        foreach (var option in PackagingCatalog.Evaluate(ctx))
        {
            var item = new PackagingOptionItem(option) { IsSelected = option.IsAvailable && selected.Contains(option.Format) };
            item.PropertyChanged += OnPackagingOptionChanged;
            PackagingOptions.Add(item);
        }
        ValidateDistribution();
        OnPropertyChanged(nameof(ShowPkgOptions));
        NextCommand.NotifyCanExecuteChanged();
    }

    private void ValidateDistribution()
    {
        DistributionErrors.Clear();
        var d = CreateDistributionOptions();
        var formats = SelectedFormats();
        if (IsSwiftGui && d.SigningMode == MacSigningMode.DeveloperId)
        {
            if (!DeveloperIdAvailable) DistributionErrors.Add("Developer ID signing is not available: " + DeveloperIdReason);
            else if (d.DeveloperIdApplicationSha1 is null) DistributionErrors.Add("Choose the Developer ID Application certificate.");
        }
        var needsReview = (IsSwiftGui && d.SigningMode == MacSigningMode.DeveloperId) || formats.Contains(PackagingFormat.MacAppStore);
        if (needsReview && !d.EntitlementsConfirmed)
            DistributionErrors.Add("Review the entitlements shown and confirm them (\"I reviewed these entitlements\").");
        if (d.PkgInstallerSha1 is not null && d.SigningMode != MacSigningMode.DeveloperId && formats.Contains(PackagingFormat.MacPkg))
            DistributionErrors.Add("A signed installer needs Developer ID signing for the app; choose Developer ID or an unsigned package.");
        HasDistributionErrors = DistributionErrors.Count > 0;
    }

    /// <summary>Disabled functionality from deselected entitlements, shown in the summary (never hidden).</summary>
    private IEnumerable<string> DisabledFeatures()
    {
        foreach (var e in DeveloperIdEntitlements.Where(e => !e.IsSelected && IsDeveloperId)) yield return $"Developer ID: {e.Capability} will not work ({e.Key} not granted)";
        foreach (var e in AppStoreEntitlements.Where(e => !e.IsSelected)) yield return $"Mac App Store: {e.Capability} will not work in the sandbox ({e.Key} not granted)";
    }

    // ───────────── Results: explicit actions ─────────────

    [ObservableProperty] private bool _canNotarize;
    [ObservableProperty] private string _notarizeNote = "";
    [ObservableProperty] private bool _canTestInstall;
    [ObservableProperty] private bool _canValidateAppStore;
    [ObservableProperty] private bool _launchAfterTestInstall;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(NotarizeCommand), nameof(TestInstallCommand), nameof(ValidateAppStoreCommand))]
    private bool _isRunningDistributionAction;
    public ObservableCollection<DistributionStepItem> DistributionResults { get; } = [];
    [ObservableProperty] private bool _hasDistributionResults;

    private bool _keepDistributionResults;

    private async Task PrepareDistributionActionsAsync(BuildRecord build)
    {
        if (!_keepDistributionResults)
        {
            DistributionResults.Clear();
            HasDistributionResults = false;
        }
        _keepDistributionResults = false;
        var s = await _settings.LoadAsync();
        var mac = build.Settings.Platform == TargetPlatform.MacOS && build.Status == BuildStatus.Succeeded;
        var blocker = NotarizationService.Blocker(build, s.NotaryKeychainProfile);
        CanNotarize = mac && blocker is null && NotarizationService.Eligible(build).Count > 0;
        NotarizeNote = !mac ? "" : blocker ?? $"Submits {string.Join(", ", NotarizationService.Eligible(build).Select(a => a.FileName))} with the keychain profile \"{s.NotaryKeychainProfile}\".";
        CanTestInstall = mac && OperatingSystem.IsMacOS() && build.Artifacts.Any(a => a.Format == PackagingFormat.MacPkg);
        CanValidateAppStore = mac && OperatingSystem.IsMacOS() && build.Artifacts.Any(a => a.Format == PackagingFormat.MacAppStore);
    }

    private bool CanRunDistributionAction() => !IsRunningDistributionAction;

    [RelayCommand(CanExecute = nameof(CanRunDistributionAction))]
    private async Task NotarizeAsync()
    {
        if (Result is not { } build) return;
        var s = await _settings.LoadAsync();
        if (!await _dialogs.ConfirmAsync("Notarize with Apple",
                $"HCG uploads {string.Join(", ", NotarizationService.Eligible(build).Select(a => a.FileName))} to Apple's notary service using the keychain profile \"{s.NotaryKeychainProfile}\", waits for the result, then staples the ticket. This sends the app to Apple.",
                "Notarize")) return;
        await RunDistributionActionAsync(async ct =>
            await _notarization.NotarizeAsync(build, NotarizationService.Eligible(build).Select(a => a.Id).ToList(), s.NotaryKeychainProfile!, l => Append(BuildLogKind.Info, l), ct));
        // Refresh the artifacts (new checksums and checks) but keep the step-by-step notarization report on screen.
        _keepDistributionResults = true;
        ShowResult(build);
    }

    [RelayCommand(CanExecute = nameof(CanRunDistributionAction))]
    private async Task TestInstallAsync()
    {
        if (Result is not { } build || build.Artifacts.FirstOrDefault(a => a.Format == PackagingFormat.MacPkg) is not { } pkg) return;
        var app = build.Artifacts.FirstOrDefault(a => a.Format == PackagingFormat.MacAppBundle)?.Path;
        if (app is null) return;
        if (!await _dialogs.ConfirmAsync("Test install",
                $"HCG installs {pkg.FileName} into your ~/Applications folder (current user only, no administrator rights), checks the installed app" +
                (LaunchAfterTestInstall ? ", launches it (this runs the project's code)" : "") + ", and removes it again. An existing app of the same name is never overwritten.",
                "Install and check")) return;
        await RunDistributionActionAsync(async ct => [await _pkgTester.TestInstallAsync(pkg, app, LaunchAfterTestInstall, l => Append(BuildLogKind.Info, l), ct)]);
    }

    [RelayCommand(CanExecute = nameof(CanRunDistributionAction))]
    private async Task ValidateAppStoreAsync()
    {
        if (Result is not { } build || build.Artifacts.FirstOrDefault(a => a.Format == PackagingFormat.MacAppStore) is not { } pkg) return;
        var s = await _settings.LoadAsync();
        if (AppStoreValidationService.Blocker(s.AppStoreConnectKeyId, s.AppStoreConnectIssuerId) is { } b)
        {
            DistributionResults.Clear();
            DistributionResults.Add(new DistributionStepItem(pkg.FileName, new DistributionStep("Validation", VerificationStatus.NotVerifiable, b)));
            HasDistributionResults = true;
            return;
        }
        if (!await _dialogs.ConfirmAsync("Validate with App Store Connect",
                $"HCG sends {pkg.FileName} to App Store Connect for validation only (xcrun altool --validate-app). Nothing is uploaded for review or submitted.", "Validate")) return;
        await RunDistributionActionAsync(async ct => [await _validator.ValidateAsync(pkg, s.AppStoreConnectKeyId!, s.AppStoreConnectIssuerId!, ct)]);
    }

    private async Task RunDistributionActionAsync(Func<CancellationToken, Task<IReadOnlyList<DistributionActionResult>>> action)
    {
        IsRunningDistributionAction = true;
        DistributionResults.Clear();
        try
        {
            foreach (var r in await action(CancellationToken.None))
                foreach (var step in r.Steps) DistributionResults.Add(new DistributionStepItem(r.Artifact, step));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            DistributionResults.Add(new DistributionStepItem("", new DistributionStep("Action", VerificationStatus.Failed, ex.Message)));
        }
        finally
        {
            IsRunningDistributionAction = false;
            HasDistributionResults = DistributionResults.Count > 0;
        }
    }

    private void Append(BuildLogKind kind, string text) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => LogLines.Add(new LogLineItem(new BuildLogLine(DateTimeOffset.Now, kind, text))));
}

public sealed record IdentityChoice(SigningIdentity? Identity, string Label)
{
    public string? Sha1 => Identity?.Sha1;
    public override string ToString() => Label;
}

public sealed record ProfileChoice(MacProvisioningProfile Profile, bool MatchesBundle, bool Expired)
{
    public override string ToString() =>
        $"{Profile.Name ?? Path.GetFileName(Profile.Path)} — {Profile.ApplicationIdentifier}{(MatchesBundle ? "" : " (does not match this bundle id)")}{(Expired ? " (expired)" : $", expires {Profile.Expiration:yyyy-MM-dd}")}";
}

public sealed partial class EntitlementChoice(ProposedEntitlement entitlement) : ObservableObject
{
    public string Key { get; } = entitlement.Key;
    public string Capability { get; } = entitlement.Capability;
    public string Evidence { get; } = entitlement.Evidence;
    public string Explanation { get; } = entitlement.Explanation;
    public string Heading => $"{Capability} — {Key}";
    [ObservableProperty] private bool _isSelected = true;
}

public sealed class DistributionStepItem(string artifact, DistributionStep step)
{
    public string Artifact { get; } = artifact;
    public string Name => step.Name;
    public string Detail => step.Detail;
    public StatusTag Status => new(Format.Label(step.Status), Format.Tone(step.Status));
}
