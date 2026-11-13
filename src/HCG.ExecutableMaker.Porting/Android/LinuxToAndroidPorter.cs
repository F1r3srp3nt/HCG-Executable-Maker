using System.Reflection;
using System.Security;
using System.Text.RegularExpressions;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.ProjectInspection;
using HCG.ExecutableMaker.ProjectInspection.Mobile;

namespace HCG.ExecutableMaker.Porting.Android;

/// <summary>
/// Linux → Android for C/C++ command-line programs built with CMake: a new Android Gradle project whose NDK build
/// includes the original CMake project unchanged and runs the program's main() in a console host activity.
/// </summary>
public sealed partial class LinuxToAndroidPorter : IPorter
{
    public PortingPath Path => PortingPath.LinuxToAndroid;
    private const string ProgramFolder = "program";
    private const string AndroidFolder = "android";

    public IReadOnlyList<string> Scope { get; } =
    [
        "Supported: C and C++ command-line programs built with CMake (an add_executable target with main()) that use the C/C++ standard libraries and POSIX APIs available in Android's bionic libc.",
        "Generated: an Android app (API 24+, arm64-v8a and x86_64) whose NDK build includes the original CMakeLists.txt unchanged; a console screen supplies arguments and standard input and shows standard output/error.",
        "Not converted (blockers): GUI toolkits (GTK, Qt, X11, SDL, curses), desktop services (D-Bus, ALSA, PulseAudio, systemd), System V IPC, glibc-only APIs, third-party native libraries that need their own Android build.",
        "Not supported in this version: Makefile/Autotools/Meson-only projects, Rust, Go, Python, Java desktop apps, .NET apps (use .NET for Android), daemons/services.",
    ];

    public IReadOnlyList<PortSource> FindSources(SourceIndex index)
    {
        var sources = new List<PortSource>();
        foreach (var root in AndroidGradleAnalyzer.FindRoots(index))
            sources.Add(new PortSource
            {
                Path = Path, Key = "existing-android:" + root, Kind = "Existing Android Gradle project",
                DisplayName = root.Length == 0 ? "Android project (root)" : root + "/", Directory = root, Target = root,
            });
        foreach (var root in CMakeProject.FindRoots(index))
        {
            var cmake = CMakeProject.Read(index, root);
            foreach (var exe in cmake.Targets.Where(t => t.Executable))
                sources.Add(new PortSource
                {
                    Path = Path, Key = $"cmake:{root}:{exe.Name}", Kind = "CMake C/C++ executable",
                    DisplayName = $"{exe.Name} — {(root.Length == 0 ? "CMakeLists.txt" : root + "/CMakeLists.txt")}", Directory = root, Target = exe.Name,
                });
        }
        // Linux projects outside this version's scope are listed so the user gets an explanation, not silence.
        if (sources.Count == 0)
        {
            foreach (var (marker, kind) in new[] { ("Makefile", "Makefile project"), ("configure.ac", "Autotools project"), ("meson.build", "Meson project"), ("Cargo.toml", "Rust (Cargo) project"), ("go.mod", "Go module"), ("setup.py", "Python project"), ("pyproject.toml", "Python project") })
                if (index.Named(marker).OrderBy(SourceIndex.Depth).FirstOrDefault() is { } file)
                {
                    var dir = file.Contains('/') ? file[..file.LastIndexOf('/')] : "";
                    sources.Add(new PortSource { Path = Path, Key = "unsupported:" + file, Kind = kind, DisplayName = file, Directory = dir, Target = file });
                }
        }
        return sources;
    }

    public PortingAssessment Assess(SourceIndex index, PortSource source, ToolchainReport? toolchains)
    {
        var common = new PortingAssessment
        {
            Path = Path, Source = source, Capability = PortingCapability.NotSupported, Summary = "", Scope = Scope,
            Toolchains = Toolchains(toolchains), Signing = Signing, AssessedAt = DateTimeOffset.UtcNow,
        };
        if (source.Key.StartsWith("existing-android:", StringComparison.Ordinal))
            return common with
            {
                Capability = PortingCapability.BuildExistingTarget,
                Summary = $"{source.DisplayName} is already an Android project. No conversion is needed: build it in Mobile Build Studio (APK, App Bundle).",
            };
        if (source.Key.StartsWith("unsupported:", StringComparison.Ordinal))
            return common with
            {
                Summary = $"{source.Kind} ({source.DisplayName}): not supported by this version's Linux → Android conversion, which handles C/C++ programs built with CMake. " +
                          (source.Kind.Contains("Makefile", StringComparison.Ordinal) || source.Kind.Contains("Autotools", StringComparison.Ordinal) || source.Kind.Contains("Meson", StringComparison.Ordinal)
                              ? "Add a CMakeLists.txt that builds the program to make it eligible."
                              : "No Android target can be generated for this language/framework."),
            };

        var cmake = CMakeProject.Read(index, source.Directory);
        var exe = cmake.Targets.First(t => t.Executable && t.Name == source.Target);
        var findings = new List<PortingFinding>();
        var units = new List<ReusableUnit>();

        // Source files: the target's sources plus internal libraries it links; dynamic lists fall back to the folder.
        var internalLibs = cmake.Targets.Where(t => !t.Executable && exe.LinkLibraries.Contains(t.Name)).ToList();
        IReadOnlyList<string> FilesOf(CMakeProject.Target t) => t.DynamicSources || t.Sources.Count == 0
            ? CFiles(index, t.Directory)
            : t.Sources.Where(index.Files.Contains).Where(IsCFile).ToList();
        var plans = new List<CFilePlan>();
        foreach (var unit in internalLibs.Prepend(exe))
        {
            var unitFiles = FilesOf(unit);
            var unitPlans = unitFiles.Select(f => LinuxToAndroidRules.Analyze(new CSource(f, File.ReadAllText(index.FullPath(f))))).ToList();
            // Headers included by the target's sources are part of the reusable code too.
            plans.AddRange(unitPlans);
            units.Add(new ReusableUnit(unit == exe ? $"{unit.Name} (executable)" : $"{unit.Name} (library)",
                unit == exe ? "Compiled unchanged into the app's native library" : "Linked unchanged",
                unitPlans.Count, unitPlans.Sum(p => p.Source.Lines.Count), unitPlans.Count(p => p.HasBlockers), 0));
            findings.AddRange(unitPlans.SelectMany(p => p.Findings));
        }
        var headers = CFiles(index, source.Directory).Where(f => f.EndsWith(".h", StringComparison.Ordinal) || f.EndsWith(".hpp", StringComparison.Ordinal))
            .Where(f => !plans.Any(p => p.Source.Path == f)).ToList();
        var headerPlans = headers.Select(f => LinuxToAndroidRules.Analyze(new CSource(f, File.ReadAllText(index.FullPath(f))))).ToList();
        if (headerPlans.Count > 0)
        {
            units.Add(new ReusableUnit("Headers", "Included unchanged", headerPlans.Count, headerPlans.Sum(p => p.Source.Lines.Count), headerPlans.Count(p => p.HasBlockers), 0));
            findings.AddRange(headerPlans.SelectMany(p => p.Findings));
        }

        var mains = plans.Where(p => p.DefinesMain).ToList();
        if (mains.Count == 0)
            findings.Add(new PortingFinding
            {
                Category = FindingCategory.BuildSystem, Disposition = FindingDisposition.Blocker, Title = $"No main() found for '{exe.Name}'",
                Detail = "The console host calls the program's main(); HCG could not find it in the target's sources.",
                File = cmake.Files.FirstOrDefault(),
            });
        else
            findings.Add(new PortingFinding
            {
                Category = FindingCategory.UiAdaptation, Disposition = FindingDisposition.AutoAdapted, Title = "Console program → Android console screen",
                Detail = "The program's main() runs unchanged in the app. The screen provides the command-line arguments, an input field for standard input, and shows standard output and error (also logged to logcat, tag HCGPort). It runs once per app launch.",
                File = mains[0].Source.Path,
            });

        // PkgConfig is only the lookup mechanism; the modules it looks up are reported individually.
        foreach (var (package, file) in cmake.Packages.Where(p => p.Package != "PkgConfig").DistinctBy(p => p.Package))
        {
            var threads = package == "Threads";
            findings.Add(new PortingFinding
            {
                Category = FindingCategory.Dependency, File = file,
                Disposition = threads ? FindingDisposition.AutoAdapted : FindingDisposition.Blocker,
                Title = threads ? "find_package(Threads)" : $"Dependency {package}",
                Detail = threads ? "Threads are part of bionic; the package is found again in the Android build so Threads::Threads resolves."
                    : "This package must be available as an Android (NDK) build for every ABI; HCG v1 does not build or download native dependencies.",
                Guidance = threads ? null : "Provide an Android build (prefab AAR, prebuilt .so with CMake config) or remove the dependency.",
            });
        }
        foreach (var lib in exe.LinkLibraries.Distinct())
        {
            if (lib is "pthread" or "rt" or "-lpthread" or "-lrt")
                findings.Add(new PortingFinding
                {
                    Category = FindingCategory.BuildSystem, Disposition = FindingDisposition.AutoAdapted, Title = $"Link library '{lib}' dropped",
                    Detail = "pthread and rt functions are part of Android's libc; there is no separate library to link.", File = cmake.Files.FirstOrDefault(),
                });
            else if (lib is "m" or "dl" or "z" or "log" or "android" || lib.Contains("::", StringComparison.Ordinal) || lib.Contains('$') || cmake.Targets.Any(t => t.Name == lib) || lib.StartsWith('-')) { }
            else
                findings.Add(new PortingFinding
                {
                    Category = FindingCategory.Dependency, Disposition = FindingDisposition.Blocker, Title = $"Links system library '{lib}'",
                    Detail = "Desktop system libraries are not available to Android apps (the NDK provides libc, libm, libdl, libz, liblog, libandroid and a few graphics/media libraries).",
                    Guidance = "Provide an Android build of the library or remove the dependency.", File = cmake.Files.FirstOrDefault(),
                });
        }
        foreach (var (construct, file) in cmake.Unsupported.DistinctBy(u => u.Construct))
            findings.Add(new PortingFinding
            {
                Category = FindingCategory.BuildSystem, Disposition = construct.StartsWith("file(", StringComparison.Ordinal) ? FindingDisposition.NeedsReview : FindingDisposition.Blocker,
                Title = construct, File = file,
                Detail = construct.StartsWith("file(", StringComparison.Ordinal)
                    ? "Source globbing works in the Android build too (it runs the same CMake), but HCG's assessment scanned the folder instead of an exact source list."
                    : "Downloading or building external projects during configuration is not supported in the generated Android build.",
            });
        if (plans.Any(p => p.UsesNetwork))
            findings.Add(new PortingFinding
            {
                Category = FindingCategory.Permission, Disposition = FindingDisposition.AutoAdapted, Title = "INTERNET permission",
                Detail = "The program uses sockets; the generated manifest requests android.permission.INTERNET.",
            });

        var blockers = findings.Count(f => f.Disposition == FindingDisposition.Blocker);
        var files = units.Sum(u => u.Files);
        var clean = units.Sum(u => u.FilesUnchanged);
        var capability = blockers > 0 ? PortingCapability.BlockedNeedsPorting : PortingCapability.GenerateTarget;
        return common with
        {
            Capability = capability,
            Summary = $"{exe.Name}: C/C++ program built with CMake{(cmake.ProjectName is null ? "" : $" (project {cmake.ProjectName})")}. " + (capability == PortingCapability.GenerateTarget
                ? $"HCG can generate an Android app: all {files} source file(s) are compiled unchanged with the NDK; {findings.Count(f => f.Disposition == FindingDisposition.NeedsReview)} item(s) to review."
                : $"{blockers} blocker(s) need manual porting before an Android app can work ({clean} of {files} source file(s) have no blockers)."),
            Reusable = units,
            Findings = findings.OrderBy(f => f.Disposition switch { FindingDisposition.Blocker => 0, FindingDisposition.NeedsReview => 1, FindingDisposition.AutoAdapted => 2, _ => 3 })
                .ThenBy(f => f.File, StringComparer.Ordinal).ThenBy(f => f.Line).ToList(),
            SuggestedAppName = exe.Name,
            SuggestedAppId = "com.example." + Regex.Replace(exe.Name.ToLowerInvariant(), "[^a-z0-9]", ""),
        };
    }

    private static readonly string[] Signing =
    [
        "Debug builds are signed automatically with the Android debug key (installable on emulators and devices with USB debugging).",
        "Release builds need your own upload/signing keystore (Mobile Build Studio can use the project's signing config). HCG never creates release keys.",
        "Google Play upload and store listing stay manual.",
    ];

    private static IReadOnlyList<ToolchainRequirement> Toolchains(ToolchainReport? r)
    {
        ToolchainRequirement T(string id, string name, string? min, string? fallback)
        {
            var t = r?.Find(id);
            return new ToolchainRequirement(id, name, min, t?.IsAvailable == true, t?.IsAvailable == true ? (t.Version is { } v ? $"{name} {v}" : t.ExecutablePath) : t?.InstallHint ?? fallback);
        }
        return
        [
            T(ToolchainIds.Jdk, "JDK", "17", "Install a JDK 17+."),
            T(ToolchainIds.AndroidSdk, "Android SDK (API 35 platform)", "35", "Install the Android SDK."),
            T(ToolchainIds.AndroidNdk, "Android NDK", null, "SDK Manager → SDK Tools → NDK (Side by side)."),
            T(ToolchainIds.AndroidCmake, "CMake (Android SDK)", "3.18.1", "SDK Manager → SDK Tools → CMake."),
            T(ToolchainIds.Adb, "adb (for emulator/device launch)", null, "SDK Manager → Platform-Tools."),
        ];
    }

    private static bool IsCFile(string f) => f.EndsWith(".c", StringComparison.Ordinal) || f.EndsWith(".cc", StringComparison.Ordinal) || f.EndsWith(".cpp", StringComparison.Ordinal) || f.EndsWith(".cxx", StringComparison.Ordinal);

    private static IReadOnlyList<string> CFiles(SourceIndex index, string dir)
    {
        var prefix = dir.Length == 0 ? "" : dir + "/";
        return index.WithExtension(".c", ".cc", ".cpp", ".cxx", ".h", ".hpp", ".hh")
            .Where(f => f.StartsWith(prefix, StringComparison.Ordinal))
            .Where(f => !f[prefix.Length..].Split('/').SkipLast(1).Any(d => CMakeProject.Ignored.Contains(d) || d.StartsWith("cmake-build", StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal).ToList();
    }

    // ───────────── Generation ─────────────

    public (IReadOnlyList<string> Steps, string BuildRoot) Generate(SourceIndex index, PortingAssessment assessment, PortOptions options, PortOutput output)
    {
        if (assessment.Capability is PortingCapability.BuildExistingTarget or PortingCapability.NotSupported)
            throw new InvalidOperationException("Nothing to generate for this source: " + assessment.Summary);
        if (assessment.Capability == PortingCapability.BlockedNeedsPorting && !options.Draft)
            throw new InvalidOperationException("This source has blockers. Resolve them, or generate an explicit draft (blocker sites are marked with #error).");
        if (!AppIdRegex().IsMatch(options.AppId))
            throw new InvalidOperationException($"'{options.AppId}' is not a valid Android application id (e.g. com.example.tool).");

        var source = assessment.Source;
        var cmake = CMakeProject.Read(index, source.Directory);
        var steps = new List<string>();
        output.CopyTree(index, "", ProgramFolder);
        steps.Add($"Copied the project into {ProgramFolder}/ (unchanged; the original is not modified).");
        if (options.Draft)
        {
            var marked = 0;
            foreach (var group in assessment.Blockers.Where(b => b.File is not null && b.Line is not null && IsCOrHeader(b.File)).GroupBy(b => b.File!))
            {
                var lines = File.ReadAllText(index.FullPath(group.Key)).Replace("\r\n", "\n").Split('\n').ToList();
                foreach (var b in group.OrderByDescending(b => b.Line))
                {
                    var message = (b.Title + (b.Guidance is null ? "" : " — " + b.Guidance)).Replace("\\", "\\\\").Replace("\"", "'");
                    lines.Insert(b.Line!.Value - 1, $"#error \"HCG port blocker: {message}\"");
                    marked++;
                }
                var target = PortOutput.Join(ProgramFolder, group.Key);
                output.Modify(target, group.Key, string.Join('\n', lines), "Draft: blocker marked with #error");
            }
            steps.Add($"DRAFT: {marked} blocker site(s) carry #error markers. The Android build fails until they are ported by hand.");
        }

        var t = ReadTemplates();
        var reports = assessment.Findings;
        var packages = cmake.Packages.Where(p => p.Package == "Threads").Select(p => $"find_package({p.Package} REQUIRED)").Distinct();
        var cpp = PortOutput.Join(AndroidFolder, "app/src/main/cpp");
        var programDir = PortOutput.Relative(cpp, PortOutput.Join(ProgramFolder, source.Directory));
        var ndk = options.NdkVersion;
        var cmakeVersion = options.CmakeVersion;
        var replacements = new Dictionary<string, string>
        {
            ["{{PROJECT_NAME}}"] = Regex.Replace(options.AppName, @"[^\w\- ]", "").Trim() is { Length: > 0 } pn ? pn : "Port",
            ["{{APP_ID}}"] = options.AppId,
            ["{{VERSION_CODE}}"] = options.BuildNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["{{VERSION_NAME}}"] = options.Version.Replace("\"", ""),
            ["{{NDK_VERSION_LINE}}"] = ndk is null ? "    // ndkVersion: not pinned (no NDK was detected when this port was generated)." : $"    ndkVersion = \"{ndk}\"",
            ["{{CMAKE_VERSION_LINE}}"] = cmakeVersion is null ? "            // version: not pinned (no SDK CMake was detected)." : $"            version = \"{cmakeVersion}\"",
            ["{{APP_NAME}}"] = SecurityElement.Escape(options.AppName) ?? "Port",
            ["{{PERMISSIONS}}"] = reports.Any(f => f.Title == "INTERNET permission") ? "    <uses-permission android:name=\"android.permission.INTERNET\" />" : "",
            ["{{PROGRAM}}"] = JavaString(source.Target),
            ["{{DEFAULT_ARGUMENTS}}"] = JavaString(options.DefaultArguments),
            ["{{TARGET}}"] = source.Target,
            ["{{PROGRAM_DIR}}"] = programDir,
            ["{{FIND_PACKAGES}}"] = string.Join('\n', packages),
        };
        foreach (var (name, content) in t)
        {
            var dest = PortOutput.Join(AndroidFolder, name.EndsWith(".tmpl", StringComparison.Ordinal) ? name[..^5] : name);
            if (content is byte[] bytes)
            {
                output.AddBinary(dest, bytes, "Gradle wrapper (Gradle 8.14.3)", executable: name == "gradlew");
                continue;
            }
            var text = (string)content;
            foreach (var (k, v) in replacements) text = text.Replace(k, v, StringComparison.Ordinal);
            output.Add(dest, text, Reason(name));
            if (name == "gradlew" && !OperatingSystem.IsWindows())
                File.SetUnixFileMode(System.IO.Path.Combine(output.Root, dest), File.GetUnixFileMode(System.IO.Path.Combine(output.Root, dest)) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        steps.Add($"Generated {AndroidFolder}/: Android Gradle project (AGP 8.7.3, Gradle 8.14.3 wrapper), app id {options.AppId}, API 24+ (target 35), ABIs arm64-v8a and x86_64{(ndk is null ? "" : $", NDK {ndk}")}{(cmakeVersion is null ? "" : $", CMake {cmakeVersion}")}.");
        steps.Add($"The NDK build includes {programDir}/CMakeLists.txt unchanged and compiles target '{source.Target}' into libhcgport.so with a console bridge (stdout/stderr/stdin pipes, exit() interception).");
        steps.Add("UI adaptation: console screen with arguments, standard input and output (MainActivity, no third-party libraries).");
        var review = assessment.Findings.Count(f => f.Disposition == FindingDisposition.NeedsReview);
        if (review > 0) steps.Add($"{review} item(s) need your review (see the assessment).");
        steps.Add("Next: compile a debug APK (Compile step), then launch it on an emulator. Device testing and Play Store upload stay manual.");
        return (steps, AndroidFolder);
    }

    private static bool IsCOrHeader(string f) => IsCFile(f) || f.EndsWith(".h", StringComparison.Ordinal) || f.EndsWith(".hpp", StringComparison.Ordinal);

    private static string Reason(string name) => name switch
    {
        "app/src/main/cpp/CMakeLists.txt.tmpl" => "NDK build: includes the original CMake project and builds the program into libhcgport.so",
        "app/src/main/cpp/hcg_console_bridge.c" => "Console bridge: runs main(), pipes stdio, intercepts exit()",
        "app/src/main/java/hcg/port/console/MainActivity.java.tmpl" => "Console screen (UI adaptation for a terminal program)",
        "app/src/main/AndroidManifest.xml.tmpl" => "App manifest",
        "app/build.gradle.kts.tmpl" => "App module build script",
        _ => "Gradle project file",
    };

    private static string JavaString(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");

    /// <summary>Template files: relative name → string (text) or byte[] (binary).</summary>
    internal static IReadOnlyList<(string Name, object Content)> ReadTemplates()
    {
        var asm = typeof(LinuxToAndroidPorter).Assembly;
        const string prefix = "Templates/android/";
        var list = new List<(string, object)>();
        foreach (var res in asm.GetManifestResourceNames().Select(r => (Raw: r, Norm: r.Replace('\\', '/'))).Where(r => r.Norm.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(r => r.Norm, StringComparer.Ordinal))
        {
            using var stream = asm.GetManifestResourceStream(res.Raw)!;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var name = res.Norm[prefix.Length..];
            list.Add(name.EndsWith(".jar", StringComparison.Ordinal) ? (name, ms.ToArray()) : (name, System.Text.Encoding.UTF8.GetString(ms.ToArray())));
        }
        return list;
    }

    [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9_]*(\.[a-zA-Z][a-zA-Z0-9_]*)+$")]
    private static partial Regex AppIdRegex();
}
