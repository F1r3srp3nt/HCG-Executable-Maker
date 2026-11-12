using System.Runtime.CompilerServices;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.ProjectInspection.Native;

/// <summary>
/// C/C++ workspaces: CMake, Makefile and source-only projects (built for this computer's platform), Theos projects
/// (recognized, never built as desktop apps) and Meson / Autotools / Visual Studio projects (recognized, not built).
/// Static: no CMake, make or compiler runs during inspection. A CMake project's executable targets appear under
/// Project to build after the user-trusted configure step read them through the CMake File API.
/// </summary>
public sealed class NativeProjectInspector(TimeProvider time) : IProjectInspector
{
    private static readonly ConditionalWeakTable<SourceIndex, NativeAnalysis> Cache = new();

    public string Id => "native";
    public string DisplayName => "C / C++";

    public static NativeAnalysis Analyze(SourceIndex index) => Cache.GetValue(index, NativeProjectScanner.Scan);

    /// <summary>
    /// 40 when a C/C++ project is found (below .NET, Swift, Electron and mobile projects); 25 when a package.json,
    /// Package.swift or .NET project sits at the same depth or higher, so a convenience Makefile in a web or .NET
    /// repository never hides the real project.
    /// </summary>
    public int Match(SourceIndex index)
    {
        var native = Analyze(index);
        if (native.Projects.Count == 0) return 0;
        var depth = native.Projects.Min(p => SourceIndex.Depth(p.ManifestPath));
        var other = index.Named("package.json", "Package.swift").Concat(index.WithExtension(".csproj", ".fsproj", ".vbproj", ".sln", ".slnx"))
            .Where(f => !NativeProjectScanner.IsExcluded(f)).Select(SourceIndex.Depth).DefaultIfEmpty(int.MaxValue).Min();
        return other <= depth ? 25 : 40;
    }

    public Task<ProjectAnalysis> InspectAsync(SourceIndex index, ToolchainReport? toolchains, CancellationToken cancellationToken) =>
        Task.Run(() => Build(Analyze(index), toolchains, time.GetUtcNow()), cancellationToken);

    /// <summary>The analysis for a set of native projects (also used after a configure replaced one of them).</summary>
    public static ProjectAnalysis Build(NativeAnalysis native, ToolchainReport? toolchains, DateTimeOffset now)
    {
        var host = toolchains?.Host.Platform ?? HCG.ExecutableMaker.Core.HostInfo.CurrentPlatform;
        var diagnostics = new List<AnalysisDiagnostic>();
        var detected = new List<DetectedProject>();
        var assessments = native.Projects.ToDictionary(p => p.ManifestPath, p => NativeCapabilities.Evaluate(p, toolchains));
        foreach (var p in native.Projects)
        {
            var a = assessments[p.ManifestPath];
            diagnostics.Add(new(DiagnosticSeverity.Info, $"{NativeCapabilities.KindLabel(p.Kind)}: {p.Name} in {p.DisplayDirectory} — {string.Join("; ", p.Evidence)}", p.ManifestPath));
            if (p.UnsupportedReason is { } why) diagnostics.Add(new(DiagnosticSeverity.Warning, why, p.ManifestPath));
            foreach (var n in p.Notes) diagnostics.Add(new(DiagnosticSeverity.Info, n, p.ManifestPath));
            foreach (var r in a.Remediation) diagnostics.Add(new(DiagnosticSeverity.Warning, r, p.ManifestPath));
            detected.AddRange(Entries(p, host));
        }
        diagnostics.Add(new(DiagnosticSeverity.Info, "CMakeLists.txt, Makefiles and sources were read as text. No CMake, make, compiler or project script ran during inspection; configuring a CMake project runs its code and needs your confirmation first."));

        var buildable = native.Projects.Where(p => p.Kind is NativeProjectKind.CMake or NativeProjectKind.Make or NativeProjectKind.Sources).ToList();
        var best = buildable.Select(p => (Project: p, A: assessments[p.ManifestPath])).OrderBy(x => Rank(x.A.Support)).FirstOrDefault();
        SupportLevel support;
        string summary;
        string appType;
        IReadOnlyList<ToolchainRequirement> requirements = [];
        IReadOnlyList<string> missing = [];
        if (best.Project is null)
        {
            var first = native.Projects[0];
            support = SupportLevel.Unsupported;
            summary = first.UnsupportedReason ?? "No buildable C/C++ project.";
            appType = NativeCapabilities.KindLabel(first.Kind);
        }
        else
        {
            requirements = best.A.Requirements;
            if (best.A.Support != SupportLevel.Supported)
            {
                support = best.A.Support;
                summary = best.A.Summary;
                missing = best.A.Remediation;
            }
            else
            {
                var p = best.Project;
                var ready = p.Kind == NativeProjectKind.CMake && p.Configured is { } c && c.Executables.Any();
                support = ready ? SupportLevel.Supported : SupportLevel.RequiresConfiguration;
                summary = p.Kind switch
                {
                    NativeProjectKind.CMake when p.Configured is { } cfg && cfg.Executables.Any() =>
                        $"{p.Name}: configured with {cfg.Generator} ({cfg.Configuration}). Executable targets: {string.Join(", ", cfg.Executables.Select(t => t.Name))}." +
                        (cfg.Libraries.Any() ? $" Libraries (not runnable on their own): {string.Join(", ", cfg.Libraries.Select(t => t.Name))}." : ""),
                    NativeProjectKind.CMake when p.Configured is { } empty =>
                        $"{p.Name}: configured, but it defines no executable target{(empty.Libraries.Any() ? $" — only libraries ({string.Join(", ", empty.Libraries.Select(t => t.Name))}), which are not runnable applications" : "")}.",
                    NativeProjectKind.CMake =>
                        $"{p.Name}: CMake project. Configure it on this computer (after confirming you trust it) so HCG can list its real executable targets through the CMake File API.",
                    NativeProjectKind.Make => $"{p.Name}: Makefile project. Choose the make target and the file it produces.",
                    _ => $"{p.Name}: C/C++ sources without a build system. Choose the files to compile; HCG generates the build configuration in its own build folder.",
                };
            }
            appType = NativeCapabilities.KindLabel(best.Project.Kind);
        }
        var runnable = detected.Where(d => d.IsRunnableApplication).ToList();
        var bundle = native.Projects.Any(p => p.Configured?.Executables.Any(t => t.IsMacBundle) == true);
        return new ProjectAnalysis
        {
            InspectorId = "native",
            ProjectName = native.Projects.Count == 1 || buildable.Count == 1 ? (buildable.FirstOrDefault() ?? native.Projects[0]).Name : "",
            Language = LanguageLabel(native.Projects.SelectMany(p => p.Configured?.Targets.SelectMany(t => t.Languages) ?? p.Languages).Distinct().ToList()),
            Framework = string.Join(", ", native.Projects.Select(p => p.Kind switch
            {
                NativeProjectKind.CMake => "CMake", NativeProjectKind.Make => "Make", NativeProjectKind.Sources => "No build system", NativeProjectKind.Theos => "Theos",
                _ => p.Evidence.FirstOrDefault()?.Split('(').LastOrDefault()?.TrimEnd(')') ?? "Other",
            }).Distinct()),
            Runtime = "Native",
            ApplicationType = appType,
            Support = support,
            SupportSummary = summary,
            Projects = detected,
            PrimaryProjectPath = runnable.Count == 1 ? runnable[0].RelativePath : null,
            RequiredToolchains = requirements,
            MissingDependencies = missing,
            RecommendedFormats = buildable.Count == 0 ? [] : bundle ? [PackagingFormat.MacAppBundle, PackagingFormat.PortableZip] : [PackagingFormat.PortableFolder, PackagingFormat.PortableZip],
            Diagnostics = diagnostics,
            Native = native,
            AnalyzedAt = now,
        };
    }

    /// <summary>Replaces one project (e.g. after a trusted configure) and rebuilds the analysis.</summary>
    public static ProjectAnalysis WithProject(ProjectAnalysis analysis, NativeProjectInfo project, ToolchainReport? toolchains, DateTimeOffset now)
    {
        var native = analysis.Native ?? new NativeAnalysis();
        var updated = native with { Projects = native.Projects.Select(p => p.ManifestPath == project.ManifestPath ? project : p).ToList() };
        return Build(updated, toolchains, now);
    }

    /// <summary>The key of a CMake target entry under Project to build: "dir/CMakeLists.txt#target".</summary>
    public static string TargetKey(NativeProjectInfo p, string target) => p.ManifestPath + "#" + target;

    /// <summary>Entries under Project to build: real executable targets of configured CMake projects, else one entry per project.</summary>
    private static IEnumerable<DetectedProject> Entries(NativeProjectInfo p, TargetPlatform host)
    {
        if (p.Kind == NativeProjectKind.CMake && p.Configured is { } c && c.Executables.Any())
        {
            foreach (var t in c.Executables)
                yield return new DetectedProject
                {
                    Name = t.Name, RelativePath = TargetKey(p, t.Name), Language = LanguageLabel(t.Languages),
                    ApplicationType = t.IsMacBundle ? "macOS app bundle (CMake target)" : "Executable (CMake target)",
                    IsRunnableApplication = true, CompatiblePlatforms = [host], Version = p.Version, Product = p.Name,
                    OutputType = t.IsMacBundle ? "MACOSX_BUNDLE" : "Executable",
                    Diagnostics = t.Dependencies.Count == 0 ? [] : [new AnalysisDiagnostic(DiagnosticSeverity.Info, "Depends on the project's targets: " + string.Join(", ", t.Dependencies), p.ManifestPath)],
                };
            yield break;
        }
        yield return new DetectedProject
        {
            Name = p.Name, RelativePath = p.ManifestPath, Language = LanguageLabel(p.Languages), Version = p.Version,
            ApplicationType = p.Kind switch
            {
                NativeProjectKind.CMake when p.Configured is not null => "CMake project — no executable target (libraries only)",
                NativeProjectKind.CMake => "CMake project — configure to list its targets",
                NativeProjectKind.Make => "Makefile project — choose the target and its output",
                NativeProjectKind.Sources => "C/C++ sources — choose the files to compile",
                NativeProjectKind.Theos => "Theos project — not a desktop app",
                _ => "C/C++ project — build system not supported",
            },
            IsRunnableApplication = false,
            CompatiblePlatforms = p.Kind is NativeProjectKind.Make or NativeProjectKind.Sources ? [host] : [],
        };
    }

    public static string LanguageLabel(IReadOnlyCollection<string> languages)
    {
        var c = languages.Contains("C");
        var cxx = languages.Contains("CXX");
        return c && cxx ? "C/C++" : cxx ? "C++" : c ? "C" : languages.Count > 0 ? string.Join("/", languages) : "C/C++";
    }

    private static int Rank(SupportLevel s) => s switch
    {
        SupportLevel.Supported => 0, SupportLevel.RequiresConfiguration => 1, SupportLevel.MissingToolchain => 2, _ => 3,
    };
}
