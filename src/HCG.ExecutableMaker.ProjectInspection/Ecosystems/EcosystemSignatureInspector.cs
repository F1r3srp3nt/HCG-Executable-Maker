using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.ProjectInspection.Ecosystems;

/// <summary>
/// Recognizes non-.NET ecosystems by their manifest files and reports them honestly as unsupported in this version.
/// These inspectors are placeholders for future real inspectors; they never claim build support.
/// </summary>
public sealed class EcosystemSignatureInspector(
    string id,
    string displayName,
    string language,
    string framework,
    int score,
    Func<SourceIndex, IReadOnlyList<string>> detect,
    TimeProvider time) : IProjectInspector
{
    public string Id => id;
    public string DisplayName => displayName;

    public int Match(SourceIndex index) => detect(index).Count > 0 ? score : 0;

    public Task<ProjectAnalysis> InspectAsync(SourceIndex index, ToolchainReport? toolchains, CancellationToken cancellationToken)
    {
        var markers = detect(index);
        var name = markers.Count > 0 && markers[0].Contains('/')
            ? markers[0][..markers[0].IndexOf('/')]
            : new DirectoryInfo(index.Root).Name;
        return Task.FromResult(new ProjectAnalysis
        {
            InspectorId = id,
            ProjectName = name,
            Language = language,
            Framework = framework,
            ApplicationType = $"{displayName} project",
            Support = SupportLevel.Unsupported,
            SupportSummary = $"Detected a {displayName} project. HCG Executable Maker M0.1 does not build {displayName} projects; support is planned for a later milestone (M0.5+).",
            Diagnostics = [.. markers.Take(5).Select(m => new AnalysisDiagnostic(DiagnosticSeverity.Info, "Project marker found", m))],
            AnalyzedAt = time.GetUtcNow(),
        });
    }

    private static Func<SourceIndex, IReadOnlyList<string>> Files(params string[] names) =>
        idx => idx.Named(names).OrderBy(SourceIndex.Depth).ToList();

    private static bool ContainsText(SourceIndex idx, string rel, string needle)
    {
        try
        {
            var full = idx.FullPath(rel);
            return new FileInfo(full).Length < 2_000_000 && File.ReadAllText(full).Contains(needle, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException) { return false; }
    }

    /// <summary>The set of ecosystems recognized (but not built) in M0.1.</summary>
    public static IEnumerable<IProjectInspector> CreateDefaults(TimeProvider time) =>
    [
        new Node.NodeProjectInspector(time),
        // Android, Xcode, Flutter and .NET MAUI are handled by MobileProjectInspector (real support);
        // Node.js and Electron by NodeProjectInspector.
        new EcosystemSignatureInspector("react-native", "React Native", "JavaScript/TypeScript", "React Native", 48,
            idx => idx.Named("package.json").Where(f => ContainsText(idx, f, "\"react-native\"")).ToList(), time),
        new EcosystemSignatureInspector("unity", "Unity", "C#", "Unity", 47,
            idx => idx.Files.Where(f => f.EndsWith("ProjectSettings/ProjectVersion.txt", StringComparison.Ordinal)).ToList(), time),
        new EcosystemSignatureInspector("rust", "Rust / Cargo", "Rust", "Cargo", 45, Files("Cargo.toml"), time),
        new EcosystemSignatureInspector("java", "Java", "Java", "Maven/Gradle", 35, Files("pom.xml", "build.gradle", "build.gradle.kts"), time),
        new EcosystemSignatureInspector("python", "Python", "Python", "Python", 25, Files("pyproject.toml", "setup.py", "requirements.txt"), time),
        new EcosystemSignatureInspector("cpp", "C / C++", "C/C++", "CMake/Make", 20,
            idx => idx.Named("CMakeLists.txt", "meson.build", "Makefile").Concat(idx.WithExtension(".vcxproj")).ToList(), time),
    ];
}
