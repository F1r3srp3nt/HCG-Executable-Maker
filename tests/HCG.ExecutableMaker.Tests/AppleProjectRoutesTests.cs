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
    public void An_xcode_project_that_only_wraps_another_build_is_not_a_mobile_app_project()
    {
        var mail = SourceIndex.Build(MailRepo(), CancellationToken.None);
        Assert.True(XcodeProjectAnalyzer.WrapsExternalBuild(mail, "iOS App Store Submission/HCGMail.xcodeproj"));
        Assert.Empty(XcodeProjectAnalyzer.FindMobileProjects(mail));
        Assert.Equal(["iOS App Store Submission/HCGMail.xcodeproj"], XcodeProjectAnalyzer.FindExternalBuildWrappers(mail));

        // A real iOS app project (native target) is still found.
        var hello = SourceIndex.Build(FixtureDir("HelloIOS"), CancellationToken.None);
        Assert.False(XcodeProjectAnalyzer.WrapsExternalBuild(hello, "HelloIOS.xcodeproj"));
        Assert.Equal(["HelloIOS.xcodeproj"], XcodeProjectAnalyzer.FindMobileProjects(hello));
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

        // The .NET iOS SDK archives only with code signing (CI run 78: "Code signing must be enabled to create an Xcode
        // archive"), so an unsigned archive is refused before anything runs — and readiness says why.
        var refused = Assert.Throws<BuildValidationException>(() => DotNetIosPlanner.Plan(Input(m, new MobileBuildOptions { Framework = MobileFramework.DotNetIos, Output = MobileOutput.IosArchive, Signing = MobileSigningMode.Unsigned })));
        Assert.Contains("Code signing must be enabled", refused.Message);
        var unsignedReadiness = MobileReadiness.Evaluate(m, MobileOutput.IosArchive, new MobileBuildOptions { Framework = MobileFramework.DotNetIos, Output = MobileOutput.IosArchive, Signing = MobileSigningMode.Unsigned }, MacReport());
        Assert.Contains(unsignedReadiness.Blockers, b => b.Tool == "Code signing" && b.Detail.Contains("only with code signing"));

        var signed = DotNetIosPlanner.Plan(Input(m, new MobileBuildOptions
        {
            Framework = MobileFramework.DotNetIos, Output = MobileOutput.IosIpa, Signing = MobileSigningMode.AppleManual,
            SigningIdentity = "Apple Distribution: Hakmi Capital Group (ABCDE12345)", ProvisioningProfile = "HCG Mail App Store", DevelopmentTeam = "ABCDE12345", ExportMethod = IosExportMethod.AppStoreConnect,
        }));
        var sargs = Assert.Single(signed.Steps).Process.Arguments;   // one SDK invocation; no xcodebuild -exportArchive for .NET
        Assert.Contains("-p:CodesignKey=Apple Distribution: Hakmi Capital Group (ABCDE12345)", sargs);
        Assert.Contains("-p:CodesignProvision=HCG Mail App Store", sargs);
        // The IPA is the .NET iOS SDK's own (BuildIpa), at an exact per-build path; the build service takes the archive and
        // the IPA from their exact paths (no output search).
        var signedInput = Input(m, new MobileBuildOptions { Framework = MobileFramework.DotNetIos, Output = MobileOutput.IosIpa, Signing = MobileSigningMode.AppleManual, SigningIdentity = "x", ProvisioningProfile = "y" });
        Assert.Empty(signed.Outputs);
        Assert.Contains("-p:BuildIpa=true", sargs);
        Assert.Contains("-p:IpaPackagePath=" + DotNetIosPlanner.IpaPath(signedInput), sargs);
        Assert.EndsWith(".ipa", DotNetIosPlanner.IpaPath(signedInput));
        Assert.StartsWith(_t.Combine("hcg data", "builds", "b1", "work"), DotNetIosPlanner.IpaPath(signedInput));

        var archiveOptions = new MobileBuildOptions { Framework = MobileFramework.DotNetIos, Output = MobileOutput.IosArchive, Signing = MobileSigningMode.AppleManual, SigningIdentity = "Apple Development: Jane (ABCDE12345)", ProvisioningProfile = "HCG Mail Dev" };
        var archive = DotNetIosPlanner.Plan(Input(m, archiveOptions));
        var args = Assert.Single(archive.Steps).Process.Arguments;
        Assert.Equal(["publish", project, "-c", "Release", "-r", "ios-arm64"], args.Take(6));
        Assert.Contains("-p:ArchiveOnBuild=true", args);
        Assert.Contains("-p:BuildIpa=false", args);
        Assert.DoesNotContain(args, a => a.StartsWith("-p:IpaPackagePath=", StringComparison.Ordinal));
        // Regression (HCG Mail on Xcode 27.2): the SDK's Archive task writes the archive INTO ArchiveDir, so ArchiveDir must be
        // the .xcarchive itself; a parent folder produced an archive no search for "*.xcarchive" could find.
        var archiveDir = args.Single(a => a.StartsWith("-p:ArchiveDir=", StringComparison.Ordinal))["-p:ArchiveDir=".Length..];
        Assert.EndsWith(".xcarchive", archiveDir);
        Assert.Equal(DotNetIosPlanner.ArchivePath(Input(m, archiveOptions)), archiveDir);
        Assert.Equal(DotNetIosPlanner.ArchiveDirectory(Input(m, archiveOptions)), Path.GetDirectoryName(archiveDir));
        Assert.Equal("HCGMail.iOS.xcarchive", Path.GetFileName(archiveDir));   // the AssemblyName, as the SDK names HCGMail.iOS.app (HCG Mail PR #28)
        Assert.Empty(archive.Outputs);
        Assert.Contains(args, a => a.StartsWith("-p:OutputPath=", StringComparison.Ordinal) && a.Contains(DotNetIosPlanner.DeviceDirectory(Input(m, archiveOptions)), StringComparison.Ordinal));
        Assert.DoesNotContain("-p:EnableCodeSigning=false", args);
        Assert.Equal(Path.Combine(MailRepo(), "iOS App Store Submission", "src", "HCG.Mail.iOS"), archive.Steps[0].Process.WorkingDirectory);   // inside the whole repository

        Assert.Throws<BuildValidationException>(() => DotNetIosPlanner.Plan(Input(m, new MobileBuildOptions
        {
            Framework = MobileFramework.DotNetIos, Output = MobileOutput.IosArchive, Signing = MobileSigningMode.AppleManual, SigningIdentity = "x\" ; rm -rf ~", ProvisioningProfile = "p",
        })));
        Assert.Throws<BuildValidationException>(() => DotNetIosPlanner.Plan(Input(m, new MobileBuildOptions { Framework = MobileFramework.DotNetIos, Output = MobileOutput.IosSimulatorApp }, MacReport(mac: false))));
        Assert.Throws<BuildValidationException>(() => DotNetIosPlanner.Plan(Input(m, new MobileBuildOptions { Framework = MobileFramework.DotNetIos, Output = MobileOutput.IosSimulatorApp }, MacReport(iosWorkload: null))));
    }

    // ------------------------------------------------------------------ .NET for iOS: routes, effective properties, signing

    private static MobileBuildOptions Manual(MobileOutput output, string? variant = null, IosExportMethod? method = null) => new()
    {
        Framework = MobileFramework.DotNetIos, Output = output, Variant = variant, ExportMethod = method, Signing = MobileSigningMode.AppleManual,
        SigningIdentity = "Apple Development: Jane (ABCDE12345)", ProvisioningProfile = "Wildcard Development",
    };

    [Fact]
    public async Task Each_dotnet_ios_output_is_its_own_route_with_its_own_folder()
    {
        var m = await MailMobile();
        // Simulator Release (Debug is the default).
        var simRelease = DotNetIosPlanner.Plan(Input(m, new MobileBuildOptions { Framework = MobileFramework.DotNetIos, Output = MobileOutput.IosSimulatorApp, Variant = "Release" }));
        Assert.Equal(["-c", "Release", "-r", "iossimulator-arm64"], simRelease.Steps[0].Process.Arguments.Skip(2).Take(4));

        // Signed device app, Debug by default, Release on request: dotnet build -r ios-arm64, no archive, no IPA.
        foreach (var (variant, expected) in new[] { ((string?)null, "Debug"), ("Release", "Release") })
        {
            var input = Input(m, Manual(MobileOutput.IosDeviceApp, variant));
            var device = DotNetIosPlanner.Plan(input);
            var args = Assert.Single(device.Steps).Process.Arguments;
            Assert.Equal(["build", "-c", expected, "-r", "ios-arm64", "-o", DotNetIosPlanner.DeviceDirectory(input)], new[] { args[0] }.Concat(args.Skip(2).Take(6)));
            Assert.Contains("-p:CodesignKey=Apple Development: Jane (ABCDE12345)", args);
            Assert.DoesNotContain(args, a => a.StartsWith("-p:ArchiveOnBuild", StringComparison.Ordinal) || a.StartsWith("-p:BuildIpa", StringComparison.Ordinal));
            Assert.Equal(PackagingFormat.IosDeviceApp, Assert.Single(device.Outputs).Format);
        }
        Assert.Throws<BuildValidationException>(() => DotNetIosPlanner.Plan(Input(m, new MobileBuildOptions { Framework = MobileFramework.DotNetIos, Output = MobileOutput.IosDeviceApp, Signing = MobileSigningMode.Unsigned })));

        // A distribution IPA is Release only; a development IPA may be Debug.
        Assert.Contains("must be a Release build", Assert.Throws<BuildValidationException>(() => DotNetIosPlanner.Plan(Input(m, Manual(MobileOutput.IosIpa, "Debug", IosExportMethod.AppStoreConnect)))).Message);
        Assert.Contains("-p:BuildIpa=true", DotNetIosPlanner.Plan(Input(m, Manual(MobileOutput.IosIpa, "Debug", IosExportMethod.Debugging))).Steps[0].Process.Arguments);
        Assert.Contains("wildcard", DotNetIosPlanner.Explain(MobileOutput.IosDeviceApp, null));
        Assert.Contains("not a wildcard", DotNetIosPlanner.Explain(MobileOutput.IosIpa, IosExportMethod.AppStoreConnect));
    }

    [Fact]
    public async Task The_effective_msbuild_properties_are_read_with_the_builds_own_settings_and_checked_before_compiling()
    {
        var m = await MailMobile();
        var input = Input(m, Manual(MobileOutput.IosIpa));
        var plan = DotNetIosPlanner.Plan(input);
        var probe = DotNetIosPlanner.PropertyProbe(input, plan)!;
        var a = probe.Process.Arguments;
        Assert.Equal("msbuild", a[0]);
        Assert.Contains("-p:Configuration=Release", a);
        Assert.Contains("-p:RuntimeIdentifier=ios-arm64", a);
        Assert.Contains("-p:ArchiveDir=" + DotNetIosPlanner.ArchivePath(input), a);       // the same global properties as the build
        Assert.Contains("-p:CodesignProvision=Wildcard Development", a);
        Assert.Contains("-getProperty:ArchiveDir", a);
        Assert.Contains("-getProperty:BuildIpa", a);
        Assert.DoesNotContain(a, x => x is "publish" or "build");                          // evaluation only
        // dotnet msbuild has no -f: MSBuild rejects it (MSB1001), the probe printed nothing and the real HCG Mail build
        // went on unchecked (CI run 46112257158). The framework goes in as the property -f sets.
        var build = plan.Steps[0].Process.Arguments.ToList();
        Assert.DoesNotContain("-f", a);
        Assert.Contains("-p:TargetFramework=" + build[build.IndexOf("-f") + 1], a);
        Assert.Null(DotNetIosPlanner.PropertyProbe(Input(m, new MobileBuildOptions { Framework = MobileFramework.DotNetIos, Output = MobileOutput.IosSimulatorApp }), plan));

        var json = $$"""
            {
              "Properties": {
                "OutputType": "Exe", "ApplicationId": "com.hakmicapitalgroup.hcgmail", "ArchiveOnBuild": "true",
                "ArchiveDir": "{{DotNetIosPlanner.ArchivePath(input).Replace("\\", "\\\\")}}", "BuildIpa": "true",
                "CodesignKey": "Apple Development: Jane (ABCDE12345)", "CodesignProvision": "Wildcard Development", "RuntimeIdentifiers": ""
              }
            }
            """;
        var props = DotNetIosPlanner.ParseProperties("Some MSBuild banner\n" + json);
        Assert.Equal("true", props["BuildIpa"]);
        Assert.DoesNotContain(DotNetIosPlanner.CheckProperties(props, input.Options, m.DotNetIos!), f => f.Severity == DiagnosticSeverity.Error);

        // What the old adapter did, and what a library or a project overriding the properties would do: refused before compiling.
        var bad = new Dictionary<string, string>(props) { ["ArchiveDir"] = "/w/archives", ["BuildIpa"] = "false", ["OutputType"] = "Library" };
        var errors = DotNetIosPlanner.CheckProperties(bad, input.Options, m.DotNetIos!).Where(f => f.Severity == DiagnosticSeverity.Error).Select(f => f.Text).ToList();
        Assert.Contains(errors, e => e.Contains("must be the .xcarchive", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("would make no IPA", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("not Exe", StringComparison.Ordinal));
        Assert.Contains(DotNetIosPlanner.CheckProperties(new Dictionary<string, string>(), input.Options, m.DotNetIos!), f => f.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public async Task Profiles_are_chosen_by_route_and_bundle_id_never_by_team_alone()
    {
        var m = await MailMobile();
        var exp = DateTimeOffset.UtcNow.AddMonths(6);
        IosProvisioningProfile P(string name, string appId, IosProfileKind kind) => new("/p/" + name, name, Guid.NewGuid().ToString(), "ABCDE12345", "ABCDE12345." + appId, kind, exp);
        var dev = new SigningIdentity("1111111111111111111111111111111111111111", "Apple Development: Jane (ABCDE12345)", AppleCertificateKind.Development, "ABCDE12345");
        var dist = new SigningIdentity("2222222222222222222222222222222222222222", "Apple Distribution: HCG (ABCDE12345)", AppleCertificateKind.AppleDistribution, "ABCDE12345");
        var report = MacReport(identities: [dev, dist]) with
        {
            IosProfiles = [P("Wildcard Development", "*", IosProfileKind.Development), P("Other App", "com.hakmicapitalgroup.hcgnews", IosProfileKind.Development),
                           P("Wildcard Store", "com.hakmicapitalgroup.*", IosProfileKind.AppStore), P("HCG Mail App Store", "com.hakmicapitalgroup.hcgmail", IosProfileKind.AppStore)],
        };
        ReadinessItem Profile(MobileBuildOptions o) => MobileReadiness.Evaluate(m, o.Output, o, report).Items.Single(i => i.Tool == "Provisioning profile");

        // Development: a compatible wildcard profile is fine.
        Assert.Equal(ReadinessState.Ready, Profile(Manual(MobileOutput.IosDeviceApp)).State);
        // Same team, another App ID: refused, and the reason says why.
        var other = Profile(Manual(MobileOutput.IosDeviceApp) with { ProvisioningProfile = "Other App" });
        Assert.Equal(ReadinessState.IncompatibleVersion, other.State);
        Assert.Contains("same team is not enough", other.Detail);
        // App Store: never a wildcard, only the profile for this exact App ID, with a distribution certificate.
        var store = Manual(MobileOutput.IosIpa, "Release", IosExportMethod.AppStoreConnect) with { SigningIdentity = dist.Name };
        Assert.Contains("wildcard App Store profile", Profile(store with { ProvisioningProfile = "Wildcard Store" }).Detail);
        Assert.Equal(ReadinessState.Ready, Profile(store with { ProvisioningProfile = "HCG Mail App Store" }).State);
        Assert.Equal(ReadinessState.IncompatibleVersion, Profile(store with { ProvisioningProfile = "Wildcard Development" }).State);   // wrong type

        // The device app, archive and IPA are offered for .NET for iOS, judged with this Mac's signing assets (not as unsigned).
        var outputs = MobileCapabilities.Evaluate(m, report);
        Assert.Contains(outputs, o => o.Output == MobileOutput.IosDeviceApp && o.IsAvailable);
        Assert.Contains(outputs, o => o.Output == MobileOutput.IosArchive && o.IsAvailable);
        Assert.DoesNotContain(outputs, o => o.Reason?.Contains("only with code signing", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Entitlement_variables_are_resolved_per_profile_before_they_are_compared()
    {
        // HCG Mail's Entitlements.plist: keychain-access-groups with $(AppIdentifierPrefix)…, against a wildcard development profile.
        var requested = "$(AppIdentifierPrefix)$(CFBundleIdentifier), $(AppIdentifierPrefix)com.microsoft.adalcache";
        var expanded = HCG.ExecutableMaker.Packaging.Signing.SigningValidator.Expand(requested, "ABCDE12345", "com.hakmicapitalgroup.hcgmail");
        Assert.Equal("ABCDE12345.com.hakmicapitalgroup.hcgmail, ABCDE12345.com.microsoft.adalcache", expanded);
        Assert.True(HCG.ExecutableMaker.Packaging.Signing.SigningValidator.ValueAllowed(expanded, "ABCDE12345.*"));
        Assert.False(HCG.ExecutableMaker.Packaging.Signing.SigningValidator.ValueAllowed(requested, "ABCDE12345.*"));   // what the unexpanded value did
        Assert.False(HCG.ExecutableMaker.Packaging.Signing.SigningValidator.ValueAllowed(expanded, "OTHERTEAM1.*"));
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
