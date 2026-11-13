using HCG.ExecutableMaker.BuildEngine;
using HCG.ExecutableMaker.BuildEngine.Mobile;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Infrastructure.Toolchains;
using HCG.ExecutableMaker.ProjectInspection;
using HCG.ExecutableMaker.ProjectInspection.DotNet;
using HCG.ExecutableMaker.ProjectInspection.Mobile;

namespace HCG.ExecutableMaker.Tests;

/// <summary>
/// HCG Mail's .NET for iOS head (detection through the imported props chain, repository-root references, readiness and
/// the build plan) and HCG Sentinel's deployment-target preflight, project-level xcodebuild errors and result bundles.
/// Fixtures are the real build definitions of both repositories.
/// </summary>
public class AppleProjectRoutesTests : IDisposable
{
    private readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    private static string FixtureDir(params string[] parts) => Path.Combine([Fixtures.RepoRoot, "tests", "Fixtures", .. parts]);

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), overwrite: true);
        foreach (var d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
    }

    /// <summary>The Mail fixture, imported as a whole repository whose path contains spaces.</summary>
    private string MailRepo()
    {
        var dir = _t.Combine("My Repos", "HCG Mail");
        if (!Directory.Exists(dir)) CopyDir(FixtureDir("DotNetIos", "HCG Mail like"), dir);
        return dir;
    }

    private static ProjectInspectionService Service() => new(
        new IProjectInspector[] { new DotNetProjectInspector(TimeProvider.System), new MobileProjectInspector(TimeProvider.System) }
            .Concat(HCG.ExecutableMaker.ProjectInspection.Ecosystems.EcosystemSignatureInspector.CreateDefaults(TimeProvider.System)),
        TimeProvider.System);

    // ------------------------------------------------------------------ Mail: detection

    [Fact]
    public void The_props_chain_is_followed_through_imports_with_MSBuildThisFileDirectory()
    {
        var repo = MailRepo();
        var ios = Path.Combine(repo, "iOS App Store Submission", "src", "HCG.Mail.iOS", "HCG.Mail.iOS.csproj");
        var file = MsBuildProjectFile.Parse(ios, [Path.Combine(repo, "iOS App Store Submission", "Directory.Build.props")], null, repo);
        Assert.Equal("net10.0-ios", file.Get("TargetFramework"));                  // the project overrides the imported net10.0
        Assert.Equal("0.1.0", file.Get("ApplicationDisplayVersion"));              // $(Version) from ../Windows-executable-Application/Directory.Build.props
        Assert.Equal("com.hakmicapitalgroup.hcgmail", file.Get("ApplicationId"));  // $(HcgBundleId)
        Assert.Contains(file.Imports, i => i.EndsWith(Path.Combine("Windows-executable-Application", "Directory.Build.props"), StringComparison.Ordinal));
        Assert.Empty(file.OutsideWorkspace);

        // The same project imported without the rest of the repository: the import is named, not read.
        var partial = MsBuildProjectFile.Parse(ios, [Path.Combine(repo, "iOS App Store Submission", "Directory.Build.props")], null, Path.Combine(repo, "iOS App Store Submission"));
        // Outside files are named relative to the import, with "/" on every host.
        Assert.Contains("../Windows-executable-Application/Directory.Build.props", partial.OutsideWorkspace);
        Assert.Null(partial.Get("Version"));
    }

    [Fact]
    public async Task At_the_repository_root_the_desktop_app_stays_the_build_and_the_ios_head_is_attached_for_mobile_build_studio()
    {
        var a = await Service().InspectAsync(MailRepo(), null, CancellationToken.None);
        Assert.Equal("dotnet", a.InspectorId);
        Assert.Equal("Windows-executable-Application/src/HCG.Mail.App/HCG.Mail.App.csproj", a.PrimaryProjectPath);   // desktop support preserved
        var head = a.Projects.Single(p => p.Name == "HCG.Mail.iOS");
        Assert.Equal(".NET for iOS application (Avalonia)", head.ApplicationType);
        Assert.False(head.IsRunnableApplication);                                   // never published as a desktop app
        Assert.DoesNotContain(a.Diagnostics, d => d.Message.Contains("not supported in M0.1", StringComparison.Ordinal) && d.Path == head.RelativePath);

        var ios = a.Mobile!.DotNetIos!;
        Assert.Equal(MobileFramework.DotNetIos, a.Mobile.Framework);
        Assert.Equal(("iOS App Store Submission/src/HCG.Mail.iOS/HCG.Mail.iOS.csproj", "net10.0-ios"), (ios.ProjectPath, ios.TargetFramework));
        Assert.Equal(("com.hakmicapitalgroup.hcgmail", "HCG Mail", "0.1.0", "1", "16.0"), (ios.ApplicationId, ios.ApplicationTitle, ios.DisplayVersion, ios.BuildNumber, ios.MinimumOS));
        Assert.Equal(("Avalonia", "11.3.22"), (ios.UiFramework, ios.UiFrameworkVersion));
        Assert.Equal(("26.2.10233/10.0.100", "iOS App Store Submission/scripts/workload-rollback.json", "10.0.100"), (ios.WorkloadPin, ios.WorkloadPinFile, ios.GlobalJsonSdk));
        Assert.Equal(
        [
            "iOS App Store Submission/src/HCG.Mail.Mobile/HCG.Mail.Mobile.csproj",
            "Windows-executable-Application/src/HCG.Mail.Core/HCG.Mail.Core.csproj", "Windows-executable-Application/src/HCG.Mail.Email/HCG.Mail.Email.csproj",
            "Windows-executable-Application/src/HCG.Mail.Infrastructure/HCG.Mail.Infrastructure.csproj", "Windows-executable-Application/src/HCG.Mail.Presentation/HCG.Mail.Presentation.csproj",
        ], ios.ProjectReferences);
        Assert.Empty(ios.OutsideWorkspace);
        Assert.Empty(ios.MissingReferences);
    }

    [Fact]
    public async Task Importing_only_the_submission_folder_is_a_configuration_problem_named_precisely_not_unsupported()
    {
        var a = await Service().InspectAsync(Path.Combine(MailRepo(), "iOS App Store Submission"), null, CancellationToken.None);
        Assert.Equal("mobile", a.InspectorId);
        Assert.Equal(SupportLevel.RequiresConfiguration, a.Support);
        Assert.Contains("Import the repository root", a.SupportSummary);
        Assert.Contains(a.Mobile!.DotNetIos!.OutsideWorkspace, o => o.EndsWith("HCG.Mail.Core.csproj", StringComparison.Ordinal));
        Assert.Contains(a.Diagnostics, d => d.Severity == DiagnosticSeverity.Error && d.Message.Contains("outside the imported folder"));
    }

    // ------------------------------------------------------------------ Mail: readiness and plan

    private static ToolchainReport MacReport(string? iosWorkload = "26.2.10233/10.0.100", bool mac = true, params SigningIdentity[] identities)
    {
        var rows = new List<ToolchainInfo>
        {
            new() { Id = ToolchainIds.DotNetSdk, DisplayName = ".NET SDK", Category = "Build", Status = ToolchainStatus.Installed, Version = "10.0.103", ExecutablePath = "/usr/local/share/dotnet/dotnet" },
        };
        if (iosWorkload is not null)
            rows.Add(new() { Id = ToolchainIds.DotNetIosWorkload, DisplayName = ".NET iOS workload", Category = "Mobile", Status = ToolchainStatus.Installed, Version = iosWorkload });
        var host = mac ? new HostEnvironment(TargetPlatform.MacOS, TargetArchitecture.Arm64, "macOS", ".NET", "osx") : new HostEnvironment(TargetPlatform.Linux, TargetArchitecture.X64, "Linux", ".NET", "linux");
        return new ToolchainReport(host, rows, null, null, DateTimeOffset.UtcNow)
        {
            Xcode = mac ? new XcodeStatus { State = XcodeState.Ready, ActiveDeveloperDirectory = "/Applications/Xcode_26.3.app/Contents/Developer", VerifiedVersion = "26.3", Diagnosis = "Ready", Sdks = ["iphoneos 26.2", "iphonesimulator 26.2"] } : null,
            AppleIdentities = identities,
        };
    }

    private async Task<MobileAnalysis> MailMobile() => (await Service().InspectAsync(MailRepo(), null, CancellationToken.None)).Mobile!;

    [Fact]
    public async Task Readiness_names_the_workload_pin_and_the_mac_requirement()
    {
        var m = await MailMobile();
        var ok = MobileReadiness.Evaluate(m, MobileOutput.IosSimulatorApp, new MobileBuildOptions { Framework = m.Framework, Output = MobileOutput.IosSimulatorApp }, MacReport());
        Assert.True(ok.CanBuild, string.Join("; ", ok.Blockers.Select(b => b.Tool + ": " + b.Detail)));
        Assert.Contains(ok.Items, i => i.Tool == "Project references" && i.State == ReadinessState.Ready && i.Detail.Contains("HCG.Mail.Core"));

        var missing = MobileReadiness.Evaluate(m, MobileOutput.IosSimulatorApp, null, MacReport(iosWorkload: null));
        var w = Assert.Single(missing.Blockers, b => b.Tool == ".NET iOS workload");
        Assert.Contains("--from-rollback-file", w.Fix);
        Assert.Contains("26.2.10233", w.Fix);

        var mismatch = MobileReadiness.Evaluate(m, MobileOutput.IosSimulatorApp, null, MacReport(iosWorkload: "26.0.9752/10.0.100"));
        Assert.Equal(ReadinessState.IncompatibleVersion, Assert.Single(mismatch.Blockers, b => b.Tool == ".NET iOS workload").State);

        var linux = MobileReadiness.Evaluate(m, MobileOutput.IosSimulatorApp, null, MacReport(mac: false));
        Assert.Contains(linux.Blockers, b => b.State == ReadinessState.OtherHost);
    }

    private MobilePlanInput Input(MobileAnalysis m, MobileBuildOptions o, ToolchainReport? report = null) => new()
    {
        Options = o, Analysis = m, SourceDirectory = MailRepo(), WorkDirectory = _t.Combine("hcg data", "builds", "b1", "work"),
        Toolchains = report ?? MacReport(), Environment = new Dictionary<string, string>(), ToolHomeDirectory = _t.Combine("toolhomes"),
    };

    [Fact]
    public async Task The_plan_uses_the_net_ios_workload_from_the_repository_with_separate_simulator_archive_and_signing_routes()
    {
        var m = await MailMobile();
        var project = Path.Combine(MailRepo(), "iOS App Store Submission", "src", "HCG.Mail.iOS", "HCG.Mail.iOS.csproj");

        var sim = DotNetIosPlanner.Plan(Input(m, new MobileBuildOptions { Framework = MobileFramework.DotNetIos, Output = MobileOutput.IosSimulatorApp }));
        var simArgs = Assert.Single(sim.Steps).Process.Arguments;
        Assert.Equal(["build", project, "-c", "Debug", "-r", "iossimulator-arm64"], simArgs.Take(6));
        Assert.Contains("net10.0-ios", simArgs);
        Assert.Equal(PackagingFormat.IosSimulatorApp, Assert.Single(sim.Outputs).Format);

        var unsigned = DotNetIosPlanner.Plan(Input(m, new MobileBuildOptions { Framework = MobileFramework.DotNetIos, Output = MobileOutput.IosArchive, Signing = MobileSigningMode.Unsigned }));
        var args = Assert.Single(unsigned.Steps).Process.Arguments;
        Assert.Equal(["publish", project, "-c", "Release", "-r", "ios-arm64", "-p:ArchiveOnBuild=true"], args.Take(7));
        Assert.Contains("-p:ArchiveDir=" + DotNetIosPlanner.ArchiveDirectory(Input(m, new MobileBuildOptions { Framework = MobileFramework.DotNetIos, Output = MobileOutput.IosArchive })), args);
        Assert.Contains("-p:EnableCodeSigning=false", args);
        Assert.Contains(unsigned.Notes, n => n.Contains("UNSIGNED archive"));
        Assert.Equal(Path.Combine(MailRepo(), "iOS App Store Submission", "src", "HCG.Mail.iOS"), unsigned.Steps[0].Process.WorkingDirectory);   // inside the whole repository

        var signed = DotNetIosPlanner.Plan(Input(m, new MobileBuildOptions
        {
            Framework = MobileFramework.DotNetIos, Output = MobileOutput.IosIpa, Signing = MobileSigningMode.AppleManual,
            SigningIdentity = "Apple Distribution: Hakmi Capital Group (ABCDE12345)", ProvisioningProfile = "HCG Mail App Store", DevelopmentTeam = "ABCDE12345", ExportMethod = IosExportMethod.AppStoreConnect,
        }));
        var sargs = signed.Steps[0].Process.Arguments;
        Assert.Contains("-p:CodesignKey=Apple Distribution: Hakmi Capital Group (ABCDE12345)", sargs);
        Assert.Contains("-p:CodesignProvision=HCG Mail App Store", sargs);
        Assert.Empty(signed.Outputs);                // an IPA only comes from the separate export stage, never from the archive step

        Assert.Throws<BuildValidationException>(() => DotNetIosPlanner.Plan(Input(m, new MobileBuildOptions
        {
            Framework = MobileFramework.DotNetIos, Output = MobileOutput.IosArchive, Signing = MobileSigningMode.AppleManual, SigningIdentity = "x\" ; rm -rf ~", ProvisioningProfile = "p",
        })));
        Assert.Throws<BuildValidationException>(() => DotNetIosPlanner.Plan(Input(m, new MobileBuildOptions { Framework = MobileFramework.DotNetIos, Output = MobileOutput.IosSimulatorApp }, MacReport(mac: false))));
        Assert.Throws<BuildValidationException>(() => DotNetIosPlanner.Plan(Input(m, new MobileBuildOptions { Framework = MobileFramework.DotNetIos, Output = MobileOutput.IosSimulatorApp }, MacReport(iosWorkload: null))));
    }

    // ------------------------------------------------------------------ Sentinel: deployment target preflight

    private static IosProjectInfo Sentinel(string root)
    {
        CopyDir(FixtureDir("XcodeDeployment", "Sentinel"), root);
        return XcodeProjectAnalyzer.Analyze(SourceIndex.Build(root, CancellationToken.None), "HCGSentinel.xcodeproj", out _);
    }

    [Fact]
    public void Sentinel_targets_and_schemes_carry_their_real_deployment_targets_and_archive_targets()
    {
        var ios = Sentinel(_t.Combine("sentinel src"));
        Assert.Equal("14.7", ios.AppTargets.Single(t => t.Name == "HCGSentinel").DeploymentTarget);
        Assert.Equal("15.0", ios.AppTargets.Single(t => t.Name == "HCGSentinelModern").DeploymentTarget);
        Assert.Equal(["HCGSentinel"], ios.Schemes.Single(s => s.Name == "HCGSentinel").ArchiveTargets);
        Assert.Equal(["HCGSentinelModern"], ios.Schemes.Single(s => s.Name == "HCGSentinelModern").ArchiveTargets);
        Assert.Equal("Release", ios.Schemes.Single(s => s.Name == "HCGSentinel").ArchiveConfiguration);
    }

    private static ToolchainReport Xcode(string version, IosDeploymentRange? range) =>
        new(new HostEnvironment(TargetPlatform.MacOS, TargetArchitecture.Arm64, "macOS", ".NET", "osx"), [], null, null, DateTimeOffset.UtcNow)
        {
            Xcode = new XcodeStatus
            {
                State = XcodeState.Ready, ActiveDeveloperDirectory = $"/Applications/Xcode_{version}.app/Contents/Developer", VerifiedVersion = version, Diagnosis = "Ready",
                Sdks = ["iphoneos 27.0", "iphonesimulator 27.0"], IosDeploymentRange = range,
            },
        };

    [Fact]
    public void A_deployment_target_outside_the_selected_xcodes_range_blocks_before_xcodebuild_and_names_the_routes_without_raising_it()
    {
        var ios = Sentinel(_t.Combine("sentinel"));
        var m = new MobileAnalysis { Framework = MobileFramework.NativeIos, Platforms = [TargetPlatform.IOS], Ios = ios };
        var archive = new MobileBuildOptions { Framework = MobileFramework.NativeIos, Output = MobileOutput.IosArchive, Scheme = "HCGSentinel", Signing = MobileSigningMode.Unsigned };

        // An Xcode whose iPhoneOS SDK supports 12.0 to 26.5.99 (Xcode 26.6, measured in Sentinel's CI): 14.7 is fine.
        var ok = MobileReadiness.Evaluate(m, MobileOutput.IosArchive, archive, Xcode("26.6", new IosDeploymentRange("12.0", "26.5.99", "15.0", "26.5")));
        Assert.Contains(ok.Items, i => i.Tool == "Deployment target" && i.State == ReadinessState.Ready && i.Detail.Contains("HCGSentinel: iOS 14.7"));

        // An Xcode whose range starts above 14.7: blocked, with the exact xcodebuild message, both routes, and no change to the project.
        var newer = Xcode("27.0", new IosDeploymentRange("15.0", "27.0.99", "16.0", "27.0"));
        var blocked = MobileReadiness.Evaluate(m, MobileOutput.IosArchive, archive, newer);
        var item = Assert.Single(blocked.Blockers, b => b.Tool == "Deployment target");
        Assert.Equal(ReadinessState.IncompatibleVersion, item.State);
        Assert.Contains("HCGSentinel (scheme HCGSentinel) needs iOS 14.7", item.Detail);
        Assert.Contains("supports iOS 15.0–27.0.99 (it is below the minimum)", item.Detail);
        Assert.Contains("is set to 14.7, but the range of supported deployment target versions is 15.0 to 27.0.99", item.Detail);
        Assert.Contains("does not change the project's deployment target", item.Detail);
        Assert.Contains("Select an Xcode that supports iOS 14.7 in Toolchains", item.Fix);
        Assert.Contains("or choose scheme HCGSentinelModern (iOS 15.0", item.Fix);

        var modern = MobileReadiness.Evaluate(m, MobileOutput.IosArchive, archive with { Scheme = "HCGSentinelModern" }, newer);
        Assert.Contains(modern.Items, i => i.Tool == "Deployment target" && i.State == ReadinessState.Ready);
    }

    [Fact]
    public void The_supported_range_is_read_from_the_sdk_settings_file()
    {
        const string json = """{"Version":"26.5","SupportedTargets":{"iphoneos":{"MinimumDeploymentTarget":"12.0","MaximumDeploymentTarget":"26.5.99","RecommendedDeploymentTarget":"15.0","DeploymentTargetSettingName":"IPHONEOS_DEPLOYMENT_TARGET"}}}""";
        Assert.Equal(new IosDeploymentRange("12.0", "26.5.99", "15.0", "26.5"), XcodeDiscovery.ParseIosDeploymentRange(json));
        Assert.Null(XcodeDiscovery.ParseIosDeploymentRange("{}"));
        var dev = _t.Combine("Xcode.app", "Contents", "Developer");
        var sdk = Path.Combine(dev, "Platforms", "iPhoneOS.platform", "Developer", "SDKs", "iPhoneOS.sdk");
        Directory.CreateDirectory(sdk);
        File.WriteAllText(Path.Combine(sdk, "SDKSettings.json"), json);
        Assert.Equal("26.5.99", XcodeDiscovery.ReadIosDeploymentRange(dev)!.Maximum);
        Assert.Null(XcodeDiscovery.ReadIosDeploymentRange(_t.Combine("nothing")));
        Assert.True(new IosDeploymentRange("12.0", "26.5.99", null, null).Contains("14.7"));
        Assert.False(new IosDeploymentRange("15.0", "27.0.99", null, null).Contains("14.7"));
    }

    // ------------------------------------------------------------------ Sentinel: the error itself and what is kept

    [Fact]
    public void The_project_level_xcodebuild_error_is_recognized_in_full_and_becomes_the_first_error()
    {
        const string line = "/Users/me/HCG data/workspaces/1/source/HCGSentinel.xcodeproj: error: The iOS deployment target 'IPHONEOS_DEPLOYMENT_TARGET' is set to 14.7, but the range of supported deployment target versions is 15.0 to 27.0.99. (in target 'HCGSentinel' from project 'HCGSentinel')";
        var m = new MobileOutputParser().Parse(line);
        Assert.NotNull(m);
        Assert.Equal(DiagnosticSeverity.Error, m!.Severity);
        Assert.Equal("/Users/me/HCG data/workspaces/1/source/HCGSentinel.xcodeproj", m.File);
        Assert.EndsWith("(in target 'HCGSentinel' from project 'HCGSentinel')", m.Text);   // nothing truncated

        Assert.Equal(m.Text[..20], MobileBuildService.FirstErrorOf(
            $"xcodebuild archive (HCGSentinel) failed with exit code 65.\nFirst error: {m.Text}\nComplete log: /x/build.log")![..20]);
    }

    [Fact]
    public void Archive_and_export_keep_their_result_bundles_with_the_log()
    {
        var root = _t.Combine("sentinel src");
        var ios = Sentinel(root);
        var input = new MobilePlanInput
        {
            Options = new MobileBuildOptions { Framework = MobileFramework.NativeIos, Output = MobileOutput.IosArchive, Scheme = "HCGSentinel", Signing = MobileSigningMode.Unsigned },
            Analysis = new MobileAnalysis { Framework = MobileFramework.NativeIos, Platforms = [TargetPlatform.IOS], Ios = ios },
            SourceDirectory = root, WorkDirectory = _t.Combine("work"), Toolchains = Xcode("26.6", null), Environment = new Dictionary<string, string>(), ToolHomeDirectory = _t.Combine("th"),
        };
        var args = Assert.Single(XcodePlanner.Plan(input).Steps).Process.Arguments;
        Assert.Equal(XcodePlanner.ResultBundle(input, "HCGSentinel", "archive"), args[args.ToList().IndexOf("-resultBundlePath") + 1]);
        Assert.DoesNotContain(args, a => a.StartsWith("IPHONEOS_DEPLOYMENT_TARGET", StringComparison.Ordinal));   // HCG never overrides the project's target
        Assert.StartsWith(input.WorkDirectory, XcodePlanner.ResultBundle(input, "HCGSentinel", "archive"));
    }

    [Fact]
    public void A_destination_error_carries_the_reason_xcodebuild_prints_on_the_following_lines()
    {
        // CI run 76, macos-15 runner: Xcode 16.0–16.3 are installed without their iOS platform component.
        var parser = new MobileOutputParser();
        string[] output =
        [
            "xcodebuild: error: Unable to find a destination matching the provided destination specifier:",
            "\t\t{ generic:1, platform:iOS }",
            "",
            "\tIneligible destinations for the \"HCGSentinel\" scheme:",
            "\t\t{ id:dvtdevice-DVTiPhonePlaceholder-iphoneos:placeholder, name:Any iOS Device, error:iOS 18.0 is not installed. To use with Xcode, first download and install the platform }",
            "** ARCHIVE FAILED **",
        ];
        var messages = output.Select(parser.Parse).Where(m => m is not null).ToList();
        var first = messages.First(m => m!.Severity == DiagnosticSeverity.Error)!;
        Assert.EndsWith("destination specifier:", first.Text);
        Assert.Equal(3, parser.DetailOf(first).Count);
        var described = parser.Describe(first);
        Assert.Contains("iOS 18.0 is not installed. To use with Xcode, first download and install the platform", described);
        Assert.Contains(messages, m => m!.Text.Contains("** ARCHIVE FAILED **"));
    }
}
