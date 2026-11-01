using System.Text.Json;
using System.Text.RegularExpressions;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.Infrastructure.Toolchains;

/// <summary>
/// Mobile toolchain probes (Android SDK/JDK/bundletool, Apple iOS SDK and signing, Flutter, .NET MAUI). Probes only
/// locate tools and read versions — nothing is installed, updated or configured.
/// </summary>
public sealed partial class ToolchainDetector
{
    private const string AndroidCat = "Android";
    private const string AppleCat = "Apple mobile";
    private const string FlutterCat = "Flutter";
    private const string MauiCat = ".NET MAUI";

    private async Task<IReadOnlyList<ToolchainInfo>> DetectMobileAsync(CancellationToken ct)
    {
        var list = new List<ToolchainInfo>();
        var jdk = await DetectJdkAsync(ct).ConfigureAwait(false);
        list.Add(jdk);
        list.AddRange(DetectAndroidSdk());
        list.Add(await DetectBundletoolAsync(jdk, ct).ConfigureAwait(false));
        list.AddRange(await DetectAppleMobileAsync(ct).ConfigureAwait(false));
        list.Add(DetectFlutter());
        list.Add(await DetectMauiWorkloadsAsync(ct).ConfigureAwait(false));
        return list;
    }

    // ---------- JDK ----------

    private async Task<ToolchainInfo> DetectJdkAsync(CancellationToken ct)
    {
        const string hint = "Install a JDK 17+ (e.g. Eclipse Temurin or the JDK bundled with Android Studio) and set JAVA_HOME.";
        var exe = Path.Combine("bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(javaHome)) candidates.Add(Path.Combine(javaHome, exe));
        if (OperatingSystem.IsMacOS()) candidates.Add("/Applications/Android Studio.app/Contents/jbr/Contents/Home/bin/java");
        var java = locator.Find("java", candidates);
        if (java is null) return Missing(ToolchainIds.Jdk, "JDK (Java)", AndroidCat, hint);
        try
        {
            var (r, stdout, stderr) = await runner.CaptureAsync(new ProcessSpec { Executable = java, Arguments = ["-version"], Timeout = ProbeTimeout, Environment = ProbeEnvironment() }, ct).ConfigureAwait(false);
            var version = ParseJavaVersion(stderr + "\n" + stdout);
            if (!r.Succeeded || version is null) return Failed(ToolchainIds.Jdk, "JDK (Java)", AndroidCat, java, "java -version did not report a version.", hint);
            var hasJavac = File.Exists(Path.Combine(Path.GetDirectoryName(java)!, OperatingSystem.IsWindows() ? "javac.exe" : "javac"));
            var major = int.TryParse(version.Split('.')[0], out var m) ? m : 0;
            return new ToolchainInfo
            {
                Id = ToolchainIds.Jdk, DisplayName = "JDK (Java)", Category = AndroidCat,
                Status = hasJavac && major >= 17 ? ToolchainStatus.Installed : ToolchainStatus.ValidationFailed,
                Version = version, ExecutablePath = java,
                Detail = !hasJavac ? "Java runtime found, but javac is missing (a full JDK is required)." : major < 17 ? $"Java {major} is too old for the Android Gradle Plugin 8 (needs 17+)." : "JDK",
                InstallHint = hint,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return Failed(ToolchainIds.Jdk, "JDK (Java)", AndroidCat, java, ex.Message, hint); }
    }

    /// <summary>Parses <c>openjdk version "21.0.12"</c> / <c>java version "1.8.0_392"</c> (1.8 → "8").</summary>
    public static string? ParseJavaVersion(string output)
    {
        var m = JavaVersionRegex().Match(output);
        if (!m.Success) return null;
        var v = m.Groups[1].Value;
        return v.StartsWith("1.", StringComparison.Ordinal) ? v[2..] : v;
    }

    [GeneratedRegex(@"version\s+""([\d._]+)")]
    private static partial Regex JavaVersionRegex();

    // ---------- Android SDK ----------

    public static IEnumerable<string> AndroidSdkCandidates(Func<string, string?> getEnv)
    {
        foreach (var v in new[] { "ANDROID_HOME", "ANDROID_SDK_ROOT" })
            if (getEnv(v) is { Length: > 0 } p) yield return p;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS()) yield return Path.Combine(home, "Library", "Android", "sdk");
        else if (OperatingSystem.IsWindows()) yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk");
        else
        {
            yield return Path.Combine(home, "Android", "Sdk");
            yield return "/usr/local/lib/android/sdk";
            yield return "/opt/android-sdk";
        }
    }

    private IEnumerable<ToolchainInfo> DetectAndroidSdk()
    {
        const string hint = "Install the Android SDK (Android Studio → SDK Manager, or command-line tools) and set ANDROID_HOME.";
        var sdk = AndroidSdkCandidates(Environment.GetEnvironmentVariable).FirstOrDefault(d => Directory.Exists(Path.Combine(d, "platforms")) || Directory.Exists(Path.Combine(d, "build-tools")));
        if (sdk is null)
        {
            yield return Missing(ToolchainIds.AndroidSdk, "Android SDK", AndroidCat, hint);
            foreach (var (id, name) in new[] { (ToolchainIds.AndroidBuildTools, "Android build-tools"), (ToolchainIds.AndroidPlatforms, "Android platforms"), (ToolchainIds.Apksigner, "apksigner"), (ToolchainIds.Zipalign, "zipalign"), (ToolchainIds.Aapt2, "aapt2") })
                yield return Missing(id, name, AndroidCat, "Part of the Android SDK build-tools.");
            yield break;
        }
        var platforms = SortedVersionDirs(Path.Combine(sdk, "platforms"), "android-");
        var buildTools = SortedVersionDirs(Path.Combine(sdk, "build-tools"), "");
        yield return new ToolchainInfo
        {
            Id = ToolchainIds.AndroidSdk, DisplayName = "Android SDK", Category = AndroidCat, Status = ToolchainStatus.Installed,
            ExecutablePath = sdk, Version = platforms.Count > 0 ? "API " + platforms[0] : null,
            Detail = $"{platforms.Count} platform(s), {buildTools.Count} build-tools version(s)",
        };
        yield return buildTools.Count > 0
            ? new ToolchainInfo { Id = ToolchainIds.AndroidBuildTools, DisplayName = "Android build-tools", Category = AndroidCat, Status = ToolchainStatus.Installed, Version = buildTools[0], ExecutablePath = Path.Combine(sdk, "build-tools", buildTools[0]), AdditionalVersions = buildTools }
            : Missing(ToolchainIds.AndroidBuildTools, "Android build-tools", AndroidCat, "Install build-tools with the SDK Manager.");
        yield return platforms.Count > 0
            ? new ToolchainInfo { Id = ToolchainIds.AndroidPlatforms, DisplayName = "Android platforms", Category = AndroidCat, Status = ToolchainStatus.Installed, Version = "API " + platforms[0], ExecutablePath = Path.Combine(sdk, "platforms"), AdditionalVersions = platforms.Select(p => "API " + p).ToList() }
            : Missing(ToolchainIds.AndroidPlatforms, "Android platforms", AndroidCat, "Install an Android platform (API level) with the SDK Manager.");
        var bt = buildTools.Count > 0 ? Path.Combine(sdk, "build-tools", buildTools[0]) : null;
        string Tool(string n) => OperatingSystem.IsWindows() ? n + (n == "apksigner" ? ".bat" : ".exe") : n;
        foreach (var (id, name) in new[] { (ToolchainIds.Apksigner, "apksigner"), (ToolchainIds.Zipalign, "zipalign"), (ToolchainIds.Aapt2, "aapt2") })
        {
            var path = bt is null ? null : Path.Combine(bt, Tool(name));
            yield return path is not null && File.Exists(path)
                ? new ToolchainInfo { Id = id, DisplayName = name, Category = AndroidCat, Status = ToolchainStatus.Installed, ExecutablePath = path, Version = buildTools[0] }
                : Missing(id, name, AndroidCat, "Part of the Android SDK build-tools.");
        }
    }

    private static List<string> SortedVersionDirs(string dir, string prefix)
    {
        if (!Directory.Exists(dir)) return [];
        return Directory.GetDirectories(dir).Select(Path.GetFileName).OfType<string>()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal))
            .Select(n => n[prefix.Length..])
            .OrderByDescending(n => Version.TryParse(n.Contains('.') ? n.Split('-')[0] : n + ".0", out var v) ? v : new Version())
            .ToList();
    }

    // ---------- bundletool ----------

    private async Task<ToolchainInfo> DetectBundletoolAsync(ToolchainInfo jdk, CancellationToken ct)
    {
        const string hint = "Download bundletool-all-<version>.jar from https://github.com/google/bundletool/releases and set BUNDLETOOL_JAR to its path (HCG does not download it).";
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable("BUNDLETOOL_JAR") is { Length: > 0 } envJar) candidates.Add(envJar);
        var toolsDir = Path.Combine(paths.Root, "tools");
        if (Directory.Exists(toolsDir)) candidates.AddRange(Directory.GetFiles(toolsDir, "bundletool*.jar").OrderDescending());
        var jar = candidates.FirstOrDefault(File.Exists);
        if (jar is null) return Missing(ToolchainIds.Bundletool, "bundletool", AndroidCat, hint);
        if (!jdk.IsAvailable || jdk.ExecutablePath is null)
            return Failed(ToolchainIds.Bundletool, "bundletool", AndroidCat, jar, "bundletool needs a JDK to run.", hint);
        try
        {
            var (r, stdout, _) = await runner.CaptureAsync(new ProcessSpec { Executable = jdk.ExecutablePath, Arguments = ["-jar", jar, "version"], Timeout = ProbeTimeout, Environment = ProbeEnvironment() }, ct).ConfigureAwait(false);
            var version = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
            return r.Succeeded
                ? new ToolchainInfo { Id = ToolchainIds.Bundletool, DisplayName = "bundletool", Category = AndroidCat, Status = ToolchainStatus.Installed, ExecutablePath = jar, Version = version }
                : Failed(ToolchainIds.Bundletool, "bundletool", AndroidCat, jar, $"`bundletool version` exited with {r.ExitCode}.", hint);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return Failed(ToolchainIds.Bundletool, "bundletool", AndroidCat, jar, ex.Message, hint); }
    }

    // ---------- Apple mobile ----------

    private async Task<IReadOnlyList<ToolchainInfo>> DetectAppleMobileAsync(CancellationToken ct)
    {
        if (Host.Platform != TargetPlatform.MacOS)
            return
            [
                Unsupported(ToolchainIds.IosSdk, "iOS SDK", AppleCat, "iOS builds require macOS with Xcode."),
                Unsupported(ToolchainIds.AppleSigningIdentities, "Code-signing identities", AppleCat, "Only available on macOS hosts."),
                Unsupported(ToolchainIds.ProvisioningProfiles, "Provisioning profiles", AppleCat, "Only available on macOS hosts."),
            ];
        var result = new List<ToolchainInfo>();
        const string xcodebuild = "/usr/bin/xcodebuild";
        try
        {
            var (r, stdout, _) = await runner.CaptureAsync(new ProcessSpec { Executable = xcodebuild, Arguments = ["-showsdks", "-json"], Timeout = ProbeTimeout, Environment = ProbeEnvironment() }, ct).ConfigureAwait(false);
            var sdks = r.Succeeded ? ParseXcodeSdks(stdout) : [];
            var device = sdks.Where(s => s.Platform == "iphoneos").Select(s => s.Version).ToList();
            var sim = sdks.Where(s => s.Platform == "iphonesimulator").Select(s => s.Version).ToList();
            result.Add(device.Count > 0
                ? new ToolchainInfo { Id = ToolchainIds.IosSdk, DisplayName = "iOS SDK", Category = AppleCat, Status = ToolchainStatus.Installed, Version = device[0], ExecutablePath = xcodebuild, AdditionalVersions = [.. device.Select(v => "iphoneos " + v), .. sim.Select(v => "iphonesimulator " + v)], Detail = sim.Count > 0 ? "Device and Simulator SDKs" : "Device SDK only (no Simulator SDK)" }
                : Missing(ToolchainIds.IosSdk, "iOS SDK", AppleCat, "Install the iOS platform in Xcode → Settings → Components."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result.Add(Failed(ToolchainIds.IosSdk, "iOS SDK", AppleCat, xcodebuild, ex.Message, "Install Xcode from the Mac App Store and run it once."));
        }

        const string security = "/usr/bin/security";
        try
        {
            var (r, stdout, _) = await runner.CaptureAsync(new ProcessSpec { Executable = security, Arguments = ["find-identity", "-v", "-p", "codesigning"], Timeout = ProbeTimeout, Environment = ProbeEnvironment() }, ct).ConfigureAwait(false);
            var identities = r.Succeeded ? ParseSigningIdentities(stdout) : [];
            result.Add(identities.Count > 0
                ? new ToolchainInfo { Id = ToolchainIds.AppleSigningIdentities, DisplayName = "Code-signing identities", Category = AppleCat, Status = ToolchainStatus.Installed, Version = $"{identities.Count} valid", ExecutablePath = security, AdditionalVersions = identities, Detail = string.Join("; ", identities.Take(3)) }
                : Missing(ToolchainIds.AppleSigningIdentities, "Code-signing identities", AppleCat, "Sign in with your Apple Developer account in Xcode → Settings → Accounts to create signing certificates."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result.Add(Failed(ToolchainIds.AppleSigningIdentities, "Code-signing identities", AppleCat, security, ex.Message, null));
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var profiles = new[] { Path.Combine(home, "Library", "MobileDevice", "Provisioning Profiles"), Path.Combine(home, "Library", "Developer", "Xcode", "UserData", "Provisioning Profiles") }
            .Where(Directory.Exists).SelectMany(d => Directory.GetFiles(d, "*.mobileprovision")).ToList();
        result.Add(profiles.Count > 0
            ? new ToolchainInfo { Id = ToolchainIds.ProvisioningProfiles, DisplayName = "Provisioning profiles", Category = AppleCat, Status = ToolchainStatus.Installed, Version = $"{profiles.Count} installed" }
            : Missing(ToolchainIds.ProvisioningProfiles, "Provisioning profiles", AppleCat, "None installed. Xcode automatic signing creates them on demand; manual signing needs profiles from developer.apple.com."));
        return result;
    }

    public sealed record XcodeSdk(string Platform, string Version);

    /// <summary>Parses <c>xcodebuild -showsdks -json</c>.</summary>
    public static IReadOnlyList<XcodeSdk> ParseXcodeSdks(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.EnumerateArray()
                .Select(e => new XcodeSdk(
                    e.TryGetProperty("platform", out var p) ? p.GetString() ?? "" : "",
                    e.TryGetProperty("sdkVersion", out var v) ? v.GetString() ?? "" : ""))
                .Where(s => s.Platform.Length > 0)
                .OrderByDescending(s => Version.TryParse(s.Version, out var v) ? v : new Version())
                .ToList();
        }
        catch (JsonException) { return []; }
    }

    /// <summary>Identity display names from <c>security find-identity -v -p codesigning</c> (names only; no key material).</summary>
    public static IReadOnlyList<string> ParseSigningIdentities(string output) =>
        SigningIdentityRegex().Matches(output).Select(m => m.Groups[1].Value).Distinct().ToList();

    [GeneratedRegex(@"^\s*\d+\)\s+[0-9A-F]{40}\s+""([^""]+)""", RegexOptions.Multiline)]
    private static partial Regex SigningIdentityRegex();

    // ---------- Flutter ----------

    private ToolchainInfo DetectFlutter()
    {
        const string hint = "Install the Flutter SDK (https://docs.flutter.dev/get-started/install) and add flutter/bin to PATH or set FLUTTER_ROOT.";
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var name = OperatingSystem.IsWindows() ? "flutter.bat" : "flutter";
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable("FLUTTER_ROOT") is { Length: > 0 } fr) candidates.Add(Path.Combine(fr, "bin", name));
        candidates.AddRange([Path.Combine(home, "flutter", "bin", name), Path.Combine(home, "development", "flutter", "bin", name), Path.Combine("/opt", "flutter", "bin", name)]);
        var flutter = locator.Find("flutter", candidates);
        if (flutter is null) return Missing(ToolchainIds.Flutter, "Flutter SDK", FlutterCat, hint);
        var root = Path.GetDirectoryName(Path.GetDirectoryName(flutter)!)!;
        return new ToolchainInfo
        {
            Id = ToolchainIds.Flutter, DisplayName = "Flutter SDK", Category = FlutterCat, Status = ToolchainStatus.Installed,
            ExecutablePath = flutter, Version = ReadFlutterVersion(root), Detail = root,
        };
    }

    /// <summary>Reads the SDK version from bin/cache/flutter.version.json or the legacy version file (no flutter process is started).</summary>
    public static string? ReadFlutterVersion(string root)
    {
        try
        {
            var json = Path.Combine(root, "bin", "cache", "flutter.version.json");
            if (File.Exists(json))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(json));
                if (doc.RootElement.TryGetProperty("frameworkVersion", out var v)) return v.GetString();
            }
            var legacy = Path.Combine(root, "version");
            return File.Exists(legacy) ? File.ReadAllText(legacy).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }

    // ---------- .NET MAUI ----------

    private async Task<ToolchainInfo> DetectMauiWorkloadsAsync(CancellationToken ct)
    {
        const string hint = "Install with: dotnet workload install maui (or maui-android / maui-ios). HCG never installs workloads itself.";
        var dotnet = locator.Find("dotnet", ExecutableLocator.DotNetCandidates());
        if (dotnet is null) return Missing(ToolchainIds.MauiWorkloads, ".NET MAUI workloads", MauiCat, hint);
        try
        {
            var (r, stdout, _) = await runner.CaptureAsync(new ProcessSpec { Executable = dotnet, Arguments = ["workload", "list"], Timeout = TimeSpan.FromSeconds(60), Environment = ProbeEnvironment() }, ct).ConfigureAwait(false);
            var workloads = r.Succeeded ? ParseWorkloadList(stdout) : [];
            var mobile = workloads.Where(w => w is "maui" or "maui-android" or "maui-ios" or "android" or "ios" or "maui-mobile").ToList();
            return mobile.Count > 0
                ? new ToolchainInfo { Id = ToolchainIds.MauiWorkloads, DisplayName = ".NET MAUI workloads", Category = MauiCat, Status = ToolchainStatus.Installed, ExecutablePath = dotnet, Version = string.Join(", ", mobile), AdditionalVersions = mobile }
                : Missing(ToolchainIds.MauiWorkloads, ".NET MAUI workloads", MauiCat, hint);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return Failed(ToolchainIds.MauiWorkloads, ".NET MAUI workloads", MauiCat, dotnet, ex.Message, hint); }
    }

    /// <summary>Workload ids from the <c>dotnet workload list</c> table.</summary>
    public static IReadOnlyList<string> ParseWorkloadList(string output)
    {
        var ids = new List<string>();
        var inTable = false;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.TrimStart().StartsWith("Installed Workload Id", StringComparison.OrdinalIgnoreCase)) { inTable = true; continue; }
            if (!inTable) continue;
            if (line.TrimStart().StartsWith('-') || line.Trim().Length == 0) { if (ids.Count > 0 && line.Trim().Length == 0) break; continue; }
            if (line.StartsWith("Use `dotnet workload search`", StringComparison.Ordinal)) break;
            var id = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            if (WorkloadIdRegex().IsMatch(id)) ids.Add(id);
        }
        return ids;
    }

    [GeneratedRegex(@"^[a-z][a-z0-9\-]*$")]
    private static partial Regex WorkloadIdRegex();
}
