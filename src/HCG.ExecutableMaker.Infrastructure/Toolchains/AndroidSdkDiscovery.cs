using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.Infrastructure.Toolchains;

/// <summary>
/// Finds the Android SDK from a folder saved in HCG, ANDROID_HOME / ANDROID_SDK_ROOT, or the location Android Studio
/// uses — without needing a login shell. Platforms (android-NN containing android.jar) and build-tools are reported as
/// separate components, and apksigner / zipalign / aapt2 are taken from one complete build-tools version.
/// </summary>
public static class AndroidSdkDiscovery
{
    public const string SetupUrl = "https://developer.android.com/studio/intro/update#sdk-manager";

    public sealed record Context
    {
        public string? SavedRoot { get; init; }
        public Func<string, string?> GetEnv { get; init; } = Environment.GetEnvironmentVariable;
        public string UserHome { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        public TargetPlatform Platform { get; init; } = HostInfo.CurrentPlatform;
        public string RootPrefix { get; init; } = "";
    }

    public static IReadOnlyList<(string Root, string Source)> Candidates(Context c)
    {
        var list = new List<(string, string)>();
        void Add(string? p, string source)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            p = p.TrimEnd('/', '\\');
            if (list.All(x => x.Item1 != p)) list.Add((p, source));
        }
        string R(string path) => c.RootPrefix.Length == 0 ? path : c.RootPrefix.TrimEnd('/') + path;
        if (c.SavedRoot is { Length: > 0 } saved) Add(saved, "Saved in HCG");
        Add(c.GetEnv("ANDROID_HOME"), "ANDROID_HOME");
        Add(c.GetEnv("ANDROID_SDK_ROOT"), "ANDROID_SDK_ROOT");
        switch (c.Platform)
        {
            case TargetPlatform.MacOS:
                Add(Path.Combine(c.UserHome, "Library", "Android", "sdk"), "Android Studio default location");
                break;
            case TargetPlatform.Windows:
                Add(Path.Combine(c.GetEnv("LOCALAPPDATA") ?? Path.Combine(c.UserHome, "AppData", "Local"), "Android", "Sdk"), "Android Studio default location");
                break;
            default:
                Add(Path.Combine(c.UserHome, "Android", "Sdk"), "Android Studio default location");
                Add(R("/usr/local/lib/android/sdk"), "/usr/local/lib/android/sdk");
                Add(R("/opt/android-sdk"), "/opt/android-sdk");
                break;
        }
        return list;
    }

    /// <summary>A folder is an SDK when it contains at least one SDK component folder.</summary>
    public static bool LooksLikeSdk(string root) =>
        Directory.Exists(root) && new[] { "platforms", "build-tools", "platform-tools", "cmdline-tools", "licenses" }.Any(d => Directory.Exists(Path.Combine(root, d)));

    public static AndroidSdkDiscoveryResult Discover(Context c)
    {
        var candidates = Candidates(c);
        var examined = new List<string>();
        foreach (var (root, source) in candidates)
        {
            var ok = LooksLikeSdk(root);
            examined.Add($"{root} ({source}): {(ok ? "Android SDK" : Directory.Exists(root) ? "not an Android SDK" : "does not exist")}");
            if (source == "Saved in HCG" && !ok)
                // The user chose this folder: report it, never silently replace it with another SDK.
                return new AndroidSdkDiscoveryResult
                {
                    Root = root, Source = source, SavedRoot = c.SavedRoot, ExaminedLocations = examined,
                    Problem = Directory.Exists(root)
                        ? $"{root} is not an Android SDK (no platforms, build-tools, platform-tools or cmdline-tools folder). Choose the SDK folder shown in Android Studio › Settings › Android SDK."
                        : $"{root} does not exist any more.",
                };
            if (ok) return Inspect(root, source, c) with { SavedRoot = c.SavedRoot, ExaminedLocations = examined };
        }
        return new AndroidSdkDiscoveryResult { SavedRoot = c.SavedRoot, ExaminedLocations = examined };
    }

    public static AndroidSdkDiscoveryResult Inspect(string root, string source, Context? c = null)
    {
        var windows = (c?.Platform ?? HostInfo.CurrentPlatform) == TargetPlatform.Windows;
        var platforms = VersionDirs(Path.Combine(root, "platforms"), "android-")
            .Where(p => File.Exists(Path.Combine(root, "platforms", "android-" + p, "android.jar"))).ToList();
        var buildTools = new List<AndroidBuildToolsInfo>();
        foreach (var v in VersionDirs(Path.Combine(root, "build-tools"), ""))
        {
            var dir = Path.Combine(root, "build-tools", v);
            string? Tool(string name, string winExt) { var p = Path.Combine(dir, windows ? name + winExt : name); return File.Exists(p) ? p : null; }
            buildTools.Add(new AndroidBuildToolsInfo(v, dir, Tool("apksigner", ".bat"), Tool("zipalign", ".exe"), Tool("aapt2", ".exe")));
        }
        var licenses = Path.Combine(root, "licenses");
        return new AndroidSdkDiscoveryResult
        {
            Root = root, Source = source, Platforms = platforms, BuildTools = buildTools,
            HasPlatformTools = Directory.Exists(Path.Combine(root, "platform-tools")),
            LicensesAccepted = File.Exists(Path.Combine(licenses, "android-sdk-license")),
        };
    }

    private static List<string> VersionDirs(string dir, string prefix)
    {
        if (!Directory.Exists(dir)) return [];
        static Version Key(string n)
        {
            var core = new string(n.TakeWhile(ch => char.IsDigit(ch) || ch == '.').ToArray()).TrimEnd('.');
            return Version.TryParse(core.Contains('.') ? core : core + ".0", out var v) ? v : new Version();
        }
        return Directory.GetDirectories(dir).Select(Path.GetFileName).OfType<string>()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).Select(n => n[prefix.Length..])
            .OrderByDescending(Key).ThenByDescending(n => n, StringComparer.Ordinal).ToList();
    }

    /// <summary>Toolchain rows for the Android SDK and its components, with exact setup steps.</summary>
    public static IEnumerable<ToolchainInfo> Rows(AndroidSdkDiscoveryResult r)
    {
        const string cat = "Android";
        ToolchainInfo Row(string id, string name, ToolchainStatus status, string? path = null, string? version = null, string? detail = null, string? hint = null, IReadOnlyList<string>? more = null) => new()
        {
            Id = id, DisplayName = name, Category = cat, Status = status, ExecutablePath = path, Version = version, Detail = detail,
            InstallHint = hint, AdditionalVersions = more ?? [], Source = r.Source, SetupUrl = SetupUrl,
        };
        const string managerHint = "Install it in Android Studio › Settings › Languages & Frameworks › Android SDK (SDK Manager), then press Refresh. HCG never installs SDK components.";
        if (r.Root is null || r.Problem is not null)
        {
            var status = r.Problem is not null ? ToolchainStatus.InvalidPath : ToolchainStatus.Missing;
            yield return Row(ToolchainIds.AndroidSdk, "Android SDK", status, r.Root,
                detail: r.Problem ?? "No Android SDK found. Looked in: " + string.Join("; ", r.ExaminedLocations),
                hint: "Install Android Studio (it installs the SDK in its default location), or choose an existing SDK folder with Browse…");
            foreach (var (id, name) in new[] { (ToolchainIds.AndroidPlatforms, "Android platforms"), (ToolchainIds.AndroidBuildTools, "Android build-tools"), (ToolchainIds.Apksigner, "apksigner"), (ToolchainIds.Zipalign, "zipalign"), (ToolchainIds.Aapt2, "aapt2") })
                yield return Row(id, name, ToolchainStatus.Missing, detail: "Needs an Android SDK first.");
            yield break;
        }
        yield return Row(ToolchainIds.AndroidSdk, "Android SDK", ToolchainStatus.Installed, r.Root,
            detail: $"{r.Platforms.Count} platform(s), {r.BuildTools.Count} build-tools version(s), platform-tools {(r.HasPlatformTools ? "installed" : "not installed")}" +
                    (r.LicensesAccepted ? "" : ". SDK licences have not been accepted: Gradle cannot download missing components until you run sdkmanager --licenses (HCG never accepts licences)."));
        yield return r.Platforms.Count > 0
            ? Row(ToolchainIds.AndroidPlatforms, "Android platforms", ToolchainStatus.Installed, Path.Combine(r.Root, "platforms"), "API " + r.Platforms[0], more: r.Platforms.Select(p => "API " + p).ToList())
            : Row(ToolchainIds.AndroidPlatforms, "Android platforms", ToolchainStatus.SetupRequired, Path.Combine(r.Root, "platforms"),
                detail: "No SDK platform (platforms/android-NN with android.jar) is installed.", hint: "Install an SDK Platform (SDK Platforms tab). " + managerHint);
        var bt = r.SelectedBuildTools;
        if (bt is not null)
            yield return Row(ToolchainIds.AndroidBuildTools, "Android build-tools", ToolchainStatus.Installed, bt.Directory, bt.Version,
                detail: r.BuildTools.Count > 1 ? $"Using {bt.Version} (newest complete version)" : null, more: r.BuildTools.Select(b => b.Version + (b.IsComplete ? "" : " (incomplete)")).ToList());
        else
            yield return Row(ToolchainIds.AndroidBuildTools, "Android build-tools", ToolchainStatus.SetupRequired, Path.Combine(r.Root, "build-tools"),
                detail: r.BuildTools.Count == 0 ? "No build-tools are installed." : $"Build-tools {string.Join(", ", r.BuildTools.Select(b => b.Version))} are incomplete (apksigner, zipalign or aapt2 missing).",
                hint: "Install SDK Build-Tools (SDK Tools tab). " + managerHint);
        foreach (var (id, name, path) in new[] { (ToolchainIds.Apksigner, "apksigner", bt?.Apksigner), (ToolchainIds.Zipalign, "zipalign", bt?.Zipalign), (ToolchainIds.Aapt2, "aapt2", bt?.Aapt2) })
            yield return path is not null
                ? Row(id, name, ToolchainStatus.Installed, path, bt!.Version, detail: "From build-tools " + bt.Version)
                : Row(id, name, ToolchainStatus.SetupRequired, detail: "Part of SDK Build-Tools; no complete build-tools version is installed.", hint: "Install SDK Build-Tools. " + managerHint);
    }
}
