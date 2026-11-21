using System.IO.Compression;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.Jailbreak;

namespace HCG.ExecutableMaker.Tests;

public sealed class UnleashifyTests : IDisposable
{
    private readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    private static ProjectAnalysis IosAnalysis(bool withTheos = false) => new()
    {
        InspectorId = "mobile", ProjectName = "HCGSentinel", Language = "Swift", Framework = "SwiftUI", ApplicationType = "iOS app",
        Support = SupportLevel.Supported, SupportSummary = "", AnalyzedAt = DateTimeOffset.UtcNow,
        Mobile = new MobileAnalysis
        {
            Framework = MobileFramework.NativeIos,
            Ios = new IosProjectInfo
            {
                ProjectPath = "HCGSentinel.xcodeproj",
                Targets = [new IosTargetInfo { Name = "HCGSentinelModern", ProductType = "com.apple.product-type.application", BundleIdentifier = "com.hcg.sentinel", DeploymentTarget = "15.0" }],
            },
        },
        Native = withTheos ? new NativeAnalysis { Projects = [new NativeProjectInfo { Kind = NativeProjectKind.Theos, Directory = "tweak", ManifestPath = "tweak/Makefile", Name = "Tweak" }] } : null,
    };

    [Fact]
    public void Only_the_app_mode_can_be_built_and_the_others_say_why()
    {
        var modes = Unleashify.Modes(IosAnalysis());
        Assert.Equal(["Jailbreak App", "Jailbreak Tweak", "App + Tweak"], modes.Select(m => m.Label));
        Assert.Equal([true, false, false], modes.Select(m => m.Available));
        Assert.Contains("does not turn an app into a tweak", modes[1].Reason, StringComparison.Ordinal);
        Assert.Contains("does not combine an app and a tweak", modes[2].Reason, StringComparison.Ordinal);
        Assert.Contains("Theos project (tweak/)", Unleashify.Modes(IosAnalysis(withTheos: true))[1].Reason, StringComparison.Ordinal);
        Assert.False(Unleashify.Modes(null)[0].Available);
    }

    [Fact]
    public void The_iPhone_8_on_iOS_13_3_is_a_separate_target_that_metadata_cannot_reach()
    {
        var r = Unleashify.Compatibility("15.0", [new("iPhone 13 Pro", "16.5"), new("iPhone 8", "13.3")]);
        Assert.True(r[0].Compatible);
        Assert.False(r[1].Compatible);
        Assert.Contains("require iOS 15.0", r[1].Detail, StringComparison.Ordinal);
        Assert.Contains("changing that metadata would not help", r[1].Detail, StringComparison.Ordinal);
        Assert.Contains("Rootless jailbreaks (/var/jb) exist only for iOS 15 and later", r[1].Detail, StringComparison.Ordinal);
        Assert.Contains("source change in the app", r[1].Detail, StringComparison.Ordinal);
    }

    private const string Install = "/var/jb/Applications/HCGSentinelModern.app";

    [Fact]
    public void A_sideloaded_Sentinel_with_the_same_bundle_ID_is_a_conflict()
    {
        const string uicache = """
            com.apple.Preferences : /Applications/Preferences.app
            com.hcg.sentinel : /private/var/containers/Bundle/Application/7A1E/HCGSentinel.app
            """;
        var r = Unleashify.CheckInstalled("com.hcg.sentinel", "com.hakmicapitalgroup.hcgsentinel", "0.1.0~beta1", Install, uicache, testerConfirmedNone: true);
        Assert.Equal(ConflictState.Conflict, r.State);
        Assert.Contains(r.Findings, f => f.StartsWith("CONFLICT: another build with bundle ID com.hcg.sentinel is installed at /private/var/containers", StringComparison.Ordinal)
                                         && f.Contains("sideloaded / development-signed", StringComparison.Ordinal));
    }

    [Fact]
    public void The_packages_own_earlier_install_and_upgrades_are_not_conflicts_but_downgrades_are()
    {
        var own = Unleashify.CheckInstalled("com.hcg.sentinel", "com.hakmicapitalgroup.hcgsentinel", "0.1.0~beta2", Install,
            "com.hcg.sentinel : /var/jb/Applications/HCGSentinelModern.app\nii  com.hakmicapitalgroup.hcgsentinel  0.1.0~beta1  iphoneos-arm64  HCG Sentinel", false);
        Assert.Equal(ConflictState.Clear, own.State);
        Assert.Contains(own.Findings, f => f.Contains("upgrades it", StringComparison.Ordinal));

        var down = Unleashify.CheckInstalled("com.hcg.sentinel", "com.hakmicapitalgroup.hcgsentinel", "0.1.0~beta1", Install,
            "com.hakmicapitalgroup.hcgsentinel\t0.2.0", false);
        Assert.Equal(ConflictState.Conflict, down.State);
        Assert.Contains("downgrade", down.Findings.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_listing_only_the_testers_confirmation_clears_the_check()
    {
        Assert.Equal(ConflictState.NotChecked, Unleashify.CheckInstalled("com.hcg.sentinel", "p", "1", Install, null, false).State);
        var confirmed = Unleashify.CheckInstalled("com.hcg.sentinel", "p", "1", Install, "  ", true);
        Assert.Equal(ConflictState.Clear, confirmed.State);
        Assert.Contains("not read by Maker", confirmed.Summary, StringComparison.Ordinal);
    }

    // ───────────── Imported revision ─────────────

    private ProjectRecord Project(ProjectSourceKind kind, string location, string? archive = null)
    {
        var ws = _t.Combine("ws-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(ws, "source"));
        return new ProjectRecord { Id = Guid.NewGuid(), Name = "HCGSentinel", SourceKind = kind, SourceLocation = location, WorkspacePath = ws, PreservedArchivePath = archive, ImportedAt = DateTimeOffset.UtcNow };
    }

    private string GitFolder(string name, string commit, string branch = "main")
    {
        var dir = _t.Combine(name);
        _t.Write($"{name}/.git/HEAD", $"ref: refs/heads/{branch}\n");
        _t.Write($"{name}/.git/refs/heads/{branch}", commit + "\n");
        return dir;
    }

    [Fact]
    public async Task A_folder_import_records_its_commit_and_turns_stale_when_the_folder_moves_on()
    {
        const string a = "490858c15343e9c24e6fe85a9a83f498d744a7af", b = "1111111111111111111111111111111111111111";
        var folder = GitFolder("HCG-Sentinel", a);
        var p = Project(ProjectSourceKind.Directory, folder);
        await ImportRevisions.WriteAsync(p, ImportRevisions.Capture(p, TimeProvider.System), CancellationToken.None);
        var now = ImportRevisions.Check(p);
        Assert.Equal(RevisionState.Current, now.State);
        Assert.Equal(a, now.Recorded!.Commit);
        Assert.Equal("main", now.Recorded.Branch);

        File.WriteAllText(Path.Combine(folder, ".git", "refs", "heads", "main"), b + "\n");
        var later = ImportRevisions.Check(p);
        Assert.Equal(RevisionState.Stale, later.State);
        Assert.Contains("STALE: the original folder is now at 111111111111 (main); the imported copy is 490858c15343", later.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_GitHub_source_archive_records_the_commit_from_its_comment()
    {
        var zip = _t.Combine("HCG-Sentinel-main.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create)) { z.CreateEntry("HCG-Sentinel-main/README.md"); z.Comment = "490858c15343e9c24e6fe85a9a83f498d744a7af"; }
        var p = Project(ProjectSourceKind.ZipArchive, zip, zip);
        await ImportRevisions.WriteAsync(p, ImportRevisions.Capture(p, TimeProvider.System), CancellationToken.None);
        var s = ImportRevisions.Check(p);
        Assert.Equal(RevisionState.FromArchive, s.State);
        Assert.Equal("490858c15343e9c24e6fe85a9a83f498d744a7af", s.Recorded!.Commit);
        Assert.Contains("cannot see newer commits from a ZIP", s.Detail, StringComparison.Ordinal);
        Assert.Equal(RevisionState.Unknown, ImportRevisions.Check(Project(ProjectSourceKind.Directory, _t.Path)).State);
    }
}
