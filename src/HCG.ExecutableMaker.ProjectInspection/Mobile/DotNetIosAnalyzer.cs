using System.Text.Json;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.ProjectInspection.DotNet;

namespace HCG.ExecutableMaker.ProjectInspection.Mobile;

/// <summary>
/// Finds .NET for iOS application projects that are not MAUI (an Avalonia.iOS head, a plain UIKit .NET app): OutputType
/// Exe and only net*-ios target frameworks, read with their imported Directory.*.props chain. No .xcodeproj is needed or
/// expected. Project references are followed through the repository; anything outside the imported folder is reported,
/// because building only part of a repository cannot work.
/// </summary>
public static class DotNetIosAnalyzer
{
    public static IReadOnlyList<DotNetIosProjectInfo> Find(SourceIndex index)
    {
        var list = new List<DotNetIosProjectInfo>();
        foreach (var rel in index.WithExtension(".csproj").OrderBy(SourceIndex.Depth))
        {
            var file = Parse(index, rel);
            if (file is null || file.IsTrue("UseMaui")) continue;
            var tfms = (file.Get("TargetFrameworks") ?? file.Get("TargetFramework") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            var output = file.Get("OutputType") ?? "Library";
            if (tfms.Count == 0 || !tfms.All(t => t.Contains("-ios", StringComparison.OrdinalIgnoreCase)) || !output.Equals("Exe", StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(Describe(index, rel, file, tfms[0]));
        }
        return list;
    }

    private static MsBuildProjectFile? Parse(SourceIndex index, string rel)
    {
        var full = index.FullPath(rel);
        var dir = Path.GetDirectoryName(full)!;
        try { return MsBuildProjectFile.Parse(full, DotNetProjectInspector.FindAncestorFiles(index.Root, dir, "Directory.Build.props"), DotNetProjectInspector.FindCentralVersions(index.Root, dir), index.Root); }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException) { return null; }
    }

    private static DotNetIosProjectInfo Describe(SourceIndex index, string rel, MsBuildProjectFile file, string tfm)
    {
        var root = Path.GetFullPath(index.Root);
        var refs = new List<string>();
        var missing = new List<string>();
        var outside = new SortedSet<string>(file.OutsideWorkspace, StringComparer.Ordinal);
        var queue = new Queue<(string Rel, MsBuildProjectFile File)>([(rel, file)]);
        var seen = new HashSet<string>(StringComparer.Ordinal) { rel };
        while (queue.Count > 0 && seen.Count < 200)
        {
            var (current, f) = queue.Dequeue();
            var dir = Path.GetDirectoryName(index.FullPath(current))!;
            foreach (var r in f.ProjectReferences)
            {
                if (r.Contains("$(", StringComparison.Ordinal)) { missing.Add($"{r} (from {current}; uses properties that could not be resolved)"); continue; }
                var target = Path.GetFullPath(Path.Combine(dir, r));
                if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) { outside.Add(Path.GetRelativePath(root, target).Replace('\\', '/')); continue; }
                var relTarget = Path.GetRelativePath(root, target).Replace('\\', '/');
                if (!seen.Add(relTarget)) continue;
                if (!File.Exists(target)) { missing.Add($"{relTarget} (from {current})"); continue; }
                refs.Add(relTarget);
                if (Parse(index, relTarget) is { } child)
                {
                    foreach (var o in child.OutsideWorkspace) outside.Add(o);
                    queue.Enqueue((relTarget, child));
                }
            }
        }
        var avalonia = file.PackageReferences.FirstOrDefault(p => p.Name.Equals("Avalonia.iOS", StringComparison.OrdinalIgnoreCase));
        string? Clean(string? v) => v is null || v.Contains("$(", StringComparison.Ordinal) ? null : v;
        var (pin, pinFile) = WorkloadPin(index, rel);
        var pinIsRollback = pin is not null;
        var globalSdk = GlobalJsonSdk(index, rel);
        // A toolchain props file may declare the iOS workload manifest instead of shipping a rollback file
        // (HCG projects: HcgIosWorkloadManifest / HcgRequiredXcode / HcgMinimumDotnetSdk in toolchain.props).
        if (pin is null && Clean(file.Get("HcgIosWorkloadManifest")) is { } manifest)
        {
            var sdk = Clean(file.Get("HcgMinimumDotnetSdk")) ?? globalSdk;
            var band = sdk is null ? null : FeatureBand(sdk);
            pin = band is null ? manifest : $"{manifest}/{band}";
            pinFile = Relative(index, file.Imports.FirstOrDefault(i => File.Exists(i) && File.ReadAllText(i).Contains("HcgIosWorkloadManifest", StringComparison.Ordinal)));
            pinIsRollback = false;
        }
        string? install = pin is null ? "dotnet workload install ios"
            : pinIsRollback ? $"dotnet workload install ios --from-rollback-file \"{pinFile}\""
            : pin.Contains('/') ? $"dotnet workload install ios --from-rollback-file <a file containing {{\"microsoft.net.sdk.ios\": \"{pin}\"}}>" : null;
        var entitlements = Clean(file.Get("CodesignEntitlements"));
        return new DotNetIosProjectInfo
        {
            ProjectPath = rel, TargetFramework = tfm, AssemblyName = Clean(file.Get("AssemblyName")) ?? Path.GetFileNameWithoutExtension(rel),
            CodesignKey = Clean(file.Get("CodesignKey")),
            ApplicationId = Clean(file.Get("ApplicationId")), ApplicationTitle = Clean(file.Get("ApplicationTitle")),
            DisplayVersion = Clean(file.Get("ApplicationDisplayVersion")) ?? Clean(file.Get("Version")), BuildNumber = Clean(file.Get("ApplicationVersion")),
            MinimumOS = Clean(file.Get("SupportedOSPlatformVersion")), UiFramework = avalonia is not null ? "Avalonia" : null, UiFrameworkVersion = Clean(avalonia?.Version),
            Entitlements = entitlements, ProjectReferences = refs, OutsideWorkspace = outside.ToList(), MissingReferences = missing,
            WorkloadPin = pin, WorkloadPinFile = pinFile, WorkloadPinIsRollbackFile = pinIsRollback, WorkloadInstallCommand = install,
            RequiredXcode = Clean(file.Get("HcgRequiredXcode")), GlobalJsonSdk = globalSdk,
        };
    }

    /// <summary>"10.0.114" → "10.0.100" (the SDK feature band a workload manifest belongs to).</summary>
    public static string? FeatureBand(string sdkVersion)
    {
        var parts = sdkVersion.Split('-')[0].Split('.');
        return parts.Length >= 3 && int.TryParse(parts[2], out var patch) ? $"{parts[0]}.{parts[1]}.{patch / 100 * 100}" : null;
    }

    private static string? Relative(SourceIndex index, string? full) =>
        full is null ? null : Path.GetRelativePath(Path.GetFullPath(index.Root), full).Replace('\\', '/');

    /// <summary>A workload rollback file ({"microsoft.net.sdk.ios": "26.2.10233/10.0.100"}) nearest to the project.</summary>
    private static (string? Pin, string? File) WorkloadPin(SourceIndex index, string projectRel)
    {
        foreach (var f in index.Files.Where(f => f.EndsWith("rollback.json", StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(f => CommonPrefix(f, projectRel)).ThenBy(SourceIndex.Depth))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(index.FullPath(f)));
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("microsoft.net.sdk.ios", out var v) && v.ValueKind == JsonValueKind.String)
                    return (v.GetString(), f);
            }
            catch (Exception ex) when (ex is JsonException or IOException) { }
        }
        return (null, null);
    }

    private static int CommonPrefix(string a, string b)
    {
        var i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
        return i;
    }

    private static string? GlobalJsonSdk(SourceIndex index, string projectRel)
    {
        var dir = projectRel.Contains('/') ? projectRel[..projectRel.LastIndexOf('/')] : "";
        while (true)
        {
            var candidate = dir.Length == 0 ? "global.json" : dir + "/global.json";
            if (index.Files.Contains(candidate))
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(index.FullPath(candidate)));
                    return doc.RootElement.TryGetProperty("sdk", out var sdk) && sdk.TryGetProperty("version", out var v) ? v.GetString() : null;
                }
                catch (Exception ex) when (ex is JsonException or IOException) { return null; }
            if (dir.Length == 0) return null;
            dir = dir.Contains('/') ? dir[..dir.LastIndexOf('/')] : "";
        }
    }
}
