using HCG.ExecutableMaker.BuildEngine.Mobile;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Infrastructure.Import;
using HCG.ExecutableMaker.Infrastructure.Persistence;
using HCG.ExecutableMaker.ProjectInspection;
using HCG.ExecutableMaker.ProjectInspection.Mobile;
using Microsoft.Extensions.Logging.Abstractions;

namespace HCG.ExecutableMaker.Tests;

/// <summary>
/// The recurring HCG Sentinel blocker, end to end: a stale imported copy (no modern scheme), refresh from source keeping the
/// selection, the deployment-target state, the build prerequisites and the diagnostics report.
/// </summary>
public sealed class SentinelEndToEndTests : IDisposable
{
    private readonly TempDir _t = new();
    private readonly AppPaths _paths;
    private readonly SqliteProjectRepository _projects;
    private readonly SqliteMobileSelectionStore _selections;
    private readonly ProjectService _service;

    public SentinelEndToEndTests()
    {
        _paths = new AppPaths(_t.Combine("app"));
        var db = new SqliteDatabase(_paths);
        _projects = new SqliteProjectRepository(db);
        _selections = new SqliteMobileSelectionStore(db);
        _service = new ProjectService(new ProjectImporter(_paths, TimeProvider.System),
            new ProjectInspectionService([new MobileProjectInspector(TimeProvider.System)], TimeProvider.System),
            _projects, new SqliteSettingsStore(db), new Toolchains(Report(Xcode())), _paths, TimeProvider.System, NullLogger<ProjectService>.Instance);
    }

    public void Dispose() => _t.Dispose();

    private sealed class Toolchains(ToolchainReport report) : IToolchainService
    {
        public ToolchainReport? Latest => report;
        public event EventHandler<ToolchainReport>? Updated { add { } remove { } }
        public Task<ToolchainReport> GetAsync(bool refresh = false, CancellationToken cancellationToken = default) => Task.FromResult(report);
    }

    private static XcodeStatus Xcode(string min = "15.0", XcodeState state = XcodeState.Ready) => new()
    {
        State = state, ActiveDeveloperDirectory = state == XcodeState.Ready ? "/Applications/Xcode.app/Contents/Developer" : null,
        VerifiedVersion = state == XcodeState.Ready ? "27.0" : null, Diagnosis = state == XcodeState.Ready ? "Ready" : "No Xcode application was found.",
        Sdks = ["iphoneos 27.0", "iphonesimulator 27.0"], Source = DeveloperDirectorySource.HcgSelection,
        IosDeploymentRange = new IosDeploymentRange(min, "27.2.99", null, "27.2"), IosSimulatorDeploymentRange = new IosDeploymentRange(min, "27.2.99", null, "27.2"),
        Installed = state == XcodeState.Ready ? [new XcodeInstallation("/Applications/Xcode.app", "/Applications/Xcode.app/Contents/Developer", "27.0", null)] : [],
    };

    private static ToolchainReport Report(XcodeStatus x) =>
        new(new HostEnvironment(TargetPlatform.MacOS, TargetArchitecture.Arm64, "macOS", ".NET", "osx"), [], null, null, DateTimeOffset.UtcNow) { Xcode = x };

    /// <summary>Sentinel as it was before its modern scheme existed: the legacy scheme only.</summary>
    private string LegacyOnlySentinel()
    {
        var root = _t.Combine("HCG Sentinel");
        MobileTestKit.CopyTree(Path.Combine(Fixtures.RepoRoot, "tests", "Fixtures", "XcodeDeployment", "Sentinel"), root);
        File.Delete(Path.Combine(root, "HCGSentinel.xcodeproj", "xcshareddata", "xcschemes", "HCGSentinelModern.xcscheme"));
        return root;
    }

    private static void AddModernScheme(string root) =>
        File.Copy(Path.Combine(Fixtures.RepoRoot, "tests", "Fixtures", "XcodeDeployment", "Sentinel", "HCGSentinel.xcodeproj", "xcshareddata", "xcschemes", "HCGSentinelModern.xcscheme"),
                  Path.Combine(root, "HCGSentinel.xcodeproj", "xcshareddata", "xcschemes", "HCGSentinelModern.xcscheme"));

    [Fact]
    public async Task A_stale_copy_is_detected_and_refresh_from_source_brings_the_modern_scheme_while_keeping_the_selection()
    {
        var origin = LegacyOnlySentinel();
        var project = (await _service.ImportDirectoryAsync(origin, null, CancellationToken.None)).Project;
        Assert.Equal("not a git checkout", project.SourceRevision);
        Assert.Matches(@"^\d+ files · sha256 [0-9a-f]{16}$", project.SourceFingerprint!);
        Assert.Equal(["HCGSentinel"], project.Analysis!.Mobile!.Ios!.Schemes.Select(s => s.Name));
        Assert.True((await _service.CompareWithSourceAsync(project, CancellationToken.None)).IsCurrent);
        await _selections.SaveAsync(new MobileSelection { ProjectId = project.Id.ToString(), Scheme = "HCGSentinel", Configuration = "Release" });

        // Sentinel gains its modern scheme after the import: Maker's copy does not have it.
        AddModernScheme(origin);
        var drift = await _service.CompareWithSourceAsync(project, CancellationToken.None);
        Assert.False(drift.IsCurrent);
        Assert.Equal(["HCGSentinel.xcodeproj/xcshareddata/xcschemes/HCGSentinelModern.xcscheme"], drift.Added);
        Assert.Contains("1 new", drift.Describe());

        var before = project.SourceFingerprint;
        var (result, reported) = await _service.RefreshFromSourceAsync(project, null, CancellationToken.None);
        Assert.Same(project, result.Project);
        Assert.False(reported.IsCurrent);
        Assert.NotEqual(before, project.SourceFingerprint);
        Assert.NotNull(project.RefreshedAt);
        Assert.Equal(["HCGSentinel", "HCGSentinelModern"], project.Analysis!.Mobile!.Ios!.Schemes.Select(s => s.Name).Order());
        Assert.True((await _service.CompareWithSourceAsync(project, CancellationToken.None)).IsCurrent);

        // Same project: id, stored record, saved selection; no leftover workspaces.
        var stored = await _projects.GetAsync(project.Id);
        Assert.Equal(project.SourceFingerprint, stored!.SourceFingerprint);
        Assert.NotNull(stored.RefreshedAt);
        Assert.Equal("HCGSentinel", (await _selections.LoadAsync(project.Id.ToString()))!.Scheme);
        Assert.Single(Directory.GetDirectories(_paths.WorkspacesDirectory));
        Assert.Empty(Directory.GetDirectories(project.WorkspacePath, "source.replaced-*"));

        // After the refresh the compatible scheme is discovered from the project and offered; nothing is inferred from names.
        var summary = IosBuildSelection.Summarize(project.Analysis.Mobile.Ios, "HCGSentinel", "Release", Xcode());
        Assert.Equal(["HCGSentinelModern"], summary.CompatibleSchemes.Select(c => c.Scheme));
    }

    [Fact]
    public async Task A_refresh_from_a_missing_original_fails_and_keeps_the_copy()
    {
        var origin = LegacyOnlySentinel();
        var project = (await _service.ImportDirectoryAsync(origin, null, CancellationToken.None)).Project;
        var fingerprint = project.SourceFingerprint;
        Directory.Delete(origin, recursive: true);
        Assert.False((await _service.CompareWithSourceAsync(project, CancellationToken.None)).OriginAvailable);
        var ex = await Assert.ThrowsAsync<ImportRejectedException>(() => _service.RefreshFromSourceAsync(project, null, CancellationToken.None));
        Assert.Contains("no longer exists", ex.Message);
        Assert.Equal(fingerprint, project.SourceFingerprint);
        Assert.True(File.Exists(Path.Combine(project.SourceDirectory, "HCGSentinel.xcodeproj", "project.pbxproj")));
    }

    [Fact]
    public void The_git_revision_of_the_original_is_read_from_disk_including_packed_refs_and_worktrees()
    {
        var repo = _t.Combine("repo");
        _t.Write("repo/.git/HEAD", "ref: refs/heads/main\n");
        _t.Write("repo/.git/refs/heads/main", "03bc6e55f0a75c32df0a05415d70b55e95f5ad9d\n");
        Directory.CreateDirectory(Path.Combine(repo, "sub"));
        Assert.Equal("git 03bc6e55f0a7 (main)", GitRevision.Read(Path.Combine(repo, "sub")));

        _t.Write("packed/.git/HEAD", "ref: refs/heads/release\n");
        _t.Write("packed/.git/packed-refs", "# pack-refs\nff36b31aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa refs/heads/release\n");
        Assert.Equal("git ff36b31aaaaa (release)", GitRevision.Read(_t.Combine("packed")));

        _t.Write("main/.git/refs/heads/topic", "1234567890abcdef1234567890abcdef12345678\n");
        _t.Write("main/.git/worktrees/wt/HEAD", "ref: refs/heads/topic\n");
        _t.Write("main/.git/worktrees/wt/commondir", "../..\n");
        _t.Write("wt/.git", "gitdir: " + _t.Combine("main/.git/worktrees/wt") + "\n");
        Assert.Equal("git 1234567890ab (topic)", GitRevision.Read(_t.Combine("wt")));
        Assert.Null(GitRevision.Read(_t.Combine("app")));
    }

    // ───────────── State and prerequisites ─────────────

    private static ProjectAnalysis Analysis(MobileAnalysis m) => new()
    {
        InspectorId = "mobile", ProjectName = "HCG Sentinel", Language = "Swift", Framework = "Xcode", ApplicationType = "iOS application",
        Support = SupportLevel.Supported, SupportSummary = "Supported", Mobile = m, AnalyzedAt = DateTimeOffset.UtcNow,
    };

    private IosProjectInfo Sentinel()
    {
        var root = _t.Combine("full");
        if (!Directory.Exists(root)) MobileTestKit.CopyTree(Path.Combine(Fixtures.RepoRoot, "tests", "Fixtures", "XcodeDeployment", "Sentinel"), root);
        return XcodeProjectAnalyzer.Analyze(SourceIndex.Build(root, CancellationToken.None), "HCGSentinel.xcodeproj", out _);
    }

    private static MobileAnalysis Mobile(IosProjectInfo ios) => new() { Framework = MobileFramework.NativeIos, Platforms = [TargetPlatform.IOS], Ios = ios, ApplicationIds = ["com.hcg.sentinel"] };

    [Fact]
    public void An_installed_xcode_that_rejects_the_minimum_ios_is_blocked_by_the_deployment_target_never_a_missing_toolchain()
    {
        var m = Mobile(Sentinel());
        var legacy = MobileCapabilities.Evaluate(m, Report(Xcode()), new MobileBuildOptions { Framework = m.Framework, Output = MobileOutput.IosSimulatorApp, Scheme = "HCGSentinel" });
        var blocked = MobileBuildState.Describe(Analysis(m), m, legacy, null, null, null, null);
        Assert.Equal("Blocked — deployment target incompatible", blocked.Tag);
        Assert.Equal("error", blocked.Tone);
        Assert.Contains("The selected Xcode is installed", blocked.Detail);
        Assert.DoesNotContain("missing", blocked.Tag, StringComparison.OrdinalIgnoreCase);

        var none = MobileCapabilities.Evaluate(m, Report(Xcode(state: XcodeState.NotInstalled)), new MobileBuildOptions { Framework = m.Framework, Output = MobileOutput.IosSimulatorApp, Scheme = "HCGSentinel" });
        Assert.Equal("Required toolchain missing", MobileBuildState.Describe(Analysis(m), m, none, null, null, null, null).Tag);

        // Installed but unusable for another reason (not the deployment target): "Selected toolchain incompatible".
        var old = new MobileOutputOption(MobileOutput.IosSimulatorApp, false, "Simulator app", "Xcode 14.0 is too old")
        { Blockers = [new ReadinessItem("Xcode", ReadinessState.IncompatibleVersion, true, "Xcode 14.0 is installed; this project needs Xcode 15 or later.")] };
        var incompatible = MobileBuildState.Describe(Analysis(m), m, [old], null, null, null, null);
        Assert.Equal("Selected toolchain incompatible", incompatible.Tag);
        Assert.StartsWith("Installed, but not usable", incompatible.Detail);

        var modern = MobileCapabilities.Evaluate(m, Report(Xcode()), new MobileBuildOptions { Framework = m.Framework, Output = MobileOutput.IosSimulatorApp, Scheme = "HCGSentinelModern" });
        Assert.All(modern.Where(o => o.Output == MobileOutput.IosSimulatorApp), o => Assert.True(o.IsAvailable, o.Reason));
    }

    [Fact]
    public void Prerequisites_list_every_blocker_and_the_project_trust_confirmation()
    {
        var m = Mobile(Sentinel());
        var legacy = MobileCapabilities.Evaluate(m, Report(Xcode()), new MobileBuildOptions { Framework = m.Framework, Output = MobileOutput.IosSimulatorApp, Scheme = "HCGSentinel" });
        var blocked = MobileBuildState.Prerequisites(null, legacy, null, trustAcknowledged: false, isBuilding: false);
        Assert.Contains(blocked, p => p is { Title: "Output", Met: false });
        Assert.Contains(blocked, p => p is { Title: "Deployment target", Met: false } && p.Detail.Contains("14.7"));
        Assert.Contains(blocked, p => p is { Title: "Project trust", Met: false } && p.Detail.Contains("I trust this project"));

        var modern = MobileCapabilities.Evaluate(m, Report(Xcode()), new MobileBuildOptions { Framework = m.Framework, Output = MobileOutput.IosSimulatorApp, Scheme = "HCGSentinelModern" });
        var sim = modern.Single(o => o.Output == MobileOutput.IosSimulatorApp);
        var ready = new BuildReadiness("Simulator app", [new ReadinessItem("Xcode", ReadinessState.Ready, true, "Xcode 27.0")]);
        var notTrusted = MobileBuildState.Prerequisites(sim, modern, ready, trustAcknowledged: false, isBuilding: false);
        Assert.Equal(["Project trust"], notTrusted.Where(p => !p.Met).Select(p => p.Title));
        Assert.All(MobileBuildState.Prerequisites(sim, modern, ready, trustAcknowledged: true, isBuilding: false), p => Assert.True(p.Met, p.Title));
    }

    // ───────────── Diagnostics ─────────────

    [Fact]
    public async Task The_diagnostics_report_names_the_build_the_source_the_xcode_ranges_the_selection_and_each_targets_deployment()
    {
        var origin = LegacyOnlySentinel();
        AddModernScheme(origin);
        var project = (await _service.ImportDirectoryAsync(origin, null, CancellationToken.None)).Project;
        var drift = await _service.CompareWithSourceAsync(project, CancellationToken.None);
        var selection = new MobileSelection { ProjectId = project.Id.ToString(), Scheme = "HCGSentinel" };
        string Report_(MobileSelection s) => MobileDiagnostics.Create(new MobileDiagnosticsInput
        {
            MakerBuild = "HCG Executable Maker 0.3.0 (commit abc123, built 2026-11-15)", MakerExecutable = "/Applications/HCG Executable Maker.app/Contents/MacOS/HCGExecutableMaker",
            MakerAppBundle = "/Applications/HCG Executable Maker.app", Project = project, Drift = drift, Toolchains = Report(Xcode()), Selection = s,
        });
        var text = Report_(selection);
        Assert.Contains("Build: HCG Executable Maker 0.3.0 (commit abc123", text);
        Assert.Contains("App bundle: /Applications/HCG Executable Maker.app", text);
        Assert.Contains($"Imported from: {origin}", text);
        Assert.Contains("Revision HCG copied: not a git checkout", text);
        Assert.Contains("Copy vs original: HCG's copy matches the original's current files.", text);
        Assert.Contains("Selected for HCG's builds: /Applications/Xcode.app/Contents/Developer  (source: chosen in HCG", text);
        Assert.Contains("iPhoneOS (device) SDK 27.2: deployment iOS 15.0–27.2.99 (MinimumDeploymentTarget/MaximumDeploymentTarget in /Applications/Xcode.app/Contents/Developer/Platforms/iPhoneOS.platform/Developer/SDKs/iPhoneOS.sdk/SDKSettings.json)", text);
        Assert.Contains("Scheme: HCGSentinel", text);
        Assert.Contains("     HCGSentinel: iOS 14.7", text);
        Assert.Contains("← outside the SDK's range", text);
        Assert.Contains("Compatible schemes: HCGSentinelModern (iOS 15.0)", text);
        Assert.Contains("State with no output chosen: Blocked — deployment target incompatible", text);
        Assert.DoesNotContain("password", text, StringComparison.OrdinalIgnoreCase);

        // The command per output follows the selection: after choosing the modern scheme, every command names it.
        var modern = Report_(selection with { Scheme = "HCGSentinelModern" });
        Assert.Contains("Scheme: HCGSentinelModern", modern);
        var commands = modern.Split('\n').Where(l => l.TrimStart().StartsWith("Command:")).ToList();
        Assert.Equal(3, commands.Count);
        Assert.All(commands, c => Assert.Contains("-scheme HCGSentinelModern", c));
        Assert.Contains("-configuration Debug -sdk iphonesimulator -destination \"generic/platform=iOS Simulator\"", commands[0]);
        Assert.Contains("-configuration Release -destination generic/platform=iOS … archive", commands[1]);
        Assert.Contains("iOS Simulator build — available", modern);
        Assert.Contains("iOS Archive (.xcarchive) — available", modern);
        Assert.Contains("HCGSentinelModern · Simulator · Debug: Compatible", modern);
    }
}
