using System.IO.Compression;
using HCG.ExecutableMaker.BuildEngine.MacDistribution;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Infrastructure.Persistence;
using HCG.ExecutableMaker.Packaging;
using HCG.ExecutableMaker.Packaging.MacDistribution;
using HCG.ExecutableMaker.Packaging.Packagers;

namespace HCG.ExecutableMaker.Tests;

/// <summary>
/// Developer ID distribution of an app built elsewhere (HCG Mail's signing input shape): inside-out signing order,
/// checks before signing, the original never modified, nothing signed when a check fails.
/// </summary>
public sealed class ExistingMacAppTests : IDisposable
{
    private readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    private static readonly byte[] Arm64 = MobileSamples.MachO(0x0100000C);

    /// <summary>HCG Mail's layout: the app host and three native libraries in Contents/MacOS, data in Resources.</summary>
    private string MailShapedApp(string root, string version = "1.0.0", string build = "5", bool dataInMacOS = false)
    {
        var app = Path.Combine(root, "HCG Mail.app");
        var macos = Path.Combine(app, "Contents", "MacOS");
        Directory.CreateDirectory(macos);
        Directory.CreateDirectory(Path.Combine(app, "Contents", "Resources"));
        File.WriteAllText(Path.Combine(app, "Contents", "Info.plist"), InfoPlist.Create(
        [
            ("CFBundleIdentifier", "com.hakmicapital.hcgmail"), ("CFBundleName", "HCG Mail"), ("CFBundleDisplayName", "HCG Mail"),
            ("CFBundleExecutable", "HCG Mail"), ("CFBundleShortVersionString", version), ("CFBundleVersion", build), ("LSMinimumSystemVersion", "12.0"),
            ("CFBundlePackageType", "APPL"),
        ]));
        File.WriteAllBytes(Path.Combine(macos, "HCG Mail"), Arm64);
        foreach (var lib in new[] { "libAvaloniaNative.dylib", "libSkiaSharp.dylib", "libHarfBuzzSharp.dylib" }) File.WriteAllBytes(Path.Combine(macos, lib), Arm64);
        File.WriteAllText(Path.Combine(app, "Contents", "Resources", "appsettings.json"), "{}");
        if (dataInMacOS) File.WriteAllText(Path.Combine(macos, "HCG.Mail.dll"), "not code");
        return app;
    }

    private string SigningInputZip(string app)
    {
        var zip = _t.Combine("HCGMail-1.0.0-beta.2-osx-arm64-unsigned-app.zip");
        ZipFile.CreateFromDirectory(app, zip, CompressionLevel.Fastest, includeBaseDirectory: true);
        return zip;
    }

    // ───────────── nested code and signing order ─────────────

    [Fact]
    public void Nested_code_is_every_Mach_O_and_nested_bundle_except_main_executables_deepest_first()
    {
        var app = MailShapedApp(_t.Combine("in"));
        var fw = Path.Combine(app, "Contents", "Frameworks", "Kit.framework");
        Directory.CreateDirectory(Path.Combine(fw, "Resources"));
        File.WriteAllText(Path.Combine(fw, "Resources", "Info.plist"), InfoPlist.Create([("CFBundleExecutable", "Kit")]));
        File.WriteAllBytes(Path.Combine(fw, "Kit"), Arm64);
        Directory.CreateDirectory(Path.Combine(fw, "Libraries"));
        File.WriteAllBytes(Path.Combine(fw, "Libraries", "libdeep.dylib"), Arm64);

        var nested = MacCodeSigner.NestedCode(app).Select(p => Path.GetRelativePath(app, p).Replace('\\', '/')).ToList();

        Assert.DoesNotContain("Contents/MacOS/HCG Mail", nested);        // the app's main executable: signed with the bundle
        Assert.DoesNotContain("Contents/Frameworks/Kit.framework/Kit", nested); // a framework's binary: signed with the framework
        Assert.DoesNotContain(nested, n => n.EndsWith(".json", StringComparison.Ordinal));
        Assert.Contains("Contents/MacOS/libSkiaSharp.dylib", nested);
        Assert.True(nested.IndexOf("Contents/Frameworks/Kit.framework/Libraries/libdeep.dylib") < nested.IndexOf("Contents/Frameworks/Kit.framework"),
            "code inside a framework is signed before the framework");
        Assert.Equal(5, nested.Count); // 3 libraries, the deep library, the framework
    }

    [Fact]
    public async Task Inside_out_signing_signs_each_library_without_entitlements_then_the_bundle_with_them()
    {
        var app = MailShapedApp(_t.Combine("in"));
        var runner = new RecordingProcessRunner(_ => (0, "", ""));
        await new MacCodeSigner(runner, "/usr/bin/codesign").SignBundleInsideOutAsync(app, "ABCDEF0123456789ABCDEF0123456789ABCDEF01", ["com.apple.security.cs.allow-jit"], new NullLog(), CancellationToken.None);

        var signs = runner.Calls.Where(c => c.Executable == "/usr/bin/codesign").Select(c => c.Arguments).ToList();
        Assert.Equal(4, signs.Count);
        foreach (var lib in signs.Take(3))
        {
            Assert.EndsWith(".dylib", lib[^1]);
            Assert.Contains("runtime", lib);
            Assert.Contains("--timestamp", lib);
            Assert.DoesNotContain("--entitlements", lib);
        }
        var bundle = signs[^1];
        Assert.Equal(app, bundle[^1]);
        Assert.Contains("--entitlements", bundle);
        Assert.Contains("runtime", bundle);
        Assert.Contains("ABCDEF0123456789ABCDEF0123456789ABCDEF01", bundle);
        Assert.DoesNotContain("--deep", bundle);
    }

    // ───────────── checks before signing ─────────────

    [Fact]
    public void Published_checksum_must_name_the_file_with_the_same_digest()
    {
        var zip = _t.Write("in.zip", "zip");
        var sha = new string('a', 64);
        var sums = _t.Write("SHA256SUMS-signing-input.txt", $"{sha}  in.zip\n{new string('b', 64)}  other.zip\n");
        Assert.Equal(VerificationStatus.Passed, ExistingMacAppDistribution.PublishedChecksum(zip, sha, sums).Status);
        var wrong = ExistingMacAppDistribution.PublishedChecksum(zip, new string('c', 64), sums);
        Assert.Equal(VerificationStatus.Failed, wrong.Status);
        Assert.Contains("not the file that was published", wrong.Detail);
        var absent = ExistingMacAppDistribution.PublishedChecksum(_t.Write("x.zip", "z"), sha, sums);
        Assert.Contains("does not list x.zip", absent.Detail);
    }

    [Fact]
    public void Release_checks_compare_Info_plist_with_the_release_and_never_change_it()
    {
        var facts = MacAppFacts.Read(MailShapedApp(_t.Combine("in")));
        ExistingMacAppRequest R(string version, string? build, TargetArchitecture arch) => new()
        {
            Input = "x", ReleaseVersion = version, ExpectedBuildNumber = build, Architecture = arch, DeveloperIdApplicationSha1 = "x", OutputDirectory = "o",
        };
        Assert.All(ExistingMacAppDistribution.ReleaseChecks(facts, R("1.0.0-beta.2", "5", TargetArchitecture.Arm64)), c => Assert.Equal(VerificationStatus.Passed, c.Status));
        Assert.Contains(ExistingMacAppDistribution.ReleaseChecks(facts, R("1.0.1", "5", TargetArchitecture.Arm64)), c => c.Name == "Version" && c.Status == VerificationStatus.Failed);
        Assert.Contains(ExistingMacAppDistribution.ReleaseChecks(facts, R("1.0.0-beta.2", "6", TargetArchitecture.Arm64)), c => c.Name == "Build number" && c.Status == VerificationStatus.Failed);
        Assert.Contains(ExistingMacAppDistribution.ReleaseChecks(facts, R("1.0.0-beta.2", "5", TargetArchitecture.Universal)), c => c.Name == "Architectures" && c.Detail.Contains("x86_64 is missing"));
    }

    [Fact]
    public void Data_files_in_Contents_MacOS_are_refused_with_the_reason()
    {
        var app = MailShapedApp(_t.Combine("in"), dataInMacOS: true);
        var check = ExistingMacAppDistribution.CodeOnlyCheck(ExistingMacAppDistribution.NonCodeInMacOS(app));
        Assert.Equal(VerificationStatus.Failed, check.Status);
        Assert.Contains("HCG.Mail.dll", check.Detail);
        Assert.Contains("extended attributes", check.Detail);
        Assert.Equal(VerificationStatus.Passed, ExistingMacAppDistribution.CodeOnlyCheck(ExistingMacAppDistribution.NonCodeInMacOS(MailShapedApp(_t.Combine("ok")))).Status);
    }

    [Fact]
    public void Entitlements_file_gives_its_true_keys_and_reports_anything_else()
    {
        var file = _t.Write("HCGMail.entitlements", """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0"><dict>
              <key>com.apple.security.cs.allow-jit</key><true/>
              <key>com.apple.security.cs.disable-library-validation</key><false/>
            </dict></plist>
            """);
        var (keys, problems) = ExistingMacAppDistribution.ReadEntitlementsFile(file);
        Assert.Equal(["com.apple.security.cs.allow-jit"], keys);
        Assert.Empty(problems);
        var groups = _t.Write("groups.entitlements", "<plist><dict><key>com.apple.security.application-groups</key><array><string>x</string></array></dict></plist>");
        Assert.Contains("only true/false", Assert.Single(ExistingMacAppDistribution.ReadEntitlementsFile(groups).Problems));
        Assert.Null(ExistingMacAppDistribution.Entitlements_Invalid(["com.apple.security.cs.allow-jit"]));
        Assert.NotNull(ExistingMacAppDistribution.Entitlements_Invalid(["allow jit"]));
    }

    [Fact]
    public void Default_file_name_follows_the_store_naming()
    {
        Assert.Equal("HCG-Mail-1.0.0-beta.2-macos-arm64", ExistingMacAppDistribution.DefaultBaseName("HCG Mail", "1.0.0-beta.2", TargetArchitecture.Arm64));
        Assert.Equal("HCG-Mail-1.0.0-macos-universal", ExistingMacAppDistribution.DefaultBaseName("HCG Mail", "1.0.0", TargetArchitecture.Universal));
    }

    // ───────────── the service ─────────────

    private (ExistingMacAppDistribution Service, SqliteBuildRepository Builds, DittoRunner Runner) Service(bool mac = true)
    {
        var paths = new AppPaths(_t.Combine("maker"));
        paths.EnsureCreated();
        var db = new SqliteDatabase(paths);
        var builds = new SqliteBuildRepository(db);
        var runner = new DittoRunner();
        return (new ExistingMacAppDistribution(runner, builds, new SqliteProjectRepository(db), paths, TimeProvider.System, () => mac), builds, runner);
    }

    private ExistingMacAppRequest Request(string input, string? sums = null, string build = "5") => new()
    {
        Input = input, ChecksumsFile = sums, Origin = "HCG Mail CI run 46007763971, artifact 15201589154", ReleaseVersion = "1.0.0-beta.2", ExpectedBuildNumber = build,
        Architecture = TargetArchitecture.Arm64, DeveloperIdApplicationSha1 = DittoRunner.AppSha1, InstallerSha1 = DittoRunner.InstallerSha1,
        Entitlements = ["com.apple.security.cs.allow-jit"], EntitlementsConfirmed = true, OutputDirectory = _t.Combine("out"),
    };

    [Fact]
    public async Task Inspecting_the_signing_input_reads_it_without_changing_it()
    {
        var zip = SigningInputZip(MailShapedApp(_t.Combine("in")));
        var before = await Checksums.Sha256FileAsync(zip);
        var sums = _t.Write("SHA256SUMS-signing-input.txt", $"{before}  {Path.GetFileName(zip)}\n");
        var (service, _, _) = Service(mac: false);
        var i = await service.InspectAsync(zip, sums, CancellationToken.None);
        Assert.True(i.Usable, string.Join("; ", i.Checks.Select(c => c.Detail)));
        Assert.Equal("com.hakmicapital.hcgmail", i.Facts.BundleIdentifier);
        Assert.Equal("5", i.Facts.BuildVersion);
        Assert.Equal(3, i.NestedCode.Count);
        Assert.Equal(before, await Checksums.Sha256FileAsync(zip));
    }

    [Fact]
    public async Task A_signing_input_that_does_not_match_its_published_checksum_is_recorded_as_failed_and_nothing_is_signed()
    {
        var zip = SigningInputZip(MailShapedApp(_t.Combine("in")));
        var sums = _t.Write("SHA256SUMS-signing-input.txt", $"{new string('0', 64)}  {Path.GetFileName(zip)}\n");
        var (service, _, runner) = Service();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SignAsync(Request(zip, sums), _ => { }, CancellationToken.None));
        Assert.Contains("not the file that was published", ex.Message);
        Assert.DoesNotContain(runner.Calls, c => c.Executable.EndsWith("codesign", StringComparison.Ordinal) && c.Arguments.Contains("--sign"));
    }

    [Fact]
    public async Task Another_build_number_stops_before_signing_and_the_job_says_why()
    {
        var zip = SigningInputZip(MailShapedApp(_t.Combine("in")));
        var before = await Checksums.Sha256FileAsync(zip);
        var (service, builds, runner) = Service();
        var build = await service.SignAsync(Request(zip, build: "6"), _ => { }, CancellationToken.None);
        Assert.Equal(BuildStatus.Failed, build.Status);
        Assert.Contains("CFBundleVersion is 5, not 6", build.FailureReason);
        Assert.DoesNotContain(runner.Calls, c => c.Executable.EndsWith("codesign", StringComparison.Ordinal) && c.Arguments.Contains("--sign"));
        Assert.Equal(before, await Checksums.Sha256FileAsync(zip));
        var stored = await builds.GetAsync(build.Id);
        Assert.Equal(ExistingMacAppDistribution.ProviderId, stored!.ToolchainVersions["build-provider"]);
        Assert.Equal(before, stored.ToolchainVersions["input-sha256"]);
        Assert.Equal("HCG Mail CI run 46007763971, artifact 15201589154", stored.ToolchainVersions["input-origin"]);
    }

    [Fact]
    public async Task Signing_needs_a_Mac_confirmed_entitlements_and_Developer_ID_identities_from_the_keychain()
    {
        var zip = SigningInputZip(MailShapedApp(_t.Combine("in")));
        var (notMac, _, _) = Service(mac: false);
        Assert.Contains("on a Mac", (await Assert.ThrowsAsync<InvalidOperationException>(() => notMac.SignAsync(Request(zip), _ => { }, CancellationToken.None))).Message);
        var (service, _, _) = Service();
        Assert.Contains("Confirm the entitlements", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SignAsync(Request(zip) with { EntitlementsConfirmed = false }, _ => { }, CancellationToken.None))).Message);
        Assert.Contains("is not a Developer ID Application identity", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SignAsync(Request(zip) with { DeveloperIdApplicationSha1 = DittoRunner.InstallerSha1 }, _ => { }, CancellationToken.None))).Message);
        Assert.Contains("No signing identity", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SignAsync(Request(zip) with { DeveloperIdApplicationSha1 = new string('9', 40) }, _ => { }, CancellationToken.None))).Message);
        Assert.Contains("Developer ID Installer identity", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SignAsync(Request(zip) with { InstallerSha1 = null }, _ => { }, CancellationToken.None))).Message);
    }

    [Fact]
    public void Notarizing_needs_a_signed_existing_app_job_and_a_profile()
    {
        var job = new BuildRecord
        {
            Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), StartedAt = DateTimeOffset.UtcNow, Status = BuildStatus.Succeeded,
            Settings = new BuildSettings
            {
                ProjectRelativePath = "HCG Mail.app", Platform = TargetPlatform.MacOS, Architecture = TargetArchitecture.Arm64, OutputDirectory = "/o",
                Identity = new AppIdentity { ApplicationName = "HCG Mail", Version = "1.0.0-beta.2", ApplicationId = "com.hakmicapital.hcgmail" },
                MacDistribution = new MacDistributionOptions { SigningMode = MacSigningMode.DeveloperId, DeveloperIdApplicationSha1 = DittoRunner.AppSha1 },
            },
        };
        Assert.Contains("not an existing-app", ExistingMacAppDistribution.NotarizeBlocker(job, "hcg-notary"));
        job.ToolchainVersions["build-provider"] = ExistingMacAppDistribution.ProviderId;
        Assert.Contains("signed app is missing", ExistingMacAppDistribution.NotarizeBlocker(job, "hcg-notary"));
        Assert.False(ExistingMacAppDistribution.ReadyForDistribution(job));
    }

    private sealed class NullLog : IPackagingLog
    {
        public void Info(string message) { }
        public void Warning(string message) { }
    }

    /// <summary>ditto as files (copy, extract, archive), the keychain's identities, and every other tool answering "ok".</summary>
    private sealed class DittoRunner : IProcessRunner
    {
        public const string AppSha1 = "1111111111111111111111111111111111111111";
        public const string InstallerSha1 = "2222222222222222222222222222222222222222";
        public List<ProcessSpec> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(ProcessSpec spec, Action<OutputStream, string>? onOutput, CancellationToken cancellationToken)
        {
            Calls.Add(spec);
            var a = spec.Arguments;
            var stdout = "";
            if (spec.Executable == "/usr/bin/ditto")
            {
                if (a[0] == "-x") ZipFile.ExtractToDirectory(a[2], a[3]);
                else if (a[0] == "-c") ZipFile.CreateFromDirectory(a[^2], a[^1], CompressionLevel.Fastest, includeBaseDirectory: true);
                else Copy(a[0], a[1]);
            }
            else if (spec.Executable == "/usr/bin/security")
                stdout = $"""
                      1) {AppSha1} "Developer ID Application: Hakmi Capital Group (HCGTEAM001)"
                      2) {InstallerSha1} "Developer ID Installer: Hakmi Capital Group (HCGTEAM001)"
                         2 valid identities found
                    """;
            foreach (var line in stdout.Split('\n')) onOutput?.Invoke(OutputStream.StdOut, line);
            return Task.FromResult(new ProcessResult(0, TimeSpan.Zero, false, false));
        }

        private static void Copy(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var d in Directory.GetDirectories(from, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, d)));
            foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories)) File.Copy(f, Path.Combine(to, Path.GetRelativePath(from, f)), overwrite: true);
        }
    }
}
