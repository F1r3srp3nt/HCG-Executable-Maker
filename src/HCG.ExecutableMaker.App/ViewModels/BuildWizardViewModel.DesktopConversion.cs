using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HCG.ExecutableMaker.BuildEngine.Electron;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.ProjectInspection.Node;
using HCG.ExecutableMaker.Security;

namespace HCG.ExecutableMaker.App.ViewModels;

/// <summary>A browser app (Vite + React) the wizard can turn into a separate desktop workspace.</summary>
public sealed class WebProjectChoice(NodeProjectInfo project)
{
    public NodeProjectInfo Project { get; } = project;
    public string Name => $"{Project.DisplayName}  ·  {string.Join(", ", Project.Frameworks.DefaultIfEmpty("browser app"))}";
    public string Path => Project.ManifestPath;
    public override string ToString() => Name;
}

public sealed class ConversionFindingItem(ConversionFinding finding)
{
    public string Area => finding.Area;
    public string Message => finding.Message;
    public string Location => finding.Location;
    public bool HasLocation => finding.Location.Length > 0;
}

public sealed class ConversionChangeItem(ConversionFileChange change)
{
    private const int MaxPreviewLines = 400;
    public string RelativePath => change.RelativePath;
    public string KindLabel => change.Kind == FileChangeKind.Add ? "New file" : "Changed";
    public string Reason => change.Reason;
    /// <summary>The whole new file, or for a changed file the lines that change (with a little context).</summary>
    public string Preview { get; } = Render(change);

    private static string Render(ConversionFileChange c)
    {
        if (c.After == "(written by npm)") return "Written by npm (npm install --package-lock-only --ignore-scripts): no package script runs.";
        var lines = c.Kind == FileChangeKind.Add ? c.After.Replace("\r\n", "\n").TrimEnd('\n').Split('\n') : TextDiff.Lines(c.Before, c.After).ToArray();
        return lines.Length <= MaxPreviewLines ? string.Join("\n", lines) : string.Join("\n", lines.Take(MaxPreviewLines)) + $"\n… {lines.Length - MaxPreviewLines} more lines";
    }
}

/// <summary>
/// "Prepare desktop app" in the New Build wizard: for a supported browser app (Vite + React) HCG shows exactly what it
/// would add and change, and only on an explicit action writes a SEPARATE desktop workspace (a copy; the imported
/// project is never modified), resolves its lockfile without running package scripts, and imports it as a new project
/// that then builds through the existing Electron pipeline. What the project lacks (configuration) and what this
/// computer lacks (installed toolchains) are reported separately.
/// </summary>
public sealed partial class BuildWizardViewModel
{
    public ObservableCollection<WebProjectChoice> ConvertibleWebProjects { get; } = [];
    [ObservableProperty] private WebProjectChoice? _selectedWebProject;
    [ObservableProperty] private bool _hasConvertibleWebProjects;
    [ObservableProperty] private bool _hasSeveralWebProjects;

    [ObservableProperty] private DesktopConversionPlan? _conversionPlan;
    [ObservableProperty] private StatusTag _conversionTag = new("—", "neutral");
    [ObservableProperty] private string _conversionSummary = "";
    [ObservableProperty] private string _conversionAppName = "";
    [ObservableProperty] private string _conversionAppId = "";
    /// <summary>Name or app id edited since the preview: the preview must be refreshed before anything is written.</summary>
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(CreateDesktopWorkspaceCommand))] private bool _conversionPreviewStale;

    public ObservableCollection<ConversionFindingItem> ConversionBlockers { get; } = [];
    public ObservableCollection<ConversionFindingItem> ConversionWarnings { get; } = [];
    public ObservableCollection<ConversionFindingItem> ConversionInfos { get; } = [];
    public ObservableCollection<ConversionChangeItem> ConversionChanges { get; } = [];
    /// <summary>Missing on THIS computer (Node.js, npm, a host that can package). Fixed on the Toolchains page, not in the project.</summary>
    public ObservableCollection<string> ConversionToolchainGaps { get; } = [];
    [ObservableProperty] private bool _hasConversionBlockers;
    [ObservableProperty] private bool _hasConversionWarnings;
    [ObservableProperty] private bool _hasConversionToolchainGaps;
    [ObservableProperty] private string _conversionToolchainText = "";
    [ObservableProperty] private string _conversionDestinationText = "";

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(CreateDesktopWorkspaceCommand), nameof(PreviewDesktopConversionCommand))] private bool _isConverting;
    [ObservableProperty] private string _conversionStatus = "";
    [ObservableProperty] private string? _conversionError;
    public ObservableCollection<string> ConversionLog { get; } = [];
    [ObservableProperty] private bool _hasConversionLog;
    /// <summary>Shown after the desktop workspace was created and imported.</summary>
    [ObservableProperty] private string? _desktopWorkspaceNotice;

    private NodeInstallation? _conversionNode;
    private CancellationTokenSource? _conversionCts;

    /// <summary>The panel is shown for browser apps; an Electron app selected for building keeps the existing Electron panel.</summary>
    public bool ShowDesktopConversion => HasConvertibleWebProjects && !IsElectronBuild;

    private void RefreshDesktopConversion()
    {
        var keep = SelectedWebProject?.Path;
        ConvertibleWebProjects.Clear();
        if (IsNodeProject)
            foreach (var p in Analysis!.Node!.Projects.Where(p => p.Kind == NodeProjectKind.WebApp)) ConvertibleWebProjects.Add(new WebProjectChoice(p));
        HasConvertibleWebProjects = ConvertibleWebProjects.Count > 0;
        HasSeveralWebProjects = ConvertibleWebProjects.Count > 1;
        OnPropertyChanged(nameof(ShowDesktopConversion));
        OnPropertyChanged(nameof(MissingHint));
        OnPropertyChanged(nameof(MissingTitle));
        // Never choose silently among several web apps; one is selected for the user.
        var next = ConvertibleWebProjects.FirstOrDefault(p => p.Path == keep) ?? (ConvertibleWebProjects.Count == 1 ? ConvertibleWebProjects[0] : null);
        if (ReferenceEquals(next, SelectedWebProject)) PlanConversion(resetIdentity: true);
        else SelectedWebProject = next;
    }

    partial void OnSelectedWebProjectChanged(WebProjectChoice? value) => PlanConversion(resetIdentity: true);
    partial void OnConversionAppNameChanged(string value) => MarkPreviewStale();
    partial void OnConversionAppIdChanged(string value) => MarkPreviewStale();

    private bool _settingIdentity;
    private void MarkPreviewStale()
    {
        if (_settingIdentity || ConversionPlan is null) return;
        ConversionPreviewStale = ConversionAppName.Trim() != ConversionPlan.Options.ProductName || ConversionAppId.Trim() != ConversionPlan.Options.AppId;
    }

    [RelayCommand(CanExecute = nameof(CanPreviewConversion))]
    private void PreviewDesktopConversion() => PlanConversion(resetIdentity: false);
    private bool CanPreviewConversion() => !IsConverting;

    private void PlanConversion(bool resetIdentity)
    {
        ConversionError = null;
        ConversionBlockers.Clear(); ConversionWarnings.Clear(); ConversionInfos.Clear(); ConversionChanges.Clear(); ConversionToolchainGaps.Clear();
        var web = SelectedWebProject?.Project;
        if (Project is null || web is null)
        {
            ConversionPlan = null;
            ConversionTag = new StatusTag("—", "neutral");
            ConversionSummary = HasSeveralWebProjects ? "Choose the browser app to prepare as a desktop app." : "";
            FinishConversionRefresh();
            return;
        }
        var root = Project.SourceDirectory;
        DesktopConversionOptions? options = null;
        if (!resetIdentity) options = new DesktopConversionOptions(ConversionAppId.Trim(), ConversionAppName.Trim());
        DesktopConversionPlan plan;
        try { plan = WebDesktopConversion.Plan(root, web, options ?? WebDesktopConversion.DefaultOptions(web, Path.Combine(root, web.Directory))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ConversionPlan = null;
            ConversionTag = new StatusTag("Unreadable", "error");
            ConversionSummary = $"HCG could not read {web.ManifestPath}: {ex.Message}";
            FinishConversionRefresh();
            return;
        }
        ConversionPlan = plan;
        _settingIdentity = true;
        ConversionAppName = plan.Options.ProductName;
        ConversionAppId = plan.Options.AppId;
        _settingIdentity = false;
        ConversionPreviewStale = false;
        foreach (var f in plan.Findings)
            (f.Level switch { ConversionFindingLevel.Blocker => ConversionBlockers, ConversionFindingLevel.Warning => ConversionWarnings, _ => ConversionInfos })
                .Add(new ConversionFindingItem(f));
        foreach (var c in plan.Changes) ConversionChanges.Add(new ConversionChangeItem(c));

        // Installed toolchains, separately from the project's configuration.
        var report = _toolchains.Latest;
        _conversionNode = null;
        if (report is null) ConversionToolchainGaps.Add("Toolchains have not been detected yet. Open the Toolchains page and press Refresh.");
        else
        {
            var (node, detail) = NodeCapabilities.SelectNode(report, web.EnginesNode);
            if (node is null) ConversionToolchainGaps.Add(detail);
            else if (node.NpmCli is null) ConversionToolchainGaps.Add($"npm was not found next to Node.js {node.Version} ({node.NodePath}). Reinstall Node.js, which includes npm.");
            else { _conversionNode = node; ConversionToolchainText = $"Uses {detail} with npm {node.NpmVersion ?? "(version unknown)"}."; }
            if (NodeCapabilities.Combinations(report.Host).Count == 0)
                ConversionToolchainGaps.Add($"The workspace can be created here, but HCG packages Electron apps only on macOS (.app, zip, dmg) and Linux (app folder). Build it on a Mac for the macOS app; packaging on {report.Host.Platform.DisplayName()} is not implemented.");
        }
        if (_conversionNode is null) ConversionToolchainText = "";

        ConversionTag = !plan.CanApply ? new StatusTag("Not supported", "error")
            : _conversionNode is null ? new StatusTag("Missing tools", "warning")
            : new StatusTag("Ready to prepare", "success");
        ConversionSummary = !plan.CanApply
            ? $"{plan.ProjectName} cannot be prepared as a desktop app automatically. The project needs the changes listed under “Project configuration” first; HCG does not convert arbitrary websites."
            : $"{plan.ProjectName} is a Vite + React browser app. HCG can create a separate desktop workspace that wraps its production build ({plan.OutputDirectory}/) in a secured Electron window — no web server, development server or Terminal is needed to run the installed app.";
        ConversionDestinationText =
            $"Creates a separate new project in HCG from a copy (it appears under Projects as “{plan.Options.ProductName}”). The imported project is not changed. node_modules, {plan.OutputDirectory}/, version-control data and .env files with real values are not copied. npm resolves package-lock.json with --ignore-scripts: no project script runs until you build.";
        FinishConversionRefresh();
    }

    private void FinishConversionRefresh()
    {
        HasConversionBlockers = ConversionBlockers.Count > 0;
        HasConversionWarnings = ConversionWarnings.Count > 0;
        HasConversionToolchainGaps = ConversionToolchainGaps.Count > 0;
        CreateDesktopWorkspaceCommand.NotifyCanExecuteChanged();
    }

    private bool CanCreateDesktopWorkspace() =>
        ConversionPlan is { CanApply: true } && _conversionNode is not null && !ConversionPreviewStale && !IsConverting && Project is not null;

    [RelayCommand(CanExecute = nameof(CanCreateDesktopWorkspace))]
    private async Task CreateDesktopWorkspaceAsync()
    {
        var plan = ConversionPlan!;
        var node = _conversionNode!;
        var sourceRoot = Project!.SourceDirectory;
        var original = Project.Name;
        // Written outside HCG's data folder (the importer refuses that), then imported as a new project and removed.
        var stagingRoot = Path.Combine(Path.GetTempPath(), "hcg-desktop-" + Guid.NewGuid().ToString("N")[..12]);
        var staging = Path.Combine(stagingRoot, Slug(plan.ProjectName) + "-desktop");
        IsConverting = true;
        ConversionError = null;
        ConversionLog.Clear();
        HasConversionLog = false;
        ConversionStatus = "Creating the desktop workspace…";
        _conversionCts = new CancellationTokenSource();
        var ct = _conversionCts.Token;
        try
        {
            var env = BuildEnvironmentPolicy.CreateUntrustedBuildEnvironment().Environment;
            var result = await Task.Run(() => WebDesktopConversion.ApplyAsync(plan, sourceRoot, staging, node, _processRunner, env,
                line => Dispatcher.UIThread.Post(() => { ConversionLog.Add(line); HasConversionLog = true; ConversionStatus = line.Trim(); }), ct), ct);
            if (!result.Success)
            {
                ConversionError = result.Error;
                ConversionStatus = "";
                return;
            }
            ConversionStatus = "Importing the desktop workspace…";
            var changed = string.Join(", ", plan.Changes.Select(c => c.RelativePath));
            await RunImportAsync(token => _projectService.ImportDirectoryAsync(staging, ImportProgressReporter(), token));
            if (Project is not null)
                DesktopWorkspaceNotice = $"Desktop workspace created from “{original}” (unchanged). Added or changed: {changed}. Build it below as an Electron app.";
        }
        catch (OperationCanceledException) { ConversionError = "Cancelled. Nothing was imported."; }
        catch (Exception ex) { ConversionError = "The desktop workspace could not be created: " + ex.Message; }
        finally
        {
            IsConverting = false;
            _conversionCts?.Dispose();
            _conversionCts = null;
            try { if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [RelayCommand] private void CancelDesktopConversion() => _conversionCts?.Cancel();

    /// <summary>Missing toolchains are installed or selected on the Toolchains page (HCG never installs them itself).</summary>
    [RelayCommand] private void OpenToolchainsPage() => _navigation.Navigate("toolchains");

    private void ResetDesktopConversion()
    {
        DesktopWorkspaceNotice = null;
        ConversionError = null;
        ConversionLog.Clear();
        HasConversionLog = false;
        ConversionStatus = "";
    }

    private static string Slug(string name)
    {
        var s = System.Text.RegularExpressions.Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return s.Length == 0 ? "app" : s.Length > 48 ? s[..48].TrimEnd('-') : s;
    }
}
