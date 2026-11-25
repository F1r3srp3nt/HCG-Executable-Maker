using System.Diagnostics;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Infrastructure.Processes;
using HCG.ExecutableMaker.Packaging.Jailbreak;
using Xunit;

namespace HCG.ExecutableMaker.Tests;

/// <summary>
/// Unleashify on the owner's computer, without CI: local provenance (no invented run or artifact), HCG Repo's
/// local-file import request and hcg-repo/ layout, an approval bound to the exact SHA-256, the package lifecycle
/// simulation and the pinned-commit checkout. The packaging itself is the same RootlessAppWorkflow CI uses.
/// </summary>
public sealed class LocalUnleashifyTests : IDisposable
{
    private readonly TempDir _t = new();
    private const string Commit = "a5b3c2322f0de4fbbe8d10df179872c590aa1d75";
    public void Dispose() => _t.Dispose();

    private static string WithXcode(string app)
    {
        var plist = Path.Combine(app, "Info.plist");
        var text = File.ReadAllText(plist);
        var at = text.LastIndexOf("</dict>", StringComparison.Ordinal);
        File.WriteAllText(plist, text[..at] + "<key>DTXcode</key><string>2720</string><key>DTXcodeBuild</key><string>27C5040a</string><key>DTSDKName</key><string>iphoneos27.2</string>\n" + text[at..]);
        return app;
    }

    private static RootlessAppPackageSpec Spec() => new("com.hakmicapitalgroup.signalcontractor", "0.1.0~beta1", "HCG Signal Contractor", RootlessAppPackager.HcgMaintainer,
        "A modular hardware connectivity, signal analysis and authorized security-research instrument.\nCandidate beta.", Timestamp: new DateTimeOffset(2026, 11, 25, 9, 0, 0, TimeSpan.Zero));

    private static PackageProvenance Local(string? commit = Commit, bool dirty = false) => new("F1r3srp3nt/HCG-Signal-Contractor", commit, dirty,
        Local: new LocalBuildInfo("HCG Executable Maker 0.5.0 (commit abc) · Unleashify, local", null, new DateTimeOffset(2026, 11, 25, 9, 0, 0, TimeSpan.Zero), "macOS 26.1 · arm64", "private", null));

    private Task<RootlessAppWorkflow.Outcome> Run(PackageProvenance prov, string? approval = null, string? approvedSha = null, string name = "w") =>
        new RootlessAppWorkflow(MachOFixture.FakeLdid(), TimeProvider.System).RunAsync(new RootlessAppWorkflow.Request(
            WithXcode(MachOFixture.App(_t.Combine(name + "-src"))), _t.Combine(name), Spec(), prov,
            new CandidateTexts("Signal analysis instrument.", ["First rootless candidate."], [], "undecided", "iPhone 11 Pro Max, iOS 17.4, Dopamine", approval, approvedSha),
            "/usr/local/bin/ldid", "17.4"), null, CancellationToken.None);

    [Fact]
    public async Task A_local_build_is_handed_to_HCG_Repos_Release_Manager_without_any_ci_claim()
    {
        var o = await Run(Local());
        Assert.True(o.Succeeded, o.Failure + "\n" + string.Join("\n", o.Log));
        var h = o.Handoff!;
        Assert.True(h.ImportRequestComplete, string.Join("\n", h.Missing));
        // HCG Repo's Release Manager writes the local-file request itself (release prepare --via local-file); Maker writes none.
        Assert.False(File.Exists(Path.Combine(h.Folder, "import-request.toml")));
        Assert.False(Directory.Exists(Path.Combine(h.Folder, "hcg-repo")));
        var m = File.ReadAllText(Path.Combine(h.Folder, SubmissionManifest.FileName));
        Assert.Contains("\"provider\": \"executable-maker-local\"", m);
        Assert.Contains($"\"commit\": \"{Commit}\"", m);
        Assert.Contains("Xcode 27.2 (27C5040a) · iphoneos27.2", m);       // measured from the app, not typed
        Assert.Contains("lifecycle passed", m);
        Assert.DoesNotContain("runId", m);
        Assert.Contains("Ship to Repo", File.ReadAllText(Path.Combine(h.Folder, "README.md")));
        var prov = File.ReadAllText(Path.Combine(h.Folder, "provenance.json"));
        Assert.Contains("\"local_build\"", prov);
        Assert.Contains("there is no run ID or artifact", prov);
        Assert.Contains("\"ci_run_id\": null", prov);
        Assert.Contains("\"source_visibility\": \"private\"", prov);
        Assert.Contains("built locally with Xcode 27.2", File.ReadAllText(Path.Combine(h.Folder, "CHANGELOG.md")));
        // Nothing from the source tree is in the handoff.
        Assert.DoesNotContain(Directory.EnumerateFiles(h.Folder, "*", SearchOption.AllDirectories), f => f.EndsWith(".swift", StringComparison.Ordinal) || f.Contains(".xcodeproj"));
    }

    [Fact]
    public async Task A_local_build_without_a_pinned_clean_commit_is_not_submittable()
    {
        var unpinned = await Run(Local(commit: "a5b3c23"), name: "short");
        Assert.False(unpinned.Handoff!.ImportRequestComplete);
        Assert.False(File.Exists(Path.Combine(unpinned.Handoff.Folder, SubmissionManifest.FileName)));
        Assert.Contains(unpinned.Handoff.Missing, m => m.Contains("not pinned to its source"));
        Assert.Contains(unpinned.Handoff.Missing, m => m.StartsWith("submission.json") && m.Contains("40-character"));
        var dirty = await Run(Local(dirty: true), name: "dirty");
        Assert.False(dirty.Handoff!.ImportRequestComplete);
        Assert.Contains(dirty.Handoff.Missing, m => m.StartsWith("submission.json") && m.Contains("dirty"));
    }

    /// <summary>
    /// HCG Repo's submission contract: a local handoff's package.toml carries no [access] exception, whatever approval text
    /// was typed into Maker. The owner approves the exact SHA-256 and the free demo in HCG Repo (approve, prepare
    /// --free-demo-approval); Maker neither writes nor needs that approval, so the handoff does not list it as missing.
    /// </summary>
    [Fact]
    public async Task A_local_handoff_never_writes_the_owners_approval_the_owner_gives_it_in_HCG_Repo()
    {
        const string words = "approved by the owner on 25 Nov 2026: 'publish to Candidate'";
        var probe = await Run(Local(), name: "probe");
        var sha = probe.Validation!.Facts.Sha256;
        foreach (var (approval, approvedSha, name) in new (string?, string?, string)[] { (null, null, "none"), (words, null, "nosha"), (words, new string('0', 64), "wrong"), (words, sha.ToUpperInvariant(), "right") })
        {
            var o = await Run(Local(), approval: approval, approvedSha: approvedSha, name: name);
            Assert.Equal(sha, o.Validation!.Facts.Sha256);   // deterministic: the same inputs give the same bytes
            var toml = File.ReadAllText(Path.Combine(o.Handoff!.Folder, "package.toml"));
            Assert.DoesNotContain("\nexception = ", toml);
            Assert.DoesNotContain("\nexception_versions = ", toml);
            Assert.Contains("--free-demo-approval", toml);
            Assert.DoesNotContain(o.Handoff.Missing, m => m.StartsWith("owner approval", StringComparison.Ordinal));
            Assert.True(o.Handoff.ImportRequestComplete);
        }
    }

    [Fact]
    public async Task The_lifecycle_runs_makers_scripts_with_a_recording_uicache_and_removal_leaves_nothing()
    {
        var o = await Run(Local(), name: "life");
        var life = o.Lifecycle!;
        Assert.True(life.Passed, string.Join("\n", life.Checks.Select(c => $"{c.Status} {c.Name}: {c.Detail}")));
        Assert.Contains(life.Checks, c => c.Name == "Install (unpack)" && c.Detail.Contains("/var/jb/Applications/HCGSentinel.app"));
        Assert.Contains(life.Checks, c => c.Name == "Remove" && c.Status == VerificationStatus.Passed);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Contains(life.Checks, c => c.Name == "postinst configure" && c.Detail.Contains("\"-p /var/jb/Applications/HCGSentinel.app\""));
            Assert.Contains(life.Checks, c => c.Name == "postinst when uicache fails" && c.Status == VerificationStatus.Passed);
            Assert.Contains(life.Checks, c => c.Name == "postrm upgrade" && c.Status == VerificationStatus.Passed);
            Assert.Contains(life.Checks, c => c.Name == "postrm remove" && c.Detail.Contains("\"-u /var/jb/Applications/HCGSentinel.app\""));
        }
    }

    [Fact]
    public void Toolchain_host_and_repository_names_carry_no_private_detail()
    {
        Assert.Equal("F1r3srp3nt/HCG-Signal-Contractor", LocalBuild.RepositoryFromRemote("https://x-access-token:SECRET@github.com/F1r3srp3nt/HCG-Signal-Contractor.git"));
        Assert.Equal("F1r3srp3nt/HCG-Signal-Contractor", LocalBuild.RepositoryFromRemote("git@github.com:F1r3srp3nt/HCG-Signal-Contractor.git"));
        Assert.Null(LocalBuild.RepositoryFromRemote("https://internal.example/repo.git"));
        var host = LocalBuild.DefaultHostLabel();
        Assert.DoesNotContain(Environment.MachineName, host, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.UserName, host, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_pinned_commit_is_checked_out_clean_from_a_local_repository_without_changing_it()
    {
        var git = new HCG.ExecutableMaker.Core.ExecutableLocator().Find("git");
        if (git is null) return; // git is on every CI runner; skip only where it is genuinely absent
        var repo = _t.Combine("src-repo");
        Directory.CreateDirectory(repo);
        void G(params string[] a)
        {
            var psi = new ProcessStartInfo(git) { WorkingDirectory = repo, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var x in a) psi.ArgumentList.Add(x);
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            Assert.True(p.ExitCode == 0, string.Join(' ', a) + ": " + p.StandardError.ReadToEnd());
        }
        string Out(params string[] a)
        {
            var psi = new ProcessStartInfo(git) { WorkingDirectory = repo, RedirectStandardOutput = true };
            foreach (var x in a) psi.ArgumentList.Add(x);
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            return o;
        }
        G("init", "-q", "-b", "main");
        G("config", "user.email", "ci@example.invalid"); G("config", "user.name", "CI");
        File.WriteAllText(Path.Combine(repo, "a.txt"), "first");
        G("add", "."); G("commit", "-qm", "first");
        var first = Out("rev-parse", "HEAD");
        File.WriteAllText(Path.Combine(repo, "a.txt"), "second");
        G("commit", "-qam", "second");
        G("remote", "add", "origin", "https://github.com/F1r3srp3nt/HCG-Signal-Contractor.git");
        File.WriteAllText(Path.Combine(repo, "uncommitted.txt"), "local change");   // never copied
        var headBefore = Out("rev-parse", "HEAD");

        var r = await PinnedSource.CheckoutAsync(new ProcessRunner(), git, repo, first[..10], _t.Combine("pinned"), CancellationToken.None);
        Assert.True(r.Succeeded, r.Failure);
        Assert.Equal(first, r.Commit);
        Assert.Equal("F1r3srp3nt/HCG-Signal-Contractor", r.Repository);
        Assert.Equal("first", File.ReadAllText(Path.Combine(r.Folder!, "a.txt")));
        Assert.False(File.Exists(Path.Combine(r.Folder!, "uncommitted.txt")));
        Assert.Contains(r.Notes, n => n.Contains("main"));
        Assert.Equal(headBefore, Out("rev-parse", "HEAD"));          // the source repository is unchanged
        Assert.True(File.Exists(Path.Combine(repo, "uncommitted.txt")));

        var missing = await PinnedSource.CheckoutAsync(new ProcessRunner(), git, repo, "0000000", _t.Combine("pinned2"), CancellationToken.None);
        Assert.False(missing.Succeeded);
        Assert.Contains("has no commit", missing.Failure);
        var option = await PinnedSource.CheckoutAsync(new ProcessRunner(), git, repo, "--upload-pack=x", _t.Combine("pinned3"), CancellationToken.None);
        Assert.False(option.Succeeded);
    }

    /// <summary>With an HCG Repo checkout (HCG_REPO_MAIN_CHECKOUT), HCG Repo's Release Manager reviews the handoff in a throwaway store.</summary>
    [Fact]
    public async Task The_import_preview_is_HCG_Repos_own_review_and_keeps_nothing()
    {
        var checkout = Environment.GetEnvironmentVariable("HCG_REPO_MAIN_CHECKOUT");
        var python = new HCG.ExecutableMaker.Core.ExecutableLocator().Find("python3");
        if (checkout is null || ShipToRepo.CheckoutProblem(checkout) is not null || python is null) return;
        var before = Directory.EnumerateFileSystemEntries(Path.GetTempPath(), "hcg-ship-preview-*").Count();
        var o = await Run(Local(), name: "preview");
        var p = await ShipToRepo.PreviewAsync(new ProcessRunner(), python, checkout, o.Handoff!.Folder, null, CancellationToken.None);
        Assert.Equal("validated", p.State);
        Assert.Contains($"SHA-256      {o.Validation!.Facts.Sha256}", p.Output);
        Assert.Contains("Script-free dpkg lifecycle", p.Output);
        Assert.Equal("passed", p.Result!["levels"]!["script_free_lifecycle"]!["state"]!.GetValue<string>());
        Assert.Equal("not-recorded", p.Result!["levels"]!["device_installation"]!["state"]!.GetValue<string>());
        Assert.Equal(before, Directory.EnumerateFileSystemEntries(Path.GetTempPath(), "hcg-ship-preview-*").Count());   // the throwaway store is gone
    }
}
