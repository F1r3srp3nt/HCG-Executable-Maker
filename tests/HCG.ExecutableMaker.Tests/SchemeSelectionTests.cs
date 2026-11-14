using HCG.ExecutableMaker.BuildEngine;
using HCG.ExecutableMaker.BuildEngine.Mobile;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Infrastructure.Toolchains;
using HCG.ExecutableMaker.Packaging.Mobile;
using HCG.ExecutableMaker.ProjectInspection;
using HCG.ExecutableMaker.ProjectInspection.Mobile;

namespace HCG.ExecutableMaker.Tests;

/// <summary>
/// Regression tests for Mobile Build Studio's scheme selection and status (HCG Sentinel: a legacy iOS 14.7 scheme and a
/// modern iOS 15.0 scheme, with an Xcode whose iPhoneOS SDK starts at iOS 15.0): effective build settings, per-SDK
/// compatibility, scheme-aware output availability, toolchain-missing versus incompatible, stale inspection results,
/// "Previous build" after a configuration change, and artifact expectations.
/// </summary>
public sealed class SchemeSelectionTests : IDisposable
{
    private readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    private static string FixtureDir(params string[] parts) => Path.Combine([Fixtures.RepoRoot, "tests", "Fixtures", .. parts]);

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
    }

    private IosProjectInfo Sentinel()
    {
        var root = _t.Combine("sentinel src");
        if (!Directory.Exists(root)) CopyDir(FixtureDir("XcodeDeployment", "Sentinel"), root);
        return XcodeProjectAnalyzer.Analyze(SourceIndex.Build(root, CancellationToken.None), "HCGSentinel.xcodeproj", out _);
    }

    private static XcodeInstallation Installed(string version, string min, string? simMin = null) =>
        new($"/Applications/Xcode_{version}.app", $"/Applications/Xcode_{version}.app/Contents/Developer", version, null)
        {
            IosRange = new IosDeploymentRange(min, "26.5.99", null, "26.5"),
            SimulatorRange = new IosDeploymentRange(simMin ?? min, "26.5.99", null, "26.5"),
        };

    /// <summary>The Mac from the report: the selected Xcode's iPhoneOS SDK accepts iOS 15.0 and later; its Simulator SDK is given separately.</summary>
    private static XcodeStatus Xcode(string deviceMin = "15.0", string simulatorMin = "15.0", params XcodeInstallation[] others) => new()
    {
        State = XcodeState.Ready, ActiveDeveloperDirectory = "/Applications/Xcode.app/Contents/Developer", VerifiedVersion = "27.0", Diagnosis = "Ready",
        Sdks = ["iphoneos 27.0", "iphonesimulator 27.0"],
        IosDeploymentRange = new IosDeploymentRange(deviceMin, "27.0.99", null, "27.0"),
        IosSimulatorDeploymentRange = new IosDeploymentRange(simulatorMin, "27.0.99", null, "27.0"),
        Installed = [new XcodeInstallation("/Applications/Xcode.app", "/Applications/Xcode.app/Contents/Developer", "27.0", null), .. others],
    };

    private static ToolchainReport Report(XcodeStatus? x) =>
        new(new HostEnvironment(TargetPlatform.MacOS, TargetArchitecture.Arm64, "macOS", ".NET", "osx"), [], null, null, DateTimeOffset.UtcNow) { Xcode = x };

    private static MobileAnalysis Mobile(IosProjectInfo ios) => new() { Framework = MobileFramework.NativeIos, Platforms = [TargetPlatform.IOS], Ios = ios, ApplicationIds = ["com.hakmicapitalgroup.hcgsentinel"] };

    // ───────────── Effective settings ─────────────

    [Fact]
    public void Sentinel_schemes_resolve_their_own_deployment_targets_per_configuration_and_sdk()
    {
        var ios = Sentinel();
        var legacy = ios.Targets.Single(t => t.Name == "HCGSentinel");
        var modern = ios.Targets.Single(t => t.Name == "HCGSentinelModern");
        Assert.Equal("14.7", legacy.SettingsFor("Release", "iphoneos")!.DeploymentTarget);
        Assert.Equal("14.7", legacy.SettingsFor("Debug", "iphonesimulator")!.DeploymentTarget);
        Assert.Equal("15.0", modern.SettingsFor("Release", "iphoneos")!.DeploymentTarget);
        var scheme = ios.Schemes.Single(s => s.Name == "HCGSentinel");
        Assert.Contains("HCGSentinel", scheme.ArchiveTargets);
        Assert.Contains("HCGSentinel", scheme.RunTargets);
    }

    private IosProjectInfo Synthetic()
    {
        // Project-level .xcconfig with #include and a configuration-specific value; target .xcconfig; an SDK-conditional
        // target setting; a bundle identifier built with a modifier; an extension the app depends on.
        _t.Write("demo/Demo/Config/Versions.xcconfig", "BASE_IOS = 14.0\n");
        _t.Write("demo/Demo/Shared.xcconfig", "#include \"Config/Versions.xcconfig\"\nIPHONEOS_DEPLOYMENT_TARGET = $(BASE_IOS)\nIPHONEOS_DEPLOYMENT_TARGET[config=Debug] = 13.0 // older test devices\n");
        _t.Write("demo/Demo/Config/App.xcconfig", "CURRENT_PROJECT_VERSION = 42\nOTHER_SWIFT_FLAGS = $(inherited) -DAPP\n");
        _t.Write("demo/Demo/Demo.xcodeproj/project.pbxproj", """
            // !$*UTF8*$!
            {
            	archiveVersion = 1;
            	classes = {
            	};
            	objectVersion = 56;
            	objects = {
            		P0 = {isa = PBXProject; buildConfigurationList = PL; mainGroup = G0; targets = (T1, T2); };
            		G0 = {isa = PBXGroup; children = (G1, F0); sourceTree = "<group>"; };
            		G1 = {isa = PBXGroup; children = (F1, F2); path = Config; sourceTree = "<group>"; };
            		F0 = {isa = PBXFileReference; path = Shared.xcconfig; sourceTree = SOURCE_ROOT; };
            		F1 = {isa = PBXFileReference; path = App.xcconfig; sourceTree = "<group>"; };
            		F2 = {isa = PBXFileReference; path = Versions.xcconfig; sourceTree = "<group>"; };
            		PL = {isa = XCConfigurationList; buildConfigurations = (PD, PR); };
            		PD = {isa = XCBuildConfiguration; name = Debug; baseConfigurationReference = F0; buildSettings = { SDKROOT = iphoneos; }; };
            		PR = {isa = XCBuildConfiguration; name = Release; baseConfigurationReference = F0; buildSettings = { SDKROOT = iphoneos; }; };
            		T1 = {isa = PBXNativeTarget; name = App; productType = "com.apple.product-type.application"; buildConfigurationList = TL1; dependencies = (D1); };
            		D1 = {isa = PBXTargetDependency; target = T2; };
            		TL1 = {isa = XCConfigurationList; buildConfigurations = (TD1, TR1); };
            		TD1 = {isa = XCBuildConfiguration; name = Debug; baseConfigurationReference = F1; buildSettings = { PRODUCT_NAME = "My App"; PRODUCT_BUNDLE_IDENTIFIER = "com.example.$(PRODUCT_NAME:rfc1034identifier)"; "IPHONEOS_DEPLOYMENT_TARGET[sdk=iphonesimulator*]" = 16.0; MARKETING_VERSION = 2.1; }; };
            		TR1 = {isa = XCBuildConfiguration; name = Release; baseConfigurationReference = F1; buildSettings = { PRODUCT_NAME = "My App"; PRODUCT_BUNDLE_IDENTIFIER = "com.example.$(PRODUCT_NAME:rfc1034identifier)"; MARKETING_VERSION = 2.1; }; };
            		T2 = {isa = PBXNativeTarget; name = Widget; productType = "com.apple.product-type.app-extension"; buildConfigurationList = TL2; dependencies = (); };
            		TL2 = {isa = XCConfigurationList; buildConfigurations = (WD, WR); };
            		WD = {isa = XCBuildConfiguration; name = Debug; buildSettings = { PRODUCT_BUNDLE_IDENTIFIER = "com.example.My-App.widget"; }; };
            		WR = {isa = XCBuildConfiguration; name = Release; buildSettings = { PRODUCT_BUNDLE_IDENTIFIER = "com.example.My-App.widget"; IPHONEOS_DEPLOYMENT_TARGET = 17.0; }; };
            	};
            	rootObject = P0;
            }
            """);
        _t.Write("demo/Demo/Demo.xcodeproj/xcshareddata/xcschemes/Demo.xcscheme", """
            <?xml version="1.0" encoding="UTF-8"?>
            <Scheme version="1.7">
              <BuildAction>
                <BuildActionEntries>
                  <BuildActionEntry buildForTesting="YES" buildForRunning="YES" buildForProfiling="YES" buildForArchiving="YES" buildForAnalyzing="YES">
                    <BuildableReference BuildableIdentifier="primary" BlueprintIdentifier="T1" BuildableName="My App.app" BlueprintName="App" ReferencedContainer="container:Demo.xcodeproj" />
                  </BuildActionEntry>
                </BuildActionEntries>
              </BuildAction>
              <LaunchAction buildConfiguration="Debug" />
              <ArchiveAction buildConfiguration="Release" revealArchiveInOrganizer="YES" />
            </Scheme>
            """);
        return XcodeProjectAnalyzer.Analyze(SourceIndex.Build(_t.Combine("demo"), CancellationToken.None), "Demo/Demo.xcodeproj", out _);
    }

    [Fact]
    public void Xcconfig_includes_configuration_and_sdk_conditions_inheritance_and_modifiers_are_resolved_like_xcode()
    {
        var ios = Synthetic();
        var app = ios.Targets.Single(t => t.Name == "App");
        Assert.Equal("14.0", app.SettingsFor("Release", "iphoneos")!.DeploymentTarget);               // Shared.xcconfig → $(BASE_IOS) from the #include
        Assert.Equal("Demo/Shared.xcconfig", app.SettingsFor("Release", "iphoneos")!.DeploymentTargetSource);
        Assert.Equal("13.0", app.SettingsFor("Debug", "iphoneos")!.DeploymentTarget);                 // [config=Debug]
        Assert.Equal("Demo/Shared.xcconfig[config=Debug]", app.SettingsFor("Debug", "iphoneos")!.DeploymentTargetSource);
        Assert.Equal("16.0", app.SettingsFor("Debug", "iphonesimulator")!.DeploymentTarget);          // target [sdk=iphonesimulator*] wins over the project
        Assert.Equal("target App (Debug)[sdk=iphonesimulator*]", app.SettingsFor("Debug", "iphonesimulator")!.DeploymentTargetSource);
        Assert.Equal("14.0", app.SettingsFor("Release", "iphonesimulator")!.DeploymentTarget);
        Assert.Equal("com.example.My-App", app.SettingsFor("Release", "iphoneos")!.BundleIdentifier); // $(PRODUCT_NAME:rfc1034identifier)
        Assert.Equal("42", app.SettingsFor("Release", "iphoneos")!.BuildVersion);                     // target .xcconfig (group path Config/)
        Assert.Equal(["Widget"], app.Dependencies);
        Assert.Equal("17.0", ios.Targets.Single(t => t.Name == "Widget").SettingsFor("Release", "iphoneos")!.DeploymentTarget);
        Assert.Equal("Debug", ios.Schemes.Single().LaunchConfiguration);
    }

    // ───────────── Compatibility per scheme, configuration and SDK ─────────────

    [Fact]
    public void The_legacy_scheme_is_incompatible_with_this_xcode_the_modern_one_is_verified_compatible_and_routes_are_named()
    {
        var ios = Sentinel();
        var x = Xcode(others: Installed("16.4", "12.0"));
        var legacy = IosSchemeCompatibility.Evaluate(ios, "HCGSentinel", null, simulator: false, x);
        Assert.Equal(IosToolchainFit.Incompatible, legacy.Fit);
        Assert.Contains("HCGSentinel needs iOS 14.7", legacy.Detail);
        Assert.Contains("accepts iOS 15.0–27.0.99", legacy.Detail);
        Assert.Contains("does not change the project's deployment target", legacy.Detail);
        Assert.Equal(["HCGSentinelModern"], legacy.CompatibleSchemes);
        Assert.Contains("select Xcode 16.4 (/Applications/Xcode_16.4.app)", legacy.Fix);       // an installed Xcode verified from its own SDK settings
        Assert.Contains("or use scheme HCGSentinelModern", legacy.Fix);

        var modern = IosSchemeCompatibility.Evaluate(ios, "HCGSentinelModern", null, simulator: false, x);
        Assert.Equal(IosToolchainFit.Compatible, modern.Fit);

        // No installed Xcode accepts 14.7: the requirement is named from the installed ranges, not invented.
        var none = IosSchemeCompatibility.Evaluate(ios, "HCGSentinel", null, false, Xcode(others: Installed("26.1", "15.0")));
        Assert.Contains("an Xcode whose iPhoneOS SDK accepts iOS 14.7 is required", none.Fix);
        Assert.Contains("Xcode 26.1: iOS 15.0–26.5.99", none.Fix);
    }

    [Fact]
    public void Simulator_and_device_are_judged_by_their_own_sdk_ranges()
    {
        var ios = Sentinel();
        var x = Xcode(deviceMin: "15.0", simulatorMin: "13.0");
        Assert.Equal(IosToolchainFit.Incompatible, IosSchemeCompatibility.Evaluate(ios, "HCGSentinel", null, simulator: false, x).Fit);
        var sim = IosSchemeCompatibility.Evaluate(ios, "HCGSentinel", null, simulator: true, x);
        Assert.Equal(IosToolchainFit.Compatible, sim.Fit);
        Assert.Equal("iphonesimulator", sim.Sdk);
        Assert.Contains("iPhoneSimulator", sim.Detail);

        // Missing Simulator range: not verifiable, never the device result reused.
        var noSimRange = Xcode() with { IosSimulatorDeploymentRange = null };
        Assert.Equal(IosToolchainFit.NotVerifiable, IosSchemeCompatibility.Evaluate(ios, "HCGSentinel", null, true, noSimRange).Fit);
    }

    [Fact]
    public void An_embedded_extension_with_its_own_deployment_target_is_checked_too()
    {
        var ios = Synthetic();
        var x = Xcode(deviceMin: "12.0") with { IosDeploymentRange = new IosDeploymentRange("12.0", "16.5.99", null, "16.5") };
        var r = IosSchemeCompatibility.Evaluate(ios, "Demo", null, simulator: false, x);
        Assert.Equal(IosToolchainFit.Incompatible, r.Fit);
        Assert.Contains("Widget (dependency) needs iOS 17.0", r.Detail);
        Assert.Equal(["App", "Widget"], r.Targets.Select(t => t.Target));
    }

    [Fact]
    public void Without_xcode_it_is_toolchain_missing_not_incompatible()
    {
        var ios = Sentinel();
        var missing = new XcodeStatus { State = XcodeState.NotInstalled, Diagnosis = "No Xcode application was found.", CorrectiveAction = "Install Xcode." };
        Assert.Equal(IosToolchainFit.ToolchainMissing, IosSchemeCompatibility.Evaluate(ios, "HCGSentinel", null, false, missing).Fit);
    }

    // ───────────── Output availability follows the selected scheme ─────────────

    [Fact]
    public void Switching_scheme_recomputes_which_outputs_are_available()
    {
        var m = Mobile(Sentinel());
        var report = Report(Xcode());
        MobileOutputOption Option(string scheme, MobileOutput o) =>
            MobileCapabilities.Evaluate(m, report, new MobileBuildOptions { Framework = m.Framework, Output = MobileOutput.IosSimulatorApp, Scheme = scheme }).Single(x => x.Output == o);

        Assert.False(Option("HCGSentinel", MobileOutput.IosArchive).IsAvailable);
        Assert.False(Option("HCGSentinel", MobileOutput.IosSimulatorApp).IsAvailable);
        Assert.True(Option("HCGSentinelModern", MobileOutput.IosArchive).IsAvailable, Option("HCGSentinelModern", MobileOutput.IosArchive).Reason);
        Assert.True(Option("HCGSentinelModern", MobileOutput.IosSimulatorApp).IsAvailable);
        // Back to the legacy scheme: unavailable again (no stale result carried over).
        Assert.False(Option("HCGSentinel", MobileOutput.IosArchive).IsAvailable);
        // Without a selection the project's default scheme is judged, as before.
        Assert.False(MobileCapabilities.Evaluate(m, report).Single(x => x.Output == MobileOutput.IosArchive).IsAvailable);
    }

    // ───────────── Selection workflow (shared by Mobile Build Studio and the build) ─────────────

    [Fact]
    public void One_concise_shared_message_and_outputs_that_only_refer_to_it()
    {
        var ios = Sentinel();
        var summary = IosBuildSelection.Summarize(ios, "HCGSentinel", null, Xcode());
        Assert.Equal("Project minimum: iOS 14.7. Selected toolchain minimum: iOS 15.0 (Xcode 27.0 · device and Simulator SDKs).", summary.Problem);
        Assert.Equal("14.7", summary.ProjectMinimum);
        Assert.Equal("15.0", summary.ToolchainMinimum);
        Assert.Equal(2, summary.Lines.Count);
        Assert.StartsWith("Device · iPhoneOS 27.0 · Release", summary.Lines[0].Label);
        Assert.StartsWith("Simulator · iPhoneSimulator 27.0 · Debug", summary.Lines[1].Label);
        Assert.Equal("HCGSentinelModern", summary.Alternative);
        Assert.Equal("15.0", summary.AlternativeMinimum);
        Assert.Contains("only on iOS 15.0 or later", summary.SwitchExplanation);
        Assert.Contains("HCGSentinel and its iOS 14.7 deployment target stay in the project unchanged", summary.SwitchExplanation);

        var m = Mobile(ios);
        var options = MobileCapabilities.Evaluate(m, Report(Xcode()), new MobileBuildOptions { Framework = m.Framework, Output = MobileOutput.IosArchive, Scheme = "HCGSentinel" });
        foreach (var o in options)
        {
            var reason = IosBuildSelection.ShortReason(o)!;
            Assert.StartsWith("Blocked by the deployment target", reason);
            Assert.DoesNotContain("xcodebuild would stop", reason);
            Assert.Contains("xcodebuild would stop", o.Reason);            // the full detail stays available in the readiness item
        }
        Assert.Null(IosBuildSelection.Summarize(ios, "HCGSentinelModern", null, Xcode()).Problem);
    }

    [Fact]
    public void Device_and_simulator_are_summarised_separately()
    {
        var summary = IosBuildSelection.Summarize(Sentinel(), "HCGSentinel", null, Xcode(deviceMin: "15.0", simulatorMin: "14.0"));
        Assert.EndsWith("(Xcode 27.0 · device SDK).", summary.Problem);
        Assert.False(summary.DeviceFits);
        Assert.True(summary.SimulatorFits);
        Assert.Equal("HCGSentinelModern", summary.Alternative);
    }

    [Fact]
    public void A_saved_selection_is_restored_only_while_it_still_matches_the_project()
    {
        var ios = Sentinel();
        var fresh = IosBuildSelection.Restore("p", ios, null);
        Assert.Equal("HCGSentinel.xcodeproj", fresh.Container);
        Assert.Equal("HCGSentinel", fresh.Scheme);
        Assert.Null(fresh.Configuration);
        var saved = new MobileSelection { ProjectId = "p", Container = "HCGSentinel.xcodeproj", Scheme = "HCGSentinelModern", Configuration = "Debug" };
        Assert.Equal(saved with { SavedAt = default }, IosBuildSelection.Restore("p", ios, saved) with { SavedAt = default });
        var stale = IosBuildSelection.Restore("p", ios, saved with { Scheme = "Deleted", Configuration = "Staging", Container = "Other.xcworkspace" });
        Assert.Equal("HCGSentinel", stale.Scheme);
        Assert.Null(stale.Configuration);
        Assert.Equal("HCGSentinel.xcodeproj", stale.Container);
    }

    [Fact]
    public void The_command_shown_is_the_command_planned_for_the_build()
    {
        var ios = Sentinel();
        var root = _t.Combine("sentinel src");
        var m = Mobile(ios);
        foreach (var (scheme, output, config) in new[] { ("HCGSentinelModern", MobileOutput.IosArchive, (string?)null), ("HCGSentinel", MobileOutput.IosSimulatorApp, "Release"), ("HCGSentinelModern", MobileOutput.IosSimulatorApp, null) })
        {
            var o = new MobileBuildOptions { Framework = m.Framework, Output = output, Scheme = scheme, Variant = config, Signing = MobileSigningMode.Unsigned, Container = "HCGSentinel.xcodeproj" };
            var preview = XcodePlanner.Preview(o, ios);
            var plan = XcodePlanner.Plan(new MobilePlanInput
            {
                Options = o, Analysis = m, SourceDirectory = root, WorkDirectory = _t.Combine("work"), Toolchains = Report(Xcode()),
                Environment = new Dictionary<string, string>(), ToolHomeDirectory = _t.Combine("home"),
            });
            var args = plan.Steps[0].Process.Arguments.ToList();
            Assert.Equal(scheme, args[args.IndexOf("-scheme") + 1]);
            var expectedConfig = config ?? (output == MobileOutput.IosSimulatorApp ? "Debug" : "Release");
            Assert.Equal(expectedConfig, args[args.IndexOf("-configuration") + 1]);
            var destination = args[args.IndexOf("-destination") + 1];
            Assert.Equal(output == MobileOutput.IosSimulatorApp ? "generic/platform=iOS Simulator" : "generic/platform=iOS", destination);
            Assert.Equal(output == MobileOutput.IosSimulatorApp, args.Contains("iphonesimulator"));
            Assert.Contains($"-scheme {scheme} -configuration {expectedConfig} ", preview);
            Assert.Contains("-destination " + (destination.Contains(' ') ? $"\"{destination}\"" : destination), preview);
            Assert.Equal(output == MobileOutput.IosSimulatorApp, preview.Contains("-sdk iphonesimulator"));
        }
        Assert.Throws<BuildValidationException>(() => XcodePlanner.Resolve(new MobileBuildOptions { Framework = m.Framework, Output = MobileOutput.IosArchive, Container = "../elsewhere.xcodeproj" }, ios));
    }

    [Fact]
    public async Task The_selection_is_saved_per_project()
    {
        var db = new HCG.ExecutableMaker.Infrastructure.Persistence.SqliteDatabase(_t.Combine("data", "t.db"));
        await db.MigrateAsync();
        var store = new HCG.ExecutableMaker.Infrastructure.Persistence.SqliteMobileSelectionStore(db);
        Assert.Null(await store.LoadAsync("a"));
        await store.SaveAsync(new MobileSelection { ProjectId = "a", Scheme = "HCGSentinelModern", Container = "HCGSentinel.xcodeproj" });
        await store.SaveAsync(new MobileSelection { ProjectId = "b", Scheme = "Other" });
        await store.SaveAsync(new MobileSelection { ProjectId = "a", Scheme = "HCGSentinelModern", Configuration = "Debug" });
        var a = await store.LoadAsync("a");
        Assert.Equal("HCGSentinelModern", a!.Scheme);
        Assert.Equal("Debug", a.Configuration);
        Assert.Equal("Other", (await store.LoadAsync("b"))!.Scheme);
    }

    // ───────────── States ─────────────

    private static ProjectAnalysis Analysis(MobileAnalysis m) => new()
    {
        InspectorId = "mobile", ProjectName = "HCG Sentinel", Language = "Swift", Framework = "Xcode", ApplicationType = "iOS application",
        Support = SupportLevel.Supported, SupportSummary = "Supported", Mobile = m, AnalyzedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void An_incompatible_xcode_is_not_reported_as_a_missing_toolchain_and_signing_is_never_a_toolchain_problem()
    {
        var m = Mobile(Sentinel());
        var options = MobileCapabilities.Evaluate(m, Report(Xcode()), new MobileBuildOptions { Framework = m.Framework, Output = MobileOutput.IosSimulatorApp, Scheme = "HCGSentinel" });
        var incompatible = MobileBuildState.Describe(Analysis(m), m, options, null, null, null, null);
        Assert.Equal("Selected toolchain incompatible", incompatible.Tag);
        Assert.Contains("needs iOS 14.7", incompatible.Detail);

        var noXcode = MobileCapabilities.Evaluate(m, Report(new XcodeStatus { State = XcodeState.NotInstalled, Diagnosis = "No Xcode application was found." }));
        Assert.Equal("Required toolchain missing", MobileBuildState.Describe(Analysis(m), m, noXcode, null, null, null, null).Tag);

        var signing = new BuildReadiness("ios", [new ReadinessItem("Apple Development certificate", ReadinessState.SetupRequired, true, "No Apple Development certificate in the keychain."),
                                                 new ReadinessItem("Provisioning profile", ReadinessState.NotFound, true, "No profile.")]);
        var selected = options.First();
        Assert.Equal("Configuration required", MobileBuildState.Describe(Analysis(m), m, options, selected, signing, null, null).Tag);
    }

    // ───────────── Stale inspection results ─────────────

    [Fact]
    public async Task A_slow_inspection_for_an_older_selection_does_not_overwrite_the_newer_one()
    {
        var gate = new InspectionGate();
        var shown = "none";
        var slow = new TaskCompletionSource<string>();
        var fast = new TaskCompletionSource<string>();
        async Task Inspect(Task<string> work)
        {
            var ticket = gate.Next();                    // the selection changes: a new generation
            var result = await work;
            gate.TryApply(ticket, () => shown = result);
        }
        var first = Inspect(slow.Task);                  // HCGSentinel inspection starts…
        var second = Inspect(fast.Task);                 // …the user switches to HCGSentinelModern
        fast.SetResult("HCGSentinelModern");
        await second;
        slow.SetResult("HCGSentinel");                   // the older inspection finishes last
        await first;
        Assert.Equal("HCGSentinelModern", shown);
        Assert.False(gate.IsCurrent(gate.Current - 1));
    }

    // ───────────── Previous build ─────────────

    private ProjectRecord Project(MobileAnalysis m)
    {
        Directory.CreateDirectory(_t.Combine("ws", "source"));
        return new ProjectRecord
        {
            Id = Guid.NewGuid(), Name = "HCG Sentinel", SourceKind = ProjectSourceKind.Directory, SourceLocation = _t.Path, WorkspacePath = _t.Combine("ws"),
            ImportedAt = new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.Zero), Analysis = Analysis(m),
        };
    }

    private static BuildSettings Settings(string scheme, MobileOutput output = MobileOutput.IosIpa, string? variant = null) => new()
    {
        ProjectRelativePath = "HCGSentinel.xcodeproj", Platform = TargetPlatform.IOS, Architecture = TargetArchitecture.Universal, Configuration = BuildConfigurationKind.Release,
        OutputDirectory = "/tmp/out", Identity = new AppIdentity { ApplicationName = "HCG Sentinel", Version = "1.0", ApplicationId = "com.hakmicapitalgroup.hcgsentinel" },
        Mobile = new MobileBuildOptions { Framework = MobileFramework.NativeIos, Output = output, Scheme = scheme, Variant = variant, Signing = MobileSigningMode.AppleAutomatic, ExportMethod = IosExportMethod.Debugging },
    };

    [Fact]
    public void A_build_made_with_other_settings_is_a_previous_build_with_what_produced_it_and_its_artifact_kept()
    {
        var m = Mobile(Sentinel());
        var project = Project(m);
        var report = Report(Xcode());
        var built = MobileBuildState.ProvenanceFor(project, Settings("HCGSentinelModern"), report) with { ArtifactPaths = ["/tmp/out/HCGSentinel.ipa"] };
        Assert.Equal("import of 2026-11-13 09:00 UTC", built.SourceRevision);
        Assert.Equal("HCGSentinelModern", built.Scheme);
        Assert.Equal("Release", built.Configuration);
        Assert.Equal("iphoneos", built.Sdk);
        Assert.Equal("generic/platform=iOS", built.Destination);
        var last = new BuildRecord
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, Settings = Settings("HCGSentinelModern"), StartedAt = DateTimeOffset.UtcNow.AddMinutes(-2), EndedAt = DateTimeOffset.UtcNow.AddSeconds(-47),
            Status = BuildStatus.Succeeded, Provenance = built,
            Artifacts = [new ArtifactRecord { Id = Guid.NewGuid(), BuildId = Guid.NewGuid(), Format = PackagingFormat.IosIpa, Kind = ArtifactKind.File, Path = "/tmp/out/HCGSentinel.ipa", SizeBytes = 1, Sha256 = "00", Verification = VerificationStatus.Passed, CreatedAt = DateTimeOffset.UtcNow }],
        };

        // Same settings: the result describes them.
        var same = MobileBuildState.DescribeResult(last, MobileBuildState.ProvenanceFor(project, Settings("HCGSentinelModern"), report));
        Assert.True(same.IsCurrent);
        Assert.Equal("Artifact verified", MobileBuildState.Describe(Analysis(m), m, [], new MobileOutputOption(MobileOutput.IosIpa, true, "", null), new BuildReadiness("ios", []), last,
            MobileBuildState.ProvenanceFor(project, Settings("HCGSentinelModern"), report)).Tag);

        // The scheme changes back to the legacy one: "Previous build", the differences named, the state follows the current settings.
        var now = MobileBuildState.ProvenanceFor(project, Settings("HCGSentinel"), report);
        var previous = MobileBuildState.DescribeResult(last, now);
        Assert.False(previous.IsCurrent);
        Assert.Equal("Previous build", previous.Tag);
        Assert.Contains("Scheme: HCGSentinelModern (built) → HCGSentinel (now)", previous.Differences);
        Assert.Contains("scheme HCGSentinelModern", previous.Detail);
        var options = MobileCapabilities.Evaluate(m, report, new MobileBuildOptions { Framework = m.Framework, Output = MobileOutput.IosIpa, Scheme = "HCGSentinel" });
        Assert.Equal("Selected toolchain incompatible", MobileBuildState.Describe(Analysis(m), m, options, null, null, last, now).Tag);
        Assert.Equal(["/tmp/out/HCGSentinel.ipa"], last.Provenance!.ArtifactPaths);

        // Configuration or toolchain changes count as well; with nothing buildable the old result is still not "current".
        Assert.Contains(MobileBuildState.DescribeResult(last, MobileBuildState.ProvenanceFor(project, Settings("HCGSentinelModern", variant: "Debug"), report)).Differences, d => d.StartsWith("Configuration:"));
        var otherXcode = Report(Xcode() with { VerifiedVersion = "16.4", ActiveDeveloperDirectory = "/Applications/Xcode_16.4.app/Contents/Developer" });
        Assert.Contains(MobileBuildState.DescribeResult(last, MobileBuildState.ProvenanceFor(project, Settings("HCGSentinelModern"), otherXcode)).Differences, d => d.StartsWith("Toolchain:"));
        Assert.False(MobileBuildState.DescribeResult(last, null).IsCurrent);
    }

    // ───────────── The artifact is what the selection defines ─────────────

    [Fact]
    public void The_ipa_is_checked_against_the_schemes_bundle_id_version_minimum_ios_and_architecture_and_runtime_is_not_claimed()
    {
        var expected = new IosResolvedSettings { Configuration = "Release", Sdk = "iphoneos", DeploymentTarget = "15.0", BundleIdentifier = "com.hakmicapitalgroup.hcgsentinel", MarketingVersion = "1.4", BuildVersion = "12" };
        var good = new MobileArtifactReport { Kind = MobileArtifactKind.Ipa, Path = "x.ipa", PackageId = "com.hakmicapitalgroup.hcgsentinel", VersionName = "1.4", VersionCode = "12", MinimumOs = "15.0", Architectures = ["arm64"], Platform = "iphoneos" };
        var checks = IosArtifactExpectations.Check(good, expected, simulator: false, TargetArchitecture.Arm64);
        Assert.All(checks.Where(c => c.Name is not ("Installation and runtime" or "Minimum iOS in executable")), c => Assert.Equal(VerificationStatus.Passed, c.Status));
        // The executable inside an .ipa is not read (no LC_BUILD_VERSION): said so, never a guessed pass.
        Assert.Equal(VerificationStatus.NotVerifiable, checks.Single(c => c.Name == "Minimum iOS in executable").Status);
        Assert.Equal(VerificationStatus.NotVerifiable, checks.Single(c => c.Name == "Installation and runtime").Status);

        // An IPA left over from the legacy scheme (iOS 14.7) is not accepted as the modern scheme's output.
        var wrong = good with { MinimumOs = "14.7", PackageId = "com.hakmicapitalgroup.hcgsentinel.legacy", Architectures = ["arm64e"], Platform = "iphonesimulator" };
        var bad = IosArtifactExpectations.Check(wrong, expected, false, TargetArchitecture.Arm64);
        Assert.Equal(VerificationStatus.Failed, bad.Single(c => c.Name == "Minimum iOS (matches build)").Status);
        Assert.Equal(VerificationStatus.Failed, bad.Single(c => c.Name == "Bundle identifier (matches build)").Status);
        Assert.Equal(VerificationStatus.Failed, bad.Single(c => c.Name == "Architectures (matches build)").Status);
        Assert.Equal(VerificationStatus.Failed, bad.Single(c => c.Name == "Platform (matches build)").Status);

        // Unresolved project values are not compared (never a guessed pass).
        var unresolved = IosArtifactExpectations.Check(good, expected with { BundleIdentifier = null, DeploymentTarget = null }, false, TargetArchitecture.Arm64);
        Assert.Equal(VerificationStatus.NotVerifiable, unresolved.Single(c => c.Name == "Bundle identifier (matches build)").Status);
        Assert.Equal(VerificationStatus.NotVerifiable, unresolved.Single(c => c.Name == "Minimum iOS (matches build)").Status);
    }

    [Fact]
    public void The_simulator_sdk_range_is_read_from_its_own_settings()
    {
        const string json = """{ "Version": "27.0", "SupportedTargets": { "iphonesimulator": { "MinimumDeploymentTarget": "13.0", "MaximumDeploymentTarget": "27.0.99" } } }""";
        Assert.Equal("13.0", XcodeDiscovery.ParseIosDeploymentRange(json, "iphonesimulator")!.Minimum);
        Assert.Null(XcodeDiscovery.ParseIosDeploymentRange(json, "iphoneos"));
    }
}

/// <summary>
/// The compatibility panel's resolution choices (HCG Sentinel at 03bc6e5: HCGSentinel iOS 14.7, HCGSentinelModern iOS 15.0,
/// a test target per scheme in its Test action, and the local SentinelCore package): the current scheme with its effective
/// minimum per SDK, every compatible discovered scheme judged by its own settings, the exact project change when nothing
/// compatible exists, and historical results labelled with what produced them and when.
/// </summary>
public sealed class CompatibilityResolutionTests : IDisposable
{
    private readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    private string Copy(string name)
    {
        var root = _t.Combine(name);
        if (!Directory.Exists(root)) MobileTestKit.CopyTree(Path.Combine(Fixtures.RepoRoot, "tests", "Fixtures", "XcodeDeployment", "Sentinel"), root);
        return root;
    }

    private IosProjectInfo Sentinel(string name = "sentinel") => XcodeProjectAnalyzer.Analyze(SourceIndex.Build(Copy(name), CancellationToken.None), "HCGSentinel.xcodeproj", out _);

    private static XcodeStatus Xcode(string min = "15.0", string simMin = "15.0", params XcodeInstallation[] others) => new()
    {
        State = XcodeState.Ready, ActiveDeveloperDirectory = "/Applications/Xcode.app/Contents/Developer", VerifiedVersion = "27.0", Diagnosis = "Ready",
        Sdks = ["iphoneos 27.0", "iphonesimulator 27.0"],
        IosDeploymentRange = new IosDeploymentRange(min, "27.2.99", null, "27.2"), IosSimulatorDeploymentRange = new IosDeploymentRange(simMin, "27.2.99", null, "27.2"),
        Installed = [new XcodeInstallation("/Applications/Xcode.app", "/Applications/Xcode.app/Contents/Developer", "27.0", null), .. others],
    };

    [Fact]
    public void The_panel_names_the_current_scheme_its_minimum_per_sdk_and_only_the_targets_its_builds_contain()
    {
        var ios = Sentinel();
        // Test targets exist in the project but belong to the schemes' Test actions, not to their run/archive builds.
        Assert.Contains(ios.Targets, t => t.Name == "HCGSentinelTests");
        var summary = IosBuildSelection.Summarize(ios, "HCGSentinel", null, Xcode());
        Assert.Equal("HCGSentinel — minimum iOS 14.7 for device (Release), iOS 14.7 for Simulator (Debug); checked: HCGSentinel.", summary.CurrentScheme);
        Assert.Equal("Project minimum: iOS 14.7. Selected toolchain minimum: iOS 15.0 (Xcode 27.0 · device and Simulator SDKs).", summary.Problem);
        Assert.All(summary.Lines, l => Assert.Contains("27.2", l.Label));            // the selected SDK, read from that Xcode
        Assert.Equal([("HCGSentinelModern", (string?)"15.0")], summary.CompatibleSchemes);
        Assert.Null(summary.ProjectChange);                                          // a compatible option exists
        Assert.Equal("HCGSentinelModern — minimum iOS 15.0 for device (Release), iOS 15.0 for Simulator (Debug); checked: HCGSentinelModern.",
            IosBuildSelection.Summarize(ios, "HCGSentinelModern", null, Xcode()).CurrentScheme);
    }

    [Fact]
    public void Compatibility_comes_from_each_schemes_settings_not_from_its_name()
    {
        // Rename the schemes: the modern one is now "Aardvark", the legacy one "ZModern".
        var root = Copy("renamed");
        var schemes = Path.Combine(root, "HCGSentinel.xcodeproj", "xcshareddata", "xcschemes");
        File.Move(Path.Combine(schemes, "HCGSentinelModern.xcscheme"), Path.Combine(schemes, "Aardvark.xcscheme"));
        File.Move(Path.Combine(schemes, "HCGSentinel.xcscheme"), Path.Combine(schemes, "ZModern.xcscheme"));
        var ios = XcodeProjectAnalyzer.Analyze(SourceIndex.Build(root, CancellationToken.None), "HCGSentinel.xcodeproj", out _);
        var summary = IosBuildSelection.Summarize(ios, "ZModern", null, Xcode());
        Assert.NotNull(summary.Problem);
        Assert.Equal("Aardvark", summary.Alternative);                               // the one whose targets fit
        Assert.Null(IosBuildSelection.Summarize(ios, "Aardvark", null, Xcode()).Problem);
    }

    [Fact]
    public void Without_a_compatible_scheme_or_xcode_the_exact_targets_and_configurations_needing_a_change_are_named()
    {
        // An Xcode whose SDKs start at iOS 16.0: neither scheme fits and no other Xcode is installed.
        var summary = IosBuildSelection.Summarize(Sentinel(), "HCGSentinel", null, Xcode("16.0", "16.0"));
        Assert.Empty(summary.CompatibleSchemes);
        Assert.Null(summary.Alternative);
        Assert.Equal("No discovered scheme and no installed Xcode can build HCGSentinel with Xcode 27.0. A project change is needed: " +
                     "target HCGSentinel: IPHONEOS_DEPLOYMENT_TARGET = 14.7 in Release (device, set in target HCGSentinel (Release)) and Debug (Simulator, set in target HCGSentinel (Debug)) " +
                     "— must be iOS 16.0 or later. HCG does not change the project; the build-only option below, where offered, applies to one build only.", summary.ProjectChange);

        // An installed Xcode that accepts 14.7 is a compatible option: no project change is demanded.
        var older = new XcodeInstallation("/Applications/Xcode_16.4.app", "/Applications/Xcode_16.4.app/Contents/Developer", "16.4", null)
        {
            IosRange = new IosDeploymentRange("12.0", "18.5.99", null, "18.5"), SimulatorRange = new IosDeploymentRange("12.0", "18.5.99", null, "18.5"),
        };
        var withOlder = IosBuildSelection.Summarize(Sentinel(), "HCGSentinel", null, Xcode("16.0", "16.0", older));
        Assert.Null(withOlder.ProjectChange);
        Assert.Contains("select Xcode 16.4", withOlder.KeepRoute);
    }

    [Fact]
    public void An_incompatible_dependency_is_named_in_the_project_change()
    {
        // The app fits; its embedded widget (a dependency, iOS 14.0) does not, and no other scheme or Xcode helps.
        var root = _t.Combine("dep");
        _t.Write(Path.GetRelativePath(_t.Path, Path.Combine(root, "Demo.xcodeproj", "project.pbxproj")), """
            // !$*UTF8*$!
            {
            	archiveVersion = 1;
            	objectVersion = 56;
            	objects = {
            		P0 = {isa = PBXProject; buildConfigurationList = PL; mainGroup = G0; targets = (T1, T2); };
            		G0 = {isa = PBXGroup; children = (); sourceTree = "<group>"; };
            		PL = {isa = XCConfigurationList; buildConfigurations = (PD, PR); };
            		PD = {isa = XCBuildConfiguration; name = Debug; buildSettings = { SDKROOT = iphoneos; IPHONEOS_DEPLOYMENT_TARGET = 16.0; }; };
            		PR = {isa = XCBuildConfiguration; name = Release; buildSettings = { SDKROOT = iphoneos; IPHONEOS_DEPLOYMENT_TARGET = 16.0; }; };
            		T1 = {isa = PBXNativeTarget; name = App; productType = "com.apple.product-type.application"; buildConfigurationList = TL1; dependencies = (D1); };
            		D1 = {isa = PBXTargetDependency; target = T2; };
            		TL1 = {isa = XCConfigurationList; buildConfigurations = (AD, AR); };
            		AD = {isa = XCBuildConfiguration; name = Debug; buildSettings = { PRODUCT_BUNDLE_IDENTIFIER = com.example.app; }; };
            		AR = {isa = XCBuildConfiguration; name = Release; buildSettings = { PRODUCT_BUNDLE_IDENTIFIER = com.example.app; }; };
            		T2 = {isa = PBXNativeTarget; name = Widget; productType = "com.apple.product-type.app-extension"; buildConfigurationList = TL2; dependencies = (); };
            		TL2 = {isa = XCConfigurationList; buildConfigurations = (WD, WR); };
            		WD = {isa = XCBuildConfiguration; name = Debug; buildSettings = { PRODUCT_BUNDLE_IDENTIFIER = com.example.app.widget; IPHONEOS_DEPLOYMENT_TARGET = 14.0; }; };
            		WR = {isa = XCBuildConfiguration; name = Release; buildSettings = { PRODUCT_BUNDLE_IDENTIFIER = com.example.app.widget; IPHONEOS_DEPLOYMENT_TARGET = 14.0; }; };
            	};
            	rootObject = P0;
            }
            """);
        var ios = XcodeProjectAnalyzer.Analyze(SourceIndex.Build(root, CancellationToken.None), "Demo.xcodeproj", out _);
        var summary = IosBuildSelection.Summarize(ios, null, null, Xcode());
        Assert.Contains("checked: App, Widget (dependency).", summary.CurrentScheme);
        Assert.Contains("target Widget (dependency): IPHONEOS_DEPLOYMENT_TARGET = 14.0 in Release (device, set in target Widget (Release))", summary.ProjectChange);
        Assert.DoesNotContain("target App:", summary.ProjectChange);
    }

    [Fact]
    public void A_scheme_change_reaches_the_command_and_simulator_and_archive_use_their_own_settings()
    {
        var ios = Sentinel();
        foreach (var scheme in new[] { "HCGSentinel", "HCGSentinelModern" })
        {
            var sim = XcodePlanner.Resolve(new MobileBuildOptions { Framework = MobileFramework.NativeIos, Output = MobileOutput.IosSimulatorApp, Scheme = scheme }, ios);
            var archive = XcodePlanner.Resolve(new MobileBuildOptions { Framework = MobileFramework.NativeIos, Output = MobileOutput.IosArchive, Scheme = scheme }, ios);
            Assert.Equal((scheme, "Debug", "build"), (sim.Scheme, sim.Configuration, sim.Action));
            Assert.Equal(["-sdk", "iphonesimulator", "-destination", "generic/platform=iOS Simulator"], sim.Destination);
            Assert.Equal((scheme, "Release", "archive"), (archive.Scheme, archive.Configuration, archive.Action));
            Assert.Equal(["-destination", "generic/platform=iOS"], archive.Destination);
            Assert.Contains($"-scheme {scheme} -configuration Release -destination generic/platform=iOS", XcodePlanner.Preview(new MobileBuildOptions { Framework = MobileFramework.NativeIos, Output = MobileOutput.IosArchive, Scheme = scheme }, ios));
        }
        // Different Simulator and device ranges: only the device fails, and only its outputs are blocked.
        var split = IosBuildSelection.Summarize(ios, "HCGSentinel", null, Xcode("15.0", "14.0"));
        Assert.Equal((false, true), (split.DeviceFits, split.SimulatorFits));
        var options = MobileCapabilities.Evaluate(new MobileAnalysis { Framework = MobileFramework.NativeIos, Platforms = [TargetPlatform.IOS], Ios = ios },
            new ToolchainReport(new HostEnvironment(TargetPlatform.MacOS, TargetArchitecture.Arm64, "macOS", ".NET", "osx"), [], null, null, DateTimeOffset.UtcNow) { Xcode = Xcode("15.0", "14.0") },
            new MobileBuildOptions { Framework = MobileFramework.NativeIos, Output = MobileOutput.IosSimulatorApp, Scheme = "HCGSentinel" });
        Assert.True(options.Single(o => o.Output == MobileOutput.IosSimulatorApp).IsAvailable);
        Assert.False(options.Single(o => o.Output == MobileOutput.IosArchive).IsAvailable);
    }

    [Fact]
    public void A_historical_result_is_labelled_with_its_scheme_configuration_and_time_and_never_as_the_current_state()
    {
        var ios = Sentinel();
        var m = new MobileAnalysis { Framework = MobileFramework.NativeIos, Platforms = [TargetPlatform.IOS], Ios = ios };
        var project = new ProjectRecord
        {
            Id = Guid.NewGuid(), Name = "HCG Sentinel", SourceKind = ProjectSourceKind.Directory, SourceLocation = Copy("sentinel"), WorkspacePath = _t.Combine("ws"),
            ImportedAt = new DateTimeOffset(2026, 11, 14, 9, 0, 0, TimeSpan.Zero),
            Analysis = new ProjectAnalysis { InspectorId = "mobile", ProjectName = "HCGSentinel", Language = "Swift", Framework = "Xcode", Runtime = "iOS", ApplicationType = "iOS application", Support = SupportLevel.Supported, SupportSummary = "", Mobile = m, AnalyzedAt = DateTimeOffset.UtcNow },
        };
        BuildSettings Settings(string scheme) => new()
        {
            ProjectRelativePath = "HCGSentinel.xcodeproj", Platform = TargetPlatform.IOS, Architecture = TargetArchitecture.Universal, Configuration = BuildConfigurationKind.Release, OutputDirectory = "/tmp/out",
            Identity = new AppIdentity { ApplicationName = "Sentinel", Version = "1.0", ApplicationId = "com.hcg.sentinel" },
            Mobile = new MobileBuildOptions { Framework = MobileFramework.NativeIos, Output = MobileOutput.IosArchive, Scheme = scheme, Signing = MobileSigningMode.Unsigned },
        };
        var report = new ToolchainReport(new HostEnvironment(TargetPlatform.MacOS, TargetArchitecture.Arm64, "macOS", ".NET", "osx"), [], null, null, DateTimeOffset.UtcNow) { Xcode = Xcode() };
        var last = new BuildRecord
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, Settings = Settings("HCGSentinelModern"), Status = BuildStatus.Succeeded,
            StartedAt = new DateTimeOffset(2026, 11, 14, 12, 1, 0, TimeSpan.Zero), EndedAt = new DateTimeOffset(2026, 11, 14, 12, 3, 30, TimeSpan.Zero),
            Provenance = MobileBuildState.ProvenanceFor(project, Settings("HCGSentinelModern"), report),
        };
        // Now the legacy scheme is selected and nothing is buildable (every output unavailable): no current result.
        var view = MobileBuildState.DescribeResult(last, null);
        Assert.False(view.IsCurrent);
        Assert.Equal("Previous build", view.Tag);
        Assert.StartsWith("Succeeded", view.Detail);
        Assert.Contains("built with scheme HCGSentinelModern, Release, finished 2026-11-14 12:03 UTC", view.Detail);
        Assert.Contains("does not describe them", view.Detail);
        // With other settings selected, the differences are listed as well.
        var other = MobileBuildState.DescribeResult(last, MobileBuildState.ProvenanceFor(project, Settings("HCGSentinel"), report));
        Assert.False(other.IsCurrent);
        Assert.Contains("Scheme: HCGSentinelModern (built) → HCGSentinel (now)", other.Differences);
    }
}
