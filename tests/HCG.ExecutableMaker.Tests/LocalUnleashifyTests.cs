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
    public async Task A_local_build_exports_a_local_file_request_and_the_hcg_repo_layout_without_any_ci_claim()
    {
        var o = await Run(Local());
        Assert.True(o.Succeeded, o.Failure + "\n" + string.Join("\n", o.Log));
        var h = o.Handoff!;
        Assert.True(h.ImportRequestComplete, string.Join("\n", h.Missing));
        var sha = o.Validation!.Facts.Sha256;
        var req = File.ReadAllText(Path.Combine(h.Folder, "import-request.toml"));
        Assert.Contains("kind = \"local-file\"", req);
        Assert.Contains("path = \"imports/local/com.hakmicapitalgroup.signalcontractor_0.1.0~beta1_iphoneos-arm64.deb\"", req);
        Assert.Contains($"sha256 = \"{sha}\"", req);
        Assert.Contains($"commit = \"{Commit}\"", req);
        Assert.Contains("repository = \"F1r3srp3nt/HCG-Signal-Contractor\"", req);
        Assert.Contains("toolchain = \"Xcode 27.2 (27C5040a) · iphoneos27.2\"", req);   // measured from the app, not typed
        Assert.Contains("source_visibility = \"private\"", req);
        Assert.Contains("validation = \"HCG Executable Maker rootless validator: passed", req);
        Assert.Contains("lifecycle passed", req);
        Assert.DoesNotContain("run_id", req);
        Assert.DoesNotContain("artifact", req);

        // The files to copy onto an HCG Repo checkout, at their paths there; the .deb is the validated bytes.
        var repo = Path.Combine(h.Folder, "hcg-repo");
        Assert.Equal(File.ReadAllBytes(o.Package!.DebPath), File.ReadAllBytes(Path.Combine(repo, "imports", "local", "com.hakmicapitalgroup.signalcontractor_0.1.0~beta1_iphoneos-arm64.deb")));
        Assert.Equal(req, File.ReadAllText(Path.Combine(repo, "imports", "com.hakmicapitalgroup.signalcontractor-0.1.0-beta1.toml")));
        Assert.True(File.Exists(Path.Combine(repo, "packages", "com.hakmicapitalgroup.signalcontractor", "package.toml")));
        var prov = File.ReadAllText(Path.Combine(h.Folder, "provenance.json"));
        Assert.Contains("\"local_build\"", prov);
        Assert.Contains("there is no run ID or artifact", prov);
        Assert.Contains("\"ci_run_id\": null", prov);
        Assert.Contains("built locally with Xcode 27.2", File.ReadAllText(Path.Combine(h.Folder, "CHANGELOG.md")));
        // Nothing from the source tree is in the handoff.
        Assert.DoesNotContain(Directory.EnumerateFiles(h.Folder, "*", SearchOption.AllDirectories), f => f.EndsWith(".swift", StringComparison.Ordinal) || f.Contains(".xcodeproj"));
    }

    [Fact]
    public async Task A_local_build_without_a_pinned_clean_commit_is_not_importable()
    {
        var unpinned = await Run(Local(commit: "a5b3c23"), name: "short");
        Assert.False(unpinned.Handoff!.ImportRequestComplete);
        Assert.True(File.Exists(Path.Combine(unpinned.Handoff.Folder, "import-request.INCOMPLETE.toml")));
        Assert.False(Directory.Exists(Path.Combine(unpinned.Handoff.Folder, "hcg-repo")));
        Assert.Contains(unpinned.Handoff.Missing, m => m.Contains("not pinned to its source"));
        var dirty = await Run(Local(dirty: true), name: "dirty");
        Assert.False(dirty.Handoff!.ImportRequestComplete);
    }

    [Fact]
    public async Task The_owners_approval_counts_only_for_the_exact_sha256_it_names()
    {
        const string words = "approved by the owner on 25 Nov 2026: 'publish to Candidate'";
        var probe = await Run(Local(), name: "probe");
        var sha = probe.Validation!.Facts.Sha256;

        var none = await Run(Local(), approval: words, name: "nosha");   // local build, approval without a checksum
        Assert.Contains(none.Handoff!.Missing, m => m.StartsWith("owner approval NOT recorded") && m.Contains("exact SHA-256"));
        Assert.DoesNotContain("\nexception = ", File.ReadAllText(Path.Combine(none.Handoff.Folder, "package.toml")));

        var wrong = await Run(Local(), approval: words, approvedSha: new string('0', 64), name: "wrong");
        Assert.Contains(wrong.Handoff!.Missing, m => m.Contains("rebuilt bytes need a new approval"));
        Assert.DoesNotContain("\nexception = ", File.ReadAllText(Path.Combine(wrong.Handoff.Folder, "package.toml")));

        var right = await Run(Local(), approval: words, approvedSha: sha.ToUpperInvariant(), name: "right");
        Assert.Equal(sha, right.Validation!.Facts.Sha256);   // deterministic: the same inputs give the same bytes
        var toml = File.ReadAllText(Path.Combine(right.Handoff!.Folder, "package.toml"));
        Assert.Contains("\nexception = \"", toml);
        Assert.Contains($"Approved package SHA-256: {sha}.", toml);
        Assert.Contains("exception_versions = [\"0.1.0~beta1\"]", toml);
        Assert.DoesNotContain(right.Handoff.Missing, m => m.StartsWith("owner approval"));
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

    /// <summary>With an HCG Repo checkout (HCG_REPO_CHECKOUT, local-file support), HCG Repo's own importer judges the exported bytes.</summary>
    [Fact]
    public async Task HCG_Repos_importer_accepts_the_local_file_request_and_waits_only_for_the_approval()
    {
        var checkout = Environment.GetEnvironmentVariable("HCG_REPO_CHECKOUT");
        var python = new HCG.ExecutableMaker.Core.ExecutableLocator().Find("python3");
        if (checkout is null || python is null) return; // runs in CI's Unleashify job and wherever a checkout is given
        var o = await Run(Local(), name: "preview");
        var p = await HcgRepoImportPreview.RunAsync(new ProcessRunner(), python, checkout, o.Handoff!.Folder, hypotheticalApproval: true, requireGenuineApproval: false, null, CancellationToken.None);
        Assert.True(p.Verdict.StartsWith("BYTES VALIDATED, NOT YET IMPORTABLE", StringComparison.Ordinal), p.Output);
        Assert.Contains("local-file imports/local/", p.Output);

        var sha = o.Validation!.Facts.Sha256;
        var approved = await Run(Local(), approval: "approved by the owner on 25 Nov 2026: 'test'", approvedSha: sha, name: "preview-approved");
        var q = await HcgRepoImportPreview.RunAsync(new ProcessRunner(), python, checkout, approved.Handoff!.Folder, hypotheticalApproval: false, requireGenuineApproval: true, null, CancellationToken.None);
        Assert.True(q.Verdict.StartsWith("VALIDATED", StringComparison.Ordinal), q.Output);
    }
}
