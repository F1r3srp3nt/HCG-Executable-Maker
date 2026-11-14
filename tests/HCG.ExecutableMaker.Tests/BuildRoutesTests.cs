using HCG.ExecutableMaker.BuildEngine;
using HCG.ExecutableMaker.BuildEngine.Mobile;
using HCG.ExecutableMaker.BuildEngine.Native;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Infrastructure.Persistence;
using HCG.ExecutableMaker.Infrastructure.Processes;
using HCG.ExecutableMaker.Packaging;
using HCG.ExecutableMaker.ProjectInspection;
using HCG.ExecutableMaker.ProjectInspection.DotNet;
using HCG.ExecutableMaker.ProjectInspection.Ecosystems;
using HCG.ExecutableMaker.ProjectInspection.Mobile;
using HCG.ExecutableMaker.ProjectInspection.Swift;

namespace HCG.ExecutableMaker.Tests;

/// <summary>
/// One folder, two build routes: HCG Nexus' Theos tweak (root Makefile with HCG_ENV rootless/rootful, TWEAK_NAME
/// HCGNexus, SUBPROJECTS += Prefs, control, PreviewHarness) next to an iOS app project HCGNexus.xcodeproj. Both
/// analyses come out of one inspection, New Build offers both routes, each builds with its own planner, and the
/// routes never mix (.deb only on the Theos route, IPA only on the Xcode route).
/// </summary>
public class BuildRoutesTests : IDisposable
{
    private readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    private static string Fixture => Path.Combine(Fixtures.RepoRoot, "tests", "Fixtures", "Theos", "NexusWithXcode");

    /// <summary>The fixture copied to a folder whose path contains spaces, like an imported workspace.</summary>
    private string Repo()
    {
        var dir = _t.Combine("HCG Nexus repo", "source");
        if (!Directory.Exists(dir)) MobileTestKit.CopyTree(Fixture, dir);
        return dir;
    }

    /// <summary>The inspectors the app registers (Composition.cs).</summary>
    private static ProjectInspectionService Service() => new(
        new IProjectInspector[]
        {
            new DotNetProjectInspector(TimeProvider.System), new MobileProjectInspector(TimeProvider.System),
            new MobileArtifactProjectInspector(TimeProvider.System), new SwiftProjectInspector(TimeProvider.System),
        }.Concat(EcosystemSignatureInspector.CreateDefaults(TimeProvider.System)),
        TimeProvider.System);

    /// <summary>A Mac with Theos (iPhoneOS 16.5 and 14.5 SDKs), ldid, GNU make, perl, Xcode's clang and Xcode itself.</summary>
    private static ToolchainReport MacReport()
    {
        ToolchainInfo Row(string id, string path, params string[] extra) =>
            new() { Id = id, DisplayName = id, Category = "x", Status = ToolchainStatus.Installed, ExecutablePath = path, Version = "1", AdditionalVersions = extra };
        return new ToolchainReport(new HostEnvironment(TargetPlatform.MacOS, TargetArchitecture.Arm64, "macOS", ".NET", "osx"),
        [
            Row(ToolchainIds.Theos, "/opt/theos", "iPhoneOS16.5.sdk", "iPhoneOS14.5.sdk"), Row(ToolchainIds.TheosMake, "/opt/homebrew/bin/gmake"), Row(ToolchainIds.Ldid, "/opt/homebrew/bin/ldid"),
            Row(ToolchainIds.Perl, "/usr/bin/perl"), Row(ToolchainIds.TheosIosToolchain, "/Applications/Xcode.app/clang"),
            Row(ToolchainIds.Xcode, "/usr/bin/xcodebuild"), Row(ToolchainIds.IosSdk, "/usr/bin/xcodebuild"), Row(ToolchainIds.AppleSigningIdentities, "/usr/bin/security"),
        ], null, null, DateTimeOffset.UtcNow);
    }

    private static Task<ProjectAnalysis> InspectAsync(string dir) => Service().InspectAsync(dir, MacReport(), CancellationToken.None);

    // ------------------------------------------------------------------ inspection

    [Fact]
    public async Task A_folder_with_an_xcode_app_and_a_theos_tweak_yields_both_analyses()
    {
        var a = await InspectAsync(Repo());

        // The Xcode app leads, as before.
        Assert.Equal("mobile", a.InspectorId);
        Assert.Equal(MobileFramework.NativeIos, a.Mobile!.Framework);
        Assert.Equal("HCGNexus.xcodeproj", a.Mobile.Ios!.ProjectPath);
        Assert.Contains(a.Mobile.Ios.AppTargets, t => t.Name == "HCGNexus" && t.BundleIdentifier == "com.hakmicapitalgroup.hcgnexus.app");

        // The Theos projects are attached: the tweak (with its Prefs subproject) and the separately packaged PreviewHarness.
        var native = a.Native!;
        Assert.All(native.Projects, p => Assert.Equal(NativeProjectKind.Theos, p.Kind));
        var root = native.Projects.Single(p => p.Directory == "");
        Assert.Null(root.UnsupportedReason);
        Assert.Equal("com.hakmicapitalgroup.hcgnexus", root.Theos!.PackageId);
        Assert.Equal(["Prefs"], root.Theos.Subprojects);
        Assert.Collection(root.Theos.Instances,
            i => Assert.Equal((TheosInstanceType.Tweak, "HCGNexus"), (i.Type, i.Name)),
            i => Assert.Equal((TheosInstanceType.PreferenceBundle, "HCGNexusPrefs"), (i.Type, i.Name)));
        Assert.True(root.Theos.UsesParentDirectory);
        var rootless = root.Theos.Schemes.Single(s => s.SelectorValue == "rootless");
        Assert.Equal(("HCG_ENV", "rootless", true), (rootless.SelectorVariable, rootless.PackageScheme, rootless.IsDefault));
        Assert.Equal(("HCG_ENV", "rootful"), root.Theos.Schemes.Single(s => s.SelectorValue == "rootful") is var rf ? (rf.SelectorVariable, rf.PackageScheme) : default);
        var preview = native.Projects.Single(p => p.Directory == "PreviewHarness");
        Assert.Equal(TheosInstanceType.Application, preview.Theos!.Instances.Single().Type);
        Assert.Equal("com.hakmicapitalgroup.hcgnexus.preview", preview.Theos.PackageId);

        // The Theos project is no longer dismissed as "not part of this build"; the user is told where to choose it.
        Assert.DoesNotContain(a.Diagnostics, d => d.Message.Contains("C / C++", StringComparison.Ordinal));
        Assert.Contains(a.Diagnostics, d => d.Message.Contains("Theos project HCG Nexus in (workspace root)", StringComparison.Ordinal) && d.Message.Contains(BuildRoutes.TheosDebLabel, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Single_route_folders_are_unchanged()
    {
        // Theos only (HCG Nexus without the Xcode project): the C/C++ inspector leads, no route choice.
        var theosOnly = Repo();
        Directory.Delete(Path.Combine(theosOnly, "HCGNexus.xcodeproj"), true);
        var t = await InspectAsync(theosOnly);
        Assert.Equal("native", t.InspectorId);
        Assert.Null(t.Mobile);
        Assert.Empty(BuildRoutes.For(t));
        Assert.Same(t, BuildRoutes.View(t, BuildRoutes.TheosDeb, MacReport()));

        // Xcode only: nothing attached, no route choice.
        var x = await InspectAsync(MobileTestKit.Fixture("HelloIOS"));
        Assert.Null(x.Native);
        Assert.Empty(BuildRoutes.For(x));
    }

    // ------------------------------------------------------------------ routes

    [Fact]
    public async Task New_build_offers_the_ipa_route_and_the_deb_route_with_their_own_analyses()
    {
        var a = await InspectAsync(Repo());
        var routes = BuildRoutes.For(a);
        Assert.Equal([BuildRoutes.XcodeIpa, BuildRoutes.TheosDeb], routes.Select(r => r.Id));
        Assert.Equal(["iOS app (IPA, Xcode)", "Jailbreak package (.deb, Theos)"], routes.Select(r => r.Label));
        Assert.Contains("HCGNexus.xcodeproj", routes[0].Description);
        Assert.Contains("Never packages a .deb", routes[0].Description);
        Assert.Contains("Never produces or exports an IPA", routes[1].Description);

        // IPA route: the Xcode analysis (Mobile Build Studio), recommending IPA/archive and never a .deb.
        var ipa = BuildRoutes.View(a, BuildRoutes.XcodeIpa, MacReport());
        Assert.Equal("mobile", ipa.InspectorId);
        Assert.NotNull(ipa.Mobile);
        Assert.Contains(PackagingFormat.IosIpa, ipa.RecommendedFormats);
        Assert.DoesNotContain(PackagingFormat.JailbreakDeb, ipa.RecommendedFormats);
        Assert.Equal(ipa, BuildRoutes.View(a, null, MacReport()));     // the default route is the leading one

        // .deb route: the same C/C++ (Theos) analysis as a Theos-only folder — New Build's Theos panel works on it unchanged.
        var deb = BuildRoutes.View(a, BuildRoutes.TheosDeb, MacReport());
        Assert.Equal("native", deb.InspectorId);
        Assert.Null(deb.Mobile);
        Assert.Equal([PackagingFormat.JailbreakDeb], deb.RecommendedFormats);
        Assert.Equal(SupportLevel.Supported, deb.Support);
        Assert.Equal(["Makefile", "PreviewHarness/Makefile"], deb.Projects.Select(p => p.RelativePath).Order());
        Assert.All(deb.Projects, p => Assert.Equal([TargetPlatform.IOS], p.CompatiblePlatforms));
        Assert.Contains(deb.Projects, p => p.ApplicationType.StartsWith("Jailbreak package (.deb): tweak HCGNexus + preference bundle HCGNexusPrefs", StringComparison.Ordinal));
        Assert.Contains(deb.Projects, p => p.ApplicationType.Contains("app (in a .deb, not an IPA) HCGNexusPreview", StringComparison.Ordinal));   // a separate package
        Assert.Contains(deb.Diagnostics, d => d.Message.Contains("iOS app (IPA, Xcode)", StringComparison.Ordinal));

        var theosOnly = Repo();
        Directory.Delete(Path.Combine(theosOnly, "HCGNexus.xcodeproj"), true);
        var reference = await InspectAsync(theosOnly);
        Assert.Equal(reference.Projects.Select(p => (p.RelativePath, p.ApplicationType)), deb.Projects.Select(p => (p.RelativePath, p.ApplicationType)));
        Assert.Equal(reference.SupportSummary, deb.SupportSummary);
    }

    // ------------------------------------------------------------------ each route builds with its own planner

    private static TheosSchemeChoice Choice(NativeProjectInfo p, string value) => p.Theos!.Schemes.Single(s => s.SelectorValue == value) is var s
        ? new TheosSchemeChoice
        {
            SelectorVariable = s.SelectorVariable, SelectorValue = s.SelectorValue, PackageScheme = s.PackageScheme, SdkVersion = s.SdkVersion, DeploymentTarget = s.DeploymentTarget,
            Architectures = p.Theos.Architectures, PackageId = p.Theos.PackageId, PackageVersion = p.Theos.PackageVersion, Instances = p.Theos.Instances,
        }
        : null!;

    private static BuildSettings TheosSettings(NativeProjectInfo p, string selector, string output = "/tmp") => new()
    {
        ProjectRelativePath = p.ManifestPath, Platform = TargetPlatform.IOS, Architecture = TargetArchitecture.Arm64, OutputDirectory = output,
        Identity = new AppIdentity { ApplicationName = "HCG Nexus", Version = "0.1.0", ApplicationId = "com.hakmicapitalgroup.hcgnexus" },
        Native = new NativeBuildOptions { ProjectDirectory = p.Directory, Kind = NativeProjectKind.Theos, Target = "package", TheosScheme = Choice(p, selector) },
        PackagingFormats = [PackagingFormat.JailbreakDeb],
    };

    [Fact]
    public async Task The_deb_route_plans_make_package_with_the_projects_HCG_ENV_and_never_an_ipa()
    {
        var repo = Repo();
        var a = await InspectAsync(repo);
        var root = a.Native!.Projects.Single(p => p.Directory == "");
        var host = new HostEnvironment(TargetPlatform.MacOS, TargetArchitecture.Arm64, "macOS", ".NET", "osx");
        var work = _t.Combine("work");
        foreach (var selector in new[] { "rootless", "rootful" })
        {
            var settings = TheosSettings(root, selector);
            TheosBuildPlanner.Validate(settings, host, repo);
            var prep = TheosBuildPlanner.Prepare(settings, repo, work, root.Theos!.UsesParentDirectory, CancellationToken.None);
            try
            {
                Assert.True(File.Exists(Path.Combine(prep.ProjectDirectory, "Prefs", "Makefile")));
                var step = TheosBuildPlanner.Plan(settings, prep, new TheosBuildEnvironment("/opt/theos", "/opt/homebrew/bin/gmake", "/opt/homebrew/bin/ldid", "/usr/bin/perl", new Dictionary<string, string> { ["THEOS"] = "/opt/theos" }));
                Assert.Equal("/opt/homebrew/bin/gmake", step.Process.Executable);
                Assert.Contains("package", step.Process.Arguments);
                Assert.Contains("HCG_ENV=" + selector, step.Process.Arguments);
                Assert.DoesNotContain(step.Process.Arguments, x => x.Contains("xcodebuild", StringComparison.Ordinal) || x.Contains("ipa", StringComparison.OrdinalIgnoreCase) || x is "install");
            }
            finally { if (prep.IsTemporary) Directory.Delete(Path.GetDirectoryName(prep.WorkRoot)!, true); }
        }

        // The Theos route never produces or exports an IPA.
        var ok = TheosSettings(root, "rootless");
        Assert.Contains("never builds or exports an IPA", Assert.Throws<BuildValidationException>(() =>
            TheosBuildPlanner.Validate(ok with { PackagingFormats = [PackagingFormat.JailbreakDeb, PackagingFormat.IosIpa] }, host, repo)).Message);
        Assert.Throws<BuildValidationException>(() => TheosBuildPlanner.Validate(ok with { PackagingFormats = [PackagingFormat.IosArchive] }, host, repo));
        Assert.Throws<BuildValidationException>(() => TheosBuildPlanner.Validate(
            ok with { Mobile = new MobileBuildOptions { Framework = MobileFramework.NativeIos, Output = MobileOutput.IosIpa } }, host, repo));
    }

    [Fact]
    public async Task The_ipa_route_plans_xcodebuild_for_the_xcode_project_and_never_a_deb()
    {
        using var k = new MobileTestKit();
        var repo = Repo();
        var a = await InspectAsync(repo);
        var report = k.Report(TargetPlatform.MacOS, MobileTestKit.Apple);
        var options = new MobileBuildOptions
        {
            Framework = MobileFramework.NativeIos, Output = MobileOutput.IosArchive, Signing = MobileSigningMode.Unsigned, ExportMethod = IosExportMethod.AppStoreConnect,
        };
        var plan = XcodePlanner.Plan(new MobilePlanInput
        {
            Options = options, Analysis = a.Mobile!, SourceDirectory = repo, WorkDirectory = k.Temp.Combine("work"), Toolchains = report,
            Environment = new Dictionary<string, string> { ["PATH"] = "/usr/bin" }, ToolHomeDirectory = k.ToolHome,
        });
        var args = plan.Steps.Single().Process.Arguments.ToList();
        Assert.Equal("/usr/bin/xcodebuild", plan.Steps.Single().Process.Executable);
        Assert.Equal(Path.Combine(repo, "HCGNexus.xcodeproj"), args[args.IndexOf("-project") + 1]);
        Assert.Equal("HCGNexus", args[args.IndexOf("-scheme") + 1]);
        Assert.Equal("archive", args[^1]);
        Assert.DoesNotContain(args, x => x.Contains("HCG_ENV", StringComparison.Ordinal) || x.Contains("THEOS", StringComparison.Ordinal));

        // The IPA route never packages a .deb.
        var settings = new BuildSettings
        {
            ProjectRelativePath = "HCGNexus.xcodeproj", Platform = TargetPlatform.IOS, Architecture = TargetArchitecture.Arm64, OutputDirectory = "/tmp",
            Identity = new AppIdentity { ApplicationName = "HCG Nexus", Version = "0.1.0", ApplicationId = "com.hakmicapitalgroup.hcgnexus.app" }, Mobile = options,
        };
        MobileBuildService.ValidateRoute(settings);
        Assert.Contains("never packages a .deb", Assert.Throws<BuildValidationException>(() =>
            MobileBuildService.ValidateRoute(settings with { PackagingFormats = [PackagingFormat.JailbreakDeb] })).Message);
        var root = a.Native!.Projects.Single(p => p.Directory == "");
        Assert.Throws<BuildValidationException>(() => MobileBuildService.ValidateRoute(settings with { Native = TheosSettings(root, "rootless").Native }));
    }

    // ------------------------------------------------------------------ BuildService finds the Theos project in the Xcode-led inspection

    private sealed class FixedToolchains(ToolchainReport report) : IToolchainService
    {
        public ToolchainReport? Latest => report;
        public event EventHandler<ToolchainReport>? Updated { add { } remove { } }
        public Task<ToolchainReport> GetAsync(bool refresh = false, CancellationToken cancellationToken = default) => Task.FromResult(report);
    }

    private sealed class Observer : IBuildObserver
    {
        public void OnStage(BuildStage stage, string description) { }
        public void OnLog(BuildLogLine line) { }
    }

    [Fact]
    public async Task A_theos_build_of_the_xcode_led_workspace_runs_the_theos_planner()
    {
        var report = MacReport();
        var paths = new AppPaths(_t.Combine("hcg data"));
        paths.EnsureCreated();
        var db = new SqliteDatabase(paths);
        var ws = Path.Combine(paths.WorkspacesDirectory, Guid.NewGuid().ToString("N"));
        MobileTestKit.CopyTree(Fixture, Path.Combine(ws, "source"));
        var project = new ProjectRecord { Id = Guid.NewGuid(), Name = "HCG Nexus", SourceKind = ProjectSourceKind.Directory, SourceLocation = "/orig", WorkspacePath = ws, ImportedAt = DateTimeOffset.UtcNow };
        project.Analysis = await Service().InspectAsync(project.SourceDirectory, report, CancellationToken.None);
        Assert.Equal("mobile", project.Analysis.InspectorId);                       // stored as inspected: the Xcode app leads
        await new SqliteProjectRepository(db).UpsertAsync(project);

        // The worker records the processes the build starts; make produces nothing, so the build stops after it. The first
        // process is the preflight in the exact child environment (bash answers where ldid and perl resolve).
        var worker = new ScriptedMobileWorker((spec, output, _) =>
        {
            if (spec.Executable is "/bin/bash" or "/bin/sh")
            {
                Assert.Contains("/opt/homebrew/bin", spec.Environment!["PATH"].Split(':'));
                // Answer like the shell would for this PATH; ldid and perl are the report's (they do not exist on this host).
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(spec.Arguments[1], @"command -v (\w+)"))
                {
                    var name = m.Groups[1].Value;
                    output(OutputStream.StdOut, name + "=" + (name switch
                    {
                        "ldid" => "/opt/homebrew/bin/ldid",
                        "perl" => "/usr/bin/perl",
                        _ => TheosBuildEnvironment.Which(name, spec.Environment["PATH"]) ?? "",
                    }));
                }
            }
            return ScriptedMobileWorker.Exit(0);
        });
        var service = new BuildService(worker, new FixedToolchains(report), new SqliteBuildRepository(db), new PackagingRegistry([]), paths, TimeProvider.System);
        var root = project.Analysis.Native!.Projects.Single(p => p.Directory == "");
        var build = await service.RunAsync(new BuildRequest { Project = project, Settings = TheosSettings(root, "rootful", _t.Combine("out")), TrustAcknowledged = true }, new Observer(), CancellationToken.None);

        Assert.DoesNotContain("not in the workspace's inspection", build.FailureReason ?? "");
        Assert.Equal(2, worker.Calls.Count);
        Assert.Equal("/bin/bash", worker.Calls[0].Executable);
        var make = worker.Calls[1];
        Assert.Equal("/opt/homebrew/bin/gmake", make.Executable);
        Assert.Equal(worker.Calls[0].Environment, make.Environment);              // checked and run with the same environment
        Assert.Equal("/opt/homebrew/bin/ldid", make.Environment!["TARGET_CODESIGN"]);
        Assert.Contains("package", make.Arguments);
        Assert.Contains("HCG_ENV=rootful", make.Arguments);
        Assert.DoesNotContain(worker.Calls, c => c.Executable.EndsWith("xcodebuild", StringComparison.Ordinal));

        // Without the attached Theos analysis (an inspection from before both routes existed) the build is refused as before.
        project.Analysis = project.Analysis with { Native = null };
        var refused = await service.RunAsync(new BuildRequest { Project = project, Settings = TheosSettings(root, "rootful", _t.Combine("out")), TrustAcknowledged = true }, new Observer(), CancellationToken.None);
        Assert.Contains("not in the workspace's inspection", refused.FailureReason ?? "");
    }
}
