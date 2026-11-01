using System.Text.RegularExpressions;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.ProjectInspection.Mobile;

/// <summary>Static analysis of Android Gradle projects (Groovy or Kotlin DSL). Never runs Gradle.</summary>
public static partial class AndroidGradleAnalyzer
{
    private static readonly string[] SettingsNames = ["settings.gradle.kts", "settings.gradle"];
    private static readonly string[] BuildNames = ["build.gradle.kts", "build.gradle"];
    private const long MaxScriptBytes = 2_000_000;

    /// <summary>Gradle roots (relative dirs) that contain an Android application module.</summary>
    public static IReadOnlyList<string> FindRoots(SourceIndex index)
    {
        var roots = new List<string>();
        foreach (var settings in index.Named(SettingsNames).OrderBy(SourceIndex.Depth))
        {
            var root = Dir(settings);
            if (roots.Any(r => IsUnder(root, r))) continue;
            if (Analyze(index, root, out _).AppModules.Count > 0) roots.Add(root);
        }
        // Single-module project without settings file.
        foreach (var build in index.Named(BuildNames).OrderBy(SourceIndex.Depth))
        {
            var root = Dir(build);
            if (roots.Any(r => IsUnder(root, r))) continue;
            if (IsAndroidPlugin(Read(index, build), ReadCatalog(index, root), application: true)) roots.Add(root);
        }
        return roots;
    }

    public static AndroidProjectInfo Analyze(SourceIndex index, string root, out List<AnalysisDiagnostic> diagnostics)
    {
        diagnostics = [];
        var settingsFile = SettingsNames.Select(n => Join(root, n)).FirstOrDefault(f => index.Files.Contains(f));
        var catalog = ReadCatalog(index, root);
        var modules = settingsFile is null ? [":"] : GradleScript.IncludedProjects(GradleScript.StripComments(Read(index, settingsFile)));
        if (settingsFile is not null && !modules.Contains(":")) modules = [":", .. modules];

        var apps = new List<AndroidModuleInfo>();
        var libs = new List<string>();
        var usesKotlin = false;
        string? agp = null;

        foreach (var gradlePath in modules)
        {
            var dir = gradlePath == ":" ? root : Join(root, gradlePath.TrimStart(':').Replace(':', '/'));
            var buildFile = BuildNames.Select(n => Join(dir, n)).FirstOrDefault(f => index.Files.Contains(f));
            if (buildFile is null) continue;
            var script = GradleScript.StripComments(Read(index, buildFile));
            agp ??= AgpVersion(script, catalog);
            usesKotlin |= KotlinRegex().IsMatch(script) || script.Contains("kotlin.android", StringComparison.Ordinal);
            if (IsAndroidPlugin(script, catalog, application: true))
                apps.Add(ParseModule(script, dir, root, gradlePath, buildFile));
            else if (IsAndroidPlugin(script, catalog, application: false))
                libs.Add(gradlePath);
        }

        if (settingsFile is not null)
            agp ??= AgpVersion(GradleScript.StripComments(Read(index, settingsFile)), catalog);
        agp ??= catalog.Versions.GetValueOrDefault("agp") ?? catalog.Versions.GetValueOrDefault("androidGradlePlugin");

        var wrapperProps = Join(root, "gradle/wrapper/gradle-wrapper.properties");
        var wrapperJar = Join(root, "gradle/wrapper/gradle-wrapper.jar");
        string? wrapperVersion = null;
        if (index.Files.Contains(wrapperProps))
        {
            var m = DistributionRegex().Match(Read(index, wrapperProps));
            if (m.Success) wrapperVersion = m.Groups[1].Value;
        }
        var hasWrapper = index.Files.Contains(wrapperJar) && index.Files.Contains(wrapperProps);
        if (index.Files.Contains(wrapperProps) && !index.Files.Contains(wrapperJar))
            diagnostics.Add(new(DiagnosticSeverity.Warning, "gradle-wrapper.properties exists but gradle/wrapper/gradle-wrapper.jar is missing; the wrapper cannot run.", wrapperProps));
        if (apps.Count == 0)
            diagnostics.Add(new(DiagnosticSeverity.Warning, "No module applies the com.android.application plugin (libraries only).", settingsFile ?? root));
        foreach (var app in apps.Where(a => a.ApplicationId is null))
            diagnostics.Add(new(DiagnosticSeverity.Info, $"{app.GradlePath}: applicationId is not a literal (computed or inherited); the built APK will show the real value.", app.BuildFile));

        return new AndroidProjectInfo
        {
            Root = root,
            SettingsFile = settingsFile,
            HasWrapper = hasWrapper,
            WrapperGradleVersion = wrapperVersion,
            AgpVersion = agp,
            UsesKotlin = usesKotlin,
            AppModules = apps,
            LibraryModules = libs,
        };
    }

    private static AndroidModuleInfo ParseModule(string script, string dir, string root, string gradlePath, string buildFile)
    {
        var android = GradleScript.Block(script, "android") ?? script;
        var defaults = GradleScript.Block(android, "defaultConfig") ?? android;
        var buildTypes = GradleScript.Block(android, "buildTypes") is { } bt ? GradleScript.ChildBlockNames(bt) : [];
        var flavors = GradleScript.Block(android, "productFlavors") is { } pf ? GradleScript.ChildBlockNames(pf) : [];
        var allTypes = new[] { "debug", "release" }.Concat(buildTypes).Distinct(StringComparer.Ordinal).ToList();
        return new AndroidModuleInfo
        {
            Directory = dir == root ? "" : dir[(root.Length == 0 ? 0 : root.Length + 1)..],
            GradlePath = gradlePath,
            BuildFile = buildFile,
            ApplicationId = GradleScript.StringValue(defaults, "applicationId"),
            Namespace = GradleScript.StringValue(android, "namespace"),
            MinSdk = GradleScript.IntValue(defaults, "minSdk", "minSdkVersion"),
            TargetSdk = GradleScript.IntValue(defaults, "targetSdk", "targetSdkVersion"),
            CompileSdk = GradleScript.IntValue(android, "compileSdk", "compileSdkVersion"),
            VersionCode = GradleScript.IntValue(defaults, "versionCode"),
            VersionName = GradleScript.StringValue(defaults, "versionName"),
            BuildTypes = allTypes,
            ProductFlavors = flavors,
            HasSigningConfig = GradleScript.Block(android, "signingConfigs") is not null,
        };
    }

    /// <summary>Whether the script applies com.android.application (or .library), directly or via a version-catalog alias.</summary>
    public static bool IsAndroidPlugin(string script, VersionCatalog catalog, bool application)
    {
        var id = application ? "com.android.application" : "com.android.library";
        if (Regex.IsMatch(script, @"(?:id\s*\(?\s*|apply\s+plugin\s*:\s*|apply\s*\(\s*plugin\s*=\s*)[""']" + Regex.Escape(id) + @"[""']"))
        {
            // "apply false" in a root build script only declares the version.
            var line = Regex.Match(script, @"[^\n]*[""']" + Regex.Escape(id) + @"[""'][^\n]*").Value;
            return !Regex.IsMatch(line, @"apply\s*\(?\s*false");
        }
        foreach (Match m in AliasRegex().Matches(script))
        {
            var accessor = m.Groups[1].Value;
            if (Regex.IsMatch(m.Value, @"apply\s*\(?\s*false")) continue;
            if (catalog.Plugins.TryGetValue(accessor, out var plugin) && plugin.Id == id) return true;
        }
        return false;
    }

    private static string? AgpVersion(string script, VersionCatalog catalog)
    {
        var m = Regex.Match(script, @"com\.android\.tools\.build:gradle:([\w.\-]+)");
        if (m.Success) return m.Groups[1].Value;
        m = Regex.Match(script, @"id\s*\(?\s*[""']com\.android\.(?:application|library)[""']\s*\)?\s*version\s*\(?\s*[""']([\w.\-]+)[""']");
        if (m.Success) return m.Groups[1].Value;
        foreach (Match a in AliasRegex().Matches(script))
            if (catalog.Plugins.TryGetValue(a.Groups[1].Value, out var p) && p.Id.StartsWith("com.android.", StringComparison.Ordinal) && p.Version is not null)
                return p.Version;
        return null;
    }

    public sealed record CatalogPlugin(string Id, string? Version);
    public sealed record VersionCatalog(IReadOnlyDictionary<string, string> Versions, IReadOnlyDictionary<string, CatalogPlugin> Plugins)
    {
        public static readonly VersionCatalog Empty = new(new Dictionary<string, string>(), new Dictionary<string, CatalogPlugin>());
    }

    /// <summary>Reads gradle/libs.versions.toml ([versions] and [plugins]); plugin keys are normalised to Gradle accessor form.</summary>
    public static VersionCatalog ReadCatalog(SourceIndex index, string root)
    {
        var path = Join(root, "gradle/libs.versions.toml");
        if (!index.Files.Contains(path)) return VersionCatalog.Empty;
        return ParseCatalog(Read(index, path));
    }

    public static VersionCatalog ParseCatalog(string toml)
    {
        var versions = new Dictionary<string, string>(StringComparer.Ordinal);
        var plugins = new Dictionary<string, CatalogPlugin>(StringComparer.Ordinal);
        string? section = null;
        foreach (var raw in toml.Split('\n'))
        {
            var line = raw.Split('#')[0].Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('[')) { section = line.Trim('[', ']').Trim(); continue; }
            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            var key = line[..eq].Trim().Trim('"');
            var value = line[(eq + 1)..].Trim();
            if (section == "versions")
                versions[key] = value.Trim('"', '\'');
            else if (section == "plugins")
            {
                string? id, version = null;
                if (value.StartsWith('"'))
                {
                    var parts = value.Trim('"').Split(':');
                    id = parts[0];
                    version = parts.Length > 1 ? parts[1] : null;
                }
                else
                {
                    id = Regex.Match(value, @"id\s*=\s*""([^""]+)""").Groups[1].Value;
                    var vref = Regex.Match(value, @"version\.ref\s*=\s*""([^""]+)""");
                    var v = Regex.Match(value, @"version\s*=\s*""([^""]+)""");
                    version = vref.Success ? versions.GetValueOrDefault(vref.Groups[1].Value) : v.Success ? v.Groups[1].Value : null;
                }
                if (!string.IsNullOrEmpty(id)) plugins[key.Replace('-', '.').Replace('_', '.')] = new CatalogPlugin(id, version);
            }
        }
        return new VersionCatalog(versions, plugins);
    }

    [GeneratedRegex(@"alias\s*\(\s*libs\.plugins\.([\w.]+)\s*\)[^\n]*")]
    private static partial Regex AliasRegex();

    [GeneratedRegex(@"org\.jetbrains\.kotlin\.android|kotlin\s*\(\s*""android""\s*\)|kotlin-android")]
    private static partial Regex KotlinRegex();

    [GeneratedRegex(@"distributionUrl\s*=\s*\S*gradle-([\d.]+(?:-rc-\d+)?)-(?:bin|all)\.zip")]
    private static partial Regex DistributionRegex();

    internal static string Read(SourceIndex index, string rel)
    {
        try
        {
            var full = index.FullPath(rel);
            return new FileInfo(full).Length > MaxScriptBytes ? "" : File.ReadAllText(full);
        }
        catch (IOException) { return ""; }
    }

    internal static string Dir(string rel) => rel.Contains('/') ? rel[..rel.LastIndexOf('/')] : "";
    internal static string Join(string dir, string name) => dir.Length == 0 ? name : dir + "/" + name;
    private static bool IsUnder(string path, string root) => root.Length == 0 || path == root || path.StartsWith(root + "/", StringComparison.Ordinal);
}
