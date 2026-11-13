using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using HCG.ExecutableMaker.BuildEngine;
using HCG.ExecutableMaker.BuildEngine.Electron;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.ProjectInspection.Node;

namespace HCG.ExecutableMaker.App.ViewModels;

/// <summary>
/// Electron flow of the New Build wizard: choose the Electron app (nested projects included; never auto-selected when
/// there are several), see what it is and what it still needs, then build it with its own electron-builder workflow.
/// Identity, signing and icon come from the project's electron-builder configuration and are shown read-only.
/// </summary>
public sealed partial class BuildWizardViewModel
{
    /// <summary>The Node.js inspector leads this analysis (Electron apps, or Node projects explained as not desktop apps).</summary>
    public bool IsNodeProject => Analysis is { InspectorId: "node", Node: not null };

    public NodeProjectInfo? SelectedNodeProject =>
        IsNodeProject && SelectedProject is not null ? Analysis!.Node!.Projects.FirstOrDefault(p => p.ManifestPath == SelectedProject.Path) : null;

    /// <summary>Configure/Package/Build work on an Electron app (electron-builder) rather than a .NET project.</summary>
    public bool IsElectronBuild => SelectedNodeProject is { Kind: NodeProjectKind.ElectronDesktop } p
                                   && p.DesktopEligibility?.Category != DesktopConversionCategory.ElectronWithoutPackaging;
    /// <summary>Identity and icon are edited in HCG (.NET, Swift); for Electron they come from electron-builder's configuration.</summary>
    public bool IdentityEditable => !IsElectronBuild;

    [ObservableProperty] private StatusTag _electronTag = new("—", "neutral");
    [ObservableProperty] private string _electronStatus = "";
    [ObservableProperty] private string _electronFacts = "";
    [ObservableProperty] private string _electronWorkflow = "";
    /// <summary>What the PROJECT lacks (its configuration): fixed in the project, not by installing tools.</summary>
    public ObservableCollection<string> ElectronRemediation { get; } = [];
    /// <summary>What THIS COMPUTER lacks (installed toolchains): fixed on the Toolchains page.</summary>
    public ObservableCollection<string> ElectronToolchainRemediation { get; } = [];
    [ObservableProperty] private bool _hasElectronToolchainRemediation;
    public ObservableCollection<string> ElectronNotes { get; } = [];
    [ObservableProperty] private bool _hasElectronRemediation;
    [ObservableProperty] private bool _hasElectronNotes;
    /// <summary>Node.js projects in the workspace that are not desktop apps, with what each lacks.</summary>
    [ObservableProperty] private string _otherNodeProjectsText = "";

    private bool ElectronReadyToConfigure =>
        IsElectronBuild && NodeCapabilities.Evaluate(SelectedNodeProject!, _toolchains.Latest).Support == SupportLevel.Supported;

    private void RefreshElectronProject()
    {
        OnPropertyChanged(nameof(IsNodeProject));
        OnPropertyChanged(nameof(SelectedNodeProject));
        OnPropertyChanged(nameof(IsElectronBuild));
        OnPropertyChanged(nameof(IsDotNetBuild));
        OnPropertyChanged(nameof(IdentityEditable));
        ElectronRemediation.Clear();
        ElectronToolchainRemediation.Clear();
        ElectronNotes.Clear();
        OnPropertyChanged(nameof(ShowDesktopConversion));
        OtherNodeProjectsText = Analysis?.Node is { } node && IsNodeProject
            ? string.Join("\n", node.Projects.Where(p => p.Kind != NodeProjectKind.ElectronDesktop)
                .Select(p => $"{p.DisplayDirectory} — {NodeProjectInspector.KindLabel(p.Kind)}{(p.Frameworks.Count > 0 ? " (" + string.Join(", ", p.Frameworks) + ")" : "")}"))
            : "";
        var p = IsElectronBuild ? SelectedNodeProject : null;
        if (p is null)
        {
            ElectronTag = new StatusTag("—", "neutral");
            ElectronStatus = IsNodeProject && Analysis!.Node!.Projects.Any(x => x.Kind == NodeProjectKind.ElectronDesktop) ? "Choose the Electron app to build." : "";
            ElectronFacts = ElectronWorkflow = "";
            FinishElectronRefresh();
            return;
        }
        var a = NodeCapabilities.Evaluate(p, _toolchains.Latest);
        foreach (var r in a.Remediation)
            (p.MissingForDesktop.Contains(r) ? ElectronRemediation : ElectronToolchainRemediation).Add(r);
        foreach (var n in p.Notes) ElectronNotes.Add(n);
        ElectronTag = new StatusTag(a.Support == SupportLevel.Supported ? "Ready" : Format.Label(a.Support), a.Support == SupportLevel.Supported ? "success" : Format.Tone(a.Support));
        ElectronStatus = a.Summary;
        var facts = new List<string> { $"Folder: {p.DisplayDirectory}", $"Evidence: {string.Join("; ", p.Evidence)}" };
        if (p.EnginesNode is { } en) facts.Add($"Requires Node.js {en}" + (a.Requirements.FirstOrDefault(r => r.ToolchainId == ToolchainIds.Node) is { } nr ? $" — {nr.Detail}" : ""));
        facts.Add($"Package manager: {p.PackageManager.ToString().ToLowerInvariant()}" + (p.LockFile is { } lf ? $" ({lf})" : " (no lockfile)"));
        if (p.BuilderConfig is { } c)
        {
            facts.Add($"electron-builder configuration: {c.Source}" + (c.IsScript ? " (script: read when it runs)" : ""));
            if (!c.IsScript)
                facts.Add($"App id {c.AppId ?? "(default)"} · product {c.ProductName ?? p.DisplayName} · output {c.OutputDirectory}/ · mac targets {(c.MacTargets.Count > 0 ? string.Join(", ", c.MacTargets) : "(defaults)")}{(c.MacArchs.Count > 0 ? " for " + string.Join(", ", c.MacArchs) : "")} · mac identity {c.MacIdentity ?? "(auto)"}");
        }
        if (p.HcgDesktopRecord is { } recordPath && Project is not null
            && WebDesktopConversion.ReadRecord(Path.Combine(Project.SourceDirectory, recordPath.Replace('/', Path.DirectorySeparatorChar)), out _) is { } record)
            facts.Insert(0, $"Generated by HCG on {record.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm} from {(record.Source is { } s ? $"“{s.ProjectName}” ({(s.WebDirectory.Length == 0 ? "root" : s.WebDirectory)})" : "a web app")} · Electron {record.ElectronVersion} · " +
                $"{(record.MacIdentity is null ? "ad-hoc signature (not signed for distribution)" : "Developer ID: " + record.MacIdentity)} · {record.Files.Count} generated files recorded in {recordPath}");
        ElectronFacts = string.Join("\n", facts);
        var host = HostInfo.Detect();
        var combo = NodeCapabilities.Combinations(host).FirstOrDefault();
        var script = combo is null ? null : ElectronBuildDefaults.ChooseScript(p, combo.BuilderPlatform);
        ElectronWorkflow = combo is null
            ? ""
            : "Build stages: 1) install dependencies — " + (p.LockFile is "package-lock.json" or "npm-shrinkwrap.json" ? "npm ci" : "npm install") +
              " · 2) compile — " + (script is null ? "none (no packaging script)" : script.CompileSteps.Count == 0 ? $"none (script \"{script.Name}\" has no compile step)" : string.Join(" && ", script.CompileSteps.Select(s => s.Display)) + $" (from \"{script.Name}\")") +
              $" · 3) package — electron-builder --{combo.BuilderPlatform} with the project's configuration, --publish never. Output goes to HCG's build folder; the project's {p.BuilderConfig?.OutputDirectory ?? "dist"}/ folder is not touched.";
        FinishElectronRefresh();
    }

    private void FinishElectronRefresh()
    {
        HasElectronRemediation = ElectronRemediation.Count > 0;
        HasElectronToolchainRemediation = ElectronToolchainRemediation.Count > 0;
        HasElectronNotes = ElectronNotes.Count > 0;
        OnPropertyChanged(nameof(CanConfigure));
        NextCommand.NotifyCanExecuteChanged();
    }

    private NodeCapabilities.Combination? ElectronCombination() =>
        NodeCapabilities.Combinations(HostInfo.Detect()).FirstOrDefault(c => c.Platform == SelectedPlatform?.Value && c.Architecture == SelectedArchitecture?.Value);

    private ElectronBuildOptions? CreateElectronOptions(IReadOnlyList<PackagingFormat> formats)
    {
        if (!IsElectronBuild) return null;
        var combo = ElectronCombination() ?? NodeCapabilities.Combinations(HostInfo.Detect()).FirstOrDefault();
        return combo is null ? null : ElectronBuildDefaults.Options(SelectedNodeProject!, combo, formats);
    }

    private void PrepareElectronConfiguration(bool prefillIdentity)
    {
        var p = SelectedNodeProject!;
        if (prefillIdentity)
        {
            var identity = ElectronBuildDefaults.Identity(p);
            AppName = identity.ApplicationName;
            Version = identity.Version;
            AppId = identity.ApplicationId;
            Description = identity.Description;
            IconPath = null;
            SelectedConfiguration = ConfigurationOptions[0];
        }
        var combos = NodeCapabilities.Combinations(HostInfo.Detect());
        var archs = combos.Select(c => c.Architecture).Distinct().ToList();
        if (!ArchitectureOptions.Select(a => a.Value).SequenceEqual(archs))
        {
            ArchitectureOptions.Clear();
            foreach (var a in archs)
                ArchitectureOptions.Add(new Choice<TargetArchitecture>(a, a == TargetArchitecture.Arm64 ? "ARM64 (Apple Silicon)" : "x64 (Intel / AMD)"));
        }
        // The configured mac architecture (e.g. electron-builder mac.target arch [arm64]) is the default.
        var configured = p.BuilderConfig?.MacArchs.FirstOrDefault() switch { "arm64" => TargetArchitecture.Arm64, "x64" => TargetArchitecture.X64, _ => HostInfo.CurrentArchitecture };
        if (!prefillIdentity && SelectedArchitecture is { } keep && ArchitectureOptions.FirstOrDefault(a => a.Value == keep.Value) is { } same) { SelectedArchitecture = same; return; }
        SelectedArchitecture = ArchitectureOptions.FirstOrDefault(a => a.Value == configured) ?? ArchitectureOptions.FirstOrDefault(a => a.Value == HostInfo.CurrentArchitecture) ?? ArchitectureOptions.FirstOrDefault();
    }

    private void ValidateElectron()
    {
        if (Project is null) return;
        try { ElectronBuildPlanner.Validate(CreateSettings(DefaultElectronFormats()), HostInfo.Detect(), PathGuard.GetRealPath(Project.SourceDirectory)); }
        catch (BuildValidationException ex) { ConfigErrors.Add(ex.Message); }
    }

    /// <summary>For a desktop project HCG generated: the formats chosen when it was created (from hcg-desktop/conversion.json).</summary>
    private IReadOnlyList<PackagingFormat>? RecordedElectronFormats()
    {
        if (SelectedNodeProject?.HcgDesktopRecord is not { } rel || Project is null || HostInfo.CurrentPlatform != TargetPlatform.MacOS) return null;
        var record = WebDesktopConversion.ReadRecord(Path.Combine(Project.SourceDirectory, rel.Replace('/', Path.DirectorySeparatorChar)), out _);
        if (record is null) return null;
        var formats = new List<PackagingFormat> { PackagingFormat.MacAppBundle };
        if (record.MacTargets.Contains("zip")) formats.Add(PackagingFormat.PortableZip);
        if (record.MacTargets.Contains("dmg")) formats.Add(PackagingFormat.MacDmg);
        return formats;
    }

    private static IReadOnlyList<PackagingFormat> DefaultElectronFormats() =>
        HostInfo.CurrentPlatform == TargetPlatform.MacOS ? [PackagingFormat.MacAppBundle] : [PackagingFormat.PortableFolder];
}
