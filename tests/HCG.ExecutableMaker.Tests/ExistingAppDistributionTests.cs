using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using HCG.ExecutableMaker.BuildEngine.MacDistribution;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging;
using HCG.ExecutableMaker.Packaging.MacDistribution;
using Xunit;

namespace HCG.ExecutableMaker.Tests;

/// <summary>
/// Sign an existing app (an already-built .NET .app such as HCG Mail's CI output): gating before anything runs, the
/// inside-out signing order, the step sequence with Developer ID (notarized, stapled, re-zipped, signed .pkg, passports)
/// and as an ad-hoc test build, the saved Project signing route, and the release-version override. The Mac tools are
/// scripted; the macOS CI job runs the same service against the real tools with an ad-hoc test identity.
/// </summary>
public sealed class ExistingAppDistributionTests : IDisposable
{
    private readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    private const string Team = "ABCDE12345";
    private const string AppSha = "1111111111111111111111111111111111111111";
    private const string InstallerSha = "2222222222222222222222222222222222222222";
    private const string AllowJit = "com.apple.security.cs.allow-jit";

    private static readonly MacSigningInventory Keychain = new()
    {
        Identities =
        [
            new SigningIdentity(AppSha, $"Developer ID Application: HCG ({Team})", AppleCertificateKind.DeveloperIdApplication, Team),
            new SigningIdentity(InstallerSha, $"Developer ID Installer: HCG ({Team})", AppleCertificateKind.DeveloperIdInstaller, Team),
            new SigningIdentity("3333333333333333333333333333333333333333", "Developer ID Installer: Other (ZZZZZ99999)", AppleCertificateKind.DeveloperIdInstaller, "ZZZZZ99999"),
            new SigningIdentity("4444444444444444444444444444444444444444", $"Apple Development: HCG ({Team})", AppleCertificateKind.Development, Team),
        ],
    };

    private static string Plist(params (string Key, string Value)[] entries) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<plist version=\"1.0\"><dict>\n" +
        string.Concat(entries.Select(e => $"<key>{e.Key}</key><string>{e.Value}</string>\n")) + "</dict></plist>\n";

    /// <summary>A bundle shaped like HCG Mail's CI output: single-file host (with CoreLib inside), native libraries beside it, one nested framework.</summary>
    private string MailLikeApp(bool dataInMacOS = false, string shortVersion = "1.0.0")
    {
        var app = _t.Combine("ci", "HCG Mail.app");
        var macos = Path.Combine(app, "Contents", "MacOS");
        Directory.CreateDirectory(macos);
        Directory.CreateDirectory(Path.Combine(app, "Contents", "Resources"));
        File.WriteAllText(Path.Combine(app, "Contents", "Info.plist"), Plist(
            ("CFBundleExecutable", "HCG Mail"), ("CFBundleIdentifier", "com.hakmicapitalgroup.hcgmail"), ("CFBundleName", "HCG Mail"),
            ("CFBundlePackageType", "APPL"), ("CFBundleShortVersionString", shortVersion), ("CFBundleVersion", "7"), ("LSMinimumSystemVersion", "11.0"),
            ("CFBundleIconFile", "AppIcon.icns")));
        File.WriteAllBytes(Path.Combine(app, "Contents", "Resources", "AppIcon.icns"), [1, 2, 3]);
        var host = MachOFixture.Build(platform: 1, minos: "11.0", dylibs: ["/usr/lib/libSystem.B.dylib"], signed: false);
        File.WriteAllBytes(Path.Combine(macos, "HCG Mail"), [.. host, .. "System.Private.CoreLib"u8.ToArray()]);
        foreach (var lib in new[] { "libSkiaSharp.dylib", "libHarfBuzzSharp.dylib", "libAvaloniaNative.dylib" })
            File.WriteAllBytes(Path.Combine(macos, lib), MachOFixture.Build(MachOFixture.Dylib, platform: 1, minos: "11.0", dylibs: ["/usr/lib/libSystem.B.dylib"], signed: false));
        var fw = Path.Combine(app, "Contents", "Frameworks", "Helper.framework", "Versions", "A");
        Directory.CreateDirectory(fw);
        File.WriteAllBytes(Path.Combine(fw, "Helper"), MachOFixture.Build(MachOFixture.Dylib, platform: 1, minos: "11.0", dylibs: ["/usr/lib/libSystem.B.dylib"], signed: false));
        if (dataInMacOS) File.WriteAllText(Path.Combine(macos, "HCGMail.dll"), "MZ managed assembly");
        return app;
    }

    private string Entitlements(string body = $"<key>{AllowJit}</key><true/>") =>
        _t.Write("HCGMail.entitlements", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<plist version=\"1.0\"><dict>{body}</dict></plist>\n");

    private ExistingAppRequest Request(string app, Func<ExistingAppRequest, ExistingAppRequest>? tweak = null)
    {
        var r = new ExistingAppRequest
        {
            InputApp = app, OutputDirectory = _t.Combine("out"), IdentitySha1 = AppSha, EntitlementsFile = Entitlements(), EntitlementsConfirmed = true,
            MakePkg = true, InstallerSha1 = InstallerSha, ReleaseVersion = "1.0.0-beta.2",
        };
        return tweak is null ? r : tweak(r);
    }

    private ExistingAppDistribution Service(FakeMac? mac = null, bool isMac = true) => new(mac ?? new FakeMac(), _t.Combine("logs"), isMac);

    private static string TreeHash(string dir)
    {
        var lines = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(f => Path.GetRelativePath(dir, f) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines))));
    }

    // ───────────── Gating ─────────────

    [Fact]
    public void A_complete_Developer_ID_request_is_ready_and_names_the_distribution_files()
    {
        var plan = Service().Plan(Request(MailLikeApp()), Keychain);
        Assert.True(plan.CanRun, string.Join(" | ", plan.Problems));
        Assert.Empty(plan.Warnings);
        Assert.Equal($"Developer ID Application: HCG ({Team})", plan.Identity!.Name);
        Assert.Equal(InstallerSha, plan.Installer!.Sha1);
        Assert.Equal([AllowJit], plan.Entitlements);
        Assert.Equal("HCG Mail.app.zip", Path.GetFileName(plan.ZipPath));
        Assert.Equal("HCG Mail-1.0.0-beta.2-osx-arm64.pkg", Path.GetFileName(plan.PkgPath));
        Assert.Equal(_t.Combine("out", "HCG Mail.app"), plan.OutputApp);
    }

    public static TheoryData<string, string> Refusals => new()
    {
        { "not-mac", "runs on a Mac" },
        { "adhoc-with-identity", "uses no certificate" },
        { "adhoc-notarize", "Test builds are not notarized" },
        { "adhoc-signed-pkg", "stays unsigned" },
        { "no-identity", "Choose a Developer ID Application identity" },
        { "unknown-identity", "is not in the keychain" },
        { "development-identity", "is not a Developer ID Application identity" },
        { "other-team-installer", "belongs to another team" },
        { "installer-as-app", "is not a Developer ID Application identity" },
        { "output-inside-app", "must be outside the app" },
        { "output-is-input-folder", "folder that holds the input app" },
        { "not-confirmed", "confirm them" },
        { "get-task-allow", "get-task-allow" },
        { "string-entitlement", "Only entitlements set to true" },
        { "missing-entitlements", "does not exist" },
        { "data-in-macos", "Contents/MacOS holds files that are not code (Contents/MacOS/HCGMail.dll)" },
        { "version-mismatch", "does not match the app" },
        { "version-not-semver", "not a Semantic Version" },
        { "not-an-app", "is not a macOS .app bundle" },
        { "empty-profile", "keychain profile name is empty" },
    };

    [Theory, MemberData(nameof(Refusals))]
    public void Every_refusal_happens_before_anything_is_copied_or_signed(string scenario, string expected)
    {
        var app = MailLikeApp(dataInMacOS: scenario == "data-in-macos");
        var request = Request(app);
        request = scenario switch
        {
            "adhoc-with-identity" => request with { AdHocTest = true, InstallerSha1 = null },
            "adhoc-notarize" => request with { AdHocTest = true, IdentitySha1 = null, InstallerSha1 = null, NotaryKeychainProfile = "HCG-Notary" },
            "adhoc-signed-pkg" => request with { AdHocTest = true, IdentitySha1 = null },
            "no-identity" => request with { IdentitySha1 = null },
            "unknown-identity" => request with { IdentitySha1 = "9999999999999999999999999999999999999999" },
            "development-identity" => request with { IdentitySha1 = "4444444444444444444444444444444444444444" },
            "other-team-installer" => request with { InstallerSha1 = "3333333333333333333333333333333333333333" },
            "installer-as-app" => request with { IdentitySha1 = InstallerSha },
            "output-inside-app" => request with { OutputDirectory = Path.Combine(app, "Contents", "out") },
            "output-is-input-folder" => request with { OutputDirectory = Path.GetDirectoryName(app)! },
            "not-confirmed" => request with { EntitlementsConfirmed = false },
            "get-task-allow" => request with { EntitlementsFile = Entitlements("<key>com.apple.security.get-task-allow</key><true/>") },
            "string-entitlement" => request with { EntitlementsFile = Entitlements("<key>com.apple.application-identifier</key><string>X.y</string>") },
            "missing-entitlements" => request with { EntitlementsFile = _t.Combine("nope.entitlements") },
            "version-mismatch" => request with { ReleaseVersion = "1.0.1-beta.2" },
            "version-not-semver" => request with { ReleaseVersion = "beta two" },
            "not-an-app" => request with { InputApp = _t.Combine("ci") },
            "empty-profile" => request with { NotaryKeychainProfile = " " },
            _ => request,
        };
        var mac = new FakeMac();
        var service = Service(mac, isMac: scenario != "not-mac");
        Assert.Contains(service.Plan(request, Keychain).Problems, p => p.Contains(expected, StringComparison.Ordinal));
        var before = TreeHash(app);
        var result = service.RunAsync(request, Keychain, _ => { }, CancellationToken.None).GetAwaiter().GetResult();
        Assert.False(result.Succeeded);
        Assert.Empty(mac.Calls);
        Assert.Equal(before, TreeHash(app));
        Assert.False(Directory.Exists(_t.Combine("out", "HCG Mail.app")));
    }

    [Fact]
    public void An_existing_signed_copy_is_never_overwritten()
    {
        var app = MailLikeApp();
        Directory.CreateDirectory(_t.Combine("out", "HCG Mail.app"));
        Assert.Contains(Service().Plan(Request(app), Keychain).Problems, p => p.Contains("already exists", StringComparison.Ordinal));
    }

    [Fact]
    public void A_dotNet_app_without_allow_jit_is_warned_about_and_an_unsigned_pkg_is_named_as_such()
    {
        var plan = Service().Plan(Request(MailLikeApp(), r => r with { EntitlementsFile = null, InstallerSha1 = null }), Keychain);
        Assert.True(plan.CanRun, string.Join(" | ", plan.Problems));
        Assert.Contains(plan.Warnings, w => w.Contains(AllowJit, StringComparison.Ordinal) && w.Contains(".NET", StringComparison.Ordinal));
        Assert.Contains(plan.Warnings, w => w.Contains("unsigned", StringComparison.Ordinal));
        Assert.True(MacBundleLayout.LooksLikeDotNet(MailLikeApp(), MacAppFacts.Read(MailLikeApp())));
    }

    [Fact]
    public void Entitlements_are_read_from_the_plist_as_written()
    {
        var (plist, keys, problem) = ExistingAppDistribution.ReadEntitlements(Entitlements());
        Assert.Null(problem);
        Assert.Equal([AllowJit], keys);
        Assert.Contains("<plist", plist);
        var none = ExistingAppDistribution.ReadEntitlements(null);
        Assert.Null(none.Plist);
        Assert.Empty(none.Keys);
        Assert.Null(none.Problem);
        Assert.NotNull(ExistingAppDistribution.ReadEntitlements(_t.Write("bad.plist", "not a plist")).Problem);
    }

    // ───────────── Signing order ─────────────

    [Fact]
    public void Nested_code_is_signed_inside_out_and_the_main_executable_only_with_the_bundle()
    {
        var app = MailLikeApp();
        var order = MacBundleLayout.SigningOrder(app, MacAppFacts.Read(app)).Select(p => Path.GetRelativePath(app, p).Replace('\\', '/')).ToList();
        Assert.DoesNotContain("Contents/MacOS/HCG Mail", order);
        Assert.Contains("Contents/MacOS/libSkiaSharp.dylib", order);
        Assert.True(order.IndexOf("Contents/Frameworks/Helper.framework/Versions/A/Helper") < order.IndexOf("Contents/Frameworks/Helper.framework"));
        Assert.Equal(5, order.Count); // three libraries, the framework binary, the framework
        Assert.Equal(["Contents/MacOS/HCGMail.dll"], MacBundleLayout.DataInMacOS(MailLikeApp(dataInMacOS: true), MacAppFacts.Read(MailLikeApp(dataInMacOS: true))));
    }

    // ───────────── The steps ─────────────

    [Fact]
    public async Task Developer_ID_signs_a_copy_notarizes_staples_rezips_packages_and_writes_passports()
    {
        var app = MailLikeApp();
        var before = TreeHash(app);
        var mac = new FakeMac();
        var lines = new List<string>();
        var result = await Service(mac).RunAsync(Request(app, r => r with { NotaryKeychainProfile = "HCG-Notary", BuildNumber = "7" }), Keychain, lines.Add, CancellationToken.None);

        Assert.True(result.Succeeded, string.Join("\n", result.Steps.Where(s => s.Status == VerificationStatus.Failed).Select(s => $"{s.Name}: {s.Detail}")));
        Assert.Equal(before, TreeHash(app)); // the input is never modified
        var copy = _t.Combine("out", "HCG Mail.app");
        Assert.Equal(copy, result.SignedApp);

        // Copy first, extended attributes cleared, then every signature on the copy only.
        Assert.Equal(["/usr/bin/ditto", Path.GetFullPath(app), copy], [mac.Calls[0].Executable, .. mac.Calls[0].Arguments]);
        Assert.Equal(["-cr", copy], mac.Calls[1].Arguments);
        var signs = mac.Calls.Where(c => c.Executable.EndsWith("codesign") && c.Arguments.Contains("--sign")).ToList();
        Assert.All(signs, s => Assert.StartsWith(copy, s.Arguments[^1], StringComparison.Ordinal));
        Assert.All(signs, s => Assert.Equal(AppSha, s.Arguments[s.Arguments.ToList().IndexOf("--sign") + 1]));
        Assert.All(signs, s => Assert.Contains("--timestamp", s.Arguments));
        Assert.All(signs, s => Assert.Equal("runtime", s.Arguments[s.Arguments.ToList().IndexOf("--options") + 1]));
        Assert.Equal(6, signs.Count);
        Assert.Equal(copy, signs[^1].Arguments[^1]); // the bundle last
        Assert.Contains("--entitlements", signs[^1].Arguments);
        Assert.All(signs.SkipLast(1), s => Assert.DoesNotContain("--entitlements", s.Arguments));
        Assert.Equal(AllowJit, Assert.Single(mac.SignedEntitlements));

        // Notarized as a ZIP, stapled, and only then zipped for distribution; the .pkg is signed, notarized and stapled too.
        int Index(Func<ProcessSpec, bool> f) => mac.Calls.FindIndex(c => f(c));
        var stapleApp = Index(c => c.Arguments is ["stapler", "staple", var p] && p == copy);
        var distZip = Index(c => c.Executable == "/usr/bin/ditto" && c.Arguments.Contains("--sequesterRsrc"));
        Assert.InRange(stapleApp, 0, distZip - 1);
        Assert.Equal(2, mac.Calls.Count(c => c.Arguments is ["notarytool", "submit", ..]));
        Assert.All(mac.Calls.Where(c => c.Arguments is ["notarytool", ..]), c => Assert.Equal("HCG-Notary", c.Arguments[c.Arguments.ToList().IndexOf("--keychain-profile") + 1]));
        var productbuild = mac.Calls.Single(c => c.Executable == "/usr/bin/productbuild");
        Assert.Equal($"Developer ID Installer: HCG ({Team})", productbuild.Arguments[productbuild.Arguments.ToList().IndexOf("--sign") + 1]);
        Assert.Equal("1.0.0-beta.2", mac.PkgVersion);
        Assert.Contains(mac.Calls, c => c.Arguments is ["stapler", "staple", var p] && p.EndsWith(".pkg", StringComparison.Ordinal));
        Assert.Contains(result.Steps, s => s is { Name: "App: Gatekeeper", Status: VerificationStatus.Passed });
        Assert.Contains(result.Steps, s => s is { Name: "Package: Gatekeeper", Status: VerificationStatus.Passed });

        // Files, checksums and passports (release version, build number, final bytes).
        Assert.Equal(["HCG Mail.app.zip", "HCG Mail-1.0.0-beta.2-osx-arm64.pkg"], result.Files.Select(Path.GetFileName));
        Assert.Equal(2, result.Passports.Count);
        foreach (var file in result.Files)
        {
            var passport = JsonNode.Parse(File.ReadAllText(file + ".release-passport.json"))!;
            Assert.Equal("1.0.0-beta.2", (string?)passport["version"]);
            Assert.Equal("7", (string?)passport["build_number"]);
            Assert.Equal("com.hakmicapitalgroup.hcgmail", (string?)passport["package"]);
            Assert.Equal(await Checksums.Sha256FileAsync(file), (string?)passport["artifact"]!["sha256"]);
        }
        var sums = File.ReadAllText(_t.Combine("out", "SHA256SUMS.txt"));
        Assert.Contains("  HCG Mail.app.zip\n", sums);
        Assert.True(File.Exists(result.Report));
        Assert.DoesNotContain(lines, l => l.Contains("BEGIN PRIVATE KEY", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_ad_hoc_test_build_runs_the_same_checks_without_a_certificate_or_notarization()
    {
        var app = MailLikeApp();
        var mac = new FakeMac();
        var result = await Service(mac).RunAsync(Request(app, r => r with { IdentitySha1 = null, InstallerSha1 = null, AdHocTest = true }), new MacSigningInventory(), _ => { }, CancellationToken.None);

        Assert.True(result.Succeeded, string.Join("\n", result.Steps.Where(s => s.Status == VerificationStatus.Failed).Select(s => $"{s.Name}: {s.Detail}")));
        var signs = mac.Calls.Where(c => c.Executable.EndsWith("codesign") && c.Arguments.Contains("--sign")).ToList();
        Assert.All(signs, s => Assert.Equal("-", s.Arguments[s.Arguments.ToList().IndexOf("--sign") + 1]));
        Assert.All(signs, s => Assert.Contains("--timestamp=none", s.Arguments));
        Assert.All(signs, s => Assert.Contains("runtime", s.Arguments));
        Assert.DoesNotContain(mac.Calls, c => c.Arguments.Contains("notarytool"));
        Assert.DoesNotContain("--sign", mac.Calls.Single(c => c.Executable == "/usr/bin/productbuild").Arguments);
        Assert.All(result.Files, f => Assert.Contains(ExistingAppDistribution.TestSuffix, Path.GetFileName(f), StringComparison.Ordinal));
        Assert.Contains(result.Steps, s => s is { Name: "Signature kind", Status: VerificationStatus.PassedWithWarnings });
        Assert.Contains(result.Steps, s => s is { Name: "Notarization", Status: VerificationStatus.NotVerifiable });
        Assert.Contains(result.Steps, s => s is { Name: "Gatekeeper", Status: VerificationStatus.PassedWithWarnings });
    }

    [Fact]
    public async Task A_notary_rejection_stops_before_any_distribution_file_and_saves_the_log()
    {
        var mac = new FakeMac { NotaryStatus = "Invalid" };
        var result = await Service(mac).RunAsync(Request(MailLikeApp(), r => r with { NotaryKeychainProfile = "HCG-Notary" }), Keychain, _ => { }, CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Contains(result.Steps, s => s is { Name: "App: Notarization", Status: VerificationStatus.Failed } && s.Detail.Contains("Invalid", StringComparison.Ordinal));
        Assert.Contains(mac.Calls, c => c.Arguments is ["notarytool", "log", ..]);
        Assert.Empty(result.Files);
        Assert.False(File.Exists(_t.Combine("out", "HCG Mail.app.zip")));
    }

    [Fact]
    public async Task A_failed_signature_check_stops_before_packaging()
    {
        var mac = new FakeMac { WrongTeam = true };
        var result = await Service(mac).RunAsync(Request(MailLikeApp()), Keychain, _ => { }, CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Contains(result.Steps, s => s is { Name: "Nested code", Status: VerificationStatus.Failed });
        Assert.DoesNotContain(mac.Calls, c => c.Executable == "/usr/bin/pkgbuild");
    }

    // ───────────── Release version, saved route, notarization gate ─────────────

    [Theory]
    [InlineData("1.0.0-beta.2", "1.0.0", null)]
    [InlineData("1.0.0-beta.2", "1.0", null)]
    [InlineData("1.0.0", "1.0.0", null)]
    [InlineData(null, "1.0.0", null)]
    [InlineData("1.0.1-beta.2", "1.0.0", "does not match")]
    [InlineData("beta", "1.0.0", "not a Semantic Version")]
    [InlineData("1.0.0-beta.2", null, "no CFBundleShortVersionString")]
    public void Release_version_override_must_match_the_bundle(string? release, string? shortVersion, string? problem)
    {
        var p = MacReleasePassportWriter.VersionProblem(release, shortVersion);
        if (problem is null) Assert.Null(p); else Assert.Contains(problem, p);
    }

    [Fact]
    public void The_saved_macOS_Developer_ID_route_fills_in_the_identities_it_still_finds()
    {
        var saved = new ProjectSigningConfiguration
        {
            ProjectId = "p", Platform = SigningPlatform.MacOs, Route = SigningRoute.MacDeveloperId, Mode = ProjectSigningMode.Manual,
            IdentitySha1 = AppSha, InstallerIdentitySha1 = InstallerSha,
        };
        var (options, note) = SavedMacSigning.Apply(new MacDistributionOptions(), saved, Keychain);
        Assert.Equal(MacSigningMode.DeveloperId, options.SigningMode);
        Assert.Equal(AppSha, options.DeveloperIdApplicationSha1);
        Assert.Equal(InstallerSha, options.PkgInstallerSha1);
        Assert.Equal($"Developer ID Installer: HCG ({Team})", options.PkgInstallerName);
        Assert.Contains("Project signing", note);

        var gone = SavedMacSigning.Apply(new MacDistributionOptions(), saved with { IdentitySha1 = "9999999999999999999999999999999999999999" }, Keychain);
        Assert.Equal(MacSigningMode.AdHoc, gone.Options.SigningMode);
        Assert.Contains("not applied", gone.Note);
        Assert.Equal((new MacDistributionOptions(), (string?)null), SavedMacSigning.Apply(new MacDistributionOptions(), saved with { Route = SigningRoute.IosAppStore }, Keychain));
        Assert.Equal((new MacDistributionOptions(), (string?)null), SavedMacSigning.Apply(new MacDistributionOptions(), null, Keychain));
        Assert.Contains("without a chosen identity", SavedMacSigning.Apply(new MacDistributionOptions(), saved with { Mode = ProjectSigningMode.XcodeAutomatic }, Keychain).Note);
    }

    [Fact]
    public void Notarization_needs_a_Mac_a_Developer_ID_signature_and_a_keychain_profile()
    {
        Assert.Null(NotaryClient.Blocker(true, "HCG-Notary", isMac: true));
        Assert.Contains("on a Mac", NotaryClient.Blocker(true, "HCG-Notary", isMac: false));
        Assert.Contains("not signed with Developer ID", NotaryClient.Blocker(false, "HCG-Notary", isMac: true));
        Assert.Contains("store-credentials", NotaryClient.Blocker(true, null, isMac: true));
    }

    [Fact]
    public void A_dotNet_GUI_app_proposes_allow_jit_for_Developer_ID()
    {
        var plan = Packaging.MacDistribution.Entitlements.ForDeveloperId([MacBundleLayout.DotNetRuntime]);
        Assert.Equal(AllowJit, Assert.Single(plan.Proposed).Key);
    }

    /// <summary>The Mac tools HCG runs, scripted with their real effects on disk (copies, archives, packages) and recorded.</summary>
    private sealed class FakeMac : IProcessRunner
    {
        public List<ProcessSpec> Calls { get; } = [];
        public List<string> SignedEntitlements { get; } = [];
        public string NotaryStatus { get; init; } = "Accepted";
        public bool WrongTeam { get; init; }
        public string? PkgVersion { get; private set; }
        private string? _pkgId, _pkgRoot;
        private bool _adHoc;
        private readonly HashSet<string> _stapled = new(StringComparer.Ordinal);
        private readonly HashSet<string> _signed = new(StringComparer.Ordinal);

        public Task<ProcessResult> RunAsync(ProcessSpec spec, Action<OutputStream, string>? onOutput, CancellationToken cancellationToken)
        {
            Calls.Add(spec);
            var (exit, stdout, stderr) = Answer(spec, [.. spec.Arguments]);
            foreach (var l in stdout.Split('\n')) onOutput?.Invoke(OutputStream.StdOut, l);
            foreach (var l in stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries)) onOutput?.Invoke(OutputStream.StdErr, l);
            return Task.FromResult(new ProcessResult(exit, TimeSpan.Zero, false, false));
        }

        private static void Copy(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var d in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, d)));
            foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories)) File.Copy(f, Path.Combine(to, Path.GetRelativePath(from, f)), true);
        }

        private (int, string, string) Answer(ProcessSpec spec, string[] a)
        {
            switch (Path.GetFileName(spec.Executable))
            {
                case "ditto" when a[0] == "-c":
                    File.WriteAllText(a[^1], $"zip of {a[^2]}; stapled={_stapled.Contains(a[^2])}");
                    return (0, "", "");
                case "ditto":
                    Copy(a[0], a[1]);
                    return (0, "", "");
                case "xattr":
                    return (0, "", "");
                case "codesign" when a.Contains("--sign"):
                    var target = a[^1];
                    _adHoc = a[Array.IndexOf(a, "--sign") + 1] == "-";
                    _signed.Add(target);
                    if (Array.IndexOf(a, "--entitlements") is >= 0 and var e)
                        SignedEntitlements.AddRange(ExistingAppDistribution.ReadEntitlements(a[e + 1]).Keys);
                    return (0, "", "");
                case "codesign" when a.Contains("--verify"):
                    return (0, "", $"{a[^1]}: valid on disk\n{a[^1]}: satisfies its Designated Requirement");
                case "codesign" when a.Contains("--entitlements"):
                    return (0, Plist().Replace("<dict>\n", "<dict>\n" + string.Concat(SignedEntitlements.Select(k => $"<key>{k}</key><true/>"))), "");
                case "codesign" when a.Contains("-d"):
                    if (!_signed.Contains(a[^1])) return (1, "", "code object is not signed at all");
                    var team = WrongTeam && !a[^1].EndsWith(".app", StringComparison.Ordinal) ? "ZZZZZ99999" : Team;
                    return (0, "", _adHoc
                        ? $"Executable={a[^1]}\nIdentifier=x\nCodeDirectory v=20500 size=1 flags=0x10002(adhoc,runtime) hashes=1+0\nSignature=adhoc\nTeamIdentifier=not set"
                        : $"Executable={a[^1]}\nIdentifier=x\nCodeDirectory v=20500 size=1 flags=0x10000(runtime) hashes=1+0\nAuthority=Developer ID Application: HCG ({team})\nAuthority=Developer ID Certification Authority\nAuthority=Apple Root CA\nTimestamp=27 Nov 2026 at 10:00:00\nTeamIdentifier={team}");
                case "spctl":
                    return _stapled.Contains(a[^1]) ? (0, "", $"{a[^1]}: accepted\nsource=Notarized Developer ID") : (3, "", $"{a[^1]}: rejected\nsource=Unnotarized Developer ID");
                case "pkgbuild":
                    _pkgId = a[Array.IndexOf(a, "--identifier") + 1];
                    PkgVersion = a[Array.IndexOf(a, "--version") + 1];
                    _pkgRoot = a[Array.IndexOf(a, "--root") + 1];
                    File.WriteAllText(a[^1], "component");
                    return (0, "", "");
                case "productbuild":
                    File.WriteAllText(a[^1], "flat package");
                    return (0, "", "");
                case "pkgutil" when a[0] == "--check-signature":
                    return Calls.Last(c => c.Executable == "/usr/bin/productbuild").Arguments.Contains("--sign")
                        ? (0, $"Package \"x.pkg\":\n   Status: signed by a developer certificate issued by Apple for distribution\n   Certificate Chain:\n    1. Developer ID Installer: HCG ({Team})\n", "")
                        : (1, "Package \"x.pkg\":\n   Status: no signature\n", "");
                case "pkgutil" when a[0] == "--expand-full":
                    var x = a[2];
                    Directory.CreateDirectory(Path.Combine(x, "component.pkg"));
                    File.WriteAllText(Path.Combine(x, "Distribution"), "<installer-gui-script/>");
                    File.WriteAllText(Path.Combine(x, "component.pkg", "PackageInfo"), $"<pkg-info identifier=\"{_pkgId}\" version=\"{PkgVersion}\" install-location=\"/Applications\"/>");
                    var app = Directory.EnumerateDirectories(_pkgRoot!, "*.app").Single();
                    Copy(app, Path.Combine(x, "component.pkg", "Payload", Path.GetFileName(app)));
                    return (0, "", "");
                case "xcrun" when a is ["notarytool", "submit", ..]:
                    return (0, $"{{\"id\":\"2f1c-{Calls.Count}\",\"status\":\"{NotaryStatus}\",\"message\":\"Processing complete\"}}", "");
                case "xcrun" when a is ["stapler", "staple", var path]:
                    _stapled.Add(path);
                    return (0, "The staple and validate action worked!", "");
                default:
                    return (0, "", "");
            }
        }
    }
}
