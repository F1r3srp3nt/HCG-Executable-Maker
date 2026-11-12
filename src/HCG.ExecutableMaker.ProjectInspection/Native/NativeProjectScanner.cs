using System.Text.Json;
using System.Text.RegularExpressions;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.ProjectInspection.Native;

/// <summary>
/// Finds C/C++ project roots from their files only (nothing is compiled, configured or run): Theos projects first (their
/// Makefiles include Theos' makefiles, so they are never mistaken for generic Make projects), then CMake projects (the
/// topmost CMakeLists.txt with a project() call), Meson / Autotools / Visual Studio projects (recognized, not built),
/// Makefile projects, and finally loose C/C++ sources. Dependency, vendored and build-output folders are never treated
/// as separate applications.
/// </summary>
public static partial class NativeProjectScanner
{
    /// <summary>Folders that hold dependencies, vendored code or build output — never a separate project to build.</summary>
    public static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "build", "_build", "builds", "out", "dist", "release", "debug", "_deps", "CMakeFiles", "third_party", "third-party", "thirdparty", "3rdparty",
        "external", "externals", "extern", "vendor", "vendored", "deps", "dependencies", "subprojects", ".theos", ".cache", ".git", "node_modules",
        "Pods", "DerivedData", ".build", "target", "venv", ".venv", "__pycache__",
    };

    private static readonly string[] MakefileNames = ["GNUmakefile", "makefile", "Makefile"];
    private static readonly HashSet<string> CExtensions = new(StringComparer.Ordinal) { ".c" };
    private static readonly HashSet<string> CxxExtensions = new(StringComparer.Ordinal) { ".cc", ".cpp", ".cxx", ".c++", ".C" };
    private const int MaxSourceFiles = 2000;

    private static readonly HashSet<string> BuildOutputDirectories = new(StringComparer.OrdinalIgnoreCase) { "build", "_build", "builds", "out", "_deps", "CMakeFiles", ".git", "node_modules", ".cache" };

    private static bool IsBuildOutput(string relativePath) =>
        relativePath.Split('/').SkipLast(1).Any(d => BuildOutputDirectories.Contains(d) || d.StartsWith("cmake-build-", StringComparison.OrdinalIgnoreCase));

    public static bool IsExcluded(string relativePath) =>
        relativePath.Split('/').SkipLast(1).Any(d => ExcludedDirectories.Contains(d) || d.StartsWith("cmake-build-", StringComparison.OrdinalIgnoreCase));

    public static NativeAnalysis Scan(SourceIndex index)
    {
        var files = index.Files.Where(f => !IsExcluded(f)).ToList();
        var projects = new List<NativeProjectInfo>();
        var claimed = new List<string>();
        bool Claimed(string dir) => claimed.Any(c => c.Length == 0 || dir == c || dir.StartsWith(c + "/", StringComparison.Ordinal));

        // 1. Theos: before any generic Make classification.
        foreach (var dir in Topmost(files.Where(f => MakefileNames.Contains(SourceIndex.FileName(f)) && Read(index, f) is { } t && TheosRegex().IsMatch(t)).Select(Dir)))
        {
            var makefile = MakefileIn(files, dir)!;
            projects.Add(Theos(index, files, dir, makefile, Read(index, makefile) ?? ""));
            claimed.Add(dir);
        }

        // 2. CMake: the topmost CMakeLists.txt with project() (nested ones are reached through add_subdirectory).
        foreach (var dir in Topmost(files.Where(f => SourceIndex.FileName(f) == "CMakeLists.txt" && Read(index, f) is { } t && ProjectCallRegex().IsMatch(t)).Select(Dir)))
        {
            if (Claimed(dir)) continue;
            projects.Add(CMake(index, files, dir));
            claimed.Add(dir);
        }

        // 3. Recognized build systems HCG does not run in this version.
        foreach (var dir in Topmost(files.Where(f => SourceIndex.FileName(f) == "meson.build").Select(Dir)))
        {
            if (Claimed(dir)) continue;
            projects.Add(Unsupported(dir, Join(dir, "meson.build"), "Meson", "Meson builds are recognized but not run by HCG in this version. Generate a CMake build, or build with meson and ninja yourself."));
            claimed.Add(dir);
        }
        foreach (var dir in Topmost(files.Where(f => SourceIndex.FileName(f) is "configure.ac" or "configure.in").Select(Dir)))
        {
            if (Claimed(dir) || MakefileIn(files, dir) is not null) continue;
            projects.Add(Unsupported(dir, Join(dir, SourceIndex.FileName(files.First(f => Dir(f) == dir && SourceIndex.FileName(f) is "configure.ac" or "configure.in"))), "Autotools",
                "Autotools project without a Makefile: it must be configured first (autoreconf / ./configure), which runs project scripts. HCG does not run configure scripts in this version."));
            claimed.Add(dir);
        }
        foreach (var dir in Topmost(files.Where(f => f.EndsWith(".vcxproj", StringComparison.OrdinalIgnoreCase)).Select(Dir)))
        {
            if (Claimed(dir)) continue;
            var vcx = files.First(f => Dir(f) == dir && f.EndsWith(".vcxproj", StringComparison.OrdinalIgnoreCase));
            projects.Add(Unsupported(dir, vcx, "Visual Studio C++",
                "Visual Studio C++ projects (.vcxproj) are recognized but not built by HCG in this version. A CMake build of the same code is supported on Windows, macOS and Linux."));
            claimed.Add(dir);
        }

        // 4. Make.
        foreach (var dir in Topmost(files.Where(f => MakefileNames.Contains(SourceIndex.FileName(f))).Select(Dir)))
        {
            if (Claimed(dir)) continue;
            projects.Add(Make(index, files, dir));
            claimed.Add(dir);
        }

        // 5. Loose sources (no build system): only files not covered by a project above, and only if one has main().
        var loose = files.Where(f => IsSource(f) && !Claimed(Dir(f))).Take(MaxSourceFiles).ToList();
        if (loose.Count > 0)
        {
            var sources = loose.Select(f => new NativeSourceFile(f, CExtensions.Contains(Path.GetExtension(f)) ? "C" : "CXX", Read(index, f) is { } t && MainRegex().IsMatch(t))).ToList();
            if (sources.Any(s => s.HasMain))
            {
                var dir = CommonDirectory(sources.Where(s => s.HasMain).Select(s => s.Path));
                var inDir = sources.Where(s => dir.Length == 0 || s.Path.StartsWith(dir + "/", StringComparison.Ordinal))
                    .Select(s => s with { Path = dir.Length == 0 ? s.Path : s.Path[(dir.Length + 1)..] }).OrderBy(s => s.Path, StringComparer.Ordinal).ToList();
                var first = inDir.First(s => s.HasMain);
                projects.Add(new NativeProjectInfo
                {
                    Directory = dir, Kind = NativeProjectKind.Sources, ManifestPath = Join(dir, first.Path),
                    Name = dir.Length == 0 ? new DirectoryInfo(index.Root).Name : dir[(dir.LastIndexOf('/') + 1)..],
                    Sources = inDir,
                    Languages = inDir.Select(s => s.Language).Distinct().Order(StringComparer.Ordinal).ToList(),
                    Evidence = [$"{inDir.Count} C/C++ source file(s) without a build system; main() in {string.Join(", ", inDir.Where(s => s.HasMain).Take(5).Select(s => s.Path))}"],
                    Notes = ["HCG compiles only the files you select, with a small CMake configuration it generates in its own build folder (never in the project)."],
                });
            }
        }
        return new NativeAnalysis { Projects = projects };
    }

    // ------------------------------------------------------------------ CMake

    private static NativeProjectInfo CMake(SourceIndex index, IReadOnlyList<string> files, string dir)
    {
        // The project's own subdirectories, vendored ones included (reached through add_subdirectory); build output excluded.
        var own = index.Files.Where(f => (dir.Length == 0 || f.StartsWith(dir + "/", StringComparison.Ordinal)) && !IsBuildOutput(f)).ToList();
        var lists = own.Where(f => SourceIndex.FileName(f) == "CMakeLists.txt")
            .Concat(own.Where(f => f.EndsWith(".cmake", StringComparison.Ordinal)))
            .Take(400).ToList();
        var rootText = Read(index, Join(dir, "CMakeLists.txt")) ?? "";
        var project = ProjectCallRegex().Match(rootText);
        var projectName = project.Success ? project.Groups["name"].Value : null;
        var projectArgs = project.Success ? project.Groups["args"].Value : "";
        var languages = new List<string>();
        if (LanguagesRegex().Match(projectArgs) is { Success: true } lm)
            languages.AddRange(lm.Groups["l"].Value.Split((char[])[' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Where(l => l.All(char.IsLetter)));
        else
        {
            // project(Name C CXX) without the LANGUAGES keyword, or the default C and CXX.
            var positional = projectArgs.Split((char[])[' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries).TakeWhile(a => a is "C" or "CXX" or "OBJC" or "OBJCXX" or "CUDA" or "ASM").ToList();
            languages.AddRange(positional.Count > 0 ? positional : ["C", "CXX"]);
        }
        var version = VersionRegex().Match(projectArgs) is { Success: true } vm ? vm.Groups["v"].Value : null;
        var minimum = MinimumRegex().Match(rootText) is { Success: true } mm ? mm.Groups["v"].Value : null;

        var exes = new SortedSet<string>(StringComparer.Ordinal);
        var libs = new SortedSet<string>(StringComparer.Ordinal);
        var packages = new SortedSet<string>(StringComparer.Ordinal);
        var downloads = new List<string>();
        foreach (var f in lists)
        {
            var text = Read(index, f);
            if (text is null) continue;
            text = CommentRegex().Replace(text, "");
            string Expand(string n) => projectName is null ? n : n.Replace("${PROJECT_NAME}", projectName, StringComparison.Ordinal).Replace("${CMAKE_PROJECT_NAME}", projectName, StringComparison.Ordinal);
            foreach (Match m in AddTargetRegex().Matches(text))
            {
                if (m.Groups["rest"].Value.TrimStart().StartsWith("IMPORTED", StringComparison.Ordinal) || m.Groups["rest"].Value.TrimStart().StartsWith("ALIAS", StringComparison.Ordinal)) continue;
                (m.Groups["kind"].Value.Equals("add_executable", StringComparison.OrdinalIgnoreCase) ? exes : libs).Add(Expand(m.Groups["name"].Value));
            }
            foreach (Match m in FindPackageRegex().Matches(text)) packages.Add(m.Groups["p"].Value);
            foreach (Match m in DownloadRegex().Matches(text)) downloads.Add($"{m.Value.TrimEnd('(').Trim()} ({f}:{LineOf(text, m.Index)})");
        }

        var presets = new List<CMakePresetInfo>();
        foreach (var name in new[] { "CMakePresets.json", "CMakeUserPresets.json" })
        {
            var path = Join(dir, name);
            if (!index.Files.Contains(path)) continue;
            presets.AddRange(ReadPresets(index, path));
        }
        var notes = new List<string>();
        if (downloads.Count > 0)
            notes.Add($"The project downloads dependencies while it is configured ({string.Join("; ", downloads.Take(4))}). HCG blocks those downloads unless you allow them for this project.");
        if (packages.Count > 0)
            notes.Add($"find_package: {string.Join(", ", packages)}. These must already be installed on this computer (HCG does not install them); configuration reports any that are missing.");
        return new NativeProjectInfo
        {
            Directory = dir, Kind = NativeProjectKind.CMake, ManifestPath = Join(dir, "CMakeLists.txt"),
            Name = projectName ?? (dir.Length == 0 ? new DirectoryInfo(index.Root).Name : dir[(dir.LastIndexOf('/') + 1)..]),
            Version = version, Languages = languages.Distinct().ToList(), MinimumCMake = minimum,
            ExecutableHints = [.. exes], LibraryHints = [.. libs], FindPackages = [.. packages], DownloadEvidence = downloads, Presets = presets,
            Evidence = [$"CMakeLists.txt with project({projectName ?? "?"}){(minimum is null ? "" : $", cmake_minimum_required {minimum}")}{(presets.Count > 0 ? $", {presets.Count(p => !p.Hidden)} configure preset(s)" : "")}"],
            Notes = notes,
        };
    }

    public static IReadOnlyList<CMakePresetInfo> ReadPresets(SourceIndex index, string path)
    {
        var list = new List<CMakePresetInfo>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(index.FullPath(path)), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (!doc.RootElement.TryGetProperty("configurePresets", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
            foreach (var p in arr.EnumerateArray())
            {
                string? S(string n) => p.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                if (S("name") is not { Length: > 0 } name) continue;
                list.Add(new CMakePresetInfo(name, S("displayName"), S("generator"), S("binaryDir"),
                    p.TryGetProperty("hidden", out var h) && h.ValueKind == JsonValueKind.True, SourceIndex.FileName(path)));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return list;
    }

    // ------------------------------------------------------------------ Make

    private static NativeProjectInfo Make(SourceIndex index, IReadOnlyList<string> files, string dir)
    {
        var makefile = MakefileIn(files, dir)!;
        var text = Read(index, makefile) ?? "";
        var targets = new List<string>();
        var phony = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith('\t') || line.TrimStart().StartsWith('#')) continue;
            if (PhonyRegex().Match(line) is { Success: true } pm)
            {
                foreach (var t in pm.Groups["t"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)) phony.Add(t);
                continue;
            }
            if (RuleRegex().Match(line) is not { Success: true } rm) continue;
            foreach (var t in rm.Groups["t"].Value.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
                if (!t.StartsWith('.') && !t.Contains('%') && !t.Contains('$') && !targets.Contains(t)) targets.Add(t);
        }
        var notes = new List<string>
        {
            "A Makefile can do anything, so HCG does not guess what it produces: choose the make target and the file it creates. HCG runs make in a copy of the project in its own build folder.",
        };
        if (files.Any(f => Dir(f) == dir && SourceIndex.FileName(f) is "configure" or "configure.ac"))
            notes.Add("A configure script is present. HCG does not run configure scripts; the committed Makefile is used as it is.");
        return new NativeProjectInfo
        {
            Directory = dir, Kind = NativeProjectKind.Make, ManifestPath = makefile,
            Name = dir.Length == 0 ? new DirectoryInfo(index.Root).Name : dir[(dir.LastIndexOf('/') + 1)..],
            MakeTargets = targets, PhonyTargets = [.. phony],
            Languages = files.Where(f => Dir(f).StartsWith(dir, StringComparison.Ordinal) && IsSource(f)).Select(f => CExtensions.Contains(Path.GetExtension(f)) ? "C" : "CXX").Distinct().Order(StringComparer.Ordinal).ToList(),
            Evidence = [$"{SourceIndex.FileName(makefile)} with {targets.Count} rule target(s){(targets.Count > 0 ? ": " + string.Join(", ", targets.Take(8)) : "")}"],
            Notes = notes,
        };
    }

    // ------------------------------------------------------------------ Theos and unsupported

    private static NativeProjectInfo Theos(SourceIndex index, IReadOnlyList<string> files, string dir, string makefile, string text)
    {
        var evidence = new List<string> { $"{makefile}: {TheosRegex().Match(text).Value.Trim()}" };
        foreach (var marker in new[] { "control", "layout/DEBIAN/control" })
            if (files.Contains(Join(dir, marker))) evidence.Add($"{Join(dir, marker)} (Debian package control file)");
        var logos = files.Count(f => (dir.Length == 0 || f.StartsWith(dir + "/", StringComparison.Ordinal)) && (f.EndsWith(".x", StringComparison.Ordinal) || f.EndsWith(".xm", StringComparison.Ordinal)));
        if (logos > 0) evidence.Add($"{logos} Logos source file(s) (.x/.xm)");
        var name = TheosNameRegex().Match(text) is { Success: true } nm ? nm.Groups["n"].Value : (dir.Length == 0 ? new DirectoryInfo(index.Root).Name : dir[(dir.LastIndexOf('/') + 1)..]);
        return new NativeProjectInfo
        {
            Directory = dir, Kind = NativeProjectKind.Theos, ManifestPath = makefile, Name = name, Evidence = evidence,
            UnsupportedReason = "Theos project: a jailbreak tweak, tool or app for iOS that Theos builds into a Debian package (.deb) for jailbroken devices. " +
                                "It needs the Theos toolchain and a dedicated package workflow, which HCG Executable Maker does not have. HCG never packages it as a desktop app or an IPA.",
        };
    }

    private static NativeProjectInfo Unsupported(string dir, string marker, string system, string reason) => new()
    {
        Directory = dir, Kind = NativeProjectKind.Unsupported, ManifestPath = marker,
        Name = dir.Length == 0 ? system + " project" : dir[(dir.LastIndexOf('/') + 1)..],
        Evidence = [$"{marker} ({system})"], UnsupportedReason = reason,
    };

    // ------------------------------------------------------------------ helpers

    private static bool IsSource(string f) => CExtensions.Contains(Path.GetExtension(f)) || CxxExtensions.Contains(Path.GetExtension(f));

    private static string? MakefileIn(IReadOnlyList<string> files, string dir) =>
        MakefileNames.Select(n => Join(dir, n)).FirstOrDefault(files.Contains);

    /// <summary>Keeps only directories not nested inside another one of the set.</summary>
    private static IEnumerable<string> Topmost(IEnumerable<string> dirs)
    {
        var kept = new List<string>();
        foreach (var d in dirs.Distinct().OrderBy(d => d.Length).ThenBy(d => d, StringComparer.Ordinal))
            if (!kept.Any(k => k.Length == 0 || d == k || d.StartsWith(k + "/", StringComparison.Ordinal))) kept.Add(d);
        return kept;
    }

    private static string CommonDirectory(IEnumerable<string> paths)
    {
        var dirs = paths.Select(Dir).ToList();
        var common = dirs[0];
        foreach (var d in dirs.Skip(1))
            while (common.Length > 0 && d != common && !d.StartsWith(common + "/", StringComparison.Ordinal))
                common = common.Contains('/') ? common[..common.LastIndexOf('/')] : "";
        return common;
    }

    public static string Dir(string relative) => relative.Contains('/') ? relative[..relative.LastIndexOf('/')] : "";
    public static string Join(string dir, string name) => dir.Length == 0 ? name : dir + "/" + name;

    private static string? Read(SourceIndex index, string relative)
    {
        try
        {
            var full = index.FullPath(relative);
            var info = new FileInfo(full);
            return info.Exists && info.Length < 2_000_000 ? File.ReadAllText(full) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static int LineOf(string text, int index) => text.AsSpan(0, Math.Min(index, text.Length)).Count('\n') + 1;

    [GeneratedRegex(@"(?im)^\s*project\s*\(\s*(?<name>[A-Za-z0-9_.+-]+)(?<args>[^)]*)\)")] private static partial Regex ProjectCallRegex();
    [GeneratedRegex(@"\bLANGUAGES\s+(?<l>[A-Z ]+?)(?=\s+(?:VERSION|DESCRIPTION|HOMEPAGE_URL)\b|\s*$)")] private static partial Regex LanguagesRegex();
    [GeneratedRegex(@"\bVERSION\s+(?<v>\d+(?:\.\d+){0,3})")] private static partial Regex VersionRegex();
    [GeneratedRegex(@"(?i)cmake_minimum_required\s*\(\s*VERSION\s+(?<v>[\d.]+)")] private static partial Regex MinimumRegex();
    [GeneratedRegex(@"#[^\n]*")] private static partial Regex CommentRegex();
    [GeneratedRegex(@"(?i)\b(?<kind>add_executable|add_library)\s*\(\s*(?<name>[A-Za-z0-9_.+${}-]+)(?<rest>[^)]*)\)")] private static partial Regex AddTargetRegex();
    [GeneratedRegex(@"(?i)\bfind_package\s*\(\s*(?<p>[A-Za-z0-9_.+-]+)")] private static partial Regex FindPackageRegex();
    [GeneratedRegex(@"(?i)\b(?:FetchContent_Declare|FetchContent_MakeAvailable|ExternalProject_Add|CPMAddPackage)\s*\(")] private static partial Regex DownloadRegex();
    [GeneratedRegex(@"\$\(THEOS(?:_MAKE_PATH)?\)[^\s]*|\bTHEOS_DEVICE_IP\b|\bTHEOS\s*[:?]?=")] private static partial Regex TheosRegex();
    [GeneratedRegex(@"(?m)^\s*(?:TWEAK_NAME|TOOL_NAME|APPLICATION_NAME|LIBRARY_NAME|BUNDLE_NAME)\s*[:?+]?=\s*(?<n>[A-Za-z0-9_.-]+)")] private static partial Regex TheosNameRegex();
    [GeneratedRegex(@"^\s*\.PHONY\s*:(?<t>.*)$")] private static partial Regex PhonyRegex();
    [GeneratedRegex(@"^(?<t>[A-Za-z0-9_./+-][A-Za-z0-9_./+ \t-]*?)\s*:(?![=:])")] private static partial Regex RuleRegex();
    [GeneratedRegex(@"\b(?:int|void)\s+(?:w?main|wWinMain|WinMain)\s*\(")] private static partial Regex MainRegex();
}
