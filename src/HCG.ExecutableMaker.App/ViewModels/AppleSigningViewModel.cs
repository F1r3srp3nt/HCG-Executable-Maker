using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HCG.ExecutableMaker.Account.Signing;
using HCG.ExecutableMaker.App.Services;
using HCG.ExecutableMaker.BuildEngine.Signing;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.Signing;

namespace HCG.ExecutableMaker.App.ViewModels;

/// <summary>One certificate row: what it is, whether it can sign on this Mac, and where it is.</summary>
public sealed class CertificateItem(AppleCertificateInfo c, DateTimeOffset now)
{
    public AppleCertificateInfo Info => c;
    public string Name => c.CommonName;
    public string Type => SigningRouteCatalog.Display(c.Kind);
    public string Team => c.TeamId is null ? "Team unknown" : $"Team {c.TeamId}{(c.TeamName is { } n ? $" · {n}" : "")}";
    public string Sha1 => "SHA-1 " + c.Sha1;
    public string Sha256 => c.Sha256.Length > 0 ? "SHA-256 " + c.Sha256 : "SHA-256 not recorded";
    public string Expiry => c.NotAfter == DateTimeOffset.MinValue ? "Expiry unknown" : $"Valid {c.NotBefore:yyyy-MM-dd} → {c.NotAfter:yyyy-MM-dd}";
    public SigningReadiness Readiness => c.Readiness(now);
    public StatusTag ReadinessTag => Readiness switch
    {
        SigningReadiness.Ready => new("Ready on this Mac", "success"),
        SigningReadiness.PrivateKeyMissing => new("Private key missing", "warning"),
        SigningReadiness.Expired => new("Expired", "error"),
        SigningReadiness.NotYetValid => new("Not yet valid", "warning"),
        SigningReadiness.ReportedRevoked => new("Reported revoked", "error"),
        SigningReadiness.TrustProblem => new("Not trusted", "warning"),
        _ => new("Not on this Mac", "neutral"),
    };
    public string KeyText => c.HasPrivateKey switch
    {
        true => "Private key: in this Mac's keychain",
        false => "Private key: not on this Mac — a certificate alone cannot sign",
        _ => "Private key: unknown (account record only)",
    };
    public string RevocationText => c.Revocation == RevocationStatus.ReportedRevoked
        ? $"Revocation: reported by macOS ({c.TrustProblem})"
        : "Revocation: not checked (Apple keeps this status)" + (c.TrustProblem is { } p ? $" · macOS: {p}" : "");
    public string LocationText => SigningAssetText.Location(c.Location, c.RecordedOnDevice);
    public bool IsOnThisMac => c.Location.HasFlag(SigningAssetLocation.ThisMac);
    public bool IsInAccount => c.Location.HasFlag(SigningAssetLocation.HcgAccountMetadata);
    public string Display => $"{c.CommonName} — {c.ShortSha1}… ({ReadinessTag.Text})";
    public override string ToString() => Display;
}

public sealed class ProfileItem(AppleProvisioningProfileInfo p, DateTimeOffset now)
{
    public AppleProvisioningProfileInfo Info => p;
    public string Name => p.Name;
    public string Uuid => "UUID " + p.Uuid;
    public string Expiry => p.Expiration is { } e ? $"Expires {e:yyyy-MM-dd}" : "Expiry unknown";
    public string Platforms => p.Platforms.Count == 0 ? "Platforms unknown" : string.Join(", ", p.Platforms.Select(ApplePlatformNames.Display));
    public string BundleId => $"App ID {p.ApplicationIdentifier ?? "unknown"}{(p.IsWildcard ? " (wildcard)" : "")}";
    public string Team => p.TeamId is null ? "Team unknown" : $"Team {p.TeamId}{(p.TeamName is { } n ? $" · {n}" : "")}";
    public string Distribution => p.DistributionDescription + (p.IsXcodeManaged ? " · Xcode-managed" : "");
    public string Entitlements => p.Entitlements.Count == 0 ? "No entitlements recorded"
        : "Entitlements: " + string.Join("; ", p.Entitlements.Where(e => e.Key is not ("application-identifier" or "com.apple.application-identifier" or "com.apple.developer.team-identifier"))
            .Select(e => $"{e.Key} = {e.Value}"));
    public string Certificates => $"{p.CertificateSha1s.Count} allowed certificate(s)";
    public string Signature => p.Signature switch
    {
        ProfileSignatureStatus.VerifiedApple => "Signature verified (Apple)",
        ProfileSignatureStatus.IntactUntrustedSigner => "Signature intact, signer not trusted as Apple",
        _ => p.Path is null ? "Not on this Mac (account record)" : "Signature not verified",
    };
    public StatusTag StateTag =>
        !p.Location.HasFlag(SigningAssetLocation.ThisMac) || p.Path is null ? new("Not on this Mac", "neutral")
        : p.Signature != ProfileSignatureStatus.VerifiedApple ? new("Unverified", "error")
        : p.IsExpired(now) ? new("Expired", "error")
        : new("Installed", "success");
    public string LocationText => SigningAssetText.Location(p.Location, p.RecordedOnDevice);
    public bool IsOnThisMac => p.Location.HasFlag(SigningAssetLocation.ThisMac) && p.Path is not null;
    public bool IsInAccount => p.Location.HasFlag(SigningAssetLocation.HcgAccountMetadata);
    public string Display => $"{p.Name} — {p.Kind}, {p.ApplicationIdentifier} ({StateTag.Text})";
    public override string ToString() => Display;
}

public static class SigningAssetText
{
    public static string Location(SigningAssetLocation l, string? device)
    {
        var parts = new List<string>();
        if (l.HasFlag(SigningAssetLocation.ThisMac)) parts.Add("On this Mac");
        if (l.HasFlag(SigningAssetLocation.HcgAccountMetadata)) parts.Add("Recorded in HCG account (metadata only)");
        if (l.HasFlag(SigningAssetLocation.AnotherMac)) parts.Add($"On another Mac{(device is { } d ? $" ({d})" : "")}");
        return parts.Count == 0 ? "Unknown" : string.Join(" · ", parts);
    }
}

public sealed class SigningCheckItem(SigningCheck c)
{
    public string Title => c.Target is { } t ? $"{c.Title} — {t}" : c.Title;
    public string Detail => c.Detail;
    public string? Remedy => c.Remedy is { } r && c.Outcome is SigningCheckOutcome.Failed or SigningCheckOutcome.Warning ? "Fix: " + r : null;
    public bool HasRemedy => Remedy is not null;
    public string Scope => c.Scope == SigningCheckScope.Local ? "Checked locally" : "Needs Apple's services — not checked by HCG";
    public StatusTag Status => c.Outcome switch
    {
        SigningCheckOutcome.Passed => new("Passed", "success"),
        SigningCheckOutcome.Warning => new("Warning", "warning"),
        SigningCheckOutcome.Failed => new("Failed", "error"),
        SigningCheckOutcome.NotApplicable => new("Not needed", "neutral"),
        _ => new("Not verified", "info"),
    };
}

/// <summary>
/// HCG Signing Vault: Apple signing identities, provisioning profiles, per-project signing configuration with pre-build
/// validation, and the (optional) HCG account backup status. Embedded in the Signing &amp; Security page.
/// </summary>
public sealed partial class AppleSigningViewModel(
    AppleSigningAssetService assets,
    IProjectSigningStore store,
    IProjectRepository projects,
    IToolchainService toolchains,
    SigningAccountService account,
    ISettingsStore settings,
    IDialogService dialogs,
    IPlatformShell shell,
    TimeProvider time) : ObservableObject
{
    private SigningAssetInventory _inventory = new();
    private string? _deviceId;

    public ObservableCollection<CertificateItem> Certificates { get; } = [];
    public ObservableCollection<ProfileItem> Profiles { get; } = [];
    public ObservableCollection<string> UnreadableProfiles { get; } = [];
    public ObservableCollection<ProjectItem> SigningProjects { get; } = [];
    public ObservableCollection<SigningCheckItem> Checks { get; } = [];
    public ObservableCollection<Choice<SigningPlatform>> Platforms { get; } = [new(SigningPlatform.Ios, "iOS / iPadOS"), new(SigningPlatform.MacOs, "macOS")];
    public ObservableCollection<Choice<ProjectSigningMode>> Modes { get; } =
    [
        new(ProjectSigningMode.XcodeAutomatic, "Xcode-managed automatic signing"),
        new(ProjectSigningMode.Manual, "Manual: choose the certificate and profile"),
    ];
    public ObservableCollection<Choice<SigningRoute>> Routes { get; } = [];
    public ObservableCollection<CertificateItem> IdentityChoices { get; } = [];
    public ObservableCollection<CertificateItem> InstallerChoices { get; } = [];
    public ObservableCollection<ProfileItem> ProfileChoices { get; } = [];

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _keychainStatus;
    [ObservableProperty] private string? _lastResult;
    [ObservableProperty] private bool _lastResultIsError;
    [ObservableProperty] private bool _backUpToAccount;
    [ObservableProperty] private string _accountStatus = "Not signed in with HCG. Signing assets stay on this Mac.";
    [ObservableProperty] private bool _accountAvailable;
    [ObservableProperty] private ProjectItem? _selectedProject;
    [ObservableProperty] private Choice<SigningPlatform>? _selectedPlatform;
    [ObservableProperty] private Choice<ProjectSigningMode>? _selectedMode;
    [ObservableProperty] private Choice<SigningRoute>? _selectedRoute;
    [ObservableProperty] private string? _teamId;
    [ObservableProperty] private CertificateItem? _selectedIdentity;
    [ObservableProperty] private CertificateItem? _selectedInstaller;
    [ObservableProperty] private ProfileItem? _selectedProfile;
    [ObservableProperty] private string? _intendedDevices;
    [ObservableProperty] private string? _configStatus;
    [ObservableProperty] private string? _routeDescription;
    [ObservableProperty] private string? _ascKeyStatus;

    public string VaultStatus => EncryptedSigningVault.UnavailableReason;
    public string AscExplanation => AppStoreConnectKeys.Explanation;
    public string RevokeExplanation => SigningRemovalEffects.RevokeWithApple;
    public bool IsManual => SelectedMode?.Value == ProjectSigningMode.Manual;
    public bool ShowInstaller => IsManual && SelectedRoute?.Value is SigningRoute.MacAppStore or SigningRoute.MacDeveloperId;
    public bool HasChecks => Checks.Count > 0;
    public bool HasUnreadable => UnreadableProfiles.Count > 0;
    public bool KeychainSupported => assets.KeychainSupported;
    public string KeychainHostNote => assets.KeychainSupported ? "" : "Keychain detection and import run on a Mac. On this computer the lists stay empty.";

    public async Task LoadAsync()
    {
        _deviceId ??= await store.DeviceIdAsync();
        SigningProjects.Clear();
        foreach (var p in await projects.ListAsync()) SigningProjects.Add(new ProjectItem(p));
        await RefreshAsync();
        var s = await settings.LoadAsync();
        AscKeyStatus = s.AppStoreConnectKeyId is { } kid
            ? $"Key ID {kid}{(s.AppStoreConnectIssuerId is { } iss ? $", Issuer ID {iss}" : ", no Issuer ID (individual key)")} (Settings › Apple credentials). The .p8 stays where you keep it."
            : "No App Store Connect API key is configured. None is needed to sign locally.";
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            await account.RefreshAsync(CancellationToken.None);
            AccountAvailable = account.State == SigningAccountState.Available;
            AccountStatus = account.StatusText;
            if (!AccountAvailable) BackUpToAccount = false;
            _inventory = await assets.ReadAsync(AccountAvailable ? account.Records : null, _deviceId, CancellationToken.None);
            var now = time.GetUtcNow();
            Certificates.Clear();
            foreach (var c in _inventory.Certificates) Certificates.Add(new CertificateItem(c, now));
            Profiles.Clear();
            foreach (var p in _inventory.Profiles) Profiles.Add(new ProfileItem(p, now));
            UnreadableProfiles.Clear();
            foreach (var (path, problem) in _inventory.UnreadableProfiles) UnreadableProfiles.Add($"{Path.GetFileName(path)}: {problem}");
            OnPropertyChanged(nameof(HasUnreadable));
            KeychainStatus = _inventory.KeychainProblem
                ?? $"{Certificates.Count(c => c.IsOnThisMac)} certificate(s) on this Mac, {Certificates.Count(c => c.Readiness == SigningReadiness.Ready)} ready to sign; " +
                   $"{Profiles.Count(p => p.IsOnThisMac)} profile(s) installed. Read {_inventory.ReadAt:HH:mm:ss}.";
            RebuildChoices();
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ImportP12Async()
    {
        var path = await shell.PickFileAsync("Import a .p12 (certificate + private key)", "Personal Information Exchange", "*.p12", "*.pfx");
        if (path is null) return;
        var password = await dialogs.PromptPasswordAsync("Password for " + Path.GetFileName(path),
            "Enter the password set when this .p12 was exported. The certificate and private key are imported into this Mac's login keychain.");
        var result = await assets.ImportPkcs12Async(await File.ReadAllBytesAsync(path), password, CancellationToken.None);
        await AfterImportAsync(result);
    }

    [RelayCommand]
    private async Task ImportCerAsync()
    {
        var path = await shell.PickFileAsync("Import a certificate (.cer)", "Certificate", "*.cer", "*.crt", "*.pem");
        if (path is null) return;
        await AfterImportAsync(await assets.ImportCertificateAsync(await File.ReadAllBytesAsync(path), CancellationToken.None));
    }

    [RelayCommand]
    private async Task ImportProfileAsync()
    {
        var path = await shell.PickFileAsync("Import a provisioning profile", "Provisioning profile", "*.mobileprovision", "*.provisionprofile");
        if (path is null) return;
        await AfterImportAsync(assets.ImportProfile(path));
    }

    private async Task AfterImportAsync(SigningImportResult result)
    {
        var message = result.Message + (result.Remedy is { } r ? "\n\nNext: " + r : "");
        if (result.Succeeded && BackUpToAccount && AccountAvailable && _deviceId is not null)
        {
            try
            {
                if (result.Certificate is { } c) await account.RecordCertificateAsync(c, _deviceId, Environment.MachineName, CancellationToken.None);
                if (result.Profile is { } p) await account.RecordProfileAsync(p, _deviceId, Environment.MachineName, CancellationToken.None);
                message += "\n\nMetadata (fingerprint, team, type, expiry) was recorded in your HCG account. The private key was not uploaded.";
            }
            catch (HCG.ExecutableMaker.Account.HcgApiException ex) { message += "\n\nThe HCG account record was not made: " + ex.Message; }
        }
        LastResult = message;
        LastResultIsError = !result.Succeeded && result.Status is not (SigningImportStatus.Duplicate or SigningImportStatus.Cancelled);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task DeleteCertificateAsync(CertificateItem item)
    {
        var effect = item.Info.HasPrivateKey == true ? SigningRemovalEffects.DeleteLocalIdentity : SigningRemovalEffects.DeleteLocalCertificate;
        if (!await dialogs.ConfirmAsync($"Delete {item.Name} from this Mac?", effect, "Delete from this Mac", destructive: true)) return;
        var r = await assets.DeleteLocalAsync(item.Info, CancellationToken.None);
        LastResult = r.Succeeded ? $"Deleted {item.Name} from this Mac's keychain." : r.Problem;
        LastResultIsError = !r.Succeeded;
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task RemoveProfileAsync(ProfileItem item)
    {
        if (!await dialogs.ConfirmAsync($"Remove {item.Name} from this Mac?", SigningRemovalEffects.RemoveLocalProfile, "Remove from this Mac", destructive: true)) return;
        var n = assets.RemoveLocalProfile(item.Info.Uuid);
        LastResult = n > 0 ? $"Removed {item.Name} from this Mac." : "The profile file was already gone.";
        LastResultIsError = false;
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task ForgetAccountRecordAsync(object item)
    {
        var (name, sha1, uuid) = item switch
        {
            CertificateItem c => (c.Name, c.Info.Sha1, (string?)null),
            ProfileItem p => (p.Name, (string?)null, p.Info.Uuid),
            _ => ("", null, null),
        };
        var records = account.Records.Where(r => (sha1 is not null && string.Equals(r.Sha1, sha1, StringComparison.OrdinalIgnoreCase))
                                                 || (uuid is not null && string.Equals(r.ProfileUuid, uuid, StringComparison.OrdinalIgnoreCase))).ToList();
        if (records.Count == 0) return;
        if (!await dialogs.ConfirmAsync($"Remove HCG's record of {name}?", SigningRemovalEffects.RemoveAccountRecord, "Remove HCG record", destructive: true)) return;
        try
        {
            foreach (var r in records) await account.ForgetAsync(r.Id, CancellationToken.None);
            LastResult = $"Removed {records.Count} HCG account record(s) of {name}. Nothing on this Mac or at Apple changed.";
            LastResultIsError = false;
        }
        catch (HCG.ExecutableMaker.Account.HcgApiException ex) { LastResult = ex.Message; LastResultIsError = true; }
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task RecordInAccountAsync(object item)
    {
        if (!AccountAvailable || _deviceId is null) return;
        try
        {
            if (item is CertificateItem c) await account.RecordCertificateAsync(c.Info, _deviceId, Environment.MachineName, CancellationToken.None);
            else if (item is ProfileItem p) await account.RecordProfileAsync(p.Info, _deviceId, Environment.MachineName, CancellationToken.None);
            LastResult = "Recorded the public details in your HCG account. Private keys are never uploaded.";
            LastResultIsError = false;
        }
        catch (HCG.ExecutableMaker.Account.HcgApiException ex) { LastResult = ex.Message; LastResultIsError = true; }
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task ExplainRevokeAsync()
    {
        if (await dialogs.ConfirmAsync("Revoking a certificate (Apple)", SigningRemovalEffects.RevokeWithApple, "Open developer.apple.com"))
            await shell.OpenUrlAsync(SigningRemovalEffects.RevokeUrl);
    }

    // ---- Project signing configuration ----

    async partial void OnSelectedProjectChanged(ProjectItem? value)
    {
        Checks.Clear();
        OnPropertyChanged(nameof(HasChecks));
        if (value is null) return;
        var saved = await store.LoadAsync(value.Project.Id.ToString());
        var platform = saved?.Platform ?? (value.Project.Analysis?.Mobile?.Ios is not null ? SigningPlatform.Ios : SigningPlatform.MacOs);
        SelectedPlatform = Platforms.First(p => p.Value == platform);
        SelectedMode = Modes.First(m => m.Value == (saved?.Mode ?? ProjectSigningMode.XcodeAutomatic));
        SelectedRoute = Routes.FirstOrDefault(r => r.Value == (saved?.Route ?? SigningRoute.LocalDevelopment)) ?? Routes.FirstOrDefault();
        TeamId = saved?.TeamId ?? value.Project.Analysis?.Mobile?.Ios?.AppTargets.Select(t => t.DevelopmentTeam).FirstOrDefault(t => t is not null);
        SelectedIdentity = IdentityChoices.FirstOrDefault(i => i.Info.Sha1 == saved?.IdentitySha1);
        SelectedInstaller = InstallerChoices.FirstOrDefault(i => i.Info.Sha1 == saved?.InstallerIdentitySha1);
        SelectedProfile = ProfileChoices.FirstOrDefault(p => p.Info.Uuid == saved?.ProfileUuid);
        IntendedDevices = saved is null ? null : string.Join(", ", saved.IntendedDevices);
        ConfigStatus = saved is null ? "Not configured yet. Settings are saved on this Mac only (references, never keys or passwords)." : $"Saved {saved.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm}.";
    }

    partial void OnSelectedPlatformChanged(Choice<SigningPlatform>? value)
    {
        Routes.Clear();
        if (value is null) return;
        foreach (var r in SigningRouteCatalog.ForPlatform(value.Value)) Routes.Add(new(r.Route, r.Title));
        SelectedRoute = Routes.FirstOrDefault();
        RebuildChoices();
    }

    partial void OnSelectedModeChanged(Choice<ProjectSigningMode>? value)
    {
        OnPropertyChanged(nameof(IsManual));
        OnPropertyChanged(nameof(ShowInstaller));
    }

    partial void OnSelectedRouteChanged(Choice<SigningRoute>? value)
    {
        OnPropertyChanged(nameof(ShowInstaller));
        if (value is not null && SelectedPlatform is { } p)
        {
            var r = SigningRouteCatalog.For(p.Value, value.Value);
            RouteDescription = $"Certificate: {r.IdentityDescription}" +
                (r.Profile == ProfileRequirement.Required ? $"; provisioning profile: {string.Join(" / ", r.ProfileKinds)}" : "; provisioning profile only for restricted entitlements") +
                (r.InstallerKinds.Count > 0 ? $"; installer: {string.Join(" / ", r.InstallerKinds.Select(SigningRouteCatalog.Display))}{(r.InstallerRequired ? "" : " (for .pkg only)")}" : "") +
                $". {r.AfterSigning}";
        }
        RebuildChoices();
    }

    partial void OnTeamIdChanged(string? value) => RebuildChoices();

    private void RebuildChoices()
    {
        if (SelectedPlatform is null || SelectedRoute is null) return;
        var route = SigningRouteCatalog.For(SelectedPlatform.Value, SelectedRoute.Value);
        var now = time.GetUtcNow();
        var keepIdentity = SelectedIdentity?.Info.Sha1;
        var keepInstaller = SelectedInstaller?.Info.Sha1;
        var keepProfile = SelectedProfile?.Info.Uuid;
        bool TeamOk(string? t) => string.IsNullOrWhiteSpace(TeamId) || t is null || t == TeamId;
        IdentityChoices.Clear();
        foreach (var c in _inventory.Certificates.Where(c => route.IdentityKinds.Contains(c.Kind) && TeamOk(c.TeamId))) IdentityChoices.Add(new CertificateItem(c, now));
        InstallerChoices.Clear();
        foreach (var c in _inventory.Certificates.Where(c => route.InstallerKinds.Contains(c.Kind) && TeamOk(c.TeamId))) InstallerChoices.Add(new CertificateItem(c, now));
        ProfileChoices.Clear();
        var platform = SigningRouteCatalog.ProfilePlatform(route.Platform);
        foreach (var p in _inventory.Profiles.Where(p => p.Platforms.Contains(platform) && route.ProfileKinds.Contains(p.Kind) && (string.IsNullOrWhiteSpace(TeamId) || p.TeamIds.Contains(TeamId!))))
            ProfileChoices.Add(new ProfileItem(p, now));
        SelectedIdentity = IdentityChoices.FirstOrDefault(i => i.Info.Sha1 == keepIdentity);
        SelectedInstaller = InstallerChoices.FirstOrDefault(i => i.Info.Sha1 == keepInstaller);
        SelectedProfile = ProfileChoices.FirstOrDefault(p => p.Info.Uuid == keepProfile);
    }

    private ProjectSigningConfiguration? CurrentConfiguration() =>
        SelectedProject is null || SelectedPlatform is null || SelectedMode is null || SelectedRoute is null ? null : new ProjectSigningConfiguration
        {
            ProjectId = SelectedProject.Project.Id.ToString(),
            Platform = SelectedPlatform.Value,
            Mode = SelectedMode.Value,
            Route = SelectedRoute.Value,
            TeamId = string.IsNullOrWhiteSpace(TeamId) ? null : TeamId.Trim().ToUpperInvariant(),
            IdentitySha1 = IsManual ? SelectedIdentity?.Info.Sha1 : null,
            InstallerIdentitySha1 = IsManual ? SelectedInstaller?.Info.Sha1 : null,
            ProfileUuid = IsManual ? SelectedProfile?.Info.Uuid : null,
            IntendedDevices = (IntendedDevices ?? "").Split([',', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            Backup = BackUpToAccount ? SigningBackupChoice.HcgAccountMetadata : SigningBackupChoice.ThisMacOnly,
            UpdatedAt = time.GetUtcNow(),
        };

    [RelayCommand]
    private async Task SaveConfigurationAsync()
    {
        if (CurrentConfiguration() is not { } cfg) { ConfigStatus = "Choose a project, platform, mode and route first."; return; }
        await store.SaveAsync(cfg);
        ConfigStatus = $"Saved on this Mac {cfg.UpdatedAt.ToLocalTime():HH:mm}.";
        if (cfg.Backup == SigningBackupChoice.HcgAccountMetadata && AccountAvailable)
        {
            var key = SelectedProject!.Project.Analysis?.Mobile?.Ios?.AppTargets.Select(t => t.BundleIdentifier).FirstOrDefault(b => b is not null) ?? cfg.ProjectId;
            try
            {
                await account.PutProjectAsync(key, cfg, CancellationToken.None);
                ConfigStatus += " Preferences were also saved to your HCG account (references only).";
            }
            catch (HCG.ExecutableMaker.Account.HcgApiException ex) { ConfigStatus += " The HCG account was not updated: " + ex.Message; }
        }
    }

    [RelayCommand]
    private async Task ValidateAsync()
    {
        if (CurrentConfiguration() is not { } cfg || SelectedProject is null) { ConfigStatus = "Choose a project, platform, mode and route first."; return; }
        IsBusy = true;
        try
        {
            var report = await toolchains.GetAsync();
            var targets = SelectedProject.Project.Analysis?.Mobile?.Ios is { } ios
                ? SigningPrebuildCheck.Targets(ios, SelectedProject.Project.SourceDirectory)
                : [new SigningTargetFacts(SelectedProject.Project.Name, SelectedProject.Project.Analysis?.Mobile?.Ios?.AppTargets.FirstOrDefault()?.BundleIdentifier, false, new Dictionary<string, string>())];
            var xcode = report.Find(ToolchainIds.Xcode);
            var result = SigningValidator.Validate(new SigningValidationInput
            {
                Configuration = cfg with { Targets = targets.Select(t => new TargetSigningConfiguration { TargetName = t.Name, BundleIdentifier = t.BundleIdentifier, IsEmbedded = t.IsEmbedded, Entitlements = t.Entitlements }).ToList() },
                Inventory = _inventory,
                Targets = targets,
                Tools = new SigningToolFacts(OperatingSystem.IsMacOS(), File.Exists("/usr/bin/codesign"), File.Exists("/usr/bin/security"), xcode?.IsAvailable == true, xcode?.Version,
                    File.Exists("/usr/bin/productbuild")),
                ProducesInstallerPackage = cfg.Route == SigningRoute.MacAppStore,
                Now = time.GetUtcNow(),
            });
            Checks.Clear();
            foreach (var c in result.Checks) Checks.Add(new SigningCheckItem(c));
            OnPropertyChanged(nameof(HasChecks));
            var failed = result.Failures.Count();
            ConfigStatus = failed == 0
                ? $"Ready as far as this Mac can tell. {result.RequiringApple.Count()} check(s) need Apple's services and were not made."
                : $"{failed} problem(s) must be fixed before signing.";
        }
        finally { IsBusy = false; }
    }
}
