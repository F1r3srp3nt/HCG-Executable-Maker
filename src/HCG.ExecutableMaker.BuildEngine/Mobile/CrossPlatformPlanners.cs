using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.BuildEngine.Mobile;

/// <summary>
/// bundletool: App Bundle → APK set. This is a transformation of an artifact the user already has (or that HCG just
/// built), using Google's own tool. It is NOT a general APK→AAB conversion (that direction is not possible).
/// </summary>
public static class BundletoolPlanner
{
    public const int MaxDeviceSpecBytes = 64 * 1024;

    public static MobileStep BuildApks(MobilePlanInput input, string aabPath, string apksPath)
    {
        var java = MobileArgs.InspectedJdk(input.Toolchains)?.JavaExecutable ?? throw new BuildValidationException("A JDK is required to run bundletool. Choose one in Toolchains › Java.");
        var jar = input.Toolchains.Find(ToolchainIds.Bundletool) is { IsAvailable: true, ExecutablePath: { } j } ? j
            : throw new BuildValidationException("bundletool was not found. Set BUNDLETOOL_JAR to a bundletool-all-*.jar downloaded from github.com/google/bundletool/releases.");
        var ks = input.Keystore ?? throw new BuildValidationException("The HCG debug keystore was not prepared.");
        var args = new List<string> { "-jar", jar, "build-apks", "--bundle=" + aabPath, "--output=" + apksPath, "--overwrite" };
        switch (input.Options.ApkSetMode)
        {
            case ApkSetMode.Universal:
                args.Add("--mode=universal");
                break;
            case ApkSetMode.DeviceTargeted:
                var spec = input.Options.DeviceSpecPath ?? throw new BuildValidationException("Device-targeted APKs need a device-spec JSON file (bundletool get-device-spec).");
                var info = new FileInfo(spec);
                if (!info.Exists || !spec.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || info.Length > MaxDeviceSpecBytes)
                    throw new BuildValidationException("The device spec must be an existing .json file of at most 64 KB.");
                args.Add("--device-spec=" + info.FullName);
                break;
        }
        // Explicit HCG debug key, so bundletool never falls back to ~/.android/debug.keystore implicitly.
        args.AddRange(["--ks=" + ks.Path, "--ks-pass=pass:" + DebugKeystore.Password, "--ks-key-alias=" + DebugKeystore.Alias, "--key-pass=pass:" + DebugKeystore.Password]);
        return new MobileStep(BuildStage.Packaging, "bundletool build-apks", new ProcessSpec
        {
            Executable = java, Arguments = args, WorkingDirectory = input.WorkDirectory,
            Environment = MobileArgs.ToolchainEnvironment(input.Environment, input.Toolchains, ("ANDROID_USER_HOME", Path.Combine(input.ToolHomeDirectory, "android"))),
        });
    }
}

/// <summary>Flutter CLI builds (macOS/Linux hosts). Flutter manages its own Gradle/Xcode invocations.</summary>
public static class FlutterPlanner
{
    public static MobilePlan Plan(MobilePlanInput input)
    {
        var o = input.Options;
        var f = input.Analysis.Flutter ?? throw new BuildValidationException("This is not a Flutter project.");
        if (input.Toolchains.Host.Platform == TargetPlatform.Windows) throw new BuildValidationException("Flutter builds from HCG are supported on macOS and Linux hosts in this milestone.");
        var flutter = input.Toolchains.Find(ToolchainIds.Flutter) is { IsAvailable: true, ExecutablePath: { } p } ? p : throw new BuildValidationException("The Flutter SDK was not found.");
        var root = MobileArgs.Inside(input.SourceDirectory, f.Root);
        var build = Path.Combine(root, "build");
        var notes = new List<string>();
        List<string> args;
        OutputRule[] outputs;
        switch (o.Output)
        {
            case MobileOutput.AndroidDebugApk:
                args = ["build", "apk", "--debug"];
                outputs = [new(PackagingFormat.AndroidApk, Path.Combine(build, "app", "outputs", "flutter-apk"), "app-debug.apk", false, false)];
                break;
            case MobileOutput.AndroidReleaseApk:
                args = ["build", "apk", "--release"];
                outputs = [new(PackagingFormat.AndroidApk, Path.Combine(build, "app", "outputs", "flutter-apk"), "app-release.apk", false, false)];
                notes.Add("Release signing comes from the project's android/app/build.gradle (Flutter's template signs release builds with the debug key until you configure your own).");
                break;
            case MobileOutput.AndroidAppBundle:
            case MobileOutput.AndroidAabToApk:
                args = ["build", "appbundle", "--release"];
                outputs = [new(PackagingFormat.AndroidAab, Path.Combine(build, "app", "outputs", "bundle", "release"), "*.aab", false, false)];
                break;
            case MobileOutput.IosSimulatorApp:
                args = ["build", "ios", "--simulator", "--debug"];
                outputs = [new(PackagingFormat.IosSimulatorApp, Path.Combine(build, "ios", "iphonesimulator"), "*.app", true, false)];
                notes.Add("Simulator builds are not installable on devices and not distributable.");
                break;
            case MobileOutput.IosArchive:
                args = ["build", "ipa", "--release"];
                if (o.Signing == MobileSigningMode.Unsigned) { args.Add("--no-codesign"); notes.Add("UNSIGNED archive: inspection only; cannot be exported or installed."); }
                outputs = [new(PackagingFormat.IosArchive, Path.Combine(build, "ios", "archive"), "*.xcarchive", true, false)];
                break;
            case MobileOutput.IosIpa:
                if (o.Signing == MobileSigningMode.Unsigned) throw new BuildValidationException("An IPA requires signing. Choose the project's signing configuration.");
                var method = o.ExportMethod ?? IosExportMethod.Debugging;
                args = ["build", "ipa", "--release", "--export-method", method switch
                {
                    IosExportMethod.AppStoreConnect => "app-store",
                    IosExportMethod.ReleaseTesting => "ad-hoc",
                    IosExportMethod.Enterprise => "enterprise",
                    _ => "development",
                }];
                outputs = [new(PackagingFormat.IosIpa, Path.Combine(build, "ios", "ipa"), "*.ipa", false, false)];
                notes.Add("The IPA is exported locally; HCG never uploads it.");
                break;
            default:
                throw new BuildValidationException($"{o.Output.DisplayName()} is not supported for Flutter.");
        }
        args.Add("--suppress-analytics");
        var env = MobileArgs.ToolchainEnvironment(input.Environment, input.Toolchains,
            ("GRADLE_USER_HOME", Path.Combine(input.ToolHomeDirectory, "gradle")), ("ANDROID_USER_HOME", Path.Combine(input.ToolHomeDirectory, "android")),
            ("FLUTTER_SUPPRESS_ANALYTICS", "true"), ("CI", "true"));
        Directory.CreateDirectory(Path.Combine(input.ToolHomeDirectory, "gradle"));
        Directory.CreateDirectory(Path.Combine(input.ToolHomeDirectory, "android"));
        return new MobilePlan([new MobileStep(BuildStage.Compiling, "flutter " + string.Join(' ', args.Take(3)), new ProcessSpec
        {
            Executable = flutter, Arguments = args, WorkingDirectory = root, Environment = env,
        })], outputs) { Notes = notes };
    }
}

/// <summary>.NET MAUI builds through the .NET SDK's Android/iOS workloads.</summary>
public static class MauiPlanner
{
    public static MobilePlan Plan(MobilePlanInput input)
    {
        var o = input.Options;
        var m = input.Analysis.Maui ?? throw new BuildValidationException("This is not a .NET MAUI project.");
        var dotnet = input.Toolchains.Find(ToolchainIds.DotNetSdk) is { IsAvailable: true, ExecutablePath: { } d } ? d : throw new BuildValidationException("The .NET SDK was not found.");
        var project = MobileArgs.Inside(input.SourceDirectory, m.ProjectPath);
        var projectDir = Path.GetDirectoryName(project)!;
        var publishDir = Path.Combine(input.WorkDirectory, "publish");
        var notes = new List<string>();
        List<string> args;
        OutputRule[] outputs;
        var android = o.Output.Platform() == TargetPlatform.Android;
        var tfm = (android ? m.AndroidTargetFramework : m.IosTargetFramework)
                  ?? throw new BuildValidationException($"The project does not target {(android ? "Android" : "iOS")}.");
        MobileArgs.RequireToken(tfm, "target framework", out tfm, @"^net\d+\.\d+-(android|ios)[\d.]*$");

        switch (o.Output)
        {
            case MobileOutput.AndroidDebugApk:
                args = ["build", project, "-f", tfm, "-c", "Debug", "-p:AndroidPackageFormats=apk", "-p:EmbedAssembliesIntoApk=true"];
                outputs = [new(PackagingFormat.AndroidApk, Path.Combine(projectDir, "bin", "Debug", tfm), "*-Signed.apk", false)];
                break;
            case MobileOutput.AndroidReleaseApk:
                args = ["publish", project, "-f", tfm, "-c", "Release", "-p:AndroidPackageFormats=apk", "-o", publishDir];
                outputs = [new(PackagingFormat.AndroidApk, publishDir, "*-Signed.apk", false)];
                notes.Add("Without AndroidKeyStore settings in the project, .NET signs Android release output with its debug keystore.");
                break;
            case MobileOutput.AndroidAppBundle:
            case MobileOutput.AndroidAabToApk:
                args = ["publish", project, "-f", tfm, "-c", "Release", "-p:AndroidPackageFormats=aab", "-o", publishDir];
                outputs = [new(PackagingFormat.AndroidAab, publishDir, "*-Signed.aab", false)];
                break;
            case MobileOutput.IosSimulatorApp:
                var simRid = input.Toolchains.Host.Architecture == TargetArchitecture.Arm64 ? "iossimulator-arm64" : "iossimulator-x64";
                args = ["build", project, "-f", tfm, "-c", "Debug", "-p:RuntimeIdentifier=" + simRid];
                outputs = [new(PackagingFormat.IosSimulatorApp, Path.Combine(projectDir, "bin", "Debug", tfm, simRid), "*.app", true, false)];
                notes.Add("Simulator builds are not installable on devices and not distributable.");
                break;
            case MobileOutput.IosIpa:
                if (o.Signing != MobileSigningMode.ProjectDefault)
                    throw new BuildValidationException(".NET MAUI IPAs are signed with the project's own CodesignKey/CodesignProvision settings; choose 'Project signing configuration'.");
                args = ["publish", project, "-f", tfm, "-c", "Release", "-p:RuntimeIdentifier=ios-arm64", "-o", publishDir];
                outputs = [new(PackagingFormat.IosIpa, publishDir, "*.ipa", false)];
                break;
            default:
                throw new BuildValidationException($"{o.Output.DisplayName()} is not offered for .NET MAUI projects in this version (MAUI produces an .ipa directly).");
        }
        if (android)
        {
            if (input.Toolchains.Find(ToolchainIds.AndroidSdk)?.ExecutablePath is { } sdk) args.Add("-p:AndroidSdkDirectory=" + sdk);
            if (MobileArgs.InspectedJdk(input.Toolchains)?.Home is { } jh) args.Add("-p:JavaSdkDirectory=" + jh);
        }
        args.AddRange(["-p:UseSharedCompilation=false", "-nodeReuse:false"]);
        return new MobilePlan([new MobileStep(BuildStage.Compiling, $"dotnet {args[0]} -f {tfm}", new ProcessSpec
        {
            Executable = dotnet, Arguments = args, WorkingDirectory = projectDir,
            Environment = MobileArgs.ToolchainEnvironment(input.Environment, input.Toolchains, ("ANDROID_USER_HOME", Path.Combine(input.ToolHomeDirectory, "android"))),
        })], outputs) { Notes = notes };
    }
}

/// <summary>
/// Capacitor apps: install the npm dependencies, build the web app with its own script, copy the build into the native
/// project with Capacitor's CLI (cap sync), then build that native project with the Gradle or Xcode planner.
/// </summary>
public static class CapacitorPlanner
{
    public static MobilePlan Plan(MobilePlanInput input)
    {
        var o = input.Options;
        var cap = input.Analysis.Capacitor ?? throw new BuildValidationException("This is not a Capacitor project.");
        var android = o.Output.Platform() == TargetPlatform.Android;
        var platform = android ? "android" : "ios";
        if (android ? !cap.HasAndroid : !cap.HasIos)
            throw new BuildValidationException($"The Capacitor app has no {platform}/ project. Add it with npx cap add {platform} (or with HCG's Validate step for a converted project).");
        if (cap.BuildScript is null) throw new BuildValidationException("package.json has no build script, so the web app cannot be built before syncing.");
        var node = input.Toolchains.NodeInstallations.FirstOrDefault(n => int.TryParse(n.Version.TrimStart('v').Split('.')[0], out var major) && major >= 22)
                   ?? throw new BuildValidationException("Capacitor 8 needs Node.js 22 or newer, and none was found.");
        if (node.NpmCli is null) throw new BuildValidationException($"npm was not found next to Node.js {node.Version} ({node.NodePath}).");
        var root = MobileArgs.Inside(input.SourceDirectory, cap.Root);
        var env = new Dictionary<string, string>(Electron.ElectronBuildPlanner.Environment(input.Environment, node)) { ["CI"] = "true", ["CAPACITOR_TELEMETRY"] = "false" };
        ProcessSpec Npm(params string[] args) => new()
        {
            Executable = node.NodePath, Arguments = [node.NpmCli, .. args], WorkingDirectory = root, Environment = env, Timeout = TimeSpan.FromMinutes(20),
        };
        var steps = new List<MobileStep>
        {
            new(BuildStage.Restoring, cap.HasLockFile ? "Install dependencies (npm ci)" : "Install dependencies (npm install)",
                Npm(cap.HasLockFile ? ["ci", "--no-audit", "--no-fund"] : ["install", "--no-audit", "--no-fund"])),
            new(BuildStage.Compiling, $"Build the web app (npm run {cap.BuildScript})", Npm("run", cap.BuildScript)),
            new(BuildStage.Compiling, $"Copy the web build into {platform}/ (cap sync {platform})", new ProcessSpec
            {
                Executable = node.NodePath, Arguments = [Path.Combine(root, "node_modules", "@capacitor", "cli", "bin", "capacitor"), "sync", platform],
                WorkingDirectory = root, Environment = env, Timeout = TimeSpan.FromMinutes(10),
            }),
        };
        var nativeInput = input with
        {
            Analysis = input.Analysis with { Framework = android ? MobileFramework.NativeAndroid : MobileFramework.NativeIos },
            Options = o with { Framework = android ? MobileFramework.NativeAndroid : MobileFramework.NativeIos },
        };
        var native = android ? GradlePlanner.Plan(nativeInput) : XcodePlanner.Plan(nativeInput);
        return new MobilePlan([.. steps, .. native.Steps], native.Outputs)
        {
            Notes = [$"Capacitor app: the web build ({cap.WebDir}/) is copied into the {platform}/ project before the native build.", .. native.Notes],
        };
    }
}
