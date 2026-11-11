using System.Text.RegularExpressions;
using HCG.ExecutableMaker.BuildEngine.Electron;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.ProjectInspection;

namespace HCG.ExecutableMaker.Porting;

/// <summary>
/// The compatibility report for an imported project: every source found (convertible or not), each feature in one of
/// four classes, and per source × target the conversion mode, whether it is implemented, and what it needs. Read-only.
/// </summary>
public static partial class CompatibilityInspector
{
    private static readonly string[] ExecutableExtensions = [".exe", ".dll", ".msi", ".dmg", ".pkg", ".appimage", ".deb", ".rpm", ".so", ".dylib", ".jar", ".app"];

    public static CompatibilityReport Build(SourceIndex index, IReadOnlyList<PortingAssessment> assessments, SourceRevision? revision, DateTimeOffset now)
    {
        var sources = new List<DetectedSource>();
        var features = new List<CompatibilityFeature>();
        var options = new List<TargetOption>();

        // ---- Sources a converter handles (or explicitly declines) ----
        foreach (var group in assessments.GroupBy(a => a.Source.Key))
        {
            var first = group.First();
            var framework = Framework(index, first);
            sources.Add(new DetectedSource
            {
                Key = group.Key, Framework = framework, Language = Language(framework, first), Directory = first.Source.Directory,
                EntryPoints = [first.Source.Target], Dependencies = Dependencies(first),
                PlatformFunctionality = group.SelectMany(a => a.Findings).Where(f => f.Disposition != FindingDisposition.Compatible)
                    .Select(f => f.Category.ToString()).Distinct().Order(StringComparer.Ordinal).ToList(),
                Evidence = first.Source.Kind + " · " + first.Source.DisplayName,
            });
            foreach (var unit in first.Reusable)
                features.Add(new CompatibilityFeature(group.Key, unit.FilesWithBlockers == unit.Files && unit.Files > 0 ? FeatureClass.Unsupported : FeatureClass.Reusable, "Code",
                    unit.Name, $"{unit.Files} file(s){(unit.Lines > 0 ? $", {unit.Lines:N0} lines" : "")}: {unit.Kind}. {unit.FilesUnchanged} unchanged, {unit.FilesAdapted} adapted, {unit.FilesWithBlockers} with blockers."));
            foreach (var f in group.SelectMany(a => a.Findings).DistinctBy(f => (f.Title, f.Location, f.Detail)))
                features.Add(new CompatibilityFeature(group.Key, f.Class, Area(f.Category), f.Title, f.Detail, f.Location.Length == 0 ? null : f.Location, f.Guidance));
            foreach (var target in Enum.GetValues<MobileTarget>())
                options.Add(Option(group.Key, framework, target, group.FirstOrDefault(a => a.Target == target)));
        }

        // ---- Frameworks no converter handles yet: shown with the matrix's requirements ----
        foreach (var other in DetectOtherSources(index, sources))
        {
            sources.Add(other.Source);
            features.AddRange(other.Features);
            foreach (var target in Enum.GetValues<MobileTarget>()) options.Add(Option(other.Source.Key, other.Source.Framework, target, null));
        }

        // ---- Compiled programs: never sufficient on their own ----
        var executables = Executables(index);
        var executableOnly = executables.Count > 0 && sources.Count == 0;
        var blockers = new List<string>();
        if (executableOnly)
            blockers.Add($"Only compiled programs were found ({string.Join(", ", executables.Take(5))}{(executables.Count > 5 ? ", …" : "")}). A compiled executable cannot be converted: it does not contain the source code, project files and resources a mobile app is built from. Import the application's source project instead.");
        else if (sources.Count == 0)
            blockers.Add("No source project HCG recognizes was found. Import the folder that contains the project files (for example package.json, Package.swift, an .xcodeproj, CMakeLists.txt or a .csproj).");
        else if (executables.Count > 0)
            features.Add(new CompatibilityFeature("", FeatureClass.Unsupported, "Binaries", "Compiled files in the project",
                $"{executables.Count} compiled file(s) ({string.Join(", ", executables.Take(5))}) are not converted; only source code is. Native libraries need their own mobile builds."));
        foreach (var a in assessments.Where(a => a.Capability == PortingCapability.BlockedNeedsPorting).DistinctBy(a => a.Source.Key))
            blockers.Add($"{a.Source.DisplayName}: {a.Blockers.Count()} feature(s) are unsupported by the current converter (listed under Unsupported). Nothing is generated for this source until they are changed in the project.");

        return new CompatibilityReport
        {
            Sources = sources, Features = features, Options = options, Blockers = blockers, ExecutableOnly = executableOnly, Executables = executables,
            Revision = revision, InspectedAt = now,
        };
    }

    private static TargetOption Option(string key, string framework, MobileTarget target, PortingAssessment? assessment)
    {
        if (assessment is { Capability: PortingCapability.BuildExistingTarget })
            return new TargetOption
            {
                SourceKey = key, Target = target, Mode = assessment.Mode, Status = SupportStatus.Implemented, Path = assessment.Path,
                Capability = assessment.Capability, Summary = assessment.Summary,
            };
        var row = ConversionCatalog.For(framework).FirstOrDefault(r => r.Target == target);
        if (row is null)
            return new TargetOption
            {
                SourceKey = key, Target = target, Mode = ConversionMode.MobileAdaptation, Status = SupportStatus.NotPlanned,
                Summary = $"No conversion of {framework} to {target.DisplayName()} is implemented or planned.",
            };
        if (row.Status == SupportStatus.Implemented && assessment is not null && row.Path == assessment.Path)
        {
            var missing = assessment.Toolchains.Where(t => !t.IsSatisfied).Select(t => $"{t.DisplayName}: {t.Detail}").ToList();
            return new TargetOption
            {
                SourceKey = key, Target = target, Mode = row.Mode, Status = SupportStatus.Implemented, Path = row.Path, Capability = assessment.Capability,
                Summary = assessment.Summary, Requirements = [.. missing.Select(m => "Missing on this computer — " + m)],
            };
        }
        return new TargetOption
        {
            SourceKey = key, Target = target, Mode = row.Mode, Status = row.Status == SupportStatus.Implemented ? SupportStatus.NotPlanned : row.Status,
            Summary = row.Status == SupportStatus.Implemented ? $"{framework} → {target.DisplayName()}: not applicable to this source. {assessment?.Summary}" : row.Summary,
            Requirements = row.Requirements,
        };
    }

    private static string Framework(SourceIndex index, PortingAssessment a)
    {
        var key = a.Source.Key;
        if (key.StartsWith("swiftpm:", StringComparison.Ordinal) || key.StartsWith("xcode:", StringComparison.Ordinal))
            return a.Findings.Any(f => f.Title.Contains("Objective-C", StringComparison.Ordinal) || f.Title.Contains("storyboard", StringComparison.OrdinalIgnoreCase) || f.Title.Contains("XIB", StringComparison.Ordinal))
                ? ConversionCatalog.AppKit : ConversionCatalog.SwiftUi;
        if (key.StartsWith("cmake:", StringComparison.Ordinal))
            return a.Findings.Any(f => f.Disposition == FindingDisposition.Blocker && GuiToolkitRegex().IsMatch(f.Title + " " + f.Detail)) ? ConversionCatalog.GtkQt : ConversionCatalog.CmakeConsole;
        if (key.StartsWith("web:", StringComparison.Ordinal)) return ConversionCatalog.ViteReact;
        if (key.StartsWith("unsupported-web:", StringComparison.Ordinal))
        {
            var facts = WebDesktopConversion.Scan(index.FullPath(a.Source.Directory));
            return facts.IsElectron ? ConversionCatalog.Electron
                : facts.SsrFrameworks.Count > 0 ? ConversionCatalog.ServerRendered
                : facts.OtherUiFrameworks.Count > 0 || facts.HasVite || facts.HasReact ? ConversionCatalog.OtherWeb
                : ConversionCatalog.NodeServer;
        }
        if (key.StartsWith("existing-", StringComparison.Ordinal) || key.StartsWith("capacitor:", StringComparison.Ordinal)) return "Existing mobile project";
        return a.Source.Kind;
    }

    private static string Language(string framework, PortingAssessment a) =>
        ConversionCatalog.Matrix.FirstOrDefault(r => r.SourceFramework == framework)?.Languages ?? a.Source.Kind;

    private static IReadOnlyList<string> Dependencies(PortingAssessment a) =>
        a.Findings.Where(f => f.Category == FindingCategory.Dependency).Select(f => f.Title).Distinct().Take(20).ToList();

    private static string Area(FindingCategory c) => c switch
    {
        FindingCategory.PlatformApi => "Platform APIs",
        FindingCategory.UiAdaptation => "Interface",
        FindingCategory.Lifecycle => "Lifecycle and sign-in",
        FindingCategory.Dependency => "Dependencies",
        FindingCategory.BuildSystem => "Build",
        FindingCategory.FileSystem => "Files",
        _ => "Permissions and storage",
    };

    private sealed record Other(DetectedSource Source, IReadOnlyList<CompatibilityFeature> Features);

    /// <summary>Projects in frameworks without a converter: recognized from their project files (read as text).</summary>
    private static IEnumerable<Other> DetectOtherSources(SourceIndex index, List<DetectedSource> known)
    {
        bool Known(string dir) => known.Any(k => k.Directory == dir);
        foreach (var proj in index.WithExtension(".csproj", ".vbproj", ".fsproj").OrderBy(SourceIndex.Depth).Take(20))
        {
            var dir = proj.Contains('/') ? proj[..proj.LastIndexOf('/')] : "";
            var text = SafeRead(index, proj);
            if (text.Contains("<UseMaui>true", StringComparison.OrdinalIgnoreCase)) continue; // a mobile project already (Mobile Build Studio)
            var winUi = text.Contains("<UseWindowsForms>true", StringComparison.OrdinalIgnoreCase) || text.Contains("<UseWPF>true", StringComparison.OrdinalIgnoreCase);
            var avalonia = text.Contains("Include=\"Avalonia", StringComparison.Ordinal);
            var framework = winUi ? ConversionCatalog.DotNetWindows : avalonia ? ConversionCatalog.Avalonia : ConversionCatalog.DotNetConsole;
            var key = "dotnet:" + proj;
            var feats = new List<CompatibilityFeature>
            {
                winUi ? new(key, FeatureClass.Unsupported, "Interface", "Windows-only UI", $"{SourceIndex.FileName(proj)} uses {(text.Contains("UseWPF", StringComparison.OrdinalIgnoreCase) ? "WPF" : "Windows Forms")}, which runs only on Windows. The interface must be rewritten (.NET MAUI or Avalonia); class libraries without UI can be reused.", proj)
                : avalonia ? new(key, FeatureClass.RequiresAdaptation, "Interface", "Avalonia desktop lifetime", "Avalonia runs on phones with a mobile head project and a single-view lifetime; windows, dialogs and file pickers need mobile equivalents. Not automated yet (planned).", proj)
                : new(key, FeatureClass.RequiresBackendOrCompanion, "Workload", "Console or server program", "A .NET console or server program keeps running on a computer; a phone can reach it through a companion interface (planned).", proj),
            };
            yield return new Other(new DetectedSource { Key = key, Framework = framework, Language = "C#", Directory = dir, EntryPoints = [proj], Evidence = SourceIndex.FileName(proj) }, feats);
        }
        foreach (var manifest in index.Named("package.json").OrderBy(SourceIndex.Depth).Take(20))
        {
            var dir = manifest.Contains('/') ? manifest[..manifest.LastIndexOf('/')] : "";
            if (Known(dir) || dir.Split('/').Any(d => d is "test" or "tests" or "fixtures" or "e2e" or "examples")) continue;
            var text = SafeRead(index, manifest);
            var servers = new[] { "express", "fastify", "koa", "hono", "@nestjs/core", "@hapi/hapi" }.Where(s => text.Contains($"\"{s}\"", StringComparison.Ordinal)).ToList();
            var cli = text.Contains("\"bin\"", StringComparison.Ordinal);
            if (servers.Count == 0 && !cli) continue;
            var key = "node:" + dir;
            yield return new Other(new DetectedSource
            {
                Key = key, Framework = ConversionCatalog.NodeServer, Language = "TypeScript, JavaScript", Directory = dir, EntryPoints = [manifest],
                Dependencies = servers, Evidence = servers.Count > 0 ? "Server framework: " + string.Join(", ", servers) : "package.json \"bin\" (command-line tool)",
            },
            [
                new(key, FeatureClass.RequiresBackendOrCompanion, "Workload", servers.Count > 0 ? "Server process" : "Command-line tool",
                    "This program runs on a computer (listening for requests or reading the file system). Phones do not run it; companion mode (planned) gives the phone an interface to it while it keeps running on the computer.", manifest),
            ]);
        }
        foreach (var (marker, framework, language) in new[]
                 {
                     ("pyproject.toml", ConversionCatalog.Python, "Python"), ("requirements.txt", ConversionCatalog.Python, "Python"), ("setup.py", ConversionCatalog.Python, "Python"),
                     ("go.mod", ConversionCatalog.GoRust, "Go"), ("Cargo.toml", ConversionCatalog.GoRust, "Rust"), ("pom.xml", ConversionCatalog.Java, "Java"),
                 })
        {
            if (index.Named(marker).OrderBy(SourceIndex.Depth).FirstOrDefault() is not { } file) continue;
            var dir = file.Contains('/') ? file[..file.LastIndexOf('/')] : "";
            if (Known(dir)) continue;
            var key = $"{language.ToLowerInvariant()}:{file}";
            if (known.Any(k => k.Key == key)) continue;
            yield return new Other(new DetectedSource { Key = key, Framework = framework, Language = language, Directory = dir, EntryPoints = [file], Evidence = file },
            [
                new(key, framework == ConversionCatalog.Java ? FeatureClass.Unsupported : FeatureClass.RequiresBackendOrCompanion, "Workload", $"{language} application",
                    framework == ConversionCatalog.Java
                        ? "Java desktop interfaces (Swing, JavaFX) do not run on phones; non-UI code can be reused in a hand-written Android app."
                        : $"{language} programs are not converted to mobile apps. As a server or tool it can keep running on a computer with a companion interface (planned).", file),
            ]);
        }
    }

    /// <summary>Compiled programs and libraries in the import (by extension, or by ELF/Mach-O/PE header for files without one).</summary>
    public static IReadOnlyList<string> Executables(SourceIndex index)
    {
        var found = new List<string>();
        var bundles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in index.Files)
        {
            var appBundle = f.Split('/').FirstOrDefault(p => p.EndsWith(".app", StringComparison.OrdinalIgnoreCase));
            if (appBundle is not null) { var b = f[..(f.IndexOf(appBundle, StringComparison.Ordinal) + appBundle.Length)]; if (bundles.Add(b)) found.Add(b); continue; }
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (ExecutableExtensions.Contains(ext)) { found.Add(f); continue; }
            if (ext.Length == 0 && found.Count < 200 && IsBinaryExecutable(index.FullPath(f))) found.Add(f);
        }
        return found;
    }

    private static bool IsBinaryExecutable(string path)
    {
        try
        {
            using var s = File.OpenRead(path);
            Span<byte> h = stackalloc byte[4];
            if (s.Read(h) < 4) return false;
            return (h[0] == 0x7F && h[1] == (byte)'E' && h[2] == (byte)'L' && h[3] == (byte)'F')     // ELF
                   || (h[0] == 0xCF && h[1] == 0xFA && h[2] == 0xED && h[3] == 0xFE)              // Mach-O 64-bit
                   || (h[0] == 0xCA && h[1] == 0xFE && h[2] == 0xBA && h[3] == 0xBE)              // Mach-O universal
                   || (h[0] == (byte)'M' && h[1] == (byte)'Z');                                   // PE
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static string SafeRead(SourceIndex index, string rel)
    {
        try { return File.ReadAllText(index.FullPath(rel)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    [GeneratedRegex(@"\b(GTK|gtk|Qt|X11|SDL|curses|OpenGL)\b")] private static partial Regex GuiToolkitRegex();
}
