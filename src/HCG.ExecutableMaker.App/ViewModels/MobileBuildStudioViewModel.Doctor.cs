using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HCG.ExecutableMaker.BuildEngine.Doctor;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.App.ViewModels;

/// <summary>One Build Doctor finding as the page shows it: a one-line problem, expandable to cause, file, repair and technical detail.</summary>
public sealed partial class DoctorFindingItem(DoctorFinding finding) : ObservableObject
{
    public DoctorFinding Finding { get; } = finding;
    public string Problem => Finding.Problem;
    public string? RootCause => Finding.RootCause;
    public bool HasRootCause => !string.IsNullOrEmpty(Finding.RootCause);
    public string? AffectedFile => Finding.AffectedFile;
    public bool HasFile => !string.IsNullOrEmpty(Finding.AffectedFile);
    public string Recommendation => Finding.Recommendation;
    public string? Technical => Finding.Technical;
    public bool HasTechnical => !string.IsNullOrEmpty(Finding.Technical);
    public string Category => Finding.Category switch
    {
        DoctorCategory.ImportScope => "Import scope", DoctorCategory.ProjectReferences => "Project reference", DoctorCategory.MsBuildProperties => "MSBuild property",
        DoctorCategory.Imports => "MSBuild import", DoctorCategory.SourceFiles => "Source files", DoctorCategory.Sdk => ".NET SDK", DoctorCategory.TargetFramework => "Target framework",
        DoctorCategory.Workload => "Workload", DoctorCategory.Xcode => "Xcode", DoctorCategory.Packages => "NuGet packages", DoctorCategory.RuntimeIdentifier => "Runtime identifier",
        DoctorCategory.BundleIdentifier => "Bundle identifier", DoctorCategory.Signing => "Signing", DoctorCategory.Provisioning => "Provisioning", DoctorCategory.Configuration => "Configuration",
        DoctorCategory.Architecture => "Architecture", DoctorCategory.Host => "Host", _ => "Build input",
    };
    public StatusTag Severity => Finding.Severity switch
    {
        DoctorSeverity.Error => new("Error", "error"), DoctorSeverity.Warning => new("Warning", "warning"), _ => new("Info", "info"),
    };
    public bool HasRepair => Finding.Repair is not null;
    public string RepairTitle => Finding.Repair?.Title ?? "";
    public StatusTag RepairMode => Finding.Repair?.Mode switch
    {
        Domain.RepairMode.Automatic => new("Automatic", "success"),
        Domain.RepairMode.RequiresApproval => new("Requires approval", "warning"),
        Domain.RepairMode.Manual => new("Manual", "neutral"),
        _ => new("No repair", "neutral"),
    };
    public bool CanReview => Finding.Repair is { Mode: not Domain.RepairMode.Manual } || Finding.Repair?.Kind == RepairKind.ConfigureSigning;
    [ObservableProperty] private StatusTag _verification = new("Not verified", "neutral");
    [ObservableProperty] private string? _verificationDetail;
    [ObservableProperty] private bool _isExpanded;
}

public sealed partial class MobileBuildStudioViewModel
{
    private DoctorReport? _doctorReport;
    private DoctorRepairRecord? _lastRepair;
    private ProjectRecord? _repairProject;
    private IReadOnlyList<DoctorFinding> _beforeRepair = [];

    public ObservableCollection<DoctorFindingItem> DoctorFindings { get; } = [];
    public ObservableCollection<string> DoctorSummary { get; } = [];
    public ObservableCollection<string> DoctorFacts { get; } = [];
    [ObservableProperty] private bool _hasDoctor;
    [ObservableProperty] private StatusTag _doctorReadiness = new("Not checked", "neutral");
    [ObservableProperty] private string _doctorHeading = "";
    [ObservableProperty] private bool _showDoctorInfo;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ApproveRepairCommand))] private DoctorFindingItem? _reviewing;
    [ObservableProperty] private string _repairPreview = "";
    [ObservableProperty] private string _repairLog = "";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ApproveRepairCommand), nameof(RollbackRepairCommand), nameof(EvaluateWithMsBuildCommand))] private bool _isRepairing;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ApproveRepairCommand), nameof(EvaluateWithMsBuildCommand))] private bool _repairTrustAcknowledged;
    [ObservableProperty] private string? _repairOutcome;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(RollbackRepairCommand))] private bool _canRollback;
    public bool IsReviewing => Reviewing is not null;
    /// <summary>Repairs that run project code (restore, MSBuild evaluation) or install software need the same trust confirmation as a build.</summary>
    public bool ReviewNeedsTrust => Reviewing?.Finding.Repair?.Kind is RepairKind.RestorePackages or RepairKind.InstallWorkload;
    public bool HasMissingWorkload => _doctorReport?.Findings.Any(f => f.Repair?.Kind == RepairKind.InstallWorkload) == true;
    public bool HasSigningFindings => _doctorReport?.Findings.Any(f => f.Category is DoctorCategory.Signing or DoctorCategory.Provisioning) == true;
    public bool HasReviewableRepairs => DoctorFindings.Any(f => f.CanReview);
    public string DoctorInfoToggleText => ShowDoctorInfo ? "Hide notes" : $"Show notes ({_doctorReport?.Findings.Count(f => f.Severity == DoctorSeverity.Info) ?? 0})";

    partial void OnReviewingChanged(DoctorFindingItem? value)
    {
        OnPropertyChanged(nameof(IsReviewing));
        OnPropertyChanged(nameof(ReviewNeedsTrust));
        RepairTrustAcknowledged = false;
        RepairLog = "";
        RepairOutcome = null;
        RepairPreview = value?.Finding.Repair is { } r ? DoctorRepairs.Preview(r) : "";
    }

    partial void OnShowDoctorInfoChanged(bool value) { OnPropertyChanged(nameof(DoctorInfoToggleText)); FillDoctorFindings(); }

    private DoctorInput? DoctorInputFor()
    {
        if (SelectedProject?.Project is not { Analysis.Mobile: { } m } p) return null;
        var output = SelectedOutput?.Option.Output ?? (m.Platforms.Contains(TargetPlatform.IOS) ? MobileOutput.IosSimulatorApp : MobileOutput.AndroidDebugApk);
        return new DoctorInput(p, m, output, CreateOptions(), _report);
    }

    /// <summary>Runs Build Doctor (read-only, no project code). Called whenever the project or output changes.</summary>
    private void RunDoctorQuietly()
    {
        if (DoctorInputFor() is not { } input) { HasDoctor = false; return; }
        try { ShowDoctor(BuildDoctor.Diagnose(input)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            HasDoctor = true;
            DoctorReadiness = new("Not checked", "warning");
            DoctorHeading = "Build Doctor could not read the project: " + ex.Message;
        }
    }

    private void ShowDoctor(DoctorReport report)
    {
        _doctorReport = report;
        HasDoctor = true;
        var readiness = BuildDoctor.Readiness(report);
        DoctorReadiness = new(readiness, report.Errors > 0 ? "error" : report.Warnings > 0 ? "warning" : "success");
        DoctorHeading = $"{report.Subject} · {report.Output.DisplayName()}";
        DoctorSummary.Clear();
        foreach (var line in BuildDoctor.Summary(report)) DoctorSummary.Add(line);
        if (DoctorSummary.Count == 0) DoctorSummary.Add("No problems found for this output.");
        DoctorFacts.Clear();
        foreach (var f in report.Facts) DoctorFacts.Add(f);
        FillDoctorFindings();
        OnPropertyChanged(nameof(HasMissingWorkload));
        OnPropertyChanged(nameof(HasSigningFindings));
        OnPropertyChanged(nameof(DoctorInfoToggleText));
    }

    private void FillDoctorFindings()
    {
        DoctorFindings.Clear();
        foreach (var f in _doctorReport?.Findings ?? [])
            if (ShowDoctorInfo || f.Severity != DoctorSeverity.Info)
                DoctorFindings.Add(new DoctorFindingItem(f) { Verification = f.Verification switch
                {
                    DoctorVerification.Passed => new("Verified", "success"), DoctorVerification.Failed => new("Still present", "error"),
                    DoctorVerification.NotVerifiable => new("Not verifiable", "neutral"), _ => new("Not verified", "neutral"),
                }, VerificationDetail = f.VerificationDetail });
        OnPropertyChanged(nameof(HasReviewableRepairs));
    }

    /// <summary>Run Build Doctor: re-detect toolchains, re-read the project, diagnose.</summary>
    [RelayCommand]
    private async Task RunBuildDoctorAsync()
    {
        _report = await _toolchains.GetAsync(refresh: true);
        if (SelectedProject is { } p)
        {
            await _projectService.InspectAsync(p.Project, CancellationToken.None);
            ApplyProject(p.Project);
        }
        RunDoctorQuietly();
    }

    [RelayCommand] private void ToggleDoctorInfo() => ShowDoctorInfo = !ShowDoctorInfo;

    [RelayCommand] private void ReviewRepair(DoctorFindingItem? item)
    {
        if (item?.Finding.Repair?.Kind == RepairKind.ConfigureSigning) { _navigation?.Navigate("security"); return; }
        Reviewing = item;
    }

    [RelayCommand] private void ReviewFirstRepair() => Reviewing = DoctorFindings.FirstOrDefault(f => f.CanReview && f.Finding.Repair?.Kind != RepairKind.ConfigureSigning);

    [RelayCommand] private void ReviewWorkloadRepair() => Reviewing = DoctorFindings.FirstOrDefault(f => f.Finding.Repair?.Kind == RepairKind.InstallWorkload);

    [RelayCommand] private void ConfigureSigning() => _navigation?.Navigate("security");

    [RelayCommand] private void CancelRepair() => Reviewing = null;

    private bool CanApprove() => !IsRepairing && Reviewing?.Finding.Repair is { Mode: not RepairMode.Manual } && (!ReviewNeedsTrust || RepairTrustAcknowledged);

    /// <summary>
    /// Applies the repair under review, exactly as previewed. File edits are backed up first; imports never change the
    /// current project; commands run with the scrubbed build environment. Build Doctor then runs again and every finding
    /// the repair addressed is marked Verified or Still present.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanApprove))]
    private async Task ApproveRepairAsync()
    {
        if (Reviewing?.Finding.Repair is not { } repair || SelectedProject?.Project is not { } project) return;
        IsRepairing = true;
        _beforeRepair = _doctorReport?.Findings ?? [];
        var lines = new List<string>();
        void Log(string line) => Dispatcher.UIThread.Post(() => { lines.Add(line); RepairLog = string.Join("\n", lines.TakeLast(400)); });
        try
        {
            var repairs = new DoctorRepairs(_runner ?? throw new InvalidOperationException("No process runner."), TimeProvider.System);
            switch (repair.Kind)
            {
                case RepairKind.ImportRepository:
                    Log($"Importing {repair.Target} (the current project is kept unchanged)…");
                    var result = await Task.Run(() => _projectService.ImportDirectoryAsync(repair.Target!, null, CancellationToken.None));
                    Log($"Imported as “{result.Project.Name}”. Running Build Doctor on the new import.");
                    await LoadProjectsAsync(result.Project);
                    RepairOutcome = $"Imported {repair.Target} as a new project. The previous import is still in the project list.";
                    break;
                case RepairKind.EditFiles:
                    _lastRepair = await repairs.ApplyFileChangesAsync(project, repair, CancellationToken.None);
                    _repairProject = project;
                    Log($"Backed up to {_lastRepair.BackupFolder}");
                    foreach (var f in _lastRepair.ChangedFiles) Log("Changed " + f);
                    await _projectService.InspectAsync(project, CancellationToken.None);
                    ApplyProject(project);
                    RepairOutcome = "Applied. A backup was kept; Roll back restores the previous files.";
                    CanRollback = true;
                    break;
                case RepairKind.RestorePackages or RepairKind.InstallWorkload:
                    var app = project.Analysis?.Mobile?.DotNetIos ?? throw new InvalidOperationException("This project has no .NET for iOS app.");
                    var dotnet = _report?.Find(ToolchainIds.DotNetSdk)?.ExecutablePath ?? throw new InvalidOperationException("The .NET SDK was not found.");
                    var record = await repairs.RunCommandAsync(project, app, repair, dotnet, Log, CancellationToken.None);
                    if (repair.Kind == RepairKind.InstallWorkload) _report = await _toolchains.GetAsync(refresh: true);
                    RepairOutcome = record.Succeeded ? $"{repair.Title}: completed." : $"{repair.Title}: {record.Outcome}. See the output above.";
                    break;
                case RepairKind.Recheck:
                    _report = await _toolchains.GetAsync(refresh: true);
                    break;
            }
            RunDoctorQuietly();
            MarkVerification(repair);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or ImportRejectedException)
        {
            RepairOutcome = "Not applied: " + ex.Message;
        }
        finally { IsRepairing = false; }
    }

    /// <summary>Marks the findings this repair addressed: gone → Verified, still there → Still present.</summary>
    private void MarkVerification(DoctorRepair repair)
    {
        if (_doctorReport is not { } after) return;
        var addressed = _beforeRepair.Where(f => f.Repair?.Id == repair.Id).Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        var stillThere = after.Findings.Where(f => addressed.Contains(f.Id)).Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var item in DoctorFindings.Where(i => stillThere.Contains(i.Finding.Id)))
        { item.Verification = new("Still present", "error"); item.VerificationDetail = "Build Doctor found this again after the repair."; }
        var fixedCount = addressed.Count - stillThere.Count;
        if (addressed.Count > 0)
            RepairOutcome = (RepairOutcome is null ? "" : RepairOutcome + " ") + $"Verification: {fixedCount} of {addressed.Count} finding(s) resolved" + (stillThere.Count > 0 ? $", {stillThere.Count} still present." : ".");
    }

    private bool CanRollbackRepair() => CanRollback && !IsRepairing && _lastRepair is not null;

    [RelayCommand(CanExecute = nameof(CanRollbackRepair))]
    private async Task RollbackRepairAsync()
    {
        if (_lastRepair is not { } record || _repairProject is not { } project) return;
        IsRepairing = true;
        try
        {
            await DoctorRepairs.RollbackAsync(project, record, CancellationToken.None);
            RepairOutcome = $"Rolled back {record.Title}: {string.Join(", ", record.ChangedFiles)} restored from {record.BackupFolder}.";
            CanRollback = false;
            await _projectService.InspectAsync(project, CancellationToken.None);
            ApplyProject(project);
            RunDoctorQuietly();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException) { RepairOutcome = "Rollback failed: " + ex.Message; }
        finally { IsRepairing = false; }
    }

    private bool CanEvaluate() => !IsRepairing && RepairTrustAcknowledged && SelectedProject?.Project.Analysis?.Mobile?.DotNetIos is not null;

    /// <summary>
    /// Evaluates the app and its project references with MSBuild itself (no targets run) and records what MSBuild says
    /// next to Build Doctor's own reading: the authoritative check that every reference resolves.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEvaluate))]
    private async Task EvaluateWithMsBuildAsync()
    {
        if (SelectedProject?.Project is not { Analysis.Mobile.DotNetIos: { } dn } project || _runner is null) return;
        var dotnet = _report?.Find(ToolchainIds.DotNetSdk)?.ExecutablePath;
        if (dotnet is null) { RepairOutcome = "The .NET SDK was not found."; return; }
        IsRepairing = true;
        try
        {
            var projects = new List<string> { dn.ProjectPath };
            projects.AddRange(dn.ProjectReferences);
            var results = await new DoctorRepairs(_runner, TimeProvider.System).EvaluateWithMsBuildAsync(project, projects, dotnet, CancellationToken.None);
            var lines = new List<string>();
            foreach (var r in results)
            {
                if (!r.Succeeded) { lines.Add($"{r.Project}: MSBuild could not evaluate it — {r.Error}"); continue; }
                lines.Add($"{r.Project}: {string.Join(", ", r.Properties.Select(kv => $"{kv.Key}={kv.Value}"))}");
                foreach (var (path, exists) in r.References) lines.Add($"   reference {path} — {(exists ? "exists" : "MISSING")}");
            }
            RepairLog = string.Join("\n", lines);
            var missing = results.SelectMany(r => r.References).Count(x => !x.Exists);
            var failed = results.Count(r => !r.Succeeded);
            RepairOutcome = failed == 0 && missing == 0
                ? $"MSBuild evaluated {results.Count} project(s): every project reference resolves."
                : $"MSBuild: {failed} project(s) could not be evaluated, {missing} reference(s) missing.";
        }
        finally { IsRepairing = false; }
    }
}
