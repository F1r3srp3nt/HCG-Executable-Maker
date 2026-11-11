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

    /// <summary>Mobile detection result: the rows plus the structured JDK / SDK / signing details behind them.</summary>
    private sealed record MobileDetection(IReadOnlyList<ToolchainInfo> Rows, JavaDiscoveryResult Java, AndroidSdkDiscoveryResult Android,
        IReadOnlyList<SigningIdentity> Identities, IReadOnlyList<IosProvisioningProfile> Profiles);

    private async Task<MobileDetection> DetectMobileAsync(AppSettings settings, Task<XcodeStatus>? xcodeTask, CancellationToken ct)
    {
        var list = new List<ToolchainInfo>();
        var java = await DiscoverJavaAsync(settings.JavaHome, ct).ConfigureAwait(false);
        var jdkRow = JdkRow(java);
        list.Add(jdkRow);
        var android = AndroidSdkDiscovery.Discover(new AndroidSdkDiscovery.Context { SavedRoot = settings.AndroidSdkPath, Platform = Host.Platform });
        list.AddRange(AndroidSdkDiscovery.Rows(android));
        list.AddRange(DetectNativeAndroidTools(android.Problem is null ? android.Root : null));
        list.Add(await DetectBundletoolAsync(jdkRow, ct).ConfigureAwait(false));
        var xcode = xcodeTask is null ? null : await xcodeTask.ConfigureAwait(false);
        var (appleRows, identities, profiles) = await DetectAppleMobileAsync(xcode, ct).ConfigureAwait(false);
        list.AddRange(appleRows);
        list.Add(DetectFlutter());
        list.Add(await DetectMauiWorkloadsAsync(ct).ConfigureAwait(false));
        return new MobileDetection(list, java, android, identities, profiles);
    }

    // ---------- JDK ----------

    private const string JdkHint = "Install a JDK 17+ — Android Studio includes one — or choose an installed JDK with Browse… (Toolchains › Java). HCG never installs Java.";

    /// <summary>Discovers JDKs without the login shell (works for Finder launches). See <see cref="JavaDiscovery"/>.</summary>
    public async Task<JavaDiscoveryResult> DiscoverJavaAsync(string? savedHome, CancellationToken ct)
    {
        var discovery = new JavaDiscovery(runner);
        var registered = Host.Platform == TargetPlatform.MacOS ? await discovery.RegisteredHomesAsync(ct).ConfigureAwait(false) : [];
        return await discovery.DiscoverAsync(new JavaDiscovery.Context
        {
            SavedHome = savedHome, Platform = Host.Platform, RegisteredHomes = registered, PathJava = locator.Find("java"),
        }, ct).ConfigureAwait(false);
    }

    /// <summary>The JDK row: only a JDK that ran and reported a version (with javac) is Installed.</summary>
    public static ToolchainInfo JdkRow(JavaDiscoveryResult java)
    {
        const string name = "JDK (Java)";
        var others = java.Candidates.Select(c => c.IsUsable
            ? $"Java {c.Version} — {c.Home} ({c.Source})"
            : $"Not usable: {c.Home} ({c.Source}) — {c.Problem}").ToList();
        var sel = java.Selected;
        if (sel is null)
        {
            var rejected = java.Candidates.Where(c => !c.IsUsable).ToList();
            return new ToolchainInfo
            {
                Id = ToolchainIds.Jdk, DisplayName = name, Category = AndroidCat, Status = ToolchainStatus.Missing, AdditionalVersions = others,
                Detail = rejected.Count == 0 ? "No JDK found (looked in JAVA_HOME, Android Studio, the system JVM folders, Homebrew, SDKMAN and PATH)."
                    : "No working JDK found. " + string.Join(" ", rejected.Select(c => $"{c.Source}: {c.Problem}")),
                InstallHint = JdkHint, SetupUrl = JavaDiscovery.SetupUrl,
            };
        }
        if (!sel.IsUsable)
            return new ToolchainInfo
            {
                Id = ToolchainIds.Jdk, DisplayName = name, Category = AndroidCat, ExecutablePath = sel.JavaExecutable, Source = sel.Source, Version = sel.Version,
                Status = File.Exists(sel.JavaExecutable) ? ToolchainStatus.ValidationFailed : ToolchainStatus.InvalidPath,
                Detail = sel.Problem, AdditionalVersions = others,
                InstallHint = "Choose another JDK with Browse…, or Use automatic detection.", SetupUrl = JavaDiscovery.SetupUrl,
            };
        var tooOld = sel.Major < JavaDiscovery.RecommendedMajor;
        return new ToolchainInfo
        {
            Id = ToolchainIds.Jdk, DisplayName = name, Category = AndroidCat, ExecutablePath = sel.JavaExecutable, Source = sel.Source, Version = sel.Version,
            Status = tooOld ? ToolchainStatus.IncompatibleVersion : ToolchainStatus.Installed,
            Detail = tooOld
                ? $"Java {sel.Major} works, but the Android Gradle plugin 8+ needs JDK 17 or newer. Each project's readiness checks its own Gradle/AGP versions."
                : $"JDK {sel.Major} with javac ({sel.Source}). Compatibility with each project's Gradle/AGP is checked per project.",
            AdditionalVersions = others, InstallHint = tooOld ? JdkHint : null, SetupUrl = JavaDiscovery.SetupUrl,
        };
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

    /// <summary>Kept for callers that list default SDK folders.</summary>
    public static IEnumerable<string> AndroidSdkCandidates(Func<string, string?> getEnv) =>
        AndroidSdkDiscovery.Candidates(new AndroidSdkDiscovery.Context { GetEnv = getEnv }).Select(c => c.Root);

    /// <summary>NDK, SDK CMake and adb: needed for native (C/C++) Android builds and for launching on emulators/devices.</summary>
    private static IEnumerable<ToolchainInfo> DetectNativeAndroidTools(string? sdk)
    {
        const string ndkHint = "Install the NDK (Side by side) and CMake from Android Studio → SDK Manager → SDK Tools, or: sdkmanager \"ndk;27.2.12479018\" \"cmake;3.22.1\". HCG does not install them.";
        var ndks = new List<(string Version, string Path)>();
        if (sdk is not null)
            foreach (var v in SortedVersionDirs(Path.Combine(sdk, "ndk"), ""))
                ndks.Add((v, Path.Combine(sdk, "ndk", v)));
        foreach (var env in new[] { "ANDROID_NDK_HOME", "ANDROID_NDK_ROOT", "ANDROID_NDK_LATEST_HOME" })
            if (Environment.GetEnvironmentVariable(env) is { Length: > 0 } dir && NdkRevision(dir) is { } rev && ndks.All(n => n.Version != rev))
                ndks.Add((rev, dir));
        ndks = ndks.Where(n => File.Exists(Path.Combine(n.Path, "source.properties"))).Select(n => (NdkRevision(n.Path) ?? n.Version, n.Path)).ToList();
        yield return ndks.Count > 0
            ? new ToolchainInfo
            {
                Id = ToolchainIds.AndroidNdk, DisplayName = "Android NDK", Category = AndroidCat, Status = ToolchainStatus.Installed,
                Version = ndks[0].Version, ExecutablePath = ndks[0].Path, AdditionalVersions = ndks.Select(n => n.Version).ToList(),
                Detail = $"{ndks.Count} NDK version(s)",
            }
            : Missing(ToolchainIds.AndroidNdk, "Android NDK", AndroidCat, ndkHint);
        var cmakes = sdk is null ? [] : SortedVersionDirs(Path.Combine(sdk, "cmake"), "")
            .Where(v => File.Exists(Path.Combine(sdk, "cmake", v, "bin", OperatingSystem.IsWindows() ? "cmake.exe" : "cmake"))).ToList();
        yield return cmakes.Count > 0
            ? new ToolchainInfo
            {
                Id = ToolchainIds.AndroidCmake, DisplayName = "CMake (Android SDK)", Category = AndroidCat, Status = ToolchainStatus.Installed,
                Version = cmakes[0], ExecutablePath = Path.Combine(sdk!, "cmake", cmakes[0], "bin"), AdditionalVersions = cmakes,
            }
            : Missing(ToolchainIds.AndroidCmake, "CMake (Android SDK)", AndroidCat, ndkHint);
        var adb = sdk is null ? null : Path.Combine(sdk, "platform-tools", OperatingSystem.IsWindows() ? "adb.exe" : "adb");
        yield return adb is not null && File.Exists(adb)
            ? new ToolchainInfo { Id = ToolchainIds.Adb, DisplayName = "adb (platform-tools)", Category = AndroidCat, Status = ToolchainStatus.Installed, ExecutablePath = adb }
            : Missing(ToolchainIds.Adb, "adb (platform-tools)", AndroidCat, "Install Android SDK Platform-Tools with the SDK Manager.");
    }

    /// <summary>Pkg.Revision from an NDK's source.properties, e.g. "27.2.12479018".</summary>
    internal static string? NdkRevision(string ndkDir)
    {
        var props = Path.Combine(ndkDir, "source.properties");
        if (!File.Exists(props)) return null;
        foreach (var line in File.ReadLines(props))
            if (line.StartsWith("Pkg.Revision", StringComparison.Ordinal) && line.IndexOf('=') is var eq and > 0)
                return line[(eq + 1)..].Trim();
        return null;
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

    private async Task<(IReadOnlyList<ToolchainInfo> Rows, IReadOnlyList<SigningIdentity> Identities, IReadOnlyList<IosProvisioningProfile> Profiles)> DetectAppleMobileAsync(XcodeStatus? xcode, CancellationToken ct)
    {
        if (Host.Platform != TargetPlatform.MacOS)
            return ([
                Unsupported(ToolchainIds.IosSdk, "iOS SDK", AppleCat, "iOS apps are built on a Mac with Xcode. Nothing to set up on this computer."),
                Unsupported(ToolchainIds.AppleSigningIdentities, "Code-signing identities", AppleCat, "Read from the keychain on a Mac."),
                Unsupported(ToolchainIds.ProvisioningProfiles, "Provisioning profiles", AppleCat, "Read on a Mac."),
            ], [], []);
        var result = new List<ToolchainInfo> { IosSdkRow(xcode) };

        const string security = "/usr/bin/security";
        IReadOnlyList<SigningIdentity> identities = [];
        try
        {
            var (r, stdout, stderr) = await runner.CaptureAsync(new ProcessSpec { Executable = security, Arguments = ["find-identity", "-v", "-p", "codesigning"], Timeout = ProbeTimeout, Environment = ProbeEnvironment() }, ct).ConfigureAwait(false);
            identities = r.Succeeded ? ParseSigningIdentityRecords(stdout) : [];
            var dev = identities.Count(i => i.Kind == AppleCertificateKind.Development);
            var dist = identities.Count(i => i.Kind == AppleCertificateKind.AppleDistribution);
            result.Add(!r.Succeeded
                ? Failed(ToolchainIds.AppleSigningIdentities, "Code-signing identities", AppleCat, security, $"security find-identity failed: {FirstLine(stderr + stdout)}", null)
                : identities.Count > 0
                    ? new ToolchainInfo
                    {
                        Id = ToolchainIds.AppleSigningIdentities, DisplayName = "Code-signing identities", Category = AppleCat, Status = ToolchainStatus.Installed,
                        Version = $"{dev} development, {dist} distribution", ExecutablePath = security, AdditionalVersions = identities.Select(i => i.Name).ToList(),
                        Detail = dist == 0 ? "Development certificates only: enough for running on your own devices, NOT for App Store / Ad Hoc / Enterprise distribution." : string.Join("; ", identities.Take(3).Select(i => i.Name)),
                    }
                    : new ToolchainInfo
                    {
                        Id = ToolchainIds.AppleSigningIdentities, DisplayName = "Code-signing identities", Category = AppleCat, Status = ToolchainStatus.Missing,
                        Detail = "None in the keychain. Not needed for Simulator builds or unsigned archives.",
                        InstallHint = "Add your Apple Developer account in Xcode › Settings › Accounts and create certificates there.",
                        SetupUrl = "https://developer.apple.com/help/account/certificates/create-developer-id-certificates",
                    });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result.Add(Failed(ToolchainIds.AppleSigningIdentities, "Code-signing identities", AppleCat, security, ex.Message, null));
        }

        var profiles = IosProfiles.Read(IosProfiles.Folders());
        var now = time.GetUtcNow();
        var valid = profiles.Where(p => !p.IsExpired(now)).ToList();
        result.Add(valid.Count > 0
            ? new ToolchainInfo
            {
                Id = ToolchainIds.ProvisioningProfiles, DisplayName = "Provisioning profiles", Category = AppleCat, Status = ToolchainStatus.Installed,
                Version = $"{valid.Count} valid", AdditionalVersions = valid.Select(p => $"{p.Name} — {p.Kind}, {p.ApplicationIdentifier}, expires {p.Expiration:yyyy-MM-dd}").ToList(),
                Detail = string.Join(", ", valid.GroupBy(p => p.Kind).Select(g => $"{g.Count()} {g.Key}")) + (profiles.Count > valid.Count ? $"; {profiles.Count - valid.Count} expired" : ""),
            }
            : new ToolchainInfo
            {
                Id = ToolchainIds.ProvisioningProfiles, DisplayName = "Provisioning profiles", Category = AppleCat, Status = ToolchainStatus.Missing,
                Detail = (profiles.Count > 0 ? $"{profiles.Count} expired profile(s) only. " : "None installed. ") +
                         "Not needed for Simulator builds or unsigned archives; Xcode automatic signing can create development profiles.",
                InstallHint = "For manual signing, download profiles at developer.apple.com › Profiles (Xcode › Settings › Accounts › Download Manual Profiles).",
                SetupUrl = "https://developer.apple.com/help/account/provisioning-profiles/create-a-development-provisioning-profile",
            });
        return (result, identities, profiles);
    }

    /// <summary>
    /// xcodebuild of the selected developer directory. The iOS SDK row's ExecutablePath is what the Xcode planner runs,
    /// so it must be the tool, never the directory.
    /// </summary>
    public static string XcodebuildFor(string? developerDirectory) =>
        developerDirectory is { Length: > 0 } dev ? Path.Combine(dev, "usr", "bin", "xcodebuild") : "/usr/bin/xcodebuild";

    /// <summary>The iOS SDK row follows the Xcode diagnosis: with the Command Line Tools selected it is "Setup required: select Xcode", not "Missing".</summary>
    public static ToolchainInfo IosSdkRow(XcodeStatus? xcode)
    {
        const string name = "iOS SDK";
        if (xcode is null)
            return new ToolchainInfo { Id = ToolchainIds.IosSdk, DisplayName = name, Category = AppleCat, Status = ToolchainStatus.Missing, Detail = "Xcode was not examined." };
        var device = xcode.Sdks.Where(s => s.StartsWith("iphoneos ", StringComparison.Ordinal)).ToList();
        var sim = xcode.Sdks.Where(s => s.StartsWith("iphonesimulator ", StringComparison.Ordinal)).ToList();
        if (xcode.State is XcodeState.NotInstalled)
            return new ToolchainInfo
            {
                Id = ToolchainIds.IosSdk, DisplayName = name, Category = AppleCat, Status = ToolchainStatus.Missing,
                Detail = "The iOS SDK comes with Xcode, which is not installed.", InstallHint = xcode.CorrectiveAction, SetupUrl = XcodeDiscovery.AppStoreUrl,
            };
        if (xcode.State is not (XcodeState.Ready or XcodeState.SdkMissing))
            return new ToolchainInfo
            {
                Id = ToolchainIds.IosSdk, DisplayName = name, Category = AppleCat, Status = ToolchainStatus.SetupRequired,
                Detail = $"Xcode must be ready first: {xcode.Diagnosis}", InstallHint = xcode.CorrectiveAction,
            };
        return device.Count > 0 || sim.Count > 0
            ? new ToolchainInfo
            {
                Id = ToolchainIds.IosSdk, DisplayName = name, Category = AppleCat, Status = ToolchainStatus.Installed,
                Version = (device.FirstOrDefault() ?? sim[0]).Split(' ')[1], ExecutablePath = XcodebuildFor(xcode.ActiveDeveloperDirectory), AdditionalVersions = [.. device, .. sim],
                Detail = device.Count > 0 && sim.Count > 0 ? "Device and Simulator SDKs" : device.Count > 0 ? "Device SDK only (no Simulator SDK: install it in Xcode › Settings › Components)" : "Simulator SDK only",
                Source = "Selected Xcode",
            }
            : new ToolchainInfo
            {
                Id = ToolchainIds.IosSdk, DisplayName = name, Category = AppleCat, Status = ToolchainStatus.SetupRequired,
                Detail = "The selected Xcode has no iOS platform installed.", InstallHint = "Install it in Xcode › Settings › Components › iOS, then press Refresh.",
                SetupUrl = "https://developer.apple.com/documentation/xcode/downloading-and-installing-additional-xcode-components",
            };
    }

    /// <summary>Identities (SHA-1, name, certificate type, team) from <c>security find-identity -v -p codesigning</c>.</summary>
    public static IReadOnlyList<SigningIdentity> ParseSigningIdentityRecords(string output) =>
        SigningIdentityRecordRegex().Matches(output).Select(m => m.Groups["name"].Value is var n
                ? new SigningIdentity(m.Groups["sha"].Value, n, AppleCertificateNames.Classify(n), TeamRegex().Match(n) is { Success: true } t ? t.Groups[1].Value : null)
                : null!)
            .DistinctBy(i => i.Sha1).ToList();

    [GeneratedRegex(@"^\s*\d+\)\s+(?<sha>[0-9A-F]{40})\s+""(?<name>[^""]+)""", RegexOptions.Multiline)]
    private static partial Regex SigningIdentityRecordRegex();

    [GeneratedRegex(@"\(([A-Z0-9]{10})\)\s*$")]
    private static partial Regex TeamRegex();

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
