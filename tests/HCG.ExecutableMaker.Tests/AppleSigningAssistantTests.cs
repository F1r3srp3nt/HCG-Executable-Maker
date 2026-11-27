using HCG.ExecutableMaker.BuildEngine.Mobile;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.BuildEngine.Signing;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.ProjectInspection;
using HCG.ExecutableMaker.ProjectInspection.Mobile;
using Xunit;

namespace HCG.ExecutableMaker.Tests;

/// <summary>
/// Apple Signing Assistant, with Unimatch as the first regression case: automatic signing, no team committed
/// (Config/Base.xcconfig: DEVELOPMENT_TEAM =, overridable in Config/Local.xcconfig), PRODUCT_BUNDLE_IDENTIFIER from
/// $(UNIMATCH_BUNDLE_ID), and a Development Run compiled into Debug only.
/// </summary>
public sealed class AppleSigningAssistantTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);
    private const string Team = "AB12CD34EF";

    /// <summary>What `xcodebuild -showBuildSettings -json` prints for Unimatch's app target (abridged, values as measured).</summary>
    private const string UnimatchSettings = """
        Command line invocation:
            /Applications/Xcode.app/Contents/Developer/usr/bin/xcodebuild -project Unimatch.xcodeproj -scheme Unimatch -configuration Debug -sdk iphoneos -showBuildSettings -json
        [
          { "action": "build", "target": "Unimatch", "buildSettings": {
              "PRODUCT_TYPE": "com.apple.product-type.application", "WRAPPER_EXTENSION": "app",
              "PRODUCT_BUNDLE_IDENTIFIER": "no.unimatchub.app.dev", "DEVELOPMENT_TEAM": "", "CODE_SIGN_STYLE": "Automatic",
              "TARGETED_DEVICE_FAMILY": "1,2", "SWIFT_ACTIVE_COMPILATION_CONDITIONS": "DEBUG  UNIMATCH_DEVELOPMENT_RUN",
              "PROJECT_DIR": "/Users/owner/HCG/workspaces/u/source" } },
          { "action": "build", "target": "UnimatchKit", "buildSettings": { "PRODUCT_TYPE": "com.apple.product-type.library.static" } }
        ]
        """;

    private static XcodeTargetSettings App(string json = UnimatchSettings) => XcodeBuildSettings.App(XcodeBuildSettings.Parse(json))!;

    private static XcodeTargetSettings WithSettings(params (string Key, string Value)[] changes)
    {
        var app = App();
        var settings = new Dictionary<string, string>(app.Settings);
        foreach (var (k, v) in changes) settings[k] = v;
        return app with { Settings = settings };
    }

    private static AppleCertificateInfo Identity(AppleCertificateKind kind, string team = Team, bool key = true) => new()
    {
        Sha1 = (kind == AppleCertificateKind.Development ? "D" : "A") + new string('1', 39), Sha256 = new string('2', 64),
        CommonName = (kind == AppleCertificateKind.Development ? "Apple Development: Owner" : "Apple Distribution: Owner") + $" ({team})",
        Kind = kind, TeamId = team, NotBefore = Now.AddYears(-1), NotAfter = Now.AddYears(1), HasPrivateKey = key,
    };

    private static AppleProvisioningProfileInfo Profile(AppleProfileKind kind, string appId, params string[] devices) => new()
    {
        Uuid = Guid.NewGuid().ToString().ToUpperInvariant(), Name = $"{kind} {appId}", TeamIds = [Team], ApplicationIdentifier = $"{Team}.{appId}",
        Kind = kind, Signature = ProfileSignatureStatus.VerifiedApple, Expiration = Now.AddMonths(6), ProvisionedDevices = devices,
        CertificateSha1s = ["D" + new string('1', 39), "A" + new string('1', 39)],
    };

    private static MobileBuildOptions Options(MobileOutput output, MobileSigningMode signing = MobileSigningMode.ProjectDefault, string? team = null) =>
        new() { Framework = MobileFramework.NativeIos, Output = output, Signing = signing, DevelopmentTeam = team };

    private static AppleSigningPlan Plan(MobileBuildOptions o, string config, XcodeTargetSettings? app = null, ProjectSigningConfiguration? saved = null,
        IReadOnlyList<AppleCertificateInfo>? certs = null, IReadOnlyList<AppleProvisioningProfileInfo>? profiles = null, string? bundleVar = null) =>
        AppleSigningAssistant.Plan(new AppleSigningInput
        {
            Options = o, Configuration = config, App = app ?? App(), Saved = saved, Certificates = certs ?? [], Profiles = profiles ?? [],
            BundleIdentifierVariable = bundleVar, Now = Now,
        });

    [Fact]
    public void Unimatchs_effective_settings_are_read_from_xcodebuild_json()
    {
        var app = App();
        Assert.Equal("Unimatch", app.Target);
        Assert.Null(app.Team);
        Assert.Equal("Automatic", app.Style);
        Assert.Equal("no.unimatchub.app.dev", app.BundleIdentifier);
        Assert.Contains("UNIMATCH_DEVELOPMENT_RUN", app.CompilationConditions);
    }

    [Fact]
    public void A_simulator_Debug_build_needs_no_team_and_is_signed_to_run_locally_without_touching_the_project()
    {
        var plan = Plan(Options(MobileOutput.IosSimulatorApp), "Debug");
        Assert.True(plan.Ready);
        Assert.Equal(AppleBuildDestination.IosSimulatorDebug, plan.Destination);
        Assert.Equal(["CODE_SIGN_STYLE=Manual", "CODE_SIGN_IDENTITY=-", "DEVELOPMENT_TEAM=", "PROVISIONING_PROFILE_SPECIFIER="], plan.Arguments);
        Assert.Contains(plan.Notes, n => n.Contains("Development Run", StringComparison.Ordinal));
    }

    [Fact]
    public void A_simulator_build_of_a_project_with_its_own_team_keeps_the_projects_signing()
    {
        var plan = Plan(Options(MobileOutput.IosSimulatorApp), "Debug", WithSettings(("DEVELOPMENT_TEAM", Team)));
        Assert.True(plan.Ready);
        Assert.Empty(plan.Arguments);
    }

    [Fact]
    public void An_iPhone_Debug_build_without_a_team_stops_before_compiling_with_the_teams_this_Mac_can_sign_for()
    {
        var plan = Plan(Options(MobileOutput.IosDeviceApp), "Debug", certs: [Identity(AppleCertificateKind.Development)]);
        Assert.False(plan.Ready);
        var team = Assert.Single(plan.Failures);
        Assert.Equal(SigningCheckCategory.Team, team.Category);
        Assert.Contains("Signing for “Unimatch” requires a development team", team.Detail);
        Assert.Contains("Config/Local.xcconfig are not changed", team.Remedy);
        Assert.Equal([Team], plan.SuggestedTeams);
    }

    [Fact]
    public void A_chosen_team_with_a_development_identity_and_profile_builds_the_iPhone_Debug_app()
    {
        var plan = Plan(Options(MobileOutput.IosDeviceApp, MobileSigningMode.AppleAutomatic, Team), "Debug",
            certs: [Identity(AppleCertificateKind.Development)], profiles: [Profile(AppleProfileKind.Development, "no.unimatchub.app.dev")]);
        Assert.True(plan.Ready, plan.Summary);
        Assert.Equal(AppleBuildDestination.IPhoneDebug, plan.Destination);
        Assert.Equal(["DEVELOPMENT_TEAM=" + Team, "CODE_SIGN_STYLE=Automatic"], plan.Arguments);
        Assert.Contains(plan.Notes, n => n.Contains("Debug build: development-only functionality", StringComparison.Ordinal));
    }

    [Fact]
    public void A_team_from_Config_Local_xcconfig_is_used_as_the_project_sets_it_and_not_overridden()
    {
        var plan = Plan(Options(MobileOutput.IosDeviceApp), "Debug", WithSettings(("DEVELOPMENT_TEAM", Team)),
            certs: [Identity(AppleCertificateKind.Development)], profiles: [Profile(AppleProfileKind.Development, "*")]);
        Assert.True(plan.Ready, plan.Summary);
        Assert.Contains("xcconfig", plan.TeamSource);
        Assert.DoesNotContain(plan.Arguments, a => a.StartsWith("DEVELOPMENT_TEAM", StringComparison.Ordinal));
    }

    [Fact]
    public void The_team_saved_in_Signing_Vault_is_reused_for_later_builds()
    {
        var plan = Plan(Options(MobileOutput.IosDeviceApp, MobileSigningMode.AppleAutomatic), "Debug",
            saved: new ProjectSigningConfiguration { ProjectId = "p", TeamId = Team },
            certs: [Identity(AppleCertificateKind.Development)], profiles: [Profile(AppleProfileKind.Development, "no.unimatchub.*")]);
        Assert.True(plan.Ready, plan.Summary);
        Assert.Contains("Signing Vault", plan.TeamSource);
    }

    [Fact]
    public void Without_a_matching_profile_Xcode_managed_provisioning_must_be_authorized_and_then_registers_devices()
    {
        var refused = Plan(Options(MobileOutput.IosDeviceApp, MobileSigningMode.AppleAutomatic, Team), "Debug", certs: [Identity(AppleCertificateKind.Development)]);
        var profile = Assert.Single(refused.Failures);
        Assert.Equal(SigningCheckCategory.Profile, profile.Category);
        Assert.Contains("not authorized", profile.Detail);

        var authorized = Plan(Options(MobileOutput.IosDeviceApp, MobileSigningMode.AppleAutomatic, Team) with { AllowProvisioningUpdates = true }, "Debug",
            certs: [Identity(AppleCertificateKind.Development)]);
        Assert.True(authorized.Ready, authorized.Summary);
        Assert.Contains("-allowProvisioningUpdates", authorized.Arguments);
        Assert.Contains("-allowProvisioningDeviceRegistration", authorized.Arguments);
        Assert.Contains(authorized.Checks, c => c.Category == SigningCheckCategory.Profile && c.Outcome == SigningCheckOutcome.NotVerified && c.Scope == SigningCheckScope.RequiresApple);
    }

    [Fact]
    public void A_missing_development_identity_is_named_with_the_way_to_create_or_import_it()
    {
        var plan = Plan(Options(MobileOutput.IosDeviceApp, MobileSigningMode.AppleAutomatic, Team), "Debug",
            certs: [Identity(AppleCertificateKind.Development, key: false)], profiles: [Profile(AppleProfileKind.Development, "*")]);
        var c = Assert.Single(plan.Failures);
        Assert.Equal(SigningCheckCategory.PrivateKey, c.Category);
        Assert.Contains("Manage Certificates", c.Remedy);
    }

    [Fact]
    public void A_physical_device_cannot_be_targeted_unsigned()
    {
        var plan = Plan(Options(MobileOutput.IosDeviceApp, MobileSigningMode.Unsigned), "Debug");
        Assert.Contains("only runs apps signed", Assert.Single(plan.Failures).Detail);
    }

    [Fact]
    public void A_registered_device_is_checked_against_the_development_profile()
    {
        var saved = new ProjectSigningConfiguration { ProjectId = "p", TeamId = Team, IntendedDevices = ["00008110-000A", "00008120-000B"] };
        var plan = Plan(Options(MobileOutput.IosDeviceApp, MobileSigningMode.AppleAutomatic), "Debug", saved: saved,
            certs: [Identity(AppleCertificateKind.Development)], profiles: [Profile(AppleProfileKind.Development, "*", "00008110-000A")]);
        var devices = Assert.Single(plan.Checks, c => c.Category == SigningCheckCategory.Devices);
        Assert.Equal(SigningCheckOutcome.Warning, devices.Outcome);
        Assert.Contains("00008120-000B", devices.Detail);
    }

    [Fact]
    public void An_iPad_build_of_an_iPhone_only_app_is_flagged()
    {
        var plan = Plan(Options(MobileOutput.IosDeviceApp, MobileSigningMode.AppleAutomatic, Team) with { DeviceFamily = AppleDeviceFamily.IPad }, "Debug",
            WithSettings(("TARGETED_DEVICE_FAMILY", "1")), certs: [Identity(AppleCertificateKind.Development)], profiles: [Profile(AppleProfileKind.Development, "*")]);
        Assert.Equal(AppleBuildDestination.IPadDebug, plan.Destination);
        Assert.Contains(plan.Checks, c => c.Category == SigningCheckCategory.Platform && c.Outcome == SigningCheckOutcome.Warning);
    }

    [Theory]
    [InlineData(MobileOutput.IosArchive, null)]
    [InlineData(MobileOutput.IosIpa, IosExportMethod.AppStoreConnect)]
    public void A_Debug_distribution_build_is_refused_so_the_Development_Run_cannot_ship(MobileOutput output, IosExportMethod? export)
    {
        var o = Options(output, MobileSigningMode.AppleAutomatic, Team) with { ExportMethod = export, AllowProvisioningUpdates = true };
        var plan = Plan(o, "Debug", certs: [Identity(AppleCertificateKind.AppleDistribution)]);
        var config = Assert.Single(plan.Failures);
        Assert.Contains("must be a Release build", config.Detail);
        Assert.Contains("Development Run", config.Detail);

        var release = Plan(o, "Release", WithSettings(("SWIFT_ACTIVE_COMPILATION_CONDITIONS", "")), certs: [Identity(AppleCertificateKind.AppleDistribution)]);
        Assert.True(release.Ready, release.Summary);
        Assert.Contains(release.Checks, c => c.Title == "Build configuration" && c.Outcome == SigningCheckOutcome.Passed);
    }

    [Fact]
    public void A_Release_configuration_that_still_compiles_DEBUG_is_refused_for_distribution()
    {
        var plan = Plan(Options(MobileOutput.IosArchive, MobileSigningMode.AppleAutomatic, Team) with { AllowProvisioningUpdates = true }, "Release",
            certs: [Identity(AppleCertificateKind.AppleDistribution)]);
        Assert.Contains("compiles with DEBUG", Assert.Single(plan.Failures).Detail);
    }

    [Fact]
    public void TestFlight_and_App_Store_are_distinct_destinations_of_the_same_App_Store_Connect_export()
    {
        var o = Options(MobileOutput.IosIpa) with { ExportMethod = IosExportMethod.AppStoreConnect };
        Assert.Equal(AppleBuildDestination.TestFlight, AppleDestinations.Of(o));
        Assert.Equal(AppleBuildDestination.AppStore, AppleDestinations.Of(o with { Destination = AppleBuildDestination.AppStore }));
        Assert.Equal("Release", AppleDestinations.Of(o).DefaultConfiguration());
        Assert.Equal("Debug", AppleDestinations.Of(Options(MobileOutput.IosDeviceApp)).DefaultConfiguration());
        Assert.False(AppleDestinations.Of(Options(MobileOutput.IosSimulatorApp)).NeedsDeviceSigning());
    }

    [Fact]
    public void A_bundle_identifier_for_one_build_overrides_the_projects_variable_so_derived_targets_follow()
    {
        const string pbx = """
            PRODUCT_BUNDLE_IDENTIFIER = "$(UNIMATCH_BUNDLE_ID)";
            PRODUCT_BUNDLE_IDENTIFIER = "$(UNIMATCH_BUNDLE_ID)";
            PRODUCT_BUNDLE_IDENTIFIER = "$(UNIMATCH_BUNDLE_ID).uitests";
            """;
        Assert.Equal("UNIMATCH_BUNDLE_ID", XcodeBuildSettings.BundleIdentifierVariable(pbx));
        Assert.Null(XcodeBuildSettings.BundleIdentifierVariable("PRODUCT_BUNDLE_IDENTIFIER = com.example.app;"));
        var plan = Plan(Options(MobileOutput.IosSimulatorApp) with { BundleIdentifier = "com.owner.unimatch" }, "Debug", bundleVar: "UNIMATCH_BUNDLE_ID");
        Assert.Contains("UNIMATCH_BUNDLE_ID=com.owner.unimatch", plan.Arguments);
        Assert.Equal("com.owner.unimatch", plan.BundleIdentifier);
    }

    [Fact]
    public void Manual_signing_names_why_a_profile_does_not_fit()
    {
        var wrong = Profile(AppleProfileKind.AppStore, "com.other.app");
        var plan = Plan(Options(MobileOutput.IosDeviceApp, MobileSigningMode.AppleManual, Team) with { ProvisioningProfile = wrong.Name }, "Debug",
            certs: [Identity(AppleCertificateKind.Development)], profiles: [wrong]);
        var f = Assert.Single(plan.Failures, c => c.Category == SigningCheckCategory.Profile);
        Assert.Contains("AppStore profile", f.Detail);
        Assert.Contains("does not cover no.unimatchub.app.dev", f.Detail);
    }

    [Fact]
    public void Xcodes_signing_errors_become_Resolve_Signing_with_the_exact_line()
    {
        var failures = AppleSigningFailures.Classify(
        [
            "/Users/owner/HCG/workspaces/u/source/Unimatch.xcodeproj: error: Signing for \"Unimatch\" requires a development team. Select a development team in the Signing & Capabilities editor. (in target 'Unimatch' from project 'Unimatch')",
            "error: No profiles for 'no.unimatchub.app' were found: Xcode couldn't find any iOS App Development provisioning profiles matching 'no.unimatchub.app'.",
            "note: Building targets in dependency order",
        ]);
        Assert.Equal([SigningCheckCategory.Team, SigningCheckCategory.Profile], failures.Select(f => f.Category));
        Assert.Contains("requires a development team", failures[0].Explanation);
        Assert.StartsWith("Resolve Signing", failures[0].Remedy);
        Assert.Empty(AppleSigningFailures.Classify(["error: cannot find 'Foo' in scope"]));
    }

    // ---- planner: the physical-device build ----

    private static string FixtureDir(string name)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "tests", "Fixtures", name))) return Path.Combine(d.FullName, "tests", "Fixtures", name);
        throw new DirectoryNotFoundException(name);
    }

    private static MobilePlanInput Input(MobileBuildOptions o, IReadOnlyList<string>? signing = null, string? configuration = null)
    {
        var root = FixtureDir("SigningProbe");
        var ios = XcodeProjectAnalyzer.Analyze(SourceIndex.Build(root, CancellationToken.None), "SigningProbe.xcodeproj", out _);
        return new MobilePlanInput
        {
            Options = o, Analysis = new MobileAnalysis { Framework = MobileFramework.NativeIos, Platforms = [TargetPlatform.IOS], Ios = ios },
            SourceDirectory = root, WorkDirectory = Path.Combine(Path.GetTempPath(), "hcg-signing-plan"), Environment = new Dictionary<string, string>(),
            ToolHomeDirectory = Path.GetTempPath(), SigningArguments = signing, Configuration = configuration,
            Toolchains = new ToolchainReport(new HostEnvironment(TargetPlatform.MacOS, TargetArchitecture.Arm64, "macOS", ".NET", "osx"), [], null, null, DateTimeOffset.UtcNow),
        };
    }

    [Fact]
    public void The_device_build_compiles_for_iphoneos_in_Debug_with_the_assistants_signing_and_collects_the_device_app()
    {
        var plan = XcodePlanner.Plan(Input(Options(MobileOutput.IosDeviceApp), ["DEVELOPMENT_TEAM=" + Team, "CODE_SIGN_STYLE=Automatic"]));
        var step = Assert.Single(plan.Steps);
        var args = step.Process.Arguments.ToList();
        Assert.Equal("Debug", args[args.IndexOf("-configuration") + 1]);
        Assert.Equal("iphoneos", args[args.IndexOf("-sdk") + 1]);
        Assert.Equal("generic/platform=iOS", args[args.IndexOf("-destination") + 1]);
        Assert.Contains("DEVELOPMENT_TEAM=" + Team, args);
        Assert.Equal("build", args[^1]);
        var output = Assert.Single(plan.Outputs);
        Assert.Equal(PackagingFormat.IosDeviceApp, output.Format);
        Assert.EndsWith("Debug-iphoneos", output.SearchRoot);
    }

    [Fact]
    public void An_explicit_Release_choice_is_kept_for_a_device_build_and_the_simulator_gets_the_assistants_settings()
    {
        var release = XcodePlanner.Plan(Input(Options(MobileOutput.IosDeviceApp) with { Variant = "Release" }, [], "Release"));
        var args = Assert.Single(release.Steps).Process.Arguments.ToList();
        Assert.Equal("Release", args[args.IndexOf("-configuration") + 1]);

        var sim = XcodePlanner.Plan(Input(Options(MobileOutput.IosSimulatorApp), ["CODE_SIGN_IDENTITY=-"]));
        var simArgs = Assert.Single(sim.Steps).Process.Arguments.ToList();
        Assert.Contains("CODE_SIGN_IDENTITY=-", simArgs);
        Assert.Equal("build", simArgs[^1]);
    }

    [Fact]
    public void The_SigningProbe_fixture_is_shaped_like_Unimatch()
    {
        var root = FixtureDir("SigningProbe");
        var pbx = File.ReadAllText(Path.Combine(root, "SigningProbe.xcodeproj", "project.pbxproj"));
        Assert.Equal("PROBE_BUNDLE_ID", XcodeBuildSettings.BundleIdentifierVariable(pbx));
        // Windows checkouts may convert the fixture to CRLF; the content is what matters.
        var xcconfig = File.ReadAllText(Path.Combine(root, "Config", "Base.xcconfig")).ReplaceLineEndings("\n");
        Assert.Contains("DEVELOPMENT_TEAM =\n", xcconfig);
        Assert.Contains("SWIFT_ACTIVE_COMPILATION_CONDITIONS[config=Debug]", xcconfig);
        Assert.Contains("#include? \"Local.xcconfig\"", xcconfig);
        var m = new MobileAnalysis { Framework = MobileFramework.NativeIos, Platforms = [TargetPlatform.IOS], Ios = Input(Options(MobileOutput.IosDeviceApp)).Analysis.Ios };
        Assert.Contains(MobileCapabilities.Evaluate(m, null), o => o.Output == MobileOutput.IosDeviceApp);
    }
}

/// <summary>
/// What a build is, verified before compiling: Unimatch's Debug build must have DEBUG and UNIMATCH_DEVELOPMENT_RUN,
/// its Release archives must have neither, and the imported copy must be the expected, current commit.
/// </summary>
public sealed class DevelopmentBuildVerificationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("hcg-devverify-").FullName;
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private static XcodeTargetSettings App(string conditions) => new("Unimatch", new Dictionary<string, string>
    {
        ["PRODUCT_TYPE"] = "com.apple.product-type.application", ["SWIFT_ACTIVE_COMPILATION_CONDITIONS"] = conditions,
    });

    // Measured shape of Unimatch's settings (Base.xcconfig: [config=Debug] adds UNIMATCH_DEVELOPMENT_RUN).
    private static readonly XcodeTargetSettings UnimatchDebug = App("DEBUG  UNIMATCH_DEVELOPMENT_RUN");
    private static readonly XcodeTargetSettings UnimatchRelease = App("");

    [Fact]
    public void Unimatchs_development_only_flag_is_found_by_comparing_Debug_with_Release()
    {
        Assert.Equal(["UNIMATCH_DEVELOPMENT_RUN"], DevelopmentBuildVerification.DevelopmentOnlyFlags(UnimatchDebug, UnimatchRelease));
        Assert.Empty(DevelopmentBuildVerification.DevelopmentOnlyFlags(UnimatchDebug, null));
    }

    [Fact]
    public void The_iPhone_Debug_build_verifies_DEBUG_and_UNIMATCH_DEVELOPMENT_RUN_are_active()
    {
        var checks = DevelopmentBuildVerification.CompilationFlags(AppleBuildDestination.IPhoneDebug, "Debug", UnimatchDebug,
            ["UNIMATCH_DEVELOPMENT_RUN"], ["DEBUG", "UNIMATCH_DEVELOPMENT_RUN"]);
        Assert.All(checks, c => Assert.Equal(SigningCheckOutcome.Passed, c.Outcome));
        Assert.Contains(checks, c => c.Title == "Development-only flags" && c.Detail.Contains("UNIMATCH_DEVELOPMENT_RUN"));
    }

    [Fact]
    public void A_Release_device_build_explains_why_the_Development_Run_is_missing_and_expected_flags_fail()
    {
        var checks = DevelopmentBuildVerification.CompilationFlags(AppleBuildDestination.IPhoneDebug, "Release", UnimatchRelease,
            ["UNIMATCH_DEVELOPMENT_RUN"], ["DEBUG", "UNIMATCH_DEVELOPMENT_RUN"]);
        var dev = Assert.Single(checks, c => c.Title == "Development-only flags");
        Assert.Equal(SigningCheckOutcome.Warning, dev.Outcome);
        Assert.Contains("Release builds never include them", dev.Detail);
        var expected = Assert.Single(checks, c => c.Title == "Expected flags");
        Assert.Equal(SigningCheckOutcome.Failed, expected.Outcome);
        Assert.Contains("DEBUG, UNIMATCH_DEVELOPMENT_RUN", expected.Detail);
    }

    [Fact]
    public void A_local_opt_out_of_the_Development_Run_in_Debug_is_reported()
    {
        var checks = DevelopmentBuildVerification.CompilationFlags(AppleBuildDestination.IPhoneDebug, "Debug", App("DEBUG"), ["UNIMATCH_DEVELOPMENT_RUN"], []);
        var dev = Assert.Single(checks, c => c.Title == "Development-only flags");
        Assert.Equal(SigningCheckOutcome.Warning, dev.Outcome);
        Assert.Contains("Config/Local.xcconfig", dev.Detail);
    }

    [Fact]
    public void A_public_Release_archive_never_carries_the_development_flags()
    {
        var clean = DevelopmentBuildVerification.CompilationFlags(AppleBuildDestination.TestFlight, "Release", UnimatchRelease, ["UNIMATCH_DEVELOPMENT_RUN"], []);
        Assert.Equal(SigningCheckOutcome.Passed, Assert.Single(clean).Outcome);
        var leaked = DevelopmentBuildVerification.CompilationFlags(AppleBuildDestination.IosDistributionArchive, "Release", App("UNIMATCH_DEVELOPMENT_RUN"), ["UNIMATCH_DEVELOPMENT_RUN"], []);
        Assert.Equal(SigningCheckOutcome.Failed, Assert.Single(leaked).Outcome);
    }

    [Fact]
    public void A_development_ipa_is_a_device_development_build_and_defaults_to_Debug()
    {
        var o = new MobileBuildOptions { Framework = MobileFramework.NativeIos, Output = MobileOutput.IosIpa, ExportMethod = IosExportMethod.Debugging };
        Assert.Equal(AppleBuildDestination.IPhoneDebug, AppleDestinations.Of(o));
        Assert.Equal("Debug", AppleDestinations.Of(o).DefaultConfiguration());
        Assert.False(AppleDestinations.Of(o).IsDistribution());
        Assert.True(AppleDestinations.Of(o with { ExportMethod = IosExportMethod.AppStoreConnect }).IsDistribution());
    }

    private ProjectRecord GitProject(string commit)
    {
        var src = Path.Combine(_root, "unimatch");
        Directory.CreateDirectory(Path.Combine(src, ".git", "refs", "heads"));
        File.WriteAllText(Path.Combine(src, ".git", "HEAD"), "ref: refs/heads/main\n");
        File.WriteAllText(Path.Combine(src, ".git", "refs", "heads", "main"), commit + "\n");
        var ws = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(ws);
        var p = new ProjectRecord { Id = Guid.NewGuid(), Name = "Unimatch", SourceKind = ProjectSourceKind.Directory, SourceLocation = src, WorkspacePath = ws, ImportedAt = DateTimeOffset.UtcNow };
        ImportRevisions.WriteAsync(p, ImportRevisions.Capture(p, TimeProvider.System), CancellationToken.None).GetAwaiter().GetResult();
        return p;
    }

    private const string Commit74229fe = "74229fe930574972e4b4bddeef6078cdc47bdd54";

    [Fact]
    public void The_source_commit_is_verified_current_and_as_expected()
    {
        var p = GitProject(Commit74229fe);
        var checks = DevelopmentBuildVerification.Source(p, "74229fe");
        Assert.All(checks, c => Assert.Equal(SigningCheckOutcome.Passed, c.Outcome));
        Assert.Contains(checks, c => c.Title == "Expected commit" && c.Detail.Contains("74229fe93057"));
        Assert.Equal(SigningCheckOutcome.Failed, Assert.Single(DevelopmentBuildVerification.Source(p, "0123456"), c => c.Title == "Expected commit").Outcome);
    }

    [Fact]
    public void A_stale_imported_copy_is_refused_so_old_code_is_never_installed_as_current()
    {
        var p = GitProject("1111111111111111111111111111111111111111");
        File.WriteAllText(Path.Combine(p.SourceLocation, ".git", "refs", "heads", "main"), Commit74229fe + "\n"); // the user pulled 74229fe after importing
        var source = Assert.Single(DevelopmentBuildVerification.Source(p, null));
        Assert.Equal(SigningCheckOutcome.Failed, source.Outcome);
        Assert.Contains("STALE", source.Detail);
        Assert.Contains("Re-import", source.Remedy);
    }
}
