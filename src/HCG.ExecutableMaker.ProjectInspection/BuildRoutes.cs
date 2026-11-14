using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.ProjectInspection.Native;

namespace HCG.ExecutableMaker.ProjectInspection;

/// <summary>One way to build what an imported folder contains, offered when it holds more than one kind of project.</summary>
public sealed record BuildRoute(string Id, string Label, string Description)
{
    public override string ToString() => Label;
}

/// <summary>
/// Build routes of one imported folder. A folder can hold both an app project (e.g. an Xcode iOS app) and a Theos
/// jailbreak project (HCG Nexus: HCGNexus.xcodeproj next to the tweak's Makefile, control, Prefs/ and PreviewHarness/).
/// The inspection keeps the leading analysis and attaches the Theos projects (<see cref="ProjectAnalysis.Native"/>);
/// New Build then offers one route per kind and shows the chosen route's own analysis (<see cref="View"/>). The routes
/// never mix: the Theos route only produces a .deb (never an IPA), the Xcode route only an IPA/archive (never a .deb).
/// </summary>
public static class BuildRoutes
{
    /// <summary>The leading analysis of a project that is not an Xcode iOS app (e.g. a .NET or Node.js project).</summary>
    public const string Primary = "primary";
    /// <summary>The Xcode iOS app: archived and exported as an IPA in Mobile Build Studio.</summary>
    public const string XcodeIpa = "xcode-ipa";
    /// <summary>The Theos projects: built with make package into a .deb for jailbroken devices.</summary>
    public const string TheosDeb = "theos-deb";

    public const string XcodeIpaLabel = "iOS app (IPA, Xcode)";
    public const string TheosDebLabel = "Jailbreak package (.deb, Theos)";

    /// <summary>Theos projects with at least one product (tweak, preference bundle, app, tool, library…).</summary>
    public static IReadOnlyList<NativeProjectInfo> TheosProjects(NativeAnalysis? native) =>
        native?.Projects.Where(p => p is { Kind: NativeProjectKind.Theos, Theos.Instances.Count: > 0 }).ToList() ?? [];

    /// <summary>
    /// The routes to choose between: none when the folder has a single kind of project (including a folder led by the
    /// Theos project itself), otherwise the leading route first and the Theos route second.
    /// </summary>
    public static IReadOnlyList<BuildRoute> For(ProjectAnalysis analysis)
    {
        if (analysis.InspectorId is "native" or "none") return [];
        var theos = TheosProjects(analysis.Native);
        if (theos.Count == 0) return [];
        var packages = string.Join(", ", theos.Select(p => p.Theos!.PackageName ?? p.Name));
        return [Lead(analysis), new BuildRoute(TheosDeb, TheosDebLabel,
            $"Builds the Theos project{(theos.Count > 1 ? "s" : "")} ({packages}) with make package into a Debian package (.deb) for jailbroken devices. Never produces or exports an IPA.")];
    }

    /// <summary>The leading route of an analysis (whether or not a second route exists).</summary>
    public static BuildRoute Lead(ProjectAnalysis analysis) =>
        IsXcodeIosApp(analysis)
            ? new BuildRoute(XcodeIpa, XcodeIpaLabel,
                $"Archives {analysis.Mobile!.Ios!.ProjectPath ?? "the Xcode project"} with Xcode and exports an IPA in Mobile Build Studio. Never packages a .deb.")
            : new BuildRoute(Primary, $"{analysis.ApplicationType} ({analysis.Framework})", $"Builds the {analysis.Framework} project. Never packages a .deb.");

    private static bool IsXcodeIosApp(ProjectAnalysis a) => a.Mobile is { Framework: MobileFramework.NativeIos, Ios: not null };

    /// <summary>
    /// The analysis New Build shows for a route. The leading route is the inspection result itself (its inspector is not
    /// "native", so the attached Theos projects are not offered there); the Theos route is the C/C++ (Theos) analysis of
    /// the Theos projects only, exactly as when the Theos project leads. Any other route id returns the leading view.
    /// </summary>
    public static ProjectAnalysis View(ProjectAnalysis analysis, string? routeId, ToolchainReport? toolchains)
    {
        var routes = For(analysis);
        if (routes.Count == 0 || routeId != TheosDeb) return analysis;
        var theos = NativeProjectInspector.Build(new NativeAnalysis { Projects = TheosProjects(analysis.Native) }, toolchains, analysis.AnalyzedAt);
        var lead = routes[0];
        return theos with
        {
            DetectedIcons = analysis.DetectedIcons,
            Diagnostics =
            [
                .. theos.Diagnostics,
                new AnalysisDiagnostic(DiagnosticSeverity.Info, $"This folder also contains another project: choose “{lead.Label}” under Build route to build it instead. This route builds only the .deb."),
            ],
        };
    }
}
