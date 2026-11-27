using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HCG.ExecutableMaker.BuildEngine.BuildHost;
using HCG.ExecutableMaker.BuildEngine.MacXcode;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.App.ViewModels;

/// <summary>
/// Build Provider selector: Local Machine (the existing Configure → Package → Build path) or HCG Server's Build Host
/// (Inspect → Build: the Build Host builds and packages with its own predefined operations). Other providers are listed
/// once they can run from here.
/// </summary>
public sealed partial class BuildWizardViewModel
{
    public IReadOnlyList<BuildProviderOption> BuildProviderOptions { get; } = BuildProviderCatalog.All;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(UsesBuildHost), nameof(BuildProviderDescription), nameof(ShowLocalBuildPanel), nameof(ShowBuildHostPanel))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private BuildProviderOption _selectedBuildProvider = BuildProviderCatalog.LocalMachine;
    public bool UsesBuildHost => SelectedBuildProvider.Kind == BuildProviderKind.HcgServerBuildHost;
    public string BuildProviderDescription => SelectedBuildProvider.Description;
    public bool ShowLocalBuildPanel => ShowTrustPanel && !UsesBuildHost;
    public bool ShowBuildHostPanel => ShowTrustPanel && UsesBuildHost;

    /// <summary>The HCG Server Build Host panel (connection, host, project, commit, operations, authorization).</summary>
    public BuildHostPanelViewModel BuildHost { get; }

    // While a Build Host job runs.
    [ObservableProperty] private string _buildHostJob = "";
    [ObservableProperty] private string _awaitingApproval = "";
    public bool IsAwaitingApproval => AwaitingApproval.Length > 0;
    partial void OnAwaitingApprovalChanged(string value) => OnPropertyChanged(nameof(IsAwaitingApproval));

    // Result: provenance, key, hashes and Release Passport evidence.
    public ObservableCollection<SummaryRow> ProviderEvidence { get; } = [];
    [ObservableProperty] private bool _hasProviderEvidence;
    [ObservableProperty] private string _distributionReadiness = "";
    public BuildHostOutcome? LastBuildHostOutcome { get; private set; }

    partial void OnSelectedBuildProviderChanged(BuildProviderOption value)
    {
        if (!value.Implemented) Dispatcher.UIThread.Post(() => SelectedBuildProvider = BuildProviderCatalog.LocalMachine);
        UpdateSteps();
    }

    private static bool IsBuildHostBuild(BuildRecord b) => b.ToolchainVersions.TryGetValue("build-provider", out var p) && p == BuildProviderIds.HcgServerBuildHost;

    private bool CanContinueWithProvider => UsesBuildHost ? Project is not null : CanConfigure;

    private async Task OpenBuildHostStepAsync()
    {
        if (Project is null) return;
        Step = WizardStep.Build;
        UpdateSteps();
        await BuildHost.LoadAsync(Project);
    }

    private bool CanStartOnBuildHost() => UsesBuildHost && !IsBuilding && Project is not null;

    /// <summary>Submits the authorized build to HCG Server's Build Host and follows it in the build view.</summary>
    [RelayCommand(CanExecute = nameof(CanStartOnBuildHost))]
    private async Task StartBuildOnHostAsync()
    {
        if (Project is null) return;
        if (!BuildHost.CanStart)
        {
            await _dialogs.AlertAsync("HCG Server Build Host", BuildHost.Blocker);
            return;
        }
        HcgServerBuildHost provider;
        BuildHostBuildRequest request;
        try { (provider, request) = BuildHost.Prepare(_paths.DefaultOutputDirectory); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            await _dialogs.AlertAsync("HCG Server Build Host", ex.Message);
            return;
        }

        LogLines.Clear();
        while (_pendingLog.TryDequeue(out _)) { }
        LogTruncated = false;
        LiveWarnings = LiveErrors = 0;
        StageNumber = 0;
        CurrentStage = "Submitting to HCG Server's Build Host…";
        StageHistory.Clear();
        BuildHostJob = AwaitingApproval = "";
        IsCancelling = false;
        IsBuilding = true;
        HasBuildStarted = true;
        _buildCts = new CancellationTokenSource();
        _buildClock.Restart();
        _uiTimer.Start();
        BuildHostOutcome? outcome = null;
        try
        {
            var token = _buildCts.Token;
            var observer = new Observer(this);
            outcome = await Task.Run(() => provider.RunAsync(request, observer, token));
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
            AwaitingApproval = "";
            _buildCts.Dispose();
            _buildCts = null;
        }
        if (outcome is null) return;
        LastBuildHostOutcome = outcome;
        await BuildHost.RecordAsync(outcome);
        ShowResult(outcome.Build);
        ShowProviderEvidence(outcome);
    }

    private void OnBuildHostJobUpdated(BuildJob job) => Dispatcher.UIThread.Post(() =>
    {
        BuildHostJob = $"Job {job.Id} · {job.State}{(job.Detail is { Length: > 0 } d ? " · " + d : "")}";
        if (job.State != BuildJobStates.AwaitingApproval) AwaitingApproval = "";
    });

    private void OnBuildHostAwaitingApproval(BuildJob job, string instruction) => Dispatcher.UIThread.Post(() =>
    {
        BuildHostJob = $"Job {job.Id} · {job.State}";
        AwaitingApproval = instruction + " Executable Maker cannot approve jobs; Cancel Build withdraws it.";
        CurrentStage = "Waiting for the owner's approval on HCG Server";
    });

    /// <summary>The Build Host's evidence for the result: provenance and its key, the job, source, toolchain, hashes, passports.</summary>
    private void ShowProviderEvidence(BuildHostOutcome outcome)
    {
        ProviderEvidence.Clear();
        var b = outcome.Build;
        ProviderEvidence.Add(new("Provider", $"HCG Server Build Host ({BuildProviderIds.HcgServerBuildHost}) · {(b.ToolchainVersions.TryGetValue("buildhost-isolation", out var iso) ? iso : "isolation unknown")}"));
        if (outcome.Job is { } job)
        {
            ProviderEvidence.Add(new("Job", $"{job.Id} · {job.State} · approved by {job.DecidedBy ?? "—"}{(job.DecidedAt is { } at ? $" at {at.ToLocalTime():yyyy-MM-dd HH:mm:ss}" : "")}"));
            foreach (var step in job.Plan)
                ProviderEvidence.Add(new("Step " + step.Operation,
                    $"{string.Join(' ', step.Argv)} · exit {step.ExitCode?.ToString() ?? "—"} · {step.DurationMs / 1000.0:0.0}s · peak {step.PeakRssBytes / (1024 * 1024)} MB · CPU {step.CpuSeconds:0.0}s{(step.Network ? " · network" : "")}"));
        }
        if (outcome.Provenance is { } pv)
        {
            var (outcomeKey, detail) = BuildProviderPlanning.KeyCheck(pv.KeyFingerprint, BuildHost.KeyPin);
            ProviderEvidence.Add(new("Provenance", $"Verified ES256 signature ({BuildHostContract.ProvenanceFormat}) issued {pv.Document.IssuedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}"));
            ProviderEvidence.Add(new("Host key", (outcomeKey == SigningCheckOutcome.Passed ? "✓ " : outcomeKey == SigningCheckOutcome.Failed ? "✗ " : "! ") + detail));
            ProviderEvidence.Add(new("Build Host", $"{pv.Document.BuildHost.Name} ({pv.Document.BuildHost.DeviceId}) · {pv.Document.BuildHost.Os}/{pv.Document.BuildHost.Architecture} · hcg-agent {pv.Document.BuildHost.AgentVersion}"));
            ProviderEvidence.Add(new("Source", $"{pv.Document.Source.Repository} @ {pv.Document.Source.Commit} · tree {pv.Document.Source.Tree} · {(pv.Document.Source.Dirty ? "DIRTY" : "clean")} · from {pv.Document.Source.Remote}"));
            ProviderEvidence.Add(new("Toolchain", string.Join(" · ", pv.Document.Toolchains.Select(t => BuildProviderPlanning.ToolVersion(t)))));
        }
        else ProviderEvidence.Add(new("Provenance", "None verified: " + (outcome.EvidenceProblem ?? b.FailureReason ?? "the job did not produce signed provenance.")));
        if (outcome.Validation is { } v) ProviderEvidence.Add(new("Validation", $"{v.Overall} · {v.Checks.Count} check(s)"));
        foreach (var a in b.Artifacts)
        {
            var passport = a.Path + ".release-passport.json";
            ProviderEvidence.Add(new("Artifact", $"{a.FileName} · {a.SizeBytes:N0} bytes · SHA-256 {a.Sha256} · {a.Verification}"));
            if (File.Exists(passport)) ProviderEvidence.Add(new("Release Passport", $"{Path.GetFileName(passport)} · build.provider {BuildProviderIds.HcgServerBuildHost}, run_id null, signing unsigned"));
        }
        if (outcome.Evidence is { } ev)
            ProviderEvidence.Add(new("HCG Repo evidence", $"{ev.Provider} · {ev.Repository}@{ev.Commit[..12]} · {ev.Toolchain} · built {ev.BuiltAt} · no CI run"));
        var dir = b.LogPath is { } log ? Path.GetDirectoryName(log) : null;
        if (dir is not null)
            foreach (var f in new[] { NativeBuildContract.RequestFileName, NativeBuildContract.ResultFileName, HcgServerBuildHost.ProvenanceFileName, HcgServerBuildHost.ValidationFileName })
                if (File.Exists(Path.Combine(dir, f))) ProviderEvidence.Add(new("Contract file", Path.Combine(dir, f)));
        HasProviderEvidence = ProviderEvidence.Count > 0;
        DistributionReadiness = b.Status == BuildStatus.Succeeded
            ? "Unsigned — not ready for public distribution. The Build Host never signs; sign the output in Executable Maker (with its own approval) before any distribution. Nothing was uploaded or published."
            : "";
        // Unsigned Build Host output is not offered for distribution from here.
        CanShipToStore = false;
    }
}
