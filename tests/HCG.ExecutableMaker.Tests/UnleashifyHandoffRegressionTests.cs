using System.Text.Json.Nodes;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Infrastructure.Processes;
using HCG.ExecutableMaker.Packaging.Jailbreak;
using SkiaSharp;
using Xunit;

namespace HCG.ExecutableMaker.Tests;

/// <summary>
/// Regression tests from the HCG Mail Unleashed handoff: (1) icon.png was Apple's CgBI PNG, which browsers and Sileo
/// cannot decode; (2) DEVICE-TEST.md searched for "sentinel" whatever the product. Each product's handoff must use its own
/// name, bundle ID, package ID, version and install path, carry a standard PNG icon, and include a valid HCG Repo
/// Distribution v2 submission manifest.
/// </summary>
public sealed class UnleashifyHandoffRegressionTests : IDisposable
{
    private readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();
    private const string Commit = "a5b3c2322f0de4fbbe8d10df179872c590aa1d75";

    public sealed record Product(string Key, string Exe, string BundleId, string Name, string PackageId, string Version, string MinOs, string Repository, string Device, string DeviceIos, bool CgbiIcon);

    public static readonly Product Sentinel = new("sentinel", "HCGSentinelModern", "com.hcg.sentinel", "HCG Sentinel", "com.hakmicapitalgroup.hcgsentinel", "0.1.0~beta1", "15.0", "F1r3srp3nt/HCG-Sentinel", "iPhone 13 Pro (Dopamine, rootless)", "16.5", false);
    public static readonly Product MailUnleashed = new("mail", "HCGMail", "com.hakmicapitalgroup.hcgmail.unleashed", "HCG Mail Unleashed", "com.hakmicapitalgroup.hcgmail.unleashed", "0.1.0~beta1", "17.0", "F1r3srp3nt/HCG-Mail", "iPhone 16 Pro Max (Dopamine, rootless)", "17.4", true);
    public static readonly Product SignalContractor = new("signal", "SignalContractor", "com.hakmicapitalgroup.signalcontractor", "HCG Signal Contractor", "com.hakmicapitalgroup.signalcontractor", "0.1.0~beta1", "17.0", "F1r3srp3nt/HCG-Signal-Contractor", "iPhone 11 Pro Max (Dopamine, rootless)", "17.4", false);

    public static TheoryData<string> Products => new() { "sentinel", "mail", "signal" };
    private static Product Get(string key) => key switch { "sentinel" => Sentinel, "mail" => MailUnleashed, _ => SignalContractor };

    private async Task<RootlessAppWorkflow.Outcome> Build(Product p, string? artwork = null, bool local = true, bool appIcon = true)
    {
        var dir = _t.Combine(p.Key + "-" + Guid.NewGuid().ToString("N")[..6]);
        var app = MachOFixture.App(Path.Combine(dir, "src"), minos: p.MinOs, framework: p.Key == "sentinel", exe: p.Exe, bundleId: p.BundleId, name: p.Name,
            icon: p.CgbiIcon ? PngFixtures.CgBI(180, 180) : PngFixtures.Standard(180));
        if (!appIcon) File.Delete(Path.Combine(app, "AppIcon60x60@3x.png"));
        var spec = new RootlessAppPackageSpec(p.PackageId, p.Version, p.Name, RootlessAppPackager.HcgMaintainer, $"{p.Name} for rootless jailbreaks.\nCandidate beta.",
            Timestamp: new DateTimeOffset(2026, 11, 25, 9, 0, 0, TimeSpan.Zero));
        var prov = local
            ? new PackageProvenance(p.Repository, Commit, false, Local: new LocalBuildInfo("HCG Executable Maker test · Unleashify, local", null, new DateTimeOffset(2026, 11, 25, 9, 0, 0, TimeSpan.Zero), "test host", "private", null))
            : new PackageProvenance(p.Repository, Commit, false, 4558357312, p.Key + "-rootless-deb", PackagedBy: "HCG Executable Maker test", CiRepository: p.Repository, CiCommit: Commit);
        var context = new HandoffContext("Jailbreak App", p.Exe, new HCG.ExecutableMaker.Domain.ImportedRevision("git", Commit, "main", "/src", DateTimeOffset.UtcNow), HCG.ExecutableMaker.Domain.RevisionState.Current, "current", null,
            Unleashify.Compatibility(p.MinOs, [new DeviceTarget(p.Device, p.DeviceIos)]));
        return await new RootlessAppWorkflow(MachOFixture.FakeLdid(p.MinOs), TimeProvider.System).RunAsync(new RootlessAppWorkflow.Request(app, Path.Combine(dir, "out"), spec, prov,
            new CandidateTexts($"{p.Name}.", ["First rootless candidate."], [], "undecided", $"{p.Device}, iOS {p.DeviceIos}", IconArtwork: artwork), null, p.DeviceIos, Context: context), null, CancellationToken.None);
    }

    [Theory, MemberData(nameof(Products))]
    public async Task Device_test_instructions_name_the_actual_product(string key)
    {
        var p = Get(key);
        var o = await Build(p);
        Assert.True(o.Succeeded, o.Failure + "\n" + string.Join("\n", o.Log));
        var h = o.Handoff!;
        var md = File.ReadAllText(Path.Combine(h.Folder, "DEVICE-TEST.md"));
        var install = $"/var/jb/Applications/{p.Exe}.app";
        var slug = Unleashify.Slug(p.PackageId);
        Assert.StartsWith($"# {p.Name} {p.Version}: device test and rollback", md);
        foreach (var expected in new[] { $"Package `{p.PackageId}`", $"bundle ID `{p.BundleId}`", $"Installs only `{install}`", $"iOS {p.MinOs} or later",
                     $"uicache -l | grep -i -e {p.BundleId}", $"dpkg -l | grep -i -e {p.PackageId}", $"sudo dpkg -i /var/mobile/{p.PackageId}_{p.Version}_iphoneos-arm64.deb",
                     $"`Version: {p.Version}`", $"uicache -p {install}", $"sudo dpkg -r {p.PackageId}", $"/var/mobile/{slug}-test-before-dpkg.txt",
                     $"Test target: {p.Device}, iOS {p.DeviceIos}", $"Open **{p.Name}**" })
            Assert.Contains(expected, md);
        foreach (var term in Unleashify.ProductTerms(p.BundleId, p.PackageId, p.Exe)) Assert.Contains($"-e {term}", md);
        if (p != Sentinel)
            foreach (var file in Directory.EnumerateFiles(h.Folder, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".deb") && !f.EndsWith(".png")))
                Assert.DoesNotContain("sentinel", File.ReadAllText(file), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Product_terms_snapshot_names_and_variant_families()
    {
        Assert.Equal(["sentinel"], Unleashify.ProductTerms(Sentinel.BundleId, Sentinel.PackageId, Sentinel.Exe));      // the earlier Sentinel instructions are kept
        Assert.Equal(["hcgmail"], Unleashify.ProductTerms(MailUnleashed.BundleId, MailUnleashed.PackageId, MailUnleashed.Exe));
        Assert.Equal(["signalcontractor"], Unleashify.ProductTerms(SignalContractor.BundleId, SignalContractor.PackageId, SignalContractor.Exe));
        Assert.Equal("hcgmail-unleashed", Unleashify.Slug(MailUnleashed.PackageId));
        Assert.Equal("signalcontractor", Unleashify.Slug(SignalContractor.PackageId));
        Assert.Equal("com.hakmicapitalgroup.hcgmail", Unleashify.FamilyBundleId(MailUnleashed.BundleId));
        Assert.Equal(["com.example.terminal"], Unleashify.ProductTerms("com.example.terminal", "com.example.terminal"));   // generic word: the whole family ID instead
    }

    [Fact]
    public async Task Mail_Unleashed_names_the_standard_app_as_a_different_app_id_and_ignores_other_products()
    {
        var o = await Build(MailUnleashed);
        var md = File.ReadAllText(Path.Combine(o.Handoff!.Folder, "DEVICE-TEST.md"));
        Assert.Contains("`com.hakmicapitalgroup.hcgmail` (or another bundle ID of the same family): a **different app ID**", md);
        var listing = "com.hakmicapitalgroup.hcgmail : /var/containers/Bundle/Application/X/HCGMail.app\ncom.hcg.sentinel : /var/jb/Applications/HCGSentinelModern.app";
        var r = Unleashify.CheckInstalled(MailUnleashed.BundleId, MailUnleashed.PackageId, MailUnleashed.Version, "/var/jb/Applications/HCGMail.app", listing, false);
        Assert.Equal(ConflictState.Clear, r.State);                                       // the standard HCG Mail has another bundle ID
        Assert.Contains(r.Findings, f => f.StartsWith("Also installed: com.hakmicapitalgroup.hcgmail ", StringComparison.Ordinal));
        Assert.DoesNotContain(r.Findings, f => f.Contains("sentinel", StringComparison.OrdinalIgnoreCase));
        var same = Unleashify.CheckInstalled(MailUnleashed.BundleId, MailUnleashed.PackageId, MailUnleashed.Version, "/var/jb/Applications/HCGMail.app",
            "com.hakmicapitalgroup.hcgmail.unleashed : /var/containers/Bundle/Application/Y/HCGMail.app", false);
        Assert.Equal(ConflictState.Conflict, same.State);
    }

    [Theory, MemberData(nameof(Products))]
    public async Task The_handoff_icon_is_a_standard_png_that_a_standard_decoder_reads(string key)
    {
        var p = Get(key);
        var o = await Build(p);
        var icon = File.ReadAllBytes(Path.Combine(o.Handoff!.Folder, "icon.png"));
        Assert.Equal(PngIcon.Kind.StandardPng, PngIcon.Inspect(icon).Kind);
        Assert.Null(PngIcon.ValidateStandard(icon));
        using (var bmp = SKBitmap.Decode(icon)) Assert.Equal((180, 180), (bmp.Width, bmp.Height));
        var prov = JsonNode.Parse(File.ReadAllText(Path.Combine(o.Handoff.Folder, "provenance.json")))!["icon"]!;
        Assert.Equal(p.CgbiIcon ? "converted from Apple CgBI to a standard RGBA PNG (180×180) and verified with a standard decoder" : "standard PNG (180×180), used unchanged",
            prov["detail"]!.GetValue<string>());
        Assert.Equal(icon, File.ReadAllBytes(Path.Combine(o.Handoff.Folder, "hcg-repo", "packages", p.PackageId, "icon.png")));   // the same file goes to HCG Repo
        // The package itself is unchanged: its app still carries the original icon bytes as built.
        Assert.True(o.Validation!.Passed);
    }

    [Fact]
    public void A_CgBI_icon_converts_to_the_same_image()
    {
        var cgbi = PngFixtures.CgBI(64, 48);
        Assert.Equal(PngIcon.Kind.AppleCgBI, PngIcon.Inspect(cgbi).Kind);
        Assert.Contains("CgBI", PngIcon.ValidateStandard(cgbi));
        Assert.False(Decodes(cgbi));                            // what browsers and Sileo see: undecodable
        var png = PngIcon.ConvertCgBI(cgbi);
        Assert.Equal(PngIcon.Kind.StandardPng, PngIcon.Inspect(png).Kind);
        using var bmp = SKBitmap.Decode(png, new SKImageInfo(64, 48, SKColorType.Rgba8888, SKAlphaType.Unpremul)) ?? throw new InvalidOperationException("decode");
        var want = PngFixtures.Pixels(64, 48);
        for (var y = 0; y < 48; y++)
        for (var x = 0; x < 64; x++)
        {
            var c = bmp.GetPixel(x, y);
            var i = (y * 64 + x) * 4;
            Assert.Equal(want[i + 3], c.Alpha);
            if (c.Alpha == 0) continue;
            var tolerance = 255 / c.Alpha + 1;                  // premultiplication loses precision at low alpha
            Assert.InRange(c.Red - want[i], -tolerance, tolerance);
            Assert.InRange(c.Green - want[i + 1], -tolerance, tolerance);
            Assert.InRange(c.Blue - want[i + 2], -tolerance, tolerance);
        }
    }

    private static bool Decodes(byte[] png)
    {
        try { using var b = SKBitmap.Decode(png); return b is not null; }
        catch (ArgumentException) { return false; }
    }

    [Fact]
    public void Damaged_small_and_non_square_icons_are_refused()
    {
        var png = PngFixtures.Standard(120);
        var bad = (byte[])png.Clone();
        bad[30] ^= 0xFF;                                         // inside IHDR or IDAT: the CRC no longer matches
        Assert.Contains("CRC", PngIcon.ValidateStandard(bad));
        Assert.Contains("too small", PngIcon.ValidateStandard(PngFixtures.Standard(40)));
        Assert.Contains("not square", PngIcon.ValidateStandard(PngFixtures.Standard(120, 100)));
        Assert.Contains("not a PNG", PngIcon.ValidateStandard("GIF89a"u8.ToArray()));
    }

    [Fact]
    public async Task Approved_artwork_is_the_fallback_and_is_validated()
    {
        // No loose icon in the app (icons only in Assets.car): without artwork, icon.png is missing and says why.
        var none = await Build(SignalContractor, appIcon: false);
        Assert.False(File.Exists(Path.Combine(none.Handoff!.Folder, "icon.png")));
        Assert.Contains(none.Handoff.Missing, m => m.StartsWith("icon.png: no usable standard PNG") && m.Contains("Assets.car") && m.Contains("approved artwork"));

        var artwork = _t.Write("AppIcon-1024.png", "");
        File.WriteAllBytes(artwork, PngFixtures.Standard(1024));
        var withArt = await Build(SignalContractor, artwork, appIcon: false);
        var icon = File.ReadAllBytes(Path.Combine(withArt.Handoff!.Folder, "icon.png"));
        Assert.Null(PngIcon.ValidateStandard(icon));
        Assert.Equal(1024, PngIcon.Inspect(icon).Width);
        Assert.Contains("approved artwork AppIcon-1024.png", File.ReadAllText(Path.Combine(withArt.Handoff.Folder, "provenance.json")));

        File.WriteAllBytes(artwork, PngFixtures.Standard(2048));
        var big = PngIcon.Choose(null, artwork);
        Assert.Equal(1024, big.Icon!.Width);                    // scaled down, still square and standard
        Assert.Null(PngIcon.ValidateStandard(big.Icon.Png));
        File.WriteAllBytes(artwork, PngFixtures.CgBI(256, 256));
        Assert.Null(PngIcon.ValidateStandard(PngIcon.Choose(null, artwork).Icon!.Png));   // even CgBI artwork becomes standard
        File.WriteAllBytes(artwork, PngFixtures.Standard(300, 200));
        Assert.Null(PngIcon.Choose(null, artwork).Icon);       // never an invalid icon

        // The app's own icon wins when it is usable; the artwork is only the fallback.
        var cgbiApp = await Build(MailUnleashed, artwork);
        Assert.Contains("app bundle AppIcon60x60@3x.png", File.ReadAllText(Path.Combine(cgbiApp.Handoff!.Folder, "provenance.json")));
    }

    [Fact]
    public void The_projects_AppIcon_set_is_suggested_as_artwork()
    {
        var set = _t.Combine("proj", "App", "Assets.xcassets", "AppIcon.appiconset");
        Directory.CreateDirectory(set);
        File.WriteAllBytes(Path.Combine(set, "icon-60@3x.png"), PngFixtures.Standard(180));
        File.WriteAllBytes(Path.Combine(set, "icon-1024.png"), PngFixtures.Standard(1024));
        Assert.Equal(Path.Combine(set, "icon-1024.png"), PngIcon.SuggestArtwork(_t.Combine("proj")));
        Assert.Null(PngIcon.SuggestArtwork(_t.Combine("nothing")));
    }

    [Theory, MemberData(nameof(Products))]
    public async Task The_submission_manifest_states_only_measured_facts(string key)
    {
        var p = Get(key);
        var o = await Build(p);
        var f = o.Validation!.Facts;
        var m = JsonNode.Parse(File.ReadAllText(Path.Combine(o.Handoff!.Folder, SubmissionManifest.FileName)))!.AsObject();
        Assert.Empty(SubmissionManifest.Validate(m));
        Assert.Equal("hcg.repo.artifact-submission", m["format"]!.GetValue<string>());
        Assert.Equal((p.PackageId, p.Version, "iphoneos-arm64", p.Name), (m["package"]!["identifier"]!.GetValue<string>(), m["package"]!["version"]!.GetValue<string>(),
            m["package"]!["architecture"]!.GetValue<string>(), m["package"]!["name"]!.GetValue<string>()));
        Assert.Equal((f.Sha256, f.Size, $"{p.PackageId}_{p.Version}_iphoneos-arm64.deb"), (m["artifact"]!["sha256"]!.GetValue<string>(), m["artifact"]!["size"]!.GetValue<long>(), m["artifact"]!["filename"]!.GetValue<string>()));
        Assert.Equal(p.MinOs, m["compatibility"]!["minimumOS"]!.GetValue<string>());
        Assert.Equal(["firmware", "uikittools"], m["dependencies"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal(["postinst", "postrm"], m["maintainerScripts"]!["declared"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal("executable-maker-local", m["build"]!["provider"]!.GetValue<string>());
        Assert.Null(m["build"]!["runId"]);
        Assert.Empty(m["compatibility"]!["jailbreaks"]!.AsArray());                        // nothing tested on a device, so nothing claimed
        Assert.False(m["validation"]!["deviceTested"]!.GetValue<bool>());
        Assert.Equal((Commit, false, "main"), (m["source"]!["commit"]!.GetValue<string>(), m["source"]!["dirty"]!.GetValue<bool>(), m["source"]!["branch"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_CI_build_is_a_github_actions_submission_with_its_run()
    {
        var o = await Build(Sentinel, local: false);
        var m = JsonNode.Parse(File.ReadAllText(Path.Combine(o.Handoff!.Folder, SubmissionManifest.FileName)))!.AsObject();
        Assert.Empty(SubmissionManifest.Validate(m));
        Assert.Equal(("github-actions", 4558357312L, "sentinel-rootless-deb"), (m["build"]!["provider"]!.GetValue<string>(), m["build"]!["runId"]!.GetValue<long>(), m["build"]!["artifactName"]!.GetValue<string>()));
        m["build"]!["provider"] = "executable-maker-local";
        Assert.Contains(SubmissionManifest.Validate(m), x => x.Contains("local build has no CI run"));
    }

    // ------------------------------------------------------------------ against HCG Repo main (Distribution v2)

    /// <summary>HCG_REPO_MAIN_CHECKOUT: an HCG Repo checkout with Distribution v2 (main). Its own code judges the handoff.</summary>
    private static string? RepoMain() => Environment.GetEnvironmentVariable("HCG_REPO_MAIN_CHECKOUT") is { } c && ShipToRepo.CheckoutProblem(c) is null ? c : null;

    private static void CopyDir(string from, string to)
    {
        foreach (var d in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories).Where(d => !d.Contains($"{Path.DirectorySeparatorChar}.git")))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, d)));
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}") && !f.Contains("__pycache__")))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(to, Path.GetRelativePath(from, f)))!);
            File.Copy(f, Path.Combine(to, Path.GetRelativePath(from, f)));
        }
    }

    [Fact]
    public async Task HCG_Repos_manifest_validator_accepts_every_products_submission()
    {
        if (RepoMain() is not { } repo || new HCG.ExecutableMaker.Core.ExecutableLocator().Find("python3") is not { } python) return;
        foreach (var p in new[] { Sentinel, MailUnleashed, SignalContractor })
        {
            var o = await Build(p);
            var manifest = Path.Combine(o.Handoff!.Folder, SubmissionManifest.FileName);
            var script = $"import json,sys; sys.path.insert(0, {System.Text.Json.JsonSerializer.Serialize(Path.Combine(repo, "tools"))}); from hcgrepo.submission import validate_manifest; " +
                         $"print(json.dumps(validate_manifest(json.load(open({System.Text.Json.JsonSerializer.Serialize(manifest)})))))";
            var (r, stdout, stderr) = await new ProcessRunner().CaptureAsync(new HCG.ExecutableMaker.Core.ProcessSpec { Executable = python, Arguments = ["-I", "-c", script] }, CancellationToken.None);
            Assert.True(r.Succeeded, stderr);
            Assert.Equal("[]", stdout.Trim());
        }
    }

    [Fact]
    public async Task Ship_to_Repo_submits_reviews_approves_the_exact_sha_and_prepares_Candidate_in_a_checkout_copy()
    {
        if (RepoMain() is not { } main || new HCG.ExecutableMaker.Core.ExecutableLocator().Find("python3") is not { } python) return;
        var repo = _t.Combine("hcg-repo");
        CopyDir(main, repo);                                    // never the real checkout
        var incoming = _t.Combine("incoming");
        var runner = new ProcessRunner();
        var o = await Build(MailUnleashed);
        var sha = o.Validation!.Facts.Sha256;
        var id = ShipToRepo.SubmissionId(MailUnleashed.PackageId, MailUnleashed.Version, "iphoneos-arm64", sha);

        var submit = await ShipToRepo.SubmitAsync(runner, python, repo, o.Handoff!.Folder, incoming, null, CancellationToken.None);
        Assert.True(submit.Succeeded, submit.Output);
        Assert.Equal((id, "validated"), (submit.SubmissionId, submit.State));
        Assert.Contains($"SHA-256      {sha}", submit.Output);

        var show = await ShipToRepo.ShowAsync(runner, python, repo, id, incoming, CancellationToken.None);
        Assert.Equal(("validated", sha), (show.State, show.Review!["sha256"]!.GetValue<string>()));

        var wrong = await ShipToRepo.ApproveAsync(runner, python, repo, id, new string('0', 64), "Owner", "approved", incoming, CancellationToken.None);
        Assert.False(wrong.Succeeded);
        Assert.Contains("is not the reviewed package's", wrong.Output);
        var nobody = await ShipToRepo.ApproveAsync(runner, python, repo, id, sha, " ", "approved", incoming, CancellationToken.None);
        Assert.False(nobody.Succeeded);

        Assert.False(ShipToRepo.HasPackageMetadata(repo, MailUnleashed.PackageId));
        var early = await ShipToRepo.PrepareAsync(runner, python, repo, id, null, true, incoming, null, CancellationToken.None);
        Assert.False(early.Succeeded);                          // HCG Repo refuses: not approved
        var approve = await ShipToRepo.ApproveAsync(runner, python, repo, id, sha.ToUpperInvariant(), "Owner (test)", "I approve these exact bytes for Candidate (test).", incoming, CancellationToken.None);
        Assert.True(approve.Succeeded, approve.Output);
        Assert.Contains("Not published", approve.Output);

        var prepare = await ShipToRepo.PrepareAsync(runner, python, repo, id, "free Candidate demo approved by the owner (test)", true, incoming, null, CancellationToken.None);
        Assert.True(prepare.Succeeded, prepare.Output);
        Assert.True(File.Exists(Path.Combine(repo, "imports", id + ".toml")));
        Assert.True(File.Exists(Path.Combine(repo, "submissions", id, "submission.json")));
        Assert.True(ShipToRepo.HasPackageMetadata(repo, MailUnleashed.PackageId));
        Assert.Contains(Directory.EnumerateFiles(Path.Combine(repo, "pool"), "*.deb", SearchOption.AllDirectories),
            f => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f))) == sha);
        Assert.Null(PngIcon.ValidateStandard(File.ReadAllBytes(Path.Combine(repo, "packages", MailUnleashed.PackageId, "icon.png"))));
        Assert.Contains("kind = \"local-submission\"", File.ReadAllText(Path.Combine(repo, "imports", id + ".toml")));
    }
}
