using HCG.ExecutableMaker.BuildEngine;
using HCG.ExecutableMaker.BuildEngine.Mobile;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Infrastructure;
using HCG.ExecutableMaker.Infrastructure.Persistence;
using HCG.ExecutableMaker.Packaging.Mobile;
using HCG.ExecutableMaker.ProjectInspection.Mobile;

namespace HCG.ExecutableMaker.Tests;

/// <summary>
/// The reported Sentinel blocker, end to end through the paths Mobile Build Studio and the build engine really use
/// (MobileCapabilities → readiness → engine gate → xcodebuild arguments), with HCG Sentinel main (03bc6e5) and an Xcode
/// whose iPhoneOS SDK supports iOS 15.0 and later — the Xcode on the reporting Mac. The legacy scheme (iOS 14.7) must
/// stay blocked; the modern scheme (iOS 15.0) must be selectable, offered, checked and built as that same scheme.
/// </summary>
public class SentinelSchemeSelectionTests : IDisposable
{
    private readonly MobileTestKit _k = new();
    private readonly AppPaths _paths;
    private readonly SqliteBuildRepository _builds;

    public SentinelSchemeSelectionTests()
    {
        _paths = new AppPaths(_k.Temp.Combine("app"));
        _paths.EnsureCreated();
        _builds = new SqliteBuildRepository(new SqliteDatabase(_paths));
    }

    public void Dispose() => _k.Dispose();

    /// <summary>An Xcode whose iPhoneOS SDK starts at iOS 15.0 (as reported on the Mac), and one that still accepts 14.7 (hosted Xcode 26.6).</summary>
    internal static ToolchainReport Xcode(string min = "15.0", string max = "27.0.99") =>
        new(new HostEnvironment(TargetPlatform.MacOS, TargetArchitecture.Arm64, "macOS", ".NET", "osx"),
            [new ToolchainInfo { Id = ToolchainIds.AppleSigningIdentities, DisplayName = "Signing identities", Category = "Apple", Status = ToolchainStatus.Missing }], null, null, DateTimeOffset.UtcNow)
        {
            Xcode = new XcodeStatus
            {
                State = XcodeState.Ready, ActiveDeveloperDirectory = "/Applications/Xcode.app/Contents/Developer", VerifiedVersion = "27.0", VerifiedBuild = "27A1",
                Diagnosis = "Ready", Sdks = ["iphoneos 27.0", "iphonesimulator 27.0"], IosDeploymentRange = new IosDeploymentRange(min, max, "16.0", "27.0"),
            },
        };

    private sealed class FixedToolchains(ToolchainReport report) : IToolchainService
    {
        public ToolchainReport? Latest => report;
        public event EventHandler<ToolchainReport>? Updated { add { } remove { } }
        public Task<ToolchainReport> GetAsync(bool refresh = false, CancellationToken cancellationToken = default) => Task.FromResult(report);
    }

    private sealed class Observer : IBuildObserver
    {
        public List<BuildLogLine> Lines { get; } = [];
        public void OnLog(BuildLogLine line) { lock (Lines) Lines.Add(line); }
        public string Text { get { lock (Lines) return string.Join('\n', Lines.Select(l => l.Text)); } }
    }

    private async Task<ProjectRecord> SentinelProject(ToolchainReport report)
    {
        var ws = Path.Combine(_paths.WorkspacesDirectory, Guid.NewGuid().ToString("N"));
        MobileTestKit.CopyTree(MobileTestKit.Fixture(Path.Combine("XcodeDeployment", "Sentinel")), Path.Combine(ws, "source"));
        var p = new ProjectRecord { Id = Guid.NewGuid(), Name = "HCG Sentinel", SourceKind = ProjectSourceKind.Directory, SourceLocation = "/orig", WorkspacePath = ws, ImportedAt = DateTimeOffset.UtcNow };
        p.Analysis = await MobileInspectionTests.Service().InspectAsync(p.SourceDirectory, report, CancellationToken.None);
        await new SqliteProjectRepository(new SqliteDatabase(_paths)).UpsertAsync(p);
        return p;
    }

    private MobileBuildRequest Request(ProjectRecord p, MobileBuildOptions o) => new()
    {
        Project = p, TrustAcknowledged = true,
        Settings = new BuildSettings
        {
            ProjectRelativePath = "HCGSentinel.xcodeproj", Platform = TargetPlatform.IOS, Architecture = TargetArchitecture.Universal,
            OutputDirectory = _k.Temp.Combine("out"), Timeout = TimeSpan.FromMinutes(5),
            Identity = new AppIdentity { ApplicationName = "HCG Sentinel", Version = "0.1.0", ApplicationId = "com.hcg.sentinel" },
            Mobile = o,
        },
    };

    private MobileBuildService Service(IBuildWorker worker, ToolchainReport report) =>
        new(worker, new FixedToolchains(report), _builds, new MobileArtifactVerifier(new HCG.ExecutableMaker.Infrastructure.Processes.ProcessRunner()), _paths, TimeProvider.System);

    [Fact]
    public async Task Engine_builds_the_modern_scheme_it_was_given_on_an_xcode_that_starts_at_ios_15()
    {
        var report = Xcode();
        var p = await SentinelProject(report);
        var worker = new ScriptedMobileWorker((_, output, _) => { output(OutputStream.StdOut, "** BUILD SUCCEEDED **"); return ScriptedMobileWorker.Exit(0); });
        var observer = new Observer();
        var options = new MobileBuildOptions { Framework = MobileFramework.NativeIos, Output = MobileOutput.IosSimulatorApp, Scheme = "HCGSentinelModern", Variant = "Debug" };

        var build = await Service(worker, report).RunAsync(Request(p, options), observer, CancellationToken.None);

        // The engine must not refuse the modern scheme because the legacy one is out of range.
        Assert.DoesNotContain("not available", build.FailureReason ?? "");
        var xcodebuild = Assert.Single(worker.Calls);
        var args = xcodebuild.Arguments.ToList();
        Assert.Equal("HCGSentinelModern", args[args.IndexOf("-scheme") + 1]);
        Assert.Contains("iphonesimulator", args);
    }

    // ------------------------------------------------------------------ the paths the Studio uses

    private static MobileAnalysis Analysis(string root)
    {
        MobileTestKit.CopyTree(MobileTestKit.Fixture(Path.Combine("XcodeDeployment", "Sentinel")), root);
        return new MobileAnalysis
        {
            Framework = MobileFramework.NativeIos, Platforms = [TargetPlatform.IOS], ApplicationIds = ["com.hcg.sentinel"],
            Ios = XcodeProjectAnalyzer.Analyze(HCG.ExecutableMaker.ProjectInspection.SourceIndex.Build(root, CancellationToken.None), "HCGSentinel.xcodeproj", out _),
        };
    }

    private static MobileBuildOptions Pick(string? scheme) => new() { Framework = MobileFramework.NativeIos, Output = MobileOutput.IosSimulatorApp, Scheme = scheme };

    [Fact]
    public void Outputs_are_judged_for_the_selected_scheme_not_the_first_one()
    {
        var m = Analysis(_k.Temp.Combine("sentinel"));
        var xcode = Xcode();

        // No scheme chosen yet: the project default is the legacy HCGSentinel (iOS 14.7) — every output blocked by its deployment target.
        var byDefault = MobileCapabilities.Evaluate(m, xcode);
        Assert.All(byDefault, o => Assert.False(o.IsAvailable));
        Assert.All(byDefault, o => Assert.Contains("HCGSentinel (scheme HCGSentinel) needs iOS 14.7", o.Reason));

        // The modern scheme selected: Simulator and unsigned archive are available; the IPA still needs signing (no certificates here).
        var modern = MobileCapabilities.Evaluate(m, xcode, Pick("HCGSentinelModern"));
        Assert.True(modern.Single(o => o.Output == MobileOutput.IosSimulatorApp).IsAvailable);
        Assert.True(modern.Single(o => o.Output == MobileOutput.IosArchive).IsAvailable);
        var ipa = modern.Single(o => o.Output == MobileOutput.IosIpa);
        Assert.False(ipa.IsAvailable);
        Assert.DoesNotContain("deployment", ipa.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("certificate", ipa.Reason, StringComparison.OrdinalIgnoreCase);

        // An Xcode that still accepts iOS 14.7 (hosted Xcode 26.6): the legacy scheme is unchanged and buildable.
        Assert.True(MobileCapabilities.Evaluate(m, Xcode("12.0", "26.5.99"), Pick("HCGSentinel")).Single(o => o.Output == MobileOutput.IosSimulatorApp).IsAvailable);
    }

    [Fact]
    public void A_deployment_target_block_is_named_as_such_and_never_as_a_missing_toolchain()
    {
        var m = Analysis(_k.Temp.Combine("sentinel"));
        var state = MobileBuildState.Classify(MobileReadiness.Evaluate(m, MobileOutput.IosSimulatorApp, Pick("HCGSentinel"), Xcode()), "iOS Simulator build");
        Assert.Equal("Blocked — deployment target incompatible", state.Tag);
        Assert.Equal("error", state.Tone);
        Assert.Contains("or choose scheme HCGSentinelModern", state.Detail);
        Assert.DoesNotContain("toolchain", state.Tag, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(MobileBuildState.Ready, MobileBuildState.Classify(MobileReadiness.Evaluate(m, MobileOutput.IosSimulatorApp, Pick("HCGSentinelModern"), Xcode()), "x").Tag);

        // Scheme picker labels say which scheme the selected Xcode accepts; nothing is selected for the user.
        var choices = MobileReadiness.SchemeChoices(m.Ios!, Xcode());
        Assert.False(choices.Single(c => c.Name == "HCGSentinel").Compatible);
        Assert.True(choices.Single(c => c.Name == "HCGSentinelModern").Compatible);
        Assert.Contains("HCGSentinel — iOS 14.7+ · outside Xcode 27.0's range iOS 15.0–27.0.99", choices.Single(c => c.Name == "HCGSentinel").Label);
        Assert.Equal("HCGSentinel", MobileReadiness.DefaultScheme(m.Ios!));
    }

    [Fact]
    public void Installed_but_unusable_or_incompatible_toolchains_are_not_reported_as_missing()
    {
        BuildReadiness R(params ReadinessItem[] items) => new("t", items);
        Assert.Equal(MobileBuildState.ToolchainMissing, MobileBuildState.Classify(R(new ReadinessItem("Xcode", ReadinessState.NotFound, true, "Xcode is not installed.")), "x").Tag);
        Assert.Equal(MobileBuildState.ToolchainSetup, MobileBuildState.Classify(R(new ReadinessItem("Xcode", ReadinessState.SetupRequired, true, "Command Line Tools are selected.")), "x").Tag);
        Assert.Equal(MobileBuildState.ToolchainUnusable, MobileBuildState.Classify(R(new ReadinessItem("Xcode", ReadinessState.InvalidPath, true, "xcodebuild failed.")), "x").Tag);
        Assert.Equal(MobileBuildState.ToolchainIncompatible, MobileBuildState.Classify(R(new ReadinessItem("JDK", ReadinessState.IncompatibleVersion, true, "JDK 11 is too old.")), "x").Tag);
        Assert.Equal(MobileBuildState.SigningRequired, MobileBuildState.Classify(R(new ReadinessItem("Apple Development certificate", ReadinessState.SetupRequired, true, "None.")), "x").Tag);
        Assert.Equal(MobileBuildState.OtherHost, MobileBuildState.Classify(R(new ReadinessItem("Mac with Xcode", ReadinessState.OtherHost, true, "Not a Mac.")), "x").Tag);
        // The most specific blocker names the state; the others are still listed.
        var both = MobileBuildState.Classify(R(new ReadinessItem("Apple Development certificate", ReadinessState.SetupRequired, true, "None."),
            new("Deployment target", ReadinessState.IncompatibleVersion, true, "needs iOS 14.7")), "x");
        Assert.Equal(MobileBuildState.DeploymentBlocked, both.Tag);
        Assert.Contains("Apple Development certificate (signing required)", both.Detail);
    }

    [Fact]
    public void Every_prerequisite_is_listed_including_the_per_build_trust_confirmation()
    {
        var m = Analysis(_k.Temp.Combine("sentinel"));
        var blocked = MobileReadiness.Evaluate(m, MobileOutput.IosSimulatorApp, Pick("HCGSentinel"), Xcode());
        var list = MobileBuildState.Prerequisites(blocked, outputSelected: false, trustAcknowledged: false);
        Assert.Contains(list, l => l.StartsWith("Choose an available output", StringComparison.Ordinal));
        Assert.Contains(list, l => l.StartsWith("Deployment target — Select an Xcode that supports iOS 14.7", StringComparison.Ordinal));
        Assert.Contains(list, l => l.Contains("I trust this project and want to run its build scripts", StringComparison.Ordinal));

        var ready = MobileReadiness.Evaluate(m, MobileOutput.IosSimulatorApp, Pick("HCGSentinelModern"), Xcode());
        Assert.Equal(["Tick “I trust this project and want to run its build scripts on this computer” (section 5). It is confirmed per build and clears after each build."],
            MobileBuildState.Prerequisites(ready, outputSelected: true, trustAcknowledged: false));
        Assert.Empty(MobileBuildState.Prerequisites(ready, outputSelected: true, trustAcknowledged: true));
    }

    [Fact]
    public void A_saved_scheme_the_imported_copy_does_not_have_is_a_blocker_never_a_silent_fallback()
    {
        var m = Analysis(_k.Temp.Combine("sentinel"));
        var r = MobileReadiness.Evaluate(m, MobileOutput.IosSimulatorApp, Pick("HCGSentinelNext"), Xcode());
        var b = Assert.Single(r.Blockers);
        Assert.Equal("Scheme", b.Tool);
        Assert.Contains("not in the imported copy", b.Detail);
        Assert.Contains("Refresh source", b.Fix);
        Assert.Equal(MobileBuildState.SchemeNotInSnapshot, MobileBuildState.Classify(r, "x").Tag);
        Assert.DoesNotContain(r.Items, i => i.Tool == "Deployment target"); // HCGSentinel is not judged in its place

        var input = new MobilePlanInput
        {
            Options = Pick("HCGSentinelNext"), Analysis = m, SourceDirectory = _k.Temp.Combine("sentinel"), WorkDirectory = _k.Temp.Combine("w"),
            Toolchains = Xcode(), Environment = new Dictionary<string, string>(), ToolHomeDirectory = _k.Temp.Combine("h"),
        };
        Assert.Throws<BuildValidationException>(() => XcodePlanner.ResolveContainer(input));
    }

    [Fact]
    public async Task The_project_summary_names_the_scheme_that_builds_instead_of_a_missing_toolchain()
    {
        var root = _k.Temp.Combine("sentinel summary");
        MobileTestKit.CopyTree(MobileTestKit.Fixture(Path.Combine("XcodeDeployment", "Sentinel")), root);
        var a = await MobileInspectionTests.Service().InspectAsync(root, Xcode(), CancellationToken.None);
        Assert.Equal(SupportLevel.Supported, a.Support);
        Assert.Contains("with scheme HCGSentinelModern", a.SupportSummary);
        Assert.Contains("Not with scheme HCGSentinel (blocked — deployment target incompatible)", a.SupportSummary);
        Assert.DoesNotContain("toolchains are missing", a.SupportSummary);
    }

    [Fact]
    public async Task Engine_refuses_the_legacy_scheme_with_the_deployment_reason_and_logs_the_scheme_it_builds()
    {
        var report = Xcode();
        var p = await SentinelProject(report);
        var worker = new ScriptedMobileWorker((_, output, _) => { output(OutputStream.StdOut, "** BUILD SUCCEEDED **"); return ScriptedMobileWorker.Exit(0); });

        var observer = new Observer();
        var legacy = await Service(worker, report).RunAsync(Request(p, new MobileBuildOptions { Framework = MobileFramework.NativeIos, Output = MobileOutput.IosSimulatorApp, Scheme = "HCGSentinel" }), observer, CancellationToken.None);
        Assert.Equal(BuildStatus.Failed, legacy.Status);
        Assert.Contains("HCGSentinel (scheme HCGSentinel) needs iOS 14.7", legacy.FailureReason);
        Assert.Contains("Scheme: HCGSentinel · builds HCGSentinel (iOS 14.7+) · selected Xcode 27.0 accepts iOS 15.0–27.0.99", observer.Text);
        Assert.Empty(worker.Calls); // nothing ran

        var modernLog = new Observer();
        await Service(worker, report).RunAsync(Request(p, new MobileBuildOptions { Framework = MobileFramework.NativeIos, Output = MobileOutput.IosSimulatorApp, Scheme = "HCGSentinelModern" }), modernLog, CancellationToken.None);
        Assert.Contains("Scheme: HCGSentinelModern · builds HCGSentinelModern (iOS 15.0+)", modernLog.Text);
        Assert.Contains("-scheme HCGSentinelModern", modernLog.Text);
    }

    // ------------------------------------------------------------------ the imported copy: freshness, refresh, report

    private ProjectService Projects(ToolchainReport report, out SqliteProjectRepository repo)
    {
        var db = new SqliteDatabase(_paths);
        repo = new SqliteProjectRepository(db);
        return new ProjectService(new HCG.ExecutableMaker.Infrastructure.Import.ProjectImporter(_paths, TimeProvider.System), MobileInspectionTests.Service(),
            repo, new SqliteSettingsStore(db), new FixedToolchains(report), _paths, TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<ProjectService>.Instance);
    }

    /// <summary>The Sentinel fixture as the user's folder: with a .git directory and generated folders the import never copies.</summary>
    private string OriginalFolder(string name)
    {
        var dir = _k.Temp.Combine(name);
        MobileTestKit.CopyTree(MobileTestKit.Fixture(Path.Combine("XcodeDeployment", "Sentinel")), dir);
        Directory.CreateDirectory(Path.Combine(dir, ".git", "refs", "heads"));
        File.WriteAllText(Path.Combine(dir, ".git", "HEAD"), "ref: refs/heads/main\n");
        File.WriteAllText(Path.Combine(dir, ".git", "refs", "heads", "main"), "810c6f99103a5e4f60e91a6fecda8259be01e0da\n");
        foreach (var generated in new[] { "DerivedData", ".build", ".swiftpm" })
        {
            Directory.CreateDirectory(Path.Combine(dir, generated));
            File.WriteAllText(Path.Combine(dir, generated, "noise.txt"), Guid.NewGuid().ToString());
        }
        return dir;
    }

    [Fact]
    public async Task A_fresh_import_matches_its_original_and_a_changed_original_is_reported_stale()
    {
        var service = Projects(Xcode(), out _);
        var original = OriginalFolder("HCG Sentinel");
        var p = (await service.ImportDirectoryAsync(original, null, CancellationToken.None)).Project;

        var fresh = SourceSnapshot.Inspect(p);
        Assert.True(fresh.MatchesOriginal, fresh.Describe()); // .git, DerivedData, .build and .swiftpm are excluded on both sides
        Assert.Equal("810c6f99103a5e4f60e91a6fecda8259be01e0da", fresh.OriginalCommit);
        Assert.Contains("matches the original (Git 810c6f99103a on main)", fresh.Describe());

        File.WriteAllText(Path.Combine(original, ".git", "refs", "heads", "main"), "03bc6e55f0a75c32df0a05415d70b55e95f5ad9d\n");
        File.AppendAllText(Path.Combine(original, "HCGSentinel.xcodeproj", "project.pbxproj"), "\n// changed upstream\n");
        var stale = SourceSnapshot.Inspect(p);
        Assert.False(stale.MatchesOriginal);
        Assert.Contains("CHANGED since it was imported", stale.Describe());
        Assert.Contains("now at Git 03bc6e55f0a7", stale.Describe());
        Assert.Same(HCG.ExecutableMaker.Infrastructure.Import.ProjectImporter.ExcludedDirectoryNames, SourceSnapshot.ExcludedDirectoryNames);
    }

    [Fact]
    public async Task Refresh_source_replaces_the_copy_in_place_and_keeps_project_id_scheme_and_history()
    {
        var service = Projects(Xcode(), out var repo);
        var original = OriginalFolder("HCG Sentinel");
        // An old copy whose modern scheme does not exist yet (as in a snapshot taken before the scheme was added).
        File.Delete(Path.Combine(original, "HCGSentinel.xcodeproj", "xcshareddata", "xcschemes", "HCGSentinelModern.xcscheme"));
        var p = (await service.ImportDirectoryAsync(original, null, CancellationToken.None)).Project;
        await service.SetMobileSchemeAsync(p.Id, "HCGSentinelModern");
        var before = MobileReadiness.Evaluate(p.Analysis!.Mobile!, MobileOutput.IosSimulatorApp, Pick("HCGSentinelModern"), Xcode());
        Assert.Contains(before.Blockers, b => b.Tool == "Scheme");

        // The original gains the scheme; Refresh source picks it up.
        MobileTestKit.CopyTree(MobileTestKit.Fixture(Path.Combine("XcodeDeployment", "Sentinel")), _k.Temp.Combine("upstream"));
        File.Copy(Path.Combine(_k.Temp.Combine("upstream"), "HCGSentinel.xcodeproj", "xcshareddata", "xcschemes", "HCGSentinelModern.xcscheme"),
            Path.Combine(original, "HCGSentinel.xcodeproj", "xcshareddata", "xcschemes", "HCGSentinelModern.xcscheme"));
        var importedAt = p.ImportedAt;
        var workspacesBefore = Directory.GetDirectories(_paths.WorkspacesDirectory).Length;

        var result = await service.RefreshSourceAsync(p, null, null, CancellationToken.None);

        Assert.True(result.Changed);
        Assert.Equal(p.Id, (await repo.GetAsync(p.Id))!.Id);
        Assert.Equal(workspacesBefore, Directory.GetDirectories(_paths.WorkspacesDirectory).Length); // the temporary import was removed
        Assert.Equal("HCGSentinelModern", await service.GetMobileSchemeAsync(p.Id));             // user choice kept
        var stored = (await repo.GetAsync(p.Id))!;
        Assert.True(stored.ImportedAt >= importedAt);
        Assert.Contains(stored.Analysis!.Mobile!.Ios!.Schemes, s => s.Name == "HCGSentinelModern");
        var after = MobileReadiness.Evaluate(stored.Analysis.Mobile, MobileOutput.IosSimulatorApp, Pick("HCGSentinelModern"), Xcode());
        Assert.True(after.CanBuild, string.Join(" ", after.Blockers.Select(b => b.Detail)));
        Assert.True(SourceSnapshot.Inspect(stored).MatchesOriginal);
        Assert.True(Directory.Exists(original)); // the original is only read
    }

    [Fact]
    public async Task Refresh_from_a_new_zip_updates_the_location_and_the_preserved_archive()
    {
        var service = Projects(Xcode(), out var repo);
        var src = OriginalFolder("zip src");
        var zip1 = _k.Temp.Combine("HCG-Sentinel-old.zip");
        System.IO.Compression.ZipFile.CreateFromDirectory(src, zip1);
        var p = (await service.ImportZipAsync(zip1, null, CancellationToken.None)).Project;
        File.AppendAllText(Path.Combine(src, "README.md"), "\nnewer\n");
        var zip2 = _k.Temp.Combine("HCG-Sentinel-main.zip");
        System.IO.Compression.ZipFile.CreateFromDirectory(src, zip2);

        await Assert.ThrowsAsync<ImportRejectedException>(() => service.RefreshSourceAsync(p, src, null, CancellationToken.None)); // a folder for a ZIP project
        var result = await service.RefreshSourceAsync(p, zip2, null, CancellationToken.None);

        Assert.True(result.Changed);
        var stored = (await repo.GetAsync(p.Id))!;
        Assert.Equal(zip2, stored.SourceLocation);
        Assert.Equal(SourceSnapshot.FileSha256(zip2), SourceSnapshot.FileSha256(stored.PreservedArchivePath!));
        Assert.True(SourceSnapshot.Inspect(stored).MatchesOriginal);
    }

    [Fact]
    public async Task The_diagnostic_report_identifies_maker_xcode_copy_revision_schemes_and_state_per_scheme()
    {
        var service = Projects(Xcode(), out _);
        var p = (await service.ImportDirectoryAsync(OriginalFolder("HCG Sentinel"), null, CancellationToken.None)).Project;
        var text = MobileDiagnosticReport.Create(p, Xcode(), "HCGSentinelModern", _paths, DateTimeOffset.UtcNow);

        Assert.Contains("Build: HCG Executable Maker ", text);
        Assert.Contains("(commit ", text);
        Assert.Contains("Version: Xcode 27.0 (27A1)", text);
        Assert.Contains("deployment targets iOS 15.0 to 27.0.99", text);
        Assert.Contains("Original Git revision: 810c6f99103a5e4f60e91a6fecda8259be01e0da (main)", text);
        Assert.Contains("Copy vs original: The imported copy matches the original", text);
        Assert.Contains("HCGSentinel.xcodeproj/project.pbxproj: SHA-256 ", text);
        Assert.Contains("HCGSentinelModern.xcscheme: SHA-256 ", text);
        Assert.Contains("HCGSentinelModern — iOS 15.0+ · supported by Xcode 27.0", text);
        Assert.Contains("Selected scheme: HCGSentinelModern", text);
        Assert.Contains("  HCGSentinel · iOS Simulator build: [Blocked — deployment target incompatible]", text);
        Assert.Contains("* HCGSentinelModern · iOS Simulator build: [Ready to build]", text);
        Assert.Contains("* HCGSentinelModern · iOS IPA", text);
        Assert.Contains("[Signing required]", text);
        Assert.Contains("-scheme HCGSentinelModern -configuration Debug -sdk iphonesimulator", text);
    }
}
