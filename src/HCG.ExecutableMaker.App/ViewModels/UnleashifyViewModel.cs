using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HCG.ExecutableMaker.App.Services;
using HCG.ExecutableMaker.BuildEngine;
using HCG.ExecutableMaker.BuildEngine.Mobile;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.Jailbreak;
using HCG.ExecutableMaker.Security;

namespace HCG.ExecutableMaker.App.ViewModels;

public sealed partial class StageItem(string name) : ObservableObject
{
    public string Name { get; } = name;
    [ObservableProperty] private StatusTag _tag = new("Waiting", "neutral");
}

/// <summary>What Mobile Build Studio hands to Unleashify: the imported project, its scheme, and a device build if one exists.</summary>
public sealed record UnleashifyRequest(Guid? ProjectId, string? Scheme, string? DeviceBuild, bool UseDeviceBuild = false);

public sealed partial class ModeItem(UnleashifyModeOption option, Action<ModeItem> selected) : ObservableObject
{
    public UnleashifyModeOption Option { get; } = option;
    public double Opacity => Option.Available ? 1.0 : 0.6;
    partial void OnIsSelectedChanged(bool value) { if (value) selected(this); }
    public string Label => Option.Label;
    public string Description => Option.Description;
    public bool Available => Option.Available;
    public string? Reason => Option.Reason;
    public StatusTag Tag => Option.Available ? new("Available", "success") : new("Not available", "neutral");
    [ObservableProperty] private bool _isSelected;
}

public sealed record DeviceItem(string Name, string Ios, bool Compatible, string Detail)
{
    public StatusTag Tag => Compatible ? new("Compatible", "success") : new("Not compatible", "error");
}

/// <summary>
/// Unleashify (Mobile Build Studio): the iOS app an imported project builds, packaged unchanged as a rootless jailbreak
/// app. Input: an unsigned device archive built here from the Xcode project (scheme chosen), or an existing device build
/// (.app, .xcarchive or .ipa; Simulator builds are refused). Build = archive (when needed) → inspect → package → validate
/// with <see cref="RootlessAppWorkflow"/>; Export Candidate Handoff writes HCG Repo's files. The panel shows the source
/// revision (and when the imported copy is stale), the installed-build check and each test device's compatibility.
/// Nothing is installed, uploaded or published.
/// </summary>
public sealed partial class UnleashifyViewModel(
    IProcessRunner runner,
    IToolchainDetector toolchains,
    IPlatformShell shell,
    IProjectRepository projects,
    MobileBuildService mobileBuilder,
    IAppPaths paths,
    ProjectService projectService,
    IDialogService dialogs) : PageViewModel
{
    public override string Title => "Unleashify";
    public override string Subtitle => "Mobile Build Studio · an iOS app as a rootless jailbreak package for HCG Repo Candidate — built, validated and handed off, never published.";

    public static IReadOnlyList<string> AccessLevels { get; } = ["public", "connect", "paid", "undecided"];

    // Project and revision.
    [ObservableProperty] private ProjectRecord? _project;
    [ObservableProperty] private string _projectSummary = "No project. Import the app in Mobile Build Studio, then open Unleashify from there — or choose an existing device build below.";
    [ObservableProperty] private string _revisionText = "—";
    [ObservableProperty] private StatusTag _revisionTag = new("No project", "neutral");
    public bool RevisionIsStale => _revision?.State == RevisionState.Stale;
    private RevisionStatus? _revision;

    // Modes.
    public ObservableCollection<ModeItem> Modes { get; } = [];
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(BuildCommand))] private ModeItem? _selectedMode;

    // Input.
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(BuildCommand))] private bool _buildFromXcode = true;
    public bool UseDeviceBuild { get => !BuildFromXcode; set => BuildFromXcode = !value; }
    public ObservableCollection<string> Schemes { get; } = [];
    [ObservableProperty] private string? _selectedScheme;
    [ObservableProperty] private string _schemeDetail = "";
    [ObservableProperty] private string _deviceBuild = "";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(BuildCommand))] private bool _trustAcknowledged;
    public bool IsMacHost => OperatingSystem.IsMacOS();
    public string TrustTitle => TrustPolicy.TrustBoundaryTitle;

    // Package.
    [ObservableProperty] private string _packageId = "";
    [ObservableProperty] private string _packageVersion = "";
    [ObservableProperty] private string _appName = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _deploymentTarget = "—";
    [ObservableProperty] private string _bundleId = "—";
    [ObservableProperty] private string _outputDir = "";
    [ObservableProperty] private string _productionAccess = "undecided";
    [ObservableProperty] private string _repository = "";
    [ObservableProperty] private string _commit = "";
    [ObservableProperty] private string _ciRun = "";
    [ObservableProperty] private string _ciArtifact = "";
    [ObservableProperty] private string _ciRepository = "";
    [ObservableProperty] private string _ciCommit = "";
    [ObservableProperty] private string _changes = "";
    [ObservableProperty] private string _knownIssues = "";
    [ObservableProperty] private string _ownerApproval = "";
    /// <summary>The exact .deb SHA-256 the owner approved. An approval of a local build counts only for these bytes.</summary>
    [ObservableProperty] private string _approvedSha256 = "";

    // Local build (no CI run): provenance measured here; the source can stay private.
    public static IReadOnlyList<string> Visibilities { get; } = ["private", "public"];
    [ObservableProperty] private bool _isLocalBuild = true;
    [ObservableProperty] private string _sourceVisibility = "private";
    [ObservableProperty] private string _hostLabel = LocalBuild.DefaultHostLabel();
    [ObservableProperty] private string _toolchainText = "Read from the app after the build (DTXcode, DTXcodeBuild, DTSDKName).";

    // Pinned source: one exact commit of a trusted local repository, checked out into a new folder and imported.
    [ObservableProperty] private string _pinRepository = "";
    [ObservableProperty] private string _pinCommit = "";
    [ObservableProperty] private string _pinStatus = "";

    // Icon: the app's own icon (Apple CgBI converted to standard PNG); approved artwork is the fallback, chosen by the owner.
    [ObservableProperty] private string _iconArtwork = "";
    [ObservableProperty] private string? _suggestedArtwork;
    [ObservableProperty] private string _iconStatus = "The handoff's icon.png is the app's own icon, converted to a standard PNG when Xcode stored it as Apple CgBI.";

    // Ship to Repo: HCG Repo's Candidate Release Manager (Distribution v2) on the owner's checkout. Never publishes.
    [ObservableProperty] private string _incomingStore = "";
    [ObservableProperty] private string _approvedBy = "";
    [ObservableProperty] private bool _createPackageMetadata;
    /// <summary>Prepare through HCG Repo's local-file route (staged for its Prepare packages workflow in CI) instead of importing here.</summary>
    [ObservableProperty] private bool _prepareViaLocalFile;
    [ObservableProperty] private bool _needsPackageMetadata;
    [ObservableProperty] private string? _submissionId;
    [ObservableProperty] private StatusTag _shipTag = new("Not submitted", "neutral");
    [ObservableProperty] private string _shipText = "";

    // HCG Repo import preview (HCG Repo's own importer, dry run, on a throwaway copy of a checkout).
    [ObservableProperty] private string _hcgRepoCheckout = "";
    [ObservableProperty] private StatusTag _previewTag = new("Not previewed", "neutral");
    [ObservableProperty] private string _previewText = "";

    // Devices and installed builds.
    [ObservableProperty] private string _testDevices = "iPhone 13 Pro (Dopamine, rootless), 16.5\niPhone 8, 13.3";
    public ObservableCollection<DeviceItem> Devices { get; } = [];
    [ObservableProperty] private string _installedListing = "";
    [ObservableProperty] private bool _testerConfirmedNoOtherBuild;
    [ObservableProperty] private StatusTag _installedTag = new("Not checked", "warning");
    [ObservableProperty] private string _installedSummary = "";
    public ObservableCollection<string> InstalledFindings { get; } = [];
    private ConflictReport? _installed;

    // Run state.
    [ObservableProperty] private string? _ldid;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(BuildCommand), nameof(ExportHandoffCommand), nameof(PreviewImportCommand), nameof(ImportPinnedCommitCommand), nameof(SubmitToRepoCommand), nameof(RefreshReviewCommand), nameof(ApproveInRepoCommand), nameof(PrepareInRepoCommand))] private bool _isBusy;
    [ObservableProperty] private string _log = "";
    [ObservableProperty] private string _appSummary = "No app inspected yet.";
    [ObservableProperty] private string? _result;
    [ObservableProperty] private StatusTag _validationTag = new("Not built", "neutral");
    [ObservableProperty] private StatusTag _readinessTag = new("Not built", "neutral");
    [ObservableProperty] private string _readinessText = "";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(PreviewImportCommand), nameof(SubmitToRepoCommand), nameof(RefreshReviewCommand), nameof(ApproveInRepoCommand), nameof(PrepareInRepoCommand))] private string? _handoffFolder;
    [ObservableProperty] private StatusTag _handoffTag = new("Not exported", "neutral");
    public ObservableCollection<StageItem> Stages { get; } = [new("Archive"), new("Inspect"), new("Package"), new("Validate"), new("Handoff")];
    public ObservableCollection<CheckItem> Checks { get; } = [];
    public ObservableCollection<string> Missing { get; } = [];
    public string LdidStatus => Ldid is null ? "ldid not found: needed to pseudo-sign the unsigned Mach-O files of a device build (macOS: brew install ldid)." : "ldid: " + Ldid;
    private RootlessAppWorkflow.Outcome? _outcome;
    private DateTimeOffset? _builtAt;
    private string? _builtFrom;
    private string? _archiveFailure;

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        try
        {
            var report = await toolchains.DetectAsync(CancellationToken.None);
            Ldid = report.Find(ToolchainIds.Ldid) is { IsAvailable: true, ExecutablePath: { } p } ? p : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { Ldid = null; }
        OnPropertyChanged(nameof(LdidStatus));
        switch (parameter)
        {
            case UnleashifyRequest r:
                if (r.ProjectId is { } id && await projects.GetAsync(id, CancellationToken.None) is { } p) LoadProject(p, r.Scheme);
                if (r.DeviceBuild is { Length: > 0 } build)
                {
                    DeviceBuild = build;
                    if (Project is null || r.UseDeviceBuild) { BuildFromXcode = false; InspectInput(); }
                }
                break;
            case string path when path.Length > 0:
                DeviceBuild = path;
                BuildFromXcode = false;
                break;
        }
        if (Modes.Count == 0) LoadModes(Project?.Analysis);
        RefreshDevices(null);
        RefreshInstalled();
    }

    /// <summary>The imported project: its revision (and whether the copy is stale), schemes, identity defaults.</summary>
    public void LoadProject(ProjectRecord p, string? scheme)
    {
        Project = p;
        var ios = p.Analysis?.Mobile?.Ios;
        ProjectSummary = $"{p.Name} · {ios?.WorkspacePath ?? ios?.ProjectPath ?? "no Xcode project"} · imported from {p.SourceLocation}";
        _revision = ImportRevisions.Check(p);
        RevisionText = (_revision.Recorded?.Commit is { } c ? $"{c} {(_revision.Recorded.Branch is { } b ? $"({b}) " : "")}· " : "") + _revision.Detail;
        RevisionTag = _revision.State switch
        {
            RevisionState.Current => new("Current", "success"),
            RevisionState.Stale => new("Stale", "error"),
            RevisionState.FromArchive => new("From archive", "info"),
            _ => new("Unknown", "warning"),
        };
        OnPropertyChanged(nameof(RevisionIsStale));
        if (Commit.Length == 0 && _revision.Recorded?.Commit is { } commit) Commit = commit;
        Schemes.Clear();
        foreach (var s in ios?.Schemes.Select(s => s.Name) ?? []) Schemes.Add(s);
        SelectedScheme = scheme is not null && Schemes.Contains(scheme) ? scheme : Schemes.FirstOrDefault(s => s.EndsWith("Modern", StringComparison.Ordinal)) ?? Schemes.FirstOrDefault();
        if (PackageId.Length == 0) PackageId = "com.hakmicapitalgroup." + new string(p.Name.ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());
        if (AppName.Length == 0) AppName = p.Name.StartsWith("HCG", StringComparison.Ordinal) && p.Name.Length > 3 && char.IsUpper(p.Name[3]) ? "HCG " + p.Name[3..] : p.Name;
        if (OutputDir.Length == 0) OutputDir = Path.Combine(paths.DefaultOutputDirectory, "Unleashify", p.Name);
        SuggestedArtwork = PngIcon.SuggestArtwork(p.SourceDirectory);
        LoadModes(p.Analysis);
    }

    private void LoadModes(ProjectAnalysis? analysis)
    {
        var options = Unleashify.Modes(analysis).ToList();
        // An existing device build can always be packaged as a jailbreak app, even without a project.
        if (analysis is null) options[0] = options[0] with { Available = true, Reason = null };
        Modes.Clear();
        foreach (var o in options) Modes.Add(new ModeItem(o, m => SelectedMode = m));
        SelectedMode = Modes.FirstOrDefault(m => m.Available) ?? Modes[0];
        foreach (var m in Modes) m.IsSelected = m == SelectedMode;
    }

    partial void OnSelectedModeChanged(ModeItem? value) { foreach (var m in Modes) m.IsSelected = m == value; }

    partial void OnSelectedSchemeChanged(string? value)
    {
        var ios = Project?.Analysis?.Mobile?.Ios;
        var target = ios?.Schemes.FirstOrDefault(s => s.Name == value) is { } sc
            ? ios.Targets.FirstOrDefault(t => sc.ArchiveTargets.Contains(t.Name)) ?? ios.Targets.FirstOrDefault(t => t.Name == value)
            : null;
        DeploymentTarget = target?.DeploymentTarget is { } dt ? $"iOS {dt} (the project's deployment target; kept)" : "—";
        BundleId = target?.BundleIdentifier ?? "—";
        SchemeDetail = target is null ? "" : $"Target {target.Name} · bundle ID {target.BundleIdentifier} · iOS {target.DeploymentTarget}+ · Release, unsigned device archive (generic/platform=iOS)";
        RefreshDevices(target?.DeploymentTarget);
        RefreshInstalled();
    }

    partial void OnBuildFromXcodeChanged(bool value) => OnPropertyChanged(nameof(UseDeviceBuild));
    partial void OnTestDevicesChanged(string value) => RefreshDevices(_outcome?.App?.MinimumOS ?? CurrentDeploymentTarget());
    partial void OnInstalledListingChanged(string value) => RefreshInstalled();
    partial void OnTesterConfirmedNoOtherBuildChanged(bool value) => RefreshInstalled();
    partial void OnPackageIdChanged(string value) => RefreshInstalled();
    partial void OnPackageVersionChanged(string value) => RefreshInstalled();

    private string? CurrentDeploymentTarget() => DeploymentTarget.StartsWith("iOS ", StringComparison.Ordinal) ? DeploymentTarget[4..].Split(' ')[0] : null;

    private IReadOnlyList<DeviceTarget> ParsedDevices() => TestDevices.Replace("\r", "").Split('\n')
        .Select(l => l.Trim()).Where(l => l.Contains(','))
        .Select(l => new DeviceTarget(l[..l.LastIndexOf(',')].Trim(), l[(l.LastIndexOf(',') + 1)..].Trim().TrimStart('i', 'O', 'S', ' ')))
        .Where(d => d.IosVersion.Length > 0).ToList();

    private void RefreshDevices(string? minimumOs)
    {
        Devices.Clear();
        foreach (var d in Unleashify.Compatibility(minimumOs ?? CurrentDeploymentTarget(), ParsedDevices()))
            Devices.Add(new DeviceItem(d.Device.Name, d.Device.IosVersion, d.Compatible, d.Detail));
        UpdateReadiness();
    }

    private string InstallPath => _outcome?.Validation?.Facts.AppPath ?? $"/var/jb/Applications/{(SelectedScheme ?? Project?.Name ?? "App")}.app";

    private void RefreshInstalled()
    {
        var bundle = _outcome?.App?.BundleId ?? (BundleId != "—" ? BundleId : null);
        if (bundle is null) { InstalledTag = new("Not checked", "warning"); InstalledSummary = "Build first: the check needs the app's bundle ID."; InstalledFindings.Clear(); return; }
        _installed = Unleashify.CheckInstalled(bundle, PackageId.Trim(), PackageVersion.Trim(), InstallPath, InstalledListing, TesterConfirmedNoOtherBuild);
        InstalledTag = _installed.State switch
        {
            ConflictState.Clear => new("No conflict", "success"),
            ConflictState.Conflict => new("Conflict", "error"),
            _ => new("Not checked", "warning"),
        };
        InstalledSummary = _installed.Summary;
        InstalledFindings.Clear();
        foreach (var f in _installed.Findings) InstalledFindings.Add(f);
        UpdateReadiness();
    }

    private HandoffContext Context() => new(
        SelectedMode?.Label ?? "Jailbreak App", BuildFromXcode ? SelectedScheme : null, _revision?.Recorded, _revision?.State, _revision?.Detail, _installed,
        Unleashify.Compatibility(_outcome?.App?.MinimumOS ?? CurrentDeploymentTarget(), ParsedDevices()));

    private void UpdateReadiness()
    {
        if (_outcome is not { Succeeded: true }) { ReadinessTag = new("Not built", "neutral"); ReadinessText = ""; return; }
        var ctx = Context();
        ReadinessTag = ctx.ReadyForDeviceTesting ? new("Ready for device testing", "success") : new("Not ready for device testing", "warning");
        ReadinessText = ctx.ReadinessText;
    }

    [RelayCommand]
    private async Task BrowseDeviceBuildAsync()
    {
        if (await shell.PickFolderAsync("Choose the device .app or .xcarchive a build produced") is { } folder) { DeviceBuild = folder; BuildFromXcode = false; InspectInput(); }
    }

    [RelayCommand]
    private async Task BrowseIpaAsync()
    {
        if (await shell.PickFileAsync("Choose an .ipa a build produced", "iOS app archive", "*.ipa") is { } file) { DeviceBuild = file; BuildFromXcode = false; InspectInput(); }
    }

    /// <summary>Reads an existing build without packaging it: refuses Simulator builds and names any other problem.</summary>
    [RelayCommand]
    private void InspectInput()
    {
        if (DeviceBuild.Length == 0) return;
        try
        {
            var app = IosAppBundle.Open(DeviceBuild, Path.Combine(Path.GetTempPath(), "hcg-unleashify-inspect"));
            AppSummary = $"{app.Name} · {app.BundleId} · version {app.ShortVersion ?? "?"} ({app.BuildVersion ?? "?"}) · iOS {app.MinimumOS ?? "?"} or later · {app.Binaries.Count} Mach-O file(s)"
                         + (app.Problems.Count > 0 ? "\n" + string.Join("\n", app.Problems) : "");
            DeploymentTarget = app.MinimumOS is { } m ? $"iOS {m} (from the build; kept)" : "—";
            BundleId = app.BundleId;
            if (PackageVersion.Length == 0 && app.ShortVersion is { } v) PackageVersion = v + "~beta1";
            Stages[1].Tag = app.Problems.Count == 0 ? new("Inspected", "success") : new("Refused", "error");
            RefreshDevices(app.MinimumOS);
            RefreshInstalled();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or FormatException or InvalidCastException)
        {
            AppSummary = "Cannot use this input: " + ex.Message;
            Stages[1].Tag = new("Failed", "error");
        }
    }

    [RelayCommand]
    private async Task BrowsePinRepositoryAsync()
    {
        if (await shell.PickFolderAsync("Choose the app's local git repository (it is only read)") is { } folder) PinRepository = folder;
    }

    [RelayCommand]
    private async Task BrowseHcgRepoAsync()
    {
        if (await shell.PickFolderAsync("Choose an HCG Repo checkout (copied for the preview; never written)") is { } folder) HcgRepoCheckout = folder;
    }

    /// <summary>Checks out one exact commit of a trusted local repository into a new folder, imports it and selects it.</summary>
    [RelayCommand]
    private async Task ImportPinnedCommitAsync()
    {
        var git = new ExecutableLocator().Find("git");
        if (git is null) { PinStatus = "git was not found (install the Xcode Command Line Tools)."; return; }
        if (PinRepository.Trim().Length == 0 || PinCommit.Trim().Length == 0) { PinStatus = "Choose the repository folder and name the commit."; return; }
        IsBusy = true;
        try
        {
            PinStatus = $"Checking out {PinCommit.Trim()}…";
            var dest = Path.Combine(paths.Root, "pinned-sources", $"{new DirectoryInfo(PinRepository.Trim()).Name}-{PinCommit.Trim()[..Math.Min(12, PinCommit.Trim().Length)]}-{DateTime.UtcNow:yyyyMMddHHmmss}");
            var r = await Task.Run(() => PinnedSource.CheckoutAsync(runner, git, PinRepository.Trim(), PinCommit.Trim(), dest, CancellationToken.None));
            if (!r.Succeeded) { PinStatus = "Not imported: " + r.Failure; return; }
            var import = await projectService.ImportDirectoryAsync(r.Folder!, null, CancellationToken.None);
            LoadProject(import.Project, null);
            Commit = r.Commit!;
            if (r.Repository is { } name) Repository = name;
            BuildFromXcode = true;
            PinStatus = $"Imported commit {r.Commit} ({import.FileCount} files) from a clean checkout. {string.Join(" ", r.Notes)}"
                        + (r.Repository is null ? " The repository has no GitHub origin: type OWNER/NAME under Provenance." : $" Repository: {r.Repository}.");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            PinStatus = "Not imported: " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    private bool CanPreview() => !IsBusy && HandoffFolder is not null;

    /// <summary>Runs HCG Repo's importer on the handoff's exact bytes (dry run on a throwaway copy of the checkout).</summary>
    [RelayCommand(CanExecute = nameof(CanPreview))]
    private async Task PreviewImportAsync()
    {
        if (HandoffFolder is not { } folder) return;
        if (HcgRepoCheckout.Trim().Length == 0) { PreviewTag = new("Choose a checkout", "warning"); PreviewText = "Choose an HCG Repo checkout (with local-file import support) first."; return; }
        var python = new ExecutableLocator().Find("python3");
        if (python is null) { PreviewTag = new("python3 missing", "error"); PreviewText = "python3 was not found; HCG Repo's importer needs Python 3.11 or later."; return; }
        IsBusy = true;
        try
        {
            PreviewTag = new("Running", "info");
            if (!File.Exists(Path.Combine(folder, "import-request.toml")) && File.Exists(Path.Combine(folder, SubmissionManifest.FileName)))
            {
                // A local build: HCG Repo's Release Manager reviews it in a throwaway incoming store (nothing is kept).
                var p = await Task.Run(() => ShipToRepo.PreviewAsync(runner, python, HcgRepoCheckout.Trim(), folder, null, CancellationToken.None));
                PreviewText = p.Output.Trim();
                PreviewTag = p.State == "validated" ? new("Validated by HCG Repo · awaiting your approval", "success") : p.State == "refused" ? new("Refused by HCG Repo", "error") : new("Not run", "error");
                Log += "\n[Preview] HCG Repo Release Manager: " + (p.State ?? "not run");
                return;
            }
            var genuine = OwnerApproval.Trim().Length > 0 && !Missing.Any(m => m.StartsWith("owner approval", StringComparison.Ordinal));
            var r = await Task.Run(() => HcgRepoImportPreview.RunAsync(runner, python, HcgRepoCheckout.Trim(), folder, hypotheticalApproval: !genuine, requireGenuineApproval: genuine, null, CancellationToken.None));
            PreviewText = r.Verdict + "\n\n" + r.Output.Trim();
            PreviewTag = !r.Ran ? new("Not run", "error")
                : r.Verdict.StartsWith("VALIDATED", StringComparison.Ordinal) ? new("Importable as exported", "success")
                : r.Verdict.StartsWith("BYTES VALIDATED", StringComparison.Ordinal) ? new("Blocked only by the owner's approval", "warning")
                : new("Rejected", "error");
            Log += "\n[Preview] " + r.Verdict;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task BrowseArtworkAsync()
    {
        if (await shell.PickFileAsync("Choose the approved icon artwork (PNG, square, e.g. the 1024×1024 AppIcon image)", "PNG image", "*.png") is { } file) IconArtwork = file;
    }

    /// <summary>The owner chooses the project's own AppIcon image as the approved fallback artwork; never applied without this click.</summary>
    [RelayCommand]
    private void UseSuggestedArtwork()
    {
        if (SuggestedArtwork is { } a) IconArtwork = a;
    }

    private string? Python() => new ExecutableLocator().Find("python3");
    private string? Incoming() => IncomingStore.Trim() is { Length: > 0 } i ? i : null;

    private bool CanShip() => !IsBusy && HandoffFolder is not null;

    /// <summary>Ship to Repo › Submit: HCG Repo validates the exact .deb into its private incoming store (outside Git). Nothing is published.</summary>
    [RelayCommand(CanExecute = nameof(CanShip))]
    private async Task SubmitToRepoAsync()
    {
        if (HandoffFolder is not { } folder || _outcome?.Validation is not { } val) return;
        if (Python() is not { } python) { ShipTag = new("python3 missing", "error"); ShipText = "HCG Repo's Release Manager needs Python 3.11+ and dpkg-deb (brew install dpkg)."; return; }
        if (ShipToRepo.CheckoutProblem(HcgRepoCheckout.Trim()) is { } bad) { ShipTag = new("Choose a checkout", "warning"); ShipText = bad; return; }
        IsBusy = true;
        try
        {
            ShipTag = new("Submitting", "info");
            var r = await Task.Run(() => ShipToRepo.SubmitAsync(runner, python, HcgRepoCheckout.Trim(), folder, Incoming(), null, CancellationToken.None));
            SubmissionId = r.SubmissionId;
            NeedsPackageMetadata = !ShipToRepo.HasPackageMetadata(HcgRepoCheckout.Trim(), PackageId.Trim());
            ShipText = r.Output.Trim();
            ShipTag = r.State switch
            {
                "validated" => new($"Validated by HCG Repo · SHA-256 {val.Facts.Sha256[..12]}… · awaiting the owner's approval", "success"),
                "refused" => new("Refused by HCG Repo", "error"),
                _ => new(r.Succeeded ? "Submitted" : "Not submitted", r.Succeeded ? "info" : "error"),
            };
            Log += "\n[Ship to Repo] " + ShipTag.Text;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanShip))]
    private async Task RefreshReviewAsync()
    {
        if (SubmissionId is not { } id || Python() is not { } python) return;
        var r = await Task.Run(() => ShipToRepo.ShowAsync(runner, python, HcgRepoCheckout.Trim(), id, Incoming(), CancellationToken.None));
        ShipTag = new($"HCG Repo: {r.State ?? "unknown"}", r.State switch { "validated" or "approved" or "prepared" => "success", "refused" => "error", _ => "neutral" });
    }

    /// <summary>Ship to Repo › Approve: records the owner's approval of the exact reviewed SHA-256 (typed by the owner) in HCG Repo's review.</summary>
    [RelayCommand(CanExecute = nameof(CanShip))]
    private async Task ApproveInRepoAsync()
    {
        if (SubmissionId is not { } id || Python() is not { } python) { ShipText = "Submit first."; return; }
        if (ApprovedSha256.Trim().Length != 64 || OwnerApproval.Trim().Length == 0 || ApprovedBy.Trim().Length == 0)
        {
            ShipTag = new("Approval incomplete", "warning");
            ShipText = "An approval needs the owner's name (Approved by), their words (Owner's approval) and the exact SHA-256 they approve.";
            return;
        }
        var ok = await dialogs.ConfirmAsync("Record the owner's approval in HCG Repo?",
            $"Submission {id}\n\nSHA-256 {ApprovedSha256.Trim().ToLowerInvariant()}\nApproved by {ApprovedBy.Trim()}: “{OwnerApproval.Trim()}”\n\n" +
            "HCG Repo records this approval for a Candidate import of exactly these bytes. Nothing is imported, committed or published by this step.", "Record approval");
        if (!ok) return;
        IsBusy = true;
        try
        {
            var r = await Task.Run(() => ShipToRepo.ApproveAsync(runner, python, HcgRepoCheckout.Trim(), id, ApprovedSha256, ApprovedBy, OwnerApproval, Incoming(), CancellationToken.None));
            ShipText = r.Output.Trim();
            ShipTag = r.Succeeded ? new("Approved for Candidate import · not published", "success") : new("Approval refused", "error");
        }
        finally { IsBusy = false; }
    }

    /// <summary>Ship to Repo › Prepare: HCG Repo imports the approved bytes into the checkout's pool/ for Candidate. No commit, push or publication.</summary>
    [RelayCommand(CanExecute = nameof(CanShip))]
    private async Task PrepareInRepoAsync()
    {
        if (SubmissionId is not { } id || Python() is not { } python) { ShipText = "Submit and approve first."; return; }
        var ok = await dialogs.ConfirmAsync("Prepare the Candidate import in your HCG Repo checkout?",
            $"HCG Repo imports the approved bytes of {id} into {HcgRepoCheckout.Trim()}: pool/, repo-lock.json, imports/{id}.toml and submissions/{id}/" +
            (CreatePackageMetadata ? $", and packages/{PackageId.Trim()}/ from the handoff's draft" : "") +
            (PrepareViaLocalFile ? " — staged under imports/local/ with HCG Repo's local-file request, for its Prepare packages workflow (instead of pool/)" : "") +
            ".\n\nNothing is committed, pushed or published. Review the changes, commit them on a branch and open a pull request; publication is HCG Repo's separate, manual Publish workflow.", "Prepare");
        if (!ok) return;
        IsBusy = true;
        try
        {
            var r = await Task.Run(() => ShipToRepo.PrepareAsync(runner, python, HcgRepoCheckout.Trim(), id, OwnerApproval.Trim() is { Length: > 0 } words ? words : null,
                CreatePackageMetadata, Incoming(), null, CancellationToken.None, PrepareViaLocalFile));
            ShipText = r.Output.Trim();
            ShipTag = r.Succeeded ? new("Prepared in the checkout · commit on a branch · not published", "success") : new("Prepare refused", "error");
            if (r.Succeeded) NeedsPackageMetadata = false;
        }
        finally { IsBusy = false; }
    }

    private bool CanBuild() => !IsBusy && SelectedMode?.Available == true && (!BuildFromXcode || (TrustAcknowledged && Project is not null));

    /// <summary>Build: an unsigned device archive from the Xcode project (or the chosen device build) → package → validate.</summary>
    [RelayCommand(CanExecute = nameof(CanBuild))]
    private async Task BuildAsync()
    {
        IsBusy = true;
        Checks.Clear(); Missing.Clear(); Log = ""; Result = null; HandoffFolder = null; _outcome = null;
        HandoffTag = new("Not exported", "neutral");
        foreach (var s in Stages) s.Tag = new("Waiting", "neutral");
        var lines = new List<string>();
        void AddLog(string line) => Dispatcher.UIThread.Post(() => { lines.Add(line); Log = string.Join("\n", lines.TakeLast(500)); });
        try
        {
            if (_revision?.State == RevisionState.Stale)
                AddLog("WARNING: the imported source is stale — " + _revision.Detail);
            string source;
            if (BuildFromXcode)
            {
                Stages[0].Tag = new("Running", "info");
                var archive = await ArchiveAsync(AddLog);
                if (archive is null) { Stages[0].Tag = new("Failed", "error"); ValidationTag = new("Archive failed", "error"); Result = _archiveFailure; return; }
                Stages[0].Tag = new("Done", "success");
                source = archive;
                DeviceBuild = archive;
            }
            else
            {
                Stages[0].Tag = new("Existing build", "neutral");
                source = DeviceBuild;
            }
            _builtFrom = source;
            var request = WorkflowRequest(source, exportHandoff: false);
            var outcome = await Task.Run(() => new RootlessAppWorkflow(runner, TimeProvider.System).RunAsync(request, (stage, line) =>
            {
                AddLog($"[{stage}] {line}");
                Dispatcher.UIThread.Post(() => { var i = (int)stage + 1; if (i < Stages.Count && Stages[i].Tag.Tone is "neutral") Stages[i].Tag = new("Running", "info"); });
            }, CancellationToken.None));
            await Dispatcher.UIThread.InvokeAsync(() => { });
            _outcome = outcome;
            _builtAt = DateTimeOffset.UtcNow;
            foreach (var c in outcome.Validation?.Checks ?? []) Checks.Add(new CheckItem(c));
            foreach (var c in outcome.Lifecycle?.Checks ?? []) Checks.Add(new CheckItem(c with { Name = "Lifecycle · " + c.Name }));
            if (outcome.App is { } builtApp)
                ToolchainText = LocalBuild.ToolchainFrom(builtApp) is { } tc ? tc + " (from the app's Info.plist)" : "Not recorded in the app's Info.plist (no DTXcode).";
            for (var k = 1; k <= 3; k++)
                Stages[k].Tag = k - 1 < (int)outcome.ReachedStage || outcome.Succeeded ? new("Done", "success")
                    : k - 1 == (int)outcome.ReachedStage ? new("Failed", "error") : new("Not run", "neutral");
            if (outcome.App is { } app)
            {
                AppSummary = $"{app.Name} · {app.BundleId} · version {app.ShortVersion ?? "?"} ({app.BuildVersion ?? "?"}) · iOS {app.MinimumOS ?? "?"} or later · from {app.SourceKind}";
                DeploymentTarget = app.MinimumOS is { } m ? $"iOS {m} (from the build; kept)" : DeploymentTarget;
                BundleId = app.BundleId;
            }
            if (outcome.Succeeded)
            {
                var f = outcome.Validation!.Facts;
                Result = $"{outcome.Package!.DebPath}\nSHA-256 {f.Sha256} · {f.Size:N0} bytes · installs {f.AppPath}";
                ValidationTag = new("Package validated", "success");
            }
            else
            {
                Result = outcome.Failure;
                ValidationTag = new($"Stopped at {outcome.ReachedStage}", "error");
            }
            RefreshDevices(outcome.App?.MinimumOS);
            RefreshInstalled();
        }
        finally { IsBusy = false; }
    }

    private async Task<string?> ArchiveAsync(Action<string> log)
    {
        var p = Project!;
        var ios = p.Analysis?.Mobile?.Ios ?? throw new InvalidOperationException("The project has no Xcode project.");
        var container = ios.WorkspacePath ?? ios.ProjectPath;
        var settings = new BuildSettings
        {
            ProjectRelativePath = container ?? throw new InvalidOperationException("The project has no Xcode project."),
            Platform = TargetPlatform.IOS,
            Architecture = TargetArchitecture.Universal,
            Configuration = BuildConfigurationKind.Release,
            OutputDirectory = Path.Combine(OutputDir.Length > 0 ? OutputDir : paths.DefaultOutputDirectory, "archive"),
            Timeout = TimeSpan.FromMinutes(60),
            Identity = new AppIdentity { ApplicationName = AppName.Length > 0 ? AppName : p.Name, Version = PackageVersion.Split('~')[0] is { Length: > 0 } v ? v : "1.0.0", ApplicationId = BundleId != "—" ? BundleId : "app" },
            Mobile = new MobileBuildOptions
            {
                Framework = MobileFramework.NativeIos, Output = MobileOutput.IosArchive, Scheme = SelectedScheme, Container = container,
                Variant = "Release", Signing = MobileSigningMode.Unsigned,
            },
        };
        log($"[Archive] {SelectedScheme}: unsigned Release device archive (generic/platform=iOS) with Mobile Build Studio's engine");
        var record = await Task.Run(() => mobileBuilder.RunAsync(new MobileBuildRequest { Project = p, Settings = settings, TrustAcknowledged = TrustAcknowledged, AllowOverwriteOutput = true },
            new LogObserver(log), CancellationToken.None));
        TrustAcknowledged = false; // trust is confirmed per build
        var archive = record.Artifacts.Where(a => a.Format == PackagingFormat.IosArchive && a.Verification != VerificationStatus.Failed).Select(a => a.Path).FirstOrDefault();
        _archiveFailure = archive is null ? $"The device archive was not produced ({record.Status}): {record.FailureReason ?? "see the build log"}" : null;
        log(archive is null ? $"[Archive] FAILED: {record.Status} — {record.FailureReason}" : $"[Archive] {archive}");
        return archive;
    }

    private sealed class LogObserver(Action<string> log) : IBuildObserver
    {
        public void OnStage(BuildStage stage, string description) => log($"[Archive] {description}");
        public void OnLog(BuildLogLine line) { if (line.Kind is not BuildLogKind.Output) log("[Archive]   " + line.Text); }
    }

    private RootlessAppWorkflow.Request WorkflowRequest(string source, bool exportHandoff)
    {
        var spec = new RootlessAppPackageSpec(PackageId.Trim(), PackageVersion.Trim(), AppName.Trim(), RootlessAppPackager.HcgMaintainer, Description.Trim());
        var prov = new PackageProvenance(Repository.Trim() is { Length: > 0 } r ? r : null, Commit.Trim() is { Length: > 0 } sha ? sha : null,
            _revision?.State == RevisionState.Stale ? true : Commit.Trim().Length > 0 ? false : null,
            long.TryParse(CiRun.Trim(), out var run) ? run : null, CiArtifact.Trim() is { Length: > 0 } a ? a : null, null, null, HostInfo.BuildIdentity,
            CiRepository.Trim() is { Length: > 0 } cr ? cr : null, CiCommit.Trim() is { Length: > 0 } cc ? cc : null,
            IsLocalBuild && CiRun.Trim().Length == 0
                ? new LocalBuildInfo($"{HostInfo.BuildIdentity} · Unleashify, local", _outcome?.App is { } built ? LocalBuild.ToolchainFrom(built) : null,
                    _builtAt ?? DateTimeOffset.UtcNow, HostLabel.Trim() is { Length: > 0 } h ? h : null, SourceVisibility, null)
                : null);
        var primary = ParsedDevices().FirstOrDefault();
        var texts = new CandidateTexts(Description.Split('\n')[0].Trim(), Split(Changes), Split(KnownIssues), ProductionAccess,
            primary is null ? null : $"{primary.Name}, iOS {primary.IosVersion}", OwnerApproval.Trim() is { Length: > 0 } ok ? ok : null,
            ApprovedSha256.Trim() is { Length: > 0 } approvedSha ? approvedSha.ToLowerInvariant() : null, IconArtwork.Trim() is { Length: > 0 } art ? art : null);
        return new RootlessAppWorkflow.Request(source, OutputDir.Length > 0 ? OutputDir : Path.Combine(Path.GetTempPath(), "unleashify"), spec, prov, texts, Ldid,
            primary?.IosVersion, ExportHandoff: exportHandoff, Context: Context());
    }

    private bool CanExport() => !IsBusy && _outcome is { Succeeded: true };

    /// <summary>Export Candidate Handoff: the validated .deb's exact bytes with SHA-256, size, provenance and HCG Repo's import files.</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportHandoffAsync()
    {
        if (_outcome is not { Succeeded: true, App: { } app, Package: { } pkg, Validation: { } val }) return;
        IsBusy = true;
        try
        {
            var r = WorkflowRequest(_builtFrom ?? DeviceBuild, exportHandoff: true);
            var folder = Path.Combine(r.OutputDir, $"{r.Spec.PackageId}_{r.Spec.Version.Replace(':', '_')}-candidate-handoff");
            var prov = RootlessAppWorkflow.Complete(r.Provenance, app, val, _outcome.Lifecycle, r.DeviceIosVersion);
            var handoff = await CandidateHandoff.ExportAsync(folder, pkg.DebPath, app, r.Spec, val, prov, r.Texts, pkg.SignedFiles, DateTimeOffset.UtcNow, CancellationToken.None, r.Context, _outcome.Lifecycle);
            HandoffFolder = handoff.Folder;
            Missing.Clear();
            foreach (var m in handoff.Missing) Missing.Add(m);
            Stages[4].Tag = new("Done", "success");
            HandoffTag = handoff.ImportRequestComplete ? new(prov.Local is null ? "Handoff exported · import request pinned" : "Handoff exported · ready for Ship to Repo", "success") : new("Handoff exported · import request incomplete", "warning");
            PreviewTag = new("Not previewed", "neutral");
            PreviewText = "";
            IconStatus = File.Exists(Path.Combine(handoff.Folder, "icon.png"))
                ? "icon.png: " + (System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(handoff.Folder, "provenance.json")))?["icon"]?["detail"]?.GetValue<string>() ?? "standard PNG")
                : "icon.png is missing: " + (handoff.Missing.FirstOrDefault(m => m.StartsWith("icon.png", StringComparison.Ordinal)) ?? "");
            SubmissionId = null;
            ShipTag = new(File.Exists(Path.Combine(handoff.Folder, SubmissionManifest.FileName)) ? "Ready to submit" : "Not submittable", File.Exists(Path.Combine(handoff.Folder, SubmissionManifest.FileName)) ? "info" : "warning");
            ShipText = "";
            Log += $"\n[Handoff] {handoff.Files.Count} files in {handoff.Folder}";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task OpenOutputAsync()
    {
        if ((HandoffFolder ?? OutputDir) is { Length: > 0 } dir && Directory.Exists(dir)) await shell.OpenFolderAsync(dir);
    }

    private static List<string> Split(string text) => text.Replace("\r", "").Split('\n').Select(l => l.Trim().TrimStart('-', '•').Trim()).Where(l => l.Length > 0).ToList();
}
