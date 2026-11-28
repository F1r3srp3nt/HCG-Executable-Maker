using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HCG.ExecutableMaker.App.Services;
using HCG.ExecutableMaker.BuildEngine.MacDistribution;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.Distribution;
using HCG.ExecutableMaker.Packaging.MacDistribution;

namespace HCG.ExecutableMaker.App.ViewModels;

/// <summary>One reported step of Sign an existing app.</summary>
public sealed record ExistingAppStepItem(string Name, StatusTag Status, string Detail);

/// <summary>A project whose saved signing route (Signing &amp; Security › Project signing) can fill in the identities.</summary>
public sealed record SavedRouteChoice(ProjectRecord? Project, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Sign an existing app: an already-built macOS .app (for example a .NET app built and qualified by CI, unsigned) is
/// copied, signed with Developer ID (or ad hoc for a test build), verified, optionally notarized and stapled, zipped,
/// optionally packaged as a signed .pkg, and described by a Release Passport per file, then handed to Ship to Store.
/// The input is never modified. Also: the Signing Doctor (read-only report of any .app) and a Release Passport for any
/// existing archive. Private keys stay in the keychain; notary credentials are a keychain profile name; nothing is published.
/// </summary>
public sealed partial class MacSignExistingViewModel(
    IProcessRunner runner, MacSigningInventoryReader inventoryReader, IAppPaths paths, ISettingsStore settings, IPlatformShell shell,
    IProjectRepository projects, IProjectSigningStore projectSigning, NavigationService navigation) : PageViewModel
{
    public override string Title => "Sign an existing app";
    public override string Subtitle => "Sign, package, notarize, staple and check a macOS .app that is already built, without rebuilding it. The input app is never changed.";

    public bool IsMac => OperatingSystem.IsMacOS();
    public string HostNote => IsMac ? "" : "Signing, notarization and the Signing Doctor run on a Mac (codesign, notarytool, spctl). On this computer the page shows what would be checked.";

    private MacSigningInventory _inventory = new();
    private MacAppFacts? _facts;
    private CancellationTokenSource? _cts;

    // ---------- 1. The app ----------
    [ObservableProperty] private string _appPath = "";
    [ObservableProperty] private string _appSummary = "Choose the built .app.";
    [ObservableProperty] private string _outputDirectory = "";
    /// <summary>Optional SHA256SUMS-style list that must name the input ZIP with its SHA-256 (for a ZIP input).</summary>
    [ObservableProperty] private string _inputChecksums = "";

    // ---------- 2. Signing ----------
    public IReadOnlyList<Choice<bool>> ModeOptions { get; } =
    [
        new(false, "Developer ID — for distribution outside the Mac App Store (then notarize)"),
        new(true, "Ad-hoc TEST build — no certificate, never notarized; Gatekeeper rejects it on other Macs. Do not distribute."),
    ];
    [ObservableProperty] private Choice<bool>? _selectedMode;
    public bool IsTest => SelectedMode?.Value == true;
    public ObservableCollection<IdentityChoice> Identities { get; } = [];
    [ObservableProperty] private IdentityChoice? _selectedIdentity;
    public ObservableCollection<SavedRouteChoice> SavedRoutes { get; } = [];
    [ObservableProperty] private SavedRouteChoice? _selectedSavedRoute;
    [ObservableProperty] private string _savedRouteNote = "";
    [ObservableProperty] private string _inventoryNote = "";

    // ---------- 3. Entitlements ----------
    [ObservableProperty] private string _entitlementsFile = "";
    public ObservableCollection<string> EntitlementKeys { get; } = [];
    [ObservableProperty] private string _entitlementsNote = "No entitlements file chosen: the app is signed without entitlements.";
    [ObservableProperty] private bool _entitlementsConfirmed;

    // ---------- 4. Package, notarization, release ----------
    [ObservableProperty] private bool _makePkg = true;
    [ObservableProperty] private bool _makeDmg;
    public ObservableCollection<IdentityChoice> InstallerIdentities { get; } = [];
    [ObservableProperty] private IdentityChoice? _selectedInstaller;
    [ObservableProperty] private bool _notarize;
    [ObservableProperty] private string _notaryProfile = "";
    [ObservableProperty] private string _notaryNote = "";
    [ObservableProperty] private string _releaseVersion = "";
    [ObservableProperty] private string _buildNumber = "";
    [ObservableProperty] private string _sourceRepository = "";

    // ---------- Plan and result ----------
    public ObservableCollection<string> Problems { get; } = [];
    public ObservableCollection<string> Warnings { get; } = [];
    [ObservableProperty] private bool _hasProblems;
    [ObservableProperty] private bool _hasWarnings;
    [ObservableProperty] private string _planSummary = "";
    public ObservableCollection<ExistingAppStepItem> Steps { get; } = [];
    public ObservableCollection<string> Files { get; } = [];
    [ObservableProperty] private bool _hasFiles;
    /// <summary>Only notarized, stapled, Gatekeeper-accepted Developer ID files; never a test build's.</summary>
    public ObservableCollection<string> ReadyForStore { get; } = [];
    [ObservableProperty] private string _storeNote = "";
    [ObservableProperty] private string _resultSummary = "";
    [ObservableProperty] private string _log = "";
    private string? _signedApp;

    // ---------- Signing Doctor and Release Passport ----------
    [ObservableProperty] private string _doctorApp = "";
    [ObservableProperty] private string _doctorReport = "";
    [ObservableProperty] private string _passportApp = "";
    [ObservableProperty] private string _passportFile = "";
    [ObservableProperty] private string _passportVersion = "";
    [ObservableProperty] private string _passportBuild = "";
    [ObservableProperty] private string _passportSource = "";
    [ObservableProperty] private string _passportResult = "";

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        SelectedMode ??= ModeOptions[0];
        var s = await settings.LoadAsync();
        if (NotaryProfile.Length == 0) NotaryProfile = s.NotaryKeychainProfile ?? "";
        if (OutputDirectory.Length == 0) OutputDirectory = Path.Combine(s.DefaultOutputDirectory ?? paths.DefaultOutputDirectory, "signed-apps");
        if (parameter is string app && app.EndsWith(".app", StringComparison.Ordinal)) AppPath = app;
        SavedRoutes.Clear();
        SavedRoutes.Add(new SavedRouteChoice(null, "None — choose the identities here"));
        foreach (var p in (await projects.ListAsync()).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            SavedRoutes.Add(new SavedRouteChoice(p, p.Name));
        SelectedSavedRoute ??= SavedRoutes[0];
        await RefreshIdentitiesAsync();
    }

    /// <summary>Reads the keychain's signing identities (names and SHA-1 only; private keys never leave the keychain).</summary>
    [RelayCommand]
    private async Task RefreshIdentitiesAsync()
    {
        if (!IsMac) { _inventory = new MacSigningInventory { Problem = "Signing identities are read on a Mac." }; InventoryNote = _inventory.Problem; }
        else
        {
            try
            {
                _inventory = await inventoryReader.ReadAsync(CancellationToken.None);
                InventoryNote = _inventory.Problem ?? $"{_inventory.OfKind(AppleCertificateKind.DeveloperIdApplication).Count()} Developer ID Application and {_inventory.OfKind(AppleCertificateKind.DeveloperIdInstaller).Count()} Developer ID Installer identit(ies) in the keychain.";
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { _inventory = new MacSigningInventory { Problem = ex.Message }; InventoryNote = "Could not read signing identities: " + ex.Message; }
        }
        var keepApp = SelectedIdentity?.Sha1;
        var keepInstaller = SelectedInstaller?.Sha1;
        Identities.Clear();
        foreach (var i in _inventory.OfKind(AppleCertificateKind.DeveloperIdApplication)) Identities.Add(new IdentityChoice(i, i.Display));
        InstallerIdentities.Clear();
        InstallerIdentities.Add(new IdentityChoice(null, "Unsigned — local testing only"));
        foreach (var i in _inventory.OfKind(AppleCertificateKind.DeveloperIdInstaller)) InstallerIdentities.Add(new IdentityChoice(i, i.Display));
        SelectedIdentity = Identities.FirstOrDefault(i => i.Sha1 == keepApp) ?? Identities.FirstOrDefault();
        SelectedInstaller = InstallerIdentities.FirstOrDefault(i => i.Sha1 == keepInstaller) ?? InstallerIdentities.Skip(1).FirstOrDefault() ?? InstallerIdentities[0];
        UpdatePlan();
    }

    partial void OnAppPathChanged(string value)
    {
        _facts = Directory.Exists(value) && value.TrimEnd('/').EndsWith(".app", StringComparison.Ordinal) ? MacAppFacts.Read(value) : null;
        AppSummary = _facts switch
        {
            null when ExistingAppDistribution.IsZipInput(value) && File.Exists(value) =>
                $"{Path.GetFileName(value)}: a ZIP of the app. It is checked (against the checksum list, if chosen) and extracted when signing starts; its app is checked before anything is signed. The ZIP is not modified.",
            null => value.Length == 0 ? "Choose the built .app (or a ZIP that holds it)." : $"{value} is not a macOS .app bundle (or a .zip that holds one).",
            { Problem: { } p } => p,
            var f => $"{f.Label} · {f.BundleIdentifier} · version {f.ShortVersion} (build {f.BuildVersion}) · {string.Join("/", f.Architectures)} · minimum macOS {f.MinimumSystemVersion ?? "not set"} · " +
                     $"{f.MachOFiles.Count} Mach-O file(s){(MacBundleLayout.LooksLikeDotNet(value, f) ? " · .NET app" : "")}",
        };
        if (DoctorApp.Length == 0) DoctorApp = value;
        if (PassportApp.Length == 0) PassportApp = value;
        UpdatePlan();
    }

    partial void OnSelectedModeChanged(Choice<bool>? value) { OnPropertyChanged(nameof(IsTest)); if (IsTest) Notarize = false; UpdatePlan(); }
    partial void OnSelectedIdentityChanged(IdentityChoice? value) => UpdatePlan();
    partial void OnSelectedInstallerChanged(IdentityChoice? value) => UpdatePlan();
    partial void OnMakePkgChanged(bool value) => UpdatePlan();
    partial void OnMakeDmgChanged(bool value) => UpdatePlan();
    partial void OnInputChecksumsChanged(string value) => UpdatePlan();
    partial void OnNotarizeChanged(bool value) => UpdatePlan();
    partial void OnNotaryProfileChanged(string value) => UpdatePlan();
    partial void OnReleaseVersionChanged(string value) => UpdatePlan();
    partial void OnOutputDirectoryChanged(string value) => UpdatePlan();
    partial void OnEntitlementsConfirmedChanged(bool value) => UpdatePlan();

    partial void OnEntitlementsFileChanged(string value)
    {
        EntitlementsConfirmed = false; // a different file must be reviewed again
        EntitlementKeys.Clear();
        var (_, keys, problem) = ExistingAppDistribution.ReadEntitlements(value.Length == 0 ? null : value);
        foreach (var k in keys) EntitlementKeys.Add(k);
        EntitlementsNote = value.Length == 0 ? "No entitlements file chosen: the app is signed without entitlements."
            : problem ?? (keys.Count == 0 ? "The file has no entitlements." : $"{keys.Count} entitlement(s), signed into the app exactly as written in the file:");
        UpdatePlan();
    }

    async partial void OnSelectedSavedRouteChanged(SavedRouteChoice? value)
    {
        SavedRouteNote = "";
        if (value?.Project is not { } project) return;
        try
        {
            var saved = await projectSigning.LoadAsync(project.Id.ToString());
            var (options, note) = SavedMacSigning.Apply(new MacDistributionOptions(), saved, _inventory);
            SavedRouteNote = note ?? $"{project.Name} has no macOS Developer ID route saved (Signing & Security › Project signing).";
            if (options.SigningMode == MacSigningMode.DeveloperId)
            {
                SelectedMode = ModeOptions[0];
                SelectedIdentity = Identities.FirstOrDefault(i => i.Sha1 == options.DeveloperIdApplicationSha1) ?? SelectedIdentity;
                if (options.PkgInstallerSha1 is { } inst) SelectedInstaller = InstallerIdentities.FirstOrDefault(i => i.Sha1 == inst) ?? SelectedInstaller;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { SavedRouteNote = "Project signing could not be read: " + ex.Message; }
    }

    private ExistingAppRequest? BuildRequest() => AppPath.Length == 0 || OutputDirectory.Length == 0 ? null : new ExistingAppRequest
    {
        InputApp = AppPath,
        InputChecksums = InputChecksums.Trim().Length == 0 ? null : InputChecksums.Trim(),
        OutputDirectory = OutputDirectory,
        IdentitySha1 = IsTest ? null : SelectedIdentity?.Sha1,
        AdHocTest = IsTest,
        EntitlementsFile = EntitlementsFile.Length == 0 ? null : EntitlementsFile,
        EntitlementsConfirmed = EntitlementsConfirmed,
        MakePkg = MakePkg,
        MakeDmg = MakeDmg,
        InstallerSha1 = IsTest ? null : SelectedInstaller?.Sha1,
        NotaryKeychainProfile = Notarize && !IsTest ? NotaryProfile.Trim() : null,
        ReleaseVersion = ReleaseVersion.Trim().Length == 0 ? null : ReleaseVersion.Trim(),
        BuildNumber = BuildNumber.Trim().Length == 0 ? null : BuildNumber.Trim(),
        SourceRepository = SourceRepository.Trim().Length == 0 ? null : SourceRepository.Trim(),
    };

    /// <summary>Re-checks everything before anything runs; the Sign button is enabled only without problems.</summary>
    private void UpdatePlan()
    {
        Problems.Clear();
        Warnings.Clear();
        NotaryNote = IsTest ? "Test builds are never notarized."
            : !IsMac ? "Notarization runs on a Mac (xcrun notarytool)."
            : NotaryProfile.Trim().Length == 0 ? "No keychain profile is set up (Settings › Apple credentials). Without one, sign now and notarize later."
            : $"Keychain profile \"{NotaryProfile.Trim()}\" (the password stays in the keychain; HCG never sees it).";
        if (BuildRequest() is not { } request)
        {
            Problems.Add(AppPath.Length == 0 ? "Choose the built .app." : "Choose an output folder.");
        }
        else
        {
            var plan = new ExistingAppDistribution(runner, paths.LogsDirectory).Plan(request, _inventory);
            foreach (var p in plan.Problems) Problems.Add(p);
            foreach (var w in plan.Warnings) Warnings.Add(w);
            PlanSummary = plan.CanRun
                ? $"Ready: {Path.GetFileName(plan.ZipPath)}{(plan.PkgPath is { } pkg ? " and " + Path.GetFileName(pkg) : "")} in {OutputDirectory}" +
                  (request.NotaryKeychainProfile is null ? ", not notarized." : ", notarized and stapled.")
                : "";
        }
        if (Notarize && !IsTest && NotaryProfile.Trim().Length == 0) Problems.Add("Enter the notarization keychain profile name (Settings › Apple credentials), or turn off Notarize.");
        HasProblems = Problems.Count > 0;
        HasWarnings = Warnings.Count > 0;
        SignCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand] private async Task PickAppAsync() { if (await shell.PickApplicationAsync("Choose the built .app") is { } p) AppPath = p; }
    [RelayCommand] private async Task PickZipAsync() { if (await shell.PickFileAsync("A ZIP that holds the built .app", "ZIP archives", "*.zip") is { } p) AppPath = p; }
    [RelayCommand] private async Task PickChecksumsAsync() { if (await shell.PickFileAsync("Checksum list that names the ZIP (SHA256SUMS)", "Checksum lists", "*.txt", "SHA256SUMS*") is { } p) InputChecksums = p; }
    [RelayCommand] private async Task PickOutputAsync() { if (await shell.PickFolderAsync("Output folder for the signed app and its files") is { } p) OutputDirectory = p; }
    [RelayCommand] private async Task PickEntitlementsAsync() { if (await shell.PickFileAsync("Entitlements plist", "Property lists", "*.entitlements", "*.plist") is { } p) EntitlementsFile = p; }
    [RelayCommand] private void ClearEntitlements() => EntitlementsFile = "";
    [RelayCommand] private async Task PickSourceAsync() { if (await shell.PickFolderAsync("The app's source checkout (for the passport's commit)") is { } p) SourceRepository = p; }

    private bool CanSign() => !IsBusy && !HasProblems && IsMac;

    [RelayCommand(CanExecute = nameof(CanSign))]
    private async Task SignAsync()
    {
        if (BuildRequest() is not { } request) return;
        IsBusy = true;
        SignCommand.NotifyCanExecuteChanged();
        Steps.Clear();
        Files.Clear();
        HasFiles = false;
        ReadyForStore.Clear();
        StoreNote = "";
        ShipToStoreCommand.NotifyCanExecuteChanged();
        Log = "";
        ResultSummary = "Working… notarization usually takes a few minutes.";
        _cts = new CancellationTokenSource();
        try
        {
            void Append(string line) => Avalonia.Threading.Dispatcher.UIThread.Post(() => Log += line + "\n");
            var result = await Task.Run(() => new ExistingAppDistribution(runner, paths.LogsDirectory).RunAsync(request, _inventory, Append, _cts.Token));
            foreach (var s in result.Steps) Steps.Add(new ExistingAppStepItem(s.Name, new StatusTag(Format.Label(s.Status), Format.Tone(s.Status)), s.Detail));
            foreach (var f in result.Files.Concat(result.Passports)) Files.Add(f);
            if (result.Report is { } r) Files.Add(r);
            HasFiles = result.Files.Count > 0;
            if (!IsTest) foreach (var f in result.ReadyForStore) ReadyForStore.Add(f);
            StoreNote = result.StoreNote ?? "";
            _signedApp = result.SignedApp;
            ResultSummary = result.Succeeded
                ? (IsTest ? "Test build complete. These files are ad-hoc signed and not notarized: do not distribute them." : "Done. Review the steps, then hand the files to Ship to Store (a private draft; nothing is published).")
                : $"{result.Steps.Count(s => s.Status == VerificationStatus.Failed)} step(s) failed; see each step. The input app was not changed.";
        }
        catch (OperationCanceledException) { ResultSummary = "Cancelled. The input app was not changed."; }
        catch (Exception ex) { ResultSummary = "Failed: " + ex.Message; }
        finally
        {
            IsBusy = false;
            SignCommand.NotifyCanExecuteChanged();
            ShipToStoreCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand] private void Cancel() => _cts?.Cancel();

    [RelayCommand] private async Task OpenOutputAsync() => await shell.OpenFolderAsync(OutputDirectory);

    /// <summary>
    /// Hands the files that are ready (notarized, stapled, Gatekeeper-accepted; never a test build's) to Ship to Store as a
    /// new private draft; their passports are next to them. Publication stays a separate, manual approval in HCG Store.
    /// </summary>
    private bool CanShipToStore() => !IsBusy && !IsTest && ReadyForStore.Count > 0;

    [RelayCommand(CanExecute = nameof(CanShipToStore))]
    private void ShipToStore()
    {
        if (!CanShipToStore()) return;
        navigation.Navigate("ship", ReadyForStore.ToList());
    }

    // ---------- Signing Doctor (read-only) ----------
    [RelayCommand] private async Task PickDoctorAppAsync() { if (await shell.PickApplicationAsync("Choose an .app to inspect") is { } p) DoctorApp = p; }
    [RelayCommand] private void DoctorSignedApp() { if (_signedApp is { } a) { DoctorApp = a; _ = RunDoctorAsync(); } }

    [RelayCommand]
    private async Task RunDoctorAsync()
    {
        if (!IsMac) { DoctorReport = "The Signing Doctor reads code signatures with codesign, spctl and stapler on a Mac."; return; }
        if (!Directory.Exists(DoctorApp) || !DoctorApp.TrimEnd('/').EndsWith(".app", StringComparison.Ordinal)) { DoctorReport = "Choose a macOS .app bundle."; return; }
        DoctorReport = "Reading the signature…";
        try { DoctorReport = MacSigningDoctor.ToText(await Task.Run(() => new MacSigningDoctor(runner).InspectAsync(Path.GetFullPath(DoctorApp), CancellationToken.None))); }
        catch (Exception ex) when (ex is not OperationCanceledException) { DoctorReport = "The Signing Doctor failed: " + ex.Message; }
    }

    // ---------- Release Passport for an existing archive ----------
    [RelayCommand] private async Task PickPassportAppAsync() { if (await shell.PickApplicationAsync("The .app inside the archive") is { } p) PassportApp = p; }
    [RelayCommand] private async Task PickPassportFileAsync() { if (await shell.PickFileAsync("The archive to describe", "App archives", "*.zip", "*.pkg", "*.dmg") is { } p) PassportFile = p; }
    [RelayCommand] private async Task PickPassportSourceAsync() { if (await shell.PickFolderAsync("The app's source checkout") is { } p) PassportSource = p; }

    [RelayCommand]
    private async Task WritePassportAsync()
    {
        if (!IsMac) { PassportResult = "The passport records the code signing as the Signing Doctor reads it, which runs on a Mac."; return; }
        var app = PassportApp.Trim();
        var file = PassportFile.Trim();
        var facts = Directory.Exists(app) ? MacAppFacts.Read(app) : null;
        if (facts is null || !File.Exists(file)) { PassportResult = "Choose the .app and its archive (.zip, .pkg or .dmg)."; return; }
        PassportResult = "Measuring…";
        try
        {
            var doctor = await Task.Run(() => new MacSigningDoctor(runner).InspectAsync(Path.GetFullPath(app), CancellationToken.None));
            var git = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(d => Path.Combine(d, "git")).FirstOrDefault(File.Exists)
                      ?? (File.Exists("/usr/bin/git") ? "/usr/bin/git" : null);
            var source = PassportSource.Trim().Length > 0 && git is not null
                ? await SourceProvenance.ReadAsync(runner, git, Path.GetFullPath(PassportSource.Trim()), CancellationToken.None)
                : SourceProvenance.Unknown;
            var issuer = "HCG Executable Maker " + HostInfo.ProductVersion;
            var r = await MacReleasePassportWriter.CreateAsync(new MacReleasePassportWriter.Input(Path.GetFullPath(app), Path.GetFullPath(file), facts, doctor, source,
                "executable-maker-local", issuer, PassportBuild.Trim().Length == 0 ? null : PassportBuild.Trim(), PassportVersion.Trim().Length == 0 ? null : PassportVersion.Trim(), issuer), CancellationToken.None);
            if (r.Passport is null) { PassportResult = "Not written: " + string.Join("; ", r.Problems); return; }
            var written = await MacReleasePassportWriter.WriteAsync(r.Passport, Path.GetFullPath(file), null, CancellationToken.None);
            PassportResult = $"Written: {written}\nversion {r.Version} · SHA-256 {r.Sha256} · {r.Size:N0} bytes · code signing {doctor.Status} · source {source.Commit ?? "unknown"}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { PassportResult = "Failed: " + ex.Message; }
    }
}
