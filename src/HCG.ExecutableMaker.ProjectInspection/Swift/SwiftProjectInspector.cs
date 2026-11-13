using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.ProjectInspection.Mobile;

namespace HCG.ExecutableMaker.ProjectInspection.Swift;

/// <summary>
/// Swift Package Manager desktop projects (and macOS-only Xcode projects, which are recognized but not built in this
/// increment). Purely static: Package.swift is read as text, never executed.
/// </summary>
public sealed class SwiftProjectInspector(TimeProvider time) : IProjectInspector
{
    public string Id => "swift";
    public string DisplayName => "Swift (macOS)";

    /// <summary>
    /// 102 when a Swift package is the top-level project (above .NET's 100); 85 when a .NET project sits higher in the
    /// tree (e.g. a .NET repository that only contains Swift test fixtures).
    /// </summary>
    public int Match(SourceIndex index)
    {
        var manifests = SwiftPackageScanner.FindManifests(index)
            .Where(m => !XcodeProjectAnalyzer.IsIosAppPackage(index, m)).ToList();
        var macProjects = XcodeProjectAnalyzer.FindProjects(index).Where(p => XcodeProjectAnalyzer.IsMacOnlyProject(index, p)).ToList();
        if (manifests.Count == 0 && macProjects.Count == 0) return 0;
        var swiftDepth = manifests.Concat(macProjects).Min(SourceIndex.Depth);
        var dotnetDepth = index.WithExtension(".csproj", ".fsproj", ".vbproj", ".sln", ".slnx").Select(SourceIndex.Depth).DefaultIfEmpty(int.MaxValue).Min();
        if (dotnetDepth < swiftDepth) return 85;
        return manifests.Count > 0 ? 102 : 60;
    }

    public Task<ProjectAnalysis> InspectAsync(SourceIndex index, ToolchainReport? toolchains, CancellationToken cancellationToken) =>
        Task.Run(() => Inspect(index, toolchains), cancellationToken);

    public ProjectAnalysis Inspect(SourceIndex index, ToolchainReport? toolchains)
    {
        var packages = SwiftPackageScanner.FindManifests(index)
            .Where(m => !XcodeProjectAnalyzer.IsIosAppPackage(index, m))
            .Select(m => SwiftPackageScanner.Scan(index, m)).ToList();
        var macProjects = XcodeProjectAnalyzer.FindProjects(index).Where(p => XcodeProjectAnalyzer.IsMacOnlyProject(index, p)).ToList();
        var diagnostics = new List<AnalysisDiagnostic>();
        var swift = new SwiftAnalysis { Packages = packages, MacXcodeProjects = macProjects };

        foreach (var mac in macProjects)
            diagnostics.Add(new(DiagnosticSeverity.Warning,
                "macOS Xcode project found. Building Xcode macOS projects/workspaces is not implemented in this version; HCG builds Swift Package Manager packages. " +
                "If the app also has a Package.swift, select that package instead.", mac));

        if (packages.Count == 0)
        {
            return new ProjectAnalysis
            {
                InspectorId = Id,
                ProjectName = Path.GetFileNameWithoutExtension(macProjects[0]),
                Language = "Swift / Objective-C",
                Framework = "Xcode (macOS)",
                ApplicationType = "macOS Xcode project",
                Support = SupportLevel.Unsupported,
                SupportSummary = "macOS Xcode projects and workspaces are not built in this version. Swift Package Manager packages (Package.swift) are supported on a Mac. iOS Xcode projects are handled in Mobile Build Studio.",
                Diagnostics = diagnostics,
                Swift = swift,
                AnalyzedAt = time.GetUtcNow(),
            };
        }

        var assessments = packages.Select(p => (Package: p, Assessment: SwiftCapabilities.Evaluate(p, toolchains))).ToList();
        foreach (var p in packages)
        {
            diagnostics.Add(new(DiagnosticSeverity.Info,
                "Package.swift is Swift code and was NOT executed. Its products are confirmed by evaluating it on this Mac after you trust the project (Inspect step → Evaluate package)." +
                (p.HasConditionalDeclarations ? " It declares some products conditionally (#if os(…)), so the product list depends on the build host." : ""),
                p.ManifestPath));
            if (p.SourceFacts.Count > 0 && toolchains?.Host.Platform == TargetPlatform.MacOS)
                diagnostics.Add(new(DiagnosticSeverity.Info,
                    toolchains.IsAvailable(ToolchainIds.XcTest)
                        ? "Tests: HCG builds the selected product only; test targets are not compiled or run. XCTest is available if you run them yourself (swift test)."
                        : "Tests: HCG builds the selected product only; test targets are not compiled or run. XCTest was not found in the selected developer directory — that affects only `swift test`, not building or packaging the app.",
                    p.ManifestPath));
            if (p.HasPackageDependencies)
                diagnostics.Add(new(DiagnosticSeverity.Info, "This package depends on other packages; SwiftPM downloads them during the build (network access required).", p.ManifestPath));
            if (p.ToolsVersion is null)
                diagnostics.Add(new(DiagnosticSeverity.Warning, "The first line has no '// swift-tools-version:' comment; SwiftPM will reject this manifest.", p.ManifestPath));
            foreach (var facts in p.SourceFacts)
                foreach (var usage in facts.PrivacyUsages)
                    diagnostics.Add(new(DiagnosticSeverity.Info,
                        $"Target {facts.Target} uses the {usage.Purpose} ({usage.Evidence}). The app needs {usage.InfoPlistKey} in its Info.plist; HCG asks for the text before building.",
                        SwiftPackageScanner.Join(p.Directory, facts.Path)));
        }
        if (packages.Count > 1)
            diagnostics.Add(new(DiagnosticSeverity.Info, $"{packages.Count} Swift packages found: {string.Join(", ", packages.Select(p => p.DisplayDirectory))}. Choose the package to build; none is selected automatically."));

        // Overall: the best support level among packages (each package is assessed on its own in the wizard).
        var best = assessments.OrderBy(a => Rank(a.Assessment.Support)).First();
        // Several packages: no single name; ProjectService keeps the import's name (e.g. the repository folder).
        var name = packages.Count == 1 ? packages[0].NameHint ?? LastSegment(packages[0].Directory, index.Root) : "";
        var summary = best.Assessment.Summary + (best.Assessment.Remediation.Count > 0 ? " " + string.Join(" ", best.Assessment.Remediation) : "");

        return new ProjectAnalysis
        {
            InspectorId = Id,
            ProjectName = name,
            Language = "Swift",
            Framework = "Swift Package Manager",
            Runtime = "macOS",
            ApplicationType = packages.Count == 1 ? "Swift package" : $"{packages.Count} Swift packages",
            Support = best.Assessment.Support,
            SupportSummary = summary,
            Projects = packages.Select(p => ToDetectedProject(p, index.Root)).ToList(),
            // Several packages: the user must choose. One package: it is the obvious choice.
            PrimaryProjectPath = packages.Count == 1 ? packages[0].ManifestPath : null,
            RequiredToolchains = best.Assessment.Requirements,
            MissingDependencies = best.Assessment.Remediation,
            RecommendedFormats = [PackagingFormat.MacAppBundle],
            Diagnostics = diagnostics,
            Swift = swift,
            AnalyzedAt = time.GetUtcNow(),
        };
    }

    private static DetectedProject ToDetectedProject(SwiftPackageInfo p, string root)
    {
        var gui = p.SourceFacts.Where(f => f.SuggestedKind == SwiftAppKind.GuiApp).Select(f => f.Target).ToList();
        var kinds = new List<string>();
        if (gui.Count > 0) kinds.Add("SwiftUI/AppKit app");
        if (p.SourceFacts.Any(f => f.SuggestedKind == SwiftAppKind.CommandLine)) kinds.Add("command-line tool");
        return new DetectedProject
        {
            Name = p.NameHint ?? (LastSegment(p.Directory, root) is { Length: > 0 } n ? n : "Swift package"),
            RelativePath = p.ManifestPath,
            Language = "Swift",
            Sdk = p.ToolsVersion is null ? null : "swift-tools-version " + p.ToolsVersion,
            TargetFrameworks = p.MinimumMacOSHint is null ? [] : ["macOS " + p.MinimumMacOSHint],
            OutputType = "Exe",
            ApplicationType = kinds.Count == 0 ? "Swift package (products confirmed after evaluation)" : "Swift package: " + string.Join(" + ", kinds) + " (unconfirmed until evaluated)",
            CompatiblePlatforms = [TargetPlatform.MacOS],
            IsRunnableApplication = true,
            EntryPointFiles = p.SourceFacts.Select(f => SwiftPackageScanner.Join(p.Directory, f.Path)).ToList(),
        };
    }

    /// <summary>Last folder name of a package directory; empty for the workspace root (the import's own name is used then).</summary>
    private static string LastSegment(string dir, string root) =>
        dir.Length == 0 ? "" : dir[(dir.LastIndexOf('/') + 1)..];

    private static int Rank(SupportLevel s) => s switch
    {
        SupportLevel.Supported => 0,
        SupportLevel.MissingToolchain => 1,
        SupportLevel.RequiresConfiguration => 2,
        SupportLevel.RequiresDifferentHost => 3,
        _ => 4,
    };
}
