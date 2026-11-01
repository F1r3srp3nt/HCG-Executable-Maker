using System.Text.RegularExpressions;
using System.Xml.Linq;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.ProjectInspection.Mobile;

/// <summary>Static analysis of Xcode projects/workspaces and Swift app packages. Never runs xcodebuild.</summary>
public static partial class XcodeProjectAnalyzer
{
    /// <summary>.xcodeproj bundles (relative paths) outside dependency folders.</summary>
    public static IReadOnlyList<string> FindProjects(SourceIndex index) =>
        index.Files.Where(f => f.EndsWith(".xcodeproj/project.pbxproj", StringComparison.Ordinal))
            .Select(f => f[..f.LastIndexOf('/')])
            .Where(p => !p.Split('/').Any(s => s is "Pods" or "Carthage" or "node_modules" or "DerivedData"))
            .OrderBy(SourceIndex.Depth).ThenBy(p => p, StringComparer.Ordinal)
            .ToList();

    public static IosProjectInfo Analyze(SourceIndex index, string projectPath, out List<AnalysisDiagnostic> diagnostics)
    {
        diagnostics = [];
        var pbxPath = projectPath + "/project.pbxproj";
        var targets = new List<IosTargetInfo>();
        var configurations = new List<string>();
        var usesPackages = false;
        try
        {
            var root = (Dictionary<string, object>)OpenStepPlist.Parse(AndroidGradleAnalyzer.Read(index, pbxPath));
            var objects = (Dictionary<string, object>)root["objects"];
            Dictionary<string, object>? Obj(object? id) => id is string s && objects.TryGetValue(s, out var o) ? o as Dictionary<string, object> : null;

            var rootObject = Obj(root.GetValueOrDefault("rootObject"));
            var projectConfigs = Obj(rootObject?.GetValueOrDefault("buildConfigurationList"));
            foreach (var cid in (projectConfigs?.GetValueOrDefault("buildConfigurations") as List<object>) ?? [])
                if (Obj(cid)?.GetValueOrDefault("name") is string n) configurations.Add(n);
            usesPackages = objects.Values.OfType<Dictionary<string, object>>().Any(o => o.GetValueOrDefault("isa") is "XCRemoteSwiftPackageReference" or "XCLocalSwiftPackageReference");

            foreach (var o in objects.Values.OfType<Dictionary<string, object>>().Where(o => o.GetValueOrDefault("isa") as string == "PBXNativeTarget"))
            {
                var settings = new Dictionary<string, string>(StringComparer.Ordinal);
                var list = Obj(o.GetValueOrDefault("buildConfigurationList"));
                // Prefer Release values, fall back to any configuration.
                var configs = ((list?.GetValueOrDefault("buildConfigurations") as List<object>) ?? []).Select(Obj).OfType<Dictionary<string, object>>()
                    .OrderByDescending(c => c.GetValueOrDefault("name") as string == "Release").ToList();
                foreach (var c in configs.AsEnumerable().Reverse())
                    if (c.GetValueOrDefault("buildSettings") is Dictionary<string, object> bs)
                        foreach (var (k, v) in bs) if (v is string sv) settings[k] = sv;
                var bundleId = settings.GetValueOrDefault("PRODUCT_BUNDLE_IDENTIFIER");
                targets.Add(new IosTargetInfo
                {
                    Name = o.GetValueOrDefault("name") as string ?? "?",
                    ProductType = o.GetValueOrDefault("productType") as string ?? "",
                    BundleIdentifier = bundleId is null || bundleId.Contains("$(") ? bundleId : bundleId,
                    DeploymentTarget = settings.GetValueOrDefault("IPHONEOS_DEPLOYMENT_TARGET"),
                    DevelopmentTeam = settings.GetValueOrDefault("DEVELOPMENT_TEAM") is { Length: > 0 } t ? t : null,
                    CodeSignStyle = settings.GetValueOrDefault("CODE_SIGN_STYLE"),
                });
            }
        }
        catch (Exception ex) when (ex is FormatException or KeyNotFoundException or InvalidCastException)
        {
            diagnostics.Add(new(DiagnosticSeverity.Error, $"project.pbxproj could not be parsed: {ex.Message}", pbxPath));
        }

        // Shared schemes (committed); user schemes are per-developer and normally not in source control.
        var schemes = index.Files
            .Where(f => f.StartsWith(projectPath + "/", StringComparison.Ordinal) && f.EndsWith(".xcscheme", StringComparison.Ordinal))
            .Select(f => new IosSchemeInfo(Path.GetFileNameWithoutExtension(f), f, f.Contains("/xcshareddata/", StringComparison.Ordinal)))
            .ToList();

        var dir = AndroidGradleAnalyzer.Dir(projectPath);
        var workspace = index.Files
            .Where(f => f.EndsWith(".xcworkspace/contents.xcworkspacedata", StringComparison.Ordinal) && !f.Contains(".xcodeproj/", StringComparison.Ordinal))
            .Select(f => f[..f.LastIndexOf('/')])
            .FirstOrDefault(w => AndroidGradleAnalyzer.Dir(w) == dir && WorkspaceReferences(index, w).Any(r => r.EndsWith(Path.GetFileName(projectPath), StringComparison.Ordinal)));
        if (workspace is not null)
            schemes.AddRange(index.Files
                .Where(f => f.StartsWith(workspace + "/xcshareddata/", StringComparison.Ordinal) && f.EndsWith(".xcscheme", StringComparison.Ordinal))
                .Select(f => new IosSchemeInfo(Path.GetFileNameWithoutExtension(f), f, true)));

        var usesPods = index.Files.Contains(AndroidGradleAnalyzer.Join(dir, "Podfile"));
        if (usesPods && !index.Files.Any(f => f.StartsWith(AndroidGradleAnalyzer.Join(dir, "Pods/"), StringComparison.Ordinal)))
            diagnostics.Add(new(DiagnosticSeverity.Warning, "Podfile found but Pods/ is not present. Run `pod install` yourself before building (HCG never runs it automatically).", AndroidGradleAnalyzer.Join(dir, "Podfile")));

        var appTargets = targets.Where(t => t.IsApplication).ToList();
        if (appTargets.Count == 0 && targets.Count > 0)
            diagnostics.Add(new(DiagnosticSeverity.Warning, "No iOS application target (only frameworks, extensions or tests).", pbxPath));
        if (schemes.Count == 0 && appTargets.Count > 0)
            diagnostics.Add(new(DiagnosticSeverity.Info, "No shared schemes committed; Xcode auto-creates one per target (named after the target).", projectPath));

        return new IosProjectInfo
        {
            ProjectPath = projectPath,
            WorkspacePath = workspace,
            Schemes = schemes.DistinctBy(s => s.Name).ToList(),
            Targets = targets,
            Configurations = configurations.Count > 0 ? configurations : ["Debug", "Release"],
            UsesSwiftPackages = usesPackages,
            UsesCocoaPods = usesPods,
        };
    }

    private static IEnumerable<string> WorkspaceReferences(SourceIndex index, string workspace)
    {
        var text = AndroidGradleAnalyzer.Read(index, workspace + "/contents.xcworkspacedata");
        if (text.Length == 0) return [];
        try
        {
            var settings = new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = System.Xml.XmlReader.Create(new StringReader(text), settings);
            return XDocument.Load(reader).Descendants("FileRef").Select(e => (string?)e.Attribute("location") ?? "").ToList();
        }
        catch (System.Xml.XmlException) { return []; }
    }

    /// <summary>Swift package manifests declaring an iOS application product (Swift Playgrounds-style app packages).</summary>
    public static IReadOnlyList<string> FindAppPackages(SourceIndex index) =>
        index.Named("Package.swift")
            .Where(p => !p.Contains(".build/", StringComparison.Ordinal))
            .Where(p => IosAppProductRegex().IsMatch(AndroidGradleAnalyzer.Read(index, p)))
            .ToList();

    [GeneratedRegex(@"\.iOSApplication\s*\(")]
    private static partial Regex IosAppProductRegex();
}
