using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Infrastructure.Toolchains;
using HCG.ExecutableMaker.ProjectInspection;
using HCG.ExecutableMaker.ProjectInspection.DotNet;
using HCG.ExecutableMaker.ProjectInspection.Ecosystems;
using HCG.ExecutableMaker.ProjectInspection.Mobile;

namespace HCG.ExecutableMaker.Tests;

public class MobileInspectionTests : IDisposable
{
    private readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    internal static ProjectInspectionService Service() => new(
        new IProjectInspector[] { new DotNetProjectInspector(TimeProvider.System), new MobileProjectInspector(TimeProvider.System) }
            .Concat(EcosystemSignatureInspector.CreateDefaults(TimeProvider.System)),
        TimeProvider.System);

    internal static ToolchainReport Report(TargetPlatform host, params string[] available)
    {
        var all = new[]
        {
            ToolchainIds.DotNetSdk, ToolchainIds.Jdk, ToolchainIds.AndroidSdk, ToolchainIds.Bundletool, ToolchainIds.Xcode,
            ToolchainIds.IosSdk, ToolchainIds.AppleSigningIdentities, ToolchainIds.Flutter, ToolchainIds.MauiWorkloads,
        };
        return new ToolchainReport(
            new HostEnvironment(host, TargetArchitecture.Arm64, "test", ".NET", "m"),
            all.Select(id => new ToolchainInfo
            {
                Id = id, DisplayName = id, Category = "x",
                Status = available.Contains(id) ? ToolchainStatus.Installed : ToolchainStatus.Missing,
                Version = id == ToolchainIds.Jdk ? "21.0.2" : "1.0",
                ExecutablePath = "/x/" + id,
                AdditionalVersions = id == ToolchainIds.MauiWorkloads ? ["maui-android", "maui-ios"] : [],
            }).ToList(),
            null, null, DateTimeOffset.UtcNow);
    }

    private static readonly string[] AndroidReady = [ToolchainIds.Jdk, ToolchainIds.AndroidSdk];

    [Fact]
    public async Task Detects_HelloAndroid_fixture_with_version_catalog_and_wrapper()
    {
        var a = await Service().InspectAsync(Path.Combine(Fixtures.RepoRoot, "tests", "Fixtures", "HelloAndroid"), Report(TargetPlatform.Linux, AndroidReady), CancellationToken.None);
        Assert.Equal("mobile", a.InspectorId);
        var m = a.Mobile!;
        Assert.Equal(MobileFramework.NativeAndroid, m.Framework);
        Assert.Equal([TargetPlatform.Android], m.Platforms);
        var android = m.Android!;
        Assert.True(android.HasWrapper);
        Assert.Equal("8.14.3", android.WrapperGradleVersion);
        Assert.Equal("8.7.3", android.AgpVersion);
        var app = Assert.Single(android.AppModules);
        Assert.Equal(":app", app.GradlePath);
        Assert.Equal("app", app.Directory);
        Assert.Equal("com.hakmicapital.hello", app.ApplicationId);
        Assert.Equal((24, 35, 35, 3, "1.2.0"), (app.MinSdk, app.TargetSdk, app.CompileSdk, app.VersionCode, app.VersionName));
        Assert.Equal(["debug", "release"], app.Variants);
        Assert.Equal(SupportLevel.Supported, a.Support);
        Assert.Equal("Java", a.Language);
        Assert.Contains("AGP 8.7.3", a.Framework);
        Assert.Contains(a.Diagnostics, d => d.Message.Contains("requires Gradle 8.9+ and JDK 17+"));
        Assert.Equal(["com.hakmicapital.hello"], m.ApplicationIds);
    }

    [Fact]
    public async Task Detects_HelloIOS_fixture_targets_schemes_and_bundle_id()
    {
        var a = await Service().InspectAsync(Path.Combine(Fixtures.RepoRoot, "tests", "Fixtures", "HelloIOS"), Report(TargetPlatform.MacOS, ToolchainIds.Xcode, ToolchainIds.IosSdk), CancellationToken.None);
        var ios = a.Mobile!.Ios!;
        Assert.Equal(MobileFramework.NativeIos, a.Mobile.Framework);
        Assert.Equal("HelloIOS.xcodeproj", ios.ProjectPath);
        var target = Assert.Single(ios.AppTargets);
        Assert.Equal(("HelloIOS", "com.hakmicapital.helloios", "16.0", "Automatic"), (target.Name, target.BundleIdentifier, target.DeploymentTarget, target.CodeSignStyle));
        Assert.Equal(["Debug", "Release"], ios.Configurations);
        var scheme = Assert.Single(ios.Schemes);
        Assert.True(scheme.Shared);
        Assert.Equal("HelloIOS", scheme.Name);
        Assert.Equal(SupportLevel.Supported, a.Support);
        Assert.Equal("iOS application", a.ApplicationType);
    }

    [Fact]
    public async Task Ios_project_on_linux_requires_a_mac()
    {
        var a = await Service().InspectAsync(Path.Combine(Fixtures.RepoRoot, "tests", "Fixtures", "HelloIOS"), Report(TargetPlatform.Linux, AndroidReady), CancellationToken.None);
        Assert.Equal(SupportLevel.RequiresDifferentHost, a.Support);
        var options = MobileCapabilities.Evaluate(a.Mobile!, Report(TargetPlatform.Linux));
        Assert.All(options, o => Assert.False(o.IsAvailable));
        Assert.All(options, o => Assert.Contains("require a Mac", o.Reason));
    }

    [Fact]
    public async Task Hcg_repository_itself_is_still_a_dotnet_desktop_project()
    {
        // Mobile fixtures nested under tests/ must not hijack the analysis of the desktop solution.
        var a = await Service().InspectAsync(Fixtures.RepoRoot, Report(TargetPlatform.Linux, ToolchainIds.DotNetSdk), CancellationToken.None);
        Assert.Equal("dotnet", a.InspectorId);
        Assert.Equal("src/HCG.ExecutableMaker.App/HCG.ExecutableMaker.App.csproj", a.PrimaryProjectPath);
    }

    [Fact]
    public async Task Groovy_multi_module_with_flavors_signing_and_library()
    {
        _t.Write("settings.gradle", "rootProject.name = 'Shop'\ninclude ':app', ':core'\n// include ':ignored'\n");
        _t.Write("build.gradle", "buildscript { dependencies { classpath 'com.android.tools.build:gradle:8.5.2' } }");
        _t.Write("app/build.gradle", """
            apply plugin: 'com.android.application'
            apply plugin: 'kotlin-android'
            android {
                namespace 'com.example.shop'
                compileSdkVersion 34
                defaultConfig {
                    applicationId "com.example.shop"   // the store id
                    minSdkVersion 21
                    targetSdkVersion 34
                    versionCode 42
                    versionName "4.2"
                }
                signingConfigs { release { storeFile file('release.jks') } }
                buildTypes {
                    release { minifyEnabled true; signingConfig signingConfigs.release }
                    staging { initWith debug }
                }
                flavorDimensions "tier"
                productFlavors {
                    free { dimension "tier" }
                    paid { dimension "tier" }
                }
            }
            """);
        _t.Write("core/build.gradle", "plugins { id 'com.android.library' }\nandroid { namespace 'com.example.core' }");
        _t.Write("gradle/wrapper/gradle-wrapper.properties", "distributionUrl=https\\://services.gradle.org/distributions/gradle-8.7-all.zip");
        _t.Write("gradle/wrapper/gradle-wrapper.jar", "jar");

        var a = await Service().InspectAsync(_t.Path, Report(TargetPlatform.Linux, AndroidReady), CancellationToken.None);
        var android = a.Mobile!.Android!;
        Assert.Equal("8.5.2", android.AgpVersion);
        Assert.Equal("8.7", android.WrapperGradleVersion);
        Assert.True(android.UsesKotlin);
        Assert.Equal([":core"], android.LibraryModules);
        var app = Assert.Single(android.AppModules);
        Assert.Equal(("com.example.shop", 21, 34, 34, 42, "4.2"), (app.ApplicationId, app.MinSdk, app.TargetSdk, app.CompileSdk, app.VersionCode, app.VersionName));
        Assert.True(app.HasSigningConfig);
        Assert.Equal(["debug", "release", "staging"], app.BuildTypes);
        Assert.Equal(["free", "paid"], app.ProductFlavors);
        Assert.Equal(["freeDebug", "freeRelease", "freeStaging", "paidDebug", "paidRelease", "paidStaging"], app.Variants);
        Assert.Equal("Kotlin", a.Language);
    }

    [Fact]
    public async Task Android_project_without_wrapper_needs_configuration()
    {
        _t.Write("settings.gradle.kts", "include(\":app\")");
        _t.Write("app/build.gradle.kts", "plugins { id(\"com.android.application\") }\nandroid { defaultConfig { applicationId = \"x.y\" } }");
        var a = await Service().InspectAsync(_t.Path, Report(TargetPlatform.Linux, AndroidReady), CancellationToken.None);
        Assert.NotEqual(SupportLevel.Supported, a.Support);
        Assert.Contains(MobileCapabilities.Evaluate(a.Mobile!, Report(TargetPlatform.Linux, AndroidReady)), o => !o.IsAvailable && o.Reason!.Contains("Gradle wrapper"));
    }

    [Fact]
    public async Task Missing_android_sdk_is_reported_per_output()
    {
        var a = await Service().InspectAsync(Path.Combine(Fixtures.RepoRoot, "tests", "Fixtures", "HelloAndroid"), Report(TargetPlatform.Linux, ToolchainIds.Jdk), CancellationToken.None);
        Assert.Equal(SupportLevel.MissingToolchain, a.Support);
        var options = MobileCapabilities.Evaluate(a.Mobile!, Report(TargetPlatform.Linux, ToolchainIds.Jdk));
        Assert.All(options, o => Assert.Contains("Android SDK was not found", o.Reason));
    }

    [Fact]
    public void Aab_to_apk_requires_bundletool()
    {
        var mobile = new MobileAnalysis
        {
            Framework = MobileFramework.NativeAndroid, Platforms = [TargetPlatform.Android],
            Android = new AndroidProjectInfo { Root = "", HasWrapper = true, AppModules = [new AndroidModuleInfo { Directory = "app", GradlePath = ":app", BuildFile = "app/build.gradle" }] },
        };
        var without = MobileCapabilities.Evaluate(mobile, Report(TargetPlatform.Linux, AndroidReady));
        Assert.True(without.Single(o => o.Output == MobileOutput.AndroidDebugApk).IsAvailable);
        Assert.Contains("bundletool", without.Single(o => o.Output == MobileOutput.AndroidAabToApk).Reason);
        var with = MobileCapabilities.Evaluate(mobile, Report(TargetPlatform.Linux, [.. AndroidReady, ToolchainIds.Bundletool]));
        Assert.True(with.Single(o => o.Output == MobileOutput.AndroidAabToApk).IsAvailable);
    }

    [Fact]
    public async Task Ipa_export_requires_signing_identities_on_mac()
    {
        var path = Path.Combine(Fixtures.RepoRoot, "tests", "Fixtures", "HelloIOS");
        var a = await Service().InspectAsync(path, Report(TargetPlatform.MacOS, ToolchainIds.Xcode, ToolchainIds.IosSdk), CancellationToken.None);
        var options = MobileCapabilities.Evaluate(a.Mobile!, Report(TargetPlatform.MacOS, ToolchainIds.Xcode, ToolchainIds.IosSdk));
        Assert.True(options.Single(o => o.Output == MobileOutput.IosSimulatorApp).IsAvailable);
        Assert.True(options.Single(o => o.Output == MobileOutput.IosArchive).IsAvailable);
        Assert.Contains("code-signing identities", options.Single(o => o.Output == MobileOutput.IosIpa).Reason);
        var signed = MobileCapabilities.Evaluate(a.Mobile!, Report(TargetPlatform.MacOS, ToolchainIds.Xcode, ToolchainIds.IosSdk, ToolchainIds.AppleSigningIdentities));
        Assert.True(signed.Single(o => o.Output == MobileOutput.IosIpa).IsAvailable);
    }

    [Fact]
    public async Task Detects_flutter_app_with_both_platforms()
    {
        _t.Write("pubspec.yaml", "name: hcg_demo\ndescription: demo\nversion: 2.1.0+5\nenvironment:\n  sdk: '>=3.4.0 <4.0.0'\ndependencies:\n  flutter:\n    sdk: flutter\n");
        _t.Write("android/settings.gradle", "include \":app\"");
        _t.Write("android/app/build.gradle", "plugins {\n id \"com.android.application\"\n id \"dev.flutter.flutter-gradle-plugin\"\n}\nandroid { defaultConfig { applicationId \"com.example.hcg_demo\" } }");
        _t.Write("ios/Runner.xcodeproj/project.pbxproj", "{ objects = { }; rootObject = X; }");
        _t.Write("lib/main.dart", "void main() {}");
        var a = await Service().InspectAsync(_t.Path, Report(TargetPlatform.Linux, [.. AndroidReady, ToolchainIds.Flutter]), CancellationToken.None);
        var f = a.Mobile!.Flutter!;
        Assert.Equal(MobileFramework.Flutter, a.Mobile.Framework);
        Assert.Equal(("hcg_demo", "2.1.0+5", ">=3.4.0 <4.0.0"), (f.Name, f.Version, f.SdkConstraint));
        Assert.Equal([TargetPlatform.Android, TargetPlatform.IOS], a.Mobile.Platforms);
        Assert.Equal("Dart", a.Language);
        Assert.Equal(["com.example.hcg_demo"], a.Mobile.ApplicationIds);
        Assert.Equal(SupportLevel.Supported, a.Support); // Android buildable here; iOS needs a Mac
    }

    [Fact]
    public async Task Pubspec_without_flutter_dependency_is_not_flutter()
    {
        _t.Write("pubspec.yaml", "name: pure_dart\ndependencies:\n  http: ^1.0.0\n");
        var a = await Service().InspectAsync(_t.Path, Report(TargetPlatform.Linux), CancellationToken.None);
        Assert.NotEqual("mobile", a.InspectorId);
    }

    [Fact]
    public async Task Detects_maui_project_with_mobile_target_frameworks()
    {
        _t.Write("Shop.Maui/Shop.Maui.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFrameworks>net10.0-android;net10.0-ios;net10.0-maccatalyst</TargetFrameworks>
                <OutputType>Exe</OutputType>
                <UseMaui>true</UseMaui>
                <ApplicationTitle>Shop</ApplicationTitle>
                <ApplicationId>com.example.shop</ApplicationId>
                <ApplicationDisplayVersion>1.0</ApplicationDisplayVersion>
              </PropertyGroup>
            </Project>
            """);
        _t.Write("Shop.Core/Shop.Core.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        var a = await Service().InspectAsync(_t.Path, Report(TargetPlatform.Linux, [.. AndroidReady, ToolchainIds.DotNetSdk, ToolchainIds.MauiWorkloads]), CancellationToken.None);
        Assert.Equal("mobile", a.InspectorId);
        var maui = a.Mobile!.Maui!;
        Assert.Equal(MobileFramework.DotNetMaui, a.Mobile.Framework);
        Assert.Equal(("net10.0-android", "net10.0-ios"), (maui.AndroidTargetFramework, maui.IosTargetFramework));
        Assert.Equal("com.example.shop", maui.ApplicationId);
        Assert.Equal("Shop", a.ProjectName);
    }

    [Fact]
    public async Task Cocoapods_project_without_workspace_reports_missing_prerequisite()
    {
        File.Copy(Path.Combine(Fixtures.RepoRoot, "tests", "Fixtures", "HelloIOS", "HelloIOS.xcodeproj", "project.pbxproj"), _t.Write("HelloIOS.xcodeproj/project.pbxproj", ""), overwrite: true);
        _t.Write("Podfile", "platform :ios, '16.0'\ntarget 'HelloIOS' do\nend");
        var a = await Service().InspectAsync(_t.Path, Report(TargetPlatform.MacOS, ToolchainIds.Xcode, ToolchainIds.IosSdk), CancellationToken.None);
        Assert.True(a.Mobile!.Ios!.UsesCocoaPods);
        Assert.Contains(a.Diagnostics, d => d.Message.Contains("pod install"));
        Assert.All(MobileCapabilities.Evaluate(a.Mobile, Report(TargetPlatform.MacOS, ToolchainIds.Xcode, ToolchainIds.IosSdk)), o => Assert.Contains("CocoaPods", o.Reason));
    }

    [Fact]
    public async Task Corrupt_pbxproj_is_diagnosed_not_thrown()
    {
        _t.Write("Broken.xcodeproj/project.pbxproj", "// !$*UTF8*$!\n{ objects = { A = { isa = PBXNativeTarget; name = ");
        var a = await Service().InspectAsync(_t.Path, Report(TargetPlatform.MacOS, ToolchainIds.Xcode, ToolchainIds.IosSdk), CancellationToken.None);
        Assert.Contains(a.Diagnostics, d => d.Severity == DiagnosticSeverity.Error && d.Message.Contains("could not be parsed"));
        Assert.Equal(SupportLevel.RequiresConfiguration, a.Support);
    }

    [Fact]
    public async Task Library_only_android_project_has_no_buildable_app()
    {
        _t.Write("settings.gradle", "include ':lib'");
        _t.Write("lib/build.gradle", "plugins { id 'com.android.library' }");
        _t.Write("build.gradle", "plugins { id 'com.android.application' version '8.6.0' apply false }");
        Assert.Equal(0, new MobileProjectInspector(TimeProvider.System).Match(SourceIndex.Build(_t.Path)));
    }
}

public class MobileParserTests
{
    [Fact]
    public void OpenStep_plist_parses_nested_values_comments_and_escapes()
    {
        var root = (Dictionary<string, object>)OpenStepPlist.Parse("""
            // !$*UTF8*$!
            {
                a = 1; /* comment */
                "quoted key" = "line\nbreak \"q\"";
                list = ( x, "y z", /* c */ );
                nested = { k = v; };
                path = "$(inherited)";
            }
            """);
        Assert.Equal("1", root["a"]);
        Assert.Equal("line\nbreak \"q\"", root["quoted key"]);
        Assert.Equal(new object[] { "x", "y z" }, (List<object>)root["list"]);
        Assert.Equal("v", ((Dictionary<string, object>)root["nested"])["k"]);
        Assert.Equal("$(inherited)", root["path"]);
        Assert.Throws<FormatException>(() => OpenStepPlist.Parse("{ a = 1 "));
        Assert.Throws<FormatException>(() => OpenStepPlist.Parse(new string('{', 200)));
    }

    [Fact]
    public void Gradle_reader_strips_comments_and_reads_blocks()
    {
        var s = GradleScript.StripComments("""
            android { // c
              /* block */ defaultConfig { applicationId = "a.b" } // "x"
              buildTypes { create("beta") { } getByName("release") { } }
            }
            val url = "http://not-a-comment"
            """);
        Assert.Contains("http://not-a-comment", s);
        Assert.DoesNotContain("block", s);
        Assert.Equal("a.b", GradleScript.StringValue(GradleScript.Block(s, "defaultConfig")!, "applicationId"));
        Assert.Equal(["beta", "release"], GradleScript.ChildBlockNames(GradleScript.Block(s, "buildTypes")!));
        Assert.Equal([":app", ":feature:login"], GradleScript.IncludedProjects("include(\":app\", \"feature:login\")"));
        Assert.Equal(36, GradleScript.IntValue("compileSdk {\n version = release(36)\n}", "compileSdk"));
    }

    [Fact]
    public void Version_catalog_resolves_plugin_aliases()
    {
        var c = AndroidGradleAnalyzer.ParseCatalog("""
            [versions]
            agp = "8.6.1" # comment
            [plugins]
            android-application = { id = "com.android.application", version.ref = "agp" }
            kotlin_android = "org.jetbrains.kotlin.android:2.0.0"
            """);
        Assert.Equal(new AndroidGradleAnalyzer.CatalogPlugin("com.android.application", "8.6.1"), c.Plugins["android.application"]);
        Assert.Equal("2.0.0", c.Plugins["kotlin.android"].Version);
        Assert.True(AndroidGradleAnalyzer.IsAndroidPlugin("plugins { alias(libs.plugins.android.application) }", c, application: true));
        Assert.False(AndroidGradleAnalyzer.IsAndroidPlugin("plugins { alias(libs.plugins.android.application) apply false }", c, application: true));
    }

    [Theory]
    [InlineData("8.7.3", "8.14.3", 21, true, true)]
    [InlineData("8.7.3", "8.6", 21, false, true)]
    [InlineData("8.2.0", "8.2", 11, true, false)]
    public void Agp_compatibility_table(string agp, string gradle, int jdk, bool gradleOk, bool jdkOk)
    {
        var r = AgpCompatibility.Check(agp, gradle, jdk);
        Assert.Equal((gradleOk, jdkOk), (r.GradleOk, r.JdkOk));
    }

    [Fact]
    public void Unknown_agp_versions_are_not_assumed_compatible()
    {
        var r = AgpCompatibility.Check("9.4.0", "9.8", 21);
        Assert.Null(r.GradleOk);
        Assert.Contains("newer than HCG's compatibility table", r.Message);
    }

    [Theory]
    [InlineData("openjdk version \"21.0.12.1\" 2026-08-18", "21.0.12.1")]
    [InlineData("java version \"1.8.0_392\"", "8.0_392")]
    [InlineData("Picked up JAVA_TOOL_OPTIONS: x\nopenjdk version \"17.0.9\" 2023-10-17", "17.0.9")]
    public void Parses_java_versions(string output, string expected) => Assert.Equal(expected, ToolchainDetector.ParseJavaVersion(output));

    [Fact]
    public void Parses_xcode_sdks_signing_identities_and_workloads()
    {
        var sdks = ToolchainDetector.ParseXcodeSdks("""[{"platform":"iphoneos","sdkVersion":"18.2"},{"platform":"iphonesimulator","sdkVersion":"18.2"},{"platform":"macosx","sdkVersion":"15.2"}]""");
        Assert.Equal(["iphoneos", "iphonesimulator", "macosx"], sdks.Select(s => s.Platform).Order());
        Assert.Empty(ToolchainDetector.ParseXcodeSdks("not json"));

        var ids = ToolchainDetector.ParseSigningIdentities("""
              1) 0123456789ABCDEF0123456789ABCDEF01234567 "Apple Development: Jane Doe (ABCDE12345)"
              2) 89ABCDEF0123456789ABCDEF0123456789ABCDEF "Apple Distribution: Hakmi Capital Group (ABCDE12345)"
                 2 valid identities found
            """);
        Assert.Equal(["Apple Development: Jane Doe (ABCDE12345)", "Apple Distribution: Hakmi Capital Group (ABCDE12345)"], ids);
        Assert.Empty(ToolchainDetector.ParseSigningIdentities("     0 valid identities found"));

        var workloads = ToolchainDetector.ParseWorkloadList("""

            Installed Workload Id      Manifest Version       Installation Source
            ---------------------------------------------------------------------
            maui-android               10.0.1/10.0.100        SDK 10.0.100
            ios                        26.0.1/10.0.100        SDK 10.0.100

            Use `dotnet workload search` to find additional workloads to install.
            """);
        Assert.Equal(["maui-android", "ios"], workloads);
        Assert.Empty(ToolchainDetector.ParseWorkloadList("\nInstalled Workload Id      Manifest Version\n-------\n\nUse `dotnet workload search` to find additional workloads to install."));
    }
}
