using System.Buffers.Binary;
using System.IO.Compression;
using HCG.ExecutableMaker.BuildEngine;
using HCG.ExecutableMaker.BuildEngine.Swift;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Infrastructure.Toolchains;
using HCG.ExecutableMaker.Packaging;
using HCG.ExecutableMaker.Packaging.Packagers;
using HCG.ExecutableMaker.Packaging.Verification;
using HCG.ExecutableMaker.ProjectInspection;
using HCG.ExecutableMaker.ProjectInspection.DotNet;
using HCG.ExecutableMaker.ProjectInspection.Ecosystems;
using HCG.ExecutableMaker.ProjectInspection.Mobile;
using HCG.ExecutableMaker.ProjectInspection.Swift;

namespace HCG.ExecutableMaker.Tests;

/// <summary>Swift Package Manager (macOS) support: discovery, evaluation parsing, capabilities, planning, packaging.</summary>
public class SwiftTests : IDisposable
{
    private readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    // Mirrors the Oslo-Sprint reference repository: two packages whose GUI products exist only under #if os(macOS).
    private const string DistilManifest = """
        // swift-tools-version: 5.9
        import PackageDescription

        var products: [Product] = [.library(name: "TokenLabCore", targets: ["TokenLabCore"])]
        var targets: [Target] = [
            .target(name: "TokenLabCore"),
            .testTarget(name: "TokenLabCoreTests", dependencies: ["TokenLabCore"])
        ]
        #if os(macOS)
        products.append(.executable(name: "TokenLab", targets: ["TokenLabApp"]))
        targets.append(.executableTarget(name: "TokenLabApp", dependencies: ["TokenLabCore"]))
        #endif

        let package = Package(
            name: "TokenLab",
            platforms: [.macOS(.v14)],
            products: products,
            targets: targets
        )
        """;

    private const string RelayManifest = """
        // swift-tools-version: 5.9
        import PackageDescription
        var products: [Product] = [
            .library(name: "RelayCore", targets: ["RelayCore"]),
            .library(name: "RelayAdapters", targets: ["RelayAdapters"]),
            .executable(name: "relay-runner", targets: ["RelayRunner"]),
        ]
        var targets: [Target] = [
            .target(name: "RelayCore"),
            .target(name: "RelayAdapters", dependencies: ["RelayCore"]),
            .executableTarget(name: "RelayRunner", dependencies: ["RelayCore"]),
            .testTarget(name: "RelayCoreTests", dependencies: ["RelayCore"]),
            .testTarget(name: "RelayAdaptersTests", dependencies: ["RelayAdapters", "RelayCore"]),
        ]
        #if os(macOS)
        products.append(.executable(name: "Relay", targets: ["RelayApp"]))
        targets.append(.executableTarget(name: "RelayApp", dependencies: ["RelayCore", "RelayAdapters"]))
        #endif
        let package = Package(name: "Relay", platforms: [.macOS(.v14)], products: products, targets: targets)
        """;

    private void WriteOsloSprint(string prefix = "")
    {
        _t.Write(prefix + "README.md", "# Oslo Sprint");
        _t.Write(prefix + "distil/Package.swift", DistilManifest);
        _t.Write(prefix + "distil/Sources/TokenLabApp/TokenLabApp.swift", "import SwiftUI\n\n@main\nstruct TokenLabApp: App {\n    var body: some Scene { WindowGroup { Text(\"Distil\") } }\n}\n");
        _t.Write(prefix + "distil/Sources/TokenLabCore/Core.swift", "import Foundation\npublic struct Tokenizer {}\n");
        _t.Write(prefix + "distil/Tests/TokenLabCoreTests/CoreTests.swift", "import XCTest\n");
        _t.Write(prefix + "relay/Package.swift", RelayManifest);
        _t.Write(prefix + "relay/Sources/RelayApp/RelayApp.swift", "import SwiftUI\n@main struct RelayApp: App { var body: some Scene { WindowGroup { Text(\"Relay\") } } }\n");
        _t.Write(prefix + "relay/Sources/RelayApp/Support/Audio.swift", "import AVFoundation\nfinal class Recorder { var recorder: AVAudioRecorder? }\n");
        _t.Write(prefix + "relay/Sources/RelayRunner/main.swift", "import Foundation\nprint(\"relay-runner\")\n");
        _t.Write(prefix + "relay/Sources/RelayCore/Core.swift", "import Foundation\n");
        // Build/cache folders must be ignored (a dependency checkout carries its own Package.swift).
        _t.Write(prefix + "relay/.build/checkouts/swift-argument-parser/Package.swift", "// swift-tools-version: 5.7\nimport PackageDescription\n");
        _t.Write(prefix + "distil/.swiftpm/xcode/package.xcworkspace/contents.xcworkspacedata", "<Workspace/>");
    }

    internal static ToolchainReport MacReport(string swift = "6.1.2", string sdk = "15.4", params string[] missing)
    {
        (string Id, string? Version, string? Path)[] tools =
        [
            (ToolchainIds.XcodeCommandLineTools, "16.3", "/Applications/Xcode.app/Contents/Developer"),
            (ToolchainIds.Swift, swift, "/usr/bin/swift"),
            (ToolchainIds.MacOSSdk, sdk, "/Applications/Xcode.app/Contents/Developer/Platforms/MacOSX.platform/Developer/SDKs/MacOSX.sdk"),
            (ToolchainIds.MacOSVersion, "15.5", null),
            (ToolchainIds.Codesign, null, "/usr/bin/codesign"),
            (ToolchainIds.Hdiutil, null, "/usr/bin/hdiutil"),
        ];
        return new ToolchainReport(new HostEnvironment(TargetPlatform.MacOS, TargetArchitecture.Arm64, "macOS 15.5", ".NET", "m"),
            tools.Select(t => new ToolchainInfo
            {
                Id = t.Id, DisplayName = t.Id, Category = "Swift",
                Status = missing.Contains(t.Id) ? ToolchainStatus.Missing : ToolchainStatus.Installed,
                Version = t.Version, ExecutablePath = t.Path,
            }).ToList(), null, null, DateTimeOffset.UtcNow);
    }

    private static ToolchainReport LinuxReport() => new(new HostEnvironment(TargetPlatform.Linux, TargetArchitecture.X64, "Ubuntu", ".NET", "m"),
        [new ToolchainInfo { Id = ToolchainIds.Swift, DisplayName = "swift", Category = "Swift", Status = ToolchainStatus.Installed, Version = "6.0.3", ExecutablePath = "/usr/bin/swift" }],
        null, null, DateTimeOffset.UtcNow);

    private static ProjectInspectionService Service() => new(
        new IProjectInspector[]
        {
            new DotNetProjectInspector(TimeProvider.System), new MobileProjectInspector(TimeProvider.System),
            new MobileArtifactProjectInspector(TimeProvider.System), new SwiftProjectInspector(TimeProvider.System),
        }.Concat(EcosystemSignatureInspector.CreateDefaults(TimeProvider.System)),
        TimeProvider.System);

    // ───────────── Passive discovery ─────────────

    [Fact]
    public async Task Finds_both_Oslo_Sprint_packages_ignores_build_folders_and_selects_none()
    {
        WriteOsloSprint();
        var a = await Service().InspectAsync(_t.Path, MacReport(), CancellationToken.None);
        Assert.Equal("swift", a.InspectorId);
        Assert.Equal(["distil/Package.swift", "relay/Package.swift"], a.Swift!.Packages.Select(p => p.ManifestPath).ToArray());
        Assert.Null(a.PrimaryProjectPath); // the user chooses distil/ or relay/
        Assert.Equal(2, a.Projects.Count);
        Assert.All(a.Projects, p => Assert.True(p.IsRunnableApplication));
        Assert.All(a.Swift.Packages, p => Assert.True(p.HasConditionalDeclarations));
        Assert.All(a.Swift.Packages, p => Assert.Null(p.Evaluated));
        Assert.Contains(a.Diagnostics, d => d.Message.Contains("NOT executed", StringComparison.Ordinal));
        Assert.Equal(SupportLevel.Supported, a.Support);
    }

    [Fact]
    public async Task Package_inside_a_ZIP_wrapper_folder_is_found()
    {
        WriteOsloSprint("Oslo-Sprint-main/");
        var a = await Service().InspectAsync(_t.Path, MacReport(), CancellationToken.None);
        Assert.Equal(["Oslo-Sprint-main/distil", "Oslo-Sprint-main/relay"], a.Swift!.Packages.Select(p => p.Directory).ToArray());
    }

    [Fact]
    public async Task Single_root_package_is_the_obvious_choice()
    {
        _t.Write("Package.swift", DistilManifest);
        _t.Write("Sources/TokenLabApp/App.swift", "import SwiftUI\n@main struct A: App { var body: some Scene { WindowGroup {} } }");
        var a = await Service().InspectAsync(_t.Path, MacReport(), CancellationToken.None);
        Assert.Equal("Package.swift", a.PrimaryProjectPath);
        Assert.Equal("", a.Swift!.Packages[0].Directory);
        Assert.Equal("TokenLab", a.ProjectName);
    }

    [Fact]
    public void Static_scan_reads_tools_version_platform_and_source_facts()
    {
        WriteOsloSprint();
        var index = SourceIndex.Build(_t.Path);
        var relay = SwiftPackageScanner.Scan(index, "relay/Package.swift");
        Assert.Equal("5.9", relay.ToolsVersion);
        Assert.Equal("14.0", relay.MinimumMacOSHint);
        Assert.False(relay.HasPackageDependencies);
        var app = SwiftPackageScanner.FactsFor(index, "relay", "RelayApp", null);
        Assert.True(app.HasGuiEntryPoint);
        Assert.Equal(SwiftAppKind.GuiApp, app.SuggestedKind);
        var mic = Assert.Single(app.PrivacyUsages);
        Assert.Equal("NSMicrophoneUsageDescription", mic.InfoPlistKey);
        Assert.Equal(SwiftAppKind.CommandLine, SwiftPackageScanner.FactsFor(index, "relay", "RelayRunner", null).SuggestedKind);
    }

    [Theory]
    [InlineData("// swift-tools-version:5.9\nimport PackageDescription", "5.9")]
    [InlineData("// swift-tools-version: 6.0\n", "6.0")]
    [InlineData("import PackageDescription\n", null)]
    public void Tools_version_comes_from_the_first_line(string manifest, string? expected) =>
        Assert.Equal(expected, SwiftPackageScanner.ParseToolsVersion(manifest));

    [Theory]
    [InlineData("platforms: [.macOS(.v14)]", "14.0")]
    [InlineData("platforms: [.iOS(.v17), .macOS(.v10_15)]", "10.15")]
    [InlineData("platforms: [.macOS(\"13.3\")]", "13.3")]
    [InlineData("platforms: [.iOS(.v17)]", null)]
    public void Minimum_macOS_hint(string code, string? expected) => Assert.Equal(expected, SwiftPackageScanner.ParseMinimumMacOS(code));

    [Fact]
    public async Task Mac_only_Xcode_project_without_a_package_is_explained_as_not_built()
    {
        _t.Write("MacApp.xcodeproj/project.pbxproj", """
            // !$*UTF8*$!
            { archiveVersion = 1; objects = {
              A1 = { isa = XCBuildConfiguration; buildSettings = { SDKROOT = macosx; PRODUCT_NAME = MacApp; }; name = Release; };
            }; rootObject = A1; }
            """);
        _t.Write("MacApp/App.swift", "import AppKit");
        var a = await Service().InspectAsync(_t.Path, MacReport(), CancellationToken.None);
        Assert.Equal("swift", a.InspectorId);
        Assert.Equal(SupportLevel.Unsupported, a.Support);
        Assert.Contains("not built in this version", a.SupportSummary, StringComparison.Ordinal);
        Assert.Contains("Mobile Build Studio", a.SupportSummary, StringComparison.Ordinal);
    }

    // ───────────── Capabilities ─────────────

    [Fact]
    public void Off_Mac_hosts_need_a_different_host_not_a_toolchain()
    {
        WriteOsloSprint();
        var pkg = SwiftPackageScanner.Scan(SourceIndex.Build(_t.Path), "distil/Package.swift");
        var a = SwiftCapabilities.Evaluate(pkg, LinuxReport());
        Assert.Equal(SupportLevel.RequiresDifferentHost, a.Support);
        Assert.Contains("Mac", a.Summary, StringComparison.Ordinal);
        Assert.All(a.Requirements, r => Assert.False(r.IsSatisfied));
    }

    [Fact]
    public void Mac_with_tools_is_supported_and_missing_tools_get_exact_fixes()
    {
        WriteOsloSprint();
        var pkg = SwiftPackageScanner.Scan(SourceIndex.Build(_t.Path), "relay/Package.swift");
        Assert.Equal(SupportLevel.Supported, SwiftCapabilities.Evaluate(pkg, MacReport()).Support);

        var noSwift = SwiftCapabilities.Evaluate(pkg, MacReport(missing: [ToolchainIds.Swift, ToolchainIds.XcodeCommandLineTools]));
        Assert.Equal(SupportLevel.MissingToolchain, noSwift.Support);
        Assert.Contains(noSwift.Remediation, r => r.Contains("xcode-select", StringComparison.Ordinal));

        var old = SwiftCapabilities.Evaluate(pkg, MacReport(swift: "5.8"));
        Assert.Equal(SupportLevel.MissingToolchain, old.Support);
        Assert.Contains(old.Remediation, r => r.Contains("tools version 5.9", StringComparison.Ordinal));

        var oldSdk = SwiftCapabilities.Evaluate(pkg, MacReport(sdk: "13.3"));
        Assert.Contains(oldSdk.Remediation, r => r.Contains("macOS 14.0", StringComparison.Ordinal));

        var noCodesign = SwiftCapabilities.Evaluate(pkg, MacReport(missing: [ToolchainIds.Codesign]));
        Assert.Contains(noCodesign.Remediation, r => r.Contains("codesign", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("5.10", "5.9", 1)]
    [InlineData("6.0", "6.0.0", 0)]
    [InlineData("5.9", "5.10", -1)]
    public void Version_comparison_is_numeric(string a, string b, int expected) => Assert.Equal(expected, Math.Sign(SwiftCapabilities.Compare(a, b)));

    [Theory]
    [InlineData("swift-driver version: 1.120.5 Apple Swift version 6.1.2 (swiftlang-6.1.2.1.2 clang-1700.0.13.5)\nTarget: arm64-apple-macosx15.0", "6.1.2")]
    [InlineData("Swift version 6.0.3 (swift-6.0.3-RELEASE)\nTarget: x86_64-unknown-linux-gnu", "6.0.3")]
    [InlineData("nothing", null)]
    public void Swift_version_is_parsed(string output, string? expected) => Assert.Equal(expected, ToolchainDetector.ParseSwiftVersion(output));

    // ───────────── Authoritative metadata (dump-package JSON) ─────────────

    internal const string RelayDumpPackage = """
        warning: some toolchain warning before the JSON
        {
          "name" : "Relay",
          "toolsVersion" : { "_version" : "5.9.0" },
          "platforms" : [ { "platformName" : "macos", "version" : "14.0", "options" : [] } ],
          "dependencies" : [],
          "products" : [
            { "name" : "RelayCore", "targets" : [ "RelayCore" ], "type" : { "library" : [ "automatic" ] }, "settings" : [] },
            { "name" : "RelayAdapters", "targets" : [ "RelayAdapters" ], "type" : { "library" : [ "automatic" ] }, "settings" : [] },
            { "name" : "relay-runner", "targets" : [ "RelayRunner" ], "type" : { "executable" : null }, "settings" : [] },
            { "name" : "Relay", "targets" : [ "RelayApp" ], "type" : { "executable" : null }, "settings" : [] }
          ],
          "targets" : [
            { "name" : "RelayCore", "type" : "regular", "dependencies" : [], "exclude" : [], "resources" : [], "settings" : [] },
            { "name" : "RelayAdapters", "type" : "regular", "dependencies" : [], "exclude" : [], "resources" : [], "settings" : [] },
            { "name" : "RelayRunner", "type" : "executable", "dependencies" : [], "exclude" : [], "resources" : [], "settings" : [] },
            { "name" : "RelayCoreTests", "type" : "test", "dependencies" : [], "exclude" : [], "resources" : [], "settings" : [] },
            { "name" : "RelayAdaptersTests", "type" : "test", "dependencies" : [], "exclude" : [], "resources" : [], "settings" : [] },
            { "name" : "RelayApp", "type" : "executable", "path" : null, "dependencies" : [], "exclude" : [],
              "resources" : [ { "rule" : { "process" : {} }, "path" : "Resources" } ], "settings" : [] }
          ]
        }
        """;

    [Fact]
    public void Dump_package_JSON_separates_executables_libraries_and_tests()
    {
        var m = SwiftManifestJson.Parse(RelayDumpPackage);
        Assert.Equal("Relay", m.Name);
        Assert.Equal("5.9.0", m.ToolsVersion);
        Assert.Equal("14.0", m.MinimumMacOS);
        Assert.Equal(["relay-runner", "Relay"], m.ExecutableProducts.Select(p => p.Name).ToArray());
        Assert.Equal(["RelayCore", "RelayAdapters"], m.LibraryProducts.Select(p => p.Name).ToArray());
        Assert.Equal(["RelayCoreTests", "RelayAdaptersTests"], m.TestTargets.Select(t => t.Name).ToArray());
        Assert.Equal(1, m.Targets.Single(t => t.Name == "RelayApp").ResourceCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("error: manifest parse error")]
    [InlineData("{ \"products\": [] }")]
    public void Unreadable_package_descriptions_are_rejected(string json) =>
        Assert.ThrowsAny<Exception>(() => SwiftManifestJson.Parse(json));

    [Fact]
    public async Task Evaluation_refuses_without_trust()
    {
        WriteOsloSprint();
        var paths = new AppPaths(_t.Combine("data"));
        var evaluator = new SwiftPackageEvaluator(new LocalProcessBuildWorkerStub(), new FixedToolchains(MacReport()), paths);
        var project = new ProjectRecord { Id = Guid.NewGuid(), Name = "x", SourceKind = ProjectSourceKind.Directory, SourceLocation = _t.Path, WorkspacePath = _t.Path, ImportedAt = DateTimeOffset.UtcNow };
        await Assert.ThrowsAsync<InvalidOperationException>(() => evaluator.EvaluateAsync(project, "distil", trustAcknowledged: false, null, CancellationToken.None));
    }

    // ───────────── Build planning ─────────────

    private static BuildSettings Settings(SwiftAppKind kind = SwiftAppKind.GuiApp, TargetArchitecture arch = TargetArchitecture.Arm64,
        string product = "TokenLab", string bundleId = "dev.tokenlab.prototype", IReadOnlyDictionary<string, string>? usages = null, string? min = "14.0") => new()
    {
        ProjectRelativePath = "distil/Package.swift",
        Platform = TargetPlatform.MacOS,
        Architecture = arch,
        OutputDirectory = "/tmp/out",
        Identity = new AppIdentity { ApplicationName = "Distil", Version = "0.4.0", ApplicationId = bundleId },
        PackagingFormats = [PackagingFormat.MacAppBundle],
        Swift = new SwiftBuildOptions
        {
            PackageDirectory = "distil", Product = product, Kind = kind, BuildNumber = "4", MinimumMacOS = min,
            Category = "public.app-category.developer-tools", UsageDescriptions = usages ?? new Dictionary<string, string>(),
        },
    };

    [Fact]
    public void Plan_resolves_builds_one_product_and_asks_for_the_bin_path()
    {
        _t.Write("distil/Package.swift", DistilManifest);
        var steps = SwiftBuildPlanner.Plan(new SwiftBuildPlanner.PlanInput(Settings(arch: TargetArchitecture.Universal), _t.Path, "/usr/bin/swift", "/hcg/build/swiftpm", new Dictionary<string, string>()));
        Assert.Equal(3, steps.Count);
        Assert.Equal(["package", "--scratch-path", "/hcg/build/swiftpm", "resolve"], steps[0].Process.Arguments);
        Assert.Equal(["build", "-c", "release", "--product", "TokenLab", "--scratch-path", "/hcg/build/swiftpm", "--arch", "arm64", "--arch", "x86_64"], steps[1].Process.Arguments);
        Assert.Equal("--show-bin-path", steps[2].Process.Arguments[^1]);
        Assert.True(steps[2].CaptureStdout);
        Assert.All(steps, s => Assert.Equal(Path.Combine(_t.Path, "distil"), s.Process.WorkingDirectory));
        Assert.All(steps, s => Assert.Equal("/usr/bin/swift", s.Process.Executable));
    }

    [Theory]
    [InlineData("TokenLab; rm -rf /", "not a valid Swift product name")]
    [InlineData("--product", "not a valid Swift product name")]
    public void Validation_rejects_unsafe_product_names(string product, string message)
    {
        var ex = Assert.Throws<BuildValidationException>(() => SwiftBuildPlanner.Validate(Settings(product: product), TargetPlatform.MacOS));
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validation_enforces_host_bundle_id_and_privacy_texts()
    {
        Assert.Contains("only be built on a Mac", Assert.Throws<BuildValidationException>(() => SwiftBuildPlanner.Validate(Settings(), TargetPlatform.Windows)).Message, StringComparison.Ordinal);
        Assert.Contains("bundle identifier", Assert.Throws<BuildValidationException>(() => SwiftBuildPlanner.Validate(Settings(bundleId: "not valid"), TargetPlatform.MacOS)).Message, StringComparison.Ordinal);
        var empty = new Dictionary<string, string> { ["NSMicrophoneUsageDescription"] = " " };
        Assert.Contains("NSMicrophoneUsageDescription", Assert.Throws<BuildValidationException>(() => SwiftBuildPlanner.Validate(Settings(usages: empty), TargetPlatform.MacOS)).Message, StringComparison.Ordinal);
        SwiftBuildPlanner.Validate(Settings(kind: SwiftAppKind.CommandLine, bundleId: "anything"), TargetPlatform.MacOS); // CLI: no bundle id needed
        var traversal = Settings() with { Swift = Settings().Swift! with { PackageDirectory = "../outside" } };
        Assert.Throws<BuildValidationException>(() => SwiftBuildPlanner.Validate(traversal, TargetPlatform.MacOS));
    }

    [Fact]
    public void Staging_copies_only_the_executable_and_resource_bundles()
    {
        var bin = _t.Combine("bin");
        _t.Write("bin/TokenLab", "exe");
        _t.Write("bin/TokenLab.swiftmodule/x", "intermediate");
        _t.Write("bin/TokenLabApp.build/main.o", "object");
        _t.Write("bin/TokenLab_TokenLabApp.bundle/Contents/Resources/a.json", "{}");
        var (exe, bundles) = SwiftBuildPlanner.Stage(bin, "TokenLab", _t.Combine("staging"));
        Assert.Equal("TokenLab", exe);
        Assert.Equal(["TokenLab_TokenLabApp.bundle"], bundles);
        Assert.Equal(["TokenLab", "TokenLab_TokenLabApp.bundle"], Directory.EnumerateFileSystemEntries(_t.Combine("staging")).Select(Path.GetFileName).Order().ToArray());
        Assert.Throws<BuildValidationException>(() => SwiftBuildPlanner.Stage(bin, "Missing", _t.Combine("staging2")));
    }

    [Fact]
    public void Swift_diagnostics_are_parsed_and_deduplicated()
    {
        var p = new SwiftOutputParser();
        var m = p.Parse("/ws/distil/Sources/TokenLabApp/ContentView.swift:12:9: error: cannot find 'foo' in scope");
        Assert.NotNull(m);
        Assert.Equal(DiagnosticSeverity.Error, m!.Severity);
        Assert.Equal(12, m.Line);
        Assert.EndsWith("ContentView.swift", m.File, StringComparison.Ordinal);
        Assert.Null(p.Parse("/ws/distil/Sources/TokenLabApp/ContentView.swift:12:9: error: cannot find 'foo' in scope"));
        Assert.Equal(DiagnosticSeverity.Warning, p.Parse("warning: 'distil': found 1 file(s) which are unhandled")!.Severity);
        Assert.Null(p.Parse("[3/12] Compiling TokenLabCore Models.swift"));
    }

    // ───────────── Packaging rules ─────────────

    private static PackagingContext Ctx(SwiftAppKind kind, ToolchainReport report) =>
        new(TargetPlatform.MacOS, report.Host, report, kind == SwiftAppKind.GuiApp, true, kind);

    [Fact]
    public void Gui_products_offer_app_zip_and_dmg_but_not_a_loose_folder()
    {
        var options = PackagingCatalog.Evaluate(Ctx(SwiftAppKind.GuiApp, MacReport())).ToDictionary(o => o.Format);
        Assert.Equal(PackagingAvailability.Available, options[PackagingFormat.MacAppBundle].Availability);
        Assert.Equal(PackagingAvailability.Available, options[PackagingFormat.PortableZip].Availability);
        Assert.Equal(PackagingAvailability.Available, options[PackagingFormat.MacDmg].Availability);
        Assert.Equal(PackagingAvailability.NotApplicable, options[PackagingFormat.PortableFolder].Availability);
    }

    [Fact]
    public void Cli_products_offer_folder_and_zip_only()
    {
        var options = PackagingCatalog.Evaluate(Ctx(SwiftAppKind.CommandLine, MacReport())).ToDictionary(o => o.Format);
        Assert.Equal(PackagingAvailability.Available, options[PackagingFormat.PortableFolder].Availability);
        Assert.Equal(PackagingAvailability.Available, options[PackagingFormat.PortableZip].Availability);
        Assert.Equal(PackagingAvailability.NotApplicable, options[PackagingFormat.MacAppBundle].Availability);
        Assert.Equal(PackagingAvailability.NotApplicable, options[PackagingFormat.MacDmg].Availability);
    }

    [Fact]
    public void Nothing_is_offered_for_local_builds_off_Mac()
    {
        var options = PackagingCatalog.Evaluate(Ctx(SwiftAppKind.GuiApp, LinuxReport()));
        Assert.DoesNotContain(options, o => o.Availability == PackagingAvailability.Available);
        Assert.Contains(options, o => o.Availability == PackagingAvailability.RequiresDifferentHost);
    }

    [Fact]
    public void App_zip_and_dmg_are_produced_after_the_bundle_by_the_Swift_packagers()
    {
        var s = Settings();
        Assert.Equal([PackagingFormat.MacAppBundle, PackagingFormat.PortableZip, PackagingFormat.MacDmg],
            PackagingCatalog.WithDependencies([PackagingFormat.MacDmg, PackagingFormat.PortableZip], s));
        var runner = new ProcessRunnerStub();
        var registry = new PackagingRegistry([new PortableFolderPackager(), new PortableZipPackager(), new MacAppBundlePackager(),
            new SwiftAppBundlePackager(runner), new SwiftAppZipPackager(runner), new DmgPackager(runner)]);
        Assert.IsType<SwiftAppBundlePackager>(registry.Get(PackagingFormat.MacAppBundle, s));
        Assert.IsType<SwiftAppZipPackager>(registry.Get(PackagingFormat.PortableZip, s));
        var cli = Settings(kind: SwiftAppKind.CommandLine);
        Assert.IsType<PortableZipPackager>(registry.Get(PackagingFormat.PortableZip, cli));
        Assert.Null(registry.Get(PackagingFormat.MacAppBundle, cli));
        var dotnet = s with { Swift = null };
        Assert.IsType<MacAppBundlePackager>(registry.Get(PackagingFormat.MacAppBundle, dotnet));
    }

    // ───────────── Mach-O and bundle verification ─────────────

    internal static byte[] ThinMachO(uint cpu, uint minos)
    {
        var b = new byte[32 + 24 + 64];
        BinaryPrimitives.WriteUInt32LittleEndian(b, 0xFEEDFACF);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), cpu);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), 2); // MH_EXECUTE
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16), 1); // ncmds
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(20), 24); // sizeofcmds
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(32), 0x32); // LC_BUILD_VERSION
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(36), 24);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(40), 1); // macOS
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(44), minos);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(48), 0x000F0000);
        return b;
    }

    internal static byte[] FatMachO(uint minos)
    {
        var arm = ThinMachO(0x0100000C, minos);
        var x64 = ThinMachO(0x01000007, minos);
        var b = new byte[8192 + x64.Length];
        BinaryPrimitives.WriteUInt32BigEndian(b, 0xCAFEBABE);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), 2);
        void Entry(int i, uint cpu, int offset, int size)
        {
            var e = b.AsSpan(8 + i * 20);
            BinaryPrimitives.WriteUInt32BigEndian(e, cpu);
            BinaryPrimitives.WriteUInt32BigEndian(e[8..], (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(e[12..], (uint)size);
            BinaryPrimitives.WriteUInt32BigEndian(e[16..], 12);
        }
        Entry(0, 0x01000007, 4096, x64.Length);
        Entry(1, 0x0100000C, 8192, arm.Length);
        x64.CopyTo(b, 4096);
        arm.CopyTo(b, 8192);
        return b;
    }

    [Fact]
    public void MachO_slices_and_deployment_targets_are_read()
    {
        File.WriteAllBytes(_t.Combine("thin"), ThinMachO(0x0100000C, 0x000E0000));
        File.WriteAllBytes(_t.Combine("fat"), FatMachO(0x000D0300));
        var thin = Assert.Single(MachO.ReadSlices(_t.Combine("thin")));
        Assert.Equal(("arm64", "14.0"), (thin.Architecture, thin.MinimumMacOS));
        Assert.Equal(["x86_64", "arm64"], MachO.ReadSlices(_t.Combine("fat")).Select(s => s.Architecture).ToArray());
        Assert.Equal("13.3", MachO.ReadSlices(_t.Combine("fat"))[0].MinimumMacOS);
        Assert.Equal(VerificationStatus.Passed, BinaryInspector.CheckMatchesTarget(_t.Combine("fat"), TargetPlatform.MacOS, TargetArchitecture.Universal).Status);
        Assert.Equal(VerificationStatus.Failed, BinaryInspector.CheckMatchesTarget(_t.Combine("thin"), TargetPlatform.MacOS, TargetArchitecture.Universal).Status);
        _t.Write("text", "not a binary");
        Assert.Empty(MachO.ReadSlices(_t.Combine("text")));
    }

    private string Bundle(IReadOnlyList<(string, object)> plist, byte[] exe, string name = "Distil.app")
    {
        var contents = _t.Combine(name, "Contents");
        Directory.CreateDirectory(Path.Combine(contents, "MacOS"));
        File.WriteAllText(Path.Combine(contents, "Info.plist"), InfoPlist.Create(plist));
        var exePath = Path.Combine(contents, "MacOS", "TokenLab");
        File.WriteAllBytes(exePath, exe);
        FileOps.MarkExecutable(exePath);
        return _t.Combine(name);
    }

    private static List<(string, object)> Plist(string min, params (string, object)[] extra) =>
    [
        ("CFBundleExecutable", "TokenLab"), ("CFBundleIdentifier", "dev.tokenlab.prototype"), ("CFBundleName", "Distil"),
        ("CFBundleDisplayName", "Distil"), ("CFBundlePackageType", "APPL"), ("CFBundleShortVersionString", "0.4.0"),
        ("CFBundleVersion", "4"), ("LSMinimumSystemVersion", min), .. extra,
    ];

    [Fact]
    public void Bundle_verifier_checks_executable_slices_deployment_target_and_privacy_keys()
    {
        var usages = new Dictionary<string, string> { ["NSMicrophoneUsageDescription"] = "Push to talk." };
        var ok = Bundle(Plist("14.0", ("NSMicrophoneUsageDescription", "Push to talk.")), ThinMachO(0x0100000C, 0x000E0000));
        var checks = SwiftBundleVerifier.VerifyStructure(ok, Settings(usages: usages), []);
        Assert.DoesNotContain(checks, c => c.Status == VerificationStatus.Failed);
        Assert.Contains(checks, c => c.Name == "Deployment target" && c.Status == VerificationStatus.Passed);
        Assert.Contains(checks, c => c.Name == "Privacy: NSMicrophoneUsageDescription" && c.Status == VerificationStatus.Passed);

        var tooLow = Bundle(Plist("13.0"), ThinMachO(0x0100000C, 0x000E0000), "Low.app");
        Assert.Contains(SwiftBundleVerifier.VerifyStructure(tooLow, Settings(), []), c => c.Name == "Deployment target" && c.Status == VerificationStatus.Failed);

        var noKey = Bundle(Plist("14.0"), ThinMachO(0x0100000C, 0x000E0000), "NoKey.app");
        Assert.Contains(SwiftBundleVerifier.VerifyStructure(noKey, Settings(usages: usages), []), c => c.Name.StartsWith("Privacy", StringComparison.Ordinal) && c.Status == VerificationStatus.Failed);

        var wrongArch = Bundle(Plist("14.0"), ThinMachO(0x01000007, 0x000E0000), "Intel.app");
        Assert.Contains(SwiftBundleVerifier.VerifyStructure(wrongArch, Settings(), []), c => c.Name == "Executable format" && c.Status == VerificationStatus.Failed);
    }

    // ───────────── Test doubles ─────────────

    private sealed class FixedToolchains(ToolchainReport report) : IToolchainService
    {
        public ToolchainReport? Latest => report;
        public event EventHandler<ToolchainReport>? Updated { add { } remove { } }
        public Task<ToolchainReport> GetAsync(bool refresh = false, CancellationToken cancellationToken = default) => Task.FromResult(report);
    }

    private sealed class ProcessRunnerStub : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessSpec spec, Action<OutputStream, string>? onOutput, CancellationToken cancellationToken) =>
            Task.FromResult(new ProcessResult(0, TimeSpan.Zero, false, false));
    }

    private sealed class LocalProcessBuildWorkerStub : HCG.ExecutableMaker.BuildEngine.IBuildWorker
    {
        public HCG.ExecutableMaker.Security.IsolationLevel Isolation => HCG.ExecutableMaker.Security.IsolationLevel.None;
        public string Description => "stub";
        public HCG.ExecutableMaker.Security.BuildEnvironmentPolicy.ScrubResult CreateEnvironment() =>
            HCG.ExecutableMaker.Security.BuildEnvironmentPolicy.CreateUntrustedBuildEnvironment();
        public Task<ProcessResult> RunAsync(ProcessSpec spec, Action<OutputStream, string> onOutput, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Nothing may run without trust.");
    }
}
