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
        string? parseError = null;
        try
        {
            var root = (Dictionary<string, object>)OpenStepPlist.Parse(AndroidGradleAnalyzer.Read(index, pbxPath));
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
            .FirstOrDefault(w => AndroidGradleAnalyzer.Dir(w) == dir && XcodeProjectAnalyzer.WorkspaceReferences(index, w).Any(r => r.EndsWith(Path.GetFileName(projectPath), StringComparison.Ordinal)));
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
            ParseError = parseError,
        };
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
