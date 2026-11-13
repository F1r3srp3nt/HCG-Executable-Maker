using System.Text.RegularExpressions;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.ProjectInspection.DotNet;

/// <summary>Static inspector for .NET solutions and projects (C#, F#, VB). Never runs MSBuild or project code.</summary>
public sealed partial class DotNetProjectInspector(TimeProvider time) : IProjectInspector
{
    private static readonly string[] ProjectExtensions = [".csproj", ".fsproj", ".vbproj"];
    private const int MaxSourceFilesScanned = 3000;

    public string Id => "dotnet";
    public string DisplayName => ".NET";

    public int Match(SourceIndex index) =>
        index.WithExtension(ProjectExtensions).Any() ? 100 :
        index.WithExtension(".sln", ".slnx").Any() ? 80 : 0;

    public Task<ProjectAnalysis> InspectAsync(SourceIndex index, ToolchainReport? toolchains, CancellationToken cancellationToken) =>
        Task.Run(() => Inspect(index, toolchains, cancellationToken), cancellationToken);

    private ProjectAnalysis Inspect(SourceIndex index, ToolchainReport? toolchains, CancellationToken ct)
    {
        var diagnostics = new List<AnalysisDiagnostic>();
        var solutions = index.WithExtension(".sln", ".slnx").OrderBy(SourceIndex.Depth).ThenBy(s => s, StringComparer.Ordinal).ToList();
        var projectFiles = index.WithExtension(ProjectExtensions).ToList();

        // Projects referenced by solutions (relative to source root).
        var inSolution = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sln in solutions)
        {
            try
            {
                var slnDir = Path.GetDirectoryName(index.FullPath(sln))!;
                foreach (var rel in SolutionParser.GetProjectPaths(index.FullPath(sln)))
                {
                    var full = Path.GetFullPath(Path.Combine(slnDir, rel));
                    var relToRoot = Path.GetRelativePath(index.Root, full).Replace('\\', '/');
                    if (relToRoot.StartsWith("../", StringComparison.Ordinal) || !File.Exists(full))
                        diagnostics.Add(new AnalysisDiagnostic(DiagnosticSeverity.Warning, $"Solution references a project that is not in the workspace: {rel}", sln));
                    else
                        inSolution.Add(relToRoot);
                }
            }
            catch (Exception ex) when (ex is IOException or System.Xml.XmlException)
            {
                diagnostics.Add(new AnalysisDiagnostic(DiagnosticSeverity.Warning, $"Could not read solution: {ex.Message}", sln));
            }
        }

        // global.json (nearest to the root wins).
        var globalJsonPath = index.Named("global.json").OrderBy(SourceIndex.Depth).FirstOrDefault();
        var globalJson = globalJsonPath is null ? null : GlobalJson.TryRead(index.FullPath(globalJsonPath));
        if (globalJsonPath is not null && globalJson is null)
            diagnostics.Add(new AnalysisDiagnostic(DiagnosticSeverity.Warning, "global.json could not be parsed.", globalJsonPath));

        var projects = new List<DetectedProject>();
        foreach (var rel in projectFiles)
        {
            ct.ThrowIfCancellationRequested();
            projects.Add(InspectProject(index, rel, ct));
        }

        var installedSdks = toolchains?.Find(ToolchainIds.DotNetSdk) is { IsAvailable: true } sdk ? sdk.AdditionalVersions : [];
        var primary = SelectPrimary(projects, inSolution);
        var maxTfmMajor = projects.SelectMany(p => p.TargetFrameworks).Select(TfmMajor).Where(m => m >= 5).DefaultIfEmpty(0).Max();

        // Toolchain requirements.
        var requirements = new List<ToolchainRequirement>();
        var missing = new List<string>();
        if (toolchains is null)
        {
            requirements.Add(new ToolchainRequirement(ToolchainIds.DotNetSdk, ".NET SDK", maxTfmMajor > 0 ? $"{maxTfmMajor}.0" : null, false, "Toolchains not detected yet."));
        }
        else
        {
            var sdkOk = installedSdks.Count > 0 && (maxTfmMajor == 0 || installedSdks.Any(v => SdkMajor(v) >= maxTfmMajor));
            var detail = installedSdks.Count == 0 ? "No .NET SDK installed." :
                sdkOk ? $"Installed: {string.Join(", ", installedSdks.Take(4))}" : $"Highest installed SDK ({installedSdks[0]}) is older than required .NET {maxTfmMajor}.";
            requirements.Add(new ToolchainRequirement(ToolchainIds.DotNetSdk, ".NET SDK", maxTfmMajor > 0 ? $"{maxTfmMajor}.0" : null, sdkOk, detail));
            if (!sdkOk) missing.Add(maxTfmMajor > 0 ? $".NET SDK {maxTfmMajor}.0 or later" : ".NET SDK");

            if (globalJson?.SdkVersion is { } pinned)
            {
                var gjOk = installedSdks.Count > 0 && globalJson.IsSatisfiedBy(installedSdks);
                requirements.Add(new ToolchainRequirement("global.json", $"global.json SDK {pinned}", pinned, gjOk,
                    gjOk ? $"rollForward: {globalJson.RollForward ?? "patch (default)"}" : $"No installed SDK satisfies version {pinned} with rollForward '{globalJson.RollForward ?? "patch"}'."));
                if (!gjOk) missing.Add($".NET SDK matching global.json ({pinned}, rollForward {globalJson.RollForward ?? "patch"})");
            }
        }

        foreach (var p in projects)
            foreach (var r in p.ProjectReferences)
            {
                var full = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(index.FullPath(p.RelativePath))!, r));
                if (!File.Exists(full)) missing.Add($"Project reference '{r}' from {p.Name}");
            }

        var icons = projects.Where(p => p.ApplicationIcon is not null).Select(p => p.ApplicationIcon!)
            .Concat(index.WithExtension(".ico", ".icns").Take(10))
            .Concat(index.Files.Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(f, @"(^|/)(Assets|Resources|Icons?)/", RegexOptions.IgnoreCase) && Regex.IsMatch(f, "icon|logo", RegexOptions.IgnoreCase)).Take(10))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var (support, summary) = DetermineSupport(primary, projects, missing, toolchains);
        var recommended = primary is null ? [] : RecommendFormats(primary);
        var name = primary?.AssemblyName ?? primary?.Name
                   ?? (solutions.Count > 0 ? Path.GetFileNameWithoutExtension(SourceIndex.FileName(solutions[0])) : new DirectoryInfo(index.Root).Name);
        var languages = projects.Select(p => p.Language).Distinct().ToList();

        return new ProjectAnalysis
        {
            InspectorId = Id,
            ProjectName = name,
            Language = primary?.Language ?? (languages.Count > 0 ? string.Join(", ", languages) : "C#"),
            Framework = primary is null ? ".NET" : DescribeFramework(primary),
            Runtime = primary?.TargetFrameworks.Count > 0 ? string.Join(", ", primary.TargetFrameworks.Select(DescribeTfm).Distinct()) : null,
            ApplicationType = primary?.ApplicationType ?? (projects.Count > 0 ? "Libraries only" : "Solution without projects"),
            Support = support,
            SupportSummary = summary,
            SolutionFiles = solutions,
            Projects = projects,
            PrimaryProjectPath = primary?.RelativePath,
            GlobalJsonSdkVersion = globalJson?.SdkVersion,
            GlobalJsonRollForward = globalJson?.RollForward,
            RequiredToolchains = requirements,
            MissingDependencies = missing.Distinct().ToList(),
            RecommendedFormats = recommended,
            DetectedIcons = icons,
            Diagnostics = [.. diagnostics, .. projects.SelectMany(p => p.Diagnostics)],
            AnalyzedAt = time.GetUtcNow(),
        };
    }

    private static DetectedProject InspectProject(SourceIndex index, string rel, CancellationToken ct)
    {
        var full = index.FullPath(rel);
        var projDir = Path.GetDirectoryName(full)!;
        var language = rel.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase) ? "F#" :
                       rel.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase) ? "Visual Basic" : "C#";
        var name = Path.GetFileNameWithoutExtension(rel);
        var diags = new List<AnalysisDiagnostic>();

        MsBuildProjectFile file;
        try
        {
            file = MsBuildProjectFile.Parse(full, FindAncestorFiles(index.Root, projDir, "Directory.Build.props"), FindCentralVersions(index.Root, projDir), index.Root);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException)
        {
            return new DetectedProject
            {
                Name = name, RelativePath = rel, Language = language, ApplicationType = "Unreadable project file",
                Diagnostics = [new AnalysisDiagnostic(DiagnosticSeverity.Error, $"Project file could not be parsed: {ex.Message}", rel)],
            };
        }

        if (file.UnresolvedProperties.Count > 0)
            diags.Add(new AnalysisDiagnostic(DiagnosticSeverity.Info, $"Uses MSBuild properties that cannot be evaluated statically: {string.Join(", ", file.UnresolvedProperties.Take(6))}", rel));

        var tfms = (file.Get("TargetFrameworks") ?? file.Get("TargetFramework") ?? file.Get("TargetFrameworkVersion") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => !t.Contains("$("))
            .ToList();
        if (tfms.Count == 0)
            diags.Add(new AnalysisDiagnostic(DiagnosticSeverity.Warning, "No target framework could be determined statically.", rel));

        var rids = (file.Get("RuntimeIdentifiers") ?? file.Get("RuntimeIdentifier") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(r => !r.Contains("$(")).ToList();

        var sdk = file.Sdk;
        var isWeb = sdk?.Contains("Web", StringComparison.OrdinalIgnoreCase) == true && sdk.Contains("BlazorWebAssembly", StringComparison.OrdinalIgnoreCase) == false;
        var isBlazorWasm = sdk?.Contains("BlazorWebAssembly", StringComparison.OrdinalIgnoreCase) == true;
        var outputType = file.Get("OutputType") ?? (isWeb ? "Exe" : "Library");
        var packages = file.PackageReferences;
        bool HasPackage(string id) => packages.Any(p => string.Equals(p.Name, id, StringComparison.OrdinalIgnoreCase));
        var isAvalonia = packages.Any(p => p.Name.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase));
        var isTest = file.IsTrue("IsTestProject") || HasPackage("Microsoft.NET.Test.Sdk");
        var isMaui = file.IsTrue("UseMaui");
        var isWpf = file.IsTrue("UseWPF");
        var isWinForms = file.IsTrue("UseWindowsForms");
        var isExe = outputType.Equals("Exe", StringComparison.OrdinalIgnoreCase) || outputType.Equals("WinExe", StringComparison.OrdinalIgnoreCase);
        // A net*-ios Exe is an iPhone/iPad app: built by the .NET iOS workload in Mobile Build Studio, never published as a desktop app.
        var isIosApp = isExe && !isMaui && tfms.Count > 0 && tfms.All(t => t.Contains("-ios", StringComparison.OrdinalIgnoreCase));

        var appType =
            !file.IsSdkStyle ? "Legacy .NET Framework project" :
            isTest ? "Test project" :
            isMaui ? ".NET MAUI application" :
            isIosApp ? (isAvalonia ? ".NET for iOS application (Avalonia)" : ".NET for iOS application") :
            isBlazorWasm ? "Blazor WebAssembly application" :
            isAvalonia && isExe ? "Avalonia desktop application" :
            isWpf && isExe ? "WPF desktop application" :
            isWinForms && isExe ? "Windows Forms application" :
            isWeb ? "ASP.NET Core web application" :
            isExe ? "Console application" :
            isAvalonia ? "Avalonia library" : "Class library";

        var runnable = file.IsSdkStyle && isExe && !isTest && !isMaui && !isBlazorWasm && !isIosApp;
        var compatible = ComputeCompatiblePlatforms(tfms, isWpf || isWinForms, out var tfmNotes);
        if (isIosApp)
            diags.Add(new AnalysisDiagnostic(DiagnosticSeverity.Info,
                $"{name} is an iPhone/iPad app ({string.Join(", ", tfms)}). It is built with the .NET iOS workload and Xcode in Mobile Build Studio (Simulator build, device archive, signed IPA), not published as a desktop app.", rel));
        else diags.AddRange(tfmNotes.Select(n => new AnalysisDiagnostic(DiagnosticSeverity.Warning, n, rel)));
        foreach (var outside in file.OutsideWorkspace.Take(4))
            diags.Add(new AnalysisDiagnostic(DiagnosticSeverity.Warning,
                $"Uses {outside}, which is outside the imported folder. Import the repository root that contains it: HCG never builds a partial copy of a repository.", rel));
        if (!file.IsSdkStyle)
            diags.Add(new AnalysisDiagnostic(DiagnosticSeverity.Warning, "Non-SDK-style (legacy) project. M0.1 only builds SDK-style projects.", rel));
        if (isMaui)
            diags.Add(new AnalysisDiagnostic(DiagnosticSeverity.Warning, ".NET MAUI requires platform workloads; not supported in M0.1.", rel));

        var icon = file.Get("ApplicationIcon");
        if (icon is not null && !icon.Contains("$("))
        {
            var iconFull = Path.GetFullPath(Path.Combine(projDir, icon.Replace('\\', '/')));
            icon = File.Exists(iconFull) && HCG.ExecutableMaker.Core.PathGuard.IsWithin(index.Root, iconFull)
                ? Path.GetRelativePath(index.Root, iconFull).Replace('\\', '/') : null;
        }
        else icon = null;

        return new DetectedProject
        {
            Name = name,
            RelativePath = rel,
            Language = language,
            Sdk = sdk,
            TargetFrameworks = tfms,
            OutputType = outputType,
            AssemblyName = file.Get("AssemblyName") is { } an && !an.Contains("$(") ? an : name,
            RuntimeIdentifiers = rids,
            PackageReferences = packages,
            ProjectReferences = file.ProjectReferences,
            ApplicationIcon = icon,
            Version = file.Get("Version") ?? file.Get("VersionPrefix"),
            Company = file.Get("Company"),
            Product = file.Get("Product"),
            Description = file.Get("Description"),
            EntryPointFiles = runnable ? FindEntryPoints(index, rel, language, ct) : [],
            ApplicationType = appType,
            IsAvalonia = isAvalonia,
            IsRunnableApplication = runnable,
            CompatiblePlatforms = runnable ? compatible : [],
            Diagnostics = diags,
        };
    }

    internal static IReadOnlyList<TargetPlatform> ComputeCompatiblePlatforms(IReadOnlyList<string> tfms, bool windowsUi, out List<string> notes)
    {
        notes = [];
        var result = new HashSet<TargetPlatform>();
        foreach (var tfm in tfms)
        {
            var lower = tfm.ToLowerInvariant();
            var major = TfmMajor(lower);
            if (lower.StartsWith("netstandard", StringComparison.Ordinal)) continue;
            if (major == 0 && (lower.StartsWith("net4", StringComparison.Ordinal) || lower.StartsWith("net3", StringComparison.Ordinal) || lower.StartsWith("net2", StringComparison.Ordinal)))
            {
                notes.Add($"{tfm} is .NET Framework. Self-contained cross-platform publishing is not possible; M0.1 does not build .NET Framework apps.");
                continue;
            }
            if (lower.Contains("-windows")) { result.Add(TargetPlatform.Windows); continue; }
            if (lower.Contains("-android")) { notes.Add($"{tfm} targets Android, which requires the Android workload (not supported in M0.1)."); continue; }
            if (lower.Contains("-ios") || lower.Contains("-maccatalyst") || lower.Contains("-macos") || lower.Contains("-tvos"))
            {
                notes.Add($"{tfm} requires Apple platform workloads (not supported in M0.1).");
                continue;
            }
            if (major >= 5 || lower.StartsWith("netcoreapp3", StringComparison.Ordinal))
            {
                if (windowsUi) result.Add(TargetPlatform.Windows);
                else { result.Add(TargetPlatform.Windows); result.Add(TargetPlatform.MacOS); result.Add(TargetPlatform.Linux); }
            }
        }
        return [.. result.OrderBy(p => p)];
    }

    /// <summary>"net10.0" → 10, "net8.0-windows" → 8, "netcoreapp3.1" → 3, otherwise 0.</summary>
    internal static int TfmMajor(string tfm)
    {
        var m = TfmRegex().Match(tfm.ToLowerInvariant());
        return m.Success && int.TryParse(m.Groups[1].Value, out var v) ? v : 0;
    }

    [GeneratedRegex(@"^net(?:coreapp)?(\d+)\.\d+")]
    private static partial Regex TfmRegex();

    private static int SdkMajor(string version) => int.TryParse(version.Split('.')[0], out var m) ? m : 0;

    private static string DescribeTfm(string tfm)
    {
        var major = TfmMajor(tfm);
        var lower = tfm.ToLowerInvariant();
        if (lower.StartsWith("netstandard", StringComparison.Ordinal)) return $".NET Standard {tfm[11..]}";
        if (major >= 5) { var dash = tfm.IndexOf('-'); return $".NET {tfm[3..(dash < 0 ? tfm.Length : dash)]}" + (dash < 0 ? "" : $" ({tfm[(dash + 1)..]})"); }
        if (lower.StartsWith("netcoreapp", StringComparison.Ordinal)) return $".NET Core {tfm[10..]}";
        return tfm;
    }

    private static string DescribeFramework(DetectedProject p)
    {
        var ui = p.IsAvalonia ? "Avalonia UI" : p.ApplicationType.StartsWith("WPF", StringComparison.Ordinal) ? "WPF" :
                 p.ApplicationType.StartsWith("Windows Forms", StringComparison.Ordinal) ? "Windows Forms" :
                 p.ApplicationType.StartsWith("ASP.NET", StringComparison.Ordinal) ? "ASP.NET Core" :
                 p.ApplicationType.StartsWith(".NET MAUI", StringComparison.Ordinal) ? ".NET MAUI" : null;
        var avalonia = p.PackageReferences.FirstOrDefault(r => r.Name.Equals("Avalonia", StringComparison.OrdinalIgnoreCase))?.Version;
        var runtime = p.TargetFrameworks.Count > 0 ? DescribeTfm(p.TargetFrameworks[0]) : ".NET";
        return ui is null ? runtime : $"{runtime} · {ui}{(avalonia is not null && p.IsAvalonia ? " " + avalonia : "")}";
    }

    private static DetectedProject? SelectPrimary(List<DetectedProject> projects, HashSet<string> inSolution) =>
        projects.Where(p => p.IsRunnableApplication)
            .OrderByDescending(p => p.CompatiblePlatforms.Count > 0)
            .ThenByDescending(p => p.IsAvalonia)
            .ThenByDescending(p => inSolution.Contains(p.RelativePath))
            .ThenBy(p => SourceIndex.Depth(p.RelativePath))
            .ThenBy(p => p.RelativePath, StringComparer.Ordinal)
            .FirstOrDefault();

    private static (SupportLevel, string) DetermineSupport(DetectedProject? primary, List<DetectedProject> projects, List<string> missing, ToolchainReport? toolchains)
    {
        if (projects.Count == 0)
            return (SupportLevel.RequiresConfiguration, "A solution was found but none of its projects are in the workspace.");
        if (primary is null)
        {
            var kinds = string.Join(", ", projects.Select(p => p.ApplicationType).Distinct());
            return (SupportLevel.RequiresConfiguration, $"No runnable application project was found (found: {kinds}). Only Exe/WinExe projects can be published as applications.");
        }
        if (primary.CompatiblePlatforms.Count == 0)
            return (SupportLevel.Unsupported, $"{primary.Name} ({primary.ApplicationType}) targets frameworks M0.1 cannot publish.");
        if (toolchains is null)
            return (SupportLevel.MissingToolchain, "Toolchains have not been detected yet.");
        if (missing.Count > 0)
            return (SupportLevel.MissingToolchain, $"Supported project, but requirements are missing: {string.Join("; ", missing)}.");
        var targets = string.Join(", ", primary.CompatiblePlatforms.Select(p => p.DisplayName()));
        return (SupportLevel.Supported, $"{primary.Name} can be built and published for: {targets}.");
    }

    private static IReadOnlyList<PackagingFormat> RecommendFormats(DetectedProject primary)
    {
        var list = new List<PackagingFormat>();
        var gui = primary.IsAvalonia || primary.OutputType?.Equals("WinExe", StringComparison.OrdinalIgnoreCase) == true;
        if (primary.CompatiblePlatforms.Contains(TargetPlatform.MacOS) && gui) list.Add(PackagingFormat.MacAppBundle);
        list.Add(PackagingFormat.PortableZip);
        list.Add(PackagingFormat.PortableFolder);
        return list;
    }

    private static IReadOnlyList<string> FindEntryPoints(SourceIndex index, string projectRel, string language, CancellationToken ct)
    {
        var dir = projectRel.Contains('/') ? projectRel[..(projectRel.LastIndexOf('/') + 1)] : "";
        var ext = language switch { "F#" => ".fs", "Visual Basic" => ".vb", _ => ".cs" };
        var otherProjectDirs = index.WithExtension(ProjectExtensions)
            .Where(p => !string.Equals(p, projectRel, StringComparison.Ordinal))
            .Select(p => p.Contains('/') ? p[..(p.LastIndexOf('/') + 1)] : "")
            .Where(d => d.Length > dir.Length && d.StartsWith(dir, StringComparison.Ordinal))
            .ToList();
        var found = new List<string>();
        var scanned = 0;
        foreach (var f in index.Files.Where(f => f.StartsWith(dir, StringComparison.Ordinal) && f.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
        {
            ct.ThrowIfCancellationRequested();
            if (otherProjectDirs.Any(d => f.StartsWith(d, StringComparison.Ordinal))) continue;
            if (++scanned > MaxSourceFilesScanned) break;
            string text;
            try
            {
                var full = index.FullPath(f);
                if (new FileInfo(full).Length > 1_000_000) continue;
                text = File.ReadAllText(full);
            }
            catch (IOException) { continue; }
            if (ext == ".cs" && MainMethodRegex().IsMatch(text)) found.Add(f);
            else if (ext == ".cs" && SourceIndex.FileName(f).Equals("Program.cs", StringComparison.OrdinalIgnoreCase) && !TypeDeclarationRegex().IsMatch(text)) found.Add(f + " (top-level statements)");
            else if (ext == ".fs" && text.Contains("[<EntryPoint>]", StringComparison.Ordinal)) found.Add(f);
            else if (ext == ".vb" && text.Contains("Sub Main", StringComparison.OrdinalIgnoreCase)) found.Add(f);
        }
        return found;
    }

    [GeneratedRegex(@"static\s+(?:async\s+)?(?:void|int|Task(?:<int>)?)\s+Main\s*\(")]
    private static partial Regex MainMethodRegex();

    [GeneratedRegex(@"^\s*(?:public\s+|internal\s+|static\s+|sealed\s+|partial\s+)*(?:class|struct|record)\s+\w+", RegexOptions.Multiline)]
    private static partial Regex TypeDeclarationRegex();

    internal static IReadOnlyList<string> FindAncestorFiles(string root, string startDir, string fileName)
    {
        // MSBuild imports the nearest Directory.Build.props only (unless it imports its parent). We mirror that.
        var dir = new DirectoryInfo(startDir);
        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        while (dir is not null && dir.FullName.Length >= rootFull.Length)
        {
            var candidate = Path.Combine(dir.FullName, fileName);
            if (File.Exists(candidate)) return [candidate];
            dir = dir.Parent;
        }
        return [];
    }

    internal static IReadOnlyDictionary<string, string>? FindCentralVersions(string root, string projDir)
    {
        var file = FindAncestorFiles(root, projDir, "Directory.Packages.props").FirstOrDefault();
        return file is null ? null : MsBuildProjectFile.ReadCentralVersions(file, root);
    }
}
