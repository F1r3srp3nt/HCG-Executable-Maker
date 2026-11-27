using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.ProjectInspection.Mobile;
using HCG.ExecutableMaker.ProjectInspection.Swift;

namespace HCG.ExecutableMaker.ProjectInspection.MacXcode;

/// <summary>
/// "Native macOS app — Xcode": a macOS-only Xcode project with an application target, built with xcodebuild into the
/// project's own .app. Static inspection only: no xcodebuild, no scripts that ship with the project.
/// </summary>
public sealed class MacXcodeProjectInspector(TimeProvider time) : IProjectInspector
{
    public const string InspectorId = "xcode-macos";
    public const string KindLabel = "Native macOS app — Xcode";
    public string Id => InspectorId;
    public string DisplayName => KindLabel;

    /// <summary>
    /// 104 for a macOS Xcode app project (above Swift packages' 102 and .NET's 100) unless a Swift package or a .NET
    /// project sits higher in the tree: then 101 / 84, and the Swift or .NET flow keeps the workspace as before.
    /// </summary>
    public int Match(SourceIndex index)
    {
        var apps = AppProjects(index);
        if (apps.Count == 0) return 0;
        var depth = apps.Min(SourceIndex.Depth);
        var dotnetDepth = index.WithExtension(".csproj", ".fsproj", ".vbproj", ".sln", ".slnx").Select(SourceIndex.Depth).DefaultIfEmpty(int.MaxValue).Min();
        if (dotnetDepth < depth) return 84;
        // A package the app uses as a local Swift package is a dependency, not a competing project.
        var local = MacXcodeProjectAnalyzer.LocalPackageManifests(index);
        var packageDepth = SwiftPackageScanner.FindManifests(index).Where(m => !XcodeProjectAnalyzer.IsIosAppPackage(index, m) && !local.Contains(m))
            .Select(SourceIndex.Depth).DefaultIfEmpty(int.MaxValue).Min();
        return packageDepth < depth ? 101 : 104;
    }

    /// <summary>macOS-only projects that have at least one application target.</summary>
    private static List<string> AppProjects(SourceIndex index) =>
        MacXcodeProjectAnalyzer.FindMacProjects(index).Where(p => MacXcodeProjectAnalyzer.Analyze(index, p).AppTargets.Count > 0).ToList();

    public Task<ProjectAnalysis> InspectAsync(SourceIndex index, ToolchainReport? toolchains, CancellationToken cancellationToken) =>
        Task.Run(() => Inspect(index, toolchains), cancellationToken);

    public ProjectAnalysis Inspect(SourceIndex index, ToolchainReport? toolchains)
    {
        var projects = MacXcodeProjectAnalyzer.FindMacProjects(index).Select(p => MacXcodeProjectAnalyzer.Analyze(index, p))
            .Where(p => p.AppTargets.Count > 0).ToList();
        var primary = projects[0];
        var app = primary.AppTargets[0];
        var assessment = MacXcodeCapabilities.Evaluate(app, toolchains);
        var diagnostics = new List<AnalysisDiagnostic>();

        foreach (var p in projects)
        {
            if (p.ParseError is { } error) diagnostics.Add(new(DiagnosticSeverity.Error, error, p.ProjectPath + "/project.pbxproj"));
            foreach (var script in p.HelperScripts)
                diagnostics.Add(new(DiagnosticSeverity.Info,
                    $"{SourceIndex.FileName(script)} ships with the project. HCG does not run it: it builds with xcodebuild directly (a script may build Debug, open the app or change settings).", script));
            if (p.Schemes.Count == 0)
                diagnostics.Add(new(DiagnosticSeverity.Info, "No shared scheme is committed; xcodebuild creates one per target, named after the target.", p.ProjectPath));
            if (p.UsesCocoaPods && !index.Files.Any(f => f.StartsWith(AppendSlash(p.Directory) + "Pods/", StringComparison.Ordinal)))
                diagnostics.Add(new(DiagnosticSeverity.Warning, "Podfile found but Pods/ is not present. Run `pod install` yourself before building (HCG never runs it).", p.Directory));
            foreach (var r in p.PackageReferences.Where(r => r.IsLocal))
                diagnostics.Add(r.ManifestPath is null
                    ? new(DiagnosticSeverity.Error, $"Local Swift package {r.Location} is referenced by the project but its Package.swift is not in the imported source: the build cannot resolve it.", p.ProjectPath)
                    : new(DiagnosticSeverity.Info, $"Local Swift package {r.Location} (in the source; no download). xcodebuild builds it as part of the app; it is not built as an app of its own.", r.ManifestPath));
            var remote = p.PackageReferences.Where(r => !r.IsLocal).ToList();
            if (remote.Count > 0)
                diagnostics.Add(new(DiagnosticSeverity.Info,
                    $"Remote Swift packages: {string.Join(", ", remote.Select(r => r.Location + (r.Requirement is { } q ? $" ({q})" : "")))}. xcodebuild resolves them in a separate step into the build folder (network access required)." +
                    (p.ResolvedPackages.Count > 0 ? $" Package.resolved pins {p.ResolvedPackages.Count}: {string.Join(", ", p.ResolvedPackages.Select(x => $"{x.Identity} {x.Version ?? x.Revision?[..Math.Min(7, x.Revision.Length)]}"))}." : " No Package.resolved is committed: versions are chosen at resolution time."),
                    p.ProjectPath));
            if (p.AppIconSet is { } iconSet && p.AppIconImages.Count == 0)
                diagnostics.Add(new(DiagnosticSeverity.Warning, $"{iconSet} has no images: the app will show the generic application icon.", iconSet));
            foreach (var (file, keys) in p.EntitlementFiles)
                diagnostics.Add(new(DiagnosticSeverity.Info, $"Entitlements file {SourceIndex.FileName(file)}: {(keys.Count == 0 ? "no entitlements" : string.Join(", ", keys))}.", file));
            foreach (var t in p.AppTargets)
            {
                diagnostics.Add(new(DiagnosticSeverity.Info,
                    $"Target {t.Name}: signing {t.SigningDescription}. HCG keeps the project's signing (Xcode signs during the build); for a Developer ID build, set your Developer ID identity in the project. Notarization is a separate step.", p.ProjectPath));
                if (t.ConfigurationFacts.Count > 1)
                    diagnostics.Add(new(DiagnosticSeverity.Info, $"Target {t.Name} by configuration — " + string.Join("; ", t.ConfigurationFacts.Select(Describe)) + ".", p.ProjectPath));
            }
            if (p.LicenseFiles.Count > 0)
                diagnostics.Add(new(DiagnosticSeverity.Info, $"Licence/attribution: {string.Join(", ", p.LicenseFiles.Select(SourceIndex.FileName))}. Shipped unchanged next to the app and inside the DMG; the app keeps its own name, bundle ID and icon.", p.Directory));
        }
        if (assessment.Note is { } note) diagnostics.Add(new(DiagnosticSeverity.Warning, note));

        var ui = primary.UiFrameworks.Count > 0 ? string.Join(" + ", primary.UiFrameworks) : "Cocoa";
        return new ProjectAnalysis
        {
            InspectorId = Id,
            ProjectName = app.ProductName ?? app.Name,
            Language = primary.Languages.Count > 0 ? string.Join(" / ", primary.Languages) : "Swift / Objective-C",
            Framework = $"{ui} (Xcode)",
            Runtime = app.DeploymentTarget is { } dt ? $"macOS {dt}+" : null,
            ApplicationType = KindLabel,
            Support = assessment.Support,
            SupportSummary = assessment.Support == SupportLevel.Supported ? assessment.Summary : assessment.Summary + " " + string.Join(" ", assessment.Remediation),
            RequiredToolchains = assessment.Requirements,
            PrimaryProjectPath = primary.ProjectPath,
            Projects = projects.SelectMany(p => p.AppTargets.Select(t => new DetectedProject
            {
                Name = t.ProductName ?? t.Name,
                RelativePath = p.ProjectPath,
                Language = p.Languages.FirstOrDefault() ?? "Swift",
                ApplicationType = KindLabel,
                IsRunnableApplication = true,
                Version = t.MarketingVersion,
                CompatiblePlatforms = [TargetPlatform.MacOS],
            })).ToList(),
            RecommendedFormats = [PackagingFormat.MacAppBundle, PackagingFormat.PortableZip, PackagingFormat.MacDmg],
            DetectedIcons = projects.Select(p => p.AppIconSet).OfType<string>().ToList(),
            MacXcode = new MacXcodeAnalysis { Projects = projects },
            Diagnostics = diagnostics,
            AnalyzedAt = time.GetUtcNow(),
        };
    }

    /// <summary>One configuration in a line: bundle ID, sandbox, hardened runtime, entitlements, compilation conditions.</summary>
    public static string Describe(MacXcodeConfigurationFacts c) =>
        $"{c.Name}: {c.BundleIdentifier ?? "bundle ID from build settings"}, App Sandbox {(c.AppSandbox switch { true => "on", false => "off", _ => "not set" })}" +
        $", hardened runtime {(c.HardenedRuntime ? "on" : "off")}" +
        (c.Entitlements is { } e ? $", entitlements {e}" : "") +
        (c.SandboxAccess.Count > 0 ? ", " + string.Join(", ", c.SandboxAccess.Select(kv => $"{kv.Key}={kv.Value}")) : "") +
        (c.CompilationConditions.Count > 0 ? ", conditions " + string.Join(" ", c.CompilationConditions) : "");

    private static string AppendSlash(string dir) => dir.Length == 0 ? "" : dir + "/";
}
