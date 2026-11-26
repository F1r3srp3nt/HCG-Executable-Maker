using System.Text.Json.Nodes;
using HCG.ExecutableMaker.BuildEngine;
using HCG.ExecutableMaker.BuildEngine.Distribution;
using HCG.ExecutableMaker.BuildEngine.MacXcode;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging;
using HCG.ExecutableMaker.Packaging.Distribution;
using HCG.ExecutableMaker.Packaging.MacDistribution;
using HCG.ExecutableMaker.ProjectInspection;
using HCG.ExecutableMaker.ProjectInspection.MacXcode;
using HCG.ExecutableMaker.ProjectInspection.Swift;
using Xunit;

namespace HCG.ExecutableMaker.Tests;

/// <summary>
/// The native macOS Xcode build provider for projects shaped like current SwiftUI apps:
/// <list type="bullet">
/// <item>an Xcode 16 project (objectVersion 77, synchronized folders) with a local Swift package under Packages/ and a
/// remote one;</item>
/// <item>Debug and Release that differ in bundle ID, App Sandbox and compilation conditions;</item>
/// <item>a shared scheme whose Test action runs the package's tests;</item>
/// <item>an asset catalog with an app icon, and a generated Info.plist.</item>
/// </list>
/// The fixture is synthetic (no product's source). With HCG_OFFENSIVE_FRONTIER_CHECKOUT, the real project is inspected too.
/// </summary>
public sealed class NativeMacXcodeProviderTests : IDisposable
{
    private readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    private const string Pbx = """
        // !$*UTF8*$!
        {
        	archiveVersion = 1;
        	objectVersion = 77;
        	objects = {
        		A1 = {isa = PBXBuildFile; productRef = A11; };
        		A3 = {isa = PBXFileReference; explicitFileType = wrapper.application; path = FieldKit.app; sourceTree = BUILT_PRODUCTS_DIR; };
        		A4 = {isa = PBXFileSystemSynchronizedRootGroup; path = FieldKit; sourceTree = "<group>"; };
        		A5 = {
        			isa = PBXNativeTarget;
        			buildConfigurationList = A6;
        			buildPhases = ( A7 );
        			fileSystemSynchronizedGroups = ( A4 );
        			name = FieldKit;
        			packageProductDependencies = ( A11, A13 );
        			productName = FieldKit;
        			productType = "com.apple.product-type.application";
        		};
        		A7 = {isa = PBXFrameworksBuildPhase; files = ( A1 ); };
        		A6 = {isa = XCConfigurationList; buildConfigurations = ( A8, A9 ); };
        		A8 = {isa = XCBuildConfiguration; name = Debug; buildSettings = {
        			ASSETCATALOG_COMPILER_APPICON_NAME = AppIcon; CODE_SIGN_IDENTITY = "-"; CODE_SIGN_STYLE = Manual; DEVELOPMENT_TEAM = "";
        			ENABLE_APP_SANDBOX = NO; ENABLE_HARDENED_RUNTIME = YES; GENERATE_INFOPLIST_FILE = YES;
        			SWIFT_ACTIVE_COMPILATION_CONDITIONS = "FIELDKIT_DEV $(inherited)"; MACOSX_DEPLOYMENT_TARGET = 14.0; MARKETING_VERSION = 0.3.0;
        			CURRENT_PROJECT_VERSION = 7; PRODUCT_BUNDLE_IDENTIFIER = "com.example.fieldkit.dev"; PRODUCT_NAME = "$(TARGET_NAME)"; SDKROOT = macosx; }; };
        		A9 = {isa = XCBuildConfiguration; name = Release; buildSettings = {
        			ASSETCATALOG_COMPILER_APPICON_NAME = AppIcon; CODE_SIGN_IDENTITY = "-"; CODE_SIGN_STYLE = Manual; DEVELOPMENT_TEAM = "";
        			ENABLE_APP_SANDBOX = YES; ENABLE_USER_SELECTED_FILES = readwrite; ENABLE_HARDENED_RUNTIME = YES; GENERATE_INFOPLIST_FILE = YES;
        			CODE_SIGN_ENTITLEMENTS = FieldKit/FieldKit.entitlements;
        			MACOSX_DEPLOYMENT_TARGET = 14.0; MARKETING_VERSION = 0.3.0; CURRENT_PROJECT_VERSION = 7;
        			PRODUCT_BUNDLE_IDENTIFIER = "com.example.fieldkit"; PRODUCT_NAME = "$(TARGET_NAME)"; SDKROOT = macosx; }; };
        		B1 = {isa = XCConfigurationList; buildConfigurations = ( B2, B3 ); };
        		B2 = {isa = XCBuildConfiguration; name = Debug; buildSettings = { SDKROOT = macosx; }; };
        		B3 = {isa = XCBuildConfiguration; name = Release; buildSettings = { SDKROOT = macosx; }; };
        		A10 = {isa = XCLocalSwiftPackageReference; relativePath = Packages/FieldKitCore; };
        		A11 = {isa = XCSwiftPackageProductDependency; productName = FieldKitCore; };
        		A12 = {isa = XCRemoteSwiftPackageReference; repositoryURL = "https://github.com/apple/swift-collections"; requirement = { kind = upToNextMajorVersion; minimumVersion = 1.1.0; }; };
        		A13 = {isa = XCSwiftPackageProductDependency; package = A12; productName = Collections; };
        		P1 = {isa = PBXProject; buildConfigurationList = B1; targets = ( A5 ); packageReferences = ( A10, A12 ); };
        	};
        	rootObject = P1;
        }
        """;

    private const string Scheme = """
        <?xml version="1.0" encoding="UTF-8"?>
        <Scheme LastUpgradeVersion = "1600" version = "1.7">
           <BuildAction parallelizeBuildables = "YES" buildImplicitDependencies = "YES">
              <BuildActionEntries>
                 <BuildActionEntry buildForTesting = "YES" buildForRunning = "YES" buildForProfiling = "YES" buildForArchiving = "YES" buildForAnalyzing = "YES">
                    <BuildableReference BuildableIdentifier = "primary" BlueprintIdentifier = "A5" BuildableName = "FieldKit.app" BlueprintName = "FieldKit" ReferencedContainer = "container:FieldKit.xcodeproj"/>
                 </BuildActionEntry>
              </BuildActionEntries>
           </BuildAction>
           <TestAction buildConfiguration = "Debug">
              <Testables>
                 <TestableReference skipped = "NO">
                    <BuildableReference BuildableIdentifier = "primary" BlueprintIdentifier = "FieldKitCoreTests" BuildableName = "FieldKitCoreTests" BlueprintName = "FieldKitCoreTests" ReferencedContainer = "container:Packages/FieldKitCore"/>
                 </TestableReference>
              </Testables>
           </TestAction>
           <ArchiveAction buildConfiguration = "Release" revealArchiveInOrganizer = "YES"/>
        </Scheme>
        """;

    /// <summary>The synthetic project; <paramref name="root"/> receives FieldKit.xcodeproj, FieldKit/ and Packages/FieldKitCore.</summary>
    private string Project(string? root = null, bool withPackageManifest = true)
    {
        root ??= _t.Combine("FieldKit");
        void W(string rel, string text) { var p = Path.Combine(root, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, text); }
        W("FieldKit.xcodeproj/project.pbxproj", Pbx);
        W("FieldKit.xcodeproj/project.xcworkspace/contents.xcworkspacedata", "<?xml version=\"1.0\"?><Workspace version=\"1.0\"><FileRef location=\"self:\"/></Workspace>");
        W("FieldKit.xcodeproj/project.xcworkspace/xcshareddata/swiftpm/Package.resolved",
            """{"originHash":"x","pins":[{"identity":"swift-collections","kind":"remoteSourceControl","location":"https://github.com/apple/swift-collections","state":{"revision":"671108c96644956dddcd89dd59c203dcdb36cec7","version":"1.1.4"}}],"version":3}""");
        W("FieldKit.xcodeproj/xcshareddata/xcschemes/FieldKit.xcscheme", Scheme);
        W("FieldKit/App/FieldKitApp.swift", "import SwiftUI\n@main struct FieldKitApp: App { var body: some Scene { WindowGroup { Text(\"x\") } } }\n");
        W("FieldKit/Views/Main.swift", "import AppKit\nimport SwiftUI\n");
        W("FieldKit/FieldKit.entitlements", "<?xml version=\"1.0\"?><plist version=\"1.0\"><dict><key>com.apple.security.app-sandbox</key><true/><key>com.apple.security.files.user-selected.read-write</key><true/></dict></plist>");
        W("FieldKit/Assets.xcassets/Contents.json", "{\"info\":{\"version\":1}}");
        W("FieldKit/Assets.xcassets/AppIcon.appiconset/Contents.json", "{\"images\":[{\"filename\":\"icon_512.png\",\"idiom\":\"mac\",\"size\":\"512x512\"},{\"filename\":\"missing.png\",\"idiom\":\"mac\",\"size\":\"16x16\"}]}");
        W("FieldKit/Assets.xcassets/AppIcon.appiconset/icon_512.png", "png");
        if (withPackageManifest)
        {
            W("Packages/FieldKitCore/Package.swift", "// swift-tools-version:5.9\nimport PackageDescription\nlet package = Package(name: \"FieldKitCore\", platforms: [.macOS(.v14)], products: [.library(name: \"FieldKitCore\", targets: [\"FieldKitCore\"])], targets: [.target(name: \"FieldKitCore\"), .testTarget(name: \"FieldKitCoreTests\", dependencies: [\"FieldKitCore\"])])\n");
            W("Packages/FieldKitCore/Sources/FieldKitCore/Core.swift", "public struct Core {}\n");
            W("Packages/FieldKitCore/Tests/FieldKitCoreTests/CoreTests.swift", "import XCTest\n");
        }
        return root;
    }

    // ------------------------------------------------------------------ inspection

    [Fact]
    public async Task An_Xcode_16_app_with_a_local_Swift_package_is_the_Xcode_app_not_a_Swift_package()
    {
        var root = Project();
        var a = await MacXcodeTests.Service().InspectAsync(root, MacXcodeTests.MacReport(_t), CancellationToken.None);
        Assert.Equal(MacXcodeProjectInspector.InspectorId, a.InspectorId);
        Assert.Equal(SupportLevel.Supported, a.Support);
        var p = Assert.Single(a.MacXcode!.Projects);
        Assert.Equal(77, p.ObjectVersion);
        var t = Assert.Single(p.AppTargets);
        Assert.Equal(["FieldKit"], t.SynchronizedFolders);
        Assert.Equal(["FieldKitCore", "Collections"], t.PackageProducts);
        Assert.True(t.GeneratedInfoPlist);

        // Packages: the local one is in the source (no download); the remote one needs resolution; the pin is read.
        var local = Assert.Single(p.PackageReferences, r => r.IsLocal);
        Assert.Equal(("Packages/FieldKitCore", "Packages/FieldKitCore/Package.swift"), (local.Location, local.ManifestPath));
        var remote = Assert.Single(p.PackageReferences, r => !r.IsLocal);
        Assert.Equal(("https://github.com/apple/swift-collections", "upToNextMajorVersion 1.1.0"), (remote.Location, remote.Requirement));
        Assert.Equal(("swift-collections", "1.1.4"), (p.ResolvedPackages.Single().Identity, p.ResolvedPackages.Single().Version));
        Assert.Contains(a.Diagnostics, d => d.Message.StartsWith("Local Swift package Packages/FieldKitCore (in the source; no download)", StringComparison.Ordinal));
        Assert.Contains(a.Diagnostics, d => d.Message.Contains("Package.resolved pins 1: swift-collections 1.1.4", StringComparison.Ordinal));

        // The scheme's Test action runs the package's tests; the archive uses Release.
        var scheme = Assert.Single(p.Schemes);
        Assert.Equal(["FieldKitCoreTests"], scheme.TestTargets);
        Assert.Equal(("Debug", "Release"), (scheme.TestConfiguration, scheme.ArchiveConfiguration));

        // Asset catalog and icon: the image the set names and has, not the one it names but lacks.
        Assert.Equal(["FieldKit/Assets.xcassets"], p.AssetCatalogs);
        Assert.Equal(["icon_512.png"], p.AppIconImages);
        Assert.Equal(["com.apple.security.app-sandbox", "com.apple.security.files.user-selected.read-write"], p.EntitlementFiles["FieldKit/FieldKit.entitlements"]);

        // Debug and Release are reported apart: they differ in identity, sandbox, entitlements and conditions.
        var debug = t.ConfigurationFacts.Single(c => c.Name == "Debug");
        var release = t.ConfigurationFacts.Single(c => c.Name == "Release");
        Assert.Equal(("com.example.fieldkit.dev", false, (string?)null), (debug.BundleIdentifier, debug.AppSandbox, debug.Entitlements));
        Assert.Equal(["FIELDKIT_DEV"], debug.CompilationConditions);
        Assert.Equal(("com.example.fieldkit", true, "FieldKit/FieldKit.entitlements"), (release.BundleIdentifier, release.AppSandbox, release.Entitlements));
        Assert.Equal("readwrite", release.SandboxAccess["ENABLE_USER_SELECTED_FILES"]);
        Assert.Contains(a.Diagnostics, d => d.Message.Contains("Debug: com.example.fieldkit.dev, App Sandbox off", StringComparison.Ordinal)
                                            && d.Message.Contains("Release: com.example.fieldkit, App Sandbox on", StringComparison.Ordinal));

        // The Swift inspector does not offer the app's own package as something to build.
        Assert.Equal(0, new SwiftProjectInspector(TimeProvider.System).Match(SourceIndex.Build(root)));
    }

    [Fact]
    public async Task A_local_package_whose_folder_is_missing_is_an_error_before_any_build()
    {
        var a = await MacXcodeTests.Service().InspectAsync(Project(withPackageManifest: false), MacXcodeTests.MacReport(_t), CancellationToken.None);
        Assert.Contains(a.Diagnostics, d => d.Severity == DiagnosticSeverity.Error && d.Message.Contains("its Package.swift is not in the imported source", StringComparison.Ordinal));
    }

    [Fact]
    public void A_Swift_Packages_folder_is_indexed_and_a_NuGet_packages_folder_is_not()
    {
        var root = _t.Combine("index");
        Directory.CreateDirectory(Path.Combine(root, "Packages", "Core"));
        File.WriteAllText(Path.Combine(root, "Packages", "Core", "Package.swift"), "// swift-tools-version:5.9");
        Directory.CreateDirectory(Path.Combine(root, "src", "packages", "Newtonsoft.Json.13.0.1"));
        File.WriteAllText(Path.Combine(root, "src", "packages", "Newtonsoft.Json.13.0.1", "Newtonsoft.Json.13.0.1.nupkg"), "x");
        var files = SourceIndex.Build(root).Files;
        Assert.Contains("Packages/Core/Package.swift", files);
        Assert.DoesNotContain(files, f => f.Contains(".nupkg", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_workspace_in_another_folder_that_references_the_project_is_built_instead_of_the_project()
    {
        var root = _t.Combine("ws");
        Project(Path.Combine(root, "App"));
        Directory.CreateDirectory(Path.Combine(root, "FieldKit.xcworkspace"));
        File.WriteAllText(Path.Combine(root, "FieldKit.xcworkspace", "contents.xcworkspacedata"),
            "<?xml version=\"1.0\"?><Workspace version=\"1.0\"><FileRef location=\"group:App/FieldKit.xcodeproj\"/></Workspace>");
        var a = await MacXcodeTests.Service().InspectAsync(root, MacXcodeTests.MacReport(_t), CancellationToken.None);
        Assert.Equal("FieldKit.xcworkspace", a.MacXcode!.Projects.Single().WorkspacePath);
        Assert.Equal("App/FieldKit.xcodeproj", MacXcodeProjectAnalyzer.WorkspaceRef("FieldKit.xcworkspace", "group:App/FieldKit.xcodeproj"));
        Assert.Equal("FieldKit.xcodeproj", MacXcodeProjectAnalyzer.WorkspaceRef("App/X.xcworkspace", "group:../FieldKit.xcodeproj"));
        Assert.Null(MacXcodeProjectAnalyzer.WorkspaceRef("X.xcworkspace", "absolute:/Users/someone/App.xcodeproj"));
    }

    /// <summary>The FieldKit fixture that CI compiles with the real Xcode (tests/Fixtures/MacXcode/FieldKit).</summary>
    [Fact]
    public async Task The_FieldKit_fixture_that_CI_compiles_is_recognised_as_the_Xcode_16_app_it_is()
    {
        var a = await MacXcodeTests.Service().InspectAsync(Path.Combine(Fixtures.RepoRoot, "tests", "Fixtures", "MacXcode", "FieldKit"), MacXcodeTests.MacReport(_t), CancellationToken.None);
        Assert.Equal((MacXcodeProjectInspector.InspectorId, SupportLevel.Supported), (a.InspectorId, a.Support));
        var p = Assert.Single(a.MacXcode!.Projects);
        Assert.Equal(77, p.ObjectVersion);
        Assert.Equal(["FieldKitCoreTests"], Assert.Single(p.Schemes).TestTargets);
        var t = Assert.Single(p.AppTargets);
        Assert.Equal(("FieldKit", "org.example.fieldkit", true), (t.ProductName, t.BundleIdentifier, t.GeneratedInfoPlist));
        Assert.Equal(["FieldKitCore"], t.PackageProducts);
        Assert.Equal(["org.example.fieldkit.dev", "org.example.fieldkit"], t.ConfigurationFacts.Select(c => c.BundleIdentifier));
        Assert.Equal([false, true], t.ConfigurationFacts.Select(c => c.AppSandbox ?? false));
        Assert.Equal(["icon_1024.png"], p.AppIconImages);
    }

    /// <summary>The real HCG Offensive Frontier checkout (private source, read here only, never copied into this repository).</summary>
    [Fact]
    public async Task HCG_Offensive_Frontier_is_recognised_with_its_scheme_package_and_configurations()
    {
        if (Environment.GetEnvironmentVariable("HCG_OFFENSIVE_FRONTIER_CHECKOUT") is not { } checkout || !Directory.Exists(checkout)) return;
        var a = await MacXcodeTests.Service().InspectAsync(checkout, MacXcodeTests.MacReport(_t), CancellationToken.None);
        Assert.Equal(MacXcodeProjectInspector.InspectorId, a.InspectorId);
        var p = a.MacXcode!.Projects.Single(x => x.ProjectPath == "OffensiveFrontier.xcodeproj");
        Assert.Contains(p.Schemes, s => s.Name == "OffensiveFrontier" && s.Shared && s.TestTargets.Contains("OffensiveFrontierCoreTests"));
        Assert.Contains(p.PackageReferences, r => r.IsLocal && r.ManifestPath == "Packages/OffensiveFrontierCore/Package.swift");
        var t = p.AppTargets.Single();
        Assert.Equal(["Debug", "Release"], t.ConfigurationFacts.Select(c => c.Name));
        Assert.True(t.ConfigurationFacts.Single(c => c.Name == "Release").AppSandbox);
    }

    // ------------------------------------------------------------------ the xcodebuild plan

    private static BuildSettings Settings(MacXcodeBuildOptions o, params PackagingFormat[] formats) => new()
    {
        ProjectRelativePath = o.ProjectPath, Platform = TargetPlatform.MacOS, Architecture = TargetArchitecture.Arm64, OutputDirectory = "/tmp/out",
        PackagingFormats = formats.Length == 0 ? [PackagingFormat.MacAppBundle] : formats, MacXcode = o,
        Identity = new AppIdentity { ApplicationName = "FieldKit", Version = "0.3.0", ApplicationId = "com.example.fieldkit", Publisher = "", Description = "" },
    };

    private static MacXcodeBuildOptions Options(bool tests = false, bool archive = false) => new()
    {
        ProjectPath = "FieldKit.xcodeproj", Scheme = "FieldKit", Configuration = "Release", AppTarget = "FieldKit", RunTests = tests, CreateArchive = archive,
    };

    private static MacXcodeBuildPlanner.Layout Layout() =>
        new("/b/xcode", "/b/xcode/source", "/b/xcode/source/FieldKit.xcodeproj", false, "/b/xcode/DerivedData", "/b/xcode/FieldKit.xcresult", "/b/xcode/SourcePackages");

    [Fact]
    public void The_plan_discovers_resolves_builds_tests_and_archives_as_separate_xcodebuild_steps()
    {
        var steps = MacXcodeBuildPlanner.Plan(Settings(Options(tests: true), PackagingFormat.MacAppBundle, PackagingFormat.MacXcodeArchive), Layout(), "/src", "/X/xcodebuild", null,
            new Dictionary<string, string>(), resolvePackages: true, hostArch: "arm64");
        Assert.Equal(["/usr/bin/ditto", "-list", "-resolvePackageDependencies", "-showBuildSettings", "build", "test", "archive"],
            steps.Select(s => s.Process.Executable == "/usr/bin/ditto" ? "/usr/bin/ditto"
                : s.Process.Arguments.FirstOrDefault(a => a is "-list" or "-resolvePackageDependencies" or "-showBuildSettings" or "build" or "test" or "archive")));
        var list = steps[1];
        Assert.Equal(MacXcodeBuildPlanner.ListKey, list.CaptureKey);
        Assert.NotNull(list.Check);
        var test = steps.Single(s => s.Process.Arguments.Contains("test"));
        Assert.Contains("platform=macOS,arch=arm64", test.Process.Arguments);
        Assert.Contains("/b/xcode/FieldKit-Tests.xcresult", test.Process.Arguments);
        Assert.DoesNotContain(test.Process.Arguments, a => a.StartsWith("ARCHS=", StringComparison.Ordinal));     // tests run on this Mac's architecture
        Assert.Contains("/b/xcode/FieldKit.xcarchive", steps.Single(s => s.Process.Arguments.Contains("archive")).Process.Arguments);
        // Without packages, tests or an archive: no such steps.
        Assert.Equal(4, MacXcodeBuildPlanner.Plan(Settings(Options()), Layout(), "/src", "/X/xcodebuild", null, new Dictionary<string, string>()).Count);
    }

    [Fact]
    public void Xcodebuild_list_decides_whether_the_scheme_and_configuration_exist()
    {
        string[] project = ["Command line invocation:", "{", "  \"project\" : {", "    \"configurations\" : [ \"Debug\", \"Release\" ],", "    \"name\" : \"FieldKit\",",
            "    \"schemes\" : [ \"FieldKit\" ],", "    \"targets\" : [ \"FieldKit\" ]", "  }", "}"];
        var (info, _) = MacXcodeBuildPlanner.ParseList(project);
        Assert.Equal("FieldKit", info!.Name);
        Assert.Equal(["FieldKit"], info.Schemes);
        Assert.Equal(["Debug", "Release"], info.Configurations);
        Assert.Null(MacXcodeBuildPlanner.CheckList(project, Options()));
        Assert.Contains("does not list the scheme Other", MacXcodeBuildPlanner.CheckList(project, Options() with { Scheme = "Other" }));
        Assert.Contains("no configuration Beta", MacXcodeBuildPlanner.CheckList(project, Options() with { Configuration = "Beta" }));
        var (ws, _) = MacXcodeBuildPlanner.ParseList(["{ \"workspace\" : { \"name\" : \"FieldKit\", \"schemes\" : [ \"FieldKit\", \"FieldKitCore\" ] } }"]);
        Assert.Equal(["FieldKit", "FieldKitCore"], ws!.Schemes);
        Assert.NotNull(MacXcodeBuildPlanner.ParseList(["xcodebuild: error: no project"]).Problem);
    }

    [Fact]
    public void An_Xcode_app_can_be_packaged_as_app_zip_dmg_pkg_and_xcarchive_on_a_Mac_only()
    {
        PackagingContext Ctx(TargetPlatform host) => new(TargetPlatform.MacOS, new HostEnvironment(host, TargetArchitecture.Arm64, "x", ".NET", "m"),
            MacXcodeTests.MacReport(_t), true) { IsMacXcode = true };
        var mac = PackagingCatalog.Evaluate(Ctx(TargetPlatform.MacOS));
        foreach (var f in new[] { PackagingFormat.MacAppBundle, PackagingFormat.PortableZip, PackagingFormat.MacDmg, PackagingFormat.MacPkg, PackagingFormat.MacXcodeArchive })
            Assert.Equal(PackagingAvailability.Available, mac.Single(o => o.Format == f).Availability);
        Assert.All(PackagingCatalog.Evaluate(Ctx(TargetPlatform.Linux)).Where(o => o.Format is PackagingFormat.MacPkg or PackagingFormat.MacXcodeArchive or PackagingFormat.MacAppBundle),
            o => Assert.Equal(PackagingAvailability.RequiresDifferentHost, o.Availability));
    }

    // ------------------------------------------------------------------ diagnostics

    /// <summary>xcodebuild's real output shapes (Xcode 16/26): a Swift error and warning, the failed-commands list, BUILD FAILED.</summary>
    private static readonly string[] FailedLog =
    [
        "Command line invocation:",
        "    /Applications/Xcode.app/Contents/Developer/usr/bin/xcodebuild -project FieldKit.xcodeproj -scheme FieldKit build",
        "SwiftCompile normal arm64 Compiling\\ Main.swift /b/xcode/source/FieldKit/Views/Main.swift (in target 'FieldKit' from project 'FieldKit')",
        "/b/xcode/source/FieldKit/Views/Main.swift:3:10: error: cannot find 'Missing' in scope",
        "/b/xcode/source/FieldKit/Views/Main.swift:3:10: note: did you mean 'Mission'?",
        "/b/xcode/source/FieldKit/App/FieldKitApp.swift:9:13: warning: variable 'x' was never used; consider replacing with '_' or removing it",
        "/b/xcode/source/FieldKit/Views/Main.swift:3:10: error: cannot find 'Missing' in scope",
        "** BUILD FAILED **",
        "",
        "The following build commands failed:",
        "\tSwiftCompile normal arm64 Compiling\\ Main.swift /b/xcode/source/FieldKit/Views/Main.swift (in target 'FieldKit' from project 'FieldKit')",
        "\tBuilding project FieldKit with scheme FieldKit and configuration Release",
        "(2 failures)",
    ];

    [Fact]
    public void A_failed_build_reports_the_Swift_error_with_file_line_column_and_target_and_stays_failed()
    {
        var r = XcodeDiagnostics.Analyze(FailedLog, "Failed", TimeSpan.FromSeconds(42.4), ["/b/xcode/source"]);
        var e = Assert.Single(r.Issues, i => i.Severity == "error");            // the repeated error is reported once; the note is not an issue
        Assert.Equal(("compiler", "cannot find 'Missing' in scope", "FieldKit/Views/Main.swift", 3, 10, "FieldKit"), (e.Category, e.Message, e.File, e.Line, e.Column, e.Target));
        var w = Assert.Single(r.Issues, i => i.Severity == "warning");
        Assert.Equal(("FieldKit/App/FieldKitApp.swift", 9), (w.File, w.Line));
        Assert.Equal(["FieldKit"], r.FailedTargets);
        Assert.Equal(2, r.FailedCommands.Count);
        Assert.Equal("SwiftCompile normal arm64 Compiling\\ Main.swift /b/xcode/source/FieldKit/Views/Main.swift", r.FailedCommands[0].Command);
        Assert.Equal(["** BUILD FAILED **"], r.ResultLines);

        // The report keeps the build's own verdict and quotes the source line.
        var src = _t.Combine("src");
        Directory.CreateDirectory(Path.Combine(src, "FieldKit", "Views"));
        File.WriteAllText(Path.Combine(src, "FieldKit", "Views", "Main.swift"), "import AppKit\nimport SwiftUI\nlet y = Missing()\n");
        var ctx = new XcodeDiagnostics.Context("FieldKit.xcodeproj", "FieldKit", "Release", "Apple silicon (arm64)", "26.6 (17F100)", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            "/b/build.log", [], "Build FieldKit failed with exit code 65.");
        var md = XcodeDiagnostics.ToMarkdown(r, ctx, src);
        Assert.Contains("**Status:** FAILED", md);
        Assert.DoesNotContain("SUCCEEDED", md);
        Assert.Contains("## Compiler errors (1)", md);
        Assert.Contains("line 3, column 10: cannot find 'Missing' in scope _(target `FieldKit`)_", md);
        Assert.Contains(">    3 | let y = Missing()", md);
        var json = XcodeDiagnostics.ToJson(r, ctx);
        Assert.Equal("Failed", json["status"]!.GetValue<string>());
        Assert.Empty(Packaging.Distribution.JsonSchemaLite.Validate(Schema("build-diagnostics-v1.schema.json"), json));
    }

    [Fact]
    public void Dependency_signing_linker_and_test_failures_are_named_as_such()
    {
        string[] log =
        [
            "xcodebuild: error: Could not resolve package dependencies:",
            "  Failed to clone repository https://github.com/example/missing:",
            "/b/xcode/source/FieldKit.xcodeproj: error: Signing for \"FieldKit\" requires a development team. Select a development team in the Signing & Capabilities editor. (in target 'FieldKit' from project 'FieldKit')",
            "ld: warning: building for macOS-14.0, but linking with dylib built for newer version",
            "Undefined symbols for architecture arm64:",
            "Test Case '-[FieldKitCoreTests.CoreTests testParse]' failed (0.012 seconds).",
            "/b/xcode/source/Packages/FieldKitCore/Tests/FieldKitCoreTests/CoreTests.swift:12: error: -[FieldKitCoreTests.CoreTests testParse] : XCTAssertEqual failed: (\"1\") is not equal to (\"2\")",
            "✘ Test roundTrip() recorded an issue at CoreTests.swift:30:7: Expectation failed: (a → 1) == (b → 2)",
            "** TEST FAILED **",
        ];
        var r = XcodeDiagnostics.Analyze(log, "Failed", null, ["/b/xcode/source"]);
        Assert.Contains(r.Issues, i => i.Category == "dependency" && i.Message.StartsWith("Could not resolve package dependencies", StringComparison.Ordinal));
        Assert.Contains(r.Issues, i => i.Category == "signing" && i.Message.Contains("requires a development team", StringComparison.Ordinal));
        Assert.Contains(r.Issues, i => i.Category == "linker" && i.Severity == "error");
        Assert.Contains(r.Issues, i => i.Category == "linker" && i.Severity == "warning");
        Assert.Equal(3, r.TestFailures.Count);
        Assert.Contains(r.TestFailures, t => t.Test == "FieldKitCoreTests.CoreTests testParse" && t.File == "Packages/FieldKitCore/Tests/FieldKitCoreTests/CoreTests.swift" && t.Line == 12);
        Assert.Contains(r.TestFailures, t => t.Test == "roundTrip()" && t.Line == 30);
        Assert.Equal(["** TEST FAILED **"], r.ResultLines);
    }

    /// <summary>Xcode 26.6's own output when a scheme's Test action has no test bundle it can build (seen in CI on FieldKit).</summary>
    [Fact]
    public void A_scheme_without_buildable_test_bundles_is_a_project_error_not_a_test_failure()
    {
        var r = XcodeDiagnostics.Analyze(["   ** BUILD SUCCEEDED **",
            "xcodebuild: error: Failed to build project FieldKit with scheme FieldKit.: There are no test bundles available to test."], "Failed", null, null);
        var e = Assert.Single(r.Issues);
        Assert.Equal(("error", "project"), (e.Severity, e.Category));
        Assert.Empty(r.TestFailures);
    }

    [Fact]
    public void HCGs_own_build_log_lines_are_read_like_xcodebuilds_raw_output()
    {
        string[] logged = ["[19:01:02]   /b/xcode/source/FieldKit/Views/Main.swift:3:10: error: cannot find 'Missing' in scope", "[19:01:03] ERROR Build failed with exit code 65.", "[19:01:03]   ** BUILD FAILED **"];
        var r = XcodeDiagnostics.Analyze(logged, "Failed", null, ["/b/xcode/source"]);
        Assert.Equal(("FieldKit/Views/Main.swift", 3), (r.Issues.Single().File, r.Issues.Single().Line));
        Assert.Equal(["** BUILD FAILED **"], r.ResultLines);
        var ok = XcodeDiagnostics.Analyze(["[10:00:00]   ** BUILD SUCCEEDED **"], "Succeeded", TimeSpan.FromMinutes(2), null);
        Assert.Equal((0, "Succeeded"), (ok.Errors, ok.Status));
    }

    // ------------------------------------------------------------------ signing, passport, contract

    [Fact]
    public void Signing_Doctor_tells_unsigned_ad_hoc_development_Developer_ID_and_notarized_apart()
    {
        Assert.Equal(MacSigningDoctor.Kind.Unsigned, MacSigningDoctor.Classify(null));
        Assert.Equal(MacSigningDoctor.Kind.AdHoc, MacSigningDoctor.Classify(MacCodeSigner.ParseSignatureInfo("Identifier=com.example.fieldkit\nSignature=adhoc\nTeamIdentifier=not set")));
        Assert.Equal(MacSigningDoctor.Kind.Development, MacSigningDoctor.Classify(MacCodeSigner.ParseSignatureInfo("Authority=Apple Development: Jane (ABC)\nTeamIdentifier=TEAM123456")));
        var devId = MacCodeSigner.ParseSignatureInfo("Authority=Developer ID Application: Example AB (TEAM123456)\nAuthority=Developer ID Certification Authority\nTeamIdentifier=TEAM123456\nCodeDirectory v=20500 size=1 flags=0x10000(runtime) hashes=1\nTimestamp=25 Nov 2026");
        Assert.Equal((MacSigningDoctor.Kind.DeveloperId, true, true), (MacSigningDoctor.Classify(devId), devId.HardenedRuntime, devId.SecureTimestamp));
        Assert.Equal(MacSigningDoctor.Kind.AppStore, MacSigningDoctor.Classify(MacCodeSigner.ParseSignatureInfo("Authority=Apple Distribution: Example AB (TEAM123456)")));

        Assert.Equal("accepted (Notarized Developer ID)", MacSigningDoctor.ParseGatekeeper(true, "/x/FieldKit.app: accepted\nsource=Notarized Developer ID\norigin=Developer ID Application: Example AB (TEAM123456)"));
        Assert.Equal("rejected (Unnotarized Developer ID)", MacSigningDoctor.ParseGatekeeper(false, "/x/FieldKit.app: rejected\nsource=Unnotarized Developer ID"));
        Assert.Equal("notarized, ticket stapled", MacSigningDoctor.ParseStapler(true, "Processing: /x/FieldKit.app\nThe validate action worked!"));
        Assert.Equal("no stapled notarization ticket", MacSigningDoctor.ParseStapler(false, "FieldKit.app does not have a ticket stapled to it."));
        var report = new MacSigningDoctor.Report { App = "/x/FieldKit.app", SignatureKind = MacSigningDoctor.Kind.DeveloperId, Gatekeeper = "accepted (Notarized Developer ID)" };
        Assert.Equal("developer-id-notarized", report.Status);
        Assert.Equal("developer-id", (report with { Gatekeeper = "rejected (Unnotarized Developer ID)" }).Status);
    }

    [Fact]
    public void Signing_Doctor_finds_every_embedded_piece_of_code()
    {
        var app = _t.Combine("FieldKit.app");
        foreach (var d in new[] { "Contents/MacOS", "Contents/Frameworks/Sparkle.framework", "Contents/PlugIns/Widget.appex", "Contents/XPCServices/Fetcher.xpc", "Contents/Library/LoginItems/Helper.app" })
            Directory.CreateDirectory(Path.Combine(app, d));
        File.WriteAllText(Path.Combine(app, "Contents", "Info.plist"), "<?xml version=\"1.0\"?><plist version=\"1.0\"><dict><key>CFBundleExecutable</key><string>FieldKit</string></dict></plist>");
        File.WriteAllText(Path.Combine(app, "Contents", "MacOS", "FieldKit"), "x");
        File.WriteAllText(Path.Combine(app, "Contents", "MacOS", "fieldkit-helper"), "x");
        File.WriteAllText(Path.Combine(app, "Contents", "Frameworks", "libswift_Concurrency.dylib"), "x");
        Assert.Equal(["Contents/Frameworks/Sparkle.framework", "Contents/Frameworks/libswift_Concurrency.dylib", "Contents/Library/LoginItems/Helper.app", "Contents/MacOS/fieldkit-helper",
                "Contents/PlugIns/Widget.appex", "Contents/XPCServices/Fetcher.xpc"], MacSigningDoctor.NestedCodePaths(app));
    }

    private static ArtifactRecord Artifact(PackagingFormat f, string name) => new()
    {
        Id = Guid.NewGuid(), BuildId = Guid.NewGuid(), Format = f, Kind = ArtifactKind.File, Path = "/out/" + name, SizeBytes = 1234, Sha256 = new string('a', 64),
        Verification = VerificationStatus.Passed, Checks = [new VerificationCheck("Expands to the app", VerificationStatus.Passed, "identical")], CreatedAt = DateTimeOffset.UtcNow,
    };

    private static MacAppFacts Facts() => new()
    {
        BundlePath = "/out/FieldKit.app", BundleIdentifier = "com.example.fieldkit", Name = "FieldKit", ShortVersion = "0.3.0", BuildVersion = "7",
        Architectures = ["arm64", "x86_64"],
    };

    [Fact]
    public void The_Release_Passport_of_a_macOS_artifact_is_valid_and_states_code_signing_not_repository_signing()
    {
        var signing = new MacSigningDoctor.Report
        {
            App = "/out/FieldKit.app", BundleIdentifier = "com.example.fieldkit", SignatureKind = MacSigningDoctor.Kind.AdHoc, HardenedRuntime = true, SignatureValid = true,
            Entitlements = new Dictionary<string, object> { ["com.apple.security.app-sandbox"] = true }, Gatekeeper = "rejected (no usable signature)", Notarization = "no stapled notarization ticket",
            Nested = [new MacSigningDoctor.NestedCode("Contents/Frameworks/libswift_Concurrency.dylib", MacSigningDoctor.Kind.AdHoc, null, null, false, true, "")],
        };
        var source = new SourceProvenance("Example/FieldKit", new string('b', 40), "main", false, "git in FieldKit");
        var p = MacXcodePassport.ForArtifact(Artifact(PackagingFormat.PortableZip, "FieldKit-0.3.0-osx-universal.zip"), Facts(), signing, source, "26.6 (17F100)", "26.5",
            DateTimeOffset.UtcNow, null, testsRequested: false);
        Assert.Empty(ReleasePassport.ValidateBuildHalf(p));
        Assert.Equal(("macos", "universal", "com.example.fieldkit", "0.3.0", "7"),
            (p["platform"]!.GetValue<string>(), p["architecture"]!.GetValue<string>(), p["package"]!.GetValue<string>(), p["version"]!.GetValue<string>(), p["build_number"]!.GetValue<string>()));
        Assert.Equal(new string('b', 40), p["source"]!["commit"]!.GetValue<string>());
        Assert.StartsWith("Xcode 26.6 (17F100), macOS SDK 26.5, HCG Executable Maker", p["build"]!["toolchain"]!.GetValue<string>());
        var cs = p["code_signing"]!;
        Assert.Equal(("ad-hoc", 2, true, false, true), (cs["status"]!.GetValue<string>(), cs["binaries"]!.GetValue<int>(), cs["all_signed"]!.GetValue<bool>(), cs["notarized"]!.GetValue<bool>(), cs["app_sandbox"]!.GetValue<bool>()));
        Assert.Equal(["scope", "status"], p["repository_signing"]!.AsObject().Select(kv => kv.Key));
        Assert.Equal("not-run", p["validation"]!["tests"]!["result"]!.GetValue<string>());
        Assert.Equal("not-recorded", p["validation"]!["levels"]!["application_launch"]!["state"]!.GetValue<string>());

        // Ship to Store reads it for the exact file: local provider, the commit, the Xcode toolchain.
        var store = StoreProvenance.From(p, new string('a', 64), 1234);
        Assert.True(store.Complete);
        Assert.Equal(("local", new string('b', 40)), (store.BuildProvider, store.SourceCommit));
        Assert.Contains("ad-hoc", store.ValidationReport!.Summary);
    }

    private static JsonNode Schema(string file) => JsonNode.Parse(File.ReadAllText(Path.Combine(Fixtures.RepoRoot, "docs", "contracts", file)))!;

    [Fact]
    public void The_native_build_request_and_result_follow_the_shared_contract()
    {
        var build = new BuildRecord { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), Settings = Settings(Options(tests: true)), StartedAt = DateTimeOffset.UtcNow, TrustAcknowledged = true };
        var request = NativeBuildContract.Request(build, build.Settings, new SourceProvenance(null, null, null, null, "FieldKit is not a Git working copy with a commit"),
            "Mac (Arm64)", ["discover", "resolve-packages", "build", "test", "package"], networkForPackages: true);
        Assert.Empty(Packaging.Distribution.JsonSchemaLite.Validate(Schema("native-build-request-v1.schema.json"), request));
        Assert.False(request["provider"]!["dependsOnGitHubActions"]!.GetValue<bool>());
        Assert.Contains("no notarization", request["approval"]!["scope"]!.GetValue<string>());

        build.Status = BuildStatus.Failed;
        build.FailureReason = "Build FieldKit failed with exit code 65.";
        build.EndedAt = build.StartedAt.AddSeconds(42);
        var diag = XcodeDiagnostics.Analyze(FailedLog, "Failed", TimeSpan.FromSeconds(42), ["/b/xcode/source"]);
        var result = NativeBuildContract.Result(build, request, [new("Build FieldKit", "xcodebuild … build", 65, 41.2)], diag, []);
        Assert.Empty(Packaging.Distribution.JsonSchemaLite.Validate(Schema("native-build-result-v1.schema.json"), result));
        Assert.Equal(("failed", 1, 65), (result["status"]!.GetValue<string>(), result["diagnostics"]!["errors"]!.GetValue<int>(), result["steps"]![0]!["exitCode"]!.GetValue<int>()));
        Assert.Empty(Packaging.Distribution.JsonSchemaLite.UnsupportedKeywords(Schema("native-build-request-v1.schema.json")));
    }

    [Fact]
    public void Source_provenance_is_only_what_Git_or_the_archive_says()
    {
        Assert.Equal(new string('c', 40), SourceProvenance.FromArchiveComment(new string('C', 40)).Commit);
        Assert.Null(SourceProvenance.FromArchiveComment("Made with love").Commit);
        Assert.Null(SourceProvenance.Unknown.Commit);
        Assert.False(new SourceProvenance("a/b", new string('d', 40), null, true, "git").IsPinned);
    }
}
