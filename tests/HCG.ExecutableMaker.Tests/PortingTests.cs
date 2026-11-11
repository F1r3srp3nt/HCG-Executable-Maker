using System.Security.Cryptography;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Porting;
using HCG.ExecutableMaker.Porting.Android;
using HCG.ExecutableMaker.Porting.Apple;
using HCG.ExecutableMaker.ProjectInspection;
using HCG.ExecutableMaker.ProjectInspection.Mobile;

namespace HCG.ExecutableMaker.Tests;

/// <summary>Source-based porting: macOS → iOS and Linux → Android assessments, generation and reporting.</summary>
public class PortingTests : IDisposable
{
    private readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    private static string Fixture(string name) => Path.Combine(Fixtures.RepoRoot, "tests", "Fixtures", name);

    private static string TreeHash(string dir) => Convert.ToHexString(SHA256.HashData(
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .SelectMany(f => System.Text.Encoding.UTF8.GetBytes(Path.GetRelativePath(dir, f)).Concat(File.ReadAllBytes(f))).ToArray()));

    private static (PortingAssessment Assessment, SourceIndex Index) Assess(IPorter porter, string root, string key)
    {
        var index = SourceIndex.Build(root);
        var source = porter.FindSources(index).Single(s => s.Key == key);
        return (porter.Assess(index, source, null), index);
    }

    // ───────────── Diff ─────────────

    [Fact]
    public void Unified_diff_shows_hunks_with_context()
    {
        var a = string.Join('\n', Enumerable.Range(1, 20).Select(i => "line " + i)) + "\n";
        var b = a.Replace("line 10\n", "line 10\n#if os(macOS)\n", StringComparison.Ordinal).Replace("line 12\n", "line 12 changed\n", StringComparison.Ordinal);
        var diff = TextDiff.Unified(a, b, "f.swift", "f.swift");
        Assert.Contains("--- a/f.swift", diff, StringComparison.Ordinal);
        Assert.Contains("+#if os(macOS)", diff, StringComparison.Ordinal);
        Assert.Contains("-line 12", diff, StringComparison.Ordinal);
        Assert.Contains("+line 12 changed", diff, StringComparison.Ordinal);
        Assert.Contains("@@ -8,8 +8,9 @@", diff, StringComparison.Ordinal);
        Assert.DoesNotContain("line 1\n", diff, StringComparison.Ordinal);
        Assert.Equal("", TextDiff.Unified(a, a, "f", "f"));
        Assert.StartsWith("--- /dev/null", TextDiff.Unified(null, "new\n", "n", "n"), StringComparison.Ordinal);
    }

    // ───────────── Swift analysis ─────────────

    [Fact]
    public void Swift_conditions_comments_and_strings_are_understood()
    {
        var src = new SwiftSource("A.swift", """
            import SwiftUI
            // NSWindow in a comment
            let s = "NSOpenPanel in a string \(NSWorkspace.shared)"
            #if os(macOS)
            let w = NSWindow()
            #elseif canImport(UIKit)
            let v = UIView()
            #endif
            #if !os(iOS)
            let x = 1
            #endif
            """);
        Assert.True(src.ActiveOnIOS[0]);
        Assert.False(src.ActiveOnIOS[4]);
        Assert.True(src.ActiveOnMacOS[4]);
        Assert.True(src.ActiveOnIOS[6]);
        Assert.False(src.ActiveOnMacOS[6]);
        Assert.False(src.ActiveOnIOS[9]);
        Assert.DoesNotContain("NSWindow", src.Code[1], StringComparison.Ordinal);
        Assert.DoesNotContain("NSOpenPanel", src.Code[2], StringComparison.Ordinal);
        Assert.Contains("NSWorkspace", src.Code[2], StringComparison.Ordinal); // interpolated code is still code

        var plan = MacToIosRules.Analyze(src, isAppTarget: false);
        var blocker = Assert.Single(plan.Findings, f => f.Disposition == FindingDisposition.Blocker);
        Assert.Equal(3, blocker.Line);
        Assert.Contains("NSWorkspace", blocker.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void Supported_adaptations_keep_macOS_code_and_add_iOS_equivalents()
    {
        var plan = MacToIosRules.Analyze(new SwiftSource("ContentView.swift", File.ReadAllText(Path.Combine(Fixture("HelloMac"), "HelloMac", "ContentView.swift"))), false);
        Assert.DoesNotContain(plan.Findings, f => f.Disposition == FindingDisposition.Blocker);
        var text = plan.Apply(draft: false);
        Assert.Contains("#if canImport(AppKit)\nimport AppKit\n#elseif canImport(UIKit)\nimport UIKit\n#endif", text, StringComparison.Ordinal);
        Assert.Contains("NSPasteboard.general.setString(\"\\(count)\", forType: .string)", text, StringComparison.Ordinal);
        Assert.Contains("UIPasteboard.general.string = \"\\(count)\"", text, StringComparison.Ordinal);

        var app = MacToIosRules.Analyze(new SwiftSource("App.swift", File.ReadAllText(Path.Combine(Fixture("HelloMac"), "HelloMac", "HelloMacApp.swift"))), false);
        Assert.Contains(app.Findings, f => f.Disposition == FindingDisposition.NeedsReview && f.Title.Contains("Settings", StringComparison.Ordinal));
        Assert.Contains("#if os(macOS)\n        Settings {", app.Apply(false), StringComparison.Ordinal);
    }

    [Fact]
    public void Activation_delegate_is_adapted_but_real_lifecycle_code_is_a_blocker()
    {
        const string shim = """
            import AppKit
            import SwiftUI
            final class D: NSObject, NSApplicationDelegate {
                func applicationDidFinishLaunching(_ notification: Notification) {
                    NSApp.setActivationPolicy(.regular)
                    NSApp.activate()
                }
                func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }
            }
            @main
            struct A: App {
                @NSApplicationDelegateAdaptor(D.self) private var delegate
                var body: some Scene {
                    WindowGroup { Text("x") }
                    .windowStyle(.hiddenTitleBar)
                }
            }
            """;
        var plan = MacToIosRules.Analyze(new SwiftSource("A.swift", shim), true);
        Assert.DoesNotContain(plan.Findings, f => f.Disposition == FindingDisposition.Blocker);
        var text = plan.Apply(false);
        Assert.Contains("#if os(macOS)\nfinal class D", text, StringComparison.Ordinal);
        Assert.Contains("    #if os(macOS)\n    @NSApplicationDelegateAdaptor", text, StringComparison.Ordinal);
        Assert.Contains("        #if os(macOS)\n        .windowStyle(.hiddenTitleBar)\n        #endif", text, StringComparison.Ordinal);

        var real = shim.Replace("NSApp.activate()", "NSApp.activate()\n        NSApp.mainMenu = nil", StringComparison.Ordinal);
        var blocked = MacToIosRules.Analyze(new SwiftSource("A.swift", real), true);
        Assert.Contains(blocked.Findings, f => f.Disposition == FindingDisposition.Blocker && f.Title.StartsWith("NSApplicationDelegate", StringComparison.Ordinal));
    }

    [Fact]
    public void Draft_marks_each_blocker_with_an_explicit_compile_error()
    {
        var plan = MacToIosRules.Analyze(new SwiftSource("M.swift", "import Foundation\nfunc export() {\n    let panel = NSSavePanel()\n}\n"), false);
        var text = plan.Apply(draft: true);
        Assert.Contains("    #if os(iOS)\n    #error(\"HCG port blocker: NSSavePanel is macOS-only", text, StringComparison.Ordinal);
        Assert.Contains("let panel = NSSavePanel()", text, StringComparison.Ordinal); // never replaced or stubbed
    }

    [Theory]
    [InlineData("let package = Package(\n    name: \"X\",\n    platforms: [.macOS(.v14)],\n    targets: []\n)", "platforms: [.macOS(.v14), .iOS(.v17)]", false)]
    [InlineData("let package = Package(name: \"X\", targets: [])", "name: \"X\",\n    platforms: [.iOS(.v17)],", false)]
    [InlineData("let package = Package(name: \"X\", platforms: [.macOS(.v14), .iOS(.v16)])", ".iOS(.v16)", true)]
    public void Package_manifest_gains_an_iOS_platform(string manifest, string expected, bool alreadyPresent)
    {
        var result = new SwiftManifestText(manifest).WithIOSPlatform(17, out var present);
        Assert.Equal(alreadyPresent, present);
        Assert.Contains(expected, result, StringComparison.Ordinal);
    }

    [Fact]
    public void Static_manifest_reading_finds_targets_dependencies_and_products()
    {
        var m = new SwiftManifestText(File.ReadAllText(Path.Combine(Fixture("TallyPackage"), "Package.swift")));
        var app = m.FindTarget("TallyApp")!;
        Assert.Equal("executableTarget", app.Kind);
        Assert.Equal(["TallyCore"], app.Dependencies.Select(d => d.Name).ToArray());
        Assert.Equal("TallyCore", m.LibraryFor("TallyCore")!.Name);
    }

    // ───────────── macOS → iOS end to end (generation) ─────────────

    [Fact]
    public void Swift_package_app_becomes_an_iOS_Xcode_project_in_a_separate_copy()
    {
        var root = Fixture("TallyPackage");
        var before = TreeHash(root);
        var porter = new MacToIosPorter();
        var (a, index) = Assess(porter, root, "swiftpm::TallyApp");
        Assert.Equal(PortingCapability.GenerateTarget, a.Capability);
        Assert.Equal(["NSMicrophoneUsageDescription"], a.RequiredTexts);
        Assert.Equal("Tally", a.SuggestedAppName);

        var options = new PortOptions { AppName = "Tally", AppId = "com.hakmicapital.tally" };
        Assert.Throws<InvalidOperationException>(() => porter.Generate(index, a, options, new PortOutput(root, _t.Combine("x"))));
        var output = new PortOutput(root, _t.Combine("port"));
        var (steps, buildRoot) = porter.Generate(index, a, options with { UsageDescriptions = new Dictionary<string, string> { ["NSMicrophoneUsageDescription"] = "Checks the microphone." } }, output);
        Assert.Equal(before, TreeHash(root)); // the original is untouched
        Assert.Equal("iOS", buildRoot);
        Assert.NotEmpty(steps);

        var outIndex = SourceIndex.Build(_t.Combine("port"));
        var project = Assert.Single(XcodeProjectAnalyzer.FindProjects(outIndex));
        Assert.Equal("iOS/Tally.xcodeproj", project);
        Assert.False(XcodeProjectAnalyzer.IsMacOnlyProject(outIndex, project));
        var info = XcodeProjectAnalyzer.Analyze(outIndex, project, out _);
        Assert.Equal("Tally", Assert.Single(info.Schemes).Name);
        var pbx = File.ReadAllText(_t.Combine("port", "iOS", "Tally.xcodeproj", "project.pbxproj"));
        Assert.Contains("relativePath = ..;", pbx, StringComparison.Ordinal);
        Assert.Contains("productName = TallyCore;", pbx, StringComparison.Ordinal);
        Assert.Contains("path = ../Sources/TallyApp;", pbx, StringComparison.Ordinal);
        Assert.Contains("INFOPLIST_KEY_NSMicrophoneUsageDescription = \"Checks the microphone.\";", pbx, StringComparison.Ordinal);
        Assert.Contains("PRODUCT_BUNDLE_IDENTIFIER = com.hakmicapital.tally;", pbx, StringComparison.Ordinal);
        Assert.Contains(".iOS(.v17)", File.ReadAllText(_t.Combine("port", "Package.swift")), StringComparison.Ordinal);
        var changes = output.Changes;
        Assert.Contains(changes, c => c.Kind == FileChangeKind.Modified && c.Path == "Sources/TallyApp/ContentView.swift" && c.Diff.Contains("+UIPasteboard", StringComparison.Ordinal) is false && c.Diff.Contains("UIPasteboard.general.string", StringComparison.Ordinal));
        Assert.Contains(changes, c => c.Kind == FileChangeKind.CopiedUnchanged && c.Path == "Sources/TallyCore/Tally.swift");
        Assert.Contains(changes, c => c.Kind == FileChangeKind.Added && c.Path.EndsWith("project.pbxproj", StringComparison.Ordinal));
    }

    [Fact]
    public void Mac_Xcode_project_is_retargeted_without_stray_lines()
    {
        var root = Fixture("HelloMac");
        var porter = new MacToIosPorter();
        var (a, index) = Assess(porter, root, "xcode:HelloMac.xcodeproj");
        Assert.Equal(PortingCapability.GenerateTarget, a.Capability);
        Assert.Equal("Hello HCG", a.SuggestedAppName);
        Assert.Equal("com.hakmicapital.hellomac", a.SuggestedAppId);
        porter.Generate(index, a, new PortOptions { AppName = "Hello HCG", AppId = "com.hakmicapital.hellomac" }, new PortOutput(root, _t.Combine("port")));
        var pbx = File.ReadAllText(_t.Combine("port", "HelloMac.xcodeproj", "project.pbxproj"));
        Assert.DoesNotContain("MACOSX_DEPLOYMENT_TARGET", pbx, StringComparison.Ordinal);
        Assert.DoesNotContain("ENABLE_APP_SANDBOX", pbx, StringComparison.Ordinal);
        Assert.DoesNotContain("CODE_SIGN_IDENTITY = \"-\"", pbx, StringComparison.Ordinal);
        Assert.Contains("SDKROOT = \"iphoneos\";", pbx, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"\n\s*\n\t\t\t\t", pbx);
        Assert.False(XcodeProjectAnalyzer.IsMacOnlyProject(SourceIndex.Build(_t.Combine("port")), "HelloMac.xcodeproj"));
        Assert.IsType<Dictionary<string, object>>(OpenStepPlist.Parse(pbx));
    }

    [Fact]
    public void Output_inside_the_original_project_is_refused()
    {
        var root = _t.Combine("src");
        Directory.CreateDirectory(root);
        Assert.Throws<InvalidOperationException>(() => new PortOutput(root, Path.Combine(root, "out")));
        Assert.Throws<InvalidOperationException>(() => new PortOutput(root, _t.Path));
    }

    // ───────────── Linux → Android ─────────────

    [Fact]
    public void CMake_project_is_read_statically()
    {
        var index = SourceIndex.Build(Fixture("LinuxPrimes"));
        Assert.Equal([""], CMakeProject.FindRoots(index).ToArray());
        var cmake = CMakeProject.Read(index, "");
        Assert.Equal("LinuxPrimes", cmake.ProjectName);
        var exe = Assert.Single(cmake.Targets, t => t.Executable);
        Assert.Equal("primes", exe.Name);
        Assert.Equal(["src/main.c", "src/report.c"], exe.Sources.ToArray());
        Assert.Contains("primecore", exe.LinkLibraries);
        Assert.Contains(cmake.Targets, t => t.Name == "primecore" && t.Directory == "core" && t.Sources.SequenceEqual(new[] { "core/sieve.c" }));
        Assert.Contains(cmake.Packages, p => p.Package == "Threads");
    }

    [Fact]
    public void C_preprocessor_conditions_are_evaluated_for_bionic()
    {
        var src = new CSource("a.c", "#ifdef __GLIBC__\n#include <gnu/libc-version.h>\n#endif\n#if defined(_WIN32)\n#include <windows.h>\n#elif defined(__linux__)\n#include <unistd.h>\n#endif\n#include <execinfo.h>\n");
        Assert.False(src.ActiveOnAndroid[1]);
        Assert.False(src.ActiveOnAndroid[4]);
        Assert.True(src.ActiveOnAndroid[6]);
        var plan = LinuxToAndroidRules.Analyze(src);
        var blocker = Assert.Single(plan.Findings, f => f.Disposition == FindingDisposition.Blocker);
        Assert.Equal(9, blocker.Line);
    }

    [Fact]
    public void Console_program_assessment_and_generation()
    {
        var root = Fixture("LinuxPrimes");
        var before = TreeHash(root);
        var porter = new LinuxToAndroidPorter();
        var (a, index) = Assess(porter, root, "cmake::primes");
        Assert.Equal(PortingCapability.GenerateTarget, a.Capability);
        Assert.Contains(a.Findings, f => f.Title == "Reads standard input" && f.Disposition == FindingDisposition.AutoAdapted);
        Assert.Contains(a.Findings, f => f.Title == "Calls exit()");
        Assert.Contains(a.Findings, f => f.Title.StartsWith("Absolute path \"/tmp\"", StringComparison.Ordinal) && f.Disposition == FindingDisposition.NeedsReview);
        Assert.DoesNotContain(a.Findings, f => f.Title.Contains("glibc", StringComparison.Ordinal)); // guarded by #ifdef __GLIBC__

        var output = new PortOutput(root, _t.Combine("port"));
        var (_, buildRoot) = porter.Generate(index, a, new PortOptions
        {
            AppName = "Primes", AppId = "com.hakmicapital.primes", DefaultArguments = "-n 100000 -l", NdkVersion = "27.2.12479018", CmakeVersion = "3.22.1",
        }, output);
        Assert.Equal(before, TreeHash(root));
        Assert.Equal("android", buildRoot);
        var gradle = File.ReadAllText(_t.Combine("port", "android", "app", "build.gradle.kts"));
        Assert.Contains("applicationId = \"com.hakmicapital.primes\"", gradle, StringComparison.Ordinal);
        Assert.Contains("ndkVersion = \"27.2.12479018\"", gradle, StringComparison.Ordinal);
        Assert.Contains("version = \"3.22.1\"", gradle, StringComparison.Ordinal);
        var cmake = File.ReadAllText(_t.Combine("port", "android", "app", "src", "main", "cpp", "CMakeLists.txt"));
        Assert.Contains("${CMAKE_CURRENT_SOURCE_DIR}/../../../../../program", cmake, StringComparison.Ordinal);
        Assert.Contains("get_target_property(HCG_SOURCES primes SOURCES)", cmake, StringComparison.Ordinal);
        Assert.Contains("find_package(Threads REQUIRED)", cmake, StringComparison.Ordinal);
        Assert.True(File.Exists(_t.Combine("port", "program", "CMakeLists.txt")));
        Assert.Contains("\"-n 100000 -l\"", File.ReadAllText(_t.Combine("port", "android", "app", "src", "main", "java", "hcg", "port", "console", "MainActivity.java")), StringComparison.Ordinal);
        foreach (var f in Directory.EnumerateFiles(_t.Combine("port", "android"), "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".jar", StringComparison.Ordinal)))
            Assert.DoesNotContain("{{", File.ReadAllText(f), StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
            Assert.True((File.GetUnixFileMode(_t.Combine("port", "android", "gradlew")) & UnixFileMode.UserExecute) != 0);
        var android = AndroidGradleAnalyzer.Analyze(SourceIndex.Build(_t.Combine("port")), "android", out _);
        var module = Assert.Single(android.AppModules);
        Assert.Equal(":app", module.GradlePath);
        Assert.Equal("com.hakmicapital.primes", module.ApplicationId);
        Assert.True(android.HasWrapper);
        Assert.Equal("8.7.3", android.AgpVersion);
    }

    [Fact]
    public void GUI_toolkit_program_is_blocked_with_specific_reasons()
    {
        var (a, _) = Assess(new LinuxToAndroidPorter(), Fixture("LinuxGtkHello"), "cmake::gtk-hello");
        Assert.Equal(PortingCapability.BlockedNeedsPorting, a.Capability);
        Assert.Contains(a.Blockers, f => f.Title == "GTK / Cairo / Pango" && f.Location == "main.c:1");
        Assert.Contains(a.Blockers, f => f.Title.Contains("gtk+-3.0", StringComparison.Ordinal));
        Assert.DoesNotContain(a.Findings, f => f.Title.Contains("PkgConfig", StringComparison.Ordinal));
    }

    [Fact]
    public void Existing_targets_and_out_of_scope_projects_are_distinguished()
    {
        var android = new LinuxToAndroidPorter();
        var (existing, _) = Assess(android, Fixture("HelloAndroid"), "existing-android:");
        Assert.Equal(PortingCapability.BuildExistingTarget, existing.Capability);

        _t.Write("tool/Makefile", "all:\n\tcc -o tool main.c\n");
        _t.Write("tool/main.c", "int main(void) { return 0; }\n");
        var index = SourceIndex.Build(_t.Path);
        var source = Assert.Single(android.FindSources(index));
        var a = android.Assess(index, source, null);
        Assert.Equal(PortingCapability.NotSupported, a.Capability);
        Assert.Contains("CMakeLists.txt", a.Summary, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => android.Generate(index, a, new PortOptions { AppName = "x", AppId = "com.x.y" }, new PortOutput(_t.Path, _t.Path + "-out")));
    }

    // ───────────── Reporting ─────────────

    [Fact]
    public void Report_lists_the_four_levels_separately()
    {
        var (a, _) = Assess(new LinuxToAndroidPorter(), Fixture("LinuxPrimes"), "cmake::primes");
        var report = new PortReport
        {
            Id = Guid.NewGuid(), SourceProject = "LinuxPrimes", SourceDirectory = "/src", OutputDirectory = _t.Combine("out"), BuildRoot = "android",
            Assessment = a, Plan = new MigrationPlan { Steps = ["s"], Changes = [] }, Options = new PortOptions { AppName = "P", AppId = "com.p.q" },
            CreatedAt = DateTimeOffset.UtcNow,
            Verification = [new VerificationEntry(VerificationLevel.Generated, LevelStatus.Passed, "ok")],
        };
        var md = PortingService.Markdown(report);
        Assert.Contains("| Generated | Yes |", md, StringComparison.Ordinal);
        Assert.Contains("| Compiled | Not performed |", md, StringComparison.Ordinal);
        Assert.Contains("| Launched in simulator/emulator | Not performed |", md, StringComparison.Ordinal);
        Assert.Contains("| Tested on a physical device | Not performed |", md, StringComparison.Ordinal);
        PortingService.Save(report);
        var loaded = PortingService.Load(report.OutputDirectory);
        Assert.Equal(report.Assessment.Capability, loaded.Assessment.Capability);
        Assert.Equal(LevelStatus.NotPerformed, loaded.Level(VerificationLevel.TestedOnDevice).Status);
        var updated = loaded.With(new VerificationEntry(VerificationLevel.Compiled, LevelStatus.Failed, "x"));
        Assert.Equal(LevelStatus.Failed, updated.Level(VerificationLevel.Compiled).Status);
        Assert.Equal(LevelStatus.Passed, updated.Level(VerificationLevel.Generated).Status);
    }

    [Fact]
    public void Simulator_choice_prefers_booted_then_newest_iPhone()
    {
        const string json = """
            {"devices": {
              "com.apple.CoreSimulator.SimRuntime.iOS-17-5": [ {"udid":"A","name":"iPhone 15","state":"Shutdown","isAvailable":true} ],
              "com.apple.CoreSimulator.SimRuntime.iOS-18-2": [ {"udid":"B","name":"iPhone 16","state":"Shutdown","isAvailable":true},
                                                              {"udid":"C","name":"iPad Air","state":"Booted","isAvailable":true} ],
              "com.apple.CoreSimulator.SimRuntime.watchOS-11-2": [ {"udid":"D","name":"Apple Watch","state":"Booted","isAvailable":true} ]
            }}
            """;
        Assert.Equal("B", DeviceLauncher.PickSimulator(json)!.Value.Udid);
        Assert.Equal("A", DeviceLauncher.PickSimulator(json.Replace("\"A\",\"name\":\"iPhone 15\",\"state\":\"Shutdown\"", "\"A\",\"name\":\"iPhone 15\",\"state\":\"Booted\"", StringComparison.Ordinal))!.Value.Udid);
        Assert.Null(DeviceLauncher.PickSimulator("{\"devices\":{}}"));
    }
}
