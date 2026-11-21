using System.Text.RegularExpressions;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.Packaging.Jailbreak;

/// <summary>What Unleashify can make from an iOS project. Only modes with a working build path can be built.</summary>
public enum UnleashifyMode
{
    /// <summary>The app, unchanged, as a rootless jailbreak app package (/var/jb/Applications).</summary>
    JailbreakApp,
    /// <summary>A tweak (a dynamic library loaded into other processes by the jailbreak's injection system).</summary>
    JailbreakTweak,
    /// <summary>The app and a tweak in one package.</summary>
    AppPlusTweak,
}

public sealed record UnleashifyModeOption(UnleashifyMode Mode, string Label, string Description, bool Available, string? Reason);

public sealed record DeviceTarget(string Name, string IosVersion);

public sealed record DeviceCompatibility(DeviceTarget Device, bool Compatible, string Detail);

public enum ConflictState
{
    /// <summary>Nothing is known about what is installed on the test phone.</summary>
    NotChecked,
    /// <summary>No other build of the app is installed (from the phone's list, or confirmed by the tester).</summary>
    Clear,
    /// <summary>Another build of the app is installed and must be removed first.</summary>
    Conflict,
}

public sealed record InstalledEntry(string Id, string? Version, string? Path);

public sealed record ConflictReport(ConflictState State, string Summary, IReadOnlyList<string> Findings, IReadOnlyList<InstalledEntry> Seen);

/// <summary>
/// Unleashify: the iOS app a project builds, packaged unchanged as a rootless jailbreak app (the existing
/// <see cref="RootlessAppWorkflow"/>), plus the checks that decide whether the package is ready for a device test:
/// which modes exist, which test devices can run it, and whether another build of the app is already installed.
/// Nothing here installs, removes or publishes anything.
/// </summary>
public static partial class Unleashify
{
    public static IReadOnlyList<UnleashifyModeOption> Modes(ProjectAnalysis? analysis)
    {
        var ios = analysis?.Mobile?.Ios;
        var hasApp = ios?.Targets.Any(t => t.IsApplication) == true || analysis?.Mobile?.ArtifactPath is not null;
        var theos = analysis?.Native?.Projects.FirstOrDefault(p => p.Kind == NativeProjectKind.Theos);
        return
        [
            new(UnleashifyMode.JailbreakApp, "Jailbreak App",
                "The app as it is (same code, bundle ID, deployment target and features), packaged as a rootless app at /var/jb/Applications and registered with uicache.",
                hasApp, hasApp ? null : "This project has no iOS application target. Choose an existing device build (.app, .xcarchive or .ipa) instead."),
            new(UnleashifyMode.JailbreakTweak, "Jailbreak Tweak",
                "A tweak loaded into other processes by the jailbreak (Theos / ElleKit).",
                false, theos is not null
                    ? $"This folder has a Theos project ({theos.DisplayDirectory}): build its .deb in New Build (Build route: Jailbreak package). Unleashify does not turn an app into a tweak."
                    : "Not available: this project has no tweak, and Unleashify does not turn an app into a tweak. Tweaks are built from a Theos project in New Build."),
            new(UnleashifyMode.AppPlusTweak, "App + Tweak",
                "The app and a companion tweak in one package.",
                false, "Not available in this version: Maker does not combine an app and a tweak in one package. Build and ship them as separate packages."),
        ];
    }

    /// <summary>
    /// Whether each device can install and run a package whose binaries declare <paramref name="minimumOs"/>. A device
    /// below it cannot be made compatible by package metadata: the binaries themselves require that iOS.
    /// </summary>
    public static IReadOnlyList<DeviceCompatibility> Compatibility(string? minimumOs, IEnumerable<DeviceTarget> devices) =>
        devices.Select(d =>
        {
            if (minimumOs is null) return new DeviceCompatibility(d, false, "Unknown: the app's minimum iOS could not be read.");
            if (RootlessAppDebValidator.CompareOs(d.IosVersion, minimumOs) < 0)
                return new DeviceCompatibility(d, false,
                    $"Not compatible: the app's binaries require iOS {minimumOs} (the project's deployment target, recorded in every Mach-O) and this device runs iOS {d.IosVersion}. " +
                    $"The package declares firmware (>= {minimumOs}), so the package manager refuses it; changing that metadata would not help, because the code itself needs iOS {minimumOs} and the system refuses to load it on iOS {d.IosVersion}. " +
                    (RootlessAppDebValidator.CompareOs(d.IosVersion, "15.0") < 0 ? $"Rootless jailbreaks (/var/jb) exist only for iOS 15 and later, so a rootless package does not apply to iOS {d.IosVersion} at all. " : "") +
                    $"Supporting this device needs a separate build of the app whose deployment target and code support iOS {d.IosVersion} — a source change in the app, not a packaging option.");
            return new DeviceCompatibility(d, true, $"Compatible: iOS {d.IosVersion} meets the app's minimum iOS {minimumOs}" +
                (RootlessAppDebValidator.CompareOs(d.IosVersion, "15.0") < 0 ? ", but rootless jailbreaks exist only for iOS 15 and later." : " (rootless jailbreak, e.g. Dopamine)."));
        }).ToList();

    /// <summary>
    /// Compares the package with what is installed on the test phone. <paramref name="listing"/> is what the phone
    /// reports — <c>uicache -l</c> (bundle ID : path), <c>dpkg -l</c> or <c>dpkg-query -W</c> — pasted as text. Without a
    /// listing, only the tester's explicit confirmation clears the check; Maker never assumes the phone is clean.
    /// </summary>
    public static ConflictReport CheckInstalled(string bundleId, string packageId, string version, string installPath, string? listing, bool testerConfirmedNone)
    {
        var seen = ParseListing(listing);
        var findings = new List<string>();
        var conflict = false;
        foreach (var e in seen.Where(e => e.Id == bundleId))
        {
            if (e.Path is { } p && Normalize(p) == Normalize(installPath))
                findings.Add($"{bundleId} is registered at {p}: this package's own location (an earlier install of it). Installing replaces it.");
            else
            {
                conflict = true;
                var where = e.Path is null ? "" : $" at {e.Path}";
                var kind = e.Path is null ? "" : e.Path.Contains("/containers/Bundle/Application/", StringComparison.Ordinal)
                    ? " (a sideloaded / development-signed build, e.g. installed from Xcode or an IPA)"
                    : e.Path.Contains("/var/jb/", StringComparison.Ordinal) ? " (another jailbreak package)" : "";
                findings.Add($"CONFLICT: another build with bundle ID {bundleId} is installed{where}{kind}. Two apps with one bundle ID cannot both be registered: " +
                             "remove that build first (its own data goes with it), then install this package.");
            }
        }
        foreach (var e in seen.Where(e => e.Id == packageId && e.Version is not null))
        {
            var cmp = DebianVersion.Compare(e.Version!, version);
            if (cmp > 0)
            {
                conflict = true;
                findings.Add($"CONFLICT: {packageId} {e.Version} is installed, newer than {version}. Installing would be a downgrade; remove it first or test a newer version.");
            }
            else findings.Add(cmp == 0 ? $"{packageId} {version} is already installed: this is a reinstall of the same version." : $"{packageId} {e.Version} is installed: installing {version} upgrades it.");
        }
        foreach (var e in seen.Where(e => e.Id != bundleId && e.Id != packageId && e.Id.Contains("sentinel", StringComparison.OrdinalIgnoreCase)))
            findings.Add($"Also installed: {e.Id}{(e.Version is null ? "" : " " + e.Version)}{(e.Path is null ? "" : " at " + e.Path)} — a different identifier; check it is not another build of the same app.");

        if (conflict)
            return new ConflictReport(ConflictState.Conflict, "Conflict with an installed build: not ready for device testing until it is removed.", findings, seen);
        if (seen.Count > 0)
            return new ConflictReport(ConflictState.Clear, $"No conflicting build in the phone's list ({seen.Count} entries read).", findings, seen);
        if (testerConfirmedNone)
            return new ConflictReport(ConflictState.Clear, "The tester confirmed that no other build of the app is installed on the test phone (not read by Maker).", findings, seen);
        return new ConflictReport(ConflictState.NotChecked,
            $"Not checked: paste the phone's `uicache -l` or `dpkg -l` output, or confirm that no other build with bundle ID {bundleId} is installed.", findings, seen);
    }

    /// <summary>Reads bundle IDs / package IDs (with a path or version when given) from uicache, dpkg and dpkg-query output.</summary>
    public static IReadOnlyList<InstalledEntry> ParseListing(string? listing)
    {
        var list = new List<InstalledEntry>();
        if (string.IsNullOrWhiteSpace(listing)) return list;
        foreach (var raw in listing.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (DpkgLine().Match(line) is { Success: true } d) { list.Add(new(d.Groups["id"].Value, d.Groups["ver"].Value, null)); continue; }
            if (UicacheLine().Match(line) is { Success: true } u) { list.Add(new(u.Groups["id"].Value, null, u.Groups["path"].Value.Trim())); continue; }
            if (QueryLine().Match(line) is { Success: true } q) { list.Add(new(q.Groups["id"].Value, q.Groups["ver"].Value, null)); continue; }
            if (BareId().Match(line) is { Success: true } b) list.Add(new(b.Groups["id"].Value, null, null));
        }
        return list.DistinctBy(e => (e.Id, e.Version, e.Path)).ToList();
    }

    private static string Normalize(string path) => path.Replace("/private/var/", "/var/", StringComparison.Ordinal).TrimEnd('/');

    // dpkg -l: "ii  com.example.pkg  1.0~beta1  iphoneos-arm64  Description"
    [GeneratedRegex(@"^[a-z]{2,3}\s+(?<id>[A-Za-z0-9][A-Za-z0-9.+-]+)\s+(?<ver>\S+)\s+\S+")]
    private static partial Regex DpkgLine();

    // uicache -l: "com.example.app : /var/jb/Applications/App.app" (also "id /path" and "id: /path")
    [GeneratedRegex(@"^(?<id>[A-Za-z0-9][A-Za-z0-9-]*(?:\.[A-Za-z0-9-]+)+)\s*:?\s+(?<path>/\S.*\.app/?)$")]
    private static partial Regex UicacheLine();

    // dpkg-query -W: "com.example.pkg<TAB>1.0"
    [GeneratedRegex(@"^(?<id>[A-Za-z0-9][A-Za-z0-9.+-]*\.[A-Za-z0-9.+-]+)\t(?<ver>\S+)$")]
    private static partial Regex QueryLine();

    [GeneratedRegex(@"^(?<id>[A-Za-z0-9][A-Za-z0-9-]*(?:\.[A-Za-z0-9-]+)+)$")]
    private static partial Regex BareId();
}
