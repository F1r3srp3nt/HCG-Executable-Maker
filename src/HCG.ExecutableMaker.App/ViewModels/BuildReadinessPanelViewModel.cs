using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HCG.ExecutableMaker.App.Services;
using HCG.ExecutableMaker.BuildEngine.Readiness;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.App.ViewModels;

/// <summary>
/// The Build Readiness Center panel, shared by Toolchains and the New Build wizard: every tool the build (or project)
/// needs, what this computer has, and per tool: Install… (only after the person approves the exact command), Choose
/// location… (this project's profile or all projects), the official download page, and Refresh. After a fix, the
/// check runs again and the wizard's Start Build can be pressed again.
/// </summary>
public sealed partial class BuildReadinessPanelViewModel(BuildReadinessService readiness, IToolchainService toolchains, IDialogService dialogs, IPlatformShell shell) : ViewModelBase
{
    private string _projectName = "";
    private string? _sourceRoot;
    private Guid? _projectId;
    private BuildSettings? _build;
    private ProjectAnalysis? _analysis;
    private bool _loadingProfiles;

    public ObservableCollection<ReadinessToolItem> Items { get; } = [];
    public ObservableCollection<ToolchainProfileChoice> Profiles { get; } = [];
    [ObservableProperty] private ToolchainProfileChoice? _selectedProfile;
    [ObservableProperty] private bool _isChecking;
    [ObservableProperty] private bool _hasReport;
    [ObservableProperty] private bool _canBuild;
    [ObservableProperty] private string _summary = "Choose a project to check what its builds need.";
    [ObservableProperty] private StatusTag _state = new("Not checked", "neutral");
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _installLog = "";
    [ObservableProperty] private bool _isInstalling;
    public bool HasMessage => Message.Length > 0;
    public bool HasInstallLog => InstallLog.Length > 0;
    public bool HasProject => _projectId is not null;
    partial void OnMessageChanged(string value) => OnPropertyChanged(nameof(HasMessage));
    partial void OnInstallLogChanged(string value) => OnPropertyChanged(nameof(HasInstallLog));

    /// <summary>The last check (null before the first).</summary>
    public BuildReadinessReport? Report { get; private set; }

    /// <summary>Sets what is checked: a configured build (<paramref name="build"/>) or, without one, the project as a whole.</summary>
    public async Task<BuildReadinessReport?> CheckAsync(string projectName, string sourceRoot, Guid? projectId, BuildSettings? build, ProjectAnalysis? analysis)
    {
        _projectName = projectName;
        _sourceRoot = sourceRoot;
        _projectId = projectId;
        _build = build;
        _analysis = analysis;
        OnPropertyChanged(nameof(HasProject));
        await LoadProfilesAsync();
        return await RecheckAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        Message = "Detecting toolchains again…";
        try { await toolchains.GetAsync(refresh: true); }
        catch (Exception ex) { Message = "Toolchain detection failed: " + ex.Message; }
        await RecheckAsync();
    }

    private async Task<BuildReadinessReport?> RecheckAsync()
    {
        if (_sourceRoot is null) return null;
        IsChecking = true;
        try
        {
            var report = await Task.Run(() => readiness.CheckAsync(_projectName, _sourceRoot, _projectId, _build, _analysis));
            Apply(report);
            if (Message.StartsWith("Detecting", StringComparison.Ordinal)) Message = "";
            return report;
        }
        catch (Exception ex)
        {
            Message = "The readiness check failed: " + ex.Message;
            return null;
        }
        finally { IsChecking = false; }
    }

    private void Apply(BuildReadinessReport report)
    {
        Report = report;
        Items.Clear();
        foreach (var i in report.Items.OrderByDescending(i => i.Blocks).ThenByDescending(i => i.Requirement.Required)) Items.Add(new ReadinessToolItem(i));
        HasReport = true;
        CanBuild = report.CanBuild;
        var blockers = report.Blockers.Count();
        State = report.Items.Count == 0 ? new StatusTag("Nothing to check", "neutral")
            : blockers == 0 ? new StatusTag("Ready", "success") : new StatusTag($"{blockers} missing", "warning");
        Summary = report.Items.Count == 0
            ? "No build tools are declared by this project's files."
            : blockers == 0
                ? $"Every required tool for {report.Project} ({report.Target}) is available{(report.Profile is null ? "" : $" (profile “{report.Profile}”)")}."
                : $"{blockers} required tool(s) for {report.Project} ({report.Target}) are missing. Install, choose a location, or use another build host, then build again.";
    }

    // ---------- Actions ----------

    [RelayCommand]
    private async Task InstallAsync(ReadinessToolItem? item)
    {
        if (item?.InstallOption is not { } option || IsInstalling) return;
        var ok = await dialogs.ConfirmAsync($"Install {item.Name}?",
            $"HCG Executable Maker will run exactly this command:\n\n{option.CommandLine}\n\n{option.Note}\n\nNothing else is installed, upgraded or removed. You can also run it yourself in Terminal.",
            "Install");
        if (!ok) { Message = $"{item.Name}: not installed (you cancelled)."; return; }
        IsInstalling = true;
        InstallLog = "";
        var log = new System.Text.StringBuilder();
        try
        {
            var result = await Task.Run(() => readiness.InstallAsync(option, approved: true, line =>
            {
                lock (log) { log.AppendLine(line); }
                Avalonia.Threading.Dispatcher.UIThread.Post(() => { lock (log) { InstallLog = Tail(log.ToString()); } });
            }));
            Message = result.Succeeded ? $"{item.Name}: installed. Checked again below." : $"{item.Name}: the install command failed (exit code {result.ExitCode}). The output is shown below.";
        }
        catch (Exception ex) { Message = $"{item.Name}: {ex.Message}"; }
        finally { IsInstalling = false; }
        await RecheckAsync();
    }

    [RelayCommand] private Task ChooseForProjectAsync(ReadinessToolItem? item) => ChooseAsync(item, project: true);
    [RelayCommand] private Task ChooseForAllAsync(ReadinessToolItem? item) => ChooseAsync(item, project: false);

    private async Task ChooseAsync(ReadinessToolItem? item, bool project)
    {
        if (item is null || !item.CanChoose) return;
        if (project && _projectId is null) { Message = "Choose a project first."; return; }
        var picked = await shell.PickFileAsync($"Choose the {item.Name} executable ({string.Join(" or ", item.Status.ExecutableNames)})", item.Name, "*");
        if (picked is null) return;
        ToolPathScope scope = project ? new ToolPathScope.Project(_projectId!.Value, _projectName) : new ToolPathScope.AllProjects();
        var (check, saved) = await readiness.ChooseLocationAsync(item.Status.Requirement.ToolId, picked, item.Status.Requirement.MinimumVersion, scope);
        Message = saved ? $"{item.Name}: {check.Detail}. Your PATH and shell profiles were not changed." : $"{item.Name}: not saved — {check.Detail}";
        if (saved) { await LoadProfilesAsync(); await RecheckAsync(); }
    }

    [RelayCommand]
    private async Task ClearLocationAsync(ReadinessToolItem? item)
    {
        if (item is null) return;
        await readiness.ClearLocationAsync(item.Status.Requirement.ToolId, new ToolPathScope.AllProjects());
        if (_projectId is { } id) await readiness.ClearLocationAsync(item.Status.Requirement.ToolId, new ToolPathScope.Project(id, _projectName));
        Message = $"{item.Name}: chosen locations removed; automatic detection applies.";
        await RecheckAsync();
    }

    [RelayCommand] private async Task OpenDownloadAsync(ReadinessToolItem? item) { if (item?.OfficialUrl is { } u) await shell.OpenUrlAsync(u); }

    [RelayCommand]
    private async Task CopyCommandAsync(ReadinessToolItem? item)
    {
        if (item?.CommandText is { Length: > 0 } c) { await shell.CopyAsync(c); Message = "Copied: " + c; }
    }

    // ---------- Profiles ----------

    private async Task LoadProfilesAsync()
    {
        _loadingProfiles = true;
        try
        {
            Profiles.Clear();
            Profiles.Add(new ToolchainProfileChoice(null, "HCG defaults (automatic detection and locations chosen for all projects)"));
            foreach (var p in await readiness.ProfilesAsync()) Profiles.Add(new ToolchainProfileChoice(p.Id, $"{p.Name} — {(p.ToolPaths.Count == 0 ? "no tools pinned" : string.Join(", ", p.ToolPaths.Keys))}"));
            var current = _projectId is null ? null : (await readiness.ProfilesAsync()).FirstOrDefault(p => Report?.Profile == p.Name);
            SelectedProfile = Profiles.FirstOrDefault(p => p.Id == current?.Id) ?? Profiles[0];
        }
        finally { _loadingProfiles = false; }
    }

    partial void OnSelectedProfileChanged(ToolchainProfileChoice? value)
    {
        if (_loadingProfiles || value is null || _projectId is not { } id) return;
        _ = AssignAsync(id, value);
    }

    private async Task AssignAsync(Guid id, ToolchainProfileChoice value)
    {
        try
        {
            await readiness.AssignProfileAsync(id, value.Id);
            Message = value.Id is null ? $"{_projectName} uses HCG's defaults." : $"{_projectName} uses the profile {value.Label.Split(" — ")[0]}.";
            await RecheckAsync();
        }
        catch (InvalidOperationException ex) { Message = ex.Message; }
    }

    [RelayCommand]
    private async Task NewProfileAsync()
    {
        if (_projectId is not { } id) { Message = "Choose a project first."; return; }
        var profile = await readiness.CreateProfileAsync($"{_projectName} toolchain");
        await readiness.AssignProfileAsync(id, profile.Id);
        await LoadProfilesAsync();
        SelectedProfile = Profiles.FirstOrDefault(p => p.Id == profile.Id);
        Message = $"Created the profile “{profile.Name}” for {_projectName}. Use “Choose for this project…” to pin tool locations in it; other projects are not affected.";
        await RecheckAsync();
    }

    private static string Tail(string s) => s.Length <= 6000 ? s : "…" + s[^6000..];
}

public sealed record ToolchainProfileChoice(string? Id, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One requirement row of the Build Readiness panel.</summary>
public sealed class ReadinessToolItem(RequirementStatus status)
{
    public RequirementStatus Status { get; } = status;
    public string Name => Status.Requirement.DisplayName;
    public StatusTag Tag => new(Format.Label(Status.State), Format.Tone(Status.State, Status.Requirement.Required));
    public string Needs => (Status.Requirement.MinimumVersion is { } v ? $"{v} or newer" : "any version") + (Status.Requirement.Required ? "" : " · optional");
    public string Using => Status.Selected is { } s ? $"{s.Path} · {(s.Version is null ? "version not reported" : "version " + s.Version)}{(s.Source.Length > 0 ? " · " + s.Source : "")}" : "—";
    public string DeclaredBy => Status.Requirement.DeclaredBy;
    public string Purpose => Status.Requirement.Purpose;
    public string Detail => Status.State == ReadinessState.Ready ? "" : Status.Detail;
    public bool HasDetail => Detail.Length > 0;
    public string Found => string.Join("\n", Status.Found.Where(f => f != Status.Selected).Select(f => $"Also found: {f.Path} · {f.Version ?? "version not reported"}{(f.Source.Length > 0 ? " · " + f.Source : "")}"));
    public bool HasFound => Found.Length > 0;
    public string Notes => string.Join("\n", Status.Requirement.Notes);
    public bool HasNotes => Notes.Length > 0;
    public InstallOption? InstallOption => Status.Install.FirstOrDefault(o => o.Runnable);
    public bool CanInstall => InstallOption is not null;
    public string InstallLabel => InstallOption?.Label ?? "";
    /// <summary>The command shown for the person to run themselves (or HCG's runnable one).</summary>
    public string? CommandText => (InstallOption ?? Status.Install.FirstOrDefault(o => o.CommandLine is not null))?.CommandLine;
    public bool HasCommand => !string.IsNullOrEmpty(CommandText);
    public string InstallHelp => string.Join("\n", Status.Install.Where(o => !o.Runnable).Select(o => $"{o.Label}{(o.CommandLine is null ? "" : $": {o.CommandLine}")}{(o.Note is null ? "" : $" — {o.Note}")}"));
    public bool HasInstallHelp => InstallHelp.Length > 0 && Status.State != ReadinessState.Ready;
    public string? OfficialUrl => Status.OfficialUrl;
    public bool HasOfficialUrl => !string.IsNullOrEmpty(OfficialUrl) && Status.State != ReadinessState.Ready;
    public bool CanChoose => Status.Requirement.ToolId != "xcode" && Status.State != ReadinessState.OtherHost;
    public string Remote => string.Join("\n", Status.Remote.Select(r => $"{r.Label} has {Name} {r.ToolVersion}. {r.Transfer}"));
    public bool HasRemote => Remote.Length > 0;
}
