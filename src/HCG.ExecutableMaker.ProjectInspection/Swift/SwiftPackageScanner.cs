using System.Text.RegularExpressions;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.ProjectInspection.Swift;

/// <summary>
/// Passive, non-executing discovery of Swift packages. Package.swift is Swift code; this class only READS it as text to
/// find packages and gather hints. The authoritative product list comes from evaluating the manifest on a Mac after the
/// user trusts the project (see <c>SwiftPackageEvaluator</c>).
/// </summary>
public static partial class SwiftPackageScanner
{
    public const int MaxManifestBytes = 512 * 1024;
    private const int MaxSourceFilesPerTarget = 400;
    private const int MaxSourceBytes = 1024 * 1024;

    /// <summary>Folder names that hold build output, caches or vendored dependencies — never packages to build.</summary>
    public static readonly IReadOnlySet<string> IgnoredDirectories = new HashSet<string>(StringComparer.Ordinal)
    {
        ".build", ".swiftpm", "build", "dist", "DerivedData", "Pods", "Carthage", "checkouts", "node_modules", "SourcePackages", "xcuserdata",
    };

    /// <summary>Every Package.swift outside build/cache folders, shallowest first.</summary>
    public static IReadOnlyList<string> FindManifests(SourceIndex index) =>
        index.Files.Where(f => SourceIndex.FileName(f) == "Package.swift")
            .Where(f => !f.Split('/').SkipLast(1).Any(IgnoredDirectories.Contains))
            .OrderBy(SourceIndex.Depth).ThenBy(f => f, StringComparer.Ordinal)
            .ToList();

    public static string DirectoryOf(string manifestPath) =>
        manifestPath.Contains('/') ? manifestPath[..manifestPath.LastIndexOf('/')] : "";

    public static SwiftPackageInfo Scan(SourceIndex index, string manifestPath)
    {
        var dir = DirectoryOf(manifestPath);
        var text = Read(index, manifestPath, MaxManifestBytes);
        var code = StripComments(text);
        var executableTargets = ExecutableTargetRegex().Matches(code).Select(m => m.Groups["name"].Value).Distinct().ToList();
        return new SwiftPackageInfo
        {
            Directory = dir,
            ManifestPath = manifestPath,
            ToolsVersion = ParseToolsVersion(text),
            NameHint = PackageNameRegex().Match(code) is { Success: true } n ? n.Groups[1].Value : null,
            MinimumMacOSHint = ParseMinimumMacOS(code),
            HasConditionalDeclarations = ConditionalRegex().IsMatch(code),
            HasPackageDependencies = PackageDependencyRegex().IsMatch(code),
            ExecutableProductHints = ExecutableProductRegex().Matches(code).Select(m => m.Groups["name"].Value).Distinct().ToList(),
            SourceFacts = executableTargets.Select(t => FactsFor(index, dir, t, customPath: null)).ToList(),
        };
    }

    /// <summary>
    /// Reads the <c>// swift-tools-version:</c> line. SwiftPM itself parses this comment textually (it must be the first
    /// line), so this value is reliable without running the manifest.
    /// </summary>
    public static string? ParseToolsVersion(string manifest)
    {
        var first = manifest.TrimStart('\uFEFF').Split('\n', 2)[0];
        var m = ToolsVersionRegex().Match(first);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>".macOS(.v14)" → "14.0"; ".macOS(.v10_15)" → "10.15"; ".macOS(\"13.3\")" → "13.3".</summary>
    public static string? ParseMinimumMacOS(string code)
    {
        var m = MacOSPlatformRegex().Match(code);
        if (!m.Success) return null;
        if (m.Groups["str"].Success) return m.Groups["str"].Value;
        var major = m.Groups["major"].Value;
        return m.Groups["minor"].Success ? $"{major}.{m.Groups["minor"].Value}" : $"{major}.0";
    }

    /// <summary>Static facts about one executable target's sources.</summary>
    public static SwiftTargetSourceFacts FactsFor(SourceIndex index, string packageDir, string target, string? customPath)
    {
        var relTarget = customPath ?? $"Sources/{target}";
        var rootDir = Join(packageDir, relTarget);
        var root = rootDir.Length == 0 || rootDir == "." ? "" : rootDir + "/";
        var files = index.Files.Where(f => f.StartsWith(root, StringComparison.Ordinal)).ToList();
        var swift = files.Where(f => f.EndsWith(".swift", StringComparison.Ordinal)).Take(MaxSourceFilesPerTarget).ToList();
        bool swiftUI = false, appKit = false, entry = false;
        var usages = new Dictionary<string, SwiftPrivacyUsage>(StringComparer.Ordinal);
        var evidence = new Dictionary<string, EntitlementEvidence>(StringComparer.Ordinal);
        foreach (var file in swift)
        {
            var src = StripComments(Read(index, file, MaxSourceBytes));
            swiftUI |= ImportRegex("SwiftUI").IsMatch(src);
            appKit |= ImportRegex("AppKit").IsMatch(src) || ImportRegex("Cocoa").IsMatch(src);
            entry |= SwiftUIAppEntryRegex().IsMatch(src) || src.Contains("NSApplicationMain", StringComparison.Ordinal)
                     || (src.Contains("NSApplication.shared", StringComparison.Ordinal) && src.Contains(".run()", StringComparison.Ordinal));
            foreach (var rule in PrivacyRules)
                if (!usages.ContainsKey(rule.Key) && rule.Pattern.Match(src) is { Success: true } m)
                    usages[rule.Key] = new SwiftPrivacyUsage(rule.Key, rule.Purpose, $"{file[(Join(packageDir, "").Length)..].TrimStart('/')}: {m.Value.Trim()}");
            foreach (var rule in EntitlementRules)
                if (!evidence.ContainsKey(rule.Capability) && rule.Pattern.Match(src) is { Success: true } em)
                    evidence[rule.Capability] = new EntitlementEvidence(rule.Capability, rule.Kind, rule.Sandbox, rule.HardenedRuntime,
                        $"{file[(Join(packageDir, "").Length)..].TrimStart('/')}: {em.Value.Trim()}", rule.Explanation);
        }
        return new SwiftTargetSourceFacts
        {
            Target = target,
            Path = relTarget,
            SwiftFileCount = swift.Count,
            ImportsSwiftUI = swiftUI,
            ImportsAppKit = appKit,
            HasGuiEntryPoint = entry,
            PrivacyUsages = usages.Values.OrderBy(u => u.InfoPlistKey, StringComparer.Ordinal).ToList(),
            EntitlementEvidence = evidence.Values.OrderBy(e => e.Kind).ThenBy(e => e.Capability, StringComparer.Ordinal).ToList(),
            HasResourceDirectory = files.Any(f => f.StartsWith(root + "Resources/", StringComparison.Ordinal)),
        };
    }

    private sealed record PrivacyRule(string Key, string Purpose, Regex Pattern);

    /// <summary>APIs that make macOS terminate an app whose Info.plist lacks the matching usage description.</summary>
    private static readonly PrivacyRule[] PrivacyRules =
    [
        new("NSMicrophoneUsageDescription", "microphone",
            new Regex(@"AVCaptureDevice\.(?:requestAccess|authorizationStatus)\(\s*for:\s*\.audio|AVAudioRecorder\b|requestRecordPermission|AVAudioApplication\.requestRecordPermission|AVCaptureDevice\.default\([^)]*\.audio", RegexOptions.Compiled)),
        new("NSCameraUsageDescription", "camera",
            new Regex(@"AVCaptureDevice\.(?:requestAccess|authorizationStatus)\(\s*for:\s*\.video|AVCaptureDevice\.default\([^)]*\.video", RegexOptions.Compiled)),
        new("NSSpeechRecognitionUsageDescription", "speech recognition", new Regex(@"SFSpeechRecognizer\b", RegexOptions.Compiled)),
        new("NSLocationWhenInUseUsageDescription", "location", new Regex(@"CLLocationManager\b", RegexOptions.Compiled)),
        new("NSContactsUsageDescription", "contacts", new Regex(@"CNContactStore\b", RegexOptions.Compiled)),
        new("NSCalendarsFullAccessUsageDescription", "calendars", new Regex(@"EKEventStore\b", RegexOptions.Compiled)),
        new("NSPhotoLibraryUsageDescription", "photo library", new Regex(@"PHPhotoLibrary\b", RegexOptions.Compiled)),
        new("NSBluetoothAlwaysUsageDescription", "Bluetooth", new Regex(@"CBCentralManager\b|CBPeripheralManager\b", RegexOptions.Compiled)),
        new("NSAppleEventsUsageDescription", "Apple Events", new Regex(@"NSAppleScript\b|AEDeterminePermissionToAutomateTarget", RegexOptions.Compiled)),
    ];

    internal static string Join(string dir, string rel) => dir.Length == 0 ? rel : rel.Length == 0 ? dir : dir + "/" + rel;

    internal static string Read(SourceIndex index, string relative, int maxBytes)
    {
        try
        {
            var full = index.FullPath(relative);
            var info = new FileInfo(full);
            return info.Exists && info.Length <= maxBytes ? File.ReadAllText(full) : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    /// <summary>Removes // and /* */ comments (string contents are left alone; good enough for hints).</summary>
    internal static string StripComments(string code) => CommentRegex().Replace(code, m => m.Value.StartsWith('"') ? m.Value : " ");

    private static Regex ImportRegex(string module) => new($@"^\s*(?:@\w+\s+)*import\s+(?:\w+\s+)?{module}\b", RegexOptions.Multiline);

    [GeneratedRegex(@"^//\s*swift-tools-version\s*:\s*([0-9]+(?:\.[0-9]+){0,2})")]
    private static partial Regex ToolsVersionRegex();

    [GeneratedRegex(@"Package\s*\(\s*name\s*:\s*""([^""]+)""")]
    private static partial Regex PackageNameRegex();

    [GeneratedRegex(@"\.macOS\s*\(\s*(?:\.v(?<major>\d+)(?:_(?<minor>\d+))?|""(?<str>\d+(?:\.\d+){0,2})"")\s*\)")]
    private static partial Regex MacOSPlatformRegex();

    [GeneratedRegex(@"^\s*#if\s+[^\n]*\bos\s*\(", RegexOptions.Multiline)]
    private static partial Regex ConditionalRegex();

    [GeneratedRegex(@"\.package\s*\(\s*(?:name|url|path|id)\s*:")]
    private static partial Regex PackageDependencyRegex();

    [GeneratedRegex(@"\.executable\s*\(\s*name\s*:\s*""(?<name>[^""]+)""")]
    private static partial Regex ExecutableProductRegex();

    [GeneratedRegex(@"\.executableTarget\s*\(\s*name\s*:\s*""(?<name>[^""]+)""")]
    private static partial Regex ExecutableTargetRegex();

    [GeneratedRegex(@"@main\s+(?:@\w+\s+)*(?:final\s+)?struct\s+\w+\s*:\s*(?:[\w.]+\s*,\s*)*(?:SwiftUI\.)?App\b")]
    private static partial Regex SwiftUIAppEntryRegex();

    [GeneratedRegex(@"""(?:\\.|[^""\\])*""|//[^\n]*|/\*[\s\S]*?\*/")]
    private static partial Regex CommentRegex();
}
