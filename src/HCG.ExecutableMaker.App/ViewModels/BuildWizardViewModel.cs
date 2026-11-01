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
    private readonly NavigationService _navigation;
    private readonly ConcurrentQueue<BuildLogLine> _pendingLog = new();
    private readonly DispatcherTimer _uiTimer;
    private readonly Stopwatch _buildClock = new();
    private CancellationTokenSource? _importCts;
    private CancellationTokenSource? _buildCts;
    private string? _identityPrefilledFor;

    public BuildWizardViewModel(ProjectService projectService, IProjectRepository projects, BuildService buildService, IToolchainService toolchains,
        ISettingsStore settings, IAppPaths paths, IDialogService dialogs, IPlatformShell shell, ArtifactExporter exporter, NavigationService navigation)
    {
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
        WizardStep.Package => PackagingOptions.Any(o => o.IsSelected && o.IsEnabled),
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

    private bool CanGoBack() => ShowBack && !IsBuilding && !IsImporting;

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
        Analysis = null;
        ImportError = null;
        ImportWarnings.Clear();
        Result = null;
        _identityPrefilledFor = null;
        IconPath = null;
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
        ApplyAnalysis(project.Analysis ?? await _projectService.InspectAsync(project, CancellationToken.None));
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

    public bool CanConfigure => Analysis?.Support == SupportLevel.Supported && SelectedProject?.Project.CompatiblePlatforms.Count > 0;

    partial void OnSelectedProjectChanged(ProjectChoice? value) => OnPropertyChanged(nameof(CanConfigure));

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

    private void ApplyAnalysis(ProjectAnalysis a)
    {
        Analysis = a;
        SupportTag = new StatusTag(Format.Label(a.Support), Format.Tone(a.Support));
        AnalysisRows.Clear();
        AnalysisRows.Add(new("Project name", a.ProjectName));
        AnalysisRows.Add(new("Language", a.Language));
        AnalysisRows.Add(new("Framework", a.Framework));
        AnalysisRows.Add(new("Runtime", a.Runtime ?? "—"));
        AnalysisRows.Add(new("Application type", a.ApplicationType));
        AnalysisRows.Add(new("Solution", a.SolutionFiles.Count > 0 ? string.Join(", ", a.SolutionFiles) : "None (project files only)"));
        AnalysisRows.Add(new("Projects found", a.Projects.Count.ToString()));
        if (a.GlobalJsonSdkVersion is { } gj) AnalysisRows.Add(new("global.json SDK", $"{gj} (rollForward: {a.GlobalJsonRollForward ?? "patch"})"));
        AnalysisRows.Add(new("Recommended outputs", a.RecommendedFormats.Count > 0 ? string.Join(", ", a.RecommendedFormats.Select(f => f.DisplayName())) : "—"));
        AnalysisRows.Add(new("Icons found", a.DetectedIcons.Count > 0 ? string.Join(", ", a.DetectedIcons.Take(3)) : "None"));

        ProjectOptions.Clear();
        foreach (var p in a.Projects.Where(p => p.IsRunnableApplication)) ProjectOptions.Add(new ProjectChoice(p));
        SelectedProject = ProjectOptions.FirstOrDefault(p => p.Path == a.PrimaryProjectPath) ?? ProjectOptions.FirstOrDefault();

        Diagnostics.Clear();
        foreach (var d in a.Diagnostics.OrderByDescending(d => d.Severity)) Diagnostics.Add(new DiagnosticItem(d));
        HasDiagnostics = Diagnostics.Count > 0;
        Requirements.Clear();
        foreach (var r in a.RequiredToolchains) Requirements.Add(new RequirementItem(r));
        MissingDependencies.Clear();
        foreach (var m in a.MissingDependencies) MissingDependencies.Add(m);
        HasMissing = MissingDependencies.Count > 0;
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
    public IReadOnlyList<Choice<TargetArchitecture>> ArchitectureOptions { get; } =
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
        var key = Project?.Id + "|" + detected.RelativePath;
        if (_identityPrefilledFor != key)
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
        foreach (var p in new[] { TargetPlatform.Windows, TargetPlatform.MacOS, TargetPlatform.Linux, TargetPlatform.Android })
        {
            var compatible = detected.CompatiblePlatforms.Contains(p);
            var note = p == TargetPlatform.Android ? "not implemented" : compatible ? null : "not supported by this project";
            var name = p == TargetPlatform.Linux ? "Linux (portable preview)" : p.DisplayName();
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
        SelectedArchitecture = ArchitectureOptions.First(a => a.Value == s.Architecture);
        SelectedConfiguration = ConfigurationOptions.First(c => c.Value == s.Configuration);
        SelfContained = s.SelfContained;
        SingleFile = s.SingleFile;
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
            TargetPlatform.Windows when HostInfo.CurrentPlatform != TargetPlatform.Windows =>
                "Cross-building for Windows from this host: the .exe is produced, but its embedded file icon requires a Windows build host.",
            TargetPlatform.MacOS when HostInfo.CurrentPlatform != TargetPlatform.MacOS =>
                "Cross-building for macOS: a valid .app bundle is assembled and structurally verified; launch testing and DMG creation require a Mac.",
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
        if (ConfigErrors.Count == 0 && SelectedProject is not null)
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

    private void PreparePackaging(IReadOnlyList<PackagingFormat>? preselect = null)
    {
        var detected = SelectedProject?.Project;
        var isGui = detected is not null && (detected.IsAvalonia || string.Equals(detected.OutputType, "WinExe", StringComparison.OrdinalIgnoreCase));
        var ctx = new PackagingContext(SelectedPlatform?.Value ?? TargetPlatform.Windows, HostInfo.Detect(), _toolchains.Latest, isGui);
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
    }

    private void OnPackagingOptionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => NextCommand.NotifyCanExecuteChanged();

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
        Summary.Add(new("Target", $"{s.Platform.DisplayName()} {s.Architecture.DisplayName()} ({s.RuntimeIdentifier})"));
        Summary.Add(new("Configuration", $"{s.Configuration} · {(s.SelfContained ? "self-contained" : "framework-dependent")}{(s.SingleFile ? " · single file" : "")}"));
        Summary.Add(new("Packaging", string.Join(", ", PackagingCatalog.WithDependencies(s.PackagingFormats).Select(f => f.DisplayName()))));
        Summary.Add(new("Icon", s.Identity.IconSourcePath is null ? "None" : Path.GetFileName(s.Identity.IconSourcePath)));
        Summary.Add(new("Output", BuildService.OutputRootFor(s)));
        Summary.Add(new("Time limit", $"{s.Timeout.TotalMinutes:0} minutes"));
    }

    private bool CanStartBuild() => TrustAcknowledged && !IsBuilding && Project is not null;

    [RelayCommand(CanExecute = nameof(CanStartBuild))]
    private async Task StartBuildAsync()
    {
        if (Project is null || SelectedProject is null) return;
        var settings = CreateSettings(SelectedFormats());
        try { DotNetBuildPlanner.Validate(settings); }
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
                $"A previous HCG build of {settings.Identity.ApplicationName} {settings.Identity.Version} for {settings.RuntimeIdentifier} exists:\n\n{outputRoot}\n\nReplace it with this build?",
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

    private sealed class Observer(BuildWizardViewModel vm) : IBuildObserver
    {
        public void OnLog(BuildLogLine line) => vm._pendingLog.Enqueue(line);

        public void OnStage(BuildStage stage, string description) => Dispatcher.UIThread.Post(() =>
        {
            if (vm.IsCancelling) return;
            vm.StageNumber = (int)stage;
            vm.CurrentStage = description;
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
        ResultDetail = build.Status == BuildStatus.Succeeded
            ? $"{build.Artifacts.Count} artifact(s) generated and verified · {build.WarningCount} warning(s). Artifacts are unsigned."
            : build.FailureReason ?? "The build did not complete.";
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
