using System.IO.Compression;
using HCG.ExecutableMaker.BuildEngine;
using HCG.ExecutableMaker.BuildEngine.MacXcode;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Packaging;
using HCG.ExecutableMaker.Packaging.MacDistribution;
using HCG.ExecutableMaker.Packaging.Packagers;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Infrastructure.Import;
using HCG.ExecutableMaker.Infrastructure.Persistence;
using HCG.ExecutableMaker.ProjectInspection;
using HCG.ExecutableMaker.ProjectInspection.DotNet;
using HCG.ExecutableMaker.ProjectInspection.Ecosystems;
using HCG.ExecutableMaker.ProjectInspection.MacXcode;
using HCG.ExecutableMaker.ProjectInspection.Mobile;
using HCG.ExecutableMaker.ProjectInspection.Swift;
using Microsoft.Extensions.Logging.Abstractions;

namespace HCG.ExecutableMaker.Tests;

/// <summary>
/// Native macOS apps built with Xcode: import (ZIP with an enclosing folder), static detection, preflight. The real
/// xcodebuild build, packaging and runtime checks run on a Mac in CI (job xcode-macos).
/// </summary>
public sealed class MacXcodeTests : IDisposable
{
    private readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    private static string FixtureDir => Path.Combine(Fixtures.RepoRoot, "tests", "Fixtures", "MacXcode", "DeskLoop");

    internal static ProjectInspectionService Service() => new(
        new IProjectInspector[]
        {
            new DotNetProjectInspector(TimeProvider.System), new MobileProjectInspector(TimeProvider.System),
            new MobileArtifactProjectInspector(TimeProvider.System), new SwiftProjectInspector(TimeProvider.System),
            new MacXcodeProjectInspector(TimeProvider.System),
        }.Concat(EcosystemSignatureInspector.CreateDefaults(TimeProvider.System)),
        TimeProvider.System);

    /// <summary>A Mac with Xcode selected: a fake developer directory with usr/bin/xcodebuild and a macOS SDK with SDKSettings.json.</summary>
    private ToolchainReport Mac(string sdk = "26.5", string macOS = "26.1", string[]? archs = null, XcodeState state = XcodeState.Ready, bool commandLineTools = false,
        TargetArchitecture host = TargetArchitecture.Arm64) => MacReport(_t, sdk, macOS, archs, state, commandLineTools, host);

    internal static ToolchainReport MacReport(TempDir _t, string sdk = "26.5", string macOS = "26.1", string[]? archs = null, XcodeState state = XcodeState.Ready, bool commandLineTools = false,
        TargetArchitecture host = TargetArchitecture.Arm64)
    {
        var dev = _t.Combine(commandLineTools ? "CommandLineTools" : "Xcode_26.6.app", "Contents", "Developer");
        Directory.CreateDirectory(Path.Combine(dev, "usr", "bin"));
        File.WriteAllText(Path.Combine(dev, "usr", "bin", "xcodebuild"), "");
        var sdkPath = Path.Combine(dev, "Platforms", "MacOSX.platform", "Developer", "SDKs", "MacOSX.sdk");
        Directory.CreateDirectory(sdkPath);
        File.WriteAllText(Path.Combine(sdkPath, "SDKSettings.json"),
            "{\"Version\":\"" + sdk + "\",\"SupportedTargets\":{\"macosx\":{\"Archs\":[" +
            string.Join(",", (archs ?? ["x86_64", "x86_64h", "arm64", "arm64e"]).Select(a => "\"" + a + "\"")) + "]}}}");
        ToolchainInfo T(string id, string? version, string? path) => new() { Id = id, DisplayName = id, Category = "Apple", Status = ToolchainStatus.Installed, Version = version, ExecutablePath = path };
        return new ToolchainReport(new HostEnvironment(TargetPlatform.MacOS, host, $"macOS {macOS}", ".NET", "m"),
            [T(ToolchainIds.MacOSSdk, sdk, sdkPath), T(ToolchainIds.MacOSVersion, macOS, null), T(ToolchainIds.Codesign, null, "/usr/bin/codesign"), T(ToolchainIds.Hdiutil, null, "/usr/bin/hdiutil")],
            null, null, DateTimeOffset.UtcNow)
        {
            Xcode = new XcodeStatus
            {
                State = state, ActiveDeveloperDirectory = dev, VerifiedVersion = "26.6", VerifiedBuild = "17F100",
                Diagnosis = state == XcodeState.Ready ? "Xcode 26.6 is selected." : "Xcode is not ready.",
                CorrectiveAction = state == XcodeState.Ready ? "" : "Open Xcode once to finish its first launch.",
            },
        };
    }

    private static ToolchainReport Linux() => new(new HostEnvironment(TargetPlatform.Linux, TargetArchitecture.X64, "Ubuntu 24.04", ".NET", "m"), [], null, null, DateTimeOffset.UtcNow);

    /// <summary>The fixture as a GitHub-style ZIP: everything inside one enclosing folder (DeskLoop-main/).</summary>
    private string FixtureZip(string wrapper = "DeskLoop-main")
    {
        var staging = _t.Combine("zip-src", wrapper);
        CopyDirectory(FixtureDir, staging);
        var zip = _t.Combine(wrapper + ".zip");
        ZipFile.CreateFromDirectory(staging, zip, CompressionLevel.Fastest, includeBaseDirectory: true);
        return zip;
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (var d in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, d)));
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories)) File.Copy(f, Path.Combine(to, Path.GetRelativePath(from, f)));
    }

    // ───────────── Import + detection ─────────────

    [Fact]
    public async Task A_GitHub_style_ZIP_is_imported_and_detected_as_a_native_macOS_Xcode_app()
    {
        var paths = new AppPaths(_t.Combine("app"));
        var db = new SqliteDatabase(paths);
        var report = Mac();
        var projects = new ProjectService(new ProjectImporter(paths, TimeProvider.System), Service(), new SqliteProjectRepository(db), new SqliteSettingsStore(db),
            new FixedToolchains(report), paths, TimeProvider.System, NullLogger<ProjectService>.Instance);

        var project = (await projects.ImportZipAsync(FixtureZip(), null, CancellationToken.None)).Project;
        var a = project.Analysis!;

        Assert.Equal(MacXcodeProjectInspector.InspectorId, a.InspectorId);
        Assert.Equal("Native macOS app — Xcode", a.ApplicationType);
        Assert.Equal("DeskLoop", project.Name);
        Assert.Equal(SupportLevel.Supported, a.Support);
        // The enclosing folder is handled: paths keep it, nothing has to be moved.
        Assert.Equal("DeskLoop-main/DeskLoop.xcodeproj", a.PrimaryProjectPath);
        var p = Assert.Single(a.MacXcode!.Projects);
        var t = Assert.Single(p.AppTargets);
        Assert.Equal("DeskLoop", t.ProductName); // $(TARGET_NAME) resolved
        Assert.Equal("org.example.DeskLoop", t.BundleIdentifier);
        Assert.Equal("14.0", t.DeploymentTarget);
        Assert.Equal("2.3.0", t.MarketingVersion);
        Assert.Equal("7", t.BuildVersion);
        Assert.Equal("Desk Loop", t.DisplayName);
        Assert.Equal("-", t.CodeSignIdentity);
        Assert.Null(t.DevelopmentTeam);
        Assert.Null(t.Architectures);
        Assert.Equal(["AVFoundation", "AppKit", "ServiceManagement"], t.Frameworks);
        Assert.Equal(["Debug", "Release"], p.Configurations);
        var scheme = Assert.Single(p.Schemes);
        Assert.True(scheme.Shared);
        Assert.Equal(["DeskLoop"], scheme.ArchiveTargets);
        Assert.Equal(["Swift"], p.Languages);
        Assert.Equal(["AppKit"], p.UiFrameworks);
        Assert.Equal(["DeskLoop-main/LICENSE"], p.LicenseFiles);
        Assert.Equal(["DeskLoop-main/INSTALL.command"], p.HelperScripts);
        Assert.Equal("DeskLoop-main/DeskLoop/Resources/Assets.xcassets/AppIcon.appiconset", p.AppIconSet);
        Assert.Contains(a.Diagnostics, d => d.Message.StartsWith("INSTALL.command ships with the project. HCG does not run it", StringComparison.Ordinal));
        Assert.Contains(a.Diagnostics, d => d.Message.Contains("signing ad hoc (CODE_SIGN_IDENTITY \"-\"), no development team", StringComparison.Ordinal));
        // Importing and inspecting never ran the project's script.
        Assert.False(File.Exists(Path.Combine(project.SourceDirectory, "DeskLoop-main", "INSTALL-COMMAND-WAS-RUN")));
    }

    [Fact]
    public async Task The_MotionWallpaper_project_shape_is_read_from_a_compact_pbxproj()
    {
        // The same layout and settings as MotionWallpaper 1.0.5 (one object per line, project-level deployment target,
        // target-level identity and ad-hoc signing, framework references by SDK path).
        var src = File.ReadAllText(Path.Combine(FixtureDir, "DeskLoop.xcodeproj", "project.pbxproj"))
            .Replace("MACOSX_DEPLOYMENT_TARGET = 14.0", "MACOSX_DEPLOYMENT_TARGET = 26.0", StringComparison.Ordinal)
            .Replace("PRODUCT_NAME = \"$(TARGET_NAME)\"", "MACOSX_DEPLOYMENT_TARGET = 26.0; PRODUCT_NAME = MotionWallpaper", StringComparison.Ordinal)
            .Replace("org.example.DeskLoop", "com.example.MotionWallpaper", StringComparison.Ordinal)
            .Replace("MARKETING_VERSION = 2.3.0", "MARKETING_VERSION = 1.0.5", StringComparison.Ordinal)
            .Replace("CURRENT_PROJECT_VERSION = 7", "CURRENT_PROJECT_VERSION = 5", StringComparison.Ordinal);
        _t.Write("MotionWallpaper-master/MotionWallpaper.xcodeproj/project.pbxproj", src);
        _t.Write("MotionWallpaper-master/DeskLoop/App/main.swift", "import AppKit\nimport AVFoundation\n");

        var a = await Service().InspectAsync(_t.Path, Mac(sdk: "26.5"), CancellationToken.None);
        var t = Assert.Single(a.MacXcode!.Projects[0].AppTargets);
        Assert.Equal("MotionWallpaper", t.ProductName);
        Assert.Equal("26.0", t.DeploymentTarget);
        Assert.Equal("com.example.MotionWallpaper", t.BundleIdentifier);
        Assert.Equal(("1.0.5", "5"), (t.MarketingVersion, t.BuildVersion));
        Assert.Equal(SupportLevel.Supported, a.Support);
        Assert.Equal("macOS 26.0+", a.Runtime);
        // No shared scheme in this copy: xcodebuild's own per-target scheme is used, and that is said.
        Assert.Contains(a.Diagnostics, d => d.Message.StartsWith("No shared scheme is committed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Several_projects_and_schemes_are_all_listed_for_selection()
    {
        CopyDirectory(FixtureDir, _t.Combine("apps", "DeskLoop"));
        CopyDirectory(FixtureDir, _t.Combine("apps", "Second"));
        Directory.Move(_t.Combine("apps", "Second", "DeskLoop.xcodeproj"), _t.Combine("apps", "Second", "Second.xcodeproj"));
        _t.Write("apps/DeskLoop/DeskLoop.xcodeproj/xcshareddata/xcschemes/DeskLoop Debug Tools.xcscheme",
            File.ReadAllText(Path.Combine(FixtureDir, "DeskLoop.xcodeproj", "xcshareddata", "xcschemes", "DeskLoop.xcscheme")));

        var a = await Service().InspectAsync(_t.Path, Mac(), CancellationToken.None);
        Assert.Equal(["apps/DeskLoop/DeskLoop.xcodeproj", "apps/Second/Second.xcodeproj"], a.MacXcode!.Projects.Select(p => p.ProjectPath));
        Assert.Equal(["DeskLoop", "DeskLoop Debug Tools"], a.MacXcode.Projects[0].Schemes.Select(s => s.Name).Order(StringComparer.Ordinal));
        Assert.Equal("DeskLoop", MacXcodeProjectAnalyzer.SchemeFor(a.MacXcode.Projects[0], a.MacXcode.Projects[0].AppTargets[0])!.ArchiveTargets.Single());
    }

    [Fact]
    public async Task An_iOS_project_still_goes_to_Mobile_Build_Studio_and_a_project_without_an_app_target_to_the_Swift_explanation()
    {
        var ios = File.ReadAllText(Path.Combine(FixtureDir, "DeskLoop.xcodeproj", "project.pbxproj")).Replace("SDKROOT = macosx", "SDKROOT = iphoneos", StringComparison.Ordinal);
        _t.Write("Phone/Phone.xcodeproj/project.pbxproj", ios);
        Assert.NotEqual(MacXcodeProjectInspector.InspectorId, (await Service().InspectAsync(_t.Path, Mac(), CancellationToken.None)).InspectorId);

        using var other = new TempDir();
        other.Write("Lib.xcodeproj/project.pbxproj", File.ReadAllText(Path.Combine(FixtureDir, "DeskLoop.xcodeproj", "project.pbxproj"))
            .Replace("com.apple.product-type.application", "com.apple.product-type.framework", StringComparison.Ordinal));
        var lib = await Service().InspectAsync(other.Path, Mac(), CancellationToken.None);
        Assert.Equal("swift", lib.InspectorId);
        Assert.Contains("no application target", lib.SupportSummary, StringComparison.Ordinal);
    }

    // ───────────── Preflight ─────────────

    private static MacXcodeTargetInfo Target(string deployment = "26.0") => new()
    {
        Name = "MotionWallpaper", ProductType = "com.apple.product-type.application", ProductName = "MotionWallpaper", DeploymentTarget = deployment,
    };

    [Fact]
    public void Ready_Mac_offers_Apple_silicon_by_default_plus_Universal_and_Intel_when_the_SDK_builds_them()
    {
        var report = Mac();
        var a = MacXcodeCapabilities.Evaluate(Target(), report);
        Assert.Equal(SupportLevel.Supported, a.Support);
        Assert.Equal([MacXcodeArchitecture.AppleSilicon, MacXcodeArchitecture.Universal, MacXcodeArchitecture.Intel], a.Architectures);
        Assert.Equal(MacXcodeArchitecture.AppleSilicon, MacXcodeCapabilities.DefaultArchitecture(report.Host, a.Architectures));
        Assert.EndsWith(Path.Combine("usr", "bin", "xcodebuild"), a.Xcodebuild, StringComparison.Ordinal);
        Assert.Contains("Xcode 26.6", a.Summary, StringComparison.Ordinal);
        Assert.Contains("macOS 26.5 SDK", a.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void An_SDK_without_x86_64_offers_Apple_silicon_only()
    {
        var a = MacXcodeCapabilities.Evaluate(Target(), Mac(archs: ["arm64", "arm64e"]));
        Assert.Equal([MacXcodeArchitecture.AppleSilicon], a.Architectures);
    }

    [Fact]
    public void An_SDK_older_than_the_deployment_target_names_both_versions_and_the_fix()
    {
        var a = MacXcodeCapabilities.Evaluate(Target("26.0"), Mac(sdk: "15.5"));
        Assert.Equal(SupportLevel.MissingToolchain, a.Support);
        var fix = Assert.Single(a.Remediation);
        Assert.Contains("requires the macOS 26.0 SDK or later (MACOSX_DEPLOYMENT_TARGET = 26.0)", fix, StringComparison.Ordinal);
        Assert.Contains("has the macOS 15.5 SDK", fix, StringComparison.Ordinal);
        Assert.Contains("Xcode 26 or later", fix, StringComparison.Ordinal);
    }

    [Fact]
    public void Command_Line_Tools_alone_are_not_enough_and_say_so()
    {
        var a = MacXcodeCapabilities.Evaluate(Target(), Mac(state: XcodeState.NotInstalled, commandLineTools: true));
        Assert.Equal(SupportLevel.MissingToolchain, a.Support);
        Assert.Null(a.Xcodebuild);
        Assert.Contains(a.Remediation, r => r.Contains("Command Line Tools are selected; building an Xcode app project needs the full Xcode", StringComparison.Ordinal));
    }

    [Fact]
    public void A_Mac_running_an_older_macOS_builds_but_is_told_it_cannot_launch_the_app()
    {
        var a = MacXcodeCapabilities.Evaluate(Target("26.0"), Mac(macOS: "15.6"));
        Assert.Equal(SupportLevel.Supported, a.Support);
        Assert.Contains("cannot be launched on this Mac", a.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task On_Linux_the_project_is_recognised_but_never_reported_as_built_or_buildable()
    {
        CopyDirectory(FixtureDir, _t.Combine("DeskLoop-main"));
        var a = await Service().InspectAsync(_t.Path, Linux(), CancellationToken.None);
        Assert.Equal(MacXcodeProjectInspector.InspectorId, a.InspectorId);
        Assert.Equal(SupportLevel.RequiresDifferentHost, a.Support);
        Assert.Contains("built with Xcode on a Mac. This computer runs Linux", a.SupportSummary, StringComparison.Ordinal);
        Assert.Contains("no remote Mac build host is configured", a.SupportSummary, StringComparison.Ordinal);
        Assert.Contains(a.RequiredToolchains, r => r.ToolchainId == "macos-host" && !r.IsSatisfied);
    }

    // ───────────── Build plan ─────────────

    private static BuildSettings XcodeSettings(string output, MacXcodeArchitecture arch = MacXcodeArchitecture.AppleSilicon, bool adHoc = false) => new()
    {
        ProjectRelativePath = "MotionWallpaper-master/MotionWallpaper.xcodeproj",
        Platform = TargetPlatform.MacOS,
        Architecture = arch.ToTarget(),
        OutputDirectory = output,
        Identity = new AppIdentity { ApplicationName = "MotionWallpaper", Version = "1.0.5", ApplicationId = "com.example.MotionWallpaper", Publisher = "" },
        PackagingFormats = [PackagingFormat.MacAppBundle, PackagingFormat.PortableZip, PackagingFormat.MacDmg],
        MacXcode = new MacXcodeBuildOptions
        {
            ProjectPath = "MotionWallpaper-master/MotionWallpaper.xcodeproj", Scheme = "MotionWallpaper", Configuration = "Release", Architecture = arch,
            AppTarget = "MotionWallpaper", ProductName = "MotionWallpaper", BundleIdentifier = "com.example.MotionWallpaper", DisplayName = "Motion Wallpaper",
            DeploymentTarget = "26.0", SignLocallyAdHoc = adHoc, LicenseFiles = ["MotionWallpaper-master/LICENSE"],
        },
    };

    [Fact]
    public void The_plan_copies_the_source_and_runs_xcodebuild_on_the_copy_with_only_the_listed_overrides()
    {
        var source = _t.Combine("ws", "source");
        Directory.CreateDirectory(Path.Combine(source, "MotionWallpaper-master", "MotionWallpaper.xcodeproj"));
        var s = XcodeSettings(_t.Combine("out"));
        var layout = MacXcodeBuildPlanner.PrepareLayout(s, _t.Combine("build"), source);
        var steps = MacXcodeBuildPlanner.Plan(s, layout, source, "/Applications/Xcode_26.6.app/Contents/Developer/usr/bin/xcodebuild", null, new Dictionary<string, string> { ["PATH"] = "/usr/bin" });

        // Copy, discover (-list), read settings, build.
        Assert.Equal(4, steps.Count);
        Assert.Equal("/usr/bin/ditto", steps[0].Process.Executable);
        Assert.Equal([source, layout.SourceCopy], steps[0].Process.Arguments);
        Assert.Contains("-list", steps[1].Process.Arguments);
        var build = steps[3].Process;
        Assert.EndsWith("xcodebuild", build.Executable, StringComparison.Ordinal);
        var args = build.Arguments.ToList();
        // On the copy, never on the imported source; derived data, result bundle and packages in HCG's build folder.
        Assert.Equal(Path.Combine(layout.SourceCopy, "MotionWallpaper-master", "MotionWallpaper.xcodeproj"), args[args.IndexOf("-project") + 1]);
        Assert.StartsWith(_t.Combine("build"), args[args.IndexOf("-derivedDataPath") + 1], StringComparison.Ordinal);
        Assert.StartsWith(_t.Combine("build"), args[args.IndexOf("-resultBundlePath") + 1], StringComparison.Ordinal);
        Assert.StartsWith(_t.Combine("build"), args[args.IndexOf("-clonedSourcePackagesDirPath") + 1], StringComparison.Ordinal);
        Assert.Equal("generic/platform=macOS", args[args.IndexOf("-destination") + 1]);
        Assert.Equal(("MotionWallpaper", "Release"), (args[args.IndexOf("-scheme") + 1], args[args.IndexOf("-configuration") + 1]));
        Assert.Equal("build", args[^1]);
        // Exactly the visible overrides; no signing settings unless chosen; no project script.
        var settingsArgs = args.Where(a => a.Contains('=') && !a.StartsWith('/') && !a.StartsWith("generic/", StringComparison.Ordinal)).ToList();
        Assert.Equal(["ARCHS=arm64", "ONLY_ACTIVE_ARCH=NO", "COMPILER_INDEX_STORE_ENABLE=NO"], settingsArgs);
        Assert.DoesNotContain(steps, st => st.Process.Arguments.Any(a => a.EndsWith(".command", StringComparison.Ordinal)));
        Assert.Equal("YES", build.Environment!["NSUnbufferedIO"]);
        Assert.True(steps[2].CaptureStdout);
        Assert.Contains("-showBuildSettings", steps[2].Process.Arguments);
    }

    [Fact]
    public void Universal_and_local_ad_hoc_signing_are_visible_overrides()
    {
        var universal = MacXcodeBuildPlanner.Overrides(XcodeSettings("/o", MacXcodeArchitecture.Universal).MacXcode!, null);
        Assert.Equal("arm64 x86_64", universal.Single(o => o.Setting == "ARCHS").Value);
        Assert.Contains("the project sets no ARCHS", universal[0].Reason, StringComparison.Ordinal);
        var adHoc = MacXcodeBuildPlanner.Overrides(XcodeSettings("/o", adHoc: true).MacXcode!, "arm64");
        Assert.Equal(["ARCHS", "ONLY_ACTIVE_ARCH", "COMPILER_INDEX_STORE_ENABLE", "CODE_SIGN_IDENTITY", "CODE_SIGN_STYLE", "DEVELOPMENT_TEAM", "PROVISIONING_PROFILE_SPECIFIER"],
            adHoc.Select(o => o.Setting));
        Assert.Equal("-", adHoc.Single(o => o.Setting == "CODE_SIGN_IDENTITY").Value);
    }

    [Fact]
    public void The_built_app_is_taken_from_xcodebuilds_own_settings()
    {
        string[] captured =
        [
            "Command line invocation:", "    /Applications/Xcode.app/Contents/Developer/usr/bin/xcodebuild -showBuildSettings -json", "",
            """[ { "action" : "build", "buildSettings" : { "TARGET_BUILD_DIR" : "/b/DerivedData/Build/Products/Release", "FULL_PRODUCT_NAME" : "MotionWallpaper.app",""",
            """ "EXECUTABLE_PATH" : "MotionWallpaper.app/Contents/MacOS/MotionWallpaper", "PRODUCT_BUNDLE_IDENTIFIER" : "com.example.MotionWallpaper", "WRAPPER_EXTENSION" : "app",""",
            """ "PRODUCT_TYPE" : "com.apple.product-type.application", "MACOSX_DEPLOYMENT_TARGET" : "26.0", "MARKETING_VERSION" : "1.0.5", "CURRENT_PROJECT_VERSION" : "5", "ARCHS" : "arm64", "SDK_VERSION" : "26.5" },""",
            """ "target" : "MotionWallpaper" } ]""",
        ];
        var (app, problem) = MacXcodeBuildPlanner.Resolve(captured, XcodeSettings("/o").MacXcode!);
        Assert.Null(problem);
        Assert.Equal("/b/DerivedData/Build/Products/Release/MotionWallpaper.app", app!.AppPath);
        Assert.Equal("MotionWallpaper.app/Contents/MacOS/MotionWallpaper", app.ExecutablePath);
        Assert.Equal(("com.example.MotionWallpaper", "26.0", "arm64", "26.5"), (app.BundleIdentifier, app.DeploymentTarget, app.Archs, app.SdkVersion));
        Assert.Contains("builds no macOS application", MacXcodeBuildPlanner.Resolve(["[ ]"], XcodeSettings("/o").MacXcode!).Problem, StringComparison.Ordinal);
        Assert.Contains("not at", MacXcodeBuildPlanner.CheckFresh(app, DateTimeOffset.UtcNow), StringComparison.Ordinal);
    }

    [Fact]
    public void Building_is_refused_off_a_Mac_and_for_paths_outside_the_source()
    {
        var source = _t.Combine("src");
        Directory.CreateDirectory(Path.Combine(source, "MotionWallpaper-master", "MotionWallpaper.xcodeproj"));
        var linux = Assert.Throws<BuildValidationException>(() => MacXcodeBuildPlanner.Validate(XcodeSettings("/o"), Linux().Host, source));
        Assert.Contains("built with Xcode on a Mac. This computer runs Linux; nothing was built", linux.Message, StringComparison.Ordinal);
        var mac = Mac().Host;
        MacXcodeBuildPlanner.Validate(XcodeSettings("/o"), mac, source);
        var escape = XcodeSettings("/o") with { MacXcode = XcodeSettings("/o").MacXcode! with { ProjectPath = "../../etc/x.xcodeproj" } };
        Assert.Throws<BuildValidationException>(() => MacXcodeBuildPlanner.Validate(escape, mac, source));
        var badScheme = XcodeSettings("/o") with { MacXcode = XcodeSettings("/o").MacXcode! with { Scheme = "-quiet; rm" } };
        Assert.Throws<BuildValidationException>(() => MacXcodeBuildPlanner.Validate(badScheme, mac, source));
    }

    [Fact]
    public void Packaging_offers_app_zip_and_dmg_on_a_Mac_and_nothing_buildable_elsewhere()
    {
        var mac = PackagingCatalog.Evaluate(new PackagingContext(TargetPlatform.MacOS, Mac().Host, Mac(), true) { IsMacXcode = true });
        Assert.Equal([PackagingFormat.MacAppBundle, PackagingFormat.PortableZip, PackagingFormat.MacDmg, PackagingFormat.MacPkg, PackagingFormat.MacXcodeArchive],
            mac.Where(o => o.IsAvailable).Select(o => o.Format));
        var linux = PackagingCatalog.Evaluate(new PackagingContext(TargetPlatform.MacOS, Linux().Host, Linux(), true) { IsMacXcode = true });
        Assert.DoesNotContain(linux, o => o.IsAvailable);
        Assert.All(linux.Where(o => o.Format is PackagingFormat.MacAppBundle or PackagingFormat.PortableZip or PackagingFormat.MacDmg),
            o => Assert.Equal(PackagingAvailability.RequiresDifferentHost, o.Availability));
        // The ZIP and DMG wrap the app, so the app is always produced first.
        Assert.Equal([PackagingFormat.MacAppBundle, PackagingFormat.PortableZip, PackagingFormat.MacDmg],
            PackagingCatalog.WithDependencies([PackagingFormat.MacDmg, PackagingFormat.PortableZip], XcodeSettings("/o")));
    }

    [Theory]
    [InlineData("Signature=adhoc\nIdentifier=com.example.MotionWallpaper", "AdHoc", "Ad hoc signature (no certificate, no team)")]
    [InlineData("Authority=Developer ID Application: Example (ABCDE12345)\nTeamIdentifier=ABCDE12345\nCodeDirectory v=20500 size=1 flags=0x10000(runtime)\nTimestamp=1 Jan 2026", "DeveloperId", "Developer ID signature")]
    [InlineData("Authority=Apple Development: someone (XYZ)\nTeamIdentifier=XYZ1234567", "Development", "not a distribution signature")]
    public void Signatures_are_classified_without_blurring_ad_hoc_and_Developer_ID(string codesignOutput, string kind, string text)
    {
        var info = MacCodeSigner.ParseSignatureInfo(codesignOutput.Replace("\\n", "\n", StringComparison.Ordinal));
        Assert.Equal(kind, MacSignatureDescription.Classify(info).ToString());
        Assert.Contains(text, MacSignatureDescription.Describe(info), StringComparison.Ordinal);
        Assert.Contains("not notarized", MacSignatureDescription.Describe(info), StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FixedToolchains(ToolchainReport report) : IToolchainService
    {
        public ToolchainReport? Latest => report;
        public event EventHandler<ToolchainReport>? Updated { add { } remove { } }
        public Task<ToolchainReport> GetAsync(bool refresh = false, CancellationToken cancellationToken = default) => Task.FromResult(report);
    }
}
