using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HCG.ExecutableMaker.App.Services;
using HCG.ExecutableMaker.BuildEngine;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging;
using HCG.ExecutableMaker.Packaging.Icons;
using HCG.ExecutableMaker.Security;

namespace HCG.ExecutableMaker.App.ViewModels;

/// <summary>The six-step build workflow: Import → Inspect → Configure → Package → Build → Results.</summary>
public sealed partial class BuildWizardViewModel : PageViewModel
{
    public static readonly object ResetRequest = new();
    public static readonly object ImportZipRequest = new();
    public static readonly object ImportFolderRequest = new();
    private const int MaxDisplayedLogLines = 6000;

    private readonly ProjectService _projectService;
    private readonly IProjectRepository _projects;
    private readonly BuildService _buildService;
    private readonly IToolchainService _toolchains;
    private readonly ISettingsStore _settings;
    private readonly IAppPaths _paths;
    private readonly IDialogService _dialogs;
    private readonly IPlatformShell _shell;
    private readonly ArtifactExporter _exporter;
    private readonly HCG.ExecutableMaker.Packaging.MacDistribution.MacSigningInventoryReader _signingReader;
    private readonly HCG.ExecutableMaker.BuildEngine.MacDistribution.NotarizationService _notarization;
    private readonly HCG.ExecutableMaker.BuildEngine.MacDistribution.PkgTestInstaller _pkgTester;
    private readonly HCG.ExecutableMaker.BuildEngine.MacDistribution.AppStoreValidationService _validator;
    private MacDistributionOptions? _repeatDistribution;
    private readonly NavigationService _navigation;
    private readonly IProcessRunner _processRunner;
    private readonly HCG.ExecutableMaker.BuildEngine.Swift.SwiftPackageEvaluator _swiftEvaluator;
    private readonly HCG.ExecutableMaker.BuildEngine.Native.NativeConfigurator _nativeConfigurator;
    private readonly ConcurrentQueue<BuildLogLine> _pendingLog = new();
    private readonly DispatcherTimer _uiTimer;
    private readonly Stopwatch _buildClock = new();
    private CancellationTokenSource? _importCts;
    private CancellationTokenSource? _buildCts;
    private string? _identityPrefilledFor;

    public BuildWizardViewModel(ProjectService projectService, IProjectRepository projects, BuildService buildService, IToolchainService toolchains,
        ISettingsStore settings, IAppPaths paths, IDialogService dialogs, IPlatformShell shell, ArtifactExporter exporter, NavigationService navigation,
        HCG.ExecutableMaker.BuildEngine.Swift.SwiftPackageEvaluator swiftEvaluator,
        HCG.ExecutableMaker.Packaging.MacDistribution.MacSigningInventoryReader signingReader,
        HCG.ExecutableMaker.BuildEngine.MacDistribution.NotarizationService notarization,
        HCG.ExecutableMaker.BuildEngine.MacDistribution.PkgTestInstaller pkgTester,
        HCG.ExecutableMaker.BuildEngine.MacDistribution.AppStoreValidationService validator,
        IProcessRunner processRunner,
        HCG.ExecutableMaker.BuildEngine.Native.NativeConfigurator nativeConfigurator)
    {
        _processRunner = processRunner;
        _nativeConfigurator = nativeConfigurator;
        _signingReader = signingReader;
        _notarization = notarization;
        _pkgTester = pkgTester;
        _validator = validator;
        _swiftEvaluator = swiftEvaluator;
        _navigation = navigation;
        _projectService = projectService;
        _projects = projects;
        _buildService = buildService;
        _toolchains = toolchains;
        _settings = settings;
        _paths = paths;
        _dialogs = dialogs;
        _shell = shell;
        _exporter = exporter;
        Steps =
        [
            new(WizardStep.Import, "Import"), new(WizardStep.Inspect, "Inspect"), new(WizardStep.Configure, "Configure"),
            new(WizardStep.Package, "Package"), new(WizardStep.Build, "Build"), new(WizardStep.Results, "Results"),
        ];
        _uiTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (_, _) => FlushLog());
        SelectedInstallScope = InstallScopeOptions[0];
        UpdateSteps();
    }

    public override string Title => "New Build";
    public override string Subtitle => "One project in. A distributable application out.";

    public ObservableCollection<StepItem> Steps { get; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImportStep), nameof(IsInspectStep), nameof(IsConfigureStep), nameof(IsPackageStep), nameof(IsBuildStep), nameof(IsResultsStep), nameof(ShowBack), nameof(ShowNext))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand), nameof(BackCommand))]
    private WizardStep _step;

    public bool IsImportStep => Step == WizardStep.Import;
    public bool IsInspectStep => Step == WizardStep.Inspect;
    public bool IsConfigureStep => Step == WizardStep.Configure;
    public bool IsPackageStep => Step == WizardStep.Package;
    public bool IsBuildStep => Step == WizardStep.Build;
    public bool IsResultsStep => Step == WizardStep.Results;
    public bool ShowBack => Step is not WizardStep.Import and not WizardStep.Results;
    public bool ShowNext => Step is not WizardStep.Build and not WizardStep.Results;

    // ───────────── Navigation ─────────────

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        await LoadRecentProjectsAsync();
        if (parameter is null) return;
        if (IsBuilding)
        {
            await _dialogs.AlertAsync("Build in progress", "Finish or cancel the current build before starting another one.");
            return;
        }
        if (parameter == ResetRequest) { Reset(); return; }
        if (parameter == ImportZipRequest) { Reset(); await ImportZipAsync(); return; }
        if (parameter == ImportFolderRequest) { Reset(); await ImportFolderAsync(); return; }
        if (parameter is ProjectRecord project) { Reset(); await LoadProjectAsync(project); return; }
        if (parameter is BuildRecord previous) { Reset(); await LoadRepeatAsync(previous); }
    }

    private bool CanGoNext() => Step switch
    {
        WizardStep.Import => Project is not null && !IsImporting,
        WizardStep.Inspect => CanConfigure,
        WizardStep.Configure => ConfigErrors.Count == 0,
        WizardStep.Package => PackagingOptions.Any(o => o.IsSelected && o.IsEnabled) && !HasDistributionErrors,
        _ => false,
    };

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private async Task NextAsync()
    {
        switch (Step)
        {
            case WizardStep.Import:
                Step = WizardStep.Inspect;
                break;
            case WizardStep.Inspect:
                await PrepareConfigurationAsync();
                Step = WizardStep.Configure;
                break;
            case WizardStep.Configure:
                PreparePackaging();
                Step = WizardStep.Package;
                break;
            case WizardStep.Package:
                PrepareSummary();
                TrustAcknowledged = false;
                Step = WizardStep.Build;
                break;
        }
        UpdateSteps();
    }

    private bool CanGoBack() => ShowBack && !IsBuilding && !IsImporting && !IsEvaluatingSwift && !IsConverting;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back()
    {
        Step = Step - 1;
        UpdateSteps();
    }

    private void UpdateSteps()
    {
        foreach (var s in Steps)
        {
            s.IsActive = s.Step == Step;
            s.IsDone = s.Step < Step;
        }
        NextCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
    }

    private void Reset()
    {
        Project = null;
        _inspectedAnalysis = null;
        ClearBuildRoutes();
        Analysis = null;
        ImportError = null;
        ImportWarnings.Clear();
        Result = null;
        _identityPrefilledFor = null;
        SwiftEvaluationTrusted = false;
        SwiftEvaluationLog.Clear();
        HasSwiftEvaluationLog = false;
        IconPath = null;
        ResetDesktopConversion();
        Step = WizardStep.Import;
        UpdateSteps();
    }

    // ───────────── Step 1: Import ─────────────

    public ObservableCollection<ProjectItem> RecentProjects { get; } = [];
    public ObservableCollection<string> ImportWarnings { get; } = [];
    [ObservableProperty] private bool _hasRecentProjects;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(NextCommand), nameof(BackCommand))] private bool _isImporting;
    [ObservableProperty] private double _importPercent;
    [ObservableProperty] private bool _importIndeterminate = true;
    [ObservableProperty] private string _importStatus = "";
    [ObservableProperty] private string? _importError;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasProject), nameof(ProjectTitle), nameof(ProjectSource))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private ProjectRecord? _project;

    public bool HasProject => Project is not null;
    public string ProjectTitle => Project?.Name ?? "";
    public string ProjectSource => Project is null ? "" : $"{Format.Label(Project.SourceKind)} · {Project.SourceLocation}";

    private async Task LoadRecentProjectsAsync()
    {
        RecentProjects.Clear();
        foreach (var p in (await _projects.ListAsync()).Take(6)) RecentProjects.Add(new ProjectItem(p));
        HasRecentProjects = RecentProjects.Count > 0;
    }

    [RelayCommand]
    private async Task ImportZipAsync()
    {
        if (await _shell.PickZipAsync() is { } zip)
            await RunImportAsync(ct => _projectService.ImportZipAsync(zip, ImportProgressReporter(), ct));
    }

    [RelayCommand]
    private async Task ImportFolderAsync()
    {
        if (await _shell.PickFolderAsync("Select a project folder") is { } dir)
            await RunImportAsync(ct => _projectService.ImportDirectoryAsync(dir, ImportProgressReporter(), ct));
    }

    [RelayCommand] private void CancelImport() => _importCts?.Cancel();

    [RelayCommand]
    private async Task UseProjectAsync(ProjectItem item) => await LoadProjectAsync(item.Project);

    private IProgress<ImportProgress> ImportProgressReporter() => new Progress<ImportProgress>(p =>
    {
        ImportIndeterminate = p.Fraction is null;
        ImportPercent = (p.Fraction ?? 0) * 100;
        ImportStatus = p.TotalFiles is { } total
            ? $"{p.Stage} · {p.FilesProcessed:N0} / {total:N0} files · {Format.Bytes(p.BytesWritten)}"
            : $"{p.Stage}…";
    });

    private async Task RunImportAsync(Func<CancellationToken, Task<ImportResult>> import)
    {
        Reset();
        IsImporting = true;
        ImportStatus = "Starting import…";
        _importCts = new CancellationTokenSource();
        try
        {
            var result = await Task.Run(() => import(_importCts.Token));
            foreach (var w in result.Warnings) ImportWarnings.Add(w);
            await LoadProjectAsync(result.Project);
        }
        catch (ImportRejectedException ex) { ImportError = ex.Message; }
        catch (OperationCanceledException) { ImportError = "Import cancelled. Nothing was kept."; }
        catch (Exception ex) { ImportError = "Import failed: " + ex.Message; }
        finally
        {
            IsImporting = false;
            _importCts.Dispose();
            _importCts = null;
            await LoadRecentProjectsAsync();
        }
    }

    private async Task LoadProjectAsync(ProjectRecord project)
    {
        Project = project;
        await _projectService.MarkOpenedAsync(project);
        if (project.Analysis is null || !Directory.Exists(project.SourceDirectory))
        {
            if (!Directory.Exists(project.SourceDirectory))
            {
                ImportError = $"The workspace for “{project.Name}” no longer exists. Import the project again.";
                Project = null;
                return;
            }
            await ReinspectAsync();
        }
        else
        {
            ApplyAnalysis(project.Analysis);
        }
        Step = WizardStep.Inspect;
        UpdateSteps();
    }

    private async Task LoadRepeatAsync(BuildRecord previous)
    {
        var project = await _projects.GetAsync(previous.ProjectId);
        if (project is null || !Directory.Exists(project.SourceDirectory))
        {
            await _dialogs.AlertAsync("Project unavailable", "The project for this build was removed. Import it again to rebuild.");
            return;
        }
        Project = project;
        ApplyAnalysis(project.Analysis ?? await _projectService.InspectAsync(project, CancellationToken.None),
            previous.Settings.Native is { Kind: NativeProjectKind.Theos } ? HCG.ExecutableMaker.ProjectInspection.BuildRoutes.TheosDeb : null);
        SelectedProject = ProjectOptions.FirstOrDefault(p => p.Path == previous.Settings.ProjectRelativePath) ?? SelectedProject;
        await PrepareConfigurationAsync();
        ApplySettings(previous.Settings);
        PreparePackaging(previous.Settings.PackagingFormats);
        PrepareSummary();
        TrustAcknowledged = false;
        Step = WizardStep.Build;
        UpdateSteps();
    }

    // ───────────── Step 2: Inspect ─────────────

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasAnalysis), nameof(IsMobileProject))] private ProjectAnalysis? _analysis;
    /// <summary>Mobile projects and artifacts are built in Mobile Build Studio, not in this desktop wizard.</summary>
    public bool IsMobileProject => Analysis?.Mobile is not null;

    /// <summary>The inspection result as stored with the project; <see cref="Analysis"/> is the chosen build route's view of it.</summary>
    private ProjectAnalysis? _inspectedAnalysis;
    private bool _applyingBuildRoutes;
    /// <summary>The build routes of a folder with more than one kind of project (e.g. "iOS app (IPA, Xcode)" and "Jailbreak package (.deb, Theos)").</summary>
    public ObservableCollection<HCG.ExecutableMaker.ProjectInspection.BuildRoute> BuildRouteOptions { get; } = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(BuildRouteDescription))] private HCG.ExecutableMaker.ProjectInspection.BuildRoute? _selectedBuildRoute;
    public bool HasBuildRoutes => BuildRouteOptions.Count > 1;
    public string BuildRouteDescription => SelectedBuildRoute?.Description ?? "";

    partial void OnSelectedBuildRouteChanged(HCG.ExecutableMaker.ProjectInspection.BuildRoute? value)
    {
        if (_applyingBuildRoutes) return;
        // Another route is another build: identity, platform and packaging are prepared again for it.
        _identityPrefilledFor = null;
        ApplyRouteAnalysis();
    }

    private void ClearBuildRoutes()
    {
        _applyingBuildRoutes = true;
        try { BuildRouteOptions.Clear(); SelectedBuildRoute = null; }
        finally { _applyingBuildRoutes = false; }
        OnPropertyChanged(nameof(HasBuildRoutes));
    }

    [RelayCommand]
    private void OpenInMobileStudio()
    {
        if (Project is not null) _navigation.Navigate("mobile", Project);
    }
    [ObservableProperty] private StatusTag _supportTag = new("—", "neutral");
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(NextCommand))] private ProjectChoice? _selectedProject;
    [ObservableProperty] private bool _isInspecting;
    public bool HasAnalysis => Analysis is not null;
    public ObservableCollection<ProjectChoice> ProjectOptions { get; } = [];
    public ObservableCollection<DiagnosticItem> Diagnostics { get; } = [];
    public ObservableCollection<RequirementItem> Requirements { get; } = [];
    public ObservableCollection<string> MissingDependencies { get; } = [];
    public ObservableCollection<SummaryRow> AnalysisRows { get; } = [];
    [ObservableProperty] private bool _hasDiagnostics;
    [ObservableProperty] private bool _hasMissing;

    public bool CanConfigure => IsNativeProject
        ? NativeReadyToConfigure
        : IsSwiftProject
        ? SwiftReadyToConfigure
        : IsNodeProject
        ? ElectronReadyToConfigure
        : Analysis?.Support == SupportLevel.Supported && SelectedProject?.Project.CompatiblePlatforms.Count > 0;

    partial void OnSelectedProjectChanged(ProjectChoice? value)
    {
        RefreshSwiftPackage();
        RefreshElectronProject();
        RefreshDesktopConversion();
        RefreshNativeProject();
        OnPropertyChanged(nameof(CanConfigure));
    }

    [RelayCommand]
    private async Task ReinspectAsync()
    {
        if (Project is null) return;
        IsInspecting = true;
        try
        {
            await _toolchains.GetAsync(refresh: true);
            ApplyAnalysis(await _projectService.InspectAsync(Project, CancellationToken.None));
        }
        catch (Exception ex) { await _dialogs.AlertAsync("Inspection failed", ex.Message); }
        finally { IsInspecting = false; }
    }

    /// <summary>
    /// Shows an inspection result. When the folder holds more than one kind of project (an Xcode iOS app and a Theos
    /// jailbreak project), the user chooses the build route and the wizard shows that route's own analysis.
    /// </summary>
    private void ApplyAnalysis(ProjectAnalysis a, string? routeId = null)
    {
        _inspectedAnalysis = a;
        var keep = routeId ?? SelectedBuildRoute?.Id;
        _applyingBuildRoutes = true;
        try
        {
            BuildRouteOptions.Clear();
            foreach (var r in HCG.ExecutableMaker.ProjectInspection.BuildRoutes.For(a)) BuildRouteOptions.Add(r);
            SelectedBuildRoute = BuildRouteOptions.FirstOrDefault(r => r.Id == keep) ?? BuildRouteOptions.FirstOrDefault();
        }
        finally { _applyingBuildRoutes = false; }
        OnPropertyChanged(nameof(HasBuildRoutes));
        ApplyRouteAnalysis();
    }

    private void ApplyRouteAnalysis()
    {
        if (_inspectedAnalysis is null) return;
        ApplyAnalysisView(HCG.ExecutableMaker.ProjectInspection.BuildRoutes.View(_inspectedAnalysis, SelectedBuildRoute?.Id, _toolchains.Latest));
    }

    private void ApplyAnalysisView(ProjectAnalysis a)
    {
        Analysis = a;
        SupportTag = new StatusTag(Format.Label(a.Support), Format.Tone(a.Support));
        AnalysisRows.Clear();
        AnalysisRows.Add(new("Project name", string.IsNullOrWhiteSpace(a.ProjectName) ? Project?.Name ?? "—" : a.ProjectName));
        AnalysisRows.Add(new("Language", a.Language));
        AnalysisRows.Add(new("Framework", a.Framework));
        AnalysisRows.Add(new("Runtime", a.Runtime ?? "—"));
        AnalysisRows.Add(new("Application type", a.ApplicationType));
        if (a is { InspectorId: "node", Node: { } na })
        {
            AnalysisRows.Add(new("Node.js projects", string.Join("\n", na.Projects.Select(p =>
                $"{p.DisplayDirectory} — {HCG.ExecutableMaker.ProjectInspection.Node.NodeProjectInspector.KindLabel(p.Kind)}"))));
        }
        else if (a is { InspectorId: "native", Native: { } cn })
        {
            AnalysisRows.Add(new("C/C++ projects", string.Join("\n", cn.Projects.Select(p =>
                $"{p.DisplayDirectory} — {HCG.ExecutableMaker.ProjectInspection.Native.NativeCapabilities.KindLabel(p.Kind)}" +
                (p.Configured is { } c ? $" (configured: {c.Executables.Count()} executable(s), {c.Libraries.Count()} librar{(c.Libraries.Count() == 1 ? "y" : "ies")})" : "")))));
        }
        else if (a.Swift is { } sa)
        {
            AnalysisRows.Add(new("Swift packages", sa.Packages.Count > 0 ? string.Join(", ", sa.Packages.Select(p => p.DisplayDirectory)) : "None"));
            if (sa.MacXcodeProjects.Count > 0) AnalysisRows.Add(new("macOS Xcode projects", string.Join(", ", sa.MacXcodeProjects) + " (not built in this version)"));
        }
        else
        {
            AnalysisRows.Add(new("Solution", a.SolutionFiles.Count > 0 ? string.Join(", ", a.SolutionFiles) : "None (project files only)"));
            AnalysisRows.Add(new("Projects found", a.Projects.Count.ToString()));
        }
        if (a.GlobalJsonSdkVersion is { } gj) AnalysisRows.Add(new("global.json SDK", $"{gj} (rollForward: {a.GlobalJsonRollForward ?? "patch"})"));
        var outputsNote = a is { InspectorId: "node", Node: { } nn } && nn.Projects.All(p => p.Kind != NodeProjectKind.ElectronDesktop) && a.RecommendedFormats.Count > 0
            ? " — after Create desktop application" : "";
        AnalysisRows.Add(new("Recommended outputs", a.RecommendedFormats.Count > 0 ? string.Join(", ", a.RecommendedFormats.Select(f => f.DisplayName())) + outputsNote : "— (nothing here can produce a desktop app as it is)"));
        AnalysisRows.Add(new("Icons found", a.DetectedIcons.Count > 0 ? string.Join(", ", a.DetectedIcons.Take(3)) : "None"));

        ProjectOptions.Clear();
        // Runnable apps, plus browser apps (each labelled with what Create desktop application can do with it).
        var webApps = a.Node?.Projects.Where(p => p.Kind == NodeProjectKind.WebApp).Select(p => p.ManifestPath).ToHashSet(StringComparer.Ordinal) ?? [];
        // C/C++: every project is listed — executable targets, Makefile/source projects to configure, and Theos or
        // unsupported projects (explained, never buildable). Libraries are not entries: they are listed in the panel.
        var native = a.InspectorId == "native";
        foreach (var p in a.Projects.Where(p => native || p.IsRunnableApplication || p.IsDesktopConversionCandidate || webApps.Contains(p.RelativePath))) ProjectOptions.Add(new ProjectChoice(p));
        // Several Swift packages: the user chooses (never silently the first). Otherwise keep the existing behaviour.
        SelectedProject = ProjectOptions.FirstOrDefault(p => p.Path == a.PrimaryProjectPath)
                          ?? (a.Swift is { Packages.Count: > 0 } || (a.InspectorId is "node" or "native" && ProjectOptions.Count > 1) ? null : ProjectOptions.FirstOrDefault());

        Diagnostics.Clear();
        foreach (var d in a.Diagnostics.OrderByDescending(d => d.Severity)) Diagnostics.Add(new DiagnosticItem(d));
        HasDiagnostics = Diagnostics.Count > 0;
        Requirements.Clear();
        foreach (var r in a.RequiredToolchains) Requirements.Add(new RequirementItem(r));
        MissingDependencies.Clear();
        foreach (var m in a.MissingDependencies) MissingDependencies.Add(m);
        HasMissing = MissingDependencies.Count > 0;
        RefreshSwiftPackage();
        RefreshElectronProject();
        RefreshDesktopConversion();
        RefreshNativeProject();
        OnPropertyChanged(nameof(ProjectPickerPlaceholder));
        OnPropertyChanged(nameof(CanConfigure));
        NextCommand.NotifyCanExecuteChanged();
    }

    // ───────────── Step 3: Configure ─────────────

    [ObservableProperty] private string _appName = "";
    [ObservableProperty] private string _publisher = "";
    [ObservableProperty] private string _version = "1.0.0";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _appId = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasIcon), nameof(IconFileName))] private string? _iconPath;
    [ObservableProperty] private Bitmap? _iconPreview;
    [ObservableProperty] private string _iconSummary = "";
    public ObservableCollection<IconNote> IconNotes { get; } = [];
    public bool HasIcon => IconPath is not null;
    public string IconFileName => IconPath is null ? "No icon selected" : Path.GetFileName(IconPath);

    public ObservableCollection<Choice<TargetPlatform>> PlatformOptions { get; } = [];
    [ObservableProperty] private Choice<TargetPlatform>? _selectedPlatform;
    public ObservableCollection<Choice<TargetArchitecture>> ArchitectureOptions { get; } =
        [new(TargetArchitecture.X64, "x64 (Intel / AMD)"), new(TargetArchitecture.Arm64, "ARM64 (Apple Silicon / Windows on ARM)")];
    [ObservableProperty] private Choice<TargetArchitecture>? _selectedArchitecture;
    public IReadOnlyList<Choice<BuildConfigurationKind>> ConfigurationOptions { get; } =
        [new(BuildConfigurationKind.Release, "Release"), new(BuildConfigurationKind.Debug, "Debug")];
    [ObservableProperty] private Choice<BuildConfigurationKind>? _selectedConfiguration;
    [ObservableProperty] private bool _selfContained = true;
    [ObservableProperty] private bool _singleFile;
    [ObservableProperty] private string _outputDirectory = "";
    [ObservableProperty] private decimal? _timeoutMinutes = 30;
    public ObservableCollection<string> ConfigErrors { get; } = [];
    [ObservableProperty] private bool _hasConfigErrors;
    [ObservableProperty] private string _platformNote = "";

    private async Task PrepareConfigurationAsync()
    {
        var detected = SelectedProject?.Project;
        if (detected is null) return;
        var settings = await _settings.LoadAsync();
        var swift = IsSwiftBuild;
        if (!IsElectronBuild && !IsNativeBuild) SetArchitectureOptions(swift);
        OnPropertyChanged(nameof(IsSwiftBuild));
        OnPropertyChanged(nameof(IsDotNetBuild));
        OnPropertyChanged(nameof(IsSwiftGui));
        var key = Project?.Id + "|" + detected.RelativePath + (swift ? "|" + SelectedSwiftProduct?.Name : "");
        OnPropertyChanged(nameof(IsElectronBuild));
        OnPropertyChanged(nameof(IdentityEditable));
        OnPropertyChanged(nameof(ShowConfigurationChoice));
        if (IsNativeBuild)
        {
            PrepareNativeConfiguration(key, settings);
        }
        else if (IsElectronBuild)
        {
            if (_identityPrefilledFor != key)
            {
                _identityPrefilledFor = key;
                Publisher = settings.LastPublisher ?? "";
                OutputDirectory = settings.DefaultOutputDirectory ?? _paths.DefaultOutputDirectory;
                TimeoutMinutes = Math.Max(settings.BuildTimeoutMinutes, 45);
                PrepareElectronConfiguration(prefillIdentity: true);
            }
            else PrepareElectronConfiguration(prefillIdentity: false);
        }
        else if (_identityPrefilledFor != key && swift)
        {
            _identityPrefilledFor = key;
            Publisher = settings.LastPublisher ?? "";
            OutputDirectory = settings.DefaultOutputDirectory ?? _paths.DefaultOutputDirectory;
            TimeoutMinutes = settings.BuildTimeoutMinutes;
            SelectedConfiguration = ConfigurationOptions[0];
            SelectedArchitecture = ArchitectureOptions.First(a => a.Value == HostInfo.CurrentArchitecture);
            IconPath = null;
            PrefillSwift(SelectedSwiftPackage!, SelectedSwiftProduct!);
        }
        else if (_identityPrefilledFor != key)
        {
            _identityPrefilledFor = key;
            AppName = detected.Product ?? detected.AssemblyName ?? detected.Name;
            Publisher = detected.Company ?? settings.LastPublisher ?? "";
            Version = detected.Version is { } v && Regex.IsMatch(v, @"^\d+(\.\d+){1,3}") ? v : "1.0.0";
            Description = detected.Description ?? "";
            AppId = SuggestAppId(Publisher, AppName);
            OutputDirectory = settings.DefaultOutputDirectory ?? _paths.DefaultOutputDirectory;
            TimeoutMinutes = settings.BuildTimeoutMinutes;
            SelectedConfiguration = ConfigurationOptions[0];
            SelectedArchitecture = ArchitectureOptions.First(a => a.Value == HostInfo.CurrentArchitecture);
            IconPath = null;
            if (detected.ApplicationIcon is { } projectIcon && Project is not null)
            {
                var full = Path.Combine(Project.SourceDirectory, projectIcon);
                if (File.Exists(full)) IconPath = full;
            }
            else if (Analysis?.DetectedIcons.FirstOrDefault(i => i.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) is { } png && Project is not null)
            {
                var full = Path.Combine(Project.SourceDirectory, png);
                if (File.Exists(full)) IconPath = full;
            }
        }

        PlatformOptions.Clear();
        foreach (var p in IsTheosBuild ? [TargetPlatform.IOS] : new[] { TargetPlatform.Windows, TargetPlatform.MacOS, TargetPlatform.Linux, TargetPlatform.Android })
        {
            var compatible = detected.CompatiblePlatforms.Contains(p);
            var note = p == TargetPlatform.Android ? "not implemented" : compatible ? null
                : IsNativeBuild ? $"build on a {p.DisplayName()} computer with its toolchain" : "not supported by this project";
            var name = p == TargetPlatform.Linux ? "Linux (portable preview)" : p == TargetPlatform.IOS ? "iOS — jailbroken devices (.deb, not an IPA)" : p.DisplayName();
            PlatformOptions.Add(new Choice<TargetPlatform>(p, name, compatible && p != TargetPlatform.Android, note));
        }
        SelectedPlatform = PlatformOptions.FirstOrDefault(o => o.IsEnabled && o.Value == SelectedPlatform?.Value)
                           ?? PlatformOptions.FirstOrDefault(o => o.IsEnabled && o.Value == HostInfo.CurrentPlatform)
                           ?? PlatformOptions.FirstOrDefault(o => o.IsEnabled);
        Validate();
    }

    private void ApplySettings(BuildSettings s)
    {
        AppName = s.Identity.ApplicationName;
        Publisher = s.Identity.Publisher;
        Version = s.Identity.Version;
        Description = s.Identity.Description;
        AppId = s.Identity.ApplicationId;
        IconPath = s.Identity.IconSourcePath is { } i && File.Exists(i) ? i : null;
        SelectedPlatform = PlatformOptions.FirstOrDefault(p => p.Value == s.Platform && p.IsEnabled) ?? SelectedPlatform;
        if (s.Swift is { } swiftOptions) ApplySwiftSettings(swiftOptions);
        if (s.Native is { } nativeOptions) ApplyNativeSettings(nativeOptions);
        _repeatDistribution = s.MacDistribution;
        SelectedArchitecture = ArchitectureOptions.FirstOrDefault(a => a.Value == s.Architecture) ?? SelectedArchitecture;
        SelectedConfiguration = ConfigurationOptions.First(c => c.Value == s.Configuration);
        SelfContained = s.SelfContained;
        SingleFile = s.SingleFile;
        SelectedInstallScope = InstallScopeOptions.First(o => o.Value == s.WindowsInstaller.Scope);
        InstallFolderName = s.WindowsInstaller.InstallFolderName ?? "";
        OfferDesktopShortcut = s.WindowsInstaller.OfferDesktopShortcut;
        DesktopShortcutChecked = s.WindowsInstaller.DesktopShortcutCheckedByDefault;
        OfferLaunchAfterInstall = s.WindowsInstaller.OfferLaunchAfterInstall;
        OutputDirectory = s.OutputDirectory;
        TimeoutMinutes = (decimal)s.Timeout.TotalMinutes;
        Validate();
    }

    internal static string SuggestAppId(string publisher, string name)
    {
        static string Part(string s, string fallback)
        {
            var cleaned = new string(s.ToLowerInvariant().Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').ToArray()).Trim('-');
            if (cleaned.Length == 0) cleaned = fallback;
            return char.IsAsciiLetter(cleaned[0]) ? cleaned : "a" + cleaned;
        }
        return $"com.{Part(publisher, "example")}.{Part(name, "app")}";
    }

    partial void OnAppNameChanged(string value) => Validate();
    partial void OnVersionChanged(string value) => Validate();
    partial void OnAppIdChanged(string value) => Validate();
    partial void OnOutputDirectoryChanged(string value) => Validate();
    partial void OnTimeoutMinutesChanged(decimal? value) => Validate();
    partial void OnSelectedPlatformChanged(Choice<TargetPlatform>? value)
    {
        PlatformNote = value?.Value switch
        {
            _ when IsTheosBuild => "Theos builds the package on this computer for jailbroken iPhones and iPads. The package scheme (rootless / rootful), deployment target and architectures come from the project; HCG does not change them or claim compatibility beyond them.",
            _ when IsNativeBuild => HostInfo.CurrentPlatform == TargetPlatform.MacOS
                ? "C/C++ builds run on this Mac with its own compilers: arm64, x86_64, or both (universal, when every dependency is universal too). Windows, Linux and iOS binaries need those platforms' toolchains."
                : $"C/C++ builds run on this computer with its own compilers, for {HostInfo.CurrentPlatform.DisplayName()} {HostInfo.CurrentArchitecture.DisplayName()} only. Other platforms or architectures need their own toolchains, which HCG does not set up.",
            TargetPlatform.Windows when HostInfo.CurrentPlatform != TargetPlatform.Windows =>
                "Cross-building for Windows from this host: the .exe is produced, but its embedded file icon requires a Windows build host.",
            TargetPlatform.MacOS when HostInfo.CurrentPlatform != TargetPlatform.MacOS =>
                "Cross-building for macOS: a valid .app bundle is assembled and structurally verified; launch testing and DMG creation require a Mac.",
            TargetPlatform.Linux when IsElectronBuild => "Linux output is electron-builder's unpacked app folder (its dir target). AppImage, deb and rpm targets are not run by HCG in this version.",
            TargetPlatform.MacOS when IsElectronBuild => "electron-builder builds the .app (and the zip/dmg you choose) with the project's configuration, including its signing identity.",
            TargetPlatform.Linux => "Linux output is a portable self-contained folder/ZIP. AppImage, DEB and RPM are planned.",
            _ => "",
        };
        Validate();
    }
    partial void OnSelectedArchitectureChanged(Choice<TargetArchitecture>? value) => Validate();
    partial void OnIconPathChanged(string? value) => LoadIconPreview(value);

    private void Validate()
    {
        ConfigErrors.Clear();
        if (SelectedPlatform is not { IsEnabled: true }) ConfigErrors.Add("Choose a supported target platform.");
        if (SelectedArchitecture is null) ConfigErrors.Add("Choose a target architecture.");
        if (TimeoutMinutes is not (>= 1 and <= 720)) ConfigErrors.Add("Timeout must be between 1 and 720 minutes.");
        if (IconNotes.Any(n => n.IsError)) ConfigErrors.Add("The selected icon is invalid.");
        if (ConfigErrors.Count == 0 && SelectedProject is not null && IsNativeBuild)
        {
            ValidateNative();
        }
        else if (ConfigErrors.Count == 0 && SelectedProject is not null && IsElectronBuild)
        {
            ValidateElectron();
        }
        else if (ConfigErrors.Count == 0 && SelectedProject is not null && IsSwiftBuild)
        {
            ValidateSwift(CreateSettings([PackagingFormat.MacAppBundle]));
        }
        else if (ConfigErrors.Count == 0 && SelectedProject is not null)
        {
            try { DotNetBuildPlanner.Validate(CreateSettings([PackagingFormat.PortableFolder])); }
            catch (BuildValidationException ex) { ConfigErrors.Add(ex.Message); }
        }
        HasConfigErrors = ConfigErrors.Count > 0;
        NextCommand.NotifyCanExecuteChanged();
    }

    private void LoadIconPreview(string? path)
    {
        IconNotes.Clear();
        IconPreview = null;
        IconSummary = "";
        if (path is null) { Validate(); return; }
        var info = IconInspector.Inspect(path);
        foreach (var e in info.Errors) IconNotes.Add(new IconNote(e, "error"));
        foreach (var w in info.Warnings) IconNotes.Add(new IconNote(w, "warning"));
        if (info.IsValid)
        {
            var sizes = string.Join(", ", info.Images.Select(i => $"{i.Width}×{i.Height}").Distinct().Take(8));
            IconSummary = $"{info.Format} · {sizes}{(info.HasTransparency == true ? " · transparent" : "")}";
            try
            {
                using var stream = File.OpenRead(path);
                IconPreview = new Bitmap(stream);
            }
            catch (Exception) { IconSummary += " · preview unavailable for this format"; }
        }
        Validate();
    }

    [RelayCommand]
    private async Task BrowseIconAsync()
    {
        if (await _shell.PickIconAsync() is { } icon) IconPath = icon;
    }

    [RelayCommand] private void ClearIcon() => IconPath = null;

    [RelayCommand]
    private async Task BrowseOutputAsync()
    {
        if (await _shell.PickFolderAsync("Choose output directory") is { } dir) OutputDirectory = dir;
    }

    private BuildSettings CreateSettings(IReadOnlyList<PackagingFormat> formats) => new()
    {
        ProjectRelativePath = SelectedProject?.Path ?? "",
        Platform = SelectedPlatform?.Value ?? TargetPlatform.Windows,
        Architecture = SelectedArchitecture?.Value ?? TargetArchitecture.X64,
        Configuration = SelectedConfiguration?.Value ?? BuildConfigurationKind.Release,
        SelfContained = SelfContained,
        SingleFile = SingleFile,
        OutputDirectory = OutputDirectory.Trim(),
        Timeout = TimeSpan.FromMinutes((double)(TimeoutMinutes ?? 30)),
        PackagingFormats = formats,
        WindowsInstaller = CreateInstallerOptions(),
        Swift = CreateSwiftOptions(),
        Electron = CreateElectronOptions(formats),
        Native = IsNativeBuild ? CreateNativeOptions() : null,
        MacDistribution = CreateDistributionOptions(),
        Identity = new AppIdentity
        {
            ApplicationName = AppName.Trim(),
            Publisher = Publisher.Trim(),
            Version = Version.Trim(),
            Description = Description.Trim(),
            ApplicationId = AppId.Trim(),
            IconSourcePath = IconPath,
        },
    };

    // ───────────── Step 4: Package ─────────────

    public ObservableCollection<PackagingOptionItem> PackagingOptions { get; } = [];

    private PackagingContext BasePackagingContext()
    {
        var detected = SelectedProject?.Project;
        var isGui = detected is not null && (detected.IsAvalonia || string.Equals(detected.OutputType, "WinExe", StringComparison.OrdinalIgnoreCase));
        var swiftKind = IsSwiftBuild ? SelectedSwiftKind?.Value ?? SwiftAppKind.GuiApp : (SwiftAppKind?)null;
        if (swiftKind is { } k) isGui = k == SwiftAppKind.GuiApp;
        return new PackagingContext(SelectedPlatform?.Value ?? TargetPlatform.Windows, HostInfo.Detect(), _toolchains.Latest, isGui, SelfContained, swiftKind)
        {
            IsElectron = IsElectronBuild,
            IsNative = IsNativeBuild && !IsTheosBuild,
            IsTheos = IsTheosBuild,
            IsNativeMacBundle = IsNativeBuild && IsNativeMacBundle,
        };
    }

    private void PreparePackaging(IReadOnlyList<PackagingFormat>? preselect = null)
    {
        var baseCtx = BasePackagingContext();
        var swiftKind = baseCtx.SwiftKind;
        var ctx = DistributionContext(baseCtx);
        OnPropertyChanged(nameof(ShowDistribution));
        OnPropertyChanged(nameof(IsSwiftGui));
        if (IsElectronBuild)
            preselect ??= RecordedElectronFormats() ?? DefaultElectronFormats();
        if (swiftKind is not null)
            preselect ??= swiftKind == SwiftAppKind.GuiApp ? [PackagingFormat.MacAppBundle] : [PackagingFormat.PortableZip];
        if (IsNativeBuild)
            preselect ??= DefaultNativeFormats();
        foreach (var old in PackagingOptions) old.PropertyChanged -= OnPackagingOptionChanged;
        PackagingOptions.Clear();
        var recommended = preselect ?? Analysis?.RecommendedFormats ?? [];
        foreach (var option in PackagingCatalog.Evaluate(ctx))
        {
            var item = new PackagingOptionItem(option) { IsSelected = option.IsAvailable && recommended.Contains(option.Format) };
            item.PropertyChanged += OnPackagingOptionChanged;
            PackagingOptions.Add(item);
        }
        if (!PackagingOptions.Any(o => o.IsSelected) && PackagingOptions.FirstOrDefault(o => o.IsEnabled) is { } first) first.IsSelected = true;
        NextCommand.NotifyCanExecuteChanged();
        if (ShowDistribution)
        {
            var keep = _repeatDistribution ?? CreateDistributionOptions();
            _repeatDistribution = null;
            if (_signing is null) _ = RefreshSigningThenApplyAsync(keep);
            else PopulateDistributionChoices(keep);
        }
    }

    private async Task RefreshSigningThenApplyAsync(MacDistributionOptions keep)
    {
        await RefreshSigningAsync();
        PopulateDistributionChoices(keep);
    }

    private void OnPackagingOptionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (Step == WizardStep.Package && ShowDistribution) { ValidateDistribution(); OnPropertyChanged(nameof(ShowPkgOptions)); }
        NextCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ShowInstallerOptions));
    }

    // Windows Setup.exe options (shown when the installer format is selected).
    public bool ShowInstallerOptions => PackagingOptions.Any(o => o.IsSelected && o.IsEnabled && o.Option.Format == PackagingFormat.WindowsInstallerExe);
    public IReadOnlyList<Choice<InstallScope>> InstallScopeOptions { get; } =
    [
        new(InstallScope.CurrentUser, "Current user — no administrator rights (%LOCALAPPDATA%\\Programs)"),
        new(InstallScope.AllUsers, "All users — requires administrator rights (Program Files)"),
    ];
    [ObservableProperty] private Choice<InstallScope>? _selectedInstallScope;
    [ObservableProperty] private string _installFolderName = "";
    [ObservableProperty] private bool _offerDesktopShortcut = true;
    [ObservableProperty] private bool _desktopShortcutChecked;
    [ObservableProperty] private bool _offerLaunchAfterInstall = true;

    private WindowsInstallerOptions CreateInstallerOptions() => new()
    {
        Scope = SelectedInstallScope?.Value ?? InstallScope.CurrentUser,
        InstallFolderName = string.IsNullOrWhiteSpace(InstallFolderName) ? null : InstallFolderName.Trim(),
        OfferDesktopShortcut = OfferDesktopShortcut,
        DesktopShortcutCheckedByDefault = OfferDesktopShortcut && DesktopShortcutChecked,
        OfferLaunchAfterInstall = OfferLaunchAfterInstall,
    };

    private IReadOnlyList<PackagingFormat> SelectedFormats() =>
        PackagingOptions.Where(o => o.IsSelected && o.IsEnabled).Select(o => o.Option.Format).ToList();

    // ───────────── Step 5: Build ─────────────

    public string TrustTitle => TrustPolicy.TrustBoundaryTitle;
    public string TrustExplanation => TrustPolicy.TrustBoundaryExplanation;
    public string TrustLabel => TrustPolicy.AcknowledgementLabel;
    public string SecretsStatement => TrustPolicy.SecretsStatement;
    public string IsolationDescription => _buildService.Worker.Description;
    public ObservableCollection<SummaryRow> Summary { get; } = [];
    public ObservableCollection<LogLineItem> LogLines { get; } = [];
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(StartBuildCommand))] private bool _trustAcknowledged;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(StartBuildCommand), nameof(CancelBuildCommand), nameof(BackCommand))]
    [NotifyPropertyChangedFor(nameof(ShowTrustPanel))]
    private bool _isBuilding;
    [ObservableProperty] private bool _hasBuildStarted;
    [ObservableProperty] private string _currentStage = "Ready";
    [ObservableProperty] private string _elapsed = "0.0s";
    [ObservableProperty] private int _liveWarnings;
    [ObservableProperty] private int _liveErrors;
    [ObservableProperty] private int _stageNumber;
    [ObservableProperty] private bool _isCancelling;
    [ObservableProperty] private bool _logTruncated;
    public bool ShowTrustPanel => !IsBuilding;

    /// <summary>Raised on the UI thread after new log lines are appended (the view scrolls to the end).</summary>
    public event EventHandler? LogAppended;

    private void PrepareSummary()
    {
        var s = CreateSettings(SelectedFormats());
        Summary.Clear();
        Summary.Add(new("Project", $"{Project?.Name} · {s.ProjectRelativePath}"));
        Summary.Add(new("Application", $"{s.Identity.ApplicationName} {s.Identity.Version}"));
        Summary.Add(new("Identifier", s.Identity.ApplicationId));
        Summary.Add(new("Target", TargetLabel(s)));
        if (s.Electron is { } el)
        {
            Summary.Add(new("Electron app", el.ProjectDirectory.Length == 0 ? "(workspace root)" : el.ProjectDirectory + "/"));
            Summary.Add(new("Stages", $"{(el.UseLockfile ? "npm ci" : "npm install")} → {(el.CompileSteps.Count == 0 ? "no compile step" : string.Join(" && ", el.CompileSteps.Select(c => c.Display)))} → electron-builder --{el.Platform} {string.Join(' ', el.Targets)} --publish never"));
            Summary.Add(new("Workflow", el.PackagingScript is { } ps ? $"Follows package.json script \"{ps}\" and {(el.ConfigPath ?? "the project's electron-builder configuration")}" : "electron-builder with the project's configuration (no packaging script)"));
            Summary.Add(new("Signing", s.Platform == TargetPlatform.MacOS
                ? "As the electron-builder configuration says (mac.identity); HCG verifies the signature and does not re-sign."
                : "Not code-signed (Linux)."));
        }
        else if (s.Swift is { } sw)
        {
            Summary.Add(new("Swift package", $"{(sw.PackageDirectory.Length == 0 ? "(workspace root)" : sw.PackageDirectory + "/")} · product {sw.Product} · {(sw.Kind == SwiftAppKind.GuiApp ? "macOS app" : "command-line tool")}"));
            Summary.Add(new("Configuration", $"{s.Configuration} · build number {sw.BuildNumber} · minimum macOS {sw.MinimumMacOS ?? "from the executable"}{(sw.Category is { } c ? " · " + c : "")}"));
            if (sw.UsageDescriptions.Count > 0) Summary.Add(new("Privacy keys", string.Join(", ", sw.UsageDescriptions.Keys)));
            var d = s.MacDistribution;
            Summary.Add(new("Signing", d.SigningMode == MacSigningMode.DeveloperId
                ? $"Developer ID ({SelectedDeveloperIdIdentity?.Label}), hardened runtime, secure timestamp · entitlements: {(d.DeveloperIdEntitlements.Count == 0 ? "none" : string.Join(", ", d.DeveloperIdEntitlements))} · notarization is a separate, explicit step after the build"
                : "Ad-hoc (codesign --sign -). Not Developer ID signed, not notarized."));
            if (s.PackagingFormats.Contains(PackagingFormat.MacPkg))
                Summary.Add(new("Installer", d.PkgInstallerName is { } inst ? $"Signed with {inst}" : "Unsigned .pkg — local testing only"));
            if (s.PackagingFormats.Contains(PackagingFormat.MacAppStore))
                Summary.Add(new("Mac App Store", $"Apple Distribution ({SelectedAppStoreIdentity?.Label}) · installer {d.AppStoreInstallerName} · profile {SelectedAppStoreProfile?.Profile.Name} · " +
                                                 $"sandbox entitlements: {string.Join(", ", new[] { "com.apple.security.app-sandbox" }.Concat(d.AppStoreEntitlements))} · validation and upload stay manual"));
            var disabled = DisabledFeatures().ToList();
            if (disabled.Count > 0) Summary.Add(new("Disabled features", string.Join("; ", disabled)));
        }
        else if (s.Native is { Kind: NativeProjectKind.Theos, TheosScheme: { } th } tn)
        {
            Summary.Add(new("Adapter", "Theos: make package FINALPACKAGE=1 (the project's own makefiles), in a copy of the project"));
            Summary.Add(new("Package configuration", $"{th.Label} · installs under {new TheosScheme { PackageScheme = th.PackageScheme }.InstallPrefix} · Architecture {new TheosScheme { PackageScheme = th.PackageScheme }.DebArchitecture}"));
            Summary.Add(new("Deployment", $"iOS {th.DeploymentTarget ?? "(Theos default)"} and later · SDK iPhoneOS {th.SdkVersion ?? "latest"} · {string.Join(" + ", th.Architectures.DefaultIfEmpty("Theos default"))}"));
            Summary.Add(new("Products", string.Join(", ", th.Instances.Select(i => $"{i.DisplayType} {i.Name}"))));
            Summary.Add(new("Output type", "Debian package (.deb) for jailbroken devices — not an IPA. HCG does not install it or respring."));
            var tc = _toolchains.Latest;
            Summary.Add(new("Toolchain", string.Join(" · ", new[] { ToolchainIds.Theos, ToolchainIds.TheosMake, ToolchainIds.TheosIosToolchain, ToolchainIds.Ldid }
                .Select(id => tc?.Find(id)).OfType<ToolchainInfo>().Select(t => $"{t.DisplayName} {t.Version ?? ""} {t.ExecutablePath}".Trim()))));
        }
        else if (s.Native is { } n)
        {
            Summary.Add(new("C/C++ build", n.Kind switch
            {
                NativeProjectKind.CMake => $"CMake target {n.Target} · {n.BuildType}{(n.Preset is { } pr ? " · preset " + pr : "")}{(n.CacheEntries.Count > 0 ? " · " + string.Join(" ", n.CacheEntries.Select(e => "-D" + e)) : "")} · downloads {(n.AllowDownloads ? "allowed" : "blocked")}",
                NativeProjectKind.Make => $"make {n.Target}{(n.MakeVariables.Count > 0 ? " " + string.Join(" ", n.MakeVariables) : "")} → {n.ExpectedOutput}",
                _ => $"{n.Sources.Count} selected file(s) · {(n.Language == "CXX" ? "C++" : "C")}{n.Standard} · {n.BuildType} · generated CMakeLists.txt in HCG's build folder",
            }));
            Summary.Add(new("Runtime files", n.RuntimeFiles.Count > 0 ? string.Join(", ", n.RuntimeFiles) : "None listed (the project's own shared libraries are copied; others are reported, not bundled)"));
        }
        else
        {
            Summary.Add(new("Configuration", $"{s.Configuration} · {(s.SelfContained ? "self-contained" : "framework-dependent")}{(s.SingleFile ? " · single file" : "")}"));
        }
        Summary.Add(new("Packaging", string.Join(", ", PackagingCatalog.WithDependencies(s.PackagingFormats, s).Select(f => f.DisplayName()))));
        if (s.PackagingFormats.Contains(PackagingFormat.WindowsInstallerExe))
        {
            var w = s.WindowsInstaller;
            Summary.Add(new("Installer", $"{(w.Scope == InstallScope.AllUsers ? "All users (admin)" : "Current user")} · folder “{w.InstallFolderName ?? s.Identity.ApplicationName}” · Start-menu shortcut" +
                $"{(w.OfferDesktopShortcut ? $" · optional desktop shortcut ({(w.DesktopShortcutCheckedByDefault ? "on" : "off")} by default)" : "")}{(w.OfferLaunchAfterInstall ? " · launch after install" : "")} · uninstaller"));
        }
        Summary.Add(new("Icon", IsElectronBuild
            ? SelectedNodeProject?.BuilderConfig?.Source is { } cfg ? $"From the electron-builder configuration ({cfg})" : "From the electron-builder configuration"
            : s.Identity.IconSourcePath is null ? "None" : Path.GetFileName(s.Identity.IconSourcePath)));
        Summary.Add(new("Output", BuildService.OutputRootFor(s)));
        Summary.Add(new("Time limit", $"{s.Timeout.TotalMinutes:0} minutes"));
    }

    private bool CanStartBuild() => TrustAcknowledged && !IsBuilding && Project is not null;

    [RelayCommand(CanExecute = nameof(CanStartBuild))]
    private async Task StartBuildAsync()
    {
        if (Project is null || SelectedProject is null) return;
        var settings = CreateSettings(SelectedFormats());
        try
        {
            if (settings.Swift is not null) HCG.ExecutableMaker.BuildEngine.Swift.SwiftBuildPlanner.Validate(settings, HostInfo.CurrentPlatform);
            else DotNetBuildPlanner.Validate(settings);
        }
        catch (BuildValidationException ex)
        {
            await _dialogs.AlertAsync("Configuration problem", ex.Message);
            return;
        }

        var allowOverwrite = false;
        var outputRoot = BuildService.OutputRootFor(settings);
        if (Directory.Exists(outputRoot) && Directory.EnumerateFileSystemEntries(outputRoot).Any())
        {
            if (!File.Exists(Path.Combine(outputRoot, BuildService.OutputMarkerFile)))
            {
                await _dialogs.AlertAsync("Output folder in use",
                    $"“{outputRoot}” already exists and was not created by HCG Executable Maker. It will not be replaced.\n\nChoose another output directory or change the version.");
                return;
            }
            allowOverwrite = await _dialogs.ConfirmAsync("Replace previous output?",
                $"A previous HCG build of {settings.Identity.ApplicationName} {settings.Identity.Version} for {TargetLabel(settings)} exists:\n\n{outputRoot}\n\nReplace it with this build?",
                "Replace and build", destructive: true);
            if (!allowOverwrite) return;
        }

        var appSettings = await _settings.LoadAsync();
        if (!string.IsNullOrWhiteSpace(settings.Identity.Publisher) && appSettings.LastPublisher != settings.Identity.Publisher)
            await _settings.SaveAsync(appSettings with { LastPublisher = settings.Identity.Publisher });

        LogLines.Clear();
        while (_pendingLog.TryDequeue(out _)) { }
        LogTruncated = false;
        LiveWarnings = LiveErrors = 0;
        StageNumber = 0;
        CurrentStage = "Starting…";
        StageHistory.Clear();
        IsCancelling = false;
        IsBuilding = true;
        HasBuildStarted = true;
        _buildCts = new CancellationTokenSource();
        _buildClock.Restart();
        _uiTimer.Start();
        var observer = new Observer(this);
        BuildRecord? result = null;
        try
        {
            var request = new BuildRequest
            {
                Project = Project,
                Settings = settings,
                TrustAcknowledged = TrustAcknowledged,
                AllowOverwriteOutput = allowOverwrite,
                AssemblyName = SelectedProject.Project.AssemblyName,
            };
            var token = _buildCts.Token;
            result = await Task.Run(() => _buildService.RunAsync(request, observer, token));
        }
        catch (Exception ex)
        {
            await _dialogs.AlertAsync("Build could not run", ex.Message);
        }
        finally
        {
            _buildClock.Stop();
            _uiTimer.Stop();
            FlushLog();
            IsBuilding = false;
            IsCancelling = false;
            _buildCts.Dispose();
            _buildCts = null;
        }
        if (result is not null) ShowResult(result);
    }

    private bool CanCancelBuild() => IsBuilding;

    [RelayCommand(CanExecute = nameof(CanCancelBuild))]
    private void CancelBuild()
    {
        IsCancelling = true;
        CurrentStage = "Cancelling — terminating build processes…";
        _buildCts?.Cancel();
    }

    private void FlushLog()
    {
        Elapsed = Format.Duration(_buildClock.Elapsed);
        if (_pendingLog.IsEmpty) return;
        var added = 0;
        while (_pendingLog.TryDequeue(out var line) && added < 2000)
        {
            LogLines.Add(new LogLineItem(line));
            added++;
        }
        while (LogLines.Count > MaxDisplayedLogLines)
        {
            LogLines.RemoveAt(0);
            LogTruncated = true;
        }
        LogAppended?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Every stage this build went through, in order (install, compile, package, verify, …).</summary>
    public ObservableCollection<string> StageHistory { get; } = [];

    private sealed class Observer(BuildWizardViewModel vm) : IBuildObserver
    {
        public void OnLog(BuildLogLine line) => vm._pendingLog.Enqueue(line);

        public void OnStage(BuildStage stage, string description) => Dispatcher.UIThread.Post(() =>
        {
            if (vm.IsCancelling) return;
            vm.StageNumber = (int)stage;
            vm.CurrentStage = description;
            vm.StageHistory.Add(description);
        });

        public void OnMessage(BuildMessage message) => Dispatcher.UIThread.Post(() =>
        {
            if (message.Severity == DiagnosticSeverity.Warning) vm.LiveWarnings++;
            if (message.Severity == DiagnosticSeverity.Error) vm.LiveErrors++;
        });
    }

    // ───────────── Step 6: Results ─────────────

    [ObservableProperty] private BuildRecord? _result;
    [ObservableProperty] private StatusTag _resultTag = new("—", "neutral");
    [ObservableProperty] private string _resultTitle = "";
    [ObservableProperty] private string _resultDetail = "";
    [ObservableProperty] private string _resultDuration = "";
    [ObservableProperty] private string _resultOutput = "";
    [ObservableProperty] private bool _resultSucceeded;
    [ObservableProperty] private bool _hasResultMessages;
    [ObservableProperty] private bool _hasResultArtifacts;
    public ObservableCollection<ArtifactItem> ResultArtifacts { get; } = [];
    public ObservableCollection<MessageItem> ResultMessages { get; } = [];

    private void ShowResult(BuildRecord build)
    {
        Result = build;
        ResultSucceeded = build.Status == BuildStatus.Succeeded;
        ResultTag = new StatusTag(Format.Label(build.Status), Format.Tone(build.Status));
        ResultTitle = build.Status switch
        {
            BuildStatus.Succeeded => "Build succeeded",
            BuildStatus.Cancelled => "Build cancelled",
            BuildStatus.TimedOut => "Build timed out",
            _ => "Build failed",
        };
        var notarized = build.Artifacts.Any(a => a.Checks.Any(c => c.Name == "Notarization" && c.Status == VerificationStatus.Passed));
        ResultDetail = build.Status == BuildStatus.Succeeded
            ? $"{build.Artifacts.Count} artifact(s) generated and verified · {build.WarningCount} warning(s). " +
              (build.Settings.Native is { Kind: NativeProjectKind.Theos }
                ? "Debian package for jailbroken devices, checked by HCG (metadata, install prefix, payload, binaries). It was not installed: copy it to the device and install it yourself (INSTALL.txt next to the package explains how). Not an IPA."
               : build.Settings.Native is { } nb
                ? (nb.IsMacBundle ? "The project's .app was kept as built and signed in a separate stage (see its Code signature check); not notarized."
                                  : "Not code-signed: HCG does not sign C/C++ executables on this platform.") +
                  " HCG did not run the program. Runtime libraries are listed in RUNTIME-DEPENDENCIES.txt; a binary is portable only if every non-system library it needs ships with it."
               : build.Settings.Electron is not null ? "Built with the project's electron-builder workflow" + (build.Settings.Platform == TargetPlatform.MacOS ? "; signed as its configuration says (see each artifact's Code signature check)." : ".")
               : build.Settings.Swift is null ? "Artifacts are unsigned (ad-hoc)."
               : build.Settings.MacDistribution.SigningMode == MacSigningMode.DeveloperId
                   ? notarized ? "Developer ID signed and notarized (see each artifact's checks)." : "Developer ID signed; not notarized yet (Notarize below)."
                   : "Ad-hoc signed only: not Developer ID signed or notarized.") +
              (build.Settings.Native is not null ? "" : build.Settings.Electron is null ? " Installation and launch are tested only when you run Test install." : " Launching the app is not part of the build.")
            : build.FailureReason ?? "The build did not complete.";
        _ = PrepareDistributionActionsAsync(build);
        ResultDuration = Format.Duration(build.Duration);
        ResultOutput = build.Artifacts.Count > 0 ? BuildService.OutputRootFor(build.Settings) : "No output produced";
        ResultArtifacts.Clear();
        foreach (var a in build.Artifacts) ResultArtifacts.Add(new ArtifactItem(a, build));
        HasResultArtifacts = ResultArtifacts.Count > 0;
        ResultMessages.Clear();
        foreach (var m in build.Messages.OrderByDescending(m => m.Severity).Take(200)) ResultMessages.Add(new MessageItem(m));
        HasResultMessages = ResultMessages.Count > 0;
        Step = WizardStep.Results;
        UpdateSteps();
    }

    [RelayCommand]
    private async Task OpenOutputFolderAsync()
    {
        if (Result is null) return;
        var target = Result.Artifacts.Count > 0 ? BuildService.OutputRootFor(Result.Settings) : Result.Settings.OutputDirectory;
        if (!await _shell.OpenFolderAsync(target))
            await _dialogs.AlertAsync("Folder not found", $"The output folder does not exist:\n{target}");
    }

    [RelayCommand] private async Task CopyArtifactPathAsync(ArtifactItem item) => await _shell.CopyAsync(item.Path);
    [RelayCommand] private async Task CopyChecksumAsync(ArtifactItem item) => await _shell.CopyAsync($"{item.Sha256}  {item.Name}");

    [RelayCommand]
    private async Task ViewBuildLogAsync()
    {
        if (Result?.LogPath is { } log) await _dialogs.ShowLogAsync($"Build log — {Result.Settings.Identity.ApplicationName} {Result.Settings.RuntimeIdentifier}", log);
    }

    [RelayCommand]
    private async Task ExportArtifactsAsync()
    {
        if (Result is { Artifacts.Count: > 0 }) await _exporter.ExportAsync(Result.Artifacts);
    }

    [RelayCommand]
    private void RebuildWithChanges()
    {
        Step = WizardStep.Configure;
        UpdateSteps();
    }

    [RelayCommand]
    private async Task StartNewBuildAsync()
    {
        Reset();
        await LoadRecentProjectsAsync();
    }
}
