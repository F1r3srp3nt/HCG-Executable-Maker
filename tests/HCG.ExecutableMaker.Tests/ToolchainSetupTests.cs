using HCG.ExecutableMaker.BuildEngine;
using HCG.ExecutableMaker.BuildEngine.Mobile;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Infrastructure.Processes;
using HCG.ExecutableMaker.Infrastructure.Toolchains;
using HCG.ExecutableMaker.ProjectInspection;
using HCG.ExecutableMaker.ProjectInspection.Mobile;
using HCG.ExecutableMaker.ProjectInspection.Swift;
using Xunit;

namespace HCG.ExecutableMaker.Tests;

/// <summary>
/// Toolchain discovery and setup as seen on a real Mac: Command Line Tools selected while Xcode is installed, a macOS
/// java stub without a JDK, paths with spaces, missing SDK components, HCG launched from Finder (no shell variables),
/// per-project readiness, and builds using exactly the toolchain that passed inspection.
/// </summary>
public sealed class ToolchainSetupTests : IDisposable
{
    private readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    private static readonly HostEnvironment Mac = new(TargetPlatform.MacOS, TargetArchitecture.Arm64, "macOS 26.1", ".NET", "m");
    private static readonly HostEnvironment Windows = new(TargetPlatform.Windows, TargetArchitecture.X64, "Windows 11", ".NET", "w");

    // ───────────── Fakes ─────────────

    /// <summary>Answers java probes by the executable's path (no real Java needed).</summary>
    private sealed class JavaRunner(Func<string, (int Exit, string Stdout, string Stderr)> answer) : IProcessRunner
    {
        public List<ProcessSpec> Calls { get; } = [];
        public Task<ProcessResult> RunAsync(ProcessSpec spec, Action<OutputStream, string>? onOutput, CancellationToken cancellationToken)
        {
            Calls.Add(spec);
            var (exit, so, se) = answer(spec.Executable);
            foreach (var l in so.Split('\n')) onOutput?.Invoke(OutputStream.StdOut, l);
            foreach (var l in se.Split('\n')) onOutput?.Invoke(OutputStream.StdErr, l);
            return Task.FromResult(new ProcessResult(exit, TimeSpan.Zero, false, false));
        }
    }

    private static string JavaOutput(string version, string home) =>
        $"Property settings:\n    java.home = {home}\n    java.version = {version}\n    java.version.date = 2025-01-21\n\nopenjdk version \"{version}\" 2025-01-21\nOpenJDK Runtime Environment (build {version}+7)";

    private string FakeJdk(string home, bool javac = true)
    {
        Directory.CreateDirectory(Path.Combine(home, "bin"));
        File.WriteAllText(Path.Combine(home, "bin", "java"), "");
        if (javac) File.WriteAllText(Path.Combine(home, "bin", "javac"), "");
        File.WriteAllText(Path.Combine(home, "bin", "keytool"), "");
        return home;
    }

    // ───────────── 1. Xcode: CLT selected, Xcode installed ─────────────

    private static XcodeStatus CltWithXcodeInstalled() => XcodeDiscovery.Diagnose(new XcodeDiscovery.ProbeResults(
        "/Library/Developer/CommandLineTools", DeveloperDirectorySource.XcodeSelect,
        [new XcodeInstallation("/Applications/Xcode.app", "/Applications/Xcode.app/Contents/Developer", "27.2", "27B52")],
        1, "", "xcode-select: error: tool 'xcodebuild' requires Xcode, but active developer directory '/Library/Developer/CommandLineTools' is a command line tools instance",
        null, []));

    [Fact]
    public void Clt_selected_with_Xcode_installed_is_setup_required_everywhere_never_missing()
    {
        var x = CltWithXcodeInstalled();
        Assert.Equal(XcodeState.InstalledNotSelected, x.State);
        Assert.Equal(ToolchainStatus.SetupRequired, ToolchainDetector.XcodeToolchainInfo(x).Status);
        var ios = ToolchainDetector.IosSdkRow(x);
        Assert.Equal(ToolchainStatus.SetupRequired, ios.Status);
        Assert.Contains("Select Xcode 27.2", ios.InstallHint);

        var report = new ToolchainReport(Mac, [ToolchainDetector.XcodeToolchainInfo(x), ios], null, null, DateTimeOffset.UtcNow) { Xcode = x };
        var r = MobileReadiness.Evaluate(IosApp(), MobileOutput.IosSimulatorApp, null, report);
        var xcode = r.Items.Single(i => i.Tool == "Xcode");
        Assert.Equal(ReadinessState.SetupRequired, xcode.State);
        Assert.Contains("for HCG", xcode.Fix);                       // HCG-only selection offered
        Assert.Contains("sudo xcode-select --switch", xcode.Fix);    // the machine-wide way is only described, never run
        Assert.False(r.CanBuild);
    }

    // ───────────── 2. Java: stubs, shims, JREs ─────────────

    [Fact]
    public async Task macOS_java_stub_without_a_JDK_is_rejected_with_the_reason()
    {
        var home = FakeJdk(_t.Combine("usr"));
        var runner = new JavaRunner(_ => (1, "", "The operation couldn't be completed. Unable to locate a Java Runtime.\nPlease visit http://www.java.com for information on installing Java."));
        var c = await new JavaDiscovery(runner).ProbeAsync(home, "java on PATH", TargetPlatform.MacOS, CancellationToken.None);
        Assert.False(c.IsUsable);
        Assert.Contains("java stub", c.Problem);
        // stdout AND stderr were read, and the shim was told which JAVA_HOME is being tested.
        Assert.Equal(home, runner.Calls.Single().Environment!["JAVA_HOME"]);
        Assert.Equal(["-XshowSettings:properties", "-version"], runner.Calls.Single().Arguments);
    }

    [Theory]
    [InlineData(0, "", "", "reported no version")]
    [InlineData(127, "", "jenv: version `21' is not installed", "failed (exit code 127): jenv: version `21' is not installed")]
    public async Task Broken_shims_are_not_ready(int exit, string stdout, string stderr, string expected)
    {
        var home = FakeJdk(_t.Combine("shim"));
        var c = await new JavaDiscovery(new JavaRunner(_ => (exit, stdout, stderr))).ProbeAsync(home, "SDKMAN", TargetPlatform.MacOS, CancellationToken.None);
        Assert.False(c.IsUsable);
        Assert.Contains(expected, c.Problem);
    }

    [Fact]
    public async Task A_runtime_without_javac_is_not_a_JDK_and_a_version_on_stdout_is_still_read()
    {
        var jre = FakeJdk(_t.Combine("jre"), javac: false);
        var c = await new JavaDiscovery(new JavaRunner(_ => (0, JavaOutput("17.0.12", jre), ""))).ProbeAsync(jre, "JAVA_HOME", TargetPlatform.MacOS, CancellationToken.None);
        Assert.Equal("17.0.12", c.Version);
        Assert.False(c.IsUsable);
        Assert.Contains("runtime only", c.Problem);
    }

    [UnixFact]
    public async Task A_real_shim_script_is_run_and_both_streams_are_captured()
    {
        // A real process (no fake runner): prints the version to stderr like java does, and its java.home to stdout.
        var home = _t.Combine("Real JDK 21", "Contents", "Home");
        FakeJdk(home);
        var java = Path.Combine(home, "bin", "java");
        File.WriteAllText(java, "#!/bin/sh\necho 'openjdk version \"21.0.5\" 2024-10-15' >&2\necho '    java.version = 21.0.5' >&2\necho \"    java.home = $JAVA_HOME\"\n");
        File.SetUnixFileMode(java, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var c = await new JavaDiscovery(new ProcessRunner()).ProbeAsync(home, "Saved in HCG", TargetPlatform.Linux, CancellationToken.None);
        Assert.True(c.IsUsable, c.Problem);
        Assert.Equal(("21.0.5", 21), (c.Version, c.Major));

        File.WriteAllText(java, "#!/bin/sh\necho 'Unable to locate a Java Runtime.' >&2\nexit 1\n");
        var stub = await new JavaDiscovery(new ProcessRunner()).ProbeAsync(home, "java on PATH", TargetPlatform.Linux, CancellationToken.None);
        Assert.Contains("java stub", stub.Problem);
    }

    // ───────────── 3. Paths with spaces; Finder launch (no shell variables) ─────────────

    [UnixFact]
    public async Task Finder_launch_without_JAVA_HOME_or_PATH_finds_Android_Studios_JDK_in_a_path_with_spaces()
    {
        var root = _t.Combine("root");
        var jbr = FakeJdk(Path.Combine(root, "Applications", "Android Studio.app", "Contents", "jbr", "Contents", "Home"));
        var temurin = FakeJdk(Path.Combine(root, "Library", "Java", "JavaVirtualMachines", "temurin 11.jdk", "Contents", "Home"));
        var runner = new JavaRunner(exe => exe.StartsWith(jbr, StringComparison.Ordinal) ? (0, "", JavaOutput("21.0.6", jbr)) : (0, "", JavaOutput("11.0.25", temurin)));
        var ctx = new JavaDiscovery.Context
        {
            Platform = TargetPlatform.MacOS, RootPrefix = root, UserHome = Path.Combine(root, "Users", "me"),
            GetEnv = _ => null,   // Finder: no JAVA_HOME …
            PathJava = null,      // … and only /usr/bin:/bin on PATH
        };
        var result = await new JavaDiscovery(runner).DiscoverAsync(ctx, CancellationToken.None);
        Assert.Equal(jbr, result.Selected!.Home);
        Assert.Equal("Android Studio (bundled JDK)", result.Selected.Source);
        Assert.Contains(result.Candidates, c => c.Home == temurin && c.Major == 11);   // listed, not chosen (17+ preferred)
    }

    [Fact]
    public async Task A_saved_JDK_wins_and_is_never_silently_replaced_when_it_breaks()
    {
        var saved = _t.Combine("My JDKs", "Temurin 21.jdk", "Contents", "Home");   // spaces
        var other = FakeJdk(_t.Combine("other jdk"));
        var runner = new JavaRunner(_ => (0, "", JavaOutput("21.0.6", other)));
        var ctx = new JavaDiscovery.Context { Platform = TargetPlatform.MacOS, SavedHome = saved, GetEnv = n => n == "JAVA_HOME" ? other : null, UserHome = _t.Combine("home") };
        var broken = await new JavaDiscovery(runner).DiscoverAsync(ctx, CancellationToken.None);
        Assert.Equal(saved, broken.Selected!.Home);
        Assert.False(broken.Selected.IsUsable);
        Assert.Equal(ToolchainStatus.InvalidPath, ToolchainDetector.JdkRow(broken).Status);

        FakeJdk(saved);
        var ok = await new JavaDiscovery(runner).DiscoverAsync(ctx, CancellationToken.None);
        Assert.Equal((saved, "Saved in HCG", true), (ok.Selected!.Home, ok.Selected.Source, ok.Selected.IsUsable));
        Assert.Equal(ToolchainStatus.Installed, ToolchainDetector.JdkRow(ok).Status);
    }

    [UnixFact]
    public void Picked_folders_are_normalized_to_a_JDK_home()
    {
        var home = FakeJdk(_t.Combine("Applications", "Android Studio.app", "Contents", "jbr", "Contents", "Home"));
        Assert.Equal(home, ToolPathSelection.NormalizeJavaHome(_t.Combine("Applications", "Android Studio.app")));
        Assert.Equal(home, ToolPathSelection.NormalizeJavaHome(Path.Combine(home, "bin")));
        Assert.Equal(home, ToolPathSelection.NormalizeJavaHome(_t.Combine("Applications", "Android Studio.app", "Contents", "jbr") + "/"));
    }

    [Fact]
    public void java_home_V_output_is_parsed_including_paths_with_spaces()
    {
        const string output = """
            Matching Java Virtual Machines (2):
                21.0.2 (arm64) "Eclipse Adoptium" - "OpenJDK 21.0.2" /Library/Java/JavaVirtualMachines/temurin-21.jdk/Contents/Home
                17.0.9 (arm64) "Oracle Corporation" - "Java SE 17.0.9" /Users/me/Library/Java/JavaVirtualMachines/My JDK 17.jdk/Contents/Home
            /Library/Java/JavaVirtualMachines/temurin-21.jdk/Contents/Home
            """;
        Assert.Equal(["/Library/Java/JavaVirtualMachines/temurin-21.jdk/Contents/Home", "/Users/me/Library/Java/JavaVirtualMachines/My JDK 17.jdk/Contents/Home"],
            JavaDiscovery.ParseJavaHomeList(output));
    }

    // ───────────── 4. Android SDK components ─────────────

    /// <summary>Inspects as on a Mac regardless of the test host (tool file names differ on Windows).</summary>
    private static AndroidSdkDiscoveryResult InspectSdk(string root, string source) =>
        AndroidSdkDiscovery.Inspect(root, source, new AndroidSdkDiscovery.Context { Platform = TargetPlatform.MacOS });

    private string FakeSdk(string root, bool platformJar = true, bool completeBuildTools = true, bool licences = false)
    {
        Directory.CreateDirectory(Path.Combine(root, "platforms", "android-35"));
        if (platformJar) File.WriteAllText(Path.Combine(root, "platforms", "android-35", "android.jar"), "");
        Directory.CreateDirectory(Path.Combine(root, "platforms", "android-34"));   // no android.jar: not installed
        var bt = Path.Combine(root, "build-tools", "35.0.0");
        Directory.CreateDirectory(bt);
        File.WriteAllText(Path.Combine(bt, "apksigner"), "");
        if (completeBuildTools) { File.WriteAllText(Path.Combine(bt, "zipalign"), ""); File.WriteAllText(Path.Combine(bt, "aapt2"), ""); }
        Directory.CreateDirectory(Path.Combine(root, "platform-tools"));
        if (licences) { Directory.CreateDirectory(Path.Combine(root, "licenses")); File.WriteAllText(Path.Combine(root, "licenses", "android-sdk-license"), "x"); }
        return root;
    }

    [Fact]
    public void Platforms_and_build_tools_are_separate_and_tools_come_from_one_complete_build_tools()
    {
        var sdk = InspectSdk(FakeSdk(_t.Combine("Android SDK")), "test");
        Assert.Equal(["35"], sdk.Platforms);                       // android-34 has no android.jar
        Assert.Equal(Path.Combine(sdk.Root!, "build-tools", "35.0.0", "aapt2"), sdk.SelectedBuildTools!.Aapt2);

        var incomplete = InspectSdk(FakeSdk(_t.Combine("partial sdk"), platformJar: false, completeBuildTools: false), "test");
        Assert.Empty(incomplete.Platforms);
        Assert.Null(incomplete.SelectedBuildTools);
        var rows = AndroidSdkDiscovery.Rows(incomplete).ToDictionary(r => r.Id);
        Assert.Equal(ToolchainStatus.Installed, rows[ToolchainIds.AndroidSdk].Status);
        Assert.Equal(ToolchainStatus.SetupRequired, rows[ToolchainIds.AndroidPlatforms].Status);
        Assert.Equal(ToolchainStatus.SetupRequired, rows[ToolchainIds.AndroidBuildTools].Status);
        Assert.Contains("incomplete", rows[ToolchainIds.AndroidBuildTools].Detail);
        Assert.Equal(ToolchainStatus.SetupRequired, rows[ToolchainIds.Aapt2].Status);
        Assert.Contains("licences have not been accepted", rows[ToolchainIds.AndroidSdk].Detail);
    }

    [Fact]
    public void Finder_launch_finds_the_SDK_in_Android_Studios_default_location_and_a_bad_saved_path_is_reported()
    {
        var home = _t.Combine("Users", "me");
        FakeSdk(Path.Combine(home, "Library", "Android", "sdk"));
        var found = AndroidSdkDiscovery.Discover(new AndroidSdkDiscovery.Context { Platform = TargetPlatform.MacOS, UserHome = home, GetEnv = _ => null });
        Assert.Equal((Path.Combine(home, "Library", "Android", "sdk"), "Android Studio default location"), (found.Root, found.Source));

        var bad = AndroidSdkDiscovery.Discover(new AndroidSdkDiscovery.Context { Platform = TargetPlatform.MacOS, UserHome = home, GetEnv = _ => null, SavedRoot = _t.Combine("Documents") });
        Assert.NotNull(bad.Problem);
        Assert.Equal(ToolchainStatus.InvalidPath, AndroidSdkDiscovery.Rows(bad).First().Status);
    }

    [Fact]
    public void Missing_platform_blocks_only_when_Gradle_cannot_download_it()
    {
        var noLicences = AndroidReport(InspectSdk(FakeSdk(_t.Combine("a"), platformJar: false), "test"), Jdk(21));
        var r = MobileReadiness.Evaluate(HelloAndroid(compileSdk: 35), MobileOutput.AndroidDebugApk, null, noLicences);
        Assert.Equal(ReadinessState.SetupRequired, r.Items.Single(i => i.Tool == "SDK Platform 35").State);
        Assert.False(r.CanBuild);

        var withLicences = AndroidReport(InspectSdk(FakeSdk(_t.Combine("b"), platformJar: false, licences: true), "test"), Jdk(21));
        var r2 = MobileReadiness.Evaluate(HelloAndroid(compileSdk: 35), MobileOutput.AndroidDebugApk, null, withLicences);
        Assert.Equal(ReadinessState.NotVerifiable, r2.Items.Single(i => i.Tool == "SDK Platform 35").State);
        Assert.True(r2.CanBuild, r2.Summary);
    }

    // ───────────── 5. Readiness: required vs optional, Gradle/AGP vs JDK, signing destinations ─────────────

    private static JavaDiscoveryResult Jdk(int major, string home = "/jdk") =>
        new(new JdkCandidate(home, "JAVA_HOME", $"{major}.0.1", major, true, null), [], null);

    private ToolchainReport AndroidReport(AndroidSdkDiscoveryResult? sdk, JavaDiscoveryResult? java, params ToolchainInfo[] extra)
    {
        var rows = new List<ToolchainInfo>(extra);
        if (sdk is not null) rows.AddRange(AndroidSdkDiscovery.Rows(sdk));
        if (java is not null) rows.Add(ToolchainDetector.JdkRow(java));
        return new ToolchainReport(Mac, rows, null, null, DateTimeOffset.UtcNow) { Java = java, AndroidSdk = sdk };
    }

    private static MobileAnalysis HelloAndroid(int? compileSdk = 35, string gradle = "8.14.3", string agp = "8.7.3", MobileFramework framework = MobileFramework.NativeAndroid) => new()
    {
        Framework = framework, Platforms = [TargetPlatform.Android],
        Android = new AndroidProjectInfo
        {
            Root = "", HasWrapper = true, WrapperGradleVersion = gradle, AgpVersion = agp,
            AppModules = [new AndroidModuleInfo { Directory = "app", GradlePath = ":app", BuildFile = "app/build.gradle", CompileSdk = compileSdk }],
        },
    };

    private static MobileAnalysis IosApp() => new()
    {
        Framework = MobileFramework.NativeIos, Platforms = [TargetPlatform.IOS],
        Ios = new IosProjectInfo { ProjectPath = "App.xcodeproj", Targets = [new IosTargetInfo { Name = "App", ProductType = "com.apple.product-type.application", BundleIdentifier = "com.example.app" }] },
    };

    [Theory]
    [InlineData(21, "8.14.3", "8.7.3", ReadinessState.Ready)]
    [InlineData(17, "8.14.3", "8.7.3", ReadinessState.Ready)]
    [InlineData(21, "8.4", "8.3.0", ReadinessState.IncompatibleVersion)]     // Gradle 8.4 cannot run on JDK 21
    [InlineData(11, "8.14.3", "8.7.3", ReadinessState.IncompatibleVersion)]  // AGP 8 needs JDK 17
    [InlineData(11, "7.5", "7.4.2", ReadinessState.Ready)]                   // an AGP 7 project with JDK 11 is fine
    [InlineData(27, "9.6.0", "9.5.0", ReadinessState.NotVerifiable)]         // newer than the table: confirmed by the build, not blocked
    public void Java_is_checked_against_the_projects_Gradle_and_AGP(int jdk, string gradle, string agp, ReadinessState expected)
    {
        var item = MobileReadiness.JavaItem(AndroidReport(null, Jdk(jdk)), gradle, agp);
        Assert.Equal(expected, item.State);
        Assert.Equal(expected is ReadinessState.IncompatibleVersion, item.Blocks);
    }

    [Fact]
    public void Flutter_MAUI_and_iOS_tools_never_block_a_native_Android_build_and_bundletool_only_matters_for_APK_sets()
    {
        var report = AndroidReport(InspectSdk(FakeSdk(_t.Combine("sdk"), licences: true), "test"), Jdk(21),
            new ToolchainInfo { Id = ToolchainIds.Flutter, DisplayName = "Flutter", Category = "Flutter", Status = ToolchainStatus.Missing },
            new ToolchainInfo { Id = ToolchainIds.Bundletool, DisplayName = "bundletool", Category = "Android", Status = ToolchainStatus.Missing });
        var apk = MobileReadiness.Evaluate(HelloAndroid(), MobileOutput.AndroidDebugApk, null, report);
        Assert.True(apk.CanBuild, apk.Summary);
        Assert.Equal(ReadinessState.NotNeeded, apk.Items.Single(i => i.Tool == "Flutter SDK").State);
        Assert.Equal(ReadinessState.NotNeeded, apk.Items.Single(i => i.Tool == "bundletool").State);
        Assert.Equal(ReadinessState.NotNeeded, apk.Items.Single(i => i.Tool == "Xcode").State);

        var apks = MobileReadiness.Evaluate(HelloAndroid(), MobileOutput.AndroidAabToApk, null, report);
        Assert.False(apks.CanBuild);
        Assert.Equal("bundletool", Assert.Single(apks.Blockers).Tool);
    }

    [Fact]
    public void Missing_Flutter_and_Android_never_block_a_Swift_macOS_build()
    {
        _t.Write("Package.swift", "// swift-tools-version:5.9\nimport PackageDescription\nlet package = Package(name: \"Probe\", platforms: [.macOS(.v14)], targets: [.executableTarget(name: \"Probe\")])");
        _t.Write("Sources/Probe/main.swift", "import SwiftUI\n@main struct ProbeApp: App { var body: some Scene { WindowGroup { Text(\"x\") } } }");
        var pkg = SwiftPackageScanner.Scan(SourceIndex.Build(_t.Path), "Package.swift");
        var mac = SwiftTests.MacReport();
        var withMissingMobile = mac with
        {
            Toolchains = [.. mac.Toolchains,
                new ToolchainInfo { Id = ToolchainIds.Flutter, DisplayName = "Flutter", Category = "Flutter", Status = ToolchainStatus.Missing },
                new ToolchainInfo { Id = ToolchainIds.AndroidSdk, DisplayName = "Android SDK", Category = "Android", Status = ToolchainStatus.Missing },
                new ToolchainInfo { Id = ToolchainIds.Jdk, DisplayName = "JDK", Category = "Android", Status = ToolchainStatus.ValidationFailed },
                new ToolchainInfo { Id = ToolchainIds.WindowsSdk, DisplayName = "Windows SDK", Category = "Windows", Status = ToolchainStatus.UnsupportedOnHost }],
        };
        Assert.Equal(SwiftCapabilities.Evaluate(pkg, mac).Support, SwiftCapabilities.Evaluate(pkg, withMissingMobile).Support);
        Assert.Equal(SupportLevel.Supported, SwiftCapabilities.Evaluate(pkg, withMissingMobile).Support);
    }

    private static ToolchainReport IosReport(IReadOnlyList<SigningIdentity> ids, IReadOnlyList<IosProvisioningProfile> profiles)
    {
        var x = XcodeDiscovery.Diagnose(new XcodeDiscovery.ProbeResults("/Applications/Xcode.app/Contents/Developer", DeveloperDirectorySource.XcodeSelect,
            [new XcodeInstallation("/Applications/Xcode.app", "/Applications/Xcode.app/Contents/Developer", "27.2", "27B52")],
            0, "Xcode 27.2\nBuild version 27B52\n", "", 0, ["macosx 27.1", "iphoneos 27.1", "iphonesimulator 27.1"]));
        return new ToolchainReport(Mac, [ToolchainDetector.XcodeToolchainInfo(x), ToolchainDetector.IosSdkRow(x)], null, null, DateTimeOffset.UtcNow)
        {
            Xcode = x, AppleIdentities = ids, IosProfiles = profiles,
        };
    }

    private static readonly SigningIdentity DevCert = new(new string('1', 40), "Apple Development: Jane (ABCDE12345)", AppleCertificateKind.Development, "ABCDE12345");
    private static readonly SigningIdentity DistCert = new(new string('2', 40), "Apple Distribution: Jane (ABCDE12345)", AppleCertificateKind.AppleDistribution, "ABCDE12345");

    [Fact]
    public void Simulator_builds_need_no_certificates_or_profiles()
    {
        var r = MobileReadiness.Evaluate(IosApp(), MobileOutput.IosSimulatorApp, null, IosReport([], []));
        Assert.True(r.CanBuild, r.Summary);
        Assert.Equal(ReadinessState.NotNeeded, r.Items.Single(i => i.Tool == "Provisioning profile").State);
        Assert.Equal(ReadinessState.NotNeeded, r.Items.Single(i => i.Tool == "Signing certificate").State);
        Assert.Equal(ReadinessState.NotNeeded, r.Items.Single(i => i.Tool == "JDK").State);   // Android tools never matter for iOS
    }

    [Fact]
    public void A_development_certificate_is_not_distribution_readiness()
    {
        var appStore = new MobileBuildOptions { Framework = MobileFramework.NativeIos, Output = MobileOutput.IosIpa, Signing = MobileSigningMode.AppleManual, ExportMethod = IosExportMethod.AppStoreConnect, ProvisioningProfile = "App Store" };
        var profile = new IosProvisioningProfile("/p", "App Store", "U1", "ABCDE12345", "ABCDE12345.com.example.app", IosProfileKind.AppStore, DateTimeOffset.UtcNow.AddYears(1));
        var devOnly = MobileReadiness.Evaluate(IosApp(), MobileOutput.IosIpa, appStore, IosReport([DevCert], [profile]));
        var cert = devOnly.Items.Single(i => i.Tool == "Apple Distribution certificate");
        Assert.Equal(ReadinessState.SetupRequired, cert.State);
        Assert.Contains("a development certificate is not enough", cert.Detail);
        Assert.False(devOnly.CanBuild);

        Assert.True(MobileReadiness.Evaluate(IosApp(), MobileOutput.IosIpa, appStore, IosReport([DevCert, DistCert], [profile])).CanBuild);

        var debugging = appStore with { ExportMethod = IosExportMethod.Debugging, ProvisioningProfile = "Dev" };
        var devProfile = profile with { Name = "Dev", Kind = IosProfileKind.Development };
        Assert.True(MobileReadiness.Evaluate(IosApp(), MobileOutput.IosIpa, debugging, IosReport([DevCert], [devProfile])).CanBuild);
        var wrongType = MobileReadiness.Evaluate(IosApp(), MobileOutput.IosIpa, appStore with { ProvisioningProfile = "Dev" }, IosReport([DevCert, DistCert], [devProfile]));
        Assert.Equal(ReadinessState.IncompatibleVersion, wrongType.Items.Single(i => i.Tool == "Provisioning profile").State);
    }

    [Fact]
    public void iOS_on_Windows_is_explained_as_another_host_not_a_broken_machine()
    {
        var report = new ToolchainReport(Windows, [], null, null, DateTimeOffset.UtcNow);
        var r = MobileReadiness.Evaluate(IosApp(), MobileOutput.IosSimulatorApp, null, report);
        var item = Assert.Single(r.Blockers);
        Assert.Equal(ReadinessState.OtherHost, item.State);
        Assert.Contains("Nothing on this computer needs fixing", item.Detail);
    }

    // ───────────── 6. Builds use the toolchain that passed inspection ─────────────

    [UnixFact]
    public void Gradle_runs_the_inspected_JDK_with_its_JAVA_HOME_SDK_and_PATH_even_from_a_Finder_environment()
    {
        var jdkHome = FakeJdk(_t.Combine("Android Studio.app", "Contents", "jbr", "Contents", "Home"));
        var sdk = InspectSdk(FakeSdk(_t.Combine("Android SDK"), licences: true), "Android Studio default location");
        var java = new JavaDiscoveryResult(new JdkCandidate(jdkHome, "Android Studio (bundled JDK)", "21.0.6", 21, true, null), [], null);
        var report = AndroidReport(sdk, java);
        var dir = MobileTestKit.Fixture("HelloAndroid");
        var finderEnv = new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin:/usr/sbin:/sbin", ["HOME"] = "/Users/me" };   // no JAVA_HOME, no ANDROID_HOME
        var plan = GradlePlanner.Plan(new MobilePlanInput
        {
            Options = new MobileBuildOptions { Framework = MobileFramework.NativeAndroid, Output = MobileOutput.AndroidDebugApk },
            Analysis = HelloAndroid(), SourceDirectory = dir, WorkDirectory = _t.Combine("work"), Toolchains = report,
            Environment = finderEnv, ToolHomeDirectory = _t.Combine("toolhome"), Keystore = new DebugKeystore(_t.Combine("ks")),
        });

        var step = Assert.Single(plan.Steps).Process;
        Assert.Equal(Path.Combine(jdkHome, "bin", "java"), step.Executable);   // the inspected JDK, not whatever "java" PATH has
        var env = step.Environment!;
        Assert.Equal(jdkHome, env["JAVA_HOME"]);
        Assert.Equal(sdk.Root, env["ANDROID_HOME"]);
        Assert.Equal(sdk.Root, env["ANDROID_SDK_ROOT"]);
        var path = env["PATH"].Split(':');
        Assert.Equal(Path.Combine(jdkHome, "bin"), path[0]);
        Assert.Contains(Path.Combine(sdk.Root!, "platform-tools"), path);
        Assert.Contains(sdk.SelectedBuildTools!.Directory, path);
        Assert.Contains("/usr/bin", path);                                       // the Finder PATH is kept, just extended
    }

    [Fact]
    public void Without_a_working_JDK_the_build_is_refused_with_where_to_fix_it()
    {
        var broken = new JavaDiscoveryResult(new JdkCandidate("/usr", "java on PATH", null, 0, false, "macOS java stub"), [], null);
        var report = AndroidReport(InspectSdk(FakeSdk(_t.Combine("sdk")), "t"), broken);
        var ex = Assert.Throws<BuildValidationException>(() => GradlePlanner.Plan(new MobilePlanInput
        {
            Options = new MobileBuildOptions { Framework = MobileFramework.NativeAndroid, Output = MobileOutput.AndroidDebugApk },
            Analysis = HelloAndroid(), SourceDirectory = MobileTestKit.Fixture("HelloAndroid"), WorkDirectory = _t.Combine("w"), Toolchains = report,
            Environment = new Dictionary<string, string>(), ToolHomeDirectory = _t.Combine("th"), Keystore = new DebugKeystore(_t.Combine("ks")),
        }));
        Assert.Contains("Toolchains › Java", ex.Message);
    }
}
