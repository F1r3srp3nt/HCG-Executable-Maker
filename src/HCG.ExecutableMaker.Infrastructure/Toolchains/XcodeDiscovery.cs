using System.Xml.Linq;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.Infrastructure.Toolchains;

/// <summary>
/// Finds installed Xcode applications and diagnoses why Xcode is or isn't usable for HCG. Read-only: the system-wide
/// developer directory (xcode-select) is never changed; an HCG-specific choice is passed to HCG's processes as
/// DEVELOPER_DIR (see <see cref="DeveloperDirectorySelection"/>).
/// </summary>
public static class XcodeDiscovery
{
    public const string AppStoreUrl = "https://apps.apple.com/app/xcode/id497799835";

    /// <summary>Default places Xcode is installed (plus Spotlight results supplied by the caller).</summary>
    public static IEnumerable<string> DefaultSearchFolders()
    {
        yield return "/Applications";
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length > 0) yield return Path.Combine(home, "Applications");
    }

    /// <summary>
    /// Xcode apps in the given folders (depth 1) and extra candidate paths, newest version first. Several names for the
    /// same Xcode (symbolic links such as Xcode.app → Xcode_26.6.app) are listed once; the name that is active
    /// (<paramref name="activeDeveloperDirectory"/>) is kept, otherwise the real (non-link) path.
    /// </summary>
    public static IReadOnlyList<XcodeInstallation> FindInstallations(IEnumerable<string> folders, IEnumerable<string>? extraCandidates = null, string? activeDeveloperDirectory = null)
    {
        var candidates = new List<string>();
        foreach (var folder in folders.Where(Directory.Exists))
        {
            try { candidates.AddRange(Directory.EnumerateDirectories(folder, "*.app").Where(a => Path.GetFileName(a).StartsWith("Xcode", StringComparison.OrdinalIgnoreCase))); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        candidates.AddRange(extraCandidates ?? []);
        return candidates.Select(c => Path.GetFullPath(c).TrimEnd('/', Path.DirectorySeparatorChar)).Distinct(StringComparer.Ordinal)
            .Select(Read).OfType<XcodeInstallation>()
            .GroupBy(x => RealPath(x.AppPath), StringComparer.Ordinal)
            .Select(g => g.FirstOrDefault(x => activeDeveloperDirectory is not null && SamePath(x.DeveloperDirectory, activeDeveloperDirectory))
                         ?? g.FirstOrDefault(x => x.AppPath == g.Key)
                         ?? g.OrderBy(x => x.AppPath, StringComparer.Ordinal).First())
            .OrderByDescending(x => System.Version.TryParse(x.Version, out var v) ? v : new System.Version(0, 0))
            .ThenBy(x => x.AppPath, StringComparer.Ordinal).ToList();
    }

    /// <summary>The app's path with a symbolic link (the .app itself) resolved.</summary>
    internal static string RealPath(string appPath)
    {
        try { return new DirectoryInfo(appPath).ResolveLinkTarget(returnFinalTarget: true)?.FullName.TrimEnd('/', Path.DirectorySeparatorChar) ?? appPath; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return appPath; }
    }

    /// <summary>Reads an Xcode.app (or null when the path is not a usable Xcode application).</summary>
    public static XcodeInstallation? Read(string appPath)
    {
        appPath = appPath.TrimEnd('/', Path.DirectorySeparatorChar);
        // …/Xcode.app/Contents/Developer → …/Xcode.app (by path components, so any separator works).
        if (Path.GetFileName(appPath) == "Developer" && Path.GetFileName(Path.GetDirectoryName(appPath)) == "Contents")
            appPath = Path.GetDirectoryName(Path.GetDirectoryName(appPath)!)!;
        var info = Path.Combine(appPath, "Contents", "Info.plist");
        var dev = Path.Combine(appPath, "Contents", "Developer");
        if (!File.Exists(info) || !File.Exists(Path.Combine(dev, "usr", "bin", "xcodebuild"))) return null;
        var plist = ReadPlist(info);
        if (plist.GetValueOrDefault("CFBundleIdentifier") is { } id && id != "com.apple.dt.Xcode") return null;
        var version = ReadPlist(Path.Combine(appPath, "Contents", "version.plist"));
        // Xcode's Info.plist is a binary plist (not read here); version.plist is XML and carries the same version.
        // Each installation's own SDK ranges, read from disk: they name the Xcodes a deployment target actually needs.
        var ranges = ReadRanges(dev);
        return new XcodeInstallation(appPath, dev,
            plist.GetValueOrDefault("CFBundleShortVersionString") ?? version.GetValueOrDefault("CFBundleShortVersionString"),
            version.GetValueOrDefault("ProductBuildVersion"))
        {
            IosRange = ranges.Device,
            SimulatorRange = ranges.Simulator,
            RangeDiagnostics = ranges.Diagnostics,
        };
    }

    private static Dictionary<string, string> ReadPlist(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(path)) return result;
            var settings = new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Ignore, XmlResolver = null };
            using var reader = System.Xml.XmlReader.Create(path, settings);
            var dict = XDocument.Load(reader).Root?.Element("dict");
            var children = dict?.Elements().ToList() ?? [];
            for (var i = 0; i + 1 < children.Count; i++)
                if (children[i].Name == "key" && children[i + 1].Name == "string") result[children[i].Value] = children[++i].Value;
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException or UnauthorizedAccessException) { }
        return result;
    }

    /// <summary>Probe results that <see cref="Diagnose"/> turns into a state and an exact corrective action.</summary>
    public sealed record ProbeResults(
        string? ActiveDeveloperDirectory,
        DeveloperDirectorySource Source,
        IReadOnlyList<XcodeInstallation> Installed,
        int VersionExitCode,
        string VersionOutput,
        string VersionError,
        int? FirstLaunchExitCode,
        IReadOnlyList<string> Sdks);

    /// <summary>
    /// The iOS deployment range of a developer directory's iPhoneOS SDK, from SDKSettings.json
    /// (SupportedTargets.iphoneos.MinimumDeploymentTarget / MaximumDeploymentTarget). Read only; nothing is run.
    /// </summary>
    public static IosDeploymentRange? ReadIosDeploymentRange(string? developerDirectory) => ReadDeploymentRange(developerDirectory, "iphoneos");

    /// <summary>The iPhoneSimulator SDK's own range (SupportedTargets.iphonesimulator); a Simulator build is judged by it.</summary>
    public static IosDeploymentRange? ReadIosSimulatorDeploymentRange(string? developerDirectory) => ReadDeploymentRange(developerDirectory, "iphonesimulator");

    /// <summary>Reads the deployment range of an SDK ("iphoneos" or "iphonesimulator") from its SDKSettings.json. Nothing is run.</summary>
    public static IosDeploymentRange? ReadDeploymentRange(string? developerDirectory, string sdk) => ReadDeploymentRange(developerDirectory, sdk, out _);

    /// <summary>
    /// As <see cref="ReadDeploymentRange(string?, string)"/>, and when the range cannot be read, why: the SDK folder or
    /// SDKSettings.json is missing, the file could not be read, or it has no SupportedTargets entry for the SDK. That is an
    /// inspection failure, reported as such — never as an incompatibility.
    /// </summary>
    public static IosDeploymentRange? ReadDeploymentRange(string? developerDirectory, string sdk, out string? diagnostic)
    {
        diagnostic = null;
        if (developerDirectory is null) { diagnostic = "no developer directory is selected"; return null; }
        var platform = sdk == "iphonesimulator" ? "iPhoneSimulator" : "iPhoneOS";
        var sdks = Path.Combine(developerDirectory, "Platforms", platform + ".platform", "Developer", "SDKs");
        if (!Directory.Exists(sdks)) { diagnostic = $"{sdks} does not exist (the {platform} platform is not installed in this Xcode)"; return null; }
        var settings = new[] { Path.Combine(sdks, platform + ".sdk", "SDKSettings.json") }
            .Concat(Directory.GetDirectories(sdks, platform + "*.sdk").Select(d => Path.Combine(d, "SDKSettings.json")))
            .FirstOrDefault(File.Exists);
        if (settings is null) { diagnostic = $"no {platform}*.sdk/SDKSettings.json in {sdks}"; return null; }
        string json;
        try { json = File.ReadAllText(settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { diagnostic = $"{settings} could not be read: {ex.Message}"; return null; }
        var range = ParseIosDeploymentRange(json, sdk, out var parseError);
        if (range is null) diagnostic = $"{settings}: {parseError}";
        return range;
    }

    public static IosDeploymentRange? ParseIosDeploymentRange(string json, string sdk = "iphoneos") => ParseIosDeploymentRange(json, sdk, out _);

    public static IosDeploymentRange? ParseIosDeploymentRange(string json, string sdk, out string? error)
    {
        error = null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("SupportedTargets", out var targets) || !targets.TryGetProperty(sdk, out var ios))
            {
                error = $"no SupportedTargets.{sdk} entry";
                return null;
            }
            string? S(System.Text.Json.JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
            var min = S(ios, "MinimumDeploymentTarget");
            var max = S(ios, "MaximumDeploymentTarget");
            if (min is null || max is null) error = $"SupportedTargets.{sdk} has no Minimum/MaximumDeploymentTarget";
            return min is null || max is null ? null : new IosDeploymentRange(min, max, S(ios, "RecommendedDeploymentTarget"), S(root, "Version"));
        }
        catch (System.Text.Json.JsonException ex) { error = "not valid JSON: " + ex.Message; return null; }
    }

    /// <summary>Both SDK ranges of a developer directory, with the reader's diagnostic for each range it could not read.</summary>
    public static (IosDeploymentRange? Device, IosDeploymentRange? Simulator, IReadOnlyDictionary<string, string> Diagnostics) ReadRanges(string? developerDirectory)
    {
        var diagnostics = new Dictionary<string, string>();
        var device = ReadDeploymentRange(developerDirectory, "iphoneos", out var d);
        if (d is not null) diagnostics["iphoneos"] = d;
        var simulator = ReadDeploymentRange(developerDirectory, "iphonesimulator", out var s);
        if (s is not null) diagnostics["iphonesimulator"] = s;
        return (device, simulator, diagnostics);
    }

    public static XcodeStatus Diagnose(ProbeResults p)
    {
        var active = p.ActiveDeveloperDirectory;
        var activeIsClt = active?.Contains("CommandLineTools", StringComparison.Ordinal) == true;
        var activeXcode = active is null ? null : p.Installed.FirstOrDefault(x => SamePath(x.DeveloperDirectory, active));
        var (version, build) = ParseVersion(p.VersionOutput);
        var raw = FirstLine(p.VersionError) is { Length: > 0 } e ? e : null;
        var best = p.Installed.FirstOrDefault();
        var hcgSelect = best is null ? "" : $"Select {best.Display.Split(" — ")[0]} for HCG (Toolchains › Xcode › Select Xcode). This applies to HCG only; the system setting stays as it is. To change it for the whole Mac instead: sudo xcode-select --switch \"{best.DeveloperDirectory}\"";
        XcodeStatus S(XcodeState state, string diagnosis, string action) => new()
        {
            State = state, ActiveDeveloperDirectory = active, Source = p.Source, Installed = p.Installed, Sdks = p.Sdks,
            VerifiedVersion = state is XcodeState.Ready or XcodeState.SdkMissing or XcodeState.FirstLaunchRequired ? version : null,
            VerifiedBuild = state is XcodeState.Ready or XcodeState.SdkMissing or XcodeState.FirstLaunchRequired ? build : null,
            Diagnosis = diagnosis, CorrectiveAction = action, RawError = raw,
        };

        if (p.Source == DeveloperDirectorySource.HcgSelection && (active is null || !Directory.Exists(active)))
            return S(XcodeState.ValidationFailed, $"The Xcode selected for HCG no longer exists ({active}).",
                best is not null ? hcgSelect : "Choose another Xcode with Select Xcode, or clear the HCG selection to use the system setting.");

        if (active is null || activeIsClt || (p.VersionExitCode != 0 && raw?.Contains("command line tools instance", StringComparison.OrdinalIgnoreCase) == true))
        {
            var where = active is null ? "No developer directory is selected." : $"The active developer directory is the Command Line Tools ({active}).";
            return best is not null
                ? S(XcodeState.InstalledNotSelected,
                    $"{where} {(p.Installed.Count == 1 ? "Xcode is" : $"{p.Installed.Count} Xcode versions are")} installed but not selected: {string.Join("; ", p.Installed.Select(x => x.Display))}. " +
                    "Command Line Tools are enough for macOS Swift package builds; iOS builds, Simulator and App Store validation need Xcode.", hcgSelect)
                : S(XcodeState.NotInstalled,
                    $"{where} No Xcode application was found in /Applications or ~/Applications (or by Spotlight). Command Line Tools are enough for macOS Swift package builds.",
                    $"For iOS builds, Simulator and App Store validation: install Xcode from the Mac App Store ({AppStoreUrl}), open it once, then press Recheck. If Xcode is installed elsewhere, use Browse… to select it.");
        }

        if (p.VersionExitCode != 0)
        {
            if ((raw ?? "").Contains("license", StringComparison.OrdinalIgnoreCase))
                return S(XcodeState.LicenseNotAccepted, $"Xcode at {active} has not had its license accepted, so its tools refuse to run.",
                    $"Open Xcode once and accept the license, or run in Terminal: sudo \"{active}/usr/bin/xcodebuild\" -license accept — then press Recheck.");
            return S(XcodeState.ValidationFailed, $"xcodebuild -version failed for {active} (exit code {p.VersionExitCode}).",
                "Open this Xcode once to finish setting it up, or select another Xcode. The tool's own error is shown below.");
        }
        if (p.FirstLaunchExitCode is not null and not 0)
            return S(XcodeState.FirstLaunchRequired, $"Xcode {version} at {active} has not finished its first-launch setup (required components are not installed).",
                $"Open Xcode and let it install its components, or run in Terminal: sudo \"{active}/usr/bin/xcodebuild\" -runFirstLaunch — then press Recheck.");
        if (p.Sdks.Count == 0)
            return S(XcodeState.SdkMissing, $"Xcode {version} at {active} reports no SDKs.",
                "Open Xcode › Settings › Components and install the macOS and iOS platforms, then press Recheck.");
        var ios = p.Sdks.Any(s => s.StartsWith("iphoneos", StringComparison.Ordinal));
        return S(XcodeState.Ready,
            $"Xcode {version} ({build}) at {active}{(activeXcode is null ? "" : "")} — {p.Sdks.Count} SDK(s){(ios ? "" : "; the iOS platform is not installed")}.",
            ios ? "" : "To build for iOS: Xcode › Settings › Components › iOS, then press Recheck.");
    }

    internal static (string? Version, string? Build) ParseVersion(string output)
    {
        string? version = null, build = null;
        foreach (var line in output.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("Xcode ", StringComparison.Ordinal)) version = t[6..].Trim();
            else if (t.StartsWith("Build version ", StringComparison.Ordinal)) build = t[14..].Trim();
        }
        return (version, build);
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(a.TrimEnd('/', Path.DirectorySeparatorChar), b.TrimEnd('/', Path.DirectorySeparatorChar), StringComparison.Ordinal);

    private static string FirstLine(string s) => s.Trim().Split('\n').FirstOrDefault()?.Trim() ?? "";
}

/// <summary>
/// HCG's own developer-directory choice. Applied as DEVELOPER_DIR to HCG's process, so every tool HCG starts
/// (probes, SwiftPM evaluation, builds, codesign, simctl…) uses the same Xcode — without touching xcode-select.
/// </summary>
public sealed class DeveloperDirectorySelection(ISettingsStore settings)
{
    public const string Variable = "DEVELOPER_DIR";
    /// <summary>DEVELOPER_DIR as it was when HCG started (restored when HCG's own choice is cleared).</summary>
    public static readonly string? LaunchValue = Environment.GetEnvironmentVariable(Variable);
    private string? _selected;

    /// <summary>HCG's choice currently applied to this process (process-wide, because DEVELOPER_DIR is).</summary>
    public static string? Applied { get; private set; }

    /// <summary>Where the developer directory of HCG's child processes comes from right now.</summary>
    public static DeveloperDirectorySource ProcessSource =>
        Applied is not null ? DeveloperDirectorySource.HcgSelection
        : !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Variable)) ? DeveloperDirectorySource.LaunchEnvironment
        : DeveloperDirectorySource.XcodeSelect;

    /// <summary>The selected developer directory (…/Xcode.app/Contents/Developer), or null for the system setting.</summary>
    public string? Selected => _selected;

    public DeveloperDirectorySource CurrentSource => ProcessSource;

    /// <summary>Tests only: forget the process-wide choice (as a newly started process would).</summary>
    internal static void ResetForTests() => Applied = null;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var s = await settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        Apply(s.DeveloperDirectory);
    }

    /// <summary>Selects an Xcode (the .app or its Contents/Developer) for HCG. Returns the installation, or throws with the reason.</summary>
    public async Task<XcodeInstallation> SelectAsync(string path, CancellationToken cancellationToken = default)
    {
        var xcode = XcodeDiscovery.Read(path)
                    ?? throw new InvalidOperationException($"'{path}' is not an Xcode application (expected …/Xcode.app containing Contents/Developer/usr/bin/xcodebuild).");
        var s = await settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        await settings.SaveAsync(s with { DeveloperDirectory = xcode.DeveloperDirectory }, cancellationToken).ConfigureAwait(false);
        Apply(xcode.DeveloperDirectory);
        return xcode;
    }

    /// <summary>Clears HCG's choice: HCG's processes use the system setting (or the DEVELOPER_DIR HCG was started with).</summary>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        var s = await settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        await settings.SaveAsync(s with { DeveloperDirectory = null }, cancellationToken).ConfigureAwait(false);
        Apply(null);
    }

    private void Apply(string? developerDirectory)
    {
        _selected = string.IsNullOrWhiteSpace(developerDirectory) ? null : developerDirectory;
        Applied = _selected;
        Environment.SetEnvironmentVariable(Variable, _selected ?? LaunchValue);
    }
}
