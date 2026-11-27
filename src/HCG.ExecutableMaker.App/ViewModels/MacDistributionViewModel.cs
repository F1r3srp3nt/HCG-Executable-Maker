using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HCG.ExecutableMaker.App.Services;
using HCG.ExecutableMaker.BuildEngine.Distribution;
using HCG.ExecutableMaker.BuildEngine.MacDistribution;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.MacDistribution;

namespace HCG.ExecutableMaker.App.ViewModels;

/// <summary>One check or step shown on the page.</summary>
public sealed record MacCheckItem(string Name, VerificationStatus Status, string Detail)
{
    public string Tone => Format.Tone(Status);
    public StatusTag Chip => new(StatusText, Tone);
    public string StatusText => Status switch
    {
        VerificationStatus.Passed => "Passed",
        VerificationStatus.PassedWithWarnings => "Warning",
        VerificationStatus.Failed => "Failed",
        _ => "Not verified",
    };
}

/// <summary>A keychain identity (or none) in a picker.</summary>
public sealed record MacIdentityOption(SigningIdentity? Identity, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Sign &amp; Notarize (macOS): Developer ID distribution of an app that was built elsewhere, without rebuilding it.
/// Inspect → Sign (local) → Notarize, staple and package (sends to Apple) → Release Passports → Ship to Store (a private
/// draft, through the Ship to Store page). The input is never modified; nothing is uploaded to HCG Store from here.
/// </summary>
public sealed partial class MacDistributionViewModel(
    ExistingMacAppDistribution service, IProcessRunner runner, ISettingsStore settings, IPlatformShell shell, IDialogService dialogs,
    NavigationService navigation, IAppPaths paths) : PageViewModel
{
    public override string Title => "Sign & Notarize (macOS)";
    public override string Subtitle => "Developer ID signing, notarization, packaging and Release Passports for a macOS app built elsewhere, without rebuilding it.";

    public bool IsMac { get; } = OperatingSystem.IsMacOS();
    public string HostNote => IsMac ? "" : "Signing, notarization and Gatekeeper checks run on a Mac. On this computer the page can only inspect an app.";

    // 1. Input
    [ObservableProperty] private string _inputPath = "";
    [ObservableProperty] private string _checksumsPath = "";
    [ObservableProperty] private string _origin = "";
    [ObservableProperty] private string _appSummary = "";
    [ObservableProperty] private string _signatureAsReceived = "";
    [ObservableProperty] private string _nestedCode = "";
    [ObservableProperty] private bool _isInspected;
    public ObservableCollection<MacCheckItem> InputChecks { get; } = [];

    // 2. Release
    [ObservableProperty] private string _releaseVersion = "";
    [ObservableProperty] private string _buildNumber = "";
    [ObservableProperty] private string _bundleIdentifier = "";
    public IReadOnlyList<string> Architectures { get; } = ["arm64", "x64", "universal"];
    [ObservableProperty] private string _architecture = "arm64";

    // 3. Identities (the Signing Vault's keychain inventory)
    public ObservableCollection<MacIdentityOption> ApplicationIdentities { get; } = [];
    public ObservableCollection<MacIdentityOption> InstallerIdentities { get; } = [];
    [ObservableProperty] private MacIdentityOption? _selectedApplicationIdentity;
    [ObservableProperty] private MacIdentityOption? _selectedInstallerIdentity;
    [ObservableProperty] private string _identitiesNote = "";

    // 4. Entitlements
    [ObservableProperty] private string _entitlementsText = "";
    [ObservableProperty] private bool _entitlementsConfirmed;
    [ObservableProperty] private string _entitlementsNote = "";

    // 5. Files
    [ObservableProperty] private bool _makeZip = true;
    [ObservableProperty] private bool _makePkg = true;
    [ObservableProperty] private bool _makeDmg;
    [ObservableProperty] private string _outputFolder = "";
    [ObservableProperty] private string _artifactBaseName = "";
    [ObservableProperty] private string _notaryProfile = "";

    // 6. Passports
    [ObservableProperty] private string _sourceRepository = "";
    [ObservableProperty] private string _expectedCommit = "";

    // Progress and results
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanAct))] private bool _isBusy;
    [ObservableProperty] private string _status = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasJob), nameof(IsSigned), nameof(IsReady), nameof(JobSummary))] private BuildRecord? _job;
    public ObservableCollection<MacCheckItem> Results { get; } = [];
    public ObservableCollection<string> Log { get; } = [];
    public bool CanAct => !IsBusy;
    public bool HasJob => Job is not null;
    public bool IsSigned => Job is { Status: BuildStatus.Succeeded };
    public bool IsReady => Job is { } j && ExistingMacAppDistribution.ReadyForDistribution(j);
    public string JobSummary => Job is not { } j ? "" : $"Job {j.Id.ToString()[..8]} · {j.Status} · {j.Settings.OutputDirectory}";
    private CancellationTokenSource? _cts;

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        var s = await settings.LoadAsync();
        NotaryProfile = s.NotaryKeychainProfile ?? "";
        if (OutputFolder.Length == 0) OutputFolder = Path.Combine(paths.DefaultOutputDirectory, "macOS distribution");
        await RefreshIdentitiesAsync();
    }

    // ───────────── Input ─────────────

    [RelayCommand] private async Task BrowseZipAsync()
    {
        if (await shell.PickFileAsync("The app as built (a ZIP holding one .app)", "ZIP archive", "*.zip") is { } f) { InputPath = f; await InspectAsync(); }
    }

    [RelayCommand] private async Task BrowseAppAsync()
    {
        if (await shell.PickApplicationAsync("The app as built (.app)") is { } f) { InputPath = f; await InspectAsync(); }
    }

    [RelayCommand] private async Task BrowseChecksumsAsync()
    {
        if (await shell.PickFileAsync("Published checksums (e.g. SHA256SUMS-signing-input.txt)", "Checksum list", "*.txt", "*.sha256", "SHA256SUMS*") is { } f) { ChecksumsPath = f; if (InputPath.Length > 0) await InspectAsync(); }
    }

    [RelayCommand]
    private async Task InspectAsync()
    {
        if (InputPath.Length == 0) return;
        await RunAsync("Reading the app (nothing is changed)…", async ct =>
        {
            var i = await service.InspectAsync(InputPath, NullIfEmpty(ChecksumsPath), ct);
            InputChecks.Clear();
            foreach (var c in i.Checks) InputChecks.Add(new(c.Name, c.Status, c.Detail));
            var f = i.Facts;
            AppSummary = f.BundleIdentifier is null ? "" : $"{f.Label} · {f.BundleIdentifier} · version {f.ShortVersion} (build {f.BuildVersion}) · {string.Join("/", f.Architectures)} · macOS {f.MinimumSystemVersion ?? "?"}+";
            SignatureAsReceived = i.SignatureAsReceived is { } sig ? "Signature as received: " + sig : "";
            NestedCode = i.NestedCode.Count == 0 ? "No nested code." : $"Nested code signed first ({i.NestedCode.Count}): {string.Join(", ", i.NestedCode)}";
            if (f.BundleIdentifier is not null)
            {
                BundleIdentifier = f.BundleIdentifier;
                if (BuildNumber.Length == 0) BuildNumber = f.BuildVersion ?? "";
                if (ReleaseVersion.Length == 0) ReleaseVersion = f.ShortVersion ?? "";
                Architecture = f.Architectures.Contains("arm64") && f.Architectures.Contains("x86_64") ? "universal" : f.Architectures.Contains("x86_64") ? "x64" : "arm64";
                ArtifactBaseName = ExistingMacAppDistribution.DefaultBaseName(f.Label, ReleaseVersion.Length > 0 ? ReleaseVersion : f.ShortVersion ?? "0", Arch());
            }
            IsInspected = i.Usable;
            Status = i.Usable ? "Read. Check the release, identities and entitlements below, then Sign." : "This input cannot be distributed as it is: see the failed check.";
        });
    }

    partial void OnReleaseVersionChanged(string value)
    {
        if (AppSummary.Length > 0 && value.Length > 0 && BundleIdentifier.Length > 0)
            ArtifactBaseName = ExistingMacAppDistribution.DefaultBaseName(AppSummary.Split(" · ")[0], value, Arch());
    }

    // ───────────── Identities and entitlements ─────────────

    [RelayCommand]
    private async Task RefreshIdentitiesAsync()
    {
        var keep = (SelectedApplicationIdentity?.Identity?.Sha1, SelectedInstallerIdentity?.Identity?.Sha1);
        var inv = await new MacSigningInventoryReader(runner, TimeProvider.System).ReadAsync(CancellationToken.None);
        ApplicationIdentities.Clear();
        foreach (var i in inv.OfKind(AppleCertificateKind.DeveloperIdApplication)) ApplicationIdentities.Add(new(i, $"{i.Name} — {i.Sha1[..8]}…"));
        InstallerIdentities.Clear();
        InstallerIdentities.Add(new(null, "None (no PKG)"));
        foreach (var i in inv.OfKind(AppleCertificateKind.DeveloperIdInstaller)) InstallerIdentities.Add(new(i, $"{i.Name} — {i.Sha1[..8]}…"));
        SelectedApplicationIdentity = ApplicationIdentities.FirstOrDefault(o => o.Identity?.Sha1 == keep.Item1) ?? ApplicationIdentities.FirstOrDefault();
        SelectedInstallerIdentity = InstallerIdentities.FirstOrDefault(o => o.Identity?.Sha1 == keep.Item2) ?? InstallerIdentities.Skip(1).FirstOrDefault() ?? InstallerIdentities[0];
        IdentitiesNote = inv.Problem is { } p ? "Keychain: " + p
            : ApplicationIdentities.Count == 0 ? "No Developer ID Application identity in the login keychain. Import its .p12 in Signing & Security › Apple Signing, then Refresh."
            : InstallerIdentities.Count == 1 ? "No Developer ID Installer identity: a PKG cannot be made. Import its .p12 in Signing & Security › Apple Signing, or leave the PKG out."
            : $"{ApplicationIdentities.Count} Developer ID Application and {InstallerIdentities.Count - 1} Developer ID Installer identities in the login keychain.";
    }

    [RelayCommand] private void OpenSigningVault() => navigation.Navigate("security");
    [RelayCommand] private void OpenSettings() => navigation.Navigate("settings");

    [RelayCommand]
    private async Task LoadEntitlementsFileAsync()
    {
        if (await shell.PickFileAsync("Entitlements of the app (.entitlements or .plist)", "Entitlements", "*.entitlements", "*.plist") is not { } f) return;
        try
        {
            var (keys, problems) = ExistingMacAppDistribution.ReadEntitlementsFile(f);
            EntitlementsText = string.Join(Environment.NewLine, keys);
            EntitlementsConfirmed = false;
            EntitlementsNote = problems.Count > 0 ? "Not loaded as given: " + string.Join(" ", problems) : $"{keys.Count} entitlement(s) from {Path.GetFileName(f)}. Review them, then confirm.";
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException or UnauthorizedAccessException) { EntitlementsNote = "Could not read it: " + ex.Message; }
    }

    partial void OnEntitlementsTextChanged(string value) => EntitlementsConfirmed = false;

    private IReadOnlyList<string> EntitlementKeys() =>
        EntitlementsText.Split(['\n', '\r', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToList();

    [RelayCommand] private async Task BrowseOutputAsync() { if (await shell.PickFolderAsync("Folder for the signed app and its distribution files") is { } f) OutputFolder = f; }
    [RelayCommand] private async Task BrowseSourceAsync() { if (await shell.PickFolderAsync("The app's source checkout (for the passports' commit)") is { } f) SourceRepository = f; }

    // ───────────── 1. Sign ─────────────

    [RelayCommand]
    private async Task SignAsync()
    {
        if (!IsMac) { await dialogs.AlertAsync("Sign", HostNote); return; }
        if (SelectedApplicationIdentity?.Identity is not { } app) { await dialogs.AlertAsync("Sign", "Choose the Developer ID Application identity (Refresh after importing it in Signing & Security)."); return; }
        if (!EntitlementsConfirmed) { await dialogs.AlertAsync("Sign", "Review the entitlements and tick \"I confirm these entitlements\"; nothing is signed with entitlements nobody reviewed."); return; }
        var keys = EntitlementKeys();
        var installer = SelectedInstallerIdentity?.Identity;
        if (MakePkg && installer is null) { await dialogs.AlertAsync("Sign", "A PKG needs the Developer ID Installer identity. Choose it, or untick PKG."); return; }
        var message = $"Executable Maker copies {Path.GetFileName(InputPath)} (the original is not changed) and signs the copy, nested code first, with:\n\n" +
                      $"• {app.Name}\n• hardened runtime and a secure timestamp (Apple's timestamp server is contacted)\n• entitlements: {(keys.Count == 0 ? "none" : string.Join(", ", keys))}\n\n" +
                      $"Release {ReleaseVersion} (build {BuildNumber}), {Architecture}. Output: {OutputFolder}.\nNothing is sent to Apple's notary service in this step.";
        if (!await dialogs.ConfirmAsync("Sign with Developer ID", message, "Sign")) return;
        Results.Clear();
        await RunAsync("Signing…", async ct =>
        {
            var request = new ExistingMacAppRequest
            {
                Input = InputPath, ChecksumsFile = NullIfEmpty(ChecksumsPath), Origin = NullIfEmpty(Origin), ReleaseVersion = ReleaseVersion.Trim(),
                ExpectedBuildNumber = NullIfEmpty(BuildNumber), ExpectedBundleIdentifier = NullIfEmpty(BundleIdentifier), Architecture = Arch(),
                DeveloperIdApplicationSha1 = app.Sha1, InstallerSha1 = MakePkg ? installer?.Sha1 : null, Entitlements = keys, EntitlementsConfirmed = true,
                Zip = MakeZip, Pkg = MakePkg, Dmg = MakeDmg, OutputDirectory = OutputFolder, ArtifactBaseName = NullIfEmpty(ArtifactBaseName),
            };
            Job = await service.SignAsync(request, AppendLog, ct);
            ShowJob();
            Status = Job.Status == BuildStatus.Succeeded
                ? "Signed and verified. Next: Notarize, staple and package (sends the app to Apple)."
                : "Not signed for distribution: " + Job.FailureReason;
        });
    }

    // ───────────── 2. Notarize, staple, package ─────────────

    [RelayCommand]
    private async Task NotarizeAsync()
    {
        if (Job is not { } job) return;
        var s = await settings.LoadAsync();
        NotaryProfile = s.NotaryKeychainProfile ?? "";
        if (ExistingMacAppDistribution.NotarizeBlocker(job, NullIfEmpty(NotaryProfile)) is { } blocker) { await dialogs.AlertAsync("Notarize", blocker); return; }
        var formats = Tv(job, "formats") ?? "";
        if (!await dialogs.ConfirmAsync("Send to Apple's notary service",
                $"Executable Maker uploads {job.Settings.Identity.ApplicationName}.app to Apple's notary service with the keychain profile \"{NotaryProfile}\" and waits for the result " +
                $"(usually a few minutes), then staples the ticket.\n\nFrom the stapled app it makes: {formats.ToUpperInvariant().Replace(",", ", ")}. The PKG and DMG are uploaded to Apple too, then stapled. " +
                "Gatekeeper then assesses every file.\n\nThis sends the app to Apple. Nothing is uploaded to HCG Store.", "Notarize")) return;
        await RunAsync("Waiting for Apple's notary service…", async ct =>
        {
            var results = await service.NotarizeAndPackageAsync(job, NotaryProfile, AppendLog, ct);
            Results.Clear();
            foreach (var r in results) foreach (var st in r.Steps) Results.Add(new($"{r.Artifact} · {st.Name}", st.Status, st.Detail));
            Job = job;
            ShowJob();
            Status = ExistingMacAppDistribution.ReadyForDistribution(job)
                ? "Notarized, stapled and accepted by Gatekeeper. Next: Release Passports, then Ship to Store."
                : "Not ready for distribution: see the failed step. The notary log, when Apple rejected a file, is saved and named in the step.";
        });
    }

    // ───────────── 3. Passports and Ship to Store ─────────────

    [RelayCommand]
    private async Task WritePassportsAsync()
    {
        if (Job is not { } job) return;
        if (!ExistingMacAppDistribution.ReadyForDistribution(job))
        {
            await dialogs.AlertAsync("Release Passports", "Notarize first: passports describe the final, notarized files.");
            return;
        }
        await RunAsync("Writing Release Passports…", async ct =>
        {
            var written = await service.WritePassportsAsync(job, NullIfEmpty(SourceRepository), NullIfEmpty(ExpectedCommit), ct);
            foreach (var (file, passport, problems) in written)
                Results.Add(passport is not null ? new($"{Path.GetFileName(file)} · Release Passport", VerificationStatus.Passed, passport)
                                                 : new($"{Path.GetFileName(file)} · Release Passport", VerificationStatus.Failed, string.Join("; ", problems)));
            Status = written.All(w => w.Passport is not null) ? $"Passports written (source {Tv(job, "source-commit")}). Next: Ship to Store." : "Some passports were not written: see Results.";
        });
    }

    [RelayCommand]
    private async Task ShipToStoreAsync()
    {
        if (Job is not { } job) return;
        if (!ExistingMacAppDistribution.ReadyForDistribution(job)) { await dialogs.AlertAsync("Ship to Store", "Only notarized files that Gatekeeper accepts are offered. Notarize first."); return; }
        if (ShipFromBuild.From(job) is { Files.Count: > 0 } ship) navigation.Navigate("ship", ship);
    }

    [RelayCommand] private async Task OpenOutputAsync() { if (Job is { } j) await shell.OpenFolderAsync(j.Settings.OutputDirectory); }
    [RelayCommand] private void Cancel() => _cts?.Cancel();

    // ───────────── Helpers ─────────────

    private void ShowJob()
    {
        if (Job is not { } job) return;
        foreach (var a in job.Artifacts)
            foreach (var c in a.Checks)
                if (!Results.Any(r => r.Name == $"{a.FileName} · {c.Name}" && r.Detail == c.Detail)) Results.Add(new($"{a.FileName} · {c.Name}", c.Status, c.Detail));
        if (job.FailureReason is { } reason && job.Artifacts.Count == 0) Results.Add(new("Stopped", VerificationStatus.Failed, reason));
        OnPropertyChanged(nameof(IsSigned));
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(JobSummary));
    }

    private async Task RunAsync(string status, Func<CancellationToken, Task> work)
    {
        IsBusy = true;
        Status = status;
        _cts = new CancellationTokenSource();
        try { await work(_cts.Token); }
        catch (OperationCanceledException) { Status = "Cancelled."; }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Status = ex.Message;
            await dialogs.AlertAsync(Title, ex.Message);
        }
        finally
        {
            IsBusy = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    private void AppendLog(string line) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        Log.Add(line);
        while (Log.Count > 400) Log.RemoveAt(0);
    });

    private TargetArchitecture Arch() => Architecture switch { "x64" => TargetArchitecture.X64, "universal" => TargetArchitecture.Universal, _ => TargetArchitecture.Arm64 };
    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static string? Tv(BuildRecord b, string key) => b.ToolchainVersions.TryGetValue(key, out var v) ? v : null;
}
