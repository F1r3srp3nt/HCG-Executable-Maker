using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HCG.ExecutableMaker.App.Services;
using HCG.ExecutableMaker.BuildEngine;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Porting;
using HCG.ExecutableMaker.Security;

namespace HCG.ExecutableMaker.App.ViewModels;

/// <summary>
/// Porting Studio: source-based conversion (macOS → iOS, Linux → Android) with an assessment, a separate output copy,
/// a reviewable plan and diff, and four separately reported verification levels.
/// </summary>
public sealed partial class PortingStudioViewModel : PageViewModel
{
    private readonly ProjectService _projectService;
    private readonly IProjectRepository _projects;
    private readonly PortingService _porting;
    private readonly IPlatformShell _shell;
    private readonly IDialogService _dialogs;
    private readonly NavigationService _navigation;
    private readonly IAppPaths _paths;
    private readonly ConcurrentQueue<BuildLogLine> _pendingLog = new();
    private CancellationTokenSource? _cts;

    public PortingStudioViewModel(ProjectService projectService, IProjectRepository projects, PortingService porting, IPlatformShell shell,
        IDialogService dialogs, NavigationService navigation, IAppPaths paths)
    {
        _projectService = projectService;
        _projects = projects;
        _porting = porting;
        _shell = shell;
        _dialogs = dialogs;
        _navigation = navigation;
        _paths = paths;
        OutputParent = Path.Combine(paths.DefaultOutputDirectory, "Ports");
        foreach (var p in porting.Porters) Paths.Add(new PortPathItem(p.Path, p.Scope));
    }

    public override string Title => "Porting Studio";
    public override string Subtitle => "Source-based ports: macOS → iOS and Linux → Android, with an honest assessment and a reviewable diff.";

    public string HonestyNote =>
        "Porting works from source code, within the scopes listed for each path — it is not a universal converter. HCG generates a new target only " +
        "with supported adaptations, lists every blocker that needs manual porting (never replacing it with a stub), writes the port to a separate " +
        "folder (your project is not modified), and reports separately whether the port was generated, compiled, launched in a simulator/emulator, " +
        "or tested on a device. Publishing and store submission stay manual.";

    // ───────────── 1 · Project ─────────────

    public ObservableCollection<ProjectItem> ProjectList { get; } = [];
    [ObservableProperty] private ProjectItem? _selectedProject;
    [ObservableProperty] private bool _isWorking;
    [ObservableProperty] private string _workStatus = "";
    [ObservableProperty] private string? _error;

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        await LoadProjectsAsync(parameter as ProjectRecord);
    }

    private async Task LoadProjectsAsync(ProjectRecord? select = null)
    {
        var keep = select?.Id ?? SelectedProject?.Project.Id;
        var reassess = select is not null;
        _reloading = true;
        try
        {
            ProjectList.Clear();
            foreach (var p in await _projects.ListAsync()) ProjectList.Add(new ProjectItem(p));
            SelectedProject = ProjectList.FirstOrDefault(p => p.Project.Id == keep) ?? SelectedProject;
        }
        finally { _reloading = false; }
        if (reassess) await AssessAsync();
    }

    private bool _reloading;

    partial void OnSelectedProjectChanged(ProjectItem? value)
    {
        // A list refresh keeps the current assessment, options and port; only a user choice re-assesses.
        if (value is not null && !_reloading) _ = AssessAsync();
    }

    [RelayCommand]
    private async Task ImportFolderAsync()
    {
        if (await _shell.PickFolderAsync("Select the source project to port") is not { } dir) return;
        await RunAsync("Importing…", async ct => await LoadProjectsAsync((await _projectService.ImportDirectoryAsync(dir, null, ct)).Project));
    }

    [RelayCommand]
    private async Task ImportZipAsync()
    {
        if (await _shell.PickZipAsync() is not { } zip) return;
        await RunAsync("Importing…", async ct => await LoadProjectsAsync((await _projectService.ImportZipAsync(zip, null, ct)).Project));
    }

    private async Task RunAsync(string status, Func<CancellationToken, Task> work)
    {
        var outer = _cts;
        using var cts = outer is null ? new CancellationTokenSource() : CancellationTokenSource.CreateLinkedTokenSource(outer.Token);
        _cts = cts;
        IsWorking = true;
        WorkStatus = status;
        Error = null;
        try { await work(cts.Token); }
        catch (OperationCanceledException) { Error = "Cancelled."; }
        catch (Exception ex) { Error = ex.Message; }
        finally
        {
            _cts = outer;
            IsWorking = outer is not null;
            WorkStatus = "";
        }
    }

    [RelayCommand] private void Cancel() => _cts?.Cancel();

    // ───────────── 2 · Paths and sources ─────────────

    public ObservableCollection<PortPathItem> Paths { get; } = [];
    [ObservableProperty] private PortSourceItem? _selectedSource;
    [ObservableProperty] private bool _hasAssessments;

    [RelayCommand]
    private async Task AssessAsync()
    {
        if (SelectedProject is null) return;
        var project = SelectedProject.Project;
        await RunAsync("Assessing (read-only — nothing is executed)…", async ct =>
        {
            var assessments = await _porting.AssessAsync(project, ct);
            foreach (var path in Paths)
            {
                path.Sources.Clear();
                foreach (var a in assessments.Where(a => a.Path == path.Path)) path.Sources.Add(new PortSourceItem(a));
                path.Refresh();
            }
            HasAssessments = assessments.Count > 0;
            SelectedSource = Paths.SelectMany(p => p.Sources).OrderBy(s => s.Assessment.Capability).FirstOrDefault();
        });
    }

    [RelayCommand] private void SelectSource(PortSourceItem item) => SelectedSource = item;

    // ───────────── 3 · Assessment ─────────────

    public ObservableCollection<FindingItem> Findings { get; } = [];
    public ObservableCollection<ReusableUnit> Units { get; } = [];
    public ObservableCollection<RequirementItem> Requirements { get; } = [];
    public ObservableCollection<string> Signing { get; } = [];
    public ObservableCollection<string> Scope { get; } = [];
    public ObservableCollection<UsageTextItem> RequiredTexts { get; } = [];
    [ObservableProperty] private string _findingCounts = "";

    public bool HasSelection => SelectedSource is not null;
    public bool CanGenerateTarget => SelectedSource?.Assessment.Capability is PortingCapability.GenerateTarget or PortingCapability.BlockedNeedsPorting;
    public bool IsBlocked => SelectedSource?.Assessment.Capability == PortingCapability.BlockedNeedsPorting;
    public bool IsBuildExisting => SelectedSource?.Assessment.Capability == PortingCapability.BuildExistingTarget;
    public bool IsNotSupported => SelectedSource?.Assessment.Capability == PortingCapability.NotSupported;
    public bool IsAndroid => SelectedSource?.Assessment.Path == PortingPath.LinuxToAndroid;
    public bool HasRequiredTexts => RequiredTexts.Count > 0;

    partial void OnSelectedSourceChanged(PortSourceItem? value)
    {
        foreach (var p in Paths) foreach (var s in p.Sources) s.IsSelected = ReferenceEquals(s, value);
        Findings.Clear();
        Units.Clear();
        Requirements.Clear();
        Signing.Clear();
        Scope.Clear();
        RequiredTexts.Clear();
        if (value is { } item)
        {
            var a = item.Assessment;
            foreach (var f in a.Findings) Findings.Add(new FindingItem(f));
            foreach (var u in a.Reusable) Units.Add(u);
            foreach (var r in a.Toolchains) Requirements.Add(new RequirementItem(r));
            foreach (var s in a.Signing) Signing.Add(s);
            foreach (var s in a.Scope) Scope.Add(s);
            foreach (var key in a.RequiredTexts) RequiredTexts.Add(new UsageTextItem(key));
            FindingCounts = $"{a.Blockers.Count()} blocker(s) · {a.Findings.Count(f => f.Disposition == FindingDisposition.NeedsReview)} to review · " +
                            $"{a.Findings.Count(f => f.Disposition == FindingDisposition.AutoAdapted)} supported adaptation(s)";
            AppName = a.SuggestedAppName ?? a.Source.Target;
            AppId = a.SuggestedAppId ?? "com.example.app";
            DefaultArguments = "";
            Draft = false;
        }
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanGenerateTarget));
        OnPropertyChanged(nameof(IsBlocked));
        OnPropertyChanged(nameof(IsBuildExisting));
        OnPropertyChanged(nameof(IsNotSupported));
        OnPropertyChanged(nameof(IsAndroid));
        OnPropertyChanged(nameof(HasRequiredTexts));
        GenerateCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void OpenInMobileStudio()
    {
        if (SelectedProject is not null) _navigation.Navigate("mobile", SelectedProject.Project);
    }

    // ───────────── 4 · Generate ─────────────

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(GenerateCommand))] private string _appName = "";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(GenerateCommand))] private string _appId = "";
    [ObservableProperty] private string _version = "1.0.0";
    [ObservableProperty] private decimal? _buildNumber = 1;
    [ObservableProperty] private string _defaultArguments = "";
    [ObservableProperty] private string _outputParent;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(GenerateCommand))] private bool _draft;

    [RelayCommand]
    private async Task BrowseOutputAsync()
    {
        if (await _shell.PickFolderAsync("Folder for ports (a new sub-folder is created per port)") is { } dir) OutputParent = dir;
    }

    private bool CanGenerate() => CanGenerateTarget && !IsWorking && AppName.Trim().Length > 0 && AppId.Trim().Length > 0 && (!IsBlocked || Draft);

    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private async Task GenerateAsync()
    {
        if (SelectedProject is null || SelectedSource is null) return;
        var options = new PortOptions
        {
            AppName = AppName.Trim(), AppId = AppId.Trim(), Version = Version.Trim(), BuildNumber = (int)(BuildNumber ?? 1),
            UsageDescriptions = RequiredTexts.ToDictionary(t => t.Key, t => t.Text), DefaultArguments = DefaultArguments, Draft = Draft,
        };
        var project = SelectedProject.Project;
        var assessment = SelectedSource.Assessment;
        await RunAsync("Generating the port in a separate folder…", async ct => ShowReport(await _porting.GenerateAsync(project, assessment, options, OutputParent, ct)));
    }

    // ───────────── 5 · Plan and diff ─────────────

    [ObservableProperty] private PortReport? _report;
    public ObservableCollection<string> PlanSteps { get; } = [];
    public ObservableCollection<FileChangeItem> Changes { get; } = [];
    public ObservableCollection<DiffLineItem> DiffLines { get; } = [];
    [ObservableProperty] private FileChangeItem? _selectedChange;
    [ObservableProperty] private string _planSummary = "";
    public bool HasReport => Report is not null;

    private void ShowReport(PortReport report)
    {
        Report = report;
        PlanSteps.Clear();
        foreach (var s in report.Plan.Steps) PlanSteps.Add(s);
        Changes.Clear();
        foreach (var c in report.Plan.Changes.Where(c => c.Kind != FileChangeKind.CopiedUnchanged)) Changes.Add(new FileChangeItem(c));
        PlanSummary = $"{report.Plan.Added} added · {report.Plan.Modified} modified · {report.Plan.Unchanged} copied unchanged → {report.OutputDirectory}";
        SelectedChange = Changes.FirstOrDefault(c => c.Change.Kind == FileChangeKind.Modified) ?? Changes.FirstOrDefault();
        RefreshVerification();
        OnPropertyChanged(nameof(HasReport));
    }

    partial void OnSelectedChangeChanged(FileChangeItem? value)
    {
        DiffLines.Clear();
        if (value is null) return;
        foreach (var line in value.Change.Diff.Split('\n').Take(4000)) DiffLines.Add(new DiffLineItem(line));
    }

    [RelayCommand]
    private async Task OpenOutputAsync()
    {
        if (Report is not null) await _shell.OpenFolderAsync(Report.OutputDirectory);
    }

    // ───────────── 6 · Verification ─────────────

    public ObservableCollection<VerificationItem> Verification { get; } = [];
    public ObservableCollection<LogLineItem> LogLines { get; } = [];
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(CompileCommand), nameof(LaunchCommand))] private bool _trustAcknowledged;
    [ObservableProperty] private Bitmap? _screenshot;
    [ObservableProperty] private bool _hasLog;
    public string TrustLabel => TrustPolicy.AcknowledgementLabel;

    private void RefreshVerification()
    {
        Verification.Clear();
        if (Report is null) return;
        foreach (var level in Enum.GetValues<VerificationLevel>()) Verification.Add(new VerificationItem(Report.Level(level)));
        var shot = Report.Verification.Select(v => v.Evidence).LastOrDefault(e => e is not null && e.EndsWith(".png", StringComparison.Ordinal) && File.Exists(e));
        try { Screenshot = shot is null ? null : new Bitmap(shot); } catch (Exception) { Screenshot = null; }
        CompileCommand.NotifyCanExecuteChanged();
        LaunchCommand.NotifyCanExecuteChanged();
    }

    private bool CanCompile() => Report is not null && TrustAcknowledged && !IsWorking;
    private bool CanLaunch() => Report?.Level(VerificationLevel.Compiled).Status == LevelStatus.Passed && TrustAcknowledged && !IsWorking;

    partial void OnIsWorkingChanged(bool value)
    {
        CompileCommand.NotifyCanExecuteChanged();
        LaunchCommand.NotifyCanExecuteChanged();
        GenerateCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanCompile))]
    private async Task CompileAsync()
    {
        if (Report is not { } report) return;
        LogLines.Clear();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) => FlushLog());
        timer.Start();
        await RunAsync(report.Assessment.Path == PortingPath.MacOSToIOS ? "Compiling for the iOS Simulator…" : "Compiling a debug APK (Gradle + NDK)…", async ct =>
        {
            var outcome = await Task.Run(() => _porting.CompileAsync(report, TrustAcknowledged, new Observer(this), ct), ct);
            Report = outcome.Report;
        });
        timer.Stop();
        FlushLog();
        RefreshVerification();
        await LoadProjectsAsync();
    }

    [RelayCommand(CanExecute = nameof(CanLaunch))]
    private async Task LaunchAsync()
    {
        if (Report is not { } report) return;
        await RunAsync(report.Assessment.Path == PortingPath.MacOSToIOS ? "Launching in the iOS Simulator…" : "Installing and launching on the emulator/device…", async ct =>
            Report = await Task.Run(() => _porting.LaunchAsync(report, TrustAcknowledged, ct), ct));
        RefreshVerification();
    }

    private void FlushLog()
    {
        var added = 0;
        while (_pendingLog.TryDequeue(out var line) && added++ < 2000) LogLines.Add(new LogLineItem(line));
        while (LogLines.Count > 3000) LogLines.RemoveAt(0);
        HasLog = LogLines.Count > 0;
    }

    private sealed class Observer(PortingStudioViewModel vm) : IBuildObserver
    {
        public void OnLog(BuildLogLine line) => vm._pendingLog.Enqueue(line);
        public void OnStage(BuildStage stage, string description) => Dispatcher.UIThread.Post(() => vm.WorkStatus = description);
        public void OnMessage(BuildMessage message) { }
    }

    /// <summary>For automation (UI driver) and tests: selects a source by key.</summary>
    public bool SelectSourceByKey(string key)
    {
        var item = Paths.SelectMany(p => p.Sources).FirstOrDefault(s => s.Assessment.Source.Key == key);
        if (item is null) return false;
        SelectedSource = item;
        return true;
    }
}

public sealed partial class PortPathItem(PortingPath path, IReadOnlyList<string> scope) : ObservableObject
{
    public PortingPath Path { get; } = path;
    public string Title => Path == PortingPath.MacOSToIOS ? "macOS → iOS" : "Linux → Android";
    public string Subtitle => Path == PortingPath.MacOSToIOS ? "SwiftUI source → iPhone / iPad Xcode project" : "C/C++ (CMake) program → Android app (NDK)";
    public IReadOnlyList<string> Scope { get; } = scope;
    public string ScopeSummary => Scope.FirstOrDefault() ?? "";
    public ObservableCollection<PortSourceItem> Sources { get; } = [];
    public bool HasSources => Sources.Count > 0;
    public string EmptyText => "No source in this project matches this path's supported scope. " + ScopeSummary;
    public void Refresh() { OnPropertyChanged(nameof(HasSources)); }
}

public sealed partial class PortSourceItem(PortingAssessment assessment) : ObservableObject
{
    public PortingAssessment Assessment { get; } = assessment;
    public string Name => Assessment.Source.DisplayName;
    public string Kind => Assessment.Source.Kind;
    public string Summary => Assessment.Summary;
    public StatusTag Capability => Assessment.Capability switch
    {
        PortingCapability.BuildExistingTarget => new StatusTag("Build existing target", "success"),
        PortingCapability.GenerateTarget => new StatusTag("Can generate", "success"),
        PortingCapability.BlockedNeedsPorting => new StatusTag($"Blocked · {Assessment.Blockers.Count()}", "warning"),
        _ => new StatusTag("Not supported", "error"),
    };
    [ObservableProperty] private bool _isSelected;
}

public sealed class FindingItem(PortingFinding f)
{
    public PortingFinding Finding { get; } = f;
    public StatusTag Disposition => f.Disposition switch
    {
        FindingDisposition.Blocker => new StatusTag("Blocker", "error"),
        FindingDisposition.NeedsReview => new StatusTag("Review", "warning"),
        FindingDisposition.AutoAdapted => new StatusTag("Adapted", "success"),
        _ => new StatusTag("Compatible", "neutral"),
    };
    public string Title => f.Title;
    public string Category => f.Category switch
    {
        FindingCategory.PlatformApi => "Platform API", FindingCategory.UiAdaptation => "UI adaptation", FindingCategory.BuildSystem => "Build system",
        FindingCategory.FileSystem => "File system", _ => f.Category.ToString(),
    };
    public string Location => f.Location;
    public bool HasLocation => f.Location.Length > 0;
    public string Detail => f.Detail;
    public string? Guidance => f.Guidance;
    public bool HasGuidance => f.Guidance is not null;
    public string? Evidence => f.Evidence;
    public bool HasEvidence => !string.IsNullOrEmpty(f.Evidence);
}

public sealed partial class UsageTextItem(string key) : ObservableObject
{
    public string Key { get; } = key;
    [ObservableProperty] private string _text = "";
}

public sealed class FileChangeItem(PlannedFileChange change)
{
    public PlannedFileChange Change { get; } = change;
    public string Path => change.Path;
    public string Reason => change.Reason;
    public StatusTag Kind => change.Kind == FileChangeKind.Added ? new StatusTag("Added", "success") : new StatusTag("Modified", "warning");
}

public sealed class DiffLineItem(string line)
{
    public string Text { get; } = line;
    public string Tone => line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal) ? "header"
        : line.StartsWith('+') ? "added" : line.StartsWith('-') ? "removed" : line.StartsWith("@@", StringComparison.Ordinal) ? "hunk" : "context";
    public bool IsAdded => Tone == "added";
    public bool IsRemoved => Tone == "removed";
    public bool IsHunk => Tone is "hunk" or "header";
}

public sealed class VerificationItem(VerificationEntry e)
{
    public string Level => PortingService.LevelText(e.Level);
    public StatusTag Status => e.Status switch
    {
        LevelStatus.Passed => new StatusTag("Yes", "success"),
        LevelStatus.Failed => new StatusTag("Failed", "error"),
        LevelStatus.NotPossibleHere => new StatusTag("Not possible here", "warning"),
        _ => new StatusTag("Not performed", "neutral"),
    };
    public string Detail => e.Detail;
    public string When => e.At is { } at ? Format.When(at) : "";
}
