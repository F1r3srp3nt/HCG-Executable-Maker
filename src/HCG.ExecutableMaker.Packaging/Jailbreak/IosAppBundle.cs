using System.IO.Compression;
using HCG.ExecutableMaker.Packaging.Mobile;

namespace HCG.ExecutableMaker.Packaging.Jailbreak;

/// <summary>
/// An iOS application bundle that a build already produced: a device <c>.app</c>, the app inside an
/// <c>.xcarchive</c> (Products/Applications), or the <c>Payload/*.app</c> of an IPA (extracted, never renamed).
/// Inspection reads Info.plist and every Mach-O in the bundle; nothing is changed.
/// </summary>
public sealed record IosAppBundle(
    string AppPath,
    string Source,
    string SourceKind,
    string BundleId,
    string Name,
    string Executable,
    string? ShortVersion,
    string? BuildVersion,
    string? MinimumOS,
    IReadOnlyList<string> DeviceFamilies,
    IReadOnlyDictionary<string, string> BuildInfo,
    IReadOnlyList<IosAppBundle.Binary> Binaries,
    IReadOnlyList<string> IconFiles,
    IReadOnlyList<string> Problems)
{
    public sealed record Binary(string RelativePath, IReadOnlyList<MachOLinkage.SliceInfo> Slices);

    public string AppDirectoryName => Path.GetFileName(AppPath.TrimEnd(Path.DirectorySeparatorChar));
    public Binary? Main => Binaries.FirstOrDefault(b => b.RelativePath == Executable);

    /// <summary>
    /// Finds the app in <paramref name="source"/> (.app folder, .xcarchive, or .ipa) and inspects it. An IPA is
    /// extracted into <paramref name="workDir"/> (zip-slip safe); folders are read in place.
    /// </summary>
    public static IosAppBundle Open(string source, string workDir)
    {
        var full = Path.GetFullPath(source);
        if (Directory.Exists(full) && full.TrimEnd('/').EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            return Inspect(full, full, "app");
        if (Directory.Exists(full) && full.TrimEnd('/').EndsWith(".xcarchive", StringComparison.OrdinalIgnoreCase))
        {
            var apps = Directory.Exists(Path.Combine(full, "Products", "Applications"))
                ? Directory.GetDirectories(Path.Combine(full, "Products", "Applications"), "*.app") : [];
            return apps.Length == 1
                ? Inspect(apps[0], full, "xcarchive")
                : throw new InvalidDataException(apps.Length == 0
                    ? "The archive has no Products/Applications/*.app (it may hold a framework or a command-line tool, not an app)."
                    : $"The archive holds {apps.Length} apps; choose one .app inside Products/Applications.");
        }
        if (File.Exists(full) && full.EndsWith(".ipa", StringComparison.OrdinalIgnoreCase))
        {
            var dest = Path.Combine(workDir, "ipa-payload");
            if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
            Directory.CreateDirectory(dest);
            using (var zip = ZipFile.OpenRead(full))
            {
                foreach (var e in zip.Entries)
                {
                    if (!e.FullName.StartsWith("Payload/", StringComparison.Ordinal)) continue;
                    var target = Path.GetFullPath(Path.Combine(dest, e.FullName));
                    if (!target.StartsWith(dest + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                        throw new InvalidDataException($"The IPA entry {e.FullName} points outside the archive.");
                    if (e.FullName.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    e.ExtractToFile(target, overwrite: true);
                    // Keep the executable bits recorded in the zip (Unix mode in the high 16 bits of ExternalAttributes).
                    var mode = (e.ExternalAttributes >> 16) & 0x1FF;
                    if (!OperatingSystem.IsWindows() && mode != 0) File.SetUnixFileMode(target, (UnixFileMode)mode);
                }
            }
            var apps = Directory.Exists(Path.Combine(dest, "Payload")) ? Directory.GetDirectories(Path.Combine(dest, "Payload"), "*.app") : [];
            return apps.Length == 1
                ? Inspect(apps[0], full, "ipa")
                : throw new InvalidDataException("The IPA has no single Payload/*.app.");
        }
        throw new InvalidDataException("Choose a device .app folder, an .xcarchive, or an .ipa produced by an iOS build.");
    }

    public static IosAppBundle Inspect(string appPath, string source, string sourceKind)
    {
        var problems = new List<string>();
        var plistPath = Path.Combine(appPath, "Info.plist");
        if (!File.Exists(plistPath)) throw new InvalidDataException($"{Path.GetFileName(appPath)} has no Info.plist (a macOS .app keeps it in Contents/; this must be an iOS app).");
        var info = PlistReader.ParseDictionary(File.ReadAllBytes(plistPath));
        string? S(string key) => info.GetValueOrDefault(key) as string;
        var bundleId = S("CFBundleIdentifier") ?? "";
        var executable = S("CFBundleExecutable") ?? "";
        if (bundleId.Length == 0) problems.Add("Info.plist has no CFBundleIdentifier.");
        if (executable.Length == 0) problems.Add("Info.plist has no CFBundleExecutable.");
        var families = (info.GetValueOrDefault("UIDeviceFamily") as IEnumerable<object>)?.Select(f => Convert.ToInt64(f) switch { 1 => "iPhone", 2 => "iPad", var x => $"family {x}" }).ToList() ?? [];
        var platforms = (info.GetValueOrDefault("CFBundleSupportedPlatforms") as IEnumerable<object>)?.OfType<string>().ToList() ?? [];
        if (platforms.Any(p => p.Contains("Simulator", StringComparison.OrdinalIgnoreCase)))
            problems.Add("Info.plist says this app was built for the iOS Simulator; a Simulator build cannot run on a device.");
        var buildInfo = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in new[] { "DTXcode", "DTXcodeBuild", "DTSDKName", "DTSDKBuild", "DTPlatformName", "DTPlatformVersion", "DTCompiler", "BuildMachineOSBuild" })
            if (S(key) is { } v) buildInfo[key] = v;

        var binaries = new List<Binary>();
        foreach (var file in Directory.EnumerateFiles(appPath, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            if (new FileInfo(file).LinkTarget is not null || !MachOLinkage.IsMachO(file)) continue;
            binaries.Add(new Binary(Path.GetRelativePath(appPath, file).Replace('\\', '/'), MachOLinkage.Read(file)));
        }
        if (executable.Length > 0 && binaries.All(b => b.RelativePath != executable))
            problems.Add($"The main executable {executable} named in Info.plist is missing or is not a Mach-O.");

        var icons = Directory.EnumerateFiles(appPath, "*.png", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName).OfType<string>()
            .Where(n => n.StartsWith("AppIcon", StringComparison.Ordinal) || n.StartsWith("Icon", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).ToList();

        return new IosAppBundle(appPath, source, sourceKind, bundleId, S("CFBundleDisplayName") ?? S("CFBundleName") ?? Path.GetFileNameWithoutExtension(appPath),
            executable, S("CFBundleShortVersionString"), S("CFBundleVersion"), S("MinimumOSVersion"), families, buildInfo, binaries, icons, problems);
    }

    /// <summary>The largest app icon PNG in the bundle (Xcode copies AppIcon60x60@3x.png etc. next to Assets.car), if any.</summary>
    public string? LargestIcon() => IconFiles.Select(n => Path.Combine(AppPath, n)).OrderByDescending(p => new FileInfo(p).Length).FirstOrDefault();
}
