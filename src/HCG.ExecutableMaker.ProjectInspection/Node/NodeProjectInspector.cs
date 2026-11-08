using System.Runtime.CompilerServices;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.ProjectInspection.Node;

/// <summary>
/// Node.js workspaces: Electron desktop apps (built with electron-builder), browser apps, services and libraries.
/// Capability-based: a package.json alone is never "desktop-ready". Purely static (no npm, no scripts, no JS configs).
/// </summary>
public sealed class NodeProjectInspector(TimeProvider time) : IProjectInspector
{
    private static readonly ConditionalWeakTable<SourceIndex, NodeAnalysis> Cache = new();

    public string Id => "node";
    public string DisplayName => "Node.js / Electron";

    public static NodeAnalysis Analyze(SourceIndex index) => Cache.GetValue(index, NodeProjectScanner.Scan);

    /// <summary>
    /// 101 for an Electron app that is the shallowest project (above .NET's 100, below Swift packages and mobile apps);
    /// 75 when a .NET/Swift/Gradle project sits higher in the tree; 30 for other Node.js projects (explained, not built).
    /// </summary>
    public int Match(SourceIndex index)
    {
        if (!index.Named("package.json").Any()) return 0;
        var node = Analyze(index);
        if (node.Projects.Count == 0) return 0;
        var electron = node.Projects.Where(p => p.Kind == NodeProjectKind.ElectronDesktop).ToList();
        if (electron.Count == 0) return 30;
        var depth = electron.Min(p => SourceIndex.Depth(p.ManifestPath));
        var other = index.WithExtension(".csproj", ".fsproj", ".vbproj", ".sln", ".slnx").Concat(index.Named("Package.swift", "settings.gradle", "settings.gradle.kts"))
            .Select(SourceIndex.Depth).DefaultIfEmpty(int.MaxValue).Min();
        return other < depth ? 75 : 101;
    }

    public Task<ProjectAnalysis> InspectAsync(SourceIndex index, ToolchainReport? toolchains, CancellationToken cancellationToken) =>
        Task.Run(() => Inspect(index, toolchains), cancellationToken);

    public ProjectAnalysis Inspect(SourceIndex index, ToolchainReport? toolchains)
    {
        var node = Analyze(index);
        var diagnostics = new List<AnalysisDiagnostic>();
        var electron = node.Projects.Where(p => p.Kind == NodeProjectKind.ElectronDesktop).ToList();
        var assessments = node.Projects.ToDictionary(p => p.ManifestPath, p => NodeCapabilities.Evaluate(p, toolchains));

        foreach (var p in node.Projects)
        {
            var a = assessments[p.ManifestPath];
            diagnostics.Add(new(DiagnosticSeverity.Info, $"{KindLabel(p.Kind)}: {p.DisplayName} — {string.Join("; ", p.Evidence)}", p.ManifestPath));
            if (p.Kind == NodeProjectKind.ElectronDesktop)
            {
                foreach (var gap in p.MissingForDesktop) diagnostics.Add(new(DiagnosticSeverity.Warning, gap, p.ManifestPath));
                foreach (var note in p.Notes) diagnostics.Add(new(DiagnosticSeverity.Info, note, p.ManifestPath));
                foreach (var s in p.PackagingScripts)
                    diagnostics.Add(new(s.Problem is null ? DiagnosticSeverity.Info : DiagnosticSeverity.Warning,
                        s.Problem ?? $"Packaging script \"{s.Name}\": {(s.CompileSteps.Count == 0 ? "" : "compile with " + string.Join(" && ", s.CompileSteps.Select(c => c.Display)) + ", then ")}electron-builder{(s.Platforms.Count > 0 ? " --" + string.Join(" --", s.Platforms) : "")}{(s.Archs.Count > 0 ? " --" + string.Join(" --", s.Archs) : "")}{(s.Targets.Count > 0 ? " (" + string.Join(", ", s.Targets) + ")" : "")}.",
                        p.ManifestPath));
            }
            else if (electron.Count == 0 && p.MissingForDesktop.Count > 0 && p == node.Projects[0])
            {
                foreach (var gap in p.MissingForDesktop) diagnostics.Add(new(DiagnosticSeverity.Warning, gap, p.ManifestPath));
            }
        }
        diagnostics.Add(new(DiagnosticSeverity.Info, "package.json, lockfiles and electron-builder data files were read as text. No npm command, project script or JavaScript configuration was run during inspection."));
        if (electron.Count > 1)
            diagnostics.Add(new(DiagnosticSeverity.Info, $"{electron.Count} Electron apps found: {string.Join(", ", electron.Select(p => p.DisplayDirectory))}. Choose the one to build; none is selected automatically."));

        var language = node.Projects.Any(p => p.Scripts.Values.Any(s => s.Contains("tsc", StringComparison.Ordinal)) || File.Exists(Path.Combine(index.Root, "tsconfig.json")))
            ? "TypeScript/JavaScript" : "JavaScript/TypeScript";
        var support = SupportLevel.RequiresConfiguration;
        string summary;
        IReadOnlyList<ToolchainRequirement> requirements = [];
        IReadOnlyList<string> remediation = [];
        string name;
        string appType;
        if (electron.Count > 0)
        {
            var best = electron.Select(p => (Project: p, A: assessments[p.ManifestPath])).OrderBy(x => Rank(x.A.Support)).First();
            support = best.A.Support;
            summary = best.A.Summary;
            requirements = best.A.Requirements;
            remediation = best.A.Remediation;
            name = electron.Count == 1 ? electron[0].DisplayName : "";
            appType = electron.Count == 1 ? "Electron desktop app" : $"{electron.Count} Electron desktop apps";
        }
        else
        {
            var top = node.Projects[0];
            var a = assessments[top.ManifestPath];
            summary = a.Summary + " " + string.Join(" ", top.MissingForDesktop);
            remediation = top.MissingForDesktop;
            name = top.DisplayName;
            appType = KindLabel(top.Kind);
        }

        var combos = toolchains is null ? [] : NodeCapabilities.Combinations(toolchains.Host);
        return new ProjectAnalysis
        {
            InspectorId = Id,
            ProjectName = name,
            Language = language,
            Framework = electron.Count > 0 ? "Electron" : string.Join(", ", node.Projects[0].Frameworks.DefaultIfEmpty("Node.js")),
            Runtime = "Node.js",
            ApplicationType = appType,
            Support = support,
            SupportSummary = summary,
            Projects = node.Projects.Select(p => ToDetectedProject(p, assessments[p.ManifestPath], combos)).ToList(),
            PrimaryProjectPath = electron.Count == 1 ? electron[0].ManifestPath : null,
            RequiredToolchains = requirements,
            MissingDependencies = remediation,
            RecommendedFormats = combos.SelectMany(c => c.Formats).Distinct().ToList(),
            Diagnostics = diagnostics,
            Node = node,
            AnalyzedAt = time.GetUtcNow(),
        };
    }

    public static string KindLabel(NodeProjectKind kind) => kind switch
    {
        NodeProjectKind.ElectronDesktop => "Electron desktop app",
        NodeProjectKind.WebApp => "Web app (browser only)",
        NodeProjectKind.NodeService => "Node.js service / CLI",
        _ => "Node.js library",
    };

    private static DetectedProject ToDetectedProject(NodeProjectInfo p, NodeCapabilities.Assessment a, IReadOnlyList<NodeCapabilities.Combination> combos) => new()
    {
        Name = p.DisplayName,
        RelativePath = p.ManifestPath,
        Language = "JavaScript/TypeScript",
        Sdk = p.ElectronVersion is { } ev ? "electron " + ev : p.EnginesNode is { } en ? "node " + en : null,
        Version = p.Version,
        Product = p.ProductName,
        Description = p.Description,
        OutputType = p.Kind == NodeProjectKind.ElectronDesktop ? "Electron app" : null,
        ApplicationType = KindLabel(p.Kind),
        IsRunnableApplication = p.Kind == NodeProjectKind.ElectronDesktop,
        CompatiblePlatforms = p.Kind == NodeProjectKind.ElectronDesktop ? combos.Select(c => c.Platform).Distinct().ToList() : [],
        EntryPointFiles = p.MainEntry is { } m ? [NodeProjectScanner.Join(p.Directory, m.TrimStart('.', '/'))] : [],
        PackageReferences = p.ElectronVersion is { } v ? [new PackageDependency("electron", v)] : [],
        Diagnostics = [.. p.MissingForDesktop.Select(m => new AnalysisDiagnostic(DiagnosticSeverity.Warning, m, p.ManifestPath)),
                       .. a.Remediation.Except(p.MissingForDesktop).Select(m => new AnalysisDiagnostic(DiagnosticSeverity.Warning, m, p.ManifestPath))],
    };

    private static int Rank(SupportLevel s) => s switch
    {
        SupportLevel.Supported => 0,
        SupportLevel.MissingToolchain => 1,
        SupportLevel.RequiresConfiguration => 2,
        SupportLevel.RequiresDifferentHost => 3,
        _ => 4,
    };
}
