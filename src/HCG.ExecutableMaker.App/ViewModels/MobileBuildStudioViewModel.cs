using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HCG.ExecutableMaker.App.Services;
using HCG.ExecutableMaker.BuildEngine;
using HCG.ExecutableMaker.BuildEngine.Mobile;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.Mobile;
using HCG.ExecutableMaker.ProjectInspection.Mobile;
using HCG.ExecutableMaker.Security;

namespace HCG.ExecutableMaker.App.ViewModels;

public sealed partial class MobileOutputItem(MobileOutputOption option, string? reason = null) : ObservableObject
{
    public MobileOutputOption Option { get; } = option;
    public string Name => Option.DisplayName;
    public string Description => Option.Description;
    /// <summary>The reason shown on this output. A deployment-target problem is stated once above the outputs; here it is only referred to.</summary>
    public string? Reason { get; } = option.IsAvailable ? null : reason ?? option.Reason;
    public bool HasReason => Reason is not null;
    public bool IsEnabled => Option.IsAvailable;
    public StatusTag Status => Option.IsAvailable ? new StatusTag("Available", "success") : new StatusTag("Unavailable", "warning");
    [ObservableProperty] private bool _isSelected;
}

/// <summary>An installed Xcode offered for HCG's builds, with the iOS versions its SDKs accept.</summary>
public sealed class XcodeChoice(XcodeInstallation installation, bool active)
{
    public XcodeInstallation Installation { get; } = installation;
    public bool IsActive { get; } = active;
    public string Name => $"Xcode {Installation.Version ?? "?"}{(IsActive ? " (selected)" : "")}";
    public string Detail =>
        (Installation.IosRange is { } d ? $"Device iOS {d.Minimum}+" : "Device range unknown") + " · " +
        (Installation.SimulatorRange is { } s ? $"Simulator iOS {s.Minimum}+" : "Simulator range unknown") + " · " + Installation.AppPath;
    public override string ToString() => Name;
}

public sealed class PlatformChoice(TargetPlatform platform, bool supported, string? reason)
{
    public TargetPlatform Platform { get; } = platform;
    public string Name => Platform.DisplayName();
    public bool IsSupported { get; } = supported;
    public string Detail { get; } = reason ?? (supported ? "Supported by this project" : "");
    public override string ToString() => Name;
}

/// <summary>
/// Mobile Build Studio: one page for Android (Gradle/Flutter/.NET MAUI) and iOS (Xcode/Flutter/.NET MAUI) builds and
/// for verifying existing .apk/.aab/.ipa artifacts. Only operations the engine really performs are offered; every
/// unavailable one shows the concrete reason.
/// </summary>
public sealed partial class MobileBuildStudioViewModel : PageViewModel
{
    private const int MaxDisplayedLogLines = 6000;
    private readonly ProjectService _projectService;
    private readonly IProjectRepository _projects;
    private readonly MobileBuildService _builder;
    private readonly MobileArtifactVerifier _verifier;
    private readonly IToolchainService _toolchains;
    private readonly IBuildWorker _worker;
    private readonly IAppPaths _paths;
    private readonly IDialogService _dialogs;
    private readonly IPlatformShell _shell;
    private readonly ConcurrentQueue<BuildLogLine> _pendingLog = new();
    private readonly DispatcherTimer _uiTimer;
    private readonly Stopwatch _clock = new();
    private CancellationTokenSource? _buildCts;
    private ToolchainReport? _report;
    private bool _loading;
    /// <summary>One ticket per selected project: inspection work started for another project is discarded when it completes.</summary>
    private readonly InspectionGate _projectGate = new();
    private bool _recomputing;
    private bool _suppressSchemeChange;
    private readonly IMobileSelectionStore _selections;
    private readonly HCG.ExecutableMaker.Infrastructure.Toolchains.DeveloperDirectorySelection _developerDirectory;
    /// <summary>Saves of the selection run in order; a newer selection is never overwritten by an older save.</summary>
    private Task _saving = Task.CompletedTask;

    public MobileBuildStudioViewModel(ProjectService projectService, IProjectRepository projects, MobileBuildService builder, MobileArtifactVerifier verifier,
        IToolchainService toolchains, IBuildWorker worker, IAppPaths paths, IDialogService dialogs, IPlatformShell shell,
        IMobileSelectionStore selections, HCG.ExecutableMaker.Infrastructure.Toolchains.DeveloperDirectorySelection developerDirectory)
    {
        _selections = selections;
        _developerDirectory = developerDirectory;
        _projectService = projectService;
        _projects = projects;
        _builder = builder;
        _verifier = verifier;
        _toolchains = toolchains;
        _worker = worker;
        _paths = paths;
        _dialogs = dialogs;
        _shell = shell;
        _uiTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (_, _) => FlushLog());
        OutputDirectory = paths.DefaultOutputDirectory;
        // A toolchain change (e.g. another Xcode selected in Toolchains) re-evaluates outputs and compatibility at once.
        _toolchains.Updated += (_, r) => Dispatcher.UIThread.Post(() => OnToolchainsUpdated(r));
    }

    private void OnToolchainsUpdated(ToolchainReport report)
    {
        _report = report;
        RebuildXcodeChoices();
        if (Mobile is not null) RecomputeOutputs();
    }

    public override string Title => "Mobile Build Studio";
    public override string Subtitle => "Android APK / App Bundle and iOS builds from real project toolchains — verified, never faked.";

    public bool IsMacHost => OperatingSystem.IsMacOS();

    public string HonestyNote =>
        "Mobile apps are built from their own Android, iOS, Flutter or .NET MAUI projects. Desktop executables cannot be converted into " +
        "iOS or Android apps, renaming a file is not a conversion, and an APK cannot be turned into an App Bundle. Nothing is uploaded to " +
        "Google Play, TestFlight or App Store Connect.";

    // ───────────── Project ─────────────

    public ObservableCollection<ProjectItem> MobileProjects { get; } = [];
    public ObservableCollection<SummaryRow> ProjectRows { get; } = [];
    public ObservableCollection<DiagnosticItem> Diagnostics { get; } = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasProject), nameof(IsArtifactProject))] private ProjectItem? _selectedProject;
    [ObservableProperty] private bool _isImporting;
    [ObservableProperty] private string? _importError;
    [ObservableProperty] private StatusTag _supportTag = new("—", "neutral");
    [ObservableProperty] private string _supportSummary = "";
    public bool HasProject => SelectedProject is not null;
    public bool IsArtifactProject => SelectedProject?.Project.Analysis?.Mobile?.ArtifactPath is not null;
    private MobileAnalysis? Mobile => SelectedProject?.Project.Analysis?.Mobile;

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        _report = await _toolchains.GetAsync();
        await LoadProjectsAsync(parameter as ProjectRecord);
    }

    private async Task LoadProjectsAsync(ProjectRecord? select = null)
    {
        _loading = true;
        var keep = select?.Id ?? SelectedProject?.Project.Id;
        MobileProjects.Clear();
        foreach (var p in await _projects.ListAsync())
            if (p.Analysis?.Mobile is not null) MobileProjects.Add(new ProjectItem(p));
        _loading = false;
        SelectedProject = MobileProjects.FirstOrDefault(p => p.Project.Id == keep) ?? MobileProjects.FirstOrDefault();
        if (SelectedProject is null) ClearProject();
    }

    partial void OnSelectedProjectChanged(ProjectItem? value)
    {
        if (_loading) return;
        _projectGate.Next();
        // A result belongs to the project that produced it; another project's build is not shown as this one's.
        _lastResult = null;
        BuiltArtifacts.Clear();
        OnPropertyChanged(nameof(HasBuiltArtifacts));
        HasBuildStarted = false;
        ResultTag = new StatusTag("Not started", "neutral");
        ResultDetail = "";
        IsPreviousBuild = false;
        if (value is null) { ClearProject(); return; }
        _ = OpenProjectAsync(value);
    }

    /// <summary>
    /// Opens a project with the selection saved for it (project/workspace, scheme, configuration). The load is asynchronous:
    /// if another project is selected meanwhile, this result is discarded.
    /// </summary>
    private async Task OpenProjectAsync(ProjectItem item)
    {
        var ticket = _projectGate.Current;
        MobileSelection? saved = null;
        try { saved = await _selections.LoadAsync(item.Project.Id.ToString()); }
        catch (Exception) { /* a missing or unreadable saved selection means the project's defaults */ }
        if (!_projectGate.IsCurrent(ticket) || SelectedProject != item) return;
        ApplyProject(item.Project, saved);
        SelectionRestored?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised when a project's saved selection has been applied (used by the UI driver to wait for it).</summary>
    public event EventHandler? SelectionRestored;

    private MobileSelection CurrentSelection() => new()
    {
        ProjectId = SelectedProject?.Project.Id.ToString() ?? "",
        Container = SelectedContainer,
        Scheme = SelectedScheme,
        Configuration = SelectedConfiguration?.Value,
        SavedAt = DateTimeOffset.UtcNow,
    };

    private void SaveSelection()
    {
        if (SelectedProject is null || Mobile?.Ios is null) return;
        var selection = CurrentSelection();
        _saving = _saving.ContinueWith(_ => _selections.SaveAsync(selection), TaskScheduler.Default).Unwrap();
    }

    /// <summary>Waits for pending selection saves (tests and the UI driver).</summary>
    public Task SelectionSaved => _saving;

    private void ClearProject()
    {
        ProjectRows.Clear();
        Diagnostics.Clear();
        Platforms.Clear();
        Outputs.Clear();
        SupportSummary = "Import an Android, iOS, Flutter or .NET MAUI project — or an .apk / .aab / .ipa to verify.";
        SupportTag = new StatusTag("No project", "neutral");
        OnPropertyChanged(nameof(HasOutputs));
    }

    [ObservableProperty] private string? _portResult;

    [RelayCommand]
    private async Task PortMacProjectAsync() => await PortAsync(null);

    [RelayCommand]
    private async Task PortMacAppAsync()
    {
        PortResult = null;
        var app = await _shell.PickFileAsync("Select the Mac application", "Mac applications", "*.app");
        if (app is null) return;
        var info = Path.Combine(app, "Contents", "Info.plist");
        if (!File.Exists(info))
        {
            PortResult = "Choose a macOS .app containing Contents/Info.plist.";
            return;
        }
        try
        {
            // plutil reads binary and XML Info.plists without launching the application.
            var start = new ProcessStartInfo("/usr/bin/plutil") { RedirectStandardOutput = true, UseShellExecute = false };
            foreach (var arg in new[] { "-extract", "CFBundleIdentifier", "raw", "-o", "-", "--", info })
                start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new IOException("Could not start plutil.");
            var id = (await process.StandardOutput.ReadToEndAsync()).Trim();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0 || string.IsNullOrEmpty(id))
                throw new InvalidDataException("Could not read the Mac app’s bundle identifier.");
            PortResult = $"Source required for {id}. Select the corresponding macOS Xcode project to create its iOS port.";
            await PortAsync(id);
        }
        catch (Exception ex) { PortResult = "Mac app inspection failed: " + ex.Message; }
    }

    private async Task PortAsync(string? bundleId)
    {
        if (bundleId is null) PortResult = null;
        var selected = await _shell.PickFileAsync("Select the source .xcodeproj", "Xcode projects", "*.xcodeproj");
        if (selected is null) return;
        var source = Path.GetDirectoryName(selected);
        if (source is null || !File.Exists(Path.Combine(selected, "project.pbxproj")))
        {
            PortResult = "Select a source .xcodeproj containing project.pbxproj.";
            return;
        }
        IsImporting = true;
        try
        {
            MacProjectPort.Result? port = null;
            var result = await Task.Run(() => _projectService.ImportDirectoryAsync(source, null, CancellationToken.None,
                copy => port = MacProjectPort.PrepareCopy(copy, Path.GetFileName(selected), bundleId)));
            await LoadProjectsAsync(result.Project);
            PortResult = $"Created an editable iPhone and iPad project at {Path.GetDirectoryName(port!.ProjectFile)}. Review macOS-only APIs, linked frameworks, signing and entitlements, then build the Simulator output below.";
            if (port.ReviewFiles.Count > 0)
                PortResult += "\nReview platform-specific references or resources in " + string.Join(", ", port.ReviewFiles);
            await _shell.OpenFolderAsync(result.Project.SourceDirectory);
        }
        catch (Exception ex) { PortResult = "Project port failed: " + ex.Message; }
        finally { IsImporting = false; }
    }

    [RelayCommand]
    private async Task ImportFolderAsync()
    {
        if (await _shell.PickFolderAsync("Select a mobile project folder") is { } dir)
            await RunImportAsync(ct => _projectService.ImportDirectoryAsync(dir, null, ct));
    }

    [RelayCommand]
    private async Task ImportZipAsync()
    {
        if (await _shell.PickZipAsync() is { } zip)
            await RunImportAsync(ct => _projectService.ImportZipAsync(zip, null, ct));
    }

    [RelayCommand]
    private async Task ImportArtifactAsync()
    {
        if (await _shell.PickFileAsync("Import a mobile artifact", "Android / iOS packages", "*.apk", "*.aab", "*.apks", "*.ipa") is { } file)
            await RunImportAsync(ct => _projectService.ImportArtifactAsync(file, null, ct));
    }

    private async Task RunImportAsync(Func<CancellationToken, Task<ImportResult>> import)
    {
        ImportError = null;
        IsImporting = true;
        try
        {
            var result = await Task.Run(() => import(CancellationToken.None));
            if (result.Project.Analysis?.Mobile is null)
                ImportError = $"“{result.Project.Name}” is not a mobile project ({result.Project.Analysis?.ApplicationType ?? "unrecognized"}). Desktop projects are built in New Build.";
            await LoadProjectsAsync(result.Project);
        }
        catch (ImportRejectedException ex) { ImportError = ex.Message; }
        catch (Exception ex) { ImportError = "Import failed: " + ex.Message; }
        finally { IsImporting = false; }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var ticket = _projectGate.Current;
        var project = SelectedProject;
        _report = await _toolchains.GetAsync(refresh: true);
        if (project is { } p)
        {
            await _projectService.InspectAsync(p.Project, CancellationToken.None);
            // Another project was selected meanwhile: this inspection no longer describes what is shown.
            if (!_projectGate.IsCurrent(ticket) || SelectedProject != project) return;
            // Re-inspection brings new project data; the user's current choices (possibly changed while it ran) are kept.
            ApplyProject(p.Project, CurrentSelection());
        }
    }

    private void ApplyProject(ProjectRecord project, MobileSelection? saved)
    {
        var a = project.Analysis!;
        var m = a.Mobile!;
        SupportTag = new StatusTag(Format.Label(a.Support), Format.Tone(a.Support));
        SupportSummary = a.SupportSummary;
        ProjectRows.Clear();
        ProjectRows.Add(new("Type", a.ApplicationType));
        ProjectRows.Add(new("Framework", m.Framework.DisplayName()));
        ProjectRows.Add(new("Platforms", string.Join(", ", m.Platforms.Select(p => p.DisplayName()))));
        if (m.ApplicationIds.Count > 0) ProjectRows.Add(new("Application ID", string.Join(", ", m.ApplicationIds)));
        if (m.Android is { } android)
        {
            ProjectRows.Add(new("Gradle", $"{(android.HasWrapper ? "wrapper " + android.WrapperGradleVersion : "no wrapper")} · AGP {android.AgpVersion ?? "unknown"} · {(android.UsesKotlin ? "Kotlin" : "Java")}"));
            foreach (var mod in android.AppModules)
                ProjectRows.Add(new("Module " + mod.GradlePath, $"{mod.ApplicationId} · v{mod.VersionName} ({mod.VersionCode}) · minSdk {mod.MinSdk} · targetSdk {mod.TargetSdk} · variants {string.Join(", ", mod.Variants)}{(mod.HasSigningConfig ? " · release signing configured" : "")}"));
        }
        if (m.Ios is { } ios)
        {
            ProjectRows.Add(new("Xcode", ios.WorkspacePath ?? ios.ProjectPath ?? "—"));
            ProjectRows.Add(new("Schemes", ios.Schemes.Count > 0 ? string.Join(", ", ios.Schemes.Select(s => s.Name)) : "none shared"));
            foreach (var t in ios.AppTargets)
                ProjectRows.Add(new("Target " + t.Name, $"{t.BundleIdentifier} · iOS {t.DeploymentTarget} · signing {t.CodeSignStyle ?? "default"}{(t.DevelopmentTeam is null ? "" : " · team " + t.DevelopmentTeam)}"));
        }
        if (m.Flutter is { } f) ProjectRows.Add(new("Flutter", $"{f.Name} {f.Version} · SDK {f.SdkConstraint}"));
        if (m.Maui is { } maui) ProjectRows.Add(new(".NET MAUI", $"{maui.ProjectPath} · {string.Join(", ", maui.TargetFrameworks)}"));
        if (m.DotNetIos is { } dn)
        {
            ProjectRows.Add(new(".NET for iOS", $"{dn.ProjectPath} · {dn.TargetFramework}{(dn.UiFramework is { } ui ? $" · {ui} {dn.UiFrameworkVersion}" : "")} · no .xcodeproj (the .NET iOS workload drives Xcode)"));
            ProjectRows.Add(new("App", $"{dn.ApplicationTitle} · {dn.ApplicationId} · {dn.DisplayVersion ?? "version not known (its props are outside the import)"} ({dn.BuildNumber}) · iOS {dn.MinimumOS}+{(dn.Entitlements is { } e ? " · entitlements " + e : "")}"));
            ProjectRows.Add(new("Shared projects", dn.ProjectReferences.Count == 0 ? "none" : string.Join(", ", dn.ProjectReferences)));
            if (dn.OutsideWorkspace.Count > 0) ProjectRows.Add(new("Outside the import", string.Join(", ", dn.OutsideWorkspace) + " — import the repository root"));
            ProjectRows.Add(new("Toolchain pins", $".NET SDK {dn.GlobalJsonSdk ?? "(no global.json)"} · iOS workload {dn.WorkloadPin ?? "(not pinned)"}{(dn.WorkloadPinFile is { } pinFile ? $" ({pinFile})" : "")}"));
        }
        if (m.ArtifactPath is { } artifact) ProjectRows.Add(new("Artifact", artifact));
        Diagnostics.Clear();
        foreach (var d in a.Diagnostics) Diagnostics.Add(new DiagnosticItem(d));

        // Project/workspace, scheme and configuration belong to the project, not to an output: they are offered before the
        // outputs and stay selectable when no output is available. The saved selection for this project is restored.
        _suppressSchemeChange = true;
        Containers.Clear(); Schemes.Clear(); Configurations.Clear();
        if (m.Ios is { } iosInfo && m.ArtifactPath is null)
        {
            var restored = IosBuildSelection.Restore(project.Id.ToString(), iosInfo, saved);
            foreach (var c in IosBuildSelection.Containers(iosInfo)) Containers.Add(c);
            foreach (var sc in IosBuildSelection.Schemes(iosInfo)) Schemes.Add(sc);
            Configurations.Add(new(null, "Scheme default (Run → Simulator, Archive → device)"));
            foreach (var c in iosInfo.Configurations) Configurations.Add(new(c, c));
            SelectedContainer = restored.Container;
            SelectedScheme = restored.Scheme;
            SelectedConfiguration = Configurations.FirstOrDefault(c => c.Value == restored.Configuration) ?? Configurations[0];
        }
        else { SelectedContainer = null; SelectedScheme = null; SelectedConfiguration = null; }
        ShowContainers = Containers.Count > 1;
        _suppressSchemeChange = false;
        RebuildXcodeChoices();

        var options = MobileCapabilities.Evaluate(m, _report, SelectionContext(m), SelectedConfiguration?.Value);
        Platforms.Clear();
        foreach (var platform in new[] { TargetPlatform.Android, TargetPlatform.IOS })
        {
            var supported = m.Platforms.Contains(platform);
            Platforms.Add(new PlatformChoice(platform, supported, supported ? null : $"This project has no {platform.DisplayName()} target."));
        }
        _allOptions = options;
        SelectedPlatform = Platforms.FirstOrDefault(p => p.IsSupported && options.Any(o => o.Platform == p.Platform && o.IsAvailable))
                           ?? Platforms.FirstOrDefault(p => p.IsSupported);
        ApplyIdentityDefaults(project, m);
        _ = LoadArtifactReportAsync();
    }

    // ───────────── Target platform & output ─────────────

    private IReadOnlyList<MobileOutputOption> _allOptions = [];
    public ObservableCollection<PlatformChoice> Platforms { get; } = [];
    public ObservableCollection<MobileOutputItem> Outputs { get; } = [];
    [ObservableProperty] private PlatformChoice? _selectedPlatform;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(StartBuildCommand))] private MobileOutputItem? _selectedOutput;
    public bool HasOutputs => Outputs.Count > 0;

    partial void OnSelectedPlatformChanged(PlatformChoice? value)
    {
        Outputs.Clear();
        if (value is not null)
            foreach (var o in _allOptions.Where(o => o.Platform == value.Platform)) Outputs.Add(new MobileOutputItem(o, IosBuildSelection.ShortReason(o)));
        OnPropertyChanged(nameof(HasOutputs));
        ShowSchemes = Schemes.Count > 0 && value?.Platform == TargetPlatform.IOS;
        ShowIosSelection = value?.Platform == TargetPlatform.IOS && Mobile?.Ios is not null && Mobile.ArtifactPath is null;
        SelectOutput(Outputs.FirstOrDefault(o => o.IsEnabled));
        UpdateSchemeCompatibility();
    }

    /// <summary>The selection outputs are judged for: scheme and the configuration chosen for the current output.</summary>
    private MobileBuildOptions SelectionContext(MobileAnalysis m) => new()
    {
        Framework = m.Framework,
        Output = SelectedOutput?.Option.Output ?? MobileOutput.IosSimulatorApp,
        Scheme = SelectedScheme,
        Container = SelectedContainer,
        Variant = SelectedOutput is null || m.Ios is not null ? null : SelectedVariant,
    };

    /// <summary>
    /// Re-evaluates every output for the current scheme, configuration and toolchain. The selected output is kept while it is
    /// still available (its configuration and signing choices stay); otherwise the first available output is chosen.
    /// </summary>
    private void RecomputeOutputs()
    {
        if (_recomputing || Mobile is not { } m) return;
        _recomputing = true;
        try
        {
            _allOptions = MobileCapabilities.Evaluate(m, _report, SelectionContext(m), SelectedConfiguration?.Value);
            var keep = SelectedOutput?.Option.Output;
            Outputs.Clear();
            if (SelectedPlatform is { } platform)
                foreach (var o in _allOptions.Where(o => o.Platform == platform.Platform)) Outputs.Add(new MobileOutputItem(o, IosBuildSelection.ShortReason(o)));
            OnPropertyChanged(nameof(HasOutputs));
            var same = Outputs.FirstOrDefault(o => o.Option.Output == keep && o.IsEnabled);
            if (keep is not null && same is not null)
            {
                foreach (var o in Outputs) o.IsSelected = o == same;
                SelectedOutput = same;
            }
            else SelectOutput(Outputs.FirstOrDefault(o => o.IsEnabled));
        }
        finally { _recomputing = false; }
        UpdateSchemeCompatibility();
        PrepareSummary();
    }

    partial void OnSelectedSchemeChanged(string? value) => OnSelectionChanged();
    partial void OnSelectedContainerChanged(string? value) => OnSelectionChanged();
    partial void OnSelectedConfigurationChanged(Choice<string?>? value) => OnSelectionChanged();

    /// <summary>
    /// A new scheme, project/workspace or configuration: earlier compatibility results no longer apply. Every output is
    /// re-judged at once (synchronously, so no older result can arrive afterwards) and the choice is saved for the project.
    /// </summary>
    private void OnSelectionChanged()
    {
        if (_suppressSchemeChange || Mobile is null) return;
        _selectionGeneration++;
        RecomputeOutputs();
        SaveSelection();
    }

    private int _selectionGeneration;
    /// <summary>Increases with every selection change (lets tests see that results were recomputed).</summary>
    public int SelectionGeneration => _selectionGeneration;

    // ───────────── Project/workspace, configuration and Xcode (before the outputs) ─────────────

    public ObservableCollection<string> Containers { get; } = [];
    public ObservableCollection<Choice<string?>> Configurations { get; } = [];
    public ObservableCollection<XcodeChoice> XcodeChoices { get; } = [];
    [ObservableProperty] private string? _selectedContainer;
    [ObservableProperty] private Choice<string?>? _selectedConfiguration;
    [ObservableProperty] private XcodeChoice? _selectedXcode;
    [ObservableProperty] private bool _showContainers;
    [ObservableProperty] private bool _showIosSelection;
    [ObservableProperty] private string? _xcodeMessage;
    private bool _suppressXcodeChange;
    public bool HasXcodeChoices => XcodeChoices.Count > 0;

    private void RebuildXcodeChoices()
    {
        _suppressXcodeChange = true;
        XcodeChoices.Clear();
        if (_report?.Xcode is { } x)
            foreach (var i in x.Installed)
                XcodeChoices.Add(new XcodeChoice(i, string.Equals(i.DeveloperDirectory, x.ActiveDeveloperDirectory, StringComparison.Ordinal)));
        SelectedXcode = XcodeChoices.FirstOrDefault(c => c.IsActive);
        OnPropertyChanged(nameof(HasXcodeChoices));
        XcodeMessage = _report?.Xcode is { } xs
            ? $"Builds use {(xs.VerifiedVersion is { } v ? "Xcode " + v : "the selected Xcode")} ({xs.ActiveDeveloperDirectory}). Choosing another applies to HCG's builds only; xcode-select is not changed."
            : "No Xcode was found. Install Xcode, then press Refresh.";
        _suppressXcodeChange = false;
    }

    partial void OnSelectedXcodeChanged(XcodeChoice? value)
    {
        if (_suppressXcodeChange || value is null || value.IsActive) return;
        _ = SelectXcodeAsync(value);
    }

    /// <summary>Selects an installed Xcode for HCG's builds (DEVELOPER_DIR, as in Toolchains › Xcode), then re-detects; outputs are re-judged.</summary>
    private async Task SelectXcodeAsync(XcodeChoice choice)
    {
        try
        {
            var selected = await _developerDirectory.SelectAsync(choice.Installation.AppPath);
            XcodeMessage = $"HCG now builds with {selected.Display}. The system-wide xcode-select setting was not changed.";
        }
        catch (InvalidOperationException ex) { XcodeMessage = ex.Message; RebuildXcodeChoices(); return; }
        _report = await _toolchains.GetAsync(refresh: true);
        RebuildXcodeChoices();
        if (Mobile is not null) RecomputeOutputs();
    }

    // ───────────── Scheme compatibility (Simulator and device judged separately) ─────────────

    public ObservableCollection<SummaryRow> SchemeChecks { get; } = [];
    [ObservableProperty] private bool _hasSchemeChecks;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSchemeSuggestion), nameof(UseSuggestedSchemeLabel))] private string? _suggestedScheme;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(UseSuggestedSchemeLabel))] private string? _suggestedSchemeMinimum;
    [ObservableProperty] private string _schemeSuggestionText = "";
    [ObservableProperty] private string? _toolchainRouteText;
    /// <summary>The one shared deployment-target message (null when the selected scheme fits the selected Xcode).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasCompatibilityProblem))] private string? _compatibilityProblem;
    public bool HasCompatibilityProblem => CompatibilityProblem is not null;
    public bool HasSchemeSuggestion => SuggestedScheme is not null;
    public string UseSuggestedSchemeLabel => SuggestedScheme is null ? "" : $"Use {SuggestedScheme}{(SuggestedSchemeMinimum is { } min ? $" (iOS {min} and later)" : "")}";

    /// <summary>
    /// Switches to the scheme verified compatible — only on request, after explaining its minimum iOS. HCG never changes the
    /// scheme or any deployment target by itself, and the legacy scheme stays in the project.
    /// </summary>
    [RelayCommand]
    private async Task UseSuggestedSchemeAsync()
    {
        if (SuggestedScheme is not { } s || !Schemes.Contains(s)) return;
        if (!await _dialogs.ConfirmAsync($"Use {s}?", SchemeSuggestionText, $"Use {s}")) return;
        SelectedScheme = s;
    }

    private void UpdateSchemeCompatibility()
    {
        SchemeChecks.Clear();
        SuggestedScheme = null;
        SuggestedSchemeMinimum = null;
        SchemeSuggestionText = "";
        ToolchainRouteText = null;
        CompatibilityProblem = null;
        if (Mobile is not { Ios: { } ios, ArtifactPath: null } m || m.Framework is not (MobileFramework.NativeIos or MobileFramework.Capacitor)
            || SelectedPlatform?.Platform != TargetPlatform.IOS || _report?.Xcode is not { } x)
        {
            HasSchemeChecks = false;
            return;
        }
        // Device and Simulator are judged with their own SDK ranges and their own effective configuration; one shared
        // sentence states the problem, and each output only refers to it.
        var summary = IosBuildSelection.Summarize(ios, SelectedScheme, SelectedConfiguration?.Value, x);
        foreach (var (label, text, _) in summary.Lines) SchemeChecks.Add(new(label, text));
        HasSchemeChecks = true;
        CompatibilityProblem = summary.Problem;
        ToolchainRouteText = summary.KeepRoute;
        if (summary.Alternative is { } alt)
        {
            SuggestedScheme = alt;
            SuggestedSchemeMinimum = summary.AlternativeMinimum;
            SchemeSuggestionText = summary.SwitchExplanation ?? "";
        }
        // The project card shows the current state of the selection, not the toolchains seen at import time.
        if (summary.Problem is not null)
        {
            SupportTag = new StatusTag("Selected toolchain incompatible", "warning");
            SupportSummary = summary.Problem;
        }
        else if (Outputs.Any(o => o.IsEnabled))
        {
            SupportTag = new StatusTag(Format.Label(SupportLevel.Supported), Format.Tone(SupportLevel.Supported));
            SupportSummary = $"{SelectedScheme} can be built with the selected Xcode ({string.Join(", ", Outputs.Where(o => o.IsEnabled).Select(o => o.Name))}).";
        }
        else if (SelectedProject?.Project.Analysis is { } a)
        {
            SupportTag = new StatusTag(Format.Label(a.Support), Format.Tone(a.Support));
            SupportSummary = a.SupportSummary;
        }
    }

    [RelayCommand]
    private void SelectOutput(MobileOutputItem? item)
    {
        if (item is not null && !item.IsEnabled) return;
        foreach (var o in Outputs) o.IsSelected = o == item;
        SelectedOutput = item;
        RebuildConfiguration();
    }

    // ───────────── Configuration ─────────────

    public ObservableCollection<string> Modules { get; } = [];
    public ObservableCollection<string> Variants { get; } = [];
    public ObservableCollection<string> Schemes { get; } = [];
    public ObservableCollection<Choice<MobileSigningMode>> SigningModes { get; } = [];
    public ObservableCollection<Choice<IosExportMethod>> ExportMethods { get; } = [];
    public ObservableCollection<Choice<ApkSetMode>> ApkSetModes { get; } = [new(ApkSetMode.Universal, "Universal APK (all ABIs and densities)"), new(ApkSetMode.DeviceTargeted, "Device-targeted split APKs (device-spec JSON)")];
    [ObservableProperty] private string? _selectedModule;
    [ObservableProperty] private string? _selectedVariant;
    [ObservableProperty] private string? _selectedScheme;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowTeam), nameof(ShowProfile), nameof(ShowProvisioningUpdates), nameof(SigningExplanation), nameof(ShowSigningIdentity))] private Choice<MobileSigningMode>? _selectedSigning;
    [ObservableProperty] private Choice<IosExportMethod>? _selectedExportMethod;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowDeviceSpec))] private Choice<ApkSetMode>? _selectedApkSetMode;
    [ObservableProperty] private string _developmentTeam = "";
    [ObservableProperty] private string _provisioningProfile = "";
    [ObservableProperty] private bool _allowProvisioningUpdates;
    [ObservableProperty] private string? _deviceSpecPath;
    [ObservableProperty] private string _appName = "";
    [ObservableProperty] private string _appVersion = "";
    [ObservableProperty] private string _outputDirectory;
    [ObservableProperty] private bool _showModules;
    [ObservableProperty] private bool _showVariants;
    [ObservableProperty] private bool _showSchemes;
    [ObservableProperty] private bool _showSigning;
    [ObservableProperty] private bool _showExport;
    [ObservableProperty] private bool _showApkSet;

    public bool ShowTeam => SelectedSigning?.Value is MobileSigningMode.AppleAutomatic or MobileSigningMode.AppleManual;
    /// <summary>.NET for iOS signs with a named certificate (CodesignKey).</summary>
    public bool ShowSigningIdentity => SelectedSigning?.Value == MobileSigningMode.AppleManual && Mobile?.Framework == MobileFramework.DotNetIos;
    [ObservableProperty] private string _signingIdentity = "";
    partial void OnSigningIdentityChanged(string value) => PrepareSummary();
    public bool ShowProfile => SelectedSigning?.Value is MobileSigningMode.AppleManual;
    public bool ShowProvisioningUpdates => SelectedSigning?.Value is MobileSigningMode.AppleAutomatic;
    public bool ShowDeviceSpec => ShowApkSet && SelectedApkSetMode?.Value == ApkSetMode.DeviceTargeted;

    public string SigningExplanation => SelectedSigning?.Value switch
    {
        MobileSigningMode.ProjectDefault => "Uses only what the project itself configures. HCG never supplies credentials: secret-like environment variables are removed and personal Gradle properties are not read.",
        MobileSigningMode.Unsigned => "Produces an unsigned artifact. It cannot be installed (Android) or exported to an IPA (iOS) until it is signed.",
        MobileSigningMode.DebugKeystore => "Signs with HCG's local Android debug key (created once in HCG's data folder; the conventional public debug password). Installable for testing — never accepted by Google Play.",
        MobileSigningMode.AppleAutomatic => "Xcode automatic signing for your Apple Developer team, using certificates already in this Mac's keychain.",
        MobileSigningMode.AppleManual when Mobile?.Framework == MobileFramework.DotNetIos =>
            "The .NET iOS build signs with the certificate you name (CodesignKey) and an installed provisioning profile (CodesignProvision). The team ID is used for the IPA export options. HCG never creates certificates or profiles.",
        MobileSigningMode.AppleManual => "Xcode manual signing with an installed provisioning profile (by name). HCG never creates certificates or profiles.",
        _ => "",
    };

    private void RebuildConfiguration()
    {
        var m = Mobile;
        var output = SelectedOutput?.Option.Output;
        Modules.Clear(); Variants.Clear(); SigningModes.Clear(); ExportMethods.Clear();
        ShowModules = ShowVariants = ShowSigning = ShowExport = ShowApkSet = false;
        if (m is null || output is null) { OnPropertyChanged(nameof(ShowDeviceSpec)); return; }
        var android = output.Value.Platform() == TargetPlatform.Android;
        var debugOutput = output is MobileOutput.AndroidDebugApk or MobileOutput.IosSimulatorApp;

        if (android && m.Android is { } a && m.ArtifactPath is null)
        {
            foreach (var mod in a.AppModules) Modules.Add(mod.GradlePath);
            ShowModules = Modules.Count > 1;
            SelectedModule = Modules.FirstOrDefault();
            var module = a.AppModules.FirstOrDefault();
            var kind = output == MobileOutput.AndroidDebugApk ? "Debug" : "Release";
            foreach (var v in module?.Variants.Where(v => v.EndsWith(kind, StringComparison.OrdinalIgnoreCase)) ?? []) Variants.Add(v);
            ShowVariants = Variants.Count > 1;
            SelectedVariant = Variants.FirstOrDefault();
        }
        if (!android && m.Ios is { } ios && m.ArtifactPath is null)
        {
            // The configuration is chosen with the scheme (before the outputs); here it is only resolved for this output's SDK.
            foreach (var c in ios.Configurations) Variants.Add(c);
            ShowVariants = false;
            SelectedVariant = IosBuildSelection.Configuration(ios, SelectedScheme, SelectedConfiguration?.Value, output == MobileOutput.IosSimulatorApp);
        }

        if (!debugOutput && output != MobileOutput.AndroidAabToApk)
        {
            ShowSigning = true;
            SigningModes.Add(new(MobileSigningMode.ProjectDefault, Format.Label(MobileSigningMode.ProjectDefault)));
            // .NET for iOS creates an Xcode archive only with code signing (Xamarin.Shared.targets: "Code signing must be
            // enabled to create an Xcode archive" — CI run 78), so it is never offered unsigned.
            if (m.Framework is MobileFramework.NativeAndroid or MobileFramework.NativeIos || (m.Framework == MobileFramework.Flutter && output == MobileOutput.IosArchive))
                SigningModes.Add(new(MobileSigningMode.Unsigned, Format.Label(MobileSigningMode.Unsigned)));
            if (android && m.Framework == MobileFramework.NativeAndroid)
                SigningModes.Add(new(MobileSigningMode.DebugKeystore, Format.Label(MobileSigningMode.DebugKeystore)));
            if (!android && m.Framework == MobileFramework.DotNetIos)
                SigningModes.Add(new(MobileSigningMode.AppleManual, "Named certificate and provisioning profile (CodesignKey / CodesignProvision)"));
            if (!android && m.Framework == MobileFramework.NativeIos)
            {
                SigningModes.Add(new(MobileSigningMode.AppleAutomatic, Format.Label(MobileSigningMode.AppleAutomatic)));
                SigningModes.Add(new(MobileSigningMode.AppleManual, Format.Label(MobileSigningMode.AppleManual)));
            }
            SelectedSigning = SigningModes[0];
        }
        else SelectedSigning = null;
        if (output == MobileOutput.IosIpa)
        {
            ShowExport = true;
            foreach (var e in Enum.GetValues<IosExportMethod>()) ExportMethods.Add(new(e, Format.Label(e)));
            SelectedExportMethod = ExportMethods.First(e => e.Value == IosExportMethod.Debugging);
        }
        ShowApkSet = output == MobileOutput.AndroidAabToApk;
        SelectedApkSetMode = ApkSetModes[0];
        OnPropertyChanged(nameof(ShowDeviceSpec));
        PrepareSummary();
    }

    private void ApplyIdentityDefaults(ProjectRecord project, MobileAnalysis m)
    {
        AppName = project.Analysis?.ProjectName ?? project.Name;
        AppVersion = m.Android?.AppModules.FirstOrDefault()?.VersionName ?? m.Flutter?.Version?.Split('+')[0] ?? m.Maui?.DisplayVersion ?? m.DotNetIos?.DisplayVersion ?? "1.0.0";
        if (m.DotNetIos?.ApplicationTitle is { } title) AppName = title;
    }

    [RelayCommand]
    private async Task ChooseOutputDirectoryAsync()
    {
        if (await _shell.PickFolderAsync("Choose output directory") is { } dir) OutputDirectory = dir;
    }

    [RelayCommand]
    private async Task ChooseDeviceSpecAsync()
    {
        if (await _shell.PickFileAsync("Choose a device spec (bundletool get-device-spec)", "Device spec JSON", "*.json") is { } file) DeviceSpecPath = file;
    }

    private MobileBuildOptions? CreateOptions()
    {
        var m = Mobile;
        if (m is null || SelectedOutput is null) return null;
        return new MobileBuildOptions
        {
            Framework = m.Framework,
            Output = SelectedOutput.Option.Output,
            Module = SelectedModule,
            Variant = m.Ios is { } iosOpt && m.ArtifactPath is null
                ? IosBuildSelection.Configuration(iosOpt, SelectedScheme, SelectedConfiguration?.Value, SelectedOutput.Option.Output == MobileOutput.IosSimulatorApp)
                : SelectedVariant,
            Scheme = SelectedScheme,
            Container = m.Ios is not null && m.ArtifactPath is null ? SelectedContainer : null,
            Signing = SelectedSigning?.Value ?? MobileSigningMode.ProjectDefault,
            DevelopmentTeam = string.IsNullOrWhiteSpace(DevelopmentTeam) ? null : DevelopmentTeam.Trim(),
            ProvisioningProfile = string.IsNullOrWhiteSpace(ProvisioningProfile) ? null : ProvisioningProfile.Trim(),
            AllowProvisioningUpdates = AllowProvisioningUpdates && SelectedSigning?.Value == MobileSigningMode.AppleAutomatic,
            ExportMethod = SelectedExportMethod?.Value,
            ApkSetMode = SelectedApkSetMode?.Value ?? ApkSetMode.Universal,
            DeviceSpecPath = DeviceSpecPath,
            InputArtifact = m.ArtifactPath,
            SigningIdentity = string.IsNullOrWhiteSpace(SigningIdentity) ? null : SigningIdentity.Trim(),
        };
    }

    /// <summary>The options a build started now would use (exactly what <see cref="StartBuildAsync"/> passes to the build service).</summary>
    public MobileBuildOptions? CurrentOptions => CreateOptions();
    public ToolchainReport? CurrentToolchains => _report;

    private BuildSettings CreateSettings(MobileBuildOptions o) => new()
    {
        ProjectRelativePath = Mobile?.Android?.Root ?? o.Container ?? Mobile?.Ios?.WorkspacePath ?? Mobile?.Ios?.ProjectPath ?? Mobile?.Flutter?.Root ?? Mobile?.Maui?.ProjectPath ?? Mobile?.DotNetIos?.ProjectPath ?? Mobile?.ArtifactPath ?? "",
        Platform = o.Output.Platform(),
        Architecture = TargetArchitecture.Universal,
        Configuration = o.Output is MobileOutput.AndroidDebugApk or MobileOutput.IosSimulatorApp ? BuildConfigurationKind.Debug : BuildConfigurationKind.Release,
        OutputDirectory = OutputDirectory,
        Timeout = TimeSpan.FromMinutes(60),
        Identity = new AppIdentity
        {
            ApplicationName = string.IsNullOrWhiteSpace(AppName) ? "App" : AppName.Trim(),
            Version = string.IsNullOrWhiteSpace(AppVersion) ? "1.0.0" : AppVersion.Trim(),
            ApplicationId = Mobile?.ApplicationIds.FirstOrDefault() ?? "app",
        },
        Mobile = o,
    };

    // ───────────── Build status ─────────────

    public string TrustTitle => TrustPolicy.TrustBoundaryTitle;
    public string TrustExplanation =>
        "Building runs code from this project with your user's permissions: the Gradle wrapper and build scripts, Xcode run-script phases, " +
        "Flutter/Dart build hooks or MSBuild targets. Gradle may download the distribution named by the wrapper. Only build projects you trust.";
    public string IsolationDescription => _worker.Description;
    public ObservableCollection<SummaryRow> Summary { get; } = [];
    public ObservableCollection<LogLineItem> LogLines { get; } = [];
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(StartBuildCommand))] private bool _trustAcknowledged;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(StartBuildCommand), nameof(CancelBuildCommand))] private bool _isBuilding;
    [ObservableProperty] private bool _hasBuildStarted;
    [ObservableProperty] private string _currentStage = "Ready";
    [ObservableProperty] private string _elapsed = "0.0s";
    [ObservableProperty] private int _liveWarnings;
    [ObservableProperty] private int _liveErrors;
    [ObservableProperty] private StatusTag _resultTag = new("Not started", "neutral");
    [ObservableProperty] private string _resultDetail = "";

    public event EventHandler? LogAppended;

    partial void OnSelectedSigningChanged(Choice<MobileSigningMode>? value) => PrepareSummary();
    // iOS: the configuration is chosen in the selection card (SelectedConfiguration), which re-judges the outputs.
    partial void OnSelectedVariantChanged(string? value) => PrepareSummary();
    partial void OnOutputDirectoryChanged(string value) => PrepareSummary();
    partial void OnAppNameChanged(string value) => PrepareSummary();
    partial void OnAppVersionChanged(string value) => PrepareSummary();
    partial void OnSelectedOutputChanged(MobileOutputItem? value) => PrepareSummary();
    partial void OnSelectedExportMethodChanged(Choice<IosExportMethod>? value) => PrepareSummary();
    partial void OnProvisioningProfileChanged(string value) => PrepareSummary();
    partial void OnAllowProvisioningUpdatesChanged(bool value) => PrepareSummary();

    // ───────────── Readiness for the chosen project + output + signing ─────────────

    public ObservableCollection<ReadinessRow> ReadinessRequired { get; } = [];
    public ObservableCollection<ReadinessRow> ReadinessOptional { get; } = [];
    [ObservableProperty] private StatusTag _readinessTag = new("—", "neutral");
    [ObservableProperty] private string _readinessSummary = "";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(StartBuildCommand))] private bool _readinessCanBuild;
    [ObservableProperty] private bool _hasReadiness;

    [RelayCommand] private async Task OpenSetupUrlAsync(ReadinessRow? row) { if (row?.SetupUrl is { } u) await _shell.OpenUrlAsync(u); }

    // ───────────── Precise state of the selected project + output ─────────────

    /// <summary>
    /// Recognized project · Project implementation incomplete · Build adapter unavailable · Required toolchain missing ·
    /// Configuration required · Ready to build · Build failed · Artifact verified.
    /// </summary>
    [ObservableProperty] private StatusTag _buildStateTag = new("—", "neutral");
    [ObservableProperty] private string _buildStateDetail = "";

    private void UpdateBuildState(BuildReadiness? readiness)
    {
        var a = SelectedProject?.Project.Analysis;
        if (a?.Mobile is not { } m) { (BuildStateTag, BuildStateDetail) = (new StatusTag("—", "neutral"), ""); return; }
        var current = CurrentProvenance();
        var forPlatform = _allOptions.Where(o => SelectedPlatform is null || o.Platform == SelectedPlatform.Platform).ToList();
        var view = MobileBuildState.Describe(a, m, forPlatform, SelectedOutput?.Option, readiness, _lastResult, current);
        (BuildStateTag, BuildStateDetail) = (new StatusTag(view.Tag, view.Tone), view.Detail);
        UpdateResultView(current);
    }

    /// <summary>What building with the current selection would record as its inputs (null when no output is selected).</summary>
    private BuildProvenance? CurrentProvenance() =>
        SelectedProject is { } p && CreateOptions() is { } o ? MobileBuildState.ProvenanceFor(p.Project, CreateSettings(o), _report) : null;

    /// <summary>"Previous build": the last result was produced with other inputs than the current selection.</summary>
    [ObservableProperty] private bool _isPreviousBuild;

    private void UpdateResultView(BuildProvenance? current)
    {
        if (_lastResult is not { } r || IsBuilding) { IsPreviousBuild = false; return; }
        var view = MobileBuildState.DescribeResult(r, current);
        IsPreviousBuild = !view.IsCurrent;
        if (view.IsCurrent)
        {
            ResultTag = new StatusTag(Format.Label(r.Status), Format.Tone(r.Status));
            CurrentStage = r.Status == BuildStatus.Succeeded ? "Completed" : Format.Label(r.Status);
            ResultDetail = r.Status == BuildStatus.Succeeded
                ? $"{r.Artifacts.Count} artifact(s) in {Format.Duration(r.Duration)}. Nothing was uploaded or published."
                : r.FailureReason ?? "";
        }
        else
        {
            // The artifacts stay listed and openable; they are labelled as what produced them, not as the current state.
            ResultTag = new StatusTag("Previous build", "neutral");
            CurrentStage = "Previous build";
            ResultDetail = $"{Format.Label(r.Status)} · {view.Detail}";
        }
    }

    private BuildRecord? _lastResult;

    /// <summary>The minimum iOS of what the build archives: the scheme's app target (Xcode) or SupportedOSPlatformVersion (.NET).</summary>
    private static string? DeviceMinimum(MobileAnalysis m, MobileBuildOptions o) =>
        m.DotNetIos?.MinimumOS ?? m.Ios?.AppTargets.FirstOrDefault(t => t.Name == o.Scheme)?.DeploymentTarget ?? m.Ios?.AppTargets.FirstOrDefault()?.DeploymentTarget;

    private void UpdateReadiness(MobileBuildOptions? o)
    {
        ReadinessRequired.Clear();
        ReadinessOptional.Clear();
        if (Mobile is not { } m || o is null)
        {
            HasReadiness = false;
            ReadinessCanBuild = false;
            UpdateBuildState(null);
            return;
        }
        var r = MobileReadiness.Evaluate(m, o.Output, o, _report);
        UpdateBuildState(r);
        foreach (var i in r.Items.Where(i => i.Required)) ReadinessRequired.Add(new ReadinessRow(i));
        foreach (var i in r.Items.Where(i => !i.Required)) ReadinessOptional.Add(new ReadinessRow(i));
        HasReadiness = true;
        ReadinessCanBuild = r.CanBuild;
        ReadinessTag = r.CanBuild ? new StatusTag("Can build", "success") : new StatusTag($"{r.Blockers.Count()} blocker(s)", "error");
        ReadinessSummary = r.CanBuild
            ? r.Items.Any(i => i.Required && i.State == ReadinessState.NotVerifiable) ? "Everything required is present; items marked “Not verifiable” are confirmed by the build itself." : "Everything this build needs is ready."
            : "Fix the required items below, then press Refresh on the Toolchains page (or here).";
    }

    private void PrepareSummary()
    {
        Summary.Clear();
        UpdateReadiness(CreateOptions());
        if (CreateOptions() is not { } o) return;
        var s = CreateSettings(o);
        var m = Mobile!;
        Summary.Add(new("Project", $"{SelectedProject?.Project.Name} · {s.ProjectRelativePath}"));
        Summary.Add(new("Adapter", o.Framework switch
        {
            MobileFramework.NativeIos => o.Output == MobileOutput.IosSimulatorApp ? "xcodebuild build (Simulator SDK)" : o.Output == MobileOutput.IosIpa ? "xcodebuild archive → signature check → xcodebuild -exportArchive (separate stages)" : "xcodebuild archive",
            MobileFramework.DotNetIos => o.Output == MobileOutput.IosSimulatorApp ? ".NET iOS workload: dotnet build (Simulator)" : o.Output == MobileOutput.IosIpa
                ? ".NET iOS workload: dotnet publish (ArchiveOnBuild) → signature check → xcodebuild -exportArchive (separate stages)" : ".NET iOS workload: dotnet publish (ArchiveOnBuild)",
            MobileFramework.DotNetMaui => ".NET MAUI workload: dotnet build/publish",
            MobileFramework.Flutter => "flutter build",
            _ => "Gradle (project wrapper)",
        }));
        var tc = _report;
        string Tool(string id) => tc?.Find(id) is { IsAvailable: true } t ? $"{t.DisplayName} {t.Version}".Trim() : "";
        var xcode = tc?.Xcode is { VerifiedVersion: { } xv } xs ? $"Xcode {xv} ({xs.ActiveDeveloperDirectory})" + (xs.IosDeploymentRange is { } range ? $" · iOS SDK {range.SdkVersion}, deployment iOS {range.Minimum}–{range.Maximum}" : "") : Tool(ToolchainIds.Xcode);
        Summary.Add(new("Toolchain", o.Output.Platform() == TargetPlatform.IOS
            ? string.Join(" · ", new[] { xcode, o.Framework is MobileFramework.DotNetIos or MobileFramework.DotNetMaui ? Tool(ToolchainIds.DotNetSdk) : "", o.Framework == MobileFramework.DotNetIos ? Tool(ToolchainIds.DotNetIosWorkload) : "" }.Where(x => x.Length > 0))
            : string.Join(" · ", new[] { Tool(ToolchainIds.Jdk), Tool(ToolchainIds.AndroidSdk) }.Where(x => x.Length > 0))));
        Summary.Add(new("Architecture", o.Output switch
        {
            MobileOutput.IosSimulatorApp => $"iOS Simulator ({(tc?.Host.Architecture == TargetArchitecture.Arm64 ? "arm64" : "x86_64")})",
            MobileOutput.IosArchive or MobileOutput.IosIpa => "iOS device, arm64" + (DeviceMinimum(m, o) is { } min ? $" · iOS {min}+" : ""),
            _ => "Android ABIs as the project defines them",
        }));
        Summary.Add(new("Output type", o.Output.DisplayName()));
        if (o.Scheme is not null) Summary.Add(new("Scheme", o.Scheme));
        if (o.Variant is not null) Summary.Add(new(o.Output.Platform() == TargetPlatform.Android ? "Variant" : "Configuration", o.Variant));
        if (o.Framework == MobileFramework.NativeIos && m.Ios is { } iosCmd)
        {
            // Computed by the same function the build uses, so what is shown is what runs.
            try { Summary.Add(new("Command", XcodePlanner.Preview(o, iosCmd))); }
            catch (BuildValidationException ex) { Summary.Add(new("Command", ex.Message)); }
        }
        if (SelectedSigning is not null) Summary.Add(new("Signing", Format.Label(o.Signing)));
        if (o.ExportMethod is { } e) Summary.Add(new("Export", Format.Label(e)));
        if (o.InputArtifact is not null) Summary.Add(new("Input", o.InputArtifact));
        Summary.Add(new("Destination", MobileBuildService.OutputRootFor(s)));
    }

    private bool CanStartBuild() => TrustAcknowledged && !IsBuilding && SelectedOutput?.IsEnabled == true && ReadinessCanBuild;

    [RelayCommand(CanExecute = nameof(CanStartBuild))]
    private async Task StartBuildAsync()
    {
        if (SelectedProject is null || CreateOptions() is not { } options) return;
        var settings = CreateSettings(options);
        var root = MobileBuildService.OutputRootFor(settings);
        var allowOverwrite = false;
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
        {
            if (!File.Exists(Path.Combine(root, BuildService.OutputMarkerFile)))
            {
                await _dialogs.AlertAsync("Output folder in use", $"“{root}” already exists and was not created by HCG Executable Maker. It will not be replaced.");
                return;
            }
            allowOverwrite = await _dialogs.ConfirmAsync("Replace previous output?", $"A previous HCG build exists:\n\n{root}\n\nReplace it?", "Replace and build", destructive: true);
            if (!allowOverwrite) return;
        }

        LogLines.Clear();
        while (_pendingLog.TryDequeue(out _)) { }
        LiveWarnings = LiveErrors = 0;
        CurrentStage = "Starting…";
        ResultTag = new StatusTag("Running", "info");
        ResultDetail = "";
        BuiltArtifacts.Clear();
        IsBuilding = true;
        HasBuildStarted = true;
        _buildCts = new CancellationTokenSource();
        _clock.Restart();
        _uiTimer.Start();
        BuildRecord? result = null;
        try
        {
            var request = new MobileBuildRequest { Project = SelectedProject.Project, Settings = settings, TrustAcknowledged = TrustAcknowledged, AllowOverwriteOutput = allowOverwrite };
            var token = _buildCts.Token;
            result = await Task.Run(() => _builder.RunAsync(request, new Observer(this), token));
        }
        catch (Exception ex) { await _dialogs.AlertAsync("Build could not run", ex.Message); }
        finally
        {
            _clock.Stop();
            _uiTimer.Stop();
            FlushLog();
            IsBuilding = false;
            TrustAcknowledged = false; // trust is confirmed per build
            _buildCts.Dispose();
            _buildCts = null;
        }
        if (result is null) return;
        _lastResult = result;
        LastLogPath = result.LogPath;
        FirstError = result.Status == BuildStatus.Succeeded ? null : FirstErrorOf(result.FailureReason);
        foreach (var a in result.Artifacts) BuiltArtifacts.Add(new ArtifactItem(a, result));
        OnPropertyChanged(nameof(HasBuiltArtifacts));
        UpdateBuildState(CreateOptions() is { } again && Mobile is { } mm ? MobileReadiness.Evaluate(mm, again.Output, again, _report) : null);
    }

    // ───────────── Readable failures: first actionable error, copy, full log and result bundles ─────────────

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasFirstError))] private string? _firstError;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasLog))] private string? _lastLogPath;
    public bool HasFirstError => !string.IsNullOrEmpty(FirstError);
    public bool HasLog => LastLogPath is not null && File.Exists(LastLogPath);

    internal static string? FirstErrorOf(string? reason) => MobileBuildService.FirstErrorOf(reason);

    [RelayCommand] private async Task CopyFirstErrorAsync() { if (FirstError is { } e) await _shell.CopyAsync(e); }
    [RelayCommand] private async Task CopyFailureAsync() { if (_lastResult?.FailureReason is { } r) await _shell.CopyAsync(r); }

    /// <summary>Copies the complete build log and every .xcresult bundle of the last build to a folder the user chooses.</summary>
    [RelayCommand]
    private async Task ExportFullLogAsync()
    {
        if (LastLogPath is not { } log || !File.Exists(log)) return;
        if (await _shell.PickFolderAsync("Export the complete build log and result bundles") is not { } dir) return;
        var target = Path.Combine(dir, "hcg-build-" + Path.GetFileName(Path.GetDirectoryName(log)));
        Directory.CreateDirectory(target);
        File.Copy(log, Path.Combine(target, "build.log"), overwrite: true);
        var work = Path.Combine(Path.GetDirectoryName(log)!, "work");
        var bundles = Directory.Exists(work) ? Directory.GetDirectories(work, "*.xcresult") : [];
        foreach (var b in bundles) MobileBuildService.CopyDirectory(b, Path.Combine(target, Path.GetFileName(b)));
        await _dialogs.AlertAsync("Build log exported", $"{target}\n\nbuild.log{(bundles.Length > 0 ? " and " + string.Join(", ", bundles.Select(Path.GetFileName)) : "")}. Open .xcresult bundles in Xcode.");
    }

    [RelayCommand]
    private async Task OpenLogFolderAsync() { if (LastLogPath is { } log && Path.GetDirectoryName(log) is { } d) await _shell.OpenFolderAsync(d); }

    private bool CanCancelBuild() => IsBuilding;

    [RelayCommand(CanExecute = nameof(CanCancelBuild))]
    private void CancelBuild()
    {
        CurrentStage = "Cancelling — terminating build processes…";
        _buildCts?.Cancel();
    }

    private void FlushLog()
    {
        Elapsed = Format.Duration(_clock.Elapsed);
        if (_pendingLog.IsEmpty) return;
        var added = 0;
        while (_pendingLog.TryDequeue(out var line) && added < 2000) { LogLines.Add(new LogLineItem(line)); added++; }
        while (LogLines.Count > MaxDisplayedLogLines) LogLines.RemoveAt(0);
        LogAppended?.Invoke(this, EventArgs.Empty);
    }

    private sealed class Observer(MobileBuildStudioViewModel vm) : IBuildObserver
    {
        public void OnLog(BuildLogLine line) => vm._pendingLog.Enqueue(line);
        public void OnStage(BuildStage stage, string description) => Dispatcher.UIThread.Post(() => vm.CurrentStage = description);
        public void OnMessage(BuildMessage message) => Dispatcher.UIThread.Post(() =>
        {
            if (message.Severity == DiagnosticSeverity.Warning) vm.LiveWarnings++;
            if (message.Severity == DiagnosticSeverity.Error) vm.LiveErrors++;
        });
    }

    // ───────────── Artifacts ─────────────

    public ObservableCollection<ArtifactItem> BuiltArtifacts { get; } = [];
    public bool HasBuiltArtifacts => BuiltArtifacts.Count > 0;
    public ObservableCollection<CheckItem> ArtifactChecks { get; } = [];
    [ObservableProperty] private string? _artifactReportTitle;
    [ObservableProperty] private StatusTag _artifactReportTag = new("—", "neutral");
    [ObservableProperty] private bool _isVerifying;

    private async Task LoadArtifactReportAsync()
    {
        ArtifactChecks.Clear();
        ArtifactReportTitle = null;
        if (SelectedProject?.Project is not { } p || Mobile?.ArtifactPath is not { } rel) return;
        await VerifyPathAsync(Path.Combine(p.SourceDirectory, rel), $"Imported artifact · {rel}");
    }

    [RelayCommand]
    private async Task VerifyArtifactFileAsync()
    {
        if (await _shell.PickFileAsync("Verify a mobile artifact (it is read, never executed or modified)", "Android / iOS packages", "*.apk", "*.aab", "*.apks", "*.ipa") is { } file)
            await VerifyPathAsync(file, Path.GetFileName(file));
    }

    private async Task VerifyPathAsync(string path, string title)
    {
        IsVerifying = true;
        try
        {
            var (report, checks) = await Task.Run(() => _verifier.VerifyAsync(path, _report, CancellationToken.None));
            ArtifactChecks.Clear();
            foreach (var c in checks) ArtifactChecks.Add(new CheckItem(c));
            ArtifactReportTitle = $"{title} — {Format.Bytes(report.SizeBytes)}";
            var status = Packaging.Verification.Checks.Combine(checks);
            ArtifactReportTag = new StatusTag(Format.Label(status), Format.Tone(status));
        }
        catch (Exception ex) { await _dialogs.AlertAsync("Verification failed", ex.Message); }
        finally { IsVerifying = false; }
    }

    [RelayCommand]
    private async Task OpenArtifactFolderAsync(ArtifactItem item)
    {
        var dir = item.Artifact.Kind == ArtifactKind.Directory ? Path.GetDirectoryName(item.Path) : Path.GetDirectoryName(item.Path);
        if (dir is not null) await _shell.OpenFolderAsync(dir);
    }

    [RelayCommand]
    private async Task CopyChecksumAsync(ArtifactItem item) => await _shell.CopyAsync(item.Sha256);
}
