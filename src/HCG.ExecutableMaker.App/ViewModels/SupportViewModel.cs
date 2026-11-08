using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HCG.ExecutableMaker.Account;
using HCG.ExecutableMaker.Account.Support;
using HCG.ExecutableMaker.App.Services;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.App.ViewModels;

/// <summary>What the support form was opened from (an unsupported inspection, a failed build, or Help).</summary>
public sealed record SupportRequestContext(ProjectRecord? Project, ProjectAnalysis? Analysis, BuildRecord? Build = null, IReadOnlyList<string>? BuildLog = null);

public sealed partial class PlannedFileItem(PlannedFile file, Action changed) : ObservableObject
{
    public PlannedFile File { get; } = file;
    public string Path => File.RelativePath;
    public string Size => Format.Bytes(File.SizeBytes);
    public string Explanation => File.Explanation;
    public bool CanInclude => !File.NeverSend;
    public bool IsFlagged => File.IsFlagged;
    public bool IsExcludedByDefault => File.NeverSend || File.IsFlagged || File.ExclusionReason is not null;
    public bool Included
    {
        get => File.Included;
        set
        {
            if (File.NeverSend || File.Included == value) return;
            File.Included = value;
            OnPropertyChanged();
            changed();
        }
    }
}

/// <summary>
/// Help &amp; Support and "Request support for this project". Nothing is sent until the user reviews exactly what will
/// be sent and presses Submit. Importing or building a project never uploads anything.
/// </summary>
public sealed partial class SupportViewModel : PageViewModel
{
    private readonly HcgAccountService _account;
    private readonly AppAccessService _access;
    private readonly SupportService _support;
    private readonly IPlatformShell _shell;
    private readonly IAppPaths _paths;
    private readonly IToolchainService _toolchains;
    private readonly NavigationService _navigation;
    private CancellationTokenSource? _submitCts;
    private Guid _clientRequestId = Guid.NewGuid();
    private AttachmentPlan? _plan;

    public SupportViewModel(HcgAccountService account, AppAccessService access, SupportService support, IPlatformShell shell, IAppPaths paths,
        IToolchainService toolchains, NavigationService navigation)
    {
        _account = account;
        _access = access;
        _support = support;
        _shell = shell;
        _paths = paths;
        _toolchains = toolchains;
        _navigation = navigation;
        _account.Changed += (_, _) => Dispatcher.UIThread.Post(UpdateAvailability);
        _access.Changed += (_, _) => Dispatcher.UIThread.Post(UpdateAvailability);
        UpdateAvailability();
    }

    public override string Title => "Help & Support";
    public override string Subtitle => "Ask HCG to support a project, file type or output that HCG Executable Maker cannot build yet.";

    // ---- availability ----
    [ObservableProperty] private bool _canUseSupport;
    [ObservableProperty] private string _availability = "";
    [ObservableProperty] private bool _needsSignIn;
    public string HowItWorks =>
        "1. Describe what you are trying to build. 2. Choose diagnostics only, or diagnostics plus files you select. " +
        "3. Review exactly what will be sent. 4. Submit. HCG staff answer in My requests. A request does not change what HCG Executable Maker supports; " +
        "HCG decides whether to build support for it.";

    // ---- form ----
    [ObservableProperty] private string _projectName = "";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SubmitCommand))] private string _requestTitle = "";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SubmitCommand))] private string _goal = "";
    [ObservableProperty] private string _languageFramework = "";
    [ObservableProperty] private string _targetOs = "";
    [ObservableProperty] private string _outputFormat = "";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SubmitCommand))] private string _problem = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IncludeFiles))] private bool _diagnosticsOnly = true;
    public bool IncludeFiles
    {
        get => !DiagnosticsOnly;
        set { DiagnosticsOnly = !value; if (value) PlanFiles(); RecomputeSummary(); }
    }
    public IReadOnlyList<string> OsOptions { get; } = ["Windows", "macOS", "Linux", "Android", "iOS", "Several / not sure"];

    public ObservableCollection<DiagnosticEntry> Diagnostics { get; } = [];
    public ObservableCollection<PlannedFileItem> Files { get; } = [];
    public ObservableCollection<string> SkippedFolders { get; } = [];
    [ObservableProperty] private bool _hasProjectFolder;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _destination = "";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SubmitCommand))] private bool _flaggedAcknowledged;
    [ObservableProperty] private bool _hasFlaggedIncluded;
    [ObservableProperty] private string? _validationError;

    // ---- submission ----
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SubmitCommand), nameof(CancelSubmitCommand))] private bool _isSubmitting;
    [ObservableProperty] private double _uploadPercent;
    [ObservableProperty] private string _uploadStatus = "";
    [ObservableProperty] private string? _resultMessage;
    [ObservableProperty] private bool _resultSucceeded;
    [ObservableProperty] private bool _canResume;

    private SupportRequestContext? _context;

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        UpdateAvailability();
        if (parameter is SupportRequestContext ctx) Load(ctx);
        else if (_context is null) await LoadHelpAsync();
        if (_account.IsSignedIn) await _access.RefreshAsync(force: false);
    }

    private async Task LoadHelpAsync()
    {
        var report = await _toolchains.GetAsync();
        _context = new SupportRequestContext(null, null);
        Diagnostics.Clear();
        foreach (var d in Account.Support.DiagnosticsCollector.Clean(
            [new("Environment", "HCG Executable Maker", HostInfo.ProductVersion), new("Environment", "Computer", report.Host.DisplayName + " · " + report.Host.OsDescription)]))
            Diagnostics.Add(d);
        ProjectName = "No project selected (general request)";
        HasProjectFolder = false;
        RecomputeSummary();
    }

    private void Load(SupportRequestContext ctx)
    {
        _context = ctx;
        _clientRequestId = Guid.NewGuid();
        ResultMessage = null;
        CanResume = false;
        var host = HostInfo.Detect();
        ProjectName = ctx.Project?.Name ?? "General request";
        if (ctx.Analysis is { } a)
        {
            RequestTitle = $"Support {a.Framework} ({a.Language})";
            LanguageFramework = $"{a.Language} / {a.Framework}";
            Problem = ctx.Build is null ? a.SupportSummary : $"Build failed: {ctx.Build.FailureReason}";
        }
        else if (ctx.Build is { } b)
        {
            RequestTitle = "Build failure: " + b.ProjectName;
            Problem = $"Build failed: {b.FailureReason}";
        }
        Diagnostics.Clear();
        var items = new List<DiagnosticEntry>();
        if (ctx.Analysis is not null) items.AddRange(Account.Support.DiagnosticsCollector.FromAnalysis(ctx.Analysis, host, HostInfo.ProductVersion));
        if (ctx.Build is not null) items.AddRange(Account.Support.DiagnosticsCollector.FromBuild(ctx.Build, ctx.BuildLog ?? []));
        if (items.Count == 0) items.Add(new("Environment", "HCG Executable Maker", HostInfo.ProductVersion));
        foreach (var d in items) Diagnostics.Add(d);
        HasProjectFolder = ctx.Project is not null && Directory.Exists(ctx.Project.SourceDirectory);
        DiagnosticsOnly = true;
        Files.Clear();
        SkippedFolders.Clear();
        _plan = null;
        RecomputeSummary();
    }

    private void PlanFiles()
    {
        if (_plan is not null || _context?.Project is not { } project || !Directory.Exists(project.SourceDirectory)) return;
        _plan = AttachmentPlanner.Plan(project.SourceDirectory);
        Files.Clear();
        foreach (var f in _plan.Files) Files.Add(new PlannedFileItem(f, RecomputeSummary));
        SkippedFolders.Clear();
        foreach (var d in _plan.SkippedDirectories) SkippedFolders.Add(d + " (dependencies, build output or version control — excluded)");
    }

    private void UpdateAvailability()
    {
        NeedsSignIn = !_account.IsSignedIn;
        CanUseSupport = _access.Has(HcgPermissions.SupportSubmit);
        Availability = _account.Status == HcgAccountStatus.NotConfigured
            ? "Support requests need HCG sign-in, which is not available in this version yet (" + _account.Message + "). You can still prepare a request below; nothing can be sent."
            : !_account.IsSignedIn
                ? "Sign in with HCG (Account page) to send support requests. You can prepare the request first."
                : CanUseSupport ? "Ready: requests go to HCG Executable Maker support." : _access.Message;
        Destination = $"HCG Executable Maker support on {_account.Configuration.ApiBaseUrl.Host}. Files are stored privately; only you and authorised HCG support staff can open them.";
        RecomputeSummary();
        SubmitCommand.NotifyCanExecuteChanged();
    }

    private void RecomputeSummary()
    {
        var limits = _access.Directory?.Limits;
        var included = IncludeFiles ? Files.Where(f => f.Included).ToList() : [];
        HasFlaggedIncluded = included.Any(f => f.IsFlagged);
        var bytes = included.Sum(f => f.File.SizeBytes);
        Summary = IncludeFiles
            ? $"{Diagnostics.Count} diagnostic item(s) and {included.Count} file(s), {Format.Bytes(bytes)} before compression, sent as one ZIP archive." +
              (limits is null ? "" : $" Limits: {Format.Bytes(limits.MaxAttachmentBytes)} per file, {limits.MaxAttachments} files per request; archives up to {limits.MaxArchiveEntries:N0} entries.") +
              (limits is null ? "" : $" Attachments are deleted {limits.AttachmentRetentionDays} days after the request is closed, or immediately if you withdraw it.")
            : $"{Diagnostics.Count} diagnostic item(s); no project files.";
        SubmitCommand.NotifyCanExecuteChanged();
    }

    private bool CanSubmit() => !IsSubmitting && CanUseSupport && RequestTitle.Trim().Length >= 3 && Goal.Trim().Length >= 3 && Problem.Trim().Length >= 3
                                && (!HasFlaggedIncluded || FlaggedAcknowledged) && (!IncludeFiles || Files.Any(f => f.Included));

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private async Task SubmitAsync()
    {
        ValidationError = null;
        ResultMessage = null;
        IsSubmitting = true;
        UploadPercent = 0;
        _submitCts = new CancellationTokenSource();
        string? archivePath = null;
        try
        {
            var files = new List<UploadFile>();
            if (IncludeFiles && _plan is not null)
            {
                UploadStatus = "Preparing the archive (only the files you selected)…";
                var dir = Path.Combine(_paths.Root, "support-uploads", _clientRequestId.ToString("N"));
                var archive = await Task.Run(() => AttachmentPlanner.BuildArchive(_plan, dir, "project.zip", _submitCts.Token));
                archivePath = archive.Path;
                if (_access.Directory?.Limits is { } l && archive.SizeBytes > l.MaxAttachmentBytes)
                {
                    ValidationError = $"The archive is {Format.Bytes(archive.SizeBytes)}; HCG accepts {Format.Bytes(l.MaxAttachmentBytes)} per file. Deselect large files and try again.";
                    return;
                }
                files.Add(new UploadFile(archive.Path, "project.zip", "application/zip"));
            }
            var request = new CreateSupportRequest
            {
                ClientRequestId = _clientRequestId,
                Title = RequestTitle.Trim(),
                Details = new SupportRequestDetails
                {
                    Goal = Goal.Trim(), LanguageFramework = NullIfEmpty(LanguageFramework), TargetOs = NullIfEmpty(TargetOs),
                    OutputFormat = NullIfEmpty(OutputFormat), Problem = Problem.Trim(),
                },
                Diagnostics = Diagnostics.ToList(),
                Mode = IncludeFiles ? SubmissionMode.DiagnosticsAndFiles : SubmissionMode.DiagnosticsOnly,
                Attachments = files.Select(f => f.Manifest()).ToList(),
                AppVersion = HostInfo.ProductVersion,
            };
            UploadStatus = "Sending to HCG…";
            var progress = new Progress<UploadProgress>(p =>
            {
                UploadPercent = p.TotalBytes == 0 ? 100 : 100.0 * p.SentBytes / p.TotalBytes;
                UploadStatus = $"Uploading {p.File}: {Format.Bytes(p.SentBytes)} of {Format.Bytes(p.TotalBytes)}";
            });
            var result = await _support.SubmitAsync(request, files, progress, _submitCts.Token);
            ResultSucceeded = result.Outcome == SubmitOutcome.Submitted;
            ResultMessage = result.Message;
            CanResume = !ResultSucceeded;
            if (ResultSucceeded) _clientRequestId = Guid.NewGuid(); // a new draft for the next request; retries reuse the old id
        }
        catch (HcgApiException ex)
        {
            ResultSucceeded = false;
            ResultMessage = ex.Message + (ex.IsOutage ? " Nothing is lost: Submit again to retry (the request will not be duplicated)." : "");
            CanResume = ex.IsOutage;
        }
        catch (OperationCanceledException)
        {
            ResultMessage = "Cancelled before anything was sent.";
        }
        finally
        {
            IsSubmitting = false;
            UploadStatus = "";
            if (archivePath is not null && ResultSucceeded) TryDelete(Path.GetDirectoryName(archivePath)!);
        }
    }

    private bool CanCancel() => IsSubmitting;
    [RelayCommand(CanExecute = nameof(CanCancel))] private void CancelSubmit() => _submitCts?.Cancel();

    [RelayCommand] private void OpenAccount() => _navigation.Navigate("account");
    [RelayCommand] private void OpenMyRequests() => _navigation.Navigate("requests");

    [RelayCommand]
    private void ExcludeAllFlagged()
    {
        foreach (var f in Files.Where(f => f.IsFlagged)) f.Included = false;
        RecomputeSummary();
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
