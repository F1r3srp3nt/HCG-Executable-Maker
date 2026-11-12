using System.Diagnostics;
using HCG.ExecutableMaker.BuildEngine;
using HCG.ExecutableMaker.BuildEngine.Native;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Infrastructure.Persistence;
using HCG.ExecutableMaker.Packaging;
using HCG.ExecutableMaker.Packaging.Packagers;
using HCG.ExecutableMaker.ProjectInspection;
using HCG.ExecutableMaker.ProjectInspection.Native;
using Xunit.Abstractions;

namespace HCG.ExecutableMaker.Tests;

/// <summary>Runs only where CMake (3.20+), make and a C/C++ compiler are installed (the real build tests).</summary>
public sealed class NativeToolsFactAttribute : FactAttribute
{
    public NativeToolsFactAttribute()
    {
        var locator = new ExecutableLocator();
        if (OperatingSystem.IsWindows() || locator.Find("cmake") is null || locator.Find("make") is null || (locator.Find("cc") ?? locator.Find("gcc") ?? locator.Find("clang")) is null)
            Skip = "Needs CMake, make and a C/C++ compiler on a Linux or macOS host.";
    }
}

/// <summary>C/C++ detection (static) and real CMake / make / source builds through BuildService.</summary>
/// <remarks>
/// In the "DeveloperDir" collection: on macOS, compiler and make detection asks xcode-select, which follows the
/// process-wide DEVELOPER_DIR that XcodeDiscoveryTests temporarily points at fake Xcodes. Running alongside them would
/// make the Command Line Tools look missing.
/// </remarks>
[Collection("DeveloperDir")]
public class NativeTests : IDisposable
{
    private readonly TempDir _t = new();
    private readonly ITestOutputHelper _out;
    private static readonly Lazy<Task<ToolchainReport>> Report = new(() =>
        new HCG.ExecutableMaker.Infrastructure.Toolchains.ToolchainDetector(new HCG.ExecutableMaker.Infrastructure.Processes.ProcessRunner(), new ExecutableLocator(),
            new AppPaths(Path.Combine(Path.GetTempPath(), "hcg-native-tc-" + Guid.NewGuid().ToString("N")[..8])), TimeProvider.System).DetectAsync(CancellationToken.None));

    public NativeTests(ITestOutputHelper output) => _out = output;
    public void Dispose() => _t.Dispose();

    private static string Fixture(string name) => Path.Combine(Fixtures.RepoRoot, "tests", "Fixtures", "Native", name);

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(f, target);
        }
    }

    private static NativeAnalysis Scan(string dir) => NativeProjectScanner.Scan(SourceIndex.Build(dir));

    // ------------------------------------------------------------------ detection (static)

    [Fact]
    public void Each_kind_of_project_is_classified_from_its_files()
    {
        foreach (var (fixture, kind, name) in new[]
                 {
                     ("CHello", NativeProjectKind.CMake, "CHello"), ("CppHello", NativeProjectKind.CMake, "CppHello"), ("LibOnly", NativeProjectKind.CMake, "OnlyLib"),
                     ("MakeHello", NativeProjectKind.Make, "MakeHello"), ("LooseSources", NativeProjectKind.Sources, "LooseSources"), ("TheosTweak", NativeProjectKind.Theos, "HCGTweak"),
                 })
        {
            var dir = _t.Combine(fixture);
            CopyDir(Fixture(fixture), dir);
            var p = Assert.Single(Scan(dir).Projects);
            Assert.Equal(kind, p.Kind);
            Assert.Equal(name, p.Name);
        }
        var c = Assert.Single(Scan(_t.Combine("CHello")).Projects);
        Assert.Equal(["C"], c.Languages);
        Assert.Equal("1.4.0", c.Version);
        Assert.Equal(["chello"], c.ExecutableHints);
        Assert.Equal(["CXX"], Assert.Single(Scan(_t.Combine("CppHello")).Projects).Languages);
        var make = Assert.Single(Scan(_t.Combine("MakeHello")).Projects);
        Assert.Equal(["all", "bin/makehello", "slow", "clean"], make.MakeTargets);
        Assert.Contains("all", make.PhonyTargets);
        var loose = Assert.Single(Scan(_t.Combine("LooseSources")).Projects);
        Assert.Equal(["main.c", "scratch.c"], loose.Sources.Where(s => s.HasMain).Select(s => s.Path));
    }

    [Fact]
    public void Project_roots_are_found_inside_a_repository_without_treating_dependencies_or_build_output_as_applications()
    {
        var repo = _t.Combine("repo");
        CopyDir(Fixture("MultiTarget"), Path.Combine(repo, "engine"));
        _t.Write("repo/engine/build/CMakeLists.txt", "project(Stale)\nadd_executable(stale stale.c)\n");          // stale build output
        _t.Write("repo/engine/cmake-build-debug/CMakeLists.txt", "project(Ide)\n");                                // IDE build folder
        _t.Write("repo/vendor/zlib/CMakeLists.txt", "project(zlib C)\nadd_library(z z.c)\n");                    // vendored dependency
        _t.Write("repo/third_party/tool/Makefile", "all:\n\tcc tool.c\n");
        _t.Write("repo/README.md", "# Repo\n");
        var native = Scan(repo);
        var p = Assert.Single(native.Projects);
        Assert.Equal("engine", p.Directory);
        Assert.Equal("Multi", p.Name);
        Assert.Equal(["calc", "greeter"], p.ExecutableHints);
        Assert.Contains("dep", p.LibraryHints); // from the vendored add_subdirectory, as a library of the same project
    }

    [Fact]
    public void Theos_is_recognized_before_make_and_never_offered_as_a_desktop_app()
    {
        var repo = _t.Combine("tweaks");
        CopyDir(Fixture("TheosTweak"), Path.Combine(repo, "HCGTweak"));
        var native = Scan(repo);
        var theos = Assert.Single(native.Projects);
        Assert.Equal(NativeProjectKind.Theos, theos.Kind);
        Assert.Contains(theos.Evidence, e => e.Contains("$(THEOS)/makefiles/common.mk"));
        Assert.Contains(theos.Evidence, e => e.Contains("Debian package control file"));
        Assert.Contains("never packages it as a desktop app or an IPA", theos.UnsupportedReason);

        var analysis = NativeProjectInspector.Build(native, null, DateTimeOffset.UtcNow);
        Assert.Equal(SupportLevel.Unsupported, analysis.Support);
        Assert.Empty(analysis.RecommendedFormats);
        var entry = Assert.Single(analysis.Projects);
        Assert.False(entry.IsRunnableApplication);
        Assert.Equal("Theos project — not a desktop app", entry.ApplicationType);
        Assert.Empty(entry.CompatiblePlatforms);
        Assert.Equal(SupportLevel.Unsupported, NativeCapabilities.Evaluate(theos, null).Support);
    }

    [Fact]
    public void Other_build_systems_are_named_and_not_built()
    {
        _t.Write("m/meson.build", "project('x', 'c')\n");
        _t.Write("a/configure.ac", "AC_INIT([x], [1])\n");
        _t.Write("v/App.vcxproj", "<Project />\n");
        var kinds = Scan(_t.Path).Projects.ToDictionary(p => p.Directory, p => p);
        Assert.Contains("Meson builds are recognized but not run", kinds["m"].UnsupportedReason);
        Assert.Contains("does not run configure scripts", kinds["a"].UnsupportedReason);
        Assert.Contains("Visual Studio C++ projects", kinds["v"].UnsupportedReason);
        Assert.All(kinds.Values, p => Assert.Equal(NativeProjectKind.Unsupported, p.Kind));
    }

    [Fact]
    public void A_convenience_makefile_never_hides_a_web_or_dotnet_project()
    {
        _t.Write("site/package.json", """{ "name": "site", "dependencies": { "vite": "^7.0.0" } }""");
        _t.Write("site/Makefile", "deploy:\n\tnpm run build\n");
        Assert.Equal(25, new NativeProjectInspector(TimeProvider.System).Match(SourceIndex.Build(_t.Combine("site"))));
        CopyDir(Fixture("CHello"), _t.Combine("c"));
        Assert.Equal(40, new NativeProjectInspector(TimeProvider.System).Match(SourceIndex.Build(_t.Combine("c"))));
    }

    [Fact]
    public void Before_configuration_a_cmake_project_is_listed_as_needing_it_and_after_it_lists_real_executable_targets_only()
    {
        var p = new NativeProjectInfo { Directory = "", Kind = NativeProjectKind.CMake, ManifestPath = "CMakeLists.txt", Name = "Multi", Languages = ["C"] };
        var before = NativeProjectInspector.Build(new NativeAnalysis { Projects = [p] }, null, DateTimeOffset.UtcNow);
        Assert.Equal("CMake project — configure to list its targets", Assert.Single(before.Projects).ApplicationType);
        Assert.False(before.Projects[0].IsRunnableApplication);

        var configured = p with
        {
            Configured = new NativeConfiguration
            {
                Generator = "Ninja", Configuration = "Release",
                Targets =
                [
                    new NativeTargetInfo { Name = "calc", Type = NativeTargetType.Executable, Artifacts = ["calc"] },
                    new NativeTargetInfo { Name = "greeter", Type = NativeTargetType.Executable, Artifacts = ["greeter"], Dependencies = ["greet"] },
                    new NativeTargetInfo { Name = "greet", Type = NativeTargetType.SharedLibrary, Artifacts = ["libgreet.so"] },
                    new NativeTargetInfo { Name = "mathlib", Type = NativeTargetType.StaticLibrary, Artifacts = ["libmathlib.a"] },
                    new NativeTargetInfo { Name = "docs", Type = NativeTargetType.Utility },
                ],
            },
        };
        var host = new ToolchainReport(new HostEnvironment(TargetPlatform.Linux, TargetArchitecture.X64, "Linux", ".NET", "linux"),
            [new ToolchainInfo { Id = ToolchainIds.CMake, DisplayName = "CMake", Category = "C / C++", Status = ToolchainStatus.Installed, ExecutablePath = "/usr/bin/cmake", Version = "3.28.3" },
             new ToolchainInfo { Id = ToolchainIds.CCompiler, DisplayName = "C", Category = "C / C++", Status = ToolchainStatus.Installed, ExecutablePath = "/usr/bin/cc" },
             new ToolchainInfo { Id = ToolchainIds.Ninja, DisplayName = "Ninja", Category = "C / C++", Status = ToolchainStatus.Installed, ExecutablePath = "/usr/bin/ninja" }], null, null, DateTimeOffset.UtcNow);
        var after = NativeProjectInspector.Build(new NativeAnalysis { Projects = [configured] }, host, DateTimeOffset.UtcNow);
        Assert.Equal(["CMakeLists.txt#calc", "CMakeLists.txt#greeter"], after.Projects.Select(d => d.RelativePath));
        Assert.All(after.Projects, d => Assert.True(d.IsRunnableApplication));
        Assert.Equal(SupportLevel.Supported, after.Support);
        Assert.Contains("Libraries (not runnable on their own): greet, mathlib", after.SupportSummary);
        Assert.Null(after.PrimaryProjectPath); // two executables: the user chooses
    }

    [Fact]
    public void Missing_tools_are_named_with_setup_guidance()
    {
        var p = new NativeProjectInfo { Directory = "", Kind = NativeProjectKind.CMake, ManifestPath = "CMakeLists.txt", Name = "X", Languages = ["C", "CXX"] };
        var report = new ToolchainReport(new HostEnvironment(TargetPlatform.Linux, TargetArchitecture.X64, "Linux", ".NET", "linux"),
            [new ToolchainInfo { Id = ToolchainIds.CMake, DisplayName = "CMake", Category = "C / C++", Status = ToolchainStatus.Missing, InstallHint = "sudo apt install cmake" },
             new ToolchainInfo { Id = ToolchainIds.CCompiler, DisplayName = "C", Category = "C / C++", Status = ToolchainStatus.Installed, ExecutablePath = "/usr/bin/cc" }], null, null, DateTimeOffset.UtcNow);
        var a = NativeCapabilities.Evaluate(p, report);
        Assert.Equal(SupportLevel.MissingToolchain, a.Support);
        Assert.Contains(a.Remediation, r => r.Contains("CMake 3.20+ is missing") && r.Contains("sudo apt install cmake"));
        Assert.Contains(a.Remediation, r => r.Contains("C++ compiler is missing"));
        Assert.Contains(a.Remediation, r => r.Contains("Neither Ninja nor make"));
    }

    // ------------------------------------------------------------------ planning and validation

    private static BuildSettings Settings(HostEnvironment host, NativeBuildOptions o, string output, params PackagingFormat[] formats) => new()
    {
        ProjectRelativePath = o.ProjectDirectory.Length == 0 ? "CMakeLists.txt" : o.ProjectDirectory + "/CMakeLists.txt",
        Platform = host.Platform, Architecture = host.Architecture, OutputDirectory = output,
        Identity = new AppIdentity { ApplicationName = o.Target, Version = "1.0.0", ApplicationId = "com.example." + o.Target.Replace('/', '-') },
        PackagingFormats = formats.Length == 0 ? [PackagingFormat.PortableFolder, PackagingFormat.PortableZip] : formats,
        Native = o, Timeout = TimeSpan.FromMinutes(10),
    };

    [Theory]
    [InlineData("platform", "would need a Windows target toolchain")]
    [InlineData("arch", "needs a cross toolchain")]
    [InlineData("cache", "is not a CMake cache entry")]
    [InlineData("makevar", "is not a make variable")]
    [InlineData("target", "is not a valid target name")]
    [InlineData("output", "must be a path inside the project folder")]
    [InlineData("no-output", "Enter the file the make target produces")]
    [InlineData("standard", "C standard must be one of")]
    public void Invalid_settings_are_refused_before_anything_runs(string variant, string message)
    {
        var host = new HostEnvironment(TargetPlatform.Linux, TargetArchitecture.X64, "Linux", ".NET", "linux");
        CopyDir(Fixture("MakeHello"), _t.Combine("src", "make"));
        CopyDir(Fixture("CHello"), _t.Combine("src", "c"));
        CopyDir(Fixture("LooseSources"), _t.Combine("src", "loose"));
        var make = new NativeBuildOptions { ProjectDirectory = "make", Kind = NativeProjectKind.Make, Target = "all", ExpectedOutput = "bin/makehello" };
        var cmake = new NativeBuildOptions { ProjectDirectory = "c", Kind = NativeProjectKind.CMake, Target = "chello" };
        var settings = variant switch
        {
            "platform" => Settings(host, cmake, "/o") with { Platform = TargetPlatform.Windows },
            "arch" => Settings(host, cmake, "/o") with { Architecture = TargetArchitecture.Arm64 },
            "cache" => Settings(host, cmake with { CacheEntries = ["-G Xcode"] }, "/o"),
            "makevar" => Settings(host, make with { MakeVariables = ["CC=cc; rm -rf ~", "--eval=x"] }, "/o"),
            "target" => Settings(host, make with { Target = "-j1000" }, "/o"),
            "output" => Settings(host, make with { ExpectedOutput = "../../etc/passwd" }, "/o"),
            "no-output" => Settings(host, make with { ExpectedOutput = null }, "/o"),
            _ => Settings(host, new NativeBuildOptions { ProjectDirectory = "loose", Kind = NativeProjectKind.Sources, Target = "loose", Sources = ["main.c"], Language = "C", Standard = "20" }, "/o"),
        };
        var ex = Assert.Throws<BuildValidationException>(() => NativeBuildPlanner.Validate(settings, host, _t.Combine("src")));
        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public void Configure_arguments_respect_presets_block_downloads_by_default_and_are_never_a_shell_string()
    {
        var tools = new NativeBuildPlanner.Tools("/usr/bin/cmake", "/usr/bin/make", true, false, TargetPlatform.Linux);
        var o = new NativeBuildOptions { ProjectDirectory = "", Kind = NativeProjectKind.CMake, Target = "app", CacheEntries = ["ENABLE_X:BOOL=ON", "NAME=with spaces; rm -rf /"] };
        var args = NativeBuildPlanner.ConfigureArguments("/src dir", "/build dir", o, tools, TargetArchitecture.X64);
        Assert.Equal(["-S", "/src dir", "-B", "/build dir", "-G", "Ninja", "-DCMAKE_BUILD_TYPE=Release", "-DFETCHCONTENT_FULLY_DISCONNECTED=ON", "-DENABLE_X:BOOL=ON", "-DNAME=with spaces; rm -rf /"], args);

        var preset = NativeBuildPlanner.ConfigureArguments("/s", "/b", o with { Preset = "release", AllowDownloads = true, CacheEntries = [] }, tools, TargetArchitecture.X64);
        Assert.Equal(["-S", "/s", "-B", "/b", "--preset", "release"], preset); // the preset decides generator and build type; HCG's -B keeps output outside the source

        var noNinja = tools with { HasNinja = false };
        Assert.Equal("Unix Makefiles", NativeBuildPlanner.ChooseGenerator(noNinja, null));
        var mac = new NativeBuildPlanner.Tools("/opt/homebrew/bin/cmake", "/usr/bin/make", false, false, TargetPlatform.MacOS);
        Assert.Contains("-DCMAKE_OSX_ARCHITECTURES=arm64;x86_64", NativeBuildPlanner.ConfigureArguments("/s", "/b", o, mac, TargetArchitecture.Universal));
        var windows = new NativeBuildPlanner.Tools("C:/cmake.exe", null, false, true, TargetPlatform.Windows);
        Assert.Contains("-A", NativeBuildPlanner.ConfigureArguments("/s", "/b", o, windows, TargetArchitecture.X64)); // Visual Studio generator, x64 platform
    }

    [Fact]
    public void The_generated_configuration_compiles_exactly_the_selected_sources()
    {
        var o = new NativeBuildOptions { ProjectDirectory = "loose", Kind = NativeProjectKind.Sources, Target = "loose", Sources = ["main.c", "util.c"], Language = "C", Standard = "11" };
        var text = NativeBuildPlanner.GeneratedCMakeLists(o, "/work/my project/loose");
        Assert.Contains("project(hcg_sources LANGUAGES C)", text);
        Assert.Contains("set(CMAKE_C_STANDARD 11)", text);
        Assert.Contains("\"/work/my project/loose/main.c\"", text);
        Assert.Contains("\"/work/my project/loose/util.c\"", text);
        Assert.DoesNotContain("scratch.c", text);
    }

    [Fact]
    public void Compiler_linker_make_and_cmake_diagnostics_are_parsed()
    {
        var p = new NativeOutputParser();
        var gcc = p.Parse("/w/src dir/main.c:5:5: error: expected ';' before 'return'")!;
        Assert.Equal((DiagnosticSeverity.Error, "/w/src dir/main.c", 5, "expected ';' before 'return'"), (gcc.Severity, gcc.File, gcc.Line, gcc.Text));
        Assert.Equal(DiagnosticSeverity.Warning, p.Parse("util.cpp:3:1: warning: unused variable 'x'")!.Severity);
        var msvc = p.Parse(@"C:\src\app.cpp(12): error C2065: 'foo': undeclared identifier")!;
        Assert.Equal(("C2065", 12), (msvc.Code, msvc.Line));
        Assert.Equal("linker", p.Parse("/usr/bin/ld: main.o: in function `main': undefined reference to `missing'")!.Code);
        Assert.Equal("make", p.Parse("make: *** [Makefile:9: bin/makehello] Error 1")!.Code);
        Assert.Equal("CMake", p.Parse("CMake Error at CMakeLists.txt:3 (add_executable):")!.Code);
        Assert.Null(p.Parse("[2/3] Building C object CMakeFiles/app.dir/main.c.o"));
    }

    [Fact]
    public void Libraries_archives_and_scripts_are_not_accepted_as_executables()
    {
        var archive = _t.Write("libx.a", "!<arch>\nrest");
        var script = _t.Write("run.sh", "#!/bin/sh\necho hi\n");
        Assert.Equal(NativeImageKind.Library, NativeBinaryVerifier.ImageKind(archive).Kind);
        Assert.Equal(NativeImageKind.Unknown, NativeBinaryVerifier.ImageKind(script).Kind);
        Assert.Contains(NativeBinaryVerifier.VerifyExecutable(script, TargetPlatform.Linux, TargetArchitecture.X64), c => c.Name == "Executable image" && c.Status == VerificationStatus.Failed);
        Assert.Equal("/b/My App.app", CMakeFileApi.BundleOf("/b/My App.app/Contents/MacOS/My App"));
        Assert.Null(CMakeFileApi.BundleOf("/b/bin/tool"));
    }

    // ------------------------------------------------------------------ real builds

    private sealed class Observer : IBuildObserver
    {
        public List<string> Stages { get; } = [];
        public List<BuildLogLine> Lines { get; } = [];
        public Action<BuildStage>? OnStageEntered { get; init; }
        public void OnStage(BuildStage stage, string description) { lock (Stages) Stages.Add($"{stage}: {description}"); OnStageEntered?.Invoke(stage); }
        public void OnLog(BuildLogLine line) { lock (Lines) Lines.Add(line); }
    }

    private sealed class FixedToolchains(ToolchainReport report) : IToolchainService
    {
        public ToolchainReport? Latest => report;
        public event EventHandler<ToolchainReport>? Updated { add { } remove { } }
        public Task<ToolchainReport> GetAsync(bool refresh = false, CancellationToken cancellationToken = default) => Task.FromResult(report);
    }

    private sealed record Harness(AppPaths Paths, ProjectRecord Project, BuildService Service, NativeConfigurator Configurator, ToolchainReport Report);

    /// <summary>A workspace whose source is <paramref name="fixtures"/> copied below "source/projects with spaces/".</summary>
    private async Task<Harness> CreateAsync(params (string Fixture, string Folder)[] fixtures)
    {
        var report = await Report.Value;
        var paths = new AppPaths(_t.Combine("hcg data"));
        paths.EnsureCreated();
        var db = new SqliteDatabase(paths);
        var ws = Path.Combine(paths.WorkspacesDirectory, Guid.NewGuid().ToString("N"));
        foreach (var (fixture, folder) in fixtures) CopyDir(Fixture(fixture), Path.Combine(ws, "source", folder));
        var project = new ProjectRecord { Id = Guid.NewGuid(), Name = "Native", SourceKind = ProjectSourceKind.Directory, SourceLocation = "/orig", WorkspacePath = ws, ImportedAt = DateTimeOffset.UtcNow };
        var native = Scan(project.SourceDirectory);
        project.Analysis = NativeProjectInspector.Build(native, report, DateTimeOffset.UtcNow);
        await new SqliteProjectRepository(db).UpsertAsync(project);
        var worker = new LocalProcessBuildWorker(new HCG.ExecutableMaker.Infrastructure.Processes.ProcessRunner());
        var service = new BuildService(worker, new FixedToolchains(report), new SqliteBuildRepository(db),
            new PackagingRegistry([new PortableFolderPackager(), new PortableZipPackager()]), paths, TimeProvider.System);
        return new Harness(paths, project, service, new NativeConfigurator(worker, new FixedToolchains(report), paths), report);
    }

    private async Task<BuildRecord> BuildAsync(Harness h, NativeBuildOptions o, Observer? observer = null, CancellationToken ct = default)
    {
        var settings = Settings(h.Report.Host, o, _t.Combine("output folder"));
        var build = await h.Service.RunAsync(new BuildRequest { Project = h.Project, Settings = settings, TrustAcknowledged = true }, observer ?? new Observer(), ct);
        _out.WriteLine($"{o.Target}: {build.Status} {build.FailureReason}");
        foreach (var m in build.Messages) _out.WriteLine($"  {m.Severity} {m.Code} {m.File}:{m.Line} {m.Text}");
        if (build.LogPath is { } log && File.Exists(log)) foreach (var line in File.ReadLines(log).TakeLast(40)) _out.WriteLine("  | " + line);
        return build;
    }

    private static string RunProduced(string exe)
    {
        // The TEST runs the produced program to prove it is real; HCG itself never runs or installs build outputs.
        using var p = Process.Start(new ProcessStartInfo(exe) { RedirectStandardOutput = true, UseShellExecute = false })!;
        var text = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return text.Trim();
    }

    [NativeToolsFact]
    public async Task A_cmake_c_executable_is_configured_built_verified_and_packaged_in_paths_with_spaces()
    {
        var h = await CreateAsync(("CHello", "projects with spaces/C Hello"));
        var before = Directory.EnumerateFiles(h.Project.SourceDirectory, "*", SearchOption.AllDirectories).Order().ToList();
        var manifest = "projects with spaces/C Hello/CMakeLists.txt";

        // Configuration runs project code: refused without trust.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Configurator.ConfigureAsync(h.Project, manifest, new NativeBuildOptions { ProjectDirectory = "projects with spaces/C Hello", Kind = NativeProjectKind.CMake, Target = "?" }, false, null, CancellationToken.None));
        var configured = await h.Configurator.ConfigureAsync(h.Project, manifest,
            new NativeBuildOptions { ProjectDirectory = "projects with spaces/C Hello", Kind = NativeProjectKind.CMake, Target = "?" }, true, _out.WriteLine, CancellationToken.None);
        Assert.True(configured.Succeeded, configured.Error);
        var exe = Assert.Single(configured.Project!.Configured!.Executables);
        Assert.Equal("chello", exe.Name);
        Assert.Equal(["C"], exe.Languages);
        Assert.Equal("11", configured.Project.Configured.CacheHighlights.GetValueOrDefault("CMAKE_C_STANDARD") ?? "11");
        Assert.Contains(configured.Project.Configured.Compilers, c => c.Language == "C");
        var analysis = NativeProjectInspector.WithProject(h.Project.Analysis!, configured.Project, h.Report, DateTimeOffset.UtcNow);
        Assert.Equal("projects with spaces/C Hello/CMakeLists.txt#chello", Assert.Single(analysis.Projects).RelativePath);

        var observer = new Observer();
        var build = await BuildAsync(h, new NativeBuildOptions { ProjectDirectory = "projects with spaces/C Hello", Kind = NativeProjectKind.CMake, Target = "chello" }, observer);
        Assert.Equal(BuildStatus.Succeeded, build.Status);
        Assert.Contains(observer.Stages, s => s.StartsWith("Restoring: Configure with CMake", StringComparison.Ordinal));
        Assert.Contains(observer.Stages, s => s.StartsWith("Compiling: Build target chello (Release)", StringComparison.Ordinal));
        Assert.Contains(observer.Stages, s => s.StartsWith("VerifyingOutput", StringComparison.Ordinal));
        var folder = Assert.Single(build.Artifacts, a => a.Format == PackagingFormat.PortableFolder);
        Assert.NotEqual(VerificationStatus.Failed, folder.Verification); // "launch not performed" keeps it at passed-with-warnings: HCG never runs the program
        Assert.Contains(folder.Checks, c => c.Name == "Executable format" && c.Status == VerificationStatus.Passed);
        Assert.Contains(folder.Checks, c => c.Name == "Executable permission" && c.Status == VerificationStatus.Passed);
        Assert.Equal(VerificationStatus.Passed, Assert.Single(build.Artifacts, a => a.Format == PackagingFormat.PortableZip).Verification);
        Assert.True(File.Exists(Path.Combine(folder.Path, "RUNTIME-DEPENDENCIES.txt")));
        Assert.Contains(OperatingSystem.IsMacOS() ? "libSystem" : "libc.so", File.ReadAllText(Path.Combine(folder.Path, "RUNTIME-DEPENDENCIES.txt")));
        Assert.Equal(["chello", "RUNTIME-DEPENDENCIES.txt"], Directory.GetFiles(folder.Path).Select(Path.GetFileName).Order());
        Assert.StartsWith("Hello from C (2011", RunProduced(Path.Combine(folder.Path, "chello")));

        // Nothing was written into the imported source; the build tree was removed.
        Assert.Equal(before, Directory.EnumerateFiles(h.Project.SourceDirectory, "*", SearchOption.AllDirectories).Order().ToList());
        Assert.False(Directory.Exists(Path.Combine(h.Paths.BuildsDirectory, build.Id.ToString("N"), "native")));
    }

    [NativeToolsFact]
    public async Task A_cpp_executable_respects_the_projects_language_standard()
    {
        var h = await CreateAsync(("CppHello", "cpp"));
        var build = await BuildAsync(h, new NativeBuildOptions { ProjectDirectory = "cpp", Kind = NativeProjectKind.CMake, Target = "cpphello", BuildType = "Debug" });
        Assert.Equal(BuildStatus.Succeeded, build.Status);
        var folder = build.Artifacts.Single(a => a.Format == PackagingFormat.PortableFolder).Path;
        Assert.Equal("Hello from C++17", RunProduced(Path.Combine(folder, "cpphello")));
        Assert.Contains(OperatingSystem.IsMacOS() ? "libc++" : "libstdc++", File.ReadAllText(Path.Combine(folder, "RUNTIME-DEPENDENCIES.txt")));
    }

    [NativeToolsFact]
    public async Task Multiple_executable_targets_are_listed_separately_from_libraries_and_project_shared_libraries_are_bundled_with_a_portability_warning()
    {
        var h = await CreateAsync(("MultiTarget", "multi"));
        var configured = await h.Configurator.ConfigureAsync(h.Project, "multi/CMakeLists.txt",
            new NativeBuildOptions { ProjectDirectory = "multi", Kind = NativeProjectKind.CMake, Target = "?" }, true, null, CancellationToken.None);
        Assert.True(configured.Succeeded, configured.Error);
        var c = configured.Project!.Configured!;
        Assert.Equal(["calc", "greeter"], c.Executables.Select(t => t.Name));
        Assert.Equal(["dep", "greet", "mathlib"], c.Libraries.Select(t => t.Name).Order());
        Assert.Equal(NativeTargetType.SharedLibrary, c.Targets.Single(t => t.Name == "greet").Type);
        Assert.Contains("greet", c.Targets.Single(t => t.Name == "greeter").Dependencies);
        Assert.Equal("third_party/dep", c.Targets.Single(t => t.Name == "dep").SourceDirectory);

        var calc = await BuildAsync(h, new NativeBuildOptions { ProjectDirectory = "multi", Kind = NativeProjectKind.CMake, Target = "calc" });
        Assert.Equal(BuildStatus.Succeeded, calc.Status);
        Assert.Equal("2 + 3 = 5 (dep 42)", RunProduced(Path.Combine(calc.Artifacts.Single(a => a.Format == PackagingFormat.PortableFolder).Path, "calc")));

        var greeter = await BuildAsync(h, new NativeBuildOptions { ProjectDirectory = "multi", Kind = NativeProjectKind.CMake, Target = "greeter" });
        Assert.Equal(BuildStatus.Succeeded, greeter.Status);
        var folder = greeter.Artifacts.Single(a => a.Format == PackagingFormat.PortableFolder).Path;
        var lib = OperatingSystem.IsMacOS() ? "libgreet.dylib" : "libgreet.so";
        Assert.True(File.Exists(Path.Combine(folder, lib)));   // the project's own shared library is shipped next to it…
        var warning = Assert.Single(greeter.Messages, m => m.Code == "HCG-RUNTIME");      // …and the report says it is not found from there without an executable-relative rpath
        Assert.Contains(OperatingSystem.IsMacOS() ? "no @executable_path/@loader_path rpath" : "no $ORIGIN in RUNPATH", warning.Text);
        Assert.Contains(lib, warning.Text);
    }

    [NativeToolsFact]
    public async Task A_library_only_project_offers_no_application_and_a_library_target_is_refused()
    {
        var h = await CreateAsync(("LibOnly", "lib"));
        var configured = await h.Configurator.ConfigureAsync(h.Project, "lib/CMakeLists.txt",
            new NativeBuildOptions { ProjectDirectory = "lib", Kind = NativeProjectKind.CMake, Target = "?" }, true, null, CancellationToken.None);
        Assert.True(configured.Succeeded, configured.Error);
        Assert.Empty(configured.Project!.Configured!.Executables);
        var analysis = NativeProjectInspector.WithProject(h.Project.Analysis!, configured.Project, h.Report, DateTimeOffset.UtcNow);
        Assert.DoesNotContain(analysis.Projects, d => d.IsRunnableApplication);
        Assert.Equal(SupportLevel.RequiresConfiguration, analysis.Support);
        Assert.Contains("only libraries (onlylib), which are not runnable applications", analysis.SupportSummary);

        var build = await BuildAsync(h, new NativeBuildOptions { ProjectDirectory = "lib", Kind = NativeProjectKind.CMake, Target = "onlylib" });
        Assert.Equal(BuildStatus.Failed, build.Status);
        Assert.Contains("onlylib is a StaticLibrary target, not an executable", build.FailureReason);
        Assert.Empty(build.Artifacts);
    }

    [NativeToolsFact]
    public async Task A_configured_make_target_builds_in_a_copy_and_its_expected_output_is_verified()
    {
        var h = await CreateAsync(("MakeHello", "make app"));
        var build = await BuildAsync(h, new NativeBuildOptions
        {
            ProjectDirectory = "make app", Kind = NativeProjectKind.Make, Target = "all", ExpectedOutput = "bin/makehello", MakeVariables = ["GREETING=configured value"],
        });
        Assert.Equal(BuildStatus.Succeeded, build.Status);
        var folder = build.Artifacts.Single(a => a.Format == PackagingFormat.PortableFolder).Path;
        Assert.Equal("Hello from make: configured value", RunProduced(Path.Combine(folder, "makehello")));
        Assert.False(Directory.Exists(Path.Combine(h.Project.SourceDirectory, "make app", "bin"))); // make ran in HCG's copy, not in the source
    }

    [NativeToolsFact]
    public async Task A_make_target_that_does_not_produce_the_expected_output_fails_precisely()
    {
        var h = await CreateAsync(("MakeHello", "make"));
        var build = await BuildAsync(h, new NativeBuildOptions { ProjectDirectory = "make", Kind = NativeProjectKind.Make, Target = "all", ExpectedOutput = "build/hello" });
        Assert.Equal(BuildStatus.Failed, build.Status);
        Assert.Equal("make all succeeded, but the expected output build/hello was not produced. Check the target and the output path (relative to the project folder).", build.FailureReason);
        Assert.Empty(build.Artifacts);
    }

    [NativeToolsFact]
    public async Task A_compilation_error_fails_the_build_with_the_compilers_message_and_location()
    {
        var h = await CreateAsync(("BrokenC", "broken"));
        var build = await BuildAsync(h, new NativeBuildOptions { ProjectDirectory = "broken", Kind = NativeProjectKind.CMake, Target = "broken" });
        Assert.Equal(BuildStatus.Failed, build.Status);
        Assert.StartsWith("Build target broken (Release) failed with exit code", build.FailureReason);
        var error = Assert.Single(build.Messages, m => m.Severity == DiagnosticSeverity.Error && m.File?.EndsWith("main.c", StringComparison.Ordinal) == true && m.Line == 4);
        Assert.Contains("expected", error.Text);
        Assert.Empty(build.Artifacts);
    }

    [NativeToolsFact]
    public async Task Cancelling_stops_the_running_make_and_its_children()
    {
        var h = await CreateAsync(("MakeHello", "make"));
        using var cts = new CancellationTokenSource();
        var observer = new Observer { OnStageEntered = s => { if (s == BuildStage.Compiling) cts.CancelAfter(TimeSpan.FromSeconds(2)); } };
        var sw = Stopwatch.StartNew();
        var build = await BuildAsync(h, new NativeBuildOptions { ProjectDirectory = "make", Kind = NativeProjectKind.Make, Target = "slow", ExpectedOutput = "x" }, observer, cts.Token);
        sw.Stop();
        Assert.Equal(BuildStatus.Cancelled, build.Status);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(40), $"took {sw.Elapsed}"); // the 60-second sleep was killed with make
        Assert.Empty(build.Artifacts);
    }

    [NativeToolsFact]
    public async Task Loose_sources_build_only_the_selected_files_with_a_generated_configuration()
    {
        var h = await CreateAsync(("LooseSources", "loose"));
        var observer = new Observer();
        var build = await BuildAsync(h, new NativeBuildOptions
        {
            ProjectDirectory = "loose", Kind = NativeProjectKind.Sources, Target = "loose", Sources = ["main.c", "util.c"], Language = "C", Standard = "17",
        }, observer);
        Assert.Equal(BuildStatus.Succeeded, build.Status);
        Assert.Contains(observer.Lines, l => l.Text.Contains("Generated build configuration") && l.Text.Contains("set(CMAKE_C_STANDARD 17)"));
        Assert.Equal("Loose sources: 42", RunProduced(Path.Combine(build.Artifacts.Single(a => a.Format == PackagingFormat.PortableFolder).Path, "loose")));
        Assert.False(File.Exists(Path.Combine(h.Project.SourceDirectory, "loose", "CMakeLists.txt"))); // generated in HCG's build folder only
    }
}
