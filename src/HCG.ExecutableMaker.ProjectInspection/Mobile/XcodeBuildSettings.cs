using System.Text;
using System.Text.RegularExpressions;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.ProjectInspection.Mobile;

/// <summary>
/// Resolves a target's effective build settings statically, in the order Xcode layers them (lowest first):
/// the project's .xcconfig, the project's settings, the target's .xcconfig, the target's settings — each for one build
/// configuration. Conditional keys (KEY[sdk=iphonesimulator*], KEY[config=Debug]) are applied for the SDK and configuration
/// being evaluated; $(inherited) refers to the lower layers; $(VAR) / ${VAR} references are expanded, including the
/// :rfc1034identifier, :c99extidentifier, :identifier, :lower and :upper modifiers. #include / #include? in .xcconfig files
/// are followed. Nothing is executed. A value that depends on something only xcodebuild knows is reported as unresolved,
/// never guessed.
/// </summary>
public sealed partial class XcodeBuildSettings
{
    private readonly SourceIndex _index;
    private readonly string _projectDir;
    private readonly Dictionary<string, object> _objects;
    private readonly Dictionary<string, string> _filePaths = new(StringComparer.Ordinal);

    public XcodeBuildSettings(SourceIndex index, string projectPath, Dictionary<string, object> objects, Dictionary<string, object>? rootObject)
    {
        _index = index;
        _projectDir = AndroidGradleAnalyzer.Dir(projectPath);
        _objects = objects;
        if (rootObject?.GetValueOrDefault("mainGroup") is string main) MapGroup(main, _projectDir);
    }

    private Dictionary<string, object>? Obj(object? id) => id is string s && _objects.TryGetValue(s, out var o) ? o as Dictionary<string, object> : null;

    /// <summary>Repository-relative paths of file references and groups, from the group hierarchy (sourceTree &lt;group&gt; / SOURCE_ROOT).</summary>
    private void MapGroup(string id, string parentPath)
    {
        if (Obj(id) is not { } g || _filePaths.ContainsKey(id)) return;
        var path = PathOf(g, parentPath);
        if (path is null) return;
        _filePaths[id] = path;
        if (g.GetValueOrDefault("children") is List<object> children)
            foreach (var c in children.OfType<string>())
                if (Obj(c) is { } child)
                {
                    if (child.GetValueOrDefault("isa") is "PBXGroup" or "PBXVariantGroup" or "PBXFileSystemSynchronizedRootGroup" or "XCVersionGroup") MapGroup(c, path);
                    else if (PathOf(child, path) is { } fp) _filePaths[c] = fp;
                }
    }

    private string? PathOf(Dictionary<string, object> o, string parentPath)
    {
        var p = o.GetValueOrDefault("path") as string;
        return (o.GetValueOrDefault("sourceTree") as string) switch
        {
            "<group>" or null => p is null ? parentPath : Normalize(AndroidGradleAnalyzer.Join(parentPath, p)),
            "SOURCE_ROOT" => p is null ? _projectDir : Normalize(AndroidGradleAnalyzer.Join(_projectDir, p)),
            _ => null, // <absolute>, BUILT_PRODUCTS_DIR, SDKROOT: not part of the imported source
        };
    }

    private static string Normalize(string path)
    {
        var parts = new List<string>();
        foreach (var seg in path.Replace('\\', '/').Split('/'))
        {
            if (seg is "" or ".") continue;
            if (seg == ".." && parts.Count > 0 && parts[^1] != "..") parts.RemoveAt(parts.Count - 1);
            else parts.Add(seg);
        }
        return string.Join('/', parts);
    }

    /// <summary>The .xcconfig a build configuration is based on (baseConfigurationReference, or Xcode 16's anchor + relative path).</summary>
    private string? ConfigFileOf(Dictionary<string, object> configuration)
    {
        if (configuration.GetValueOrDefault("baseConfigurationReference") is string r && _filePaths.TryGetValue(r, out var p)) return p;
        if (configuration.GetValueOrDefault("baseConfigurationReferenceAnchor") is string anchor && _filePaths.TryGetValue(anchor, out var dir)
            && configuration.GetValueOrDefault("baseConfigurationReferenceRelativePath") is string rel)
            return Normalize(AndroidGradleAnalyzer.Join(dir, rel));
        return null;
    }

    private sealed record Assignment(string Key, IReadOnlyList<(string Name, string Pattern)> Conditions, string Value, int Order);
    private sealed record Layer(string Name, IReadOnlyList<Assignment> Assignments);

    private static readonly Regex KeyWithConditions = KeyRegex();

    private static Assignment? ParseKey(string rawKey, string value, int order)
    {
        var m = KeyWithConditions.Match(rawKey.Trim());
        if (!m.Success) return null;
        var conditions = m.Groups["c"].Captures.Select(c =>
        {
            var parts = c.Value.Split('=', 2);
            return (parts[0].Trim(), parts.Length > 1 ? parts[1].Trim() : "*");
        }).ToList();
        return new Assignment(m.Groups["k"].Value, conditions, value, order);
    }

    private Layer SettingsLayer(string name, Dictionary<string, object>? buildSettings)
    {
        var list = new List<Assignment>();
        var i = 0;
        foreach (var (k, v) in buildSettings ?? [])
        {
            var value = v switch { string s => s, List<object> l => string.Join(' ', l.OfType<string>()), _ => null };
            if (value is not null && ParseKey(k, value, i++) is { } a) list.Add(a);
        }
        return new Layer(name, list);
    }

    private Layer? XcconfigLayer(string? path, List<string> notes)
    {
        if (path is null) return null;
        var list = new List<Assignment>();
        var order = 0;
        void Read(string file, int depth, HashSet<string> seen, bool optional)
        {
            if (depth > 8 || !seen.Add(file)) return;
            if (!_index.Files.Contains(file))
            {
                if (!optional) notes.Add($"{file} is referenced but not in the imported source");
                return;
            }
            foreach (var raw in AndroidGradleAnalyzer.Read(_index, file).Split('\n'))
            {
                var line = raw.Trim();
                var comment = line.IndexOf("//", StringComparison.Ordinal);
                if (comment >= 0) line = line[..comment].Trim();
                if (line.Length == 0) continue;
                if (IncludeRegex().Match(line) is { Success: true } inc)
                {
                    var target = inc.Groups["p"].Value;
                    if (target.StartsWith('<')) continue; // developer-dir include (<DEVELOPER_DIR>/…): not part of the source
                    Read(Normalize(AndroidGradleAnalyzer.Join(AndroidGradleAnalyzer.Dir(file), target)), depth + 1, seen, inc.Groups["opt"].Success);
                    continue;
                }
                // The assignment's '=' is the first one outside the [condition=value] brackets.
                var eq = -1;
                for (int i = 0, nesting = 0; i < line.Length && eq < 0; i++)
                    if (line[i] == '[') nesting++;
                    else if (line[i] == ']') nesting--;
                    else if (line[i] == '=' && nesting == 0) eq = i;
                if (eq <= 0) continue;
                var value = line[(eq + 1)..].Trim().TrimEnd(';').Trim();
                if (ParseKey(line[..eq], value, order++) is { } a) list.Add(a);
            }
        }
        Read(path, 0, new HashSet<string>(StringComparer.Ordinal), false);
        return new Layer(path, list);
    }

    private static bool Glob(string pattern, string value) =>
        pattern == "*" || Regex.IsMatch(value, "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase);

    private static bool Matches(Assignment a, string configuration, string sdk) => a.Conditions.All(c => c.Name switch
    {
        "sdk" => Glob(c.Pattern, sdk) || Glob(c.Pattern, sdk + "99.0"),
        "config" => Glob(c.Pattern, configuration),
        "arch" => c.Pattern == "*", // an architecture-specific value does not decide a deployment target or bundle identifier
        _ => false,
    });

    /// <summary>Effective settings of a native target for one configuration and SDK.</summary>
    public IosResolvedSettings Resolve(Dictionary<string, object> target, string configuration, string sdk)
    {
        var notes = new List<string>();
        var layers = new List<Layer>();
        Dictionary<string, object>? ConfigNamed(object? listId) =>
            ((Obj(listId)?.GetValueOrDefault("buildConfigurations") as List<object>) ?? []).Select(Obj).OfType<Dictionary<string, object>>()
                .FirstOrDefault(c => c.GetValueOrDefault("name") as string == configuration);
        var root = _objects.Values.OfType<Dictionary<string, object>>().FirstOrDefault(o => o.GetValueOrDefault("isa") as string == "PBXProject");
        var projectConfig = ConfigNamed(root?.GetValueOrDefault("buildConfigurationList"));
        var targetConfig = ConfigNamed(target.GetValueOrDefault("buildConfigurationList"));
        var targetName = target.GetValueOrDefault("name") as string ?? "?";
        if (projectConfig is not null)
        {
            if (XcconfigLayer(ConfigFileOf(projectConfig), notes) is { } px) layers.Add(px);
            layers.Add(SettingsLayer($"project ({configuration})", projectConfig.GetValueOrDefault("buildSettings") as Dictionary<string, object>));
        }
        if (targetConfig is not null)
        {
            if (XcconfigLayer(ConfigFileOf(targetConfig), notes) is { } tx) layers.Add(tx);
            layers.Add(SettingsLayer($"target {targetName} ({configuration})", targetConfig.GetValueOrDefault("buildSettings") as Dictionary<string, object>));
        }
        else notes.Add($"target {targetName} has no {configuration} configuration");

        var builtins = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TARGET_NAME"] = targetName, ["PROJECT_NAME"] = Path.GetFileNameWithoutExtension(_projectDir.Length == 0 ? "" : _projectDir),
            ["CONFIGURATION"] = configuration, ["PLATFORM_NAME"] = sdk, ["SDK_NAME"] = sdk, ["EFFECTIVE_PLATFORM_NAME"] = "-" + sdk,
            ["PRODUCT_NAME"] = "$(TARGET_NAME)",
        };

        (string? Value, string? Source) Raw(string key)
        {
            string? value = null;
            string? source = null;
            foreach (var layer in layers)
            {
                var winner = layer.Assignments.Where(a => a.Key == key && Matches(a, configuration, sdk))
                    .OrderBy(a => a.Conditions.Count).ThenBy(a => a.Order).LastOrDefault();
                if (winner is null) continue;
                value = InheritedRegex().Replace(winner.Value, value ?? "").Trim();
                source = layer.Name + string.Concat(winner.Conditions.Select(c => $"[{c.Name}={c.Pattern}]"));
            }
            if (value is null && builtins.TryGetValue(key, out var b)) return (b, "Xcode default");
            return (value, source);
        }

        string? Expand(string? value, int depth, HashSet<string> unresolved)
        {
            if (value is null) return null;
            if (depth > 10) { unresolved.Add("(recursive reference)"); return null; }
            var failed = false;
            var result = VariableRegex().Replace(value, m =>
            {
                var name = m.Groups["n1"].Success ? m.Groups["n1"].Value : m.Groups["n2"].Value;
                var modifier = m.Groups["m1"].Success ? m.Groups["m1"].Value : m.Groups["m2"].Value;
                var inner = Expand(Raw(name).Value, depth + 1, unresolved);
                if (inner is null) { unresolved.Add(name); failed = true; return m.Value; }
                return modifier.Length == 0 ? inner : ApplyModifiers(inner, modifier);
            });
            return failed ? null : result;
        }

        IosResolvedSettings Build()
        {
            var unresolved = new HashSet<string>(StringComparer.Ordinal);
            string? Get(string key)
            {
                var raw = Raw(key).Value;
                var expanded = Expand(raw, 0, unresolved);
                if (raw is not null && expanded is null) notes.Add($"{key} = {raw} (depends on {string.Join(", ", unresolved)})");
                return string.IsNullOrWhiteSpace(expanded) ? null : expanded;
            }
            var deployment = Get("IPHONEOS_DEPLOYMENT_TARGET");
            return new IosResolvedSettings
            {
                Configuration = configuration,
                Sdk = sdk,
                DeploymentTarget = deployment,
                DeploymentTargetSource = Raw("IPHONEOS_DEPLOYMENT_TARGET").Source,
                BundleIdentifier = Get("PRODUCT_BUNDLE_IDENTIFIER"),
                MarketingVersion = Get("MARKETING_VERSION"),
                BuildVersion = Get("CURRENT_PROJECT_VERSION"),
                DevelopmentTeam = Get("DEVELOPMENT_TEAM"),
                CodeSignStyle = Get("CODE_SIGN_STYLE"),
                Unresolved = notes.Distinct().ToList(),
            };
        }
        return Build();
    }

    /// <summary>The SDK a target builds with (SDKROOT, resolved for its first configuration), e.g. "iphoneos" or "macosx".</summary>
    public string? SdkRootOf(Dictionary<string, object> target, string configuration)
    {
        foreach (var layerSettings in new[] { TargetConfig(target, configuration), ProjectConfig(configuration) })
            if (layerSettings?.GetValueOrDefault("buildSettings") is Dictionary<string, object> bs && bs.GetValueOrDefault("SDKROOT") is string sdk) return sdk;
        return null;
    }

    private Dictionary<string, object>? TargetConfig(Dictionary<string, object> target, string configuration) =>
        ((Obj(target.GetValueOrDefault("buildConfigurationList"))?.GetValueOrDefault("buildConfigurations") as List<object>) ?? []).Select(Obj).OfType<Dictionary<string, object>>()
            .FirstOrDefault(c => c.GetValueOrDefault("name") as string == configuration);

    private Dictionary<string, object>? ProjectConfig(string configuration)
    {
        var root = _objects.Values.OfType<Dictionary<string, object>>().FirstOrDefault(o => o.GetValueOrDefault("isa") as string == "PBXProject");
        return ((Obj(root?.GetValueOrDefault("buildConfigurationList"))?.GetValueOrDefault("buildConfigurations") as List<object>) ?? []).Select(Obj).OfType<Dictionary<string, object>>()
            .FirstOrDefault(c => c.GetValueOrDefault("name") as string == configuration);
    }

    /// <summary>Names of the targets a target depends on (PBXTargetDependency within this project).</summary>
    public IReadOnlyList<string> DependenciesOf(Dictionary<string, object> target) =>
        ((target.GetValueOrDefault("dependencies") as List<object>) ?? []).Select(Obj).OfType<Dictionary<string, object>>()
            .Select(d => Obj(d.GetValueOrDefault("target"))?.GetValueOrDefault("name") as string).OfType<string>().Distinct().ToList();

    internal static string ApplyModifiers(string value, string modifiers)
    {
        foreach (var mod in modifiers.Split(':', StringSplitOptions.RemoveEmptyEntries))
            value = mod switch
            {
                "rfc1034identifier" => Regex.Replace(value, "[^A-Za-z0-9.-]", "-"),
                "c99extidentifier" or "identifier" => Regex.Replace(value, "[^A-Za-z0-9_]", "_"),
                "lower" => value.ToLowerInvariant(),
                "upper" => value.ToUpperInvariant(),
                _ => value,
            };
        return value;
    }

    [GeneratedRegex(@"^(?<k>[A-Za-z_][A-Za-z0-9_]*)(?:\[(?<c>[^\]]+)\])*$")] private static partial Regex KeyRegex();
    [GeneratedRegex(@"^#include(?<opt>\?)?\s+""(?<p>[^""]+)""")] private static partial Regex IncludeRegex();
    [GeneratedRegex(@"\$\(inherited\)|\$\{inherited\}")] private static partial Regex InheritedRegex();
    [GeneratedRegex(@"\$\((?<n1>[A-Za-z_][A-Za-z0-9_]*)(?<m1>(?::[A-Za-z0-9]+)*)\)|\$\{(?<n2>[A-Za-z_][A-Za-z0-9_]*)(?<m2>(?::[A-Za-z0-9]+)*)\}")] private static partial Regex VariableRegex();
}
