using HCG.ExecutableMaker.BuildEngine;
using HCG.ExecutableMaker.BuildEngine.Mobile;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Infrastructure.Toolchains;
using HCG.ExecutableMaker.Packaging.Mobile;
using HCG.ExecutableMaker.ProjectInspection;
using HCG.ExecutableMaker.ProjectInspection.DotNet;
using HCG.ExecutableMaker.ProjectInspection.Ecosystems;
using HCG.ExecutableMaker.ProjectInspection.Mobile;
using HCG.ExecutableMaker.ProjectInspection.Swift;

namespace HCG.ExecutableMaker.Tests;

/// <summary>
/// Mobile Build Studio's deployment-target workflow (HCG Nexus: HCGNexus.xcodeproj, scheme HCGNexus, iOS 14.0, next to a
/// Theos package; the selected Xcode's SDKs start at iOS 15.0): a confirmed incompatibility explained once, the three
/// resolutions (another Xcode, another existing scheme, a build-only minimum iOS), the override's inspected effects, the
/// same values for validation and the build, device and Simulator judged separately, an inspection failure kept apart
/// from an incompatibility, and the Xcode app kept apart from the Theos package.
/// </summary>
public sealed class DeploymentTargetWorkflowTests : IDisposable
{
    private readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    private static string Fixture(params string[] parts) => Path.Combine([Fixtures.RepoRoot, "tests", "Fixtures", .. parts]);

    /// <summary>The Nexus fixture (Xcode app + Theos package) with the real project's iOS 14.0 deployment target.</summary>
    private string NexusRepo()
    {
        var dir = _t.Combine("HCG Nexus", "source");
        if (Directory.Exists(dir)) return dir;
        MobileTestKit.CopyTree(Fixture("Theos", "NexusWithXcode"), dir);
        var pbx = Path.Combine(dir, "HCGNexus.xcodeproj", "project.pbxproj");
        File.WriteAllText(pbx, File.ReadAllText(pbx).Replace("IPHONEOS_DEPLOYMENT_TARGET = 16.0;", "IPHONEOS_DEPLOYMENT_TARGET = 14.0;"));
        return dir;
    }

    private IosProjectInfo Nexus() => XcodeProjectAnalyzer.Analyze(SourceIndex.Build(NexusRepo(), CancellationToken.None), "HCGNexus.xcodeproj", out _);

    private IosProjectInfo Sentinel()
    {
        var root = _t.Combine("sentinel");
        if (!Directory.Exists(root)) MobileTestKit.CopyTree(Fixture("XcodeDeployment", "Sentinel"), root);
        return XcodeProjectAnalyzer.Analyze(SourceIndex.Build(root, CancellationToken.None), "HCGSentinel.xcodeproj", out _);
    }

    /// <summary>An app (iOS 14.0) embedding a widget extension with its own deployment target.</summary>
    private IosProjectInfo AppWithExtension(string extensionTarget, bool swiftPackages = false)
    {
        var root = _t.Combine("ext-" + extensionTarget.Replace('.', '_') + (swiftPackages ? "-spm" : ""));
        _t.Write(Path.GetRelativePath(_t.Path, Path.Combine(root, "Demo.xcodeproj", "project.pbxproj")), $$"""
            // !$*UTF8*$!
            {
            	archiveVersion = 1;
            	objectVersion = 56;
            	objects = {
            		P0 = {isa = PBXProject; buildConfigurationList = PL; mainGroup = G0; targets = (T1, T2); {{(swiftPackages ? "packageReferences = (PK1);" : "")}} };
            		{{(swiftPackages ? "PK1 = {isa = XCRemoteSwiftPackageReference; repositoryURL = \"https://example.com/pkg.git\"; };" : "")}}
            		G0 = {isa = PBXGroup; children = (); sourceTree = "<group>"; };
            		PL = {isa = XCConfigurationList; buildConfigurations = (PD, PR); };
            		PD = {isa = XCBuildConfiguration; name = Debug; buildSettings = { SDKROOT = iphoneos; IPHONEOS_DEPLOYMENT_TARGET = 14.0; }; };
            		PR = {isa = XCBuildConfiguration; name = Release; buildSettings = { SDKROOT = iphoneos; IPHONEOS_DEPLOYMENT_TARGET = 14.0; }; };
            		T1 = {isa = PBXNativeTarget; name = App; productType = "com.apple.product-type.application"; buildConfigurationList = TL1; dependencies = (D1); };
            		D1 = {isa = PBXTargetDependency; target = T2; };
            		TL1 = {isa = XCConfigurationList; buildConfigurations = (AD, AR); };
            		AD = {isa = XCBuildConfiguration; name = Debug; buildSettings = { PRODUCT_BUNDLE_IDENTIFIER = com.example.app; }; };
            		AR = {isa = XCBuildConfiguration; name = Release; buildSettings = { PRODUCT_BUNDLE_IDENTIFIER = com.example.app; }; };
            		T2 = {isa = PBXNativeTarget; name = Widget; productType = "com.apple.product-type.app-extension"; buildConfigurationList = TL2; dependencies = (); };
            		TL2 = {isa = XCConfigurationList; buildConfigurations = (WD, WR); };
            		WD = {isa = XCBuildConfiguration; name = Debug; buildSettings = { PRODUCT_BUNDLE_IDENTIFIER = com.example.app.widget; IPHONEOS_DEPLOYMENT_TARGET = {{extensionTarget}}; }; };
            		WR = {isa = XCBuildConfiguration; name = Release; buildSettings = { PRODUCT_BUNDLE_IDENTIFIER = com.example.app.widget; IPHONEOS_DEPLOYMENT_TARGET = {{extensionTarget}}; }; };
            	};
            	rootObject = P0;
            }
            """);
        return XcodeProjectAnalyzer.Analyze(SourceIndex.Build(root, CancellationToken.None), "Demo.xcodeproj", out _);
    }

    /// <summary>A selected Xcode whose SDK ranges are read from its SDKSettings.json (here: given).</summary>
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

    private static MobileAnalysis Mobile(IosProjectInfo ios) => new() { Framework = MobileFramework.NativeIos, Platforms = [TargetPlatform.IOS], Ios = ios };

    private static IReadOnlyList<MobileOutputOption> Outputs(IosProjectInfo ios, XcodeStatus x, string? scheme, string? raisedTo = null) =>
        MobileCapabilities.Evaluate(Mobile(ios), Report(x), new MobileBuildOptions
        {
            Framework = MobileFramework.NativeIos, Output = MobileOutput.IosSimulatorApp, Scheme = scheme, DeploymentTargetOverride = raisedTo,
        });

    private static bool Available(IReadOnlyList<MobileOutputOption> o, MobileOutput output) => o.Single(x => x.Output == output).IsAvailable;

    // ───────────── An incompatible older deployment target ─────────────

    [Fact]
    public void An_older_deployment_target_is_one_concise_confirmed_incompatibility_with_actionable_resolutions()
    {
        var ios = Nexus();
        Assert.Equal("14.0", ios.AppTargets.Single().SettingsFor("Release", "iphoneos")!.DeploymentTarget);
        var summary = IosBuildSelection.Summarize(ios, "HCGNexus", null, Xcode());

        Assert.Equal("Project minimum: iOS 14.0. Selected toolchain minimum: iOS 15.0 (Xcode 27.0 · device and Simulator SDKs).", summary.Problem);
        Assert.Equal(("14.0", "15.0"), (summary.ProjectMinimum, summary.ToolchainMinimum));
        Assert.Empty(summary.InspectionNotices);
        Assert.All(summary.Lines, l => Assert.Equal(IosToolchainFit.Incompatible, l.Fit));
        // Resolution 1: another Xcode (none installed accepts 14.0, so it says what to install). Resolution 2: none here.
        Assert.Contains("no installed Xcode accepts iOS 14.0", summary.KeepRoute);
        Assert.Null(summary.Alternative);
        // Resolution 3: raise to the selected Xcode's own minimum, inspected and available.
        var offer = Assert.IsType<IosDeploymentOverridePlan>(summary.RaiseOffer);
        Assert.True(offer.Supported);
        Assert.Equal("15.0", offer.Value);
        Assert.Equal("IPHONEOS_DEPLOYMENT_TARGET=15.0", offer.BuildSetting);
        var app = Assert.Single(offer.Effects);
        Assert.Equal(("HCGNexus", "app", "14.0", "15.0", IosOverrideChange.Raised), (app.Target, app.Kind, app.From, app.To, app.Change));
        Assert.Contains("will no longer install on iPhones or iPads running an iOS version earlier than 15.0", offer.Consequence);
        Assert.Contains("(the project itself supports iOS 14.0 and later)", offer.Consequence);
        Assert.Contains("The project is not edited", offer.Scope);
        Assert.Empty(offer.Blockers);   // (the real repository also lists HCGNexusTests as "not built by HCGNexus, so not affected")

        // Every output is blocked by that one explanation; none repeats the long paragraph.
        var outputs = Outputs(ios, Xcode(), "HCGNexus");
        Assert.All(outputs, o => Assert.False(o.IsAvailable));
        Assert.All(outputs, o => Assert.StartsWith("Blocked by the deployment target", IosBuildSelection.ShortReason(o)));
    }

    [Fact]
    public void The_toolchain_range_comes_from_the_selected_xcode_not_from_a_hardcoded_version()
    {
        var ios = Nexus();
        // The same Xcode version string with an older SDK minimum: compatible. Only the SDK's own range decides.
        Assert.Null(IosBuildSelection.Summarize(ios, "HCGNexus", null, Xcode(deviceMin: "12.0", simulatorMin: "12.0")).Problem);
        var summary = IosBuildSelection.Summarize(ios, "HCGNexus", null, Xcode(deviceMin: "16.0", simulatorMin: "16.0"));
        Assert.StartsWith("Project minimum: iOS 14.0. Selected toolchain minimum: iOS 16.0", summary.Problem);
        Assert.Equal("16.0", summary.RaiseOffer!.Value);

        // Read from SDKSettings.json on disk.
        var dev = _t.Combine("Xcode.app", "Contents", "Developer");
        _t.Write(Path.GetRelativePath(_t.Path, Path.Combine(dev, "Platforms", "iPhoneOS.platform", "Developer", "SDKs", "iPhoneOS.sdk", "SDKSettings.json")),
            """{ "Version": "27.0", "SupportedTargets": { "iphoneos": { "MinimumDeploymentTarget": "15.0", "MaximumDeploymentTarget": "27.0.99" } } }""");
        var (device, simulator, diagnostics) = XcodeDiscovery.ReadRanges(dev);
        Assert.Equal("15.0", device!.Minimum);
        Assert.Null(simulator);
        Assert.Contains("iPhoneSimulator.platform", diagnostics["iphonesimulator"]);
    }

    // ───────────── An existing compatible scheme ─────────────

    [Fact]
    public void An_existing_compatible_scheme_is_offered_next_to_the_other_resolutions()
    {
        var summary = IosBuildSelection.Summarize(Sentinel(), "HCGSentinel", null, Xcode());
        Assert.Equal("Project minimum: iOS 14.7. Selected toolchain minimum: iOS 15.0 (Xcode 27.0 · device and Simulator SDKs).", summary.Problem);
        Assert.Equal("HCGSentinelModern", summary.Alternative);
        Assert.Equal("15.0", summary.AlternativeMinimum);
        Assert.Contains("stay in the project unchanged", summary.SwitchExplanation);
        Assert.True(summary.RaiseOffer is { Supported: true, Value: "15.0" });
        Assert.Null(IosBuildSelection.Summarize(Sentinel(), "HCGSentinelModern", null, Xcode()).Problem);
    }

    [Fact]
    public void An_installed_xcode_that_accepts_the_target_is_named_as_the_way_to_keep_the_scheme()
    {
        var older = new XcodeInstallation("/Applications/Xcode_16.4.app", "/Applications/Xcode_16.4.app/Contents/Developer", "16.4", null)
        {
            IosRange = new IosDeploymentRange("12.0", "18.5.99", null, "18.5"), SimulatorRange = new IosDeploymentRange("12.0", "18.5.99", null, "18.5"),
        };
        var summary = IosBuildSelection.Summarize(Nexus(), "HCGNexus", null, Xcode("15.0", "15.0", older));
        Assert.Equal("Keep HCGNexus: select Xcode 16.4 (installed, its SDK accepts iOS 14.0) in the Xcode list above.", summary.KeepRoute);
    }

    // ───────────── An explicitly accepted build-only deployment change ─────────────

    [Fact]
    public void An_accepted_build_only_minimum_is_validated_planned_and_verified_with_the_same_value_and_the_project_is_untouched()
    {
        var ios = Nexus();
        var pbx = Path.Combine(NexusRepo(), "HCGNexus.xcodeproj", "project.pbxproj");
        var before = File.ReadAllText(pbx);

        // Validation with the override: compatible for both SDKs, outputs available.
        var summary = IosBuildSelection.Summarize(ios, "HCGNexus", null, Xcode(), deploymentOverride: "15.0");
        Assert.Null(summary.Problem);
        Assert.Equal("15.0", summary.ActiveOverride!.Value);
        Assert.All(summary.Lines, l => Assert.Contains("build-only minimum", l.Text));
        var outputs = Outputs(ios, Xcode(), "HCGNexus", raisedTo: "15.0");
        Assert.True(Available(outputs, MobileOutput.IosSimulatorApp));
        Assert.True(Available(outputs, MobileOutput.IosArchive));
        var c = IosSchemeCompatibility.Evaluate(ios, "HCGNexus", "Release", false, Xcode(), deploymentOverride: "15.0");
        Assert.Equal(("15.0", "14.0"), (c.Targets[0].DeploymentTarget, c.Targets[0].ProjectDeploymentTarget));

        // The build: the same value as one xcodebuild build setting, in the command shown and the command run.
        foreach (var output in new[] { MobileOutput.IosSimulatorApp, MobileOutput.IosArchive })
        {
            var o = new MobileBuildOptions { Framework = MobileFramework.NativeIos, Output = output, Scheme = "HCGNexus", Signing = MobileSigningMode.Unsigned, DeploymentTargetOverride = "15.0" };
            Assert.Contains(" IPHONEOS_DEPLOYMENT_TARGET=15.0 … ", XcodePlanner.Preview(o, ios));
            var plan = XcodePlanner.Plan(new MobilePlanInput
            {
                Options = o, Analysis = Mobile(ios), SourceDirectory = NexusRepo(), WorkDirectory = _t.Combine("work"), Toolchains = Report(Xcode()),
                Environment = new Dictionary<string, string>(), ToolHomeDirectory = _t.Combine("work"),
            });
            var args = plan.Steps[0].Process.Arguments;
            Assert.Single(args, a => a == "IPHONEOS_DEPLOYMENT_TARGET=15.0");
            Assert.Equal(output == MobileOutput.IosArchive ? "archive" : "build", args[^1]);
            Assert.Contains(plan.Notes, n => n.Contains("Minimum iOS raised to 15.0 for this build only", StringComparison.Ordinal));
        }
        // Without the override, nothing is added.
        Assert.DoesNotContain("IPHONEOS_DEPLOYMENT_TARGET", XcodePlanner.Preview(new MobileBuildOptions { Framework = MobileFramework.NativeIos, Output = MobileOutput.IosArchive, Scheme = "HCGNexus" }, ios));
        Assert.Equal(before, File.ReadAllText(pbx));

        // The artifact must carry the accepted value, in Info.plist and in the executable.
        var expected = ios.AppTargets.Single().SettingsFor("Release", "iphoneos")! with { DeploymentTarget = "15.0" };
        var good = new MobileArtifactReport
        {
            Kind = MobileArtifactKind.XcArchive, Path = "HCGNexus.xcarchive", PackageId = expected.BundleIdentifier, VersionName = expected.MarketingVersion,
            VersionCode = expected.BuildVersion, MinimumOs = "15.0", BinaryMinimumOs = ["15.0"], Architectures = ["arm64"], Platform = "iphoneos",
        };
        var checks = IosArtifactExpectations.Check(good, expected, false, TargetArchitecture.Arm64, "15.0");
        Assert.Equal(VerificationStatus.Passed, checks.Single(x => x.Name == "Minimum iOS (matches build)").Status);
        Assert.Contains("build-only minimum iOS 15.0", checks.Single(x => x.Name == "Minimum iOS (matches build)").Detail);
        Assert.Equal(VerificationStatus.Passed, checks.Single(x => x.Name == "Minimum iOS in executable").Status);
        var stale = IosArtifactExpectations.Check(good with { MinimumOs = "14.0", BinaryMinimumOs = ["14.0"] }, expected, false, TargetArchitecture.Arm64, "15.0");
        Assert.Equal(VerificationStatus.Failed, stale.Single(x => x.Name == "Minimum iOS (matches build)").Status);
        Assert.Equal(VerificationStatus.Failed, stale.Single(x => x.Name == "Minimum iOS in executable").Status);
    }

    [Fact]
    public void A_build_with_the_override_is_a_previous_build_once_the_override_is_removed()
    {
        var ios = Nexus();
        var project = new ProjectRecord
        {
            Id = Guid.NewGuid(), Name = "HCG Nexus", SourceKind = ProjectSourceKind.Directory, SourceLocation = NexusRepo(), WorkspacePath = _t.Combine("HCG Nexus"),
            ImportedAt = DateTimeOffset.UtcNow, Analysis = new ProjectAnalysis
            {
                InspectorId = "mobile", ProjectName = "HCGNexus", Language = "Objective-C", Framework = "Xcode", Runtime = "iOS", ApplicationType = "iOS application",
                Support = SupportLevel.Supported, SupportSummary = "", Mobile = Mobile(ios), AnalyzedAt = DateTimeOffset.UtcNow,
            },
        };
        BuildSettings Settings(string? raised) => new()
        {
            ProjectRelativePath = "HCGNexus.xcodeproj", Platform = TargetPlatform.IOS, Architecture = TargetArchitecture.Universal, Configuration = BuildConfigurationKind.Release,
            OutputDirectory = "/tmp/out", Identity = new AppIdentity { ApplicationName = "HCGNexus", Version = "0.2.0", ApplicationId = "com.hakmicapitalgroup.hcgnexus.app" },
            Mobile = new MobileBuildOptions { Framework = MobileFramework.NativeIos, Output = MobileOutput.IosArchive, Scheme = "HCGNexus", Signing = MobileSigningMode.Unsigned, DeploymentTargetOverride = raised },
        };
        var built = MobileBuildState.ProvenanceFor(project, Settings("15.0"), Report(Xcode()));
        Assert.Contains("minimum iOS raised to 15.0 for this build only", built.Describe());
        var now = MobileBuildState.ProvenanceFor(project, Settings(null), Report(Xcode()));
        Assert.Contains("Minimum iOS override: 15.0 (built) → — (now)", built.DifferencesFrom(now));
        var result = new BuildRecord
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, ProjectName = project.Name, Settings = Settings("15.0"), Status = BuildStatus.Succeeded,
            StartedAt = DateTimeOffset.UtcNow, Provenance = built,
        };
        Assert.False(MobileBuildState.DescribeResult(result, now).IsCurrent);
        Assert.True(MobileBuildState.DescribeResult(result, built).IsCurrent);
    }

    [Fact]
    public void Extensions_and_dependencies_are_inspected_and_raised_with_the_app()
    {
        var ios = AppWithExtension("14.0", swiftPackages: true);
        var plan = IosBuildSelection.Summarize(ios, null, null, Xcode()).RaiseOffer!;
        Assert.True(plan.Supported);
        Assert.Equal(["App", "Widget"], plan.Effects.Select(e => e.Target));
        Assert.All(plan.Effects, e => Assert.Equal(IosOverrideChange.Raised, e.Change));
        Assert.Equal(("app extension", true), (plan.Effects[1].Kind, plan.Effects[1].IsDependency));
        Assert.Contains(plan.Warnings, w => w.StartsWith("Embedded extensions (Widget) are raised together with the app", StringComparison.Ordinal));
        Assert.Contains(plan.Warnings, w => w.StartsWith("Swift package dependencies are built with IPHONEOS_DEPLOYMENT_TARGET=15.0 too", StringComparison.Ordinal));
        Assert.Contains("• Widget (app extension, dependency): iOS 14.0 → iOS 15.0", plan.Describe());
    }

    [Fact]
    public void A_build_wide_override_that_would_lower_a_target_is_refused()
    {
        // The app needs raising, but its extension already targets iOS 16.0: IPHONEOS_DEPLOYMENT_TARGET=15.0 would lower it.
        var ios = AppWithExtension("16.0");
        var summary = IosBuildSelection.Summarize(ios, null, null, Xcode());
        Assert.NotNull(summary.Problem);
        var plan = summary.RaiseOffer!;
        Assert.False(plan.Supported);
        Assert.Contains(plan.Blockers, b => b.StartsWith("Widget (app extension) targets iOS 16.0, which is newer than iOS 15.0", StringComparison.Ordinal));
        Assert.Equal(IosOverrideChange.Lowered, plan.Effects.Single(e => e.Target == "Widget").Change);

        // Even if such an override reached the build options, validation (and so the build) refuses it.
        var forced = IosSchemeCompatibility.Evaluate(ios, null, null, false, Xcode(), deploymentOverride: "15.0");
        Assert.Equal(IosToolchainFit.Incompatible, forced.Fit);
        Assert.StartsWith("The build-only minimum iOS 15.0 cannot be used", forced.Detail);
        Assert.All(Outputs(ios, Xcode(), null, raisedTo: "15.0"), o => Assert.False(o.IsAvailable));
    }

    [Fact]
    public void An_override_outside_the_sdk_range_or_malformed_is_refused()
    {
        var ios = Nexus();
        Assert.Contains(IosDeploymentOverride.Inspect(ios, "HCGNexus", null, Xcode(), "14.5").Blockers, b => b.StartsWith("iOS 14.5 is outside", StringComparison.Ordinal));
        Assert.Contains(IosDeploymentOverride.Inspect(ios, "HCGNexus", null, Xcode(), "15; rm -rf /").Blockers, b => b.Contains("is not an iOS version", StringComparison.Ordinal));
        Assert.Throws<BuildValidationException>(() => XcodePlanner.Resolve(
            new MobileBuildOptions { Framework = MobileFramework.NativeIos, Output = MobileOutput.IosArchive, Scheme = "HCGNexus", DeploymentTargetOverride = "15.0 OTHER=1" }, ios));
        Assert.Contains("nothing to raise", IosDeploymentOverride.Inspect(AppWithExtension("15.0"), null, null, Xcode(), "14.0").Blockers.Concat(
            IosDeploymentOverride.Inspect(Sentinel(), "HCGSentinelModern", null, Xcode(), "15.0").Blockers).Last());
    }

    // ───────────── Separate Simulator and device validation ─────────────

    [Fact]
    public void Simulator_and_device_are_validated_against_their_own_sdk()
    {
        var ios = Nexus();
        var x = Xcode(deviceMin: "15.0", simulatorMin: "13.0");
        var summary = IosBuildSelection.Summarize(ios, "HCGNexus", null, x);
        Assert.Equal("Project minimum: iOS 14.0. Selected toolchain minimum: iOS 15.0 (Xcode 27.0 · device SDK).", summary.Problem);
        Assert.Equal((false, true), (summary.DeviceFits, summary.SimulatorFits));
        var outputs = Outputs(ios, x, "HCGNexus");
        Assert.True(Available(outputs, MobileOutput.IosSimulatorApp));
        Assert.False(Available(outputs, MobileOutput.IosArchive));

        // The override must be accepted by BOTH SDKs: a Simulator SDK that starts at 16.0 makes 15.0 unusable.
        var split = Xcode(deviceMin: "15.0", simulatorMin: "16.0");
        Assert.Equal("16.0", IosDeploymentOverride.Suggest(ios, "HCGNexus", null, split));
        Assert.Contains(IosDeploymentOverride.Inspect(ios, "HCGNexus", null, split, "15.0").Blockers,
            b => b.StartsWith("iOS 15.0 is outside the selected Xcode's iPhoneSimulator", StringComparison.Ordinal));
    }

    // ───────────── Inspection failure versus confirmed incompatibility ─────────────

    [Fact]
    public void An_unreadable_sdk_range_is_an_inspection_failure_with_its_diagnostic_never_an_incompatibility()
    {
        var dev = _t.Combine("Broken.app", "Contents", "Developer");
        _t.Write(Path.GetRelativePath(_t.Path, Path.Combine(dev, "Platforms", "iPhoneOS.platform", "Developer", "SDKs", "iPhoneOS.sdk", "SDKSettings.json")), "{ not json");
        Assert.Null(XcodeDiscovery.ReadDeploymentRange(dev, "iphoneos", out var diagnostic));
        Assert.Contains("not valid JSON", diagnostic);

        var x = Xcode() with { IosDeploymentRange = null, RangeDiagnostics = new Dictionary<string, string> { ["iphoneos"] = diagnostic! } };
        var ios = Nexus();
        var device = IosSchemeCompatibility.Evaluate(ios, "HCGNexus", null, false, x);
        Assert.Equal(IosToolchainFit.NotVerifiable, device.Fit);
        Assert.True(device.IsInspectionFailure);
        Assert.Contains("not valid JSON", device.Detail);
        Assert.Contains("inspection failure, not an incompatibility", device.Detail);

        var summary = IosBuildSelection.Summarize(ios, "HCGNexus", null, x);
        // The Simulator SDK (readable, starts at 15.0) still confirms its incompatibility; the device SDK is only "not verified".
        Assert.EndsWith("(Xcode 27.0 · Simulator SDK).", summary.Problem);
        Assert.Contains(summary.InspectionNotices, n => n.StartsWith("Device (iPhoneOS SDK): not verified before building", StringComparison.Ordinal) && n.Contains("not valid JSON"));
        // A range HCG could not read is never a reason to raise anything: the override needs both ranges confirmed.
        Assert.False(summary.RaiseOffer!.Supported);
        Assert.Contains(summary.RaiseOffer.Blockers, b => b.Contains("could not be read") && b.Contains("not valid JSON"));
        // The device archive is not blocked by what could not be read.
        Assert.DoesNotContain(Outputs(ios, x, "HCGNexus").Single(o => o.Output == MobileOutput.IosArchive).Blockers, b => b.Tool == IosBuildSelection.DeploymentTargetItem);
    }

    // ───────────── Mixed Xcode / Theos repositories ─────────────

    [Fact]
    public async Task A_folder_with_an_xcode_app_and_a_theos_package_has_two_build_targets_that_never_mix()
    {
        var service = new ProjectInspectionService(
            new IProjectInspector[]
            {
                new DotNetProjectInspector(TimeProvider.System), new MobileProjectInspector(TimeProvider.System),
                new MobileArtifactProjectInspector(TimeProvider.System), new SwiftProjectInspector(TimeProvider.System),
            }.Concat(EcosystemSignatureInspector.CreateDefaults(TimeProvider.System)),
            TimeProvider.System);
        var analysis = await service.InspectAsync(NexusRepo(), Report(Xcode()), CancellationToken.None);

        // The Xcode app leads (iOS 14.0, judged against the selected Xcode); the Theos package is a separate target.
        var routes = BuildRoutes.For(analysis);
        Assert.Equal([BuildRoutes.XcodeIpa, BuildRoutes.TheosDeb], routes.Select(r => r.Id));
        Assert.Equal("14.0", analysis.Mobile!.Ios!.AppTargets.Single().DeploymentTarget);
        Assert.NotNull(IosBuildSelection.Summarize(analysis.Mobile.Ios, "HCGNexus", null, Xcode()).Problem);

        // The Theos package keeps its own deployment targets (TARGET iphone:clang:SDK:DEPLOYMENT) and is built with GNU Make.
        var theos = BuildRoutes.TheosProjects(analysis.Native);
        Assert.NotEmpty(theos);
        var schemes = theos.SelectMany(p => p.Theos!.Schemes).Where(s => s.ProjectError is null).ToList();
        Assert.Contains(schemes, s => s is { PackageScheme: "rootless", DeploymentTarget: "15.0" });
        Assert.Contains(schemes, s => s is { PackageScheme: "rootful", DeploymentTarget: "14.0" });
        var theosView = BuildRoutes.View(analysis, BuildRoutes.TheosDeb, Report(Xcode()));
        Assert.Equal("native", theosView.InspectorId);
        Assert.Null(theosView.Mobile);

        // Mobile Build Studio's build never packages the Theos project; the selection remembers which target was chosen.
        var settings = new BuildSettings
        {
            ProjectRelativePath = "", Platform = TargetPlatform.IOS, Architecture = TargetArchitecture.Arm64, Configuration = BuildConfigurationKind.Release, OutputDirectory = "/tmp/out",
            Identity = new AppIdentity { ApplicationName = "HCG Nexus", Version = "0.2.0", ApplicationId = "com.hakmicapitalgroup.hcgnexus" },
            PackagingFormats = [PackagingFormat.JailbreakDeb],
            Mobile = new MobileBuildOptions { Framework = MobileFramework.NativeIos, Output = MobileOutput.IosArchive },
        };
        Assert.Throws<BuildValidationException>(() => MobileBuildService.ValidateRoute(settings));
        var saved = new MobileSelection { ProjectId = "p", Scheme = "HCGNexus", BuildTarget = BuildRoutes.TheosDeb };
        Assert.Equal(BuildRoutes.TheosDeb, HcgJson.Deserialize<MobileSelection>(HcgJson.Serialize(saved))!.BuildTarget);
    }

    // ───────────── Switching while inspection is running ─────────────

    [Fact]
    public async Task A_compatibility_result_computed_for_an_earlier_scheme_is_discarded_when_the_scheme_changes_meanwhile()
    {
        var ios = Sentinel();
        var gate = new InspectionGate();
        IosCompatibilitySummary? shown = null;
        string? shownFor = null;
        var slow = new TaskCompletionSource();
        async Task SelectAsync(string scheme, Task delay)
        {
            var ticket = gate.Next();                    // the selection changes
            await delay;                                 // inspection still running (e.g. a re-inspection of the project)
            var summary = IosBuildSelection.Summarize(ios, scheme, null, Xcode());
            gate.TryApply(ticket, () => (shown, shownFor) = (summary, scheme));
        }
        var legacy = SelectAsync("HCGSentinel", slow.Task);
        await SelectAsync("HCGSentinelModern", Task.CompletedTask);
        slow.SetResult();                                // the older inspection finishes last
        await legacy;
        Assert.Equal("HCGSentinelModern", shownFor);
        Assert.Null(shown!.Problem);
    }
}
