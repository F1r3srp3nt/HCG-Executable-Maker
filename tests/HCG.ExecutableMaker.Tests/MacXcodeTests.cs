using System.IO.Compression;
using HCG.ExecutableMaker.Core;
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

    private static ProjectInspectionService Service() => new(
        new IProjectInspector[]
        {
            new DotNetProjectInspector(TimeProvider.System), new MobileProjectInspector(TimeProvider.System),
            new MobileArtifactProjectInspector(TimeProvider.System), new SwiftProjectInspector(TimeProvider.System),
            new MacXcodeProjectInspector(TimeProvider.System),
        }.Concat(EcosystemSignatureInspector.CreateDefaults(TimeProvider.System)),
        TimeProvider.System);

    /// <summary>A Mac with Xcode selected: a fake developer directory with usr/bin/xcodebuild and a macOS SDK with SDKSettings.json.</summary>
    private ToolchainReport Mac(string sdk = "26.5", string macOS = "26.1", string[]? archs = null, XcodeState state = XcodeState.Ready, bool commandLineTools = false,
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

    private sealed class FixedToolchains(ToolchainReport report) : IToolchainService
    {
        public ToolchainReport? Latest => report;
        public event EventHandler<ToolchainReport>? Updated { add { } remove { } }
        public Task<ToolchainReport> GetAsync(bool refresh = false, CancellationToken cancellationToken = default) => Task.FromResult(report);
    }
}
