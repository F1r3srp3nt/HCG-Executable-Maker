using System.Text.RegularExpressions;
using System.Xml.Linq;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.ProjectInspection.Mobile;

namespace HCG.ExecutableMaker.ProjectInspection.MacXcode;

/// <summary>
/// Static analysis of macOS-only Xcode projects: targets, schemes, configurations, deployment target, identity,
/// signing, frameworks, languages, licence files and helper scripts. Reads text only; never runs xcodebuild or any
/// script that ships with the project.
/// </summary>
public static partial class MacXcodeProjectAnalyzer
{
    /// <summary>
    /// Package.swift manifests that a macOS Xcode app project uses as a local Swift package (XCLocalSwiftPackageReference).
    /// They are the app's dependencies: built by xcodebuild as part of the app, never as an app of their own.
    /// </summary>
    public static IReadOnlySet<string> LocalPackageManifests(SourceIndex index) =>
        FindMacProjects(index).Select(p => Analyze(index, p)).Where(p => p.AppTargets.Count > 0)
            .SelectMany(p => p.PackageReferences).Select(r => r.ManifestPath).OfType<string>().ToHashSet(StringComparer.Ordinal);

    /// <summary>macOS-only .xcodeproj bundles in the workspace (relative paths, shallowest first).</summary>
    public static IReadOnlyList<string> FindMacProjects(SourceIndex index) =>
        XcodeProjectAnalyzer.FindProjects(index).Where(p => XcodeProjectAnalyzer.IsMacOnlyProject(index, p)).ToList();

    public static MacXcodeProjectInfo Analyze(SourceIndex index, string projectPath)
    {
        var pbxPath = projectPath + "/project.pbxproj";
        var dir = AndroidGradleAnalyzer.Dir(projectPath);
        var targets = new List<MacXcodeTargetInfo>();
        var configurations = new List<string>();
        var usesPackages = false;
        var packageRefs = new List<MacXcodePackageReference>();
        int? objectVersion = null;
        string? parseError = null;
        try
        {
            var root = (Dictionary<string, object>)OpenStepPlist.Parse(AndroidGradleAnalyzer.Read(index, pbxPath));
            objectVersion = root.GetValueOrDefault("objectVersion") is string ov && int.TryParse(ov, out var ovn) ? ovn : null;
            var objects = (Dictionary<string, object>)root["objects"];
            Dictionary<string, object>? Obj(object? id) => id is string s && objects.TryGetValue(s, out var o) ? o as Dictionary<string, object> : null;
            List<Dictionary<string, object>> Configs(Dictionary<string, object>? owner) =>
                ((Obj(owner?.GetValueOrDefault("buildConfigurationList"))?.GetValueOrDefault("buildConfigurations") as List<object>) ?? [])
                .Select(Obj).OfType<Dictionary<string, object>>().ToList();
            // Values of one configuration (Release when the project has it), the way xcodebuild layers them: project
            // level first, then the target's own settings.
            static void Merge(Dictionary<string, string> into, IEnumerable<Dictionary<string, object>> configs)
            {
                var ordered = configs.OrderByDescending(c => c.GetValueOrDefault("name") as string == "Release").ToList();
                foreach (var c in ordered.AsEnumerable().Reverse())
                    if (c.GetValueOrDefault("buildSettings") is Dictionary<string, object> bs)
                        foreach (var (k, v) in bs)
                            into[k] = v switch { string s => s, List<object> l => string.Join(' ', l.OfType<string>()), _ => into.GetValueOrDefault(k) ?? "" };
            }

            var project = Obj(root.GetValueOrDefault("rootObject"));
            var projectConfigs = Configs(project);
            configurations.AddRange(projectConfigs.Select(c => c.GetValueOrDefault("name") as string).OfType<string>());
            var projectSettings = new Dictionary<string, string>(StringComparer.Ordinal);
            Merge(projectSettings, projectConfigs);
            usesPackages = objects.Values.OfType<Dictionary<string, object>>().Any(o => o.GetValueOrDefault("isa") is "XCRemoteSwiftPackageReference" or "XCLocalSwiftPackageReference");
            foreach (var o in objects.Values.OfType<Dictionary<string, object>>())
            {
                if (o.GetValueOrDefault("isa") as string == "XCLocalSwiftPackageReference" && o.GetValueOrDefault("relativePath") is string rel)
                {
                    var manifest = AndroidGradleAnalyzer.Join(AndroidGradleAnalyzer.Join(dir, rel), "Package.swift");
                    packageRefs.Add(new("local", rel, null, index.Files.Contains(manifest) ? manifest : null));
                }
                else if (o.GetValueOrDefault("isa") as string == "XCRemoteSwiftPackageReference" && o.GetValueOrDefault("repositoryURL") is string url)
                {
                    var req = o.GetValueOrDefault("requirement") as Dictionary<string, object>;
                    var kind = req?.GetValueOrDefault("kind") as string;
                    var value = req?.GetValueOrDefault("minimumVersion") ?? req?.GetValueOrDefault("version") ?? req?.GetValueOrDefault("branch") ?? req?.GetValueOrDefault("revision");
                    packageRefs.Add(new("remote", url, kind is null ? null : $"{kind}{(value is string v ? " " + v : "")}", null));
                }
            }

            foreach (var t in objects.Values.OfType<Dictionary<string, object>>().Where(o => o.GetValueOrDefault("isa") as string == "PBXNativeTarget"))
            {
                var name = t.GetValueOrDefault("name") as string ?? "?";
                var settings = new Dictionary<string, string>(projectSettings, StringComparer.Ordinal);
                Merge(settings, Configs(t));
                string? Setting(string key) => settings.GetValueOrDefault(key) is { Length: > 0 } v
                    ? v.Replace("$(TARGET_NAME)", name, StringComparison.Ordinal).Replace("${TARGET_NAME}", name, StringComparison.Ordinal)
                    : null;
                var frameworks = new List<string>();
                foreach (var phase in ((t.GetValueOrDefault("buildPhases") as List<object>) ?? []).Select(Obj).OfType<Dictionary<string, object>>()
                             .Where(p => p.GetValueOrDefault("isa") as string == "PBXFrameworksBuildPhase"))
                    foreach (var file in ((phase.GetValueOrDefault("files") as List<object>) ?? []).Select(Obj).OfType<Dictionary<string, object>>())
                        if (Obj(file.GetValueOrDefault("fileRef")) is { } fr && (fr.GetValueOrDefault("name") ?? fr.GetValueOrDefault("path")) is string fn)
                            frameworks.Add(Path.GetFileNameWithoutExtension(fn));
                var perConfig = new List<MacXcodeConfigurationFacts>();
                foreach (var configName in configurations.Count > 0 ? configurations : Configs(t).Select(c => c.GetValueOrDefault("name") as string).OfType<string>().ToList())
                {
                    var one = new Dictionary<string, string>(StringComparer.Ordinal);
                    Merge(one, projectConfigs.Where(c => c.GetValueOrDefault("name") as string == configName));
                    Merge(one, Configs(t).Where(c => c.GetValueOrDefault("name") as string == configName));
                    string? C(string key) => one.GetValueOrDefault(key) is { Length: > 0 } v
                        ? v.Replace("$(TARGET_NAME)", name, StringComparison.Ordinal).Replace("${TARGET_NAME}", name, StringComparison.Ordinal) : null;
                    perConfig.Add(new MacXcodeConfigurationFacts
                    {
                        Name = configName,
                        BundleIdentifier = C("PRODUCT_BUNDLE_IDENTIFIER"),
                        CodeSignIdentity = one.GetValueOrDefault("CODE_SIGN_IDENTITY") ?? one.GetValueOrDefault("CODE_SIGN_IDENTITY[sdk=macosx*]"),
                        CodeSignStyle = C("CODE_SIGN_STYLE"),
                        DevelopmentTeam = C("DEVELOPMENT_TEAM"),
                        Entitlements = C("CODE_SIGN_ENTITLEMENTS"),
                        AppSandbox = C("ENABLE_APP_SANDBOX") switch { "YES" => true, "NO" => false, _ => null },
                        HardenedRuntime = C("ENABLE_HARDENED_RUNTIME") == "YES",
                        CompilationConditions = (C("SWIFT_ACTIVE_COMPILATION_CONDITIONS") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
                            .Where(x => x != "$(inherited)").Distinct().ToList(),
                        SandboxAccess = one.Where(kv => kv.Key.StartsWith("ENABLE_USER_SELECTED_FILES", StringComparison.Ordinal) || kv.Key.StartsWith("ENABLE_INCOMING_NETWORK", StringComparison.Ordinal)
                                                        || kv.Key.StartsWith("ENABLE_OUTGOING_NETWORK", StringComparison.Ordinal) || kv.Key.StartsWith("ENABLE_RESOURCE_ACCESS_", StringComparison.Ordinal)
                                                        || kv.Key.StartsWith("ENABLE_FILE_ACCESS_", StringComparison.Ordinal))
                            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
                        Architectures = C("ARCHS"),
                        GeneratedInfoPlist = C("GENERATE_INFOPLIST_FILE") == "YES",
                        InfoPlistFile = C("INFOPLIST_FILE"),
                    });
                }
                var products = ((t.GetValueOrDefault("packageProductDependencies") as List<object>) ?? []).Select(Obj).OfType<Dictionary<string, object>>()
                    .Select(d => d.GetValueOrDefault("productName") as string).OfType<string>().ToList();
                var synced = ((t.GetValueOrDefault("fileSystemSynchronizedGroups") as List<object>) ?? []).Select(Obj).OfType<Dictionary<string, object>>()
                    .Select(g => g.GetValueOrDefault("path") as string).OfType<string>().ToList();
                var infoPlist = Setting("INFOPLIST_FILE");
                var plist = infoPlist is null ? null : ReadPlistStrings(index, AndroidGradleAnalyzer.Join(dir, infoPlist));
                string? Literal(string? v) => v is { Length: > 0 } && !v.Contains("$(", StringComparison.Ordinal) && !v.Contains("${", StringComparison.Ordinal) ? v : null;
                targets.Add(new MacXcodeTargetInfo
                {
                    Name = name,
                    ProductType = t.GetValueOrDefault("productType") as string ?? "",
                    ProductName = Setting("PRODUCT_NAME") ?? name,
                    BundleIdentifier = Setting("PRODUCT_BUNDLE_IDENTIFIER"),
                    DeploymentTarget = Setting("MACOSX_DEPLOYMENT_TARGET"),
                    MarketingVersion = Setting("MARKETING_VERSION") ?? Literal(plist?.GetValueOrDefault("CFBundleShortVersionString")),
                    BuildVersion = Setting("CURRENT_PROJECT_VERSION") ?? Literal(plist?.GetValueOrDefault("CFBundleVersion")),
                    DisplayName = Literal(Setting("INFOPLIST_KEY_CFBundleDisplayName")) ?? Literal(plist?.GetValueOrDefault("CFBundleDisplayName"))
                                  ?? Literal(plist?.GetValueOrDefault("CFBundleName")),
                    InfoPlistFile = infoPlist,
                    CodeSignIdentity = settings.GetValueOrDefault("CODE_SIGN_IDENTITY") ?? settings.GetValueOrDefault("CODE_SIGN_IDENTITY[sdk=macosx*]"),
                    CodeSignStyle = Setting("CODE_SIGN_STYLE"),
                    DevelopmentTeam = Setting("DEVELOPMENT_TEAM"),
                    Architectures = Setting("ARCHS"),
                    Frameworks = frameworks.Distinct(StringComparer.Ordinal).ToList(),
                    Entitlements = Setting("CODE_SIGN_ENTITLEMENTS"),
                    HardenedRuntime = Setting("ENABLE_HARDENED_RUNTIME") == "YES",
                    AppSandbox = Setting("ENABLE_APP_SANDBOX") switch { "YES" => true, "NO" => false, _ => null },
                    GeneratedInfoPlist = Setting("GENERATE_INFOPLIST_FILE") == "YES",
                    PackageProducts = products,
                    SynchronizedFolders = synced,
                    ConfigurationFacts = perConfig,
                });
            }
        }
        catch (Exception ex) when (ex is FormatException or KeyNotFoundException or InvalidCastException)
        {
            parseError = $"project.pbxproj could not be parsed: {ex.Message}";
        }

        var schemes = index.Files
            .Where(f => f.StartsWith(projectPath + "/", StringComparison.Ordinal) && f.EndsWith(".xcscheme", StringComparison.Ordinal))
            .Select(f => XcodeProjectAnalyzer.Scheme(index, f, f.Contains("/xcshareddata/", StringComparison.Ordinal)))
            .ToList();
        var workspace = index.Files
            .Where(f => f.EndsWith(".xcworkspace/contents.xcworkspacedata", StringComparison.Ordinal) && !f.Contains(".xcodeproj/", StringComparison.Ordinal))
            .Select(f => f[..f.LastIndexOf('/')])
            .OrderBy(w => AndroidGradleAnalyzer.Dir(w) == dir ? 0 : 1).ThenBy(SourceIndex.Depth)
            .FirstOrDefault(w => XcodeProjectAnalyzer.WorkspaceReferences(index, w).Any(r => WorkspaceRef(w, r) == projectPath));
        if (workspace is not null)
            schemes.AddRange(index.Files
                .Where(f => f.StartsWith(workspace + "/xcshareddata/", StringComparison.Ordinal) && f.EndsWith(".xcscheme", StringComparison.Ordinal))
                .Select(f => XcodeProjectAnalyzer.Scheme(index, f, true)));
        // Shared schemes first: they are what the project's author committed; user schemes are per-developer.
        schemes = schemes.OrderByDescending(s => s.Shared).DistinctBy(s => s.Name).ToList();

        // Sources next to the project (not dependencies, not build output).
        var sources = index.Files.Where(f => (dir.Length == 0 || f.StartsWith(dir + "/", StringComparison.Ordinal))
                                             && !f.Split('/').Any(s => s.EndsWith(".xcodeproj", StringComparison.Ordinal) || s is "Pods" or "Carthage" or "build" or "DerivedData" or ".build"))
            .ToList();
        var languages = new List<string>();
        if (sources.Any(f => f.EndsWith(".swift", StringComparison.Ordinal))) languages.Add("Swift");
        if (sources.Any(f => f.EndsWith(".m", StringComparison.Ordinal) || f.EndsWith(".mm", StringComparison.Ordinal))) languages.Add("Objective-C");
        if (sources.Any(f => f.EndsWith(".c", StringComparison.Ordinal) || f.EndsWith(".cpp", StringComparison.Ordinal))) languages.Add("C/C++");
        var ui = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var f in sources.Where(f => f.EndsWith(".swift", StringComparison.Ordinal) || f.EndsWith(".m", StringComparison.Ordinal) || f.EndsWith(".h", StringComparison.Ordinal)).Take(400))
        {
            var text = AndroidGradleAnalyzer.Read(index, f);
            if (SwiftUiImport().IsMatch(text)) ui.Add("SwiftUI");
            if (AppKitImport().IsMatch(text)) ui.Add("AppKit");
        }

        var nearby = index.Files.Where(f => AndroidGradleAnalyzer.Dir(f) == dir).ToList();
        var licenses = nearby.Where(f => LicenseName().IsMatch(SourceIndex.FileName(f))).Order(StringComparer.Ordinal).ToList();
        var scripts = nearby.Where(f => f.EndsWith(".command", StringComparison.Ordinal) || f.EndsWith(".sh", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
        var appIcon = sources.FirstOrDefault(f => f.EndsWith("AppIcon.appiconset/Contents.json", StringComparison.Ordinal)) is { } icon ? icon[..icon.LastIndexOf('/')] : null;
        var catalogs = sources.Select(f => f.IndexOf(".xcassets/", StringComparison.Ordinal) is var i and > 0 ? f[..(i + ".xcassets".Length)] : null)
            .OfType<string>().Distinct().Order(StringComparer.Ordinal).ToList();
        var iconImages = appIcon is null ? [] : IconImages(index, appIcon);
        var entitlementFiles = sources.Where(f => f.EndsWith(".entitlements", StringComparison.Ordinal))
            .ToDictionary(f => f, f => (IReadOnlyList<string>)(ReadPlistKeys(index, f) ?? []), StringComparer.Ordinal);
        // Package.resolved: next to the project's own workspace data, or the sibling workspace's.
        var resolvedPath = new[] { projectPath + "/project.xcworkspace/xcshareddata/swiftpm/Package.resolved", workspace is null ? null : workspace + "/xcshareddata/swiftpm/Package.resolved" }
            .OfType<string>().FirstOrDefault(index.Files.Contains);
        var pins = resolvedPath is null ? [] : ReadPins(AndroidGradleAnalyzer.Read(index, resolvedPath));

        return new MacXcodeProjectInfo
        {
            ProjectPath = projectPath,
            WorkspacePath = workspace,
            Schemes = schemes,
            Targets = targets,
            Configurations = configurations.Count > 0 ? configurations : ["Debug", "Release"],
            UsesSwiftPackages = usesPackages,
            UsesCocoaPods = index.Files.Contains(AndroidGradleAnalyzer.Join(dir, "Podfile")),
            Languages = languages,
            UiFrameworks = ui.ToList(),
            LicenseFiles = licenses,
            HelperScripts = scripts,
            AppIconSet = appIcon,
            AssetCatalogs = catalogs,
            AppIconImages = iconImages,
            PackageReferences = packageRefs,
            ResolvedPackages = pins,
            EntitlementFiles = entitlementFiles,
            ObjectVersion = objectVersion,
            ParseError = parseError,
        };
    }

    /// <summary>
    /// A workspace FileRef location ("group:App.xcodeproj", "container:Apps/App.xcodeproj", "absolute:…") as a path relative
    /// to the workspace source; null for absolute or unknown references.
    /// </summary>
    public static string? WorkspaceRef(string workspace, string location)
    {
        var colon = location.IndexOf(':');
        var (kind, rel) = colon < 0 ? ("group", location) : (location[..colon], location[(colon + 1)..]);
        if (kind is not ("group" or "container" or "self") || rel.Length == 0 || rel.StartsWith('/')) return null;
        var parts = new List<string>(AndroidGradleAnalyzer.Dir(workspace).Split('/', StringSplitOptions.RemoveEmptyEntries));
        foreach (var seg in rel.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".") continue;
            if (seg == "..") { if (parts.Count == 0) return null; parts.RemoveAt(parts.Count - 1); }
            else parts.Add(seg);
        }
        return string.Join('/', parts);
    }

    /// <summary>Images an icon set's Contents.json names that exist in the set's folder.</summary>
    private static IReadOnlyList<string> IconImages(SourceIndex index, string iconSet)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(AndroidGradleAnalyzer.Read(index, iconSet + "/Contents.json"));
            if (!doc.RootElement.TryGetProperty("images", out var images) || images.ValueKind != System.Text.Json.JsonValueKind.Array) return [];
            return images.EnumerateArray().Select(i => i.TryGetProperty("filename", out var f) ? f.GetString() : null).OfType<string>()
                .Where(f => index.Files.Contains(iconSet + "/" + f)).Distinct().ToList();
        }
        catch (System.Text.Json.JsonException) { return []; }
    }

    /// <summary>Package.resolved (version 1, 2 or 3): identity, location and the pinned version or revision.</summary>
    public static IReadOnlyList<MacXcodePackagePin> ReadPins(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            System.Text.Json.JsonElement pins;
            if (root.TryGetProperty("pins", out var p2)) pins = p2;                                           // v2/v3
            else if (root.TryGetProperty("object", out var o) && o.TryGetProperty("pins", out var p1)) pins = p1;   // v1
            else return [];
            string? S(System.Text.Json.JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
            return pins.EnumerateArray().Select(pin =>
            {
                var state = pin.TryGetProperty("state", out var st) ? st : default;
                return new MacXcodePackagePin(S(pin, "identity") ?? S(pin, "package") ?? "?", S(pin, "location") ?? S(pin, "repositoryURL") ?? "",
                    state.ValueKind == System.Text.Json.JsonValueKind.Object ? S(state, "version") : null,
                    state.ValueKind == System.Text.Json.JsonValueKind.Object ? S(state, "revision") : null);
            }).ToList();
        }
        catch (System.Text.Json.JsonException) { return []; }
    }

    /// <summary>Keys of an XML property list's top-level dictionary (an .entitlements file).</summary>
    private static IReadOnlyList<string>? ReadPlistKeys(SourceIndex index, string path)
    {
        var text = AndroidGradleAnalyzer.Read(index, path);
        if (text.Length == 0) return null;
        try
        {
            var settings = new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Ignore, XmlResolver = null };
            using var reader = System.Xml.XmlReader.Create(new StringReader(text), settings);
            return XDocument.Load(reader).Root?.Element("dict")?.Elements("key").Select(k => k.Value).ToList();
        }
        catch (System.Xml.XmlException) { return null; }
    }

    /// <summary>
    /// The scheme to build an app target with: a scheme that builds it (shared first), else one named after the target.
    /// Null when the project has no scheme for it (xcodebuild then creates one named after the target).
    /// </summary>
    public static IosSchemeInfo? SchemeFor(MacXcodeProjectInfo project, MacXcodeTargetInfo target) =>
        project.Schemes.FirstOrDefault(s => s.ArchiveTargets.Contains(target.Name))
        ?? project.Schemes.FirstOrDefault(s => s.Name == target.Name);

    /// <summary>Top-level string values of an XML property list (Info.plist as committed; values may contain $(VARS)).</summary>
    private static Dictionary<string, string>? ReadPlistStrings(SourceIndex index, string path)
    {
        var text = AndroidGradleAnalyzer.Read(index, path);
        if (text.Length == 0) return null;
        try
        {
            var settings = new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Ignore, XmlResolver = null };
            using var reader = System.Xml.XmlReader.Create(new StringReader(text), settings);
            var dict = XDocument.Load(reader).Root?.Element("dict");
            if (dict is null) return null;
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var items = dict.Elements().ToList();
            for (var i = 0; i + 1 < items.Count; i++)
                if (items[i].Name == "key" && items[i + 1].Name == "string") result[items[i].Value] = items[i + 1].Value;
            return result;
        }
        catch (System.Xml.XmlException) { return null; }
    }

    [GeneratedRegex(@"^\s*import\s+SwiftUI\b", RegexOptions.Multiline)]
    private static partial Regex SwiftUiImport();

    [GeneratedRegex(@"^\s*(?:import\s+(?:AppKit|Cocoa)\b|@import\s+(?:AppKit|Cocoa)\b|#import\s+<(?:AppKit|Cocoa)/)", RegexOptions.Multiline)]
    private static partial Regex AppKitImport();

    [GeneratedRegex(@"^(?:LICEN[CS]E|COPYING|NOTICE|AUTHORS)(?:[.-][\w.-]*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex LicenseName();
}
