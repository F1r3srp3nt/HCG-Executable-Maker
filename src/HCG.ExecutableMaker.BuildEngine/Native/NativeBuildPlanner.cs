using System.Text;
using System.Text.RegularExpressions;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.BuildEngine.Native;

/// <summary>
/// Plans C/C++ builds for this computer's platform:
/// <list type="bullet">
/// <item>CMake: configure into an HCG-owned build folder (the project's preset, or a generator HCG chooses: Ninja, else
/// Unix Makefiles; Visual Studio on Windows), then <c>cmake --build --target</c>. The artifact is the path the CMake
/// File API reports for that target and configuration.</item>
/// <item>Make: the project is copied into HCG's build folder and <c>make TARGET</c> runs there (make writes next to its
/// Makefile); the artifact is the expected output the user configured.</item>
/// <item>Sources: HCG writes a small CMakeLists.txt in its build folder that compiles exactly the selected files.</item>
/// </list>
/// Every command is an argument list; names, cache entries and make variables are validated, never passed to a shell.
/// </summary>
public static partial class NativeBuildPlanner
{
    public static readonly IReadOnlyList<string> BuildTypes = ["Release", "Debug", "RelWithDebInfo", "MinSizeRel"];
    public static readonly IReadOnlyList<string> CStandards = ["99", "11", "17", "23"];
    public static readonly IReadOnlyList<string> CxxStandards = ["11", "14", "17", "20", "23"];

    /// <summary>The installed tools a native build uses (from the toolchain report).</summary>
    public sealed record Tools(string? CMake, string? Make, bool HasNinja, bool HasMsvc, TargetPlatform Host)
    {
        public static Tools From(ToolchainReport report) => new(
            report.Find(ToolchainIds.CMake) is { IsAvailable: true } c ? c.ExecutablePath : null,
            report.Find(ToolchainIds.Make) is { IsAvailable: true } m ? m.ExecutablePath : null,
            report.IsAvailable(ToolchainIds.Ninja), report.IsAvailable(ToolchainIds.MsvcTools), report.Host.Platform);
    }

    /// <summary>The generator HCG passes when the project's preset does not decide (null = CMake's default, e.g. Visual Studio).</summary>
    public static string? ChooseGenerator(Tools tools, string? preset)
    {
        if (preset is not null) return null;
        if (tools.Host == TargetPlatform.Windows && tools.HasMsvc) return null;
        if (tools.HasNinja) return "Ninja";
        if (tools.Make is not null) return tools.Host == TargetPlatform.Windows ? "MinGW Makefiles" : "Unix Makefiles";
        return null;
    }

    /// <summary>cmake configure arguments (-S, -B, preset or generator, build type, architecture, downloads, cache entries).</summary>
    public static List<string> ConfigureArguments(string sourceDir, string buildDir, NativeBuildOptions o, Tools tools, TargetArchitecture arch)
    {
        var args = new List<string> { "-S", sourceDir, "-B", buildDir };
        if (o.Preset is { } preset) args.AddRange(["--preset", preset]);
        else if (ChooseGenerator(tools, null) is { } generator) args.AddRange(["-G", generator]);
        else if (tools.Host == TargetPlatform.Windows) args.AddRange(["-A", arch == TargetArchitecture.Arm64 ? "ARM64" : "x64"]);
        if (o.Preset is null) args.Add("-DCMAKE_BUILD_TYPE=" + o.BuildType);
        if (tools.Host == TargetPlatform.MacOS && !o.CacheEntries.Any(e => e.StartsWith("CMAKE_OSX_ARCHITECTURES", StringComparison.Ordinal)))
            args.Add("-DCMAKE_OSX_ARCHITECTURES=" + arch switch { TargetArchitecture.X64 => "x86_64", TargetArchitecture.Universal => "arm64;x86_64", _ => "arm64" });
        // No unrequested downloads: FetchContent must use what is already populated unless the user allowed downloads.
        if (!o.AllowDownloads) args.Add("-DFETCHCONTENT_FULLY_DISCONNECTED=ON");
        args.AddRange(o.CacheEntries.Select(e => "-D" + e));
        return args;
    }

    public static void Validate(BuildSettings settings, HostEnvironment host, string sourceDirectory)
    {
        var o = settings.Native ?? throw new BuildValidationException("C/C++ build options are missing.");
        if (o.Kind is not (NativeProjectKind.CMake or NativeProjectKind.Make or NativeProjectKind.Sources))
            throw new BuildValidationException($"{o.Kind} projects are not built by HCG.");
        if (settings.Platform != host.Platform)
            throw new BuildValidationException($"C/C++ projects are built for this computer's platform ({host.Platform.DisplayName()}) with its own compilers. " +
                $"Building {settings.Platform.DisplayName()} binaries here would need a {settings.Platform.DisplayName()} target toolchain, which HCG does not set up.");
        var archOk = host.Platform == TargetPlatform.MacOS
            ? settings.Architecture is TargetArchitecture.Arm64 or TargetArchitecture.X64 or TargetArchitecture.Universal
            : settings.Architecture == host.Architecture;
        if (!archOk)
            throw new BuildValidationException(host.Platform == TargetPlatform.MacOS
                ? "On a Mac, C/C++ projects build for arm64, x86_64 or both (universal)."
                : $"This {host.Platform.DisplayName()} computer builds {host.Architecture.DisplayName()} binaries only; another architecture needs a cross toolchain HCG does not set up.");
        if (o.ProjectDirectory.Split('/').Any(seg => seg is ".." or ".") || Path.IsPathRooted(o.ProjectDirectory))
            throw new BuildValidationException("The project directory must be inside the workspace.");
        var dir = ProjectDirectory(sourceDirectory, o.ProjectDirectory);
        if (!TargetRegex().IsMatch(o.Target))
            throw new BuildValidationException($"'{o.Target}' is not a valid target name (letters, digits, '_', '.', '+', '-', '/').");
        if (!BuildTypes.Contains(o.BuildType)) throw new BuildValidationException($"Build type must be one of {string.Join(", ", BuildTypes)}.");
        if (o.Preset is { } p && !PresetRegex().IsMatch(p)) throw new BuildValidationException($"'{p}' is not a valid preset name.");
        foreach (var e in o.CacheEntries)
            if (!CacheEntryRegex().IsMatch(e)) throw new BuildValidationException($"'{e}' is not a CMake cache entry of the form NAME=VALUE or NAME:TYPE=VALUE.");
        foreach (var v in o.MakeVariables)
            if (!MakeVariableRegex().IsMatch(v)) throw new BuildValidationException($"'{v}' is not a make variable of the form NAME=VALUE.");
        switch (o.Kind)
        {
            case NativeProjectKind.CMake:
                if (!File.Exists(Path.Combine(dir, "CMakeLists.txt"))) throw new BuildValidationException("CMakeLists.txt is missing from the project directory.");
                break;
            case NativeProjectKind.Make:
                if (!new[] { "GNUmakefile", "makefile", "Makefile" }.Any(m => File.Exists(Path.Combine(dir, m)))) throw new BuildValidationException("The Makefile is missing from the project directory.");
                if (o.ExpectedOutput is not { Length: > 0 } output) throw new BuildValidationException("Enter the file the make target produces (relative to the project folder), e.g. bin/myapp.");
                RelativeInside(output, "The expected output");
                break;
            case NativeProjectKind.Sources:
                if (o.Sources.Count == 0) throw new BuildValidationException("Select at least one source file to compile.");
                if (o.Language is not ("C" or "CXX")) throw new BuildValidationException("The language must be C or C++.");
                if (!(o.Language == "C" ? CStandards : CxxStandards).Contains(o.Standard))
                    throw new BuildValidationException($"{(o.Language == "C" ? "C" : "C++")} standard must be one of {string.Join(", ", o.Language == "C" ? CStandards : CxxStandards)}.");
                if (!ExecutableNameRegex().IsMatch(o.Target)) throw new BuildValidationException($"'{o.Target}' is not a valid executable name.");
                foreach (var s in o.Sources)
                {
                    RelativeInside(s, "A source file");
                    if (s.Contains('"') || !File.Exists(Path.Combine(dir, s.Replace('/', Path.DirectorySeparatorChar))))
                        throw new BuildValidationException($"The source file {s} does not exist in the project.");
                }
                break;
        }
        foreach (var r in o.RuntimeFiles)
        {
            RelativeInside(r, "A runtime file");
            if (o.Kind != NativeProjectKind.Make && !File.Exists(Path.Combine(dir, r)) && !Directory.Exists(Path.Combine(dir, r)))
                throw new BuildValidationException($"The runtime file or folder {r} does not exist in the project.");
        }
    }

    private static void RelativeInside(string path, string what)
    {
        if (path.Length == 0 || Path.IsPathRooted(path) || path.Replace('\\', '/').Split('/').Any(seg => seg == "..") || path.Contains('\0'))
            throw new BuildValidationException($"{what} ({path}) must be a path inside the project folder.");
    }

    public static string ProjectDirectory(string sourceDirectory, string relative)
    {
        var dir = Path.GetFullPath(Path.Combine(sourceDirectory, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!PathGuard.IsWithin(Path.GetFullPath(sourceDirectory), dir) && dir != Path.GetFullPath(sourceDirectory).TrimEnd(Path.DirectorySeparatorChar))
            throw new BuildValidationException("The project directory is outside the workspace.");
        return dir;
    }

    // ------------------------------------------------------------------ plan

    public sealed record PlanInput(BuildSettings Settings, string SourceDirectory, string WorkDirectory, Tools Tools, IReadOnlyDictionary<string, string> Environment);

    /// <summary>Where the build happens: everything is inside <see cref="PlanInput.WorkDirectory"/> (HCG's build folder).</summary>
    public sealed record Prepared(string ProjectDirectory, string? CMakeSource, string? BuildDirectory, string? MakeDirectory);

    /// <summary>Prepares the build folder: the File API query (CMake), the generated CMakeLists.txt (sources), the project copy (Make).</summary>
    public static Prepared Prepare(PlanInput input, CancellationToken ct)
    {
        var o = input.Settings.Native!;
        var projectDir = ProjectDirectory(input.SourceDirectory, o.ProjectDirectory);
        Directory.CreateDirectory(input.WorkDirectory);
        switch (o.Kind)
        {
            case NativeProjectKind.Make:
            {
                var copy = Path.Combine(input.WorkDirectory, "make", PathGuard.SanitizeFileName(Path.GetFileName(projectDir.TrimEnd(Path.DirectorySeparatorChar)), "project"));
                CopyProject(projectDir, copy, ct);
                return new Prepared(projectDir, null, null, copy);
            }
            case NativeProjectKind.Sources:
            {
                var generated = Path.Combine(input.WorkDirectory, "generated");
                Directory.CreateDirectory(generated);
                File.WriteAllText(Path.Combine(generated, "CMakeLists.txt"), GeneratedCMakeLists(o, projectDir), new UTF8Encoding(false));
                var build = Path.Combine(input.WorkDirectory, "cmake-build");
                CMakeFileApi.WriteQuery(build);
                return new Prepared(projectDir, generated, build, null);
            }
            default:
            {
                var build = Path.Combine(input.WorkDirectory, "cmake-build");
                CMakeFileApi.WriteQuery(build);
                return new Prepared(projectDir, projectDir, build, null);
            }
        }
    }

    public static IReadOnlyList<BuildStep> Plan(PlanInput input, Prepared prepared)
    {
        var s = input.Settings;
        var o = s.Native!;
        var jobs = Math.Clamp(Environment.ProcessorCount, 1, 64).ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (o.Kind == NativeProjectKind.Make)
        {
            var make = input.Tools.Make ?? throw new BuildValidationException("make was not found. Install it (see the Toolchains page), then press Refresh.");
            List<string> args = ["-C", prepared.MakeDirectory!, "-j", jobs, o.Target, .. o.MakeVariables];
            return [new BuildStep(BuildStage.Compiling, $"make {o.Target}", new ProcessSpec { Executable = make, Arguments = args, WorkingDirectory = prepared.MakeDirectory, Environment = input.Environment })];
        }
        var cmake = input.Tools.CMake ?? throw new BuildValidationException("CMake was not found. Install CMake 3.20 or later (see the Toolchains page), then press Refresh.");
        var configure = ConfigureArguments(prepared.CMakeSource!, prepared.BuildDirectory!, o, input.Tools, s.Architecture);
        List<string> build = ["--build", prepared.BuildDirectory!, "--target", o.Target, "--config", o.BuildType, "--parallel", jobs];
        return
        [
            new BuildStep(BuildStage.Restoring, $"Configure with CMake{(o.Preset is { } p ? $" (preset {p})" : "")}",
                new ProcessSpec { Executable = cmake, Arguments = configure, WorkingDirectory = prepared.CMakeSource, Environment = input.Environment }),
            new BuildStep(BuildStage.Compiling, $"Build target {o.Target} ({o.BuildType})",
                new ProcessSpec { Executable = cmake, Arguments = build, WorkingDirectory = prepared.CMakeSource, Environment = input.Environment }),
        ];
    }

    /// <summary>The CMakeLists.txt HCG generates (in its build folder) for explicitly selected sources.</summary>
    public static string GeneratedCMakeLists(NativeBuildOptions o, string projectDir)
    {
        static string Q(string p) => "\"" + p.Replace('\\', '/') + "\"";
        var hasC = o.Sources.Any(s => s.EndsWith(".c", StringComparison.Ordinal));
        var hasCxx = o.Sources.Any(s => !s.EndsWith(".c", StringComparison.Ordinal));
        var languages = new List<string>();
        if (hasC || o.Language == "C") languages.Add("C");
        if (hasCxx || o.Language == "CXX") languages.Add("CXX");
        var sb = new StringBuilder();
        sb.Append("# Generated by HCG Executable Maker in its build folder (not in your project). It compiles exactly the files you selected.\n");
        sb.Append("cmake_minimum_required(VERSION 3.20)\n");
        sb.Append($"project(hcg_sources LANGUAGES {string.Join(' ', languages)})\n");
        if (languages.Contains("C")) sb.Append($"set(CMAKE_C_STANDARD {(o.Language == "C" ? o.Standard : "17")})\nset(CMAKE_C_STANDARD_REQUIRED ON)\n");
        if (languages.Contains("CXX")) sb.Append($"set(CMAKE_CXX_STANDARD {(o.Language == "CXX" ? o.Standard : "17")})\nset(CMAKE_CXX_STANDARD_REQUIRED ON)\n");
        sb.Append($"add_executable({o.Target}\n");
        foreach (var src in o.Sources) sb.Append("  ").Append(Q(Path.Combine(projectDir, src.Replace('/', Path.DirectorySeparatorChar)))).Append('\n');
        sb.Append(")\n");
        sb.Append($"target_include_directories({o.Target} PRIVATE {Q(projectDir)})\n");
        return sb.ToString();
    }

    private static void CopyProject(string from, string to, CancellationToken ct)
    {
        if (Directory.Exists(to)) Directory.Delete(to, recursive: true);
        Directory.CreateDirectory(to);
        foreach (var entry in new DirectoryInfo(from).EnumerateFileSystemInfos())
        {
            ct.ThrowIfCancellationRequested();
            if (entry.LinkTarget is not null || entry.Name is ".git" or ".hg" or ".svn" or "node_modules") continue;
            var target = Path.Combine(to, entry.Name);
            if (entry is DirectoryInfo d) CopyProject(d.FullName, target, ct);
            else
            {
                File.Copy(entry.FullName, target);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, File.GetUnixFileMode(entry.FullName));
            }
        }
    }

    // ------------------------------------------------------------------ locate and stage

    /// <summary>What the build produced: the executable, the .app it belongs to (if any) and the project's own shared libraries it uses.</summary>
    public sealed record Located(string Executable, string? Bundle, IReadOnlyList<string> ProjectSharedLibraries, string Description);

    public static Located Locate(BuildSettings settings, Prepared prepared)
    {
        var o = settings.Native!;
        if (o.Kind == NativeProjectKind.Make)
        {
            var path = Path.GetFullPath(Path.Combine(prepared.MakeDirectory!, o.ExpectedOutput!.Replace('/', Path.DirectorySeparatorChar)));
            if (!PathGuard.IsWithin(prepared.MakeDirectory!, path)) throw new BuildValidationException("The expected output is outside the project.");
            if (Directory.Exists(path) && path.EndsWith(".app", StringComparison.Ordinal))
            {
                var exe = Directory.Exists(Path.Combine(path, "Contents", "MacOS")) ? Directory.GetFiles(Path.Combine(path, "Contents", "MacOS")).FirstOrDefault() : null;
                if (exe is null) throw new BuildValidationException($"make {o.Target} succeeded, but {o.ExpectedOutput} has no executable in Contents/MacOS.");
                return new Located(exe, path, [], $"{o.ExpectedOutput} (macOS app bundle produced by make {o.Target})");
            }
            if (!File.Exists(path))
                throw new BuildValidationException($"make {o.Target} succeeded, but the expected output {o.ExpectedOutput} was not produced. Check the target and the output path (relative to the project folder).");
            return new Located(path, null, [], $"{o.ExpectedOutput} (from make {o.Target})");
        }
        var reply = CMakeFileApi.ReadReply(prepared.BuildDirectory!, prepared.CMakeSource!);
        var config = reply.IsMultiConfig ? o.BuildType : reply.Configurations.FirstOrDefault() ?? "";
        if (!reply.TargetsByConfiguration.TryGetValue(config, out var targets))
            throw new BuildValidationException($"CMake did not report the configuration {config} (available: {string.Join(", ", reply.Configurations)}).");
        var target = targets.FirstOrDefault(t => t.Name == o.Target)
                     ?? throw new BuildValidationException($"The project no longer defines a target named {o.Target}. Configure it again to list its targets.");
        if (target.Type != NativeTargetType.Executable)
            throw new BuildValidationException($"{o.Target} is a {target.Type} target, not an executable: HCG packages executables only.");
        var artifact = target.Artifacts.Select(a => CMakeFileApi.ArtifactPath(prepared.BuildDirectory!, a))
            .FirstOrDefault(a => !a.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) && !a.EndsWith(".lib", StringComparison.OrdinalIgnoreCase))
            ?? throw new BuildValidationException($"CMake reports no artifact for {o.Target}.");
        if (!File.Exists(artifact))
            throw new BuildValidationException($"The build finished, but {o.Target}'s artifact {artifact} does not exist.");
        var libraries = target.Dependencies
            .Select(d => targets.FirstOrDefault(t => t.Name == d))
            .Where(t => t?.Type is NativeTargetType.SharedLibrary or NativeTargetType.ModuleLibrary)
            .SelectMany(t => t!.Artifacts.Select(a => CMakeFileApi.ArtifactPath(prepared.BuildDirectory!, a)))
            .Where(a => !a.EndsWith(".lib", StringComparison.OrdinalIgnoreCase) && !a.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) && File.Exists(a))
            .Distinct().ToList();
        return new Located(artifact, CMakeFileApi.BundleOf(artifact), libraries, $"{o.Target} ({(config.Length == 0 ? "default configuration" : config)}, {reply.Generator}) → {artifact}");
    }

    /// <summary>
    /// Copies what is shipped into HCG's staging folder: the executable (or the whole .app the project produced, kept as
    /// it is), the project's own shared libraries the executable uses, and the runtime files the user listed. Object
    /// files and other build intermediates are never copied. Returns the executable's path relative to the staging folder.
    /// </summary>
    public static (string Executable, IReadOnlyList<string> Copied) Stage(Located located, BuildSettings settings, Prepared prepared, string staging, CancellationToken ct)
    {
        var o = settings.Native!;
        Directory.CreateDirectory(staging);
        var copied = new List<string>();
        string exeRelative;
        if (located.Bundle is { } bundle)
        {
            var name = Path.GetFileName(bundle.TrimEnd(Path.DirectorySeparatorChar));
            Packaging.Packagers.FileOps.CopyDirectory(bundle, Path.Combine(staging, name), ct);
            exeRelative = name + "/" + Path.GetRelativePath(bundle, located.Executable).Replace('\\', '/');
            copied.Add(name + "/");
        }
        else
        {
            var name = Path.GetFileName(located.Executable);
            File.Copy(located.Executable, Path.Combine(staging, name), overwrite: true);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(staging, name), File.GetUnixFileMode(located.Executable));
            exeRelative = name;
            copied.Add(name);
        }
        foreach (var lib in located.ProjectSharedLibraries)
        {
            File.Copy(lib, Path.Combine(staging, Path.GetFileName(lib)), overwrite: true);
            copied.Add(Path.GetFileName(lib));
        }
        var runtimeRoot = o.Kind == NativeProjectKind.Make ? prepared.MakeDirectory! : prepared.ProjectDirectory;
        foreach (var r in o.RuntimeFiles)
        {
            var from = Path.GetFullPath(Path.Combine(runtimeRoot, r.Replace('/', Path.DirectorySeparatorChar)));
            if (!PathGuard.IsWithin(runtimeRoot, from)) throw new BuildValidationException($"The runtime file {r} is outside the project.");
            var to = Path.Combine(staging, Path.GetFileName(from.TrimEnd(Path.DirectorySeparatorChar)));
            if (Directory.Exists(from)) Packaging.Packagers.FileOps.CopyDirectory(from, to, ct);
            else if (File.Exists(from)) File.Copy(from, to, overwrite: true);
            else throw new BuildValidationException($"The runtime file {r} does not exist after the build.");
            copied.Add(Path.GetFileName(to) + (Directory.Exists(from) ? "/" : ""));
        }
        return (exeRelative, copied);
    }

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.+/-]{0,127}$")] private static partial Regex TargetRegex();
    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.+-]{0,63}$")] private static partial Regex ExecutableNameRegex();
    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.:-]{0,127}$")] private static partial Regex PresetRegex();
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_.+-]*(?::[A-Z]+)?=[^\r\n\0]*$")] private static partial Regex CacheEntryRegex();
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*=[^\r\n\0]*$")] private static partial Regex MakeVariableRegex();
}

/// <summary>
/// Diagnostics from GCC/Clang ("file:line:col: error: …"), MSVC ("file(line): error C1234: …"), the linker
/// ("undefined reference to …"), make ("make: *** … Error 1") and CMake ("CMake Error at file:line").
/// </summary>
public sealed partial class NativeOutputParser : IBuildOutputParser
{
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    public IBuildOutputParser Clone()
    {
        var copy = new NativeOutputParser();
        copy._seen.UnionWith(_seen);
        return copy;
    }

    public BuildMessage? Parse(string line)
    {
        BuildMessage? m = null;
        if (GccRegex().Match(line) is { Success: true } g)
            m = new BuildMessage(Severity(g.Groups["sev"].Value), g.Groups["msg"].Value.Trim(), null, g.Groups["file"].Value, int.Parse(g.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture));
        else if (MsvcRegex().Match(line) is { Success: true } v)
            m = new BuildMessage(Severity(v.Groups["sev"].Value), v.Groups["msg"].Value.Trim(), v.Groups["code"].Value, v.Groups["file"].Value, int.Parse(v.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture));
        else if (CMakeRegex().Match(line) is { Success: true } c)
            m = new BuildMessage(c.Groups["sev"].Value.Contains("Warning", StringComparison.Ordinal) ? DiagnosticSeverity.Warning : DiagnosticSeverity.Error,
                c.Value.Trim(), "CMake", c.Groups["file"].Value, int.Parse(c.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture));
        else if (LinkerRegex().Match(line) is { Success: true } l)
            m = new BuildMessage(DiagnosticSeverity.Error, l.Value.Trim(), "linker");
        else if (MakeRegex().Match(line) is { Success: true } mk)
            m = new BuildMessage(DiagnosticSeverity.Error, mk.Value.Trim(), "make");
        else if (TheosErrorRegex().Match(line) is { Success: true } th)
            m = new BuildMessage(DiagnosticSeverity.Error, th.Groups["msg"].Value.Trim(), "Theos");
        else if (MakefileErrorRegex().Match(line) is { Success: true } me)
            m = new BuildMessage(DiagnosticSeverity.Error, me.Groups["msg"].Value.Trim(), "make", me.Groups["file"].Value, int.Parse(me.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture));
        if (m is null || m.Text.Length == 0) return null;
        return _seen.Add($"{m.Severity}|{m.File}|{m.Line}|{m.Text}") ? m : null;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"==> (?:Error|ERROR):\s*(?<msg>.+)$")] private static partial System.Text.RegularExpressions.Regex TheosErrorRegex();
    [System.Text.RegularExpressions.GeneratedRegex(@"^(?<file>(?:[^:\s][^:]*/)?(?:GNU)?[Mm]akefile):(?<line>\d+): \*\*\* (?<msg>.+)$")] private static partial System.Text.RegularExpressions.Regex MakefileErrorRegex();

    private static DiagnosticSeverity Severity(string s) => s.StartsWith("warning", StringComparison.OrdinalIgnoreCase) ? DiagnosticSeverity.Warning : DiagnosticSeverity.Error;

    [GeneratedRegex(@"^(?<file>(?:[A-Za-z]:)?[^:\s][^:]*\.(?:c|cc|cpp|cxx|c\+\+|h|hh|hpp|hxx|m|mm|inl|ipp)):(?<line>\d+):(?:\d+:)?\s*(?<sev>fatal error|error|warning):\s*(?<msg>.+)$")]
    private static partial Regex GccRegex();
    [GeneratedRegex(@"^\s*(?<file>[^()]+)\((?<line>\d+)(?:,\d+)?\)\s*:\s*(?<sev>fatal error|error|warning)\s+(?<code>[A-Z]+\d+)\s*:\s*(?<msg>.+)$")]
    private static partial Regex MsvcRegex();
    [GeneratedRegex(@"^CMake (?<sev>Error|Warning(?: \(dev\))?) at (?<file>[^:]+):(?<line>\d+)")]
    private static partial Regex CMakeRegex();
    [GeneratedRegex(@"(?:undefined reference to|Undefined symbols for architecture|unresolved external symbol|ld: (?:library|framework) not found|cannot find -l\S+).*")]
    private static partial Regex LinkerRegex();
    [GeneratedRegex(@"^(?:g?make|mingw32-make)(?:\[\d+\])?: \*\*\* .+$")]
    private static partial Regex MakeRegex();
}
