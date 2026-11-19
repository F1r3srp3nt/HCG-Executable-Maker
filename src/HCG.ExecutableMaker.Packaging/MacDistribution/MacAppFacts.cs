using HCG.ExecutableMaker.Packaging.Mobile;
using HCG.ExecutableMaker.Packaging.Verification;

namespace HCG.ExecutableMaker.Packaging.MacDistribution;

/// <summary>
/// What a built macOS .app actually contains, read from the bundle itself (binary or XML Info.plist, Mach-O headers):
/// identity, version, minimum macOS, architectures of every executable, icon and resources. Read-only.
/// </summary>
public sealed record MacAppFacts
{
    public required string BundlePath { get; init; }
    public string? BundleIdentifier { get; init; }
    public string? Name { get; init; }
    public string? DisplayName { get; init; }
    public string? ShortVersion { get; init; }
    public string? BuildVersion { get; init; }
    public string? MinimumSystemVersion { get; init; }
    public string? Executable { get; init; }
    public string? IconName { get; init; }
    public string? IconFile { get; init; }
    /// <summary>Contents/Resources/Assets.car (compiled asset catalog, where an AppIcon set lands).</summary>
    public bool HasAssetCatalog { get; init; }
    public int ResourceFiles { get; init; }
    /// <summary>Architectures of the main executable.</summary>
    public IReadOnlyList<string> Architectures { get; init; } = [];
    /// <summary>Minimum macOS each slice of the main executable was linked for (LC_BUILD_VERSION).</summary>
    public IReadOnlyList<string> LinkedMinimum { get; init; } = [];
    /// <summary>Every Mach-O file in the bundle (relative path → architectures).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> MachOFiles { get; init; } = new Dictionary<string, IReadOnlyList<string>>();
    public string? Problem { get; init; }

    public bool HasIcon => (IconName is not null && HasAssetCatalog) || (IconFile is not null && File.Exists(Path.Combine(BundlePath, "Contents", "Resources", IconFile.EndsWith(".icns", StringComparison.Ordinal) ? IconFile : IconFile + ".icns")));
    public string Label => DisplayName ?? Name ?? Path.GetFileNameWithoutExtension(BundlePath);

    public static MacAppFacts Read(string bundle)
    {
        var plistPath = Path.Combine(bundle, "Contents", "Info.plist");
        if (!File.Exists(plistPath)) return new MacAppFacts { BundlePath = bundle, Problem = "Contents/Info.plist is missing." };
        Dictionary<string, object> plist;
        try { plist = PlistReader.ParseDictionary(File.ReadAllBytes(plistPath)); }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException or FormatException)
        {
            return new MacAppFacts { BundlePath = bundle, Problem = "Info.plist could not be read: " + ex.Message };
        }
        string? S(string key) => plist.GetValueOrDefault(key) as string;
        var exe = S("CFBundleExecutable");
        var exePath = exe is null ? null : Path.Combine(bundle, "Contents", "MacOS", exe);
        var slices = exePath is not null && File.Exists(exePath) ? MachO.ReadSlices(exePath) : [];
        var resources = Path.Combine(bundle, "Contents", "Resources");
        var machO = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(bundle, "Contents"), "*", SearchOption.AllDirectories))
        {
            if (new FileInfo(file).LinkTarget is not null) continue;
            var archs = SafeSlices(file);
            if (archs.Count > 0) machO[Path.GetRelativePath(bundle, file).Replace('\\', '/')] = archs.Select(a => a.Architecture).ToList();
        }
        return new MacAppFacts
        {
            BundlePath = bundle,
            BundleIdentifier = S("CFBundleIdentifier"),
            Name = S("CFBundleName"),
            DisplayName = S("CFBundleDisplayName"),
            ShortVersion = S("CFBundleShortVersionString"),
            BuildVersion = S("CFBundleVersion"),
            MinimumSystemVersion = S("LSMinimumSystemVersion"),
            Executable = exe,
            IconName = S("CFBundleIconName"),
            IconFile = S("CFBundleIconFile"),
            HasAssetCatalog = File.Exists(Path.Combine(resources, "Assets.car")),
            ResourceFiles = Directory.Exists(resources) ? Directory.EnumerateFiles(resources, "*", SearchOption.AllDirectories).Count() : 0,
            Architectures = slices.Select(x => x.Architecture).ToList(),
            LinkedMinimum = slices.Select(x => x.MinimumMacOS).OfType<string>().Distinct().ToList(),
            MachOFiles = machO,
            Problem = exe is null ? "Info.plist has no CFBundleExecutable." : slices.Count == 0 ? $"Contents/MacOS/{exe} is missing or not a Mach-O executable." : null,
        };
    }

    private static IReadOnlyList<MachO.Slice> SafeSlices(string file)
    {
        try { return new FileInfo(file).Length >= 8 ? MachO.ReadSlices(file) : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return []; }
    }
}
