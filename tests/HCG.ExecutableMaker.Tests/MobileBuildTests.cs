using System.IO.Compression;
using HCG.ExecutableMaker.BuildEngine;
using HCG.ExecutableMaker.BuildEngine.Mobile;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Infrastructure.Persistence;
using HCG.ExecutableMaker.Security;

namespace HCG.ExecutableMaker.Tests;

/// <summary>Shared fakes for mobile build tests: a JDK-shaped folder, toolchain reports and a scripted worker.</summary>
public sealed class MobileTestKit : IDisposable
{
    public TempDir Temp { get; } = new();
    public string Java { get; }
    public string Sdk { get; }
    public string ToolHome { get; }

    public MobileTestKit()
    {
        var jdk = Temp.Combine("jdk");
        Directory.CreateDirectory(Path.Combine(jdk, "bin"));
        Java = Path.Combine(jdk, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
        File.WriteAllText(Java, "");
        File.WriteAllText(Path.Combine(jdk, "bin", OperatingSystem.IsWindows() ? "keytool.exe" : "keytool"), "");
        Sdk = Temp.Combine("android-sdk");
        Directory.CreateDirectory(Sdk);
        ToolHome = Temp.Combine("toolhomes");
    }

    public void Dispose() => Temp.Dispose();

    public ToolchainReport Report(TargetPlatform host, params string[] available)
    {
        string? PathFor(string id) => id switch
        {
            ToolchainIds.Jdk => Java,
            ToolchainIds.AndroidSdk => Sdk,
            ToolchainIds.Bundletool => Temp.Combine("bundletool-all-1.18.1.jar"),
            ToolchainIds.IosSdk => "/usr/bin/xcodebuild",
            ToolchainIds.Flutter => "/opt/flutter/bin/flutter",
            ToolchainIds.DotNetSdk => Fixtures.FakeDotNet,
            _ => "/x/" + id,
        };
        string[] all = [ToolchainIds.DotNetSdk, ToolchainIds.Jdk, ToolchainIds.AndroidSdk, ToolchainIds.Bundletool, ToolchainIds.Xcode,
            ToolchainIds.IosSdk, ToolchainIds.AppleSigningIdentities, ToolchainIds.Flutter, ToolchainIds.MauiWorkloads];
        return new ToolchainReport(new HostEnvironment(host, TargetArchitecture.Arm64, "test", ".NET", "m"),
            all.Select(id => new ToolchainInfo
            {
                Id = id, DisplayName = id, Category = "x", Status = available.Contains(id) ? ToolchainStatus.Installed : ToolchainStatus.Missing,
                Version = id == ToolchainIds.Jdk ? "21.0.2" : "1.0", ExecutablePath = PathFor(id),
                AdditionalVersions = id == ToolchainIds.MauiWorkloads ? ["maui-android", "maui-ios"] : [],
            }).ToList(), null, null, DateTimeOffset.UtcNow);
    }

    public static readonly string[] Android = [ToolchainIds.Jdk, ToolchainIds.AndroidSdk, ToolchainIds.Bundletool];
    public static readonly string[] Apple = [ToolchainIds.Xcode, ToolchainIds.IosSdk, ToolchainIds.AppleSigningIdentities];

    public static async Task<MobileAnalysis> AnalyzeAsync(string dir, ToolchainReport report) =>
        (await MobileInspectionTests.Service().InspectAsync(dir, report, CancellationToken.None)).Mobile!;

    public static string Fixture(string name) => Path.Combine(Fixtures.RepoRoot, "tests", "Fixtures", name);

    public static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.EnumerateDirectories(from))
        {
            var name = Path.GetFileName(dir);
            if (name is "build" or ".gradle" or "DerivedData") continue;
            CopyTree(dir, Path.Combine(to, name));
        }
        foreach (var file in Directory.EnumerateFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
    }
}

public sealed class ScriptedMobileWorker(Func<ProcessSpec, Action<OutputStream, string>, CancellationToken, Task<ProcessResult>> run) : IBuildWorker
{
    public List<ProcessSpec> Calls { get; } = [];
    public IsolationLevel Isolation => IsolationLevel.None;
    public string Description => "test worker";
    public BuildEnvironmentPolicy.ScrubResult CreateEnvironment() => new(new Dictionary<string, string> { ["PATH"] = "/usr/bin", ["HTTPS_PROXY"] = "http://user:hunter2@proxy.example:3128" }, ["KEYSTORE_PASSWORD"]);
    public Task<ProcessResult> RunAsync(ProcessSpec spec, Action<OutputStream, string> onOutput, CancellationToken ct)
    {
        lock (Calls) Calls.Add(spec);
        return run(spec, onOutput, ct);
    }

    public static Task<ProcessResult> Exit(int code) => Task.FromResult(new ProcessResult(code, TimeSpan.FromMilliseconds(3), false, false));
}

public class MobilePlannerTests : IDisposable
{
    private readonly MobileTestKit _k = new();
    public void Dispose() => _k.Dispose();

    private MobilePlanInput Input(MobileAnalysis analysis, MobileBuildOptions options, string source, ToolchainReport report, Dictionary<string, string>? env = null) => new()
    {
        Options = options, Analysis = analysis, SourceDirectory = source, WorkDirectory = _k.Temp.Combine("work"), Toolchains = report,
        Environment = env ?? new Dictionary<string, string> { ["PATH"] = "/usr/bin" }, ToolHomeDirectory = _k.ToolHome,
        Keystore = new DebugKeystore(_k.Temp.Combine("hcg-debug.keystore")),
    };

    private static MobileBuildOptions Android(MobileOutput output, MobileSigningMode signing = MobileSigningMode.ProjectDefault, string? variant = null) =>
        new() { Framework = MobileFramework.NativeAndroid, Output = output, Signing = signing, Variant = variant };

    private async Task<(MobileAnalysis, string, ToolchainReport)> HelloAndroid()
    {
        var report = _k.Report(TargetPlatform.Linux, MobileTestKit.Android);
        var dir = MobileTestKit.Fixture("HelloAndroid");
        return (await MobileTestKit.AnalyzeAsync(dir, report), dir, report);
    }

    [Fact]
    public async Task Gradle_debug_apk_runs_the_wrapper_jar_through_java_without_a_shell()
    {
        var (analysis, dir, report) = await HelloAndroid();
        var plan = GradlePlanner.Plan(Input(analysis, Android(MobileOutput.AndroidDebugApk), dir, report));

        var step = Assert.Single(plan.Steps);
        // The inspected JDK is passed by its real path (macOS temp folders live under the /var → /private/var link).
        Assert.Equal(HCG.ExecutableMaker.Core.PathGuard.GetRealPath(_k.Java), HCG.ExecutableMaker.Core.PathGuard.GetRealPath(step.Process.Executable));
        var args = step.Process.Arguments;
        Assert.Equal(Path.Combine(dir, "gradle", "wrapper", "gradle-wrapper.jar"), args[args.ToList().IndexOf("-classpath") + 1]);
        Assert.Contains("org.gradle.wrapper.GradleWrapperMain", args);
        Assert.Equal(":app:assembleDebug", args[^1]);
        Assert.Contains("--no-daemon", args);
        Assert.DoesNotContain(args, a => a.Contains("injected.signing"));
        Assert.Equal(dir, step.Process.WorkingDirectory);
        var env = step.Process.Environment!;
        Assert.Equal(_k.Sdk, env["ANDROID_HOME"]);
        Assert.Equal(Path.GetDirectoryName(Path.GetDirectoryName(PathGuard.GetRealPath(_k.Java))), env["JAVA_HOME"]); // symlink-free (macOS /var → /private/var)
        Assert.Equal(Path.Combine(_k.ToolHome, "gradle"), env["GRADLE_USER_HOME"]); // never ~/.gradle (credentials)
        Assert.Equal(Path.Combine(_k.ToolHome, "android"), env["ANDROID_USER_HOME"]);
        var rule = Assert.Single(plan.Outputs);
        Assert.Equal(Path.Combine(dir, "app", "build", "outputs", "apk"), rule.SearchRoot);
        Assert.Equal(PackagingFormat.AndroidApk, rule.Format);
    }

    [Fact]
    public async Task Release_with_hcg_debug_key_injects_signing_and_masks_passwords_in_logs()
    {
        var (analysis, dir, report) = await HelloAndroid();
        var plan = GradlePlanner.Plan(Input(analysis, Android(MobileOutput.AndroidReleaseApk, MobileSigningMode.DebugKeystore), dir, report));
        var step = plan.Steps.Single();
        Assert.Contains("-Pandroid.injected.signing.store.password=android", step.Process.Arguments);
        Assert.Contains("-Pandroid.injected.signing.key.alias=androiddebugkey", step.Process.Arguments);
        Assert.Equal(":app:assembleRelease", step.Process.Arguments[^1]);
        Assert.DoesNotContain("=android ", step.DisplayCommand + " ");
        Assert.Contains("password=***", step.DisplayCommand);
        Assert.Contains(plan.Notes, n => n.Contains("debug key"));
    }

    [Fact]
    public async Task Unsigned_release_is_refused_when_the_project_signs_releases_itself()
    {
        var (analysis, dir, report) = await HelloAndroid();
        var signing = analysis with { Android = analysis.Android! with { AppModules = [analysis.Android.AppModules[0] with { HasSigningConfig = true }] } };
        var ex = Assert.Throws<BuildValidationException>(() => GradlePlanner.Plan(Input(signing, Android(MobileOutput.AndroidReleaseApk, MobileSigningMode.Unsigned), dir, report)));
        Assert.Contains("does not edit project files", ex.Message);

        var plain = GradlePlanner.Plan(Input(analysis, Android(MobileOutput.AndroidReleaseApk, MobileSigningMode.Unsigned), dir, report));
        Assert.Contains(plain.Notes, n => n.Contains("UNSIGNED"));
        var projectDefault = GradlePlanner.Plan(Input(signing, Android(MobileOutput.AndroidReleaseApk), dir, report));
        Assert.Contains(projectDefault.Notes, n => n.Contains("~/.gradle/gradle.properties is not read"));
    }

    [Fact]
    public async Task Bundle_variants_are_resolved_and_validated()
    {
        var (analysis, dir, report) = await HelloAndroid();
        var flavored = analysis with { Android = analysis.Android! with { AppModules = [analysis.Android.AppModules[0] with { ProductFlavors = ["free", "pro"] }] } };
        Assert.Contains("several release variants", Assert.Throws<BuildValidationException>(() =>
            GradlePlanner.Plan(Input(flavored, Android(MobileOutput.AndroidAppBundle), dir, report))).Message);

        var plan = GradlePlanner.Plan(Input(flavored, Android(MobileOutput.AndroidAppBundle, variant: "freeRelease"), dir, report));
        Assert.Equal(":app:bundleFreeRelease", plan.Steps.Single().Process.Arguments[^1]);
        Assert.Equal(Path.Combine(dir, "app", "build", "outputs", "bundle", "freeRelease"), plan.Outputs.Single().SearchRoot);

        foreach (var bad in new[] { "release;rm -rf /", "../release", "freeDebug" })
            Assert.Throws<BuildValidationException>(() => GradlePlanner.Plan(Input(flavored, Android(MobileOutput.AndroidAppBundle, variant: bad), dir, report)));
    }

    [Fact]
    public async Task Proxy_settings_are_translated_for_java_without_credentials()
    {
        var (analysis, dir, report) = await HelloAndroid();
        GradlePlanner.Plan(Input(analysis, Android(MobileOutput.AndroidDebugApk), dir, report,
            new Dictionary<string, string> { ["HTTPS_PROXY"] = "http://user:hunter2@proxy.example:3128", ["NO_PROXY"] = "localhost,.internal" }));
        var props = File.ReadAllText(Path.Combine(_k.ToolHome, "gradle", "gradle.properties"));
        Assert.Contains("systemProp.https.proxyHost=proxy.example", props);
        Assert.Contains("systemProp.https.proxyPort=3128", props);
        Assert.Contains("systemProp.https.nonProxyHosts=localhost|*.internal", props);
        Assert.DoesNotContain("hunter2", props);
        Assert.DoesNotContain("user", props.Replace("# Written", ""));
    }

    [Fact]
    public void Missing_wrapper_and_missing_toolchains_are_explained()
    {
        using var t = new TempDir();
        t.Write("settings.gradle", "include ':app'");
        t.Write("app/build.gradle", "plugins { id 'com.android.application' }\nandroid { namespace 'a.b' }");
        var analysis = new MobileAnalysis
        {
            Framework = MobileFramework.NativeAndroid, Platforms = [TargetPlatform.Android],
            Android = new AndroidProjectInfo { Root = "", HasWrapper = false, AppModules = [new AndroidModuleInfo { Directory = "app", GradlePath = ":app", BuildFile = "app/build.gradle" }] },
        };
        Assert.Contains("Gradle wrapper", Assert.Throws<BuildValidationException>(() =>
            GradlePlanner.Plan(Input(analysis, Android(MobileOutput.AndroidDebugApk), t.Path, _k.Report(TargetPlatform.Linux, MobileTestKit.Android)))).Message);
    }

    [Fact]
    public void Wrapper_assessment_flags_insecure_or_unpinned_distributions()
    {
        var official = GradlePlanner.ReadWrapper(MobileTestKit.Fixture("HelloAndroid"))!;
        Assert.Equal("https://services.gradle.org/distributions/gradle-8.14.3-bin.zip", official.DistributionUrl);
        Assert.Equal(64, official.JarSha256.Length);
        Assert.Contains(GradlePlanner.AssessWrapper(official), w => w.Contains("distributionSha256Sum"));
        Assert.Contains(GradlePlanner.AssessWrapper(official with { DistributionUrl = "http://services.gradle.org/x.zip" }), w => w.Contains("insecure"));
        Assert.Contains(GradlePlanner.AssessWrapper(official with { DistributionUrl = "https://evil.example/gradle.zip" }), w => w.Contains("evil.example"));
        Assert.Empty(GradlePlanner.AssessWrapper(official with { DistributionSha256 = new string('a', 64) }));
    }

    // ───────────── Xcode ─────────────

    private async Task<(MobileAnalysis, string, ToolchainReport)> HelloIos(TargetPlatform host = TargetPlatform.MacOS)
    {
        var report = _k.Report(host, MobileTestKit.Apple);
        var dir = MobileTestKit.Fixture("HelloIOS");
        return (await MobileTestKit.AnalyzeAsync(dir, report), dir, report);
    }

    private static MobileBuildOptions Ios(MobileOutput output, MobileSigningMode signing = MobileSigningMode.ProjectDefault, string? team = null) =>
        new() { Framework = MobileFramework.NativeIos, Output = output, Signing = signing, DevelopmentTeam = team, ExportMethod = IosExportMethod.AppStoreConnect };

    [Fact]
    public async Task Xcode_refuses_non_mac_hosts()
    {
        var (analysis, dir, report) = await HelloIos(TargetPlatform.Linux);
        Assert.Contains("require a Mac", Assert.Throws<BuildValidationException>(() => XcodePlanner.Plan(Input(analysis, Ios(MobileOutput.IosSimulatorApp), dir, report))).Message);
    }

    [Fact]
    public async Task Xcode_simulator_build_targets_the_generic_simulator_destination()
    {
        var (analysis, dir, report) = await HelloIos();
        var plan = XcodePlanner.Plan(Input(analysis, Ios(MobileOutput.IosSimulatorApp), dir, report));
        var a = plan.Steps.Single().Process.Arguments.ToList();
        Assert.Equal("/usr/bin/xcodebuild", plan.Steps.Single().Process.Executable);
        Assert.Equal(Path.Combine(dir, "HelloIOS.xcodeproj"), a[a.IndexOf("-project") + 1]);
        Assert.Equal("HelloIOS", a[a.IndexOf("-scheme") + 1]);
        Assert.Equal("iphonesimulator", a[a.IndexOf("-sdk") + 1]);
        Assert.Equal("generic/platform=iOS Simulator", a[a.IndexOf("-destination") + 1]);
        Assert.Equal("Debug", a[a.IndexOf("-configuration") + 1]);
        Assert.Equal("build", a[^1]);
        Assert.EndsWith(Path.Combine("Build", "Products", "Debug-iphonesimulator"), plan.Outputs.Single().SearchRoot);
        Assert.Contains(plan.Notes, n => n.Contains("not distributable"));
    }

    [Fact]
    public async Task Xcode_archive_signing_modes_map_to_explicit_build_settings()
    {
        var (analysis, dir, report) = await HelloIos();
        var unsigned = XcodePlanner.Plan(Input(analysis, Ios(MobileOutput.IosArchive, MobileSigningMode.Unsigned), dir, report)).Steps.Single().Process.Arguments;
        Assert.Contains("CODE_SIGNING_ALLOWED=NO", unsigned);
        Assert.Equal("archive", unsigned[^1]);
        Assert.Contains("generic/platform=iOS", unsigned);

        Assert.Throws<BuildValidationException>(() => XcodePlanner.Plan(Input(analysis, Ios(MobileOutput.IosArchive, MobileSigningMode.AppleAutomatic), dir, report)));
        Assert.Throws<BuildValidationException>(() => XcodePlanner.Plan(Input(analysis, Ios(MobileOutput.IosArchive, MobileSigningMode.AppleAutomatic, "ABCDE12345 OTHER_FLAG=1"), dir, report)));
        var auto = XcodePlanner.Plan(Input(analysis, Ios(MobileOutput.IosArchive, MobileSigningMode.AppleAutomatic, "ABCDE12345"), dir, report)).Steps.Single().Process.Arguments;
        Assert.Contains("DEVELOPMENT_TEAM=ABCDE12345", auto);
        Assert.Contains("CODE_SIGN_STYLE=Automatic", auto);
        Assert.DoesNotContain("-allowProvisioningUpdates", auto); // only when explicitly allowed
        var allowed = XcodePlanner.Plan(Input(analysis, Ios(MobileOutput.IosArchive, MobileSigningMode.AppleAutomatic, "ABCDE12345") with { AllowProvisioningUpdates = true }, dir, report));
        Assert.Contains("-allowProvisioningUpdates", allowed.Steps.Single().Process.Arguments);
        Assert.Contains(allowed.Notes, n => n.Contains("contact Apple"));

        Assert.Throws<BuildValidationException>(() => XcodePlanner.Plan(Input(analysis, Ios(MobileOutput.IosArchive) with { Scheme = "HelloIOS; rm -rf ~" }, dir, report)));
    }

    [Fact]
    public void Export_options_always_export_locally_and_escape_values()
    {
        var o = Ios(MobileOutput.IosIpa, MobileSigningMode.AppleManual, "ABCDE12345") with { ProvisioningProfile = "HCG Store & Co", ExportMethod = IosExportMethod.ReleaseTesting };
        var xml = XcodePlanner.ExportOptions(o, IosExportMethod.ReleaseTesting, "com.hakmicapital.helloios");
        var plist = HCG.ExecutableMaker.Packaging.Mobile.PlistReader.ParseDictionary(System.Text.Encoding.UTF8.GetBytes(xml));
        Assert.Equal("export", plist["destination"]);
        Assert.Equal("release-testing", plist["method"]);
        Assert.Equal("manual", plist["signingStyle"]);
        Assert.Equal("ABCDE12345", plist["teamID"]);
        Assert.Equal("HCG Store & Co", ((Dictionary<string, object>)plist["provisioningProfiles"])["com.hakmicapital.helloios"]);
        Assert.Equal(false, plist["uploadSymbols"]);
        Assert.DoesNotContain("upload</string>", xml);
    }

    // ───────────── bundletool / Flutter / MAUI ─────────────

    [Fact]
    public async Task Bundletool_universal_mode_signs_with_the_explicit_hcg_debug_key()
    {
        var (analysis, dir, report) = await HelloAndroid();
        var step = BundletoolPlanner.BuildApks(Input(analysis, Android(MobileOutput.AndroidAabToApk), dir, report), "/in/app.aab", "/out/app.apks");
        var a = step.Process.Arguments;
        Assert.Equal(["-jar", _k.Temp.Combine("bundletool-all-1.18.1.jar"), "build-apks", "--bundle=/in/app.aab", "--output=/out/app.apks", "--overwrite", "--mode=universal"], a.Take(7));
        Assert.Contains("--ks-key-alias=androiddebugkey", a);
        Assert.Contains("--ks-pass=pass:android", a);
        Assert.DoesNotContain("pass:android", step.DisplayCommand);
    }

    [Fact]
    public async Task Bundletool_device_targeting_requires_a_small_json_spec()
    {
        var (analysis, dir, report) = await HelloAndroid();
        MobileBuildOptions Device(string? spec) => Android(MobileOutput.AndroidAabToApk) with { ApkSetMode = ApkSetMode.DeviceTargeted, DeviceSpecPath = spec };
        Assert.Throws<BuildValidationException>(() => BundletoolPlanner.BuildApks(Input(analysis, Device(null), dir, report), "a.aab", "a.apks"));
        Assert.Throws<BuildValidationException>(() => BundletoolPlanner.BuildApks(Input(analysis, Device(_k.Temp.Write("spec.txt", "{}")), dir, report), "a.aab", "a.apks"));
        Assert.Throws<BuildValidationException>(() => BundletoolPlanner.BuildApks(Input(analysis, Device(_k.Temp.Write("big.json", new string(' ', 70_000))), dir, report), "a.aab", "a.apks"));
        var ok = BundletoolPlanner.BuildApks(Input(analysis, Device(_k.Temp.Write("pixel.json", "{\"supportedAbis\":[\"arm64-v8a\"],\"sdkVersion\":34}")), dir, report), "a.aab", "a.apks");
        Assert.Contains("--device-spec=" + _k.Temp.Combine("pixel.json"), ok.Process.Arguments);
        Assert.DoesNotContain("--mode=universal", ok.Process.Arguments);
    }

    [Fact]
    public void Flutter_plans_cli_builds_and_refuses_windows_hosts()
    {
        var analysis = new MobileAnalysis
        {
            Framework = MobileFramework.Flutter, Platforms = [TargetPlatform.Android, TargetPlatform.IOS],
            Flutter = new FlutterProjectInfo { Root = "", Name = "hello", HasAndroid = true, HasIos = true },
        };
        var opts = new MobileBuildOptions { Framework = MobileFramework.Flutter, Output = MobileOutput.AndroidDebugApk };
        var plan = FlutterPlanner.Plan(Input(analysis, opts, _k.Temp.Path, _k.Report(TargetPlatform.Linux, [.. MobileTestKit.Android, ToolchainIds.Flutter])));
        Assert.Equal(["build", "apk", "--debug", "--suppress-analytics"], plan.Steps.Single().Process.Arguments);
        Assert.Equal("app-debug.apk", plan.Outputs.Single().Pattern);
        Assert.Throws<BuildValidationException>(() => FlutterPlanner.Plan(Input(analysis, opts, _k.Temp.Path, _k.Report(TargetPlatform.Windows, ToolchainIds.Flutter))));
        var ipa = FlutterPlanner.Plan(Input(analysis, opts with { Output = MobileOutput.IosIpa, ExportMethod = IosExportMethod.ReleaseTesting }, _k.Temp.Path, _k.Report(TargetPlatform.MacOS, ToolchainIds.Flutter)));
        Assert.Contains("ad-hoc", ipa.Steps.Single().Process.Arguments);
        Assert.Throws<BuildValidationException>(() => FlutterPlanner.Plan(Input(analysis, opts with { Output = MobileOutput.IosIpa, Signing = MobileSigningMode.Unsigned }, _k.Temp.Path, _k.Report(TargetPlatform.MacOS, ToolchainIds.Flutter))));
    }

    [Fact]
    public void Maui_plans_target_framework_builds()
    {
        _k.Temp.Write("Hello/Hello.csproj", "<Project/>");
        var analysis = new MobileAnalysis
        {
            Framework = MobileFramework.DotNetMaui, Platforms = [TargetPlatform.Android, TargetPlatform.IOS],
            Maui = new MauiProjectInfo { ProjectPath = "Hello/Hello.csproj", TargetFrameworks = ["net10.0-android", "net10.0-ios"] },
        };
        var opts = new MobileBuildOptions { Framework = MobileFramework.DotNetMaui, Output = MobileOutput.AndroidAppBundle };
        var plan = MauiPlanner.Plan(Input(analysis, opts, _k.Temp.Path, _k.Report(TargetPlatform.Linux, [.. MobileTestKit.Android, ToolchainIds.DotNetSdk])));
        var a = plan.Steps.Single().Process.Arguments.ToList();
        Assert.Equal("publish", a[0]);
        Assert.Equal("net10.0-android", a[a.IndexOf("-f") + 1]);
        Assert.Contains("-p:AndroidPackageFormats=aab", a);
        Assert.Contains("-p:AndroidSdkDirectory=" + _k.Sdk, a);
        Assert.Equal("*-Signed.aab", plan.Outputs.Single().Pattern);
        Assert.Throws<BuildValidationException>(() => MauiPlanner.Plan(Input(analysis, opts with { Output = MobileOutput.IosArchive }, _k.Temp.Path, _k.Report(TargetPlatform.MacOS, ToolchainIds.DotNetSdk))));
    }

    [Theory]
    [InlineData("-Pandroid.injected.signing.store.password=s3cret", "-Pandroid.injected.signing.store.password=***")]
    [InlineData("--ks-pass=pass:s3cret", "--ks-pass=***")]
    [InlineData("-storepass", "-storepass")]
    [InlineData("assembleRelease", "assembleRelease")]
    public void Password_arguments_are_masked_for_display(string arg, string shown) =>
        Assert.Equal(shown, MobileArgs.Mask(new ProcessSpec { Executable = "x", Arguments = [arg] }).Arguments.Single());

    [Theory]
    [InlineData("/src/app/Main.java:12: error: ';' expected", DiagnosticSeverity.Error, "/src/app/Main.java", 12)]
    [InlineData("/src/App.swift:3:5: warning: variable 'x' was never used", DiagnosticSeverity.Warning, "/src/App.swift", 3)]
    [InlineData("e: file:///src/Main.kt:3:5 Unresolved reference: foo", DiagnosticSeverity.Error, null, null)]
    [InlineData("error: No profiles for 'com.hakmicapital.helloios' were found", DiagnosticSeverity.Error, null, null)]
    [InlineData("FAILURE: Build failed with an exception.", DiagnosticSeverity.Error, null, null)]
    [InlineData("** ARCHIVE FAILED **", DiagnosticSeverity.Error, null, null)]
    public void Mobile_output_parser_recognizes_toolchain_diagnostics(string line, DiagnosticSeverity severity, string? file, int? lineNo)
    {
        var m = new MobileOutputParser().Parse(line);
        Assert.NotNull(m);
        Assert.Equal(severity, m!.Severity);
        Assert.Equal(file, m.File);
        Assert.Equal(lineNo, m.Line);
    }

    [Fact]
    public void Mobile_output_parser_ignores_progress_and_deduplicates()
    {
        var p = new MobileOutputParser();
        Assert.Null(p.Parse("> Task :app:compileDebugJavaWithJavac"));
        Assert.Null(p.Parse("BUILD SUCCESSFUL in 41s"));
        Assert.NotNull(p.Parse("error: something"));
        Assert.Null(p.Parse("error: something"));
    }
}

/// <summary>MobileBuildService with a scripted worker: success, failures, cancellation, timeouts, signing refusal, history.</summary>
public class MobileBuildServiceTests : IDisposable
{
    private readonly MobileTestKit _k = new();
    private readonly AppPaths _paths;
    private readonly SqliteBuildRepository _builds;

    public MobileBuildServiceTests()
    {
        _paths = new AppPaths(_k.Temp.Combine("app"));
        _paths.EnsureCreated();
        _builds = new SqliteBuildRepository(new SqliteDatabase(_paths));
    }

    public void Dispose() => _k.Dispose();

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

    private MobileBuildService Service(IBuildWorker worker, ToolchainReport report) =>
        new(worker, new FixedToolchains(report), _builds, new MobileArtifactVerifier(new HCG.ExecutableMaker.Infrastructure.Processes.ProcessRunner()), _paths, TimeProvider.System);

    private async Task<ProjectRecord> Project(string fixture, ToolchainReport report)
    {
        var ws = Path.Combine(_paths.WorkspacesDirectory, Guid.NewGuid().ToString("N"));
        MobileTestKit.CopyTree(MobileTestKit.Fixture(fixture), Path.Combine(ws, "source"));
        var p = new ProjectRecord { Id = Guid.NewGuid(), Name = fixture, SourceKind = ProjectSourceKind.Directory, SourceLocation = "/orig", WorkspacePath = ws, ImportedAt = DateTimeOffset.UtcNow };
        p.Analysis = await MobileInspectionTests.Service().InspectAsync(p.SourceDirectory, report, CancellationToken.None);
        await new SqliteProjectRepository(new SqliteDatabase(_paths)).UpsertAsync(p);
        return p;
    }

    private MobileBuildRequest Request(ProjectRecord p, MobileBuildOptions o, TimeSpan? timeout = null) => new()
    {
        Project = p, TrustAcknowledged = true,
        Settings = new BuildSettings
        {
            ProjectRelativePath = "", Platform = o.Output.Platform(), Architecture = TargetArchitecture.Universal,
            OutputDirectory = _k.Temp.Combine("out"), Timeout = timeout ?? TimeSpan.FromMinutes(5),
            Identity = new AppIdentity { ApplicationName = "Hello HCG", Version = "1.2.0", ApplicationId = "com.hakmicapital.hello" },
            Mobile = o,
        },
    };

    private static bool IsGradle(ProcessSpec s) => s.Arguments.Contains("org.gradle.wrapper.GradleWrapperMain");

    [Fact]
    public async Task Untrusted_builds_are_refused()
    {
        var report = _k.Report(TargetPlatform.Linux, MobileTestKit.Android);
        var p = await Project("HelloAndroid", report);
        var worker = new ScriptedMobileWorker((_, _, _) => ScriptedMobileWorker.Exit(0));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(worker, report).RunAsync(Request(p, new MobileBuildOptions { Framework = MobileFramework.NativeAndroid, Output = MobileOutput.AndroidDebugApk }) with { TrustAcknowledged = false }, new Observer(), CancellationToken.None));
        Assert.Empty(worker.Calls);
    }

    [Fact]
    public async Task Android_debug_build_collects_verifies_checksums_and_records_history()
    {
        var report = _k.Report(TargetPlatform.Linux, MobileTestKit.Android);
        var p = await Project("HelloAndroid", report);
        var apkDir = Path.Combine(p.SourceDirectory, "app", "build", "outputs", "apk", "debug");
        Directory.CreateDirectory(apkDir);
        // A stale APK shipped inside the imported project must never be reported as this build's output.
        var stale = MobileSamples.Apk(Path.Combine(apkDir, "stale.apk"), debuggable: true, signer: null);
        File.SetLastWriteTimeUtc(stale, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var worker = new ScriptedMobileWorker((spec, output, _) =>
        {
            output(OutputStream.StdOut, "> Task :app:assembleDebug");
            output(OutputStream.StdOut, "w: some deprecation");
            MobileSamples.Apk(Path.Combine(apkDir, "app-debug.apk"), debuggable: true, signer: MobileSamples.Certificate("CN=Android Debug, O=Android, C=US"));
            return ScriptedMobileWorker.Exit(0);
        });
        var observer = new Observer();
        var build = await Service(worker, report).RunAsync(Request(p, new MobileBuildOptions { Framework = MobileFramework.NativeAndroid, Output = MobileOutput.AndroidDebugApk }), observer, CancellationToken.None);

        Assert.True(build.Status == BuildStatus.Succeeded, build.FailureReason + "\n" + observer.Text);
        var artifact = Assert.Single(build.Artifacts);
        Assert.Equal("app-debug.apk", artifact.FileName);
        Assert.Equal(PackagingFormat.AndroidApk, artifact.Format);
        Assert.Equal(VerificationStatus.PassedWithWarnings, artifact.Verification);
        Assert.Contains(artifact.Checks, c => c.Name == "Metadata" && c.Detail.Contains("com.hakmicapital.hello") && c.Detail.Contains("DebugSigned"));
        Assert.Equal(Path.Combine(_k.Temp.Combine("out"), "Hello HCG-1.2.0-android-debug", "app-debug.apk"), artifact.Path);
        Assert.Contains(artifact.Sha256, File.ReadAllText(Path.Combine(Path.GetDirectoryName(artifact.Path)!, "SHA256SUMS.txt")));
        Assert.Equal(2, build.WarningCount); // the Kotlin "w:" line + HCG-GRADLE: the fixture's wrapper pins no distributionSha256Sum
        Assert.Contains(build.Messages, m => m.Code == "HCG-GRADLE");
        Assert.Contains("KEYSTORE_PASSWORD", observer.Text); // reported as removed, never its value
        Assert.Contains("wrapper jar SHA-256", observer.Text);

        var saved = await _builds.GetAsync(build.Id);
        Assert.Equal(BuildStatus.Succeeded, saved!.Status);
        Assert.Equal(MobileOutput.AndroidDebugApk, saved.Settings.Mobile!.Output);
        Assert.Equal(MobileFramework.NativeAndroid, saved.Settings.Mobile.Framework);
        Assert.Single(saved.Artifacts);
        Assert.Equal("21.0.2", saved.ToolchainVersions[ToolchainIds.Jdk]);
    }

    [Fact]
    public async Task Gradle_failure_fails_the_build_and_leaves_no_output()
    {
        var report = _k.Report(TargetPlatform.Linux, MobileTestKit.Android);
        var p = await Project("HelloAndroid", report);
        var worker = new ScriptedMobileWorker((_, output, _) =>
        {
            output(OutputStream.StdErr, "/src/app/MainActivity.java:9: error: cannot find symbol");
            output(OutputStream.StdErr, "FAILURE: Build failed with an exception.");
            return ScriptedMobileWorker.Exit(1);
        });
        var build = await Service(worker, report).RunAsync(Request(p, new MobileBuildOptions { Framework = MobileFramework.NativeAndroid, Output = MobileOutput.AndroidDebugApk }), new Observer(), CancellationToken.None);
        Assert.Equal(BuildStatus.Failed, build.Status);
        Assert.Contains("exit code 1", build.FailureReason);
        Assert.Equal(2, build.ErrorCount);
        Assert.Empty(build.Artifacts);
        Assert.False(Directory.Exists(_k.Temp.Combine("out")) && Directory.EnumerateFileSystemEntries(_k.Temp.Combine("out")).Any());
    }

    [Fact]
    public async Task Success_without_a_product_is_not_reported_as_success()
    {
        var report = _k.Report(TargetPlatform.Linux, MobileTestKit.Android);
        var p = await Project("HelloAndroid", report);
        var build = await Service(new ScriptedMobileWorker((_, _, _) => ScriptedMobileWorker.Exit(0)), report)
            .RunAsync(Request(p, new MobileBuildOptions { Framework = MobileFramework.NativeAndroid, Output = MobileOutput.AndroidDebugApk }), new Observer(), CancellationToken.None);
        Assert.Equal(BuildStatus.Failed, build.Status);
        Assert.Contains("no new artifact", build.FailureReason);
    }

    [Fact]
    public async Task Missing_sdk_is_reported_before_anything_runs()
    {
        var report = _k.Report(TargetPlatform.Linux, ToolchainIds.Jdk);
        var p = await Project("HelloAndroid", report);
        var worker = new ScriptedMobileWorker((_, _, _) => ScriptedMobileWorker.Exit(0));
        var build = await Service(worker, report).RunAsync(Request(p, new MobileBuildOptions { Framework = MobileFramework.NativeAndroid, Output = MobileOutput.AndroidDebugApk }), new Observer(), CancellationToken.None);
        Assert.Equal(BuildStatus.Failed, build.Status);
        Assert.Contains("Android SDK", build.FailureReason);
        Assert.Empty(worker.Calls);
    }

    [Fact]
    public async Task Cancellation_and_timeout_terminate_the_build()
    {
        var report = _k.Report(TargetPlatform.Linux, MobileTestKit.Android);
        var p = await Project("HelloAndroid", report);
        var options = new MobileBuildOptions { Framework = MobileFramework.NativeAndroid, Output = MobileOutput.AndroidDebugApk };
        async Task<ProcessResult> Hang(ProcessSpec s, Action<OutputStream, string> o, CancellationToken ct)
        {
            try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
            return new ProcessResult(-1, TimeSpan.Zero, Cancelled: true, TimedOut: false);
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var cancelled = await Service(new ScriptedMobileWorker(Hang), report).RunAsync(Request(p, options), new Observer(), cts.Token);
        Assert.Equal(BuildStatus.Cancelled, cancelled.Status);

        var timedOut = await Service(new ScriptedMobileWorker(Hang), report).RunAsync(Request(p, options, TimeSpan.FromMilliseconds(300)), new Observer(), CancellationToken.None);
        Assert.Equal(BuildStatus.TimedOut, timedOut.Status);
    }

    [Fact]
    public async Task Ios_on_linux_is_unavailable_with_a_precise_reason()
    {
        var report = _k.Report(TargetPlatform.Linux, MobileTestKit.Apple);
        var p = await Project("HelloIOS", report);
        var worker = new ScriptedMobileWorker((_, _, _) => ScriptedMobileWorker.Exit(0));
        var build = await Service(worker, report).RunAsync(Request(p, new MobileBuildOptions { Framework = MobileFramework.NativeIos, Output = MobileOutput.IosSimulatorApp }), new Observer(), CancellationToken.None);
        Assert.Equal(BuildStatus.Failed, build.Status);
        Assert.Contains("require a Mac", build.FailureReason);
        Assert.Empty(worker.Calls);
    }

    [Fact]
    public async Task Ipa_export_is_refused_for_an_unsigned_archive()
    {
        var report = _k.Report(TargetPlatform.MacOS, MobileTestKit.Apple);
        var p = await Project("HelloIOS", report);
        var worker = new ScriptedMobileWorker((spec, _, _) =>
        {
            var archive = spec.Arguments[spec.Arguments.ToList().IndexOf("-archivePath") + 1];
            WriteArchive(archive, signedProfile: null);
            return ScriptedMobileWorker.Exit(0);
        });
        var build = await Service(worker, report).RunAsync(Request(p, new MobileBuildOptions
        {
            Framework = MobileFramework.NativeIos, Output = MobileOutput.IosIpa, ExportMethod = IosExportMethod.AppStoreConnect,
        }), new Observer(), CancellationToken.None);

        Assert.Equal(BuildStatus.Failed, build.Status);
        Assert.Contains("not signed with an Apple development or distribution certificate", build.FailureReason);
        Assert.Single(worker.Calls); // archive only; export never attempted
    }

    [Fact]
    public async Task Signed_archive_is_exported_locally_and_export_options_are_cleaned_up()
    {
        var report = _k.Report(TargetPlatform.MacOS, MobileTestKit.Apple);
        var p = await Project("HelloIOS", report);
        string? exportOptions = null;
        var worker = new ScriptedMobileWorker((spec, _, _) =>
        {
            var a = spec.Arguments.ToList();
            if (a.Contains("archive"))
                WriteArchive(a[a.IndexOf("-archivePath") + 1], MobileSamples.Profile(getTaskAllow: false, devices: false, expires: DateTime.UtcNow.AddYears(1)));
            else
            {
                exportOptions = File.ReadAllText(a[a.IndexOf("-exportOptionsPlist") + 1]);
                var dir = a[a.IndexOf("-exportPath") + 1];
                Directory.CreateDirectory(dir);
                MobileSamples.Ipa(Path.Combine(dir, "HelloIOS.ipa"), "iphoneos", MobileSamples.MachO(0x0100000C), signed: true,
                    profile: MobileSamples.Profile(getTaskAllow: false, devices: false, expires: DateTime.UtcNow.AddYears(1)));
            }
            return ScriptedMobileWorker.Exit(0);
        });
        var observer = new Observer();
        var build = await Service(worker, report).RunAsync(Request(p, new MobileBuildOptions
        {
            Framework = MobileFramework.NativeIos, Output = MobileOutput.IosIpa, ExportMethod = IosExportMethod.AppStoreConnect,
        }), observer, CancellationToken.None);

        Assert.True(build.Status == BuildStatus.Succeeded, build.FailureReason + "\n" + observer.Text);
        Assert.Equal(2, worker.Calls.Count);
        Assert.Contains("<key>destination</key><string>export</string>", exportOptions);
        Assert.Contains("app-store-connect", exportOptions);
        var ipa = Assert.Single(build.Artifacts);
        Assert.Equal(PackagingFormat.IosIpa, ipa.Format);
        // Artifact checks pass; installation and runtime are reported as not tested (HCG does not install or launch the app).
        Assert.Equal(VerificationStatus.PassedWithWarnings, ipa.Verification);
        Assert.DoesNotContain(ipa.Checks, c => c.Status == VerificationStatus.Failed);
        Assert.Equal(VerificationStatus.NotVerifiable, Assert.Single(ipa.Checks, c => c.Name == "Installation and runtime").Status);
        Assert.NotNull(build.Provenance);
        Assert.Equal([ipa.Path], build.Provenance!.ArtifactPaths);
        Assert.False(File.Exists(Path.Combine(_paths.BuildsDirectory, build.Id.ToString("N"), "work", "ExportOptions.plist")));
        Assert.Contains("HCG never uploads", observer.Text);
    }

    [Fact]
    public async Task Aab_artifact_is_converted_to_a_universal_apk_with_the_hcg_debug_key()
    {
        var report = _k.Report(TargetPlatform.Linux, MobileTestKit.Android);
        File.WriteAllText(report.Find(ToolchainIds.Bundletool)!.ExecutablePath!, "jar");
        var ws = Path.Combine(_paths.WorkspacesDirectory, "artifact");
        Directory.CreateDirectory(Path.Combine(ws, "source"));
        MobileSamples.Aab(Path.Combine(ws, "source", "app-release.aab"), signer: null);
        var p = new ProjectRecord { Id = Guid.NewGuid(), Name = "app-release.aab", SourceKind = ProjectSourceKind.Artifact, SourceLocation = "/orig/app-release.aab", WorkspacePath = ws, ImportedAt = DateTimeOffset.UtcNow };
        await new SqliteProjectRepository(new SqliteDatabase(_paths)).UpsertAsync(p);

        var worker = new ScriptedMobileWorker((spec, _, _) =>
        {
            var a = spec.Arguments.ToList();
            if (a.Contains("-genkeypair")) File.WriteAllText(a[a.IndexOf("-keystore") + 1], "keystore");
            else if (a.Contains("build-apks"))
            {
                var output = a.Single(x => x.StartsWith("--output=", StringComparison.Ordinal))["--output=".Length..];
                var universal = MobileSamples.Apk(_k.Temp.Combine("u.apk"), debuggable: false, signer: MobileSamples.Certificate("CN=Android Debug,O=Android,C=US"));
                using var zip = ZipFile.Open(output, ZipArchiveMode.Create);
                zip.CreateEntryFromFile(universal, "universal.apk");
                zip.CreateEntry("toc.pb");
            }
            return ScriptedMobileWorker.Exit(0);
        });
        var observer = new Observer();
        var build = await Service(worker, report).RunAsync(Request(p, new MobileBuildOptions
        {
            Framework = MobileFramework.NativeAndroid, Output = MobileOutput.AndroidAabToApk, InputArtifact = "app-release.aab",
        }), observer, CancellationToken.None);

        Assert.True(build.Status == BuildStatus.Succeeded, build.FailureReason + "\n" + observer.Text);
        Assert.Equal([PackagingFormat.AndroidApkSet, PackagingFormat.AndroidApk], build.Artifacts.Select(x => x.Format));
        Assert.Equal("app-release-universal.apk", build.Artifacts[1].FileName);
        Assert.Contains(build.Artifacts[1].Checks, c => c.Name == "Metadata" && c.Detail.Contains("DebugSigned"));
        Assert.True(File.Exists(Path.Combine(_paths.Root, "signing", "hcg-debug.keystore")));
        Assert.DoesNotContain("pass:android", observer.Text);
        Assert.Contains("not a store-signed release", observer.Text);
    }

    private static void WriteArchive(string archive, byte[]? signedProfile)
    {
        var app = MobileSamples.AppBundle(Path.Combine(archive, "Products", "Applications", "HelloIOS.app"), "iphoneos", MobileSamples.MachO(0x0100000C), signed: signedProfile is not null);
        if (signedProfile is not null) File.WriteAllBytes(Path.Combine(app, "embedded.mobileprovision"), signedProfile);
        File.WriteAllText(Path.Combine(archive, "Info.plist"), MobileSamples.XmlPlist(new()
        {
            ["ApplicationProperties"] = new Dictionary<string, object>
            {
                ["ApplicationPath"] = "Applications/HelloIOS.app", ["CFBundleIdentifier"] = "com.hakmicapital.helloios",
                ["SigningIdentity"] = signedProfile is null ? "" : "Apple Distribution: Hakmi Capital Group (ABCDE12345)",
            },
        }));
    }
}

public class MobileArtifactImportTests : IDisposable
{
    private readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    private HCG.ExecutableMaker.Infrastructure.Import.ProjectImporter Importer()
    {
        var paths = new AppPaths(_t.Combine("app"));
        paths.EnsureCreated();
        return new(paths, TimeProvider.System);
    }

    [Fact]
    public async Task Artifacts_are_copied_unextracted_into_an_artifact_workspace()
    {
        var aab = MobileSamples.Aab(_t.Combine("My App.aab"), signer: null);
        var result = await Importer().ImportArtifactAsync(aab, new ImportLimits(), null, CancellationToken.None);
        Assert.Equal(ProjectSourceKind.Artifact, result.Project.SourceKind);
        var copied = Assert.Single(Directory.GetFiles(result.Project.SourceDirectory));
        Assert.Equal(File.ReadAllBytes(aab), File.ReadAllBytes(copied));

        var analysis = await MobileInspectionTests.Service().InspectAsync(result.Project.SourceDirectory, null, CancellationToken.None);
        Assert.Equal("mobile-artifact", analysis.InspectorId);
        Assert.Equal(Path.GetFileName(copied), analysis.Mobile!.ArtifactPath);
        Assert.Equal([TargetPlatform.Android], analysis.Mobile.Platforms);
        var option = Assert.Single(HCG.ExecutableMaker.ProjectInspection.Mobile.MobileCapabilities.Evaluate(analysis.Mobile, null));
        Assert.Equal(MobileOutput.AndroidAabToApk, option.Output);
        Assert.False(option.IsAvailable);
    }

    [Fact]
    public async Task Apk_artifacts_offer_no_conversion_outputs()
    {
        var apk = MobileSamples.Apk(_t.Combine("app.apk"), debuggable: true, signer: null);
        var result = await Importer().ImportArtifactAsync(apk, new ImportLimits(), null, CancellationToken.None);
        var analysis = await MobileInspectionTests.Service().InspectAsync(result.Project.SourceDirectory, null, CancellationToken.None);
        Assert.Empty(HCG.ExecutableMaker.ProjectInspection.Mobile.MobileCapabilities.Evaluate(analysis.Mobile!, null)); // APK → AAB is not offered
        Assert.Contains("cannot be turned into an App Bundle", analysis.SupportSummary);
    }

    [Fact]
    public async Task Renamed_files_and_other_types_are_rejected()
    {
        var fake = _t.Write("not-really.apk", "MZ this is a renamed program");
        var ex = await Assert.ThrowsAsync<ImportRejectedException>(() => Importer().ImportArtifactAsync(fake, new ImportLimits(), null, CancellationToken.None));
        Assert.Contains("renaming a file does not convert it", ex.Message);
        await Assert.ThrowsAsync<ImportRejectedException>(() => Importer().ImportArtifactAsync(_t.Write("app.exe", "MZ"), new ImportLimits(), null, CancellationToken.None));
        var big = MobileSamples.Apk(_t.Combine("big.apk"), debuggable: true, signer: null);
        await Assert.ThrowsAsync<ImportRejectedException>(() => Importer().ImportArtifactAsync(big, new ImportLimits { MaxFileBytes = 100 }, null, CancellationToken.None));
    }
}
