using System.Text.RegularExpressions;
using System.Xml.Linq;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.ProjectInspection.DotNet;

/// <summary>
/// Static (non-evaluating) reader for SDK-style MSBuild project files. Property values that depend on MSBuild
/// evaluation are substituted only from statically known properties; anything else is reported, never guessed.
/// </summary>
public sealed partial class MsBuildProjectFile
{
    public required string Path { get; init; }
    public string? Sdk { get; init; }
    public bool IsSdkStyle { get; init; }
    public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<PackageDependency> PackageReferences { get; init; } = [];
    public IReadOnlyList<string> ProjectReferences { get; init; } = [];
    public IReadOnlyList<string> UnresolvedProperties { get; init; } = [];
    /// <summary>Imports (Directory.*.props chains, explicit &lt;Import&gt;) that were followed, as absolute paths.</summary>
    public IReadOnlyList<string> Imports { get; init; } = [];
    /// <summary>Files this project imports or references that lie outside the imported folder (not read).</summary>
    public IReadOnlyList<string> OutsideWorkspace { get; init; } = [];
    /// <summary>Each &lt;ProjectReference&gt;: the Include text as written and as expanded (relative to the project).</summary>
    public IReadOnlyList<(string Raw, string Expanded)> RawProjectReferences { get; init; } = [];
    /// <summary>Explicit (non-wildcard) file items: Compile, AvaloniaResource, EmbeddedResource, Content, None, BundleResource.</summary>
    public IReadOnlyList<(string ItemType, string Raw, string Expanded)> FileItems { get; init; } = [];
    /// <summary>Imports inside the workspace whose file does not exist (absolute paths).</summary>
    public IReadOnlyList<string> MissingImports { get; init; } = [];
    /// <summary>
    /// Unresolved names that look like environment variables (UPPER_CASE). MSBuild reads them from the environment; HCG
    /// never reads their values (they may be secrets) and treats them as unset, as in its own builds.
    /// </summary>
    public IReadOnlyList<string> EnvironmentInputs => UnresolvedProperties.Where(LooksLikeEnvironmentVariable).ToList();

    public static bool LooksLikeEnvironmentVariable(string name) => name.Length > 1 && name.Any(char.IsLetter) && name.All(c => char.IsUpper(c) || char.IsDigit(c) || c == '_');

    public string? Get(string name) => Properties.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
    public bool IsTrue(string name) => string.Equals(Get(name), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Parses a project file, layering properties from Directory.Build.props files (outermost first) and resolving
    /// central package versions from Directory.Packages.props.
    /// </summary>
    public static MsBuildProjectFile Parse(string projectPath, IReadOnlyList<string>? inheritedPropsFiles = null, IReadOnlyDictionary<string, string>? centralVersions = null, string? workspaceRoot = null)
    {
        var projectDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(projectPath))! + System.IO.Path.DirectorySeparatorChar;
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MSBuildProjectName"] = System.IO.Path.GetFileNameWithoutExtension(projectPath),
            ["MSBuildProjectDirectory"] = projectDir.TrimEnd(System.IO.Path.DirectorySeparatorChar),
            ["MSBuildProjectFullPath"] = System.IO.Path.GetFullPath(projectPath),
        };
        var unresolved = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var ctx = new ImportContext(workspaceRoot is null ? null : System.IO.Path.GetFullPath(workspaceRoot), unresolved);
        foreach (var propsFile in inheritedPropsFiles ?? [])
        {
            try { ReadFile(System.IO.Path.GetFullPath(propsFile), props, ctx, 0); }
            catch (System.Xml.XmlException) { /* malformed props: ignored, reported by build */ }
        }

        var doc = MsBuildXml.Load(projectPath);
        var root = doc.Root ?? throw new System.Xml.XmlException("Project file has no root element.");
        var sdk = (string?)root.Attribute("Sdk")
                  ?? MsBuildXml.Elements(root, "Sdk").Select(e => (string?)e.Attribute("Name")).FirstOrDefault(n => n is not null)
                  ?? MsBuildXml.Elements(root, "Import").Select(e => (string?)e.Attribute("Sdk")).FirstOrDefault(n => n is not null);
        ReadDocument(doc, System.IO.Path.GetFullPath(projectPath), props, ctx, 0);

        foreach (var key in props.Keys.ToList())
            props[key] = Expand(props[key], props, unresolved);

        var packages = new List<PackageDependency>();
        foreach (var e in MsBuildXml.Elements(root, "PackageReference"))
        {
            var include = (string?)e.Attribute("Include");
            if (string.IsNullOrWhiteSpace(include)) continue;
            var version = (string?)e.Attribute("Version") ?? e.Elements().FirstOrDefault(c => c.Name.LocalName == "Version")?.Value;
            if (version is null && centralVersions?.TryGetValue(include, out var central) == true) version = central;
            if (version is not null) version = Expand(version, props, unresolved);
            packages.Add(new PackageDependency(include.Trim(), version?.Trim()));
        }

        // A path built from $(MSBuildThisFileDirectory) is absolute: keep it relative to the project, as MSBuild resolves it.
        string Relative(string v) => System.IO.Path.IsPathRooted(v) ? System.IO.Path.GetRelativePath(projectDir, v).Replace('\\', '/') : v;
        var rawRefs = MsBuildXml.Elements(root, "ProjectReference")
            .Select(e => (string?)e.Attribute("Include"))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => (Raw: s!, Expanded: Relative(Expand(s!, props, unresolved).Replace('\\', '/'))))
            .ToList();
        var projectRefs = rawRefs.Select(r => r.Expanded).ToList();
        var fileItems = new List<(string, string, string)>();
        foreach (var type in FileItemTypes)
            foreach (var e in MsBuildXml.Elements(root, type))
            {
                var include = (string?)e.Attribute("Include");
                if (string.IsNullOrWhiteSpace(include) || include.Contains('*') || include.Contains(';')) continue;
                var expanded = Expand(include, props, new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Replace('\\', '/');
                if (expanded.Contains("$(", StringComparison.Ordinal) || expanded.Contains("@(", StringComparison.Ordinal) || expanded.Contains("%(", StringComparison.Ordinal)) continue;
                fileItems.Add((type, include, Relative(expanded)));
            }
        foreach (var r in projectRefs.Where(r => !r.Contains("$(", StringComparison.Ordinal)))
        {
            var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(projectDir, r));
            if (ctx.Root is not null && !IsWithin(ctx.Root, full)) ctx.Outside.Add(full);
        }

        return new MsBuildProjectFile
        {
            Path = projectPath,
            Sdk = sdk,
            IsSdkStyle = sdk is not null,
            Properties = props,
            PackageReferences = packages,
            ProjectReferences = projectRefs,
            RawProjectReferences = rawRefs,
            FileItems = fileItems,
            MissingImports = ctx.MissingImports.ToList(),
            UnresolvedProperties = unresolved.ToList(),
            Imports = ctx.Followed,
            // Shown relative to the imported folder ("../Windows-executable-Application/…"): that is how the user finds them.
            OutsideWorkspace = ctx.Outside.Select(o => ctx.Root is null ? o : System.IO.Path.GetRelativePath(ctx.Root, o).Replace('\\', '/')).ToList(),
        };
    }

    private static readonly string[] FileItemTypes = ["Compile", "AvaloniaResource", "AvaloniaXaml", "EmbeddedResource", "Content", "None", "BundleResource", "ImageAsset", "InterfaceDefinition"];

    private sealed class ImportContext(string? root, ISet<string> unresolved)
    {
        public string? Root { get; } = root;
        public ISet<string> Unresolved { get; } = unresolved;
        public List<string> Followed { get; } = [];
        public SortedSet<string> Outside { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Visited { get; } = new(StringComparer.Ordinal);
        public SortedSet<string> MissingImports { get; } = new(StringComparer.Ordinal);
    }

    private static bool IsWithin(string root, string path) =>
        path.StartsWith(System.IO.Path.TrimEndingDirectorySeparator(root) + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal) || path == root;

    private static void ReadFile(string path, Dictionary<string, string> props, ImportContext ctx, int depth)
    {
        if (!ctx.Visited.Add(path)) return;
        ctx.Followed.Add(path);
        ReadDocument(MsBuildXml.Load(path), path, props, ctx, depth);
    }

    /// <summary>
    /// Reads PropertyGroups and follows &lt;Import Project="…"&gt; in document order, the way MSBuild evaluates them:
    /// each property and import condition is evaluated with the properties known at that point (an undefined property
    /// is the empty string, as in MSBuild), and values may use the path functions MSBuild itself provides
    /// (<c>$([MSBuild]::NormalizeDirectory(…))</c>, <c>NormalizePath</c>, <c>EnsureTrailingSlash</c>,
    /// <c>$([System.IO.Path]::Combine(…))</c>, <c>GetFullPath</c>). Imports outside the workspace are not read
    /// (recorded in <see cref="OutsideWorkspace"/>). Nothing is executed: no tasks, targets or other property functions,
    /// and the process environment is never read (a property that MSBuild could take from an environment variable is
    /// treated as unset, as it is in Maker's builds).
    /// </summary>
    private static void ReadDocument(XDocument doc, string path, Dictionary<string, string> props, ImportContext ctx, int depth)
    {
        var thisDir = System.IO.Path.GetDirectoryName(path)! + System.IO.Path.DirectorySeparatorChar;
        string Local(string v) => v.Replace("$(MSBuildThisFileDirectory)", thisDir, StringComparison.OrdinalIgnoreCase)
            .Replace("$(MSBuildThisFileFullPath)", path, StringComparison.OrdinalIgnoreCase)
            .Replace("$(MSBuildThisFile)", System.IO.Path.GetFileName(path), StringComparison.OrdinalIgnoreCase);
        // Properties whose condition could not be decided statically: used only if nothing definite sets them.
        var undecided = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in doc.Root?.Elements() ?? [])
        {
            if (e.Name.LocalName == "PropertyGroup")
            {
                var groupCondition = Condition((string?)e.Attribute("Condition"), Local, props);
                if (groupCondition == false) continue;
                foreach (var p in e.Elements())
                {
                    var own = Condition((string?)p.Attribute("Condition"), Local, props);
                    if (own == false) continue;
                    // Values are expanded as they are read, so later properties see earlier values ($(Version) etc.).
                    var value = Expand(Local(p.Value), props, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    if (groupCondition == true && own == true) { props[p.Name.LocalName] = value; undecided.Remove(p.Name.LocalName); }
                    else undecided[p.Name.LocalName] = value;
                }
            }
            else if (e.Name.LocalName == "Import" && (string?)e.Attribute("Project") is { } project && e.Attribute("Sdk") is null)
            {
                var target = Expand(Local(project), props, ctx.Unresolved);
                if (target.Contains("$(", StringComparison.Ordinal) || target.Contains('*') || depth >= 8) continue;
                var full = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(target) ? target : System.IO.Path.Combine(thisDir, target));
                var condition = Condition((string?)e.Attribute("Condition"), Local, props);
                // Undecidable conditions: followed only when the file exists (the usual Exists() guard).
                if (condition == false || (condition is null && !File.Exists(full))) continue;
                if (ctx.Root is not null && !IsWithin(ctx.Root, full)) { ctx.Outside.Add(full); continue; }
                if (!File.Exists(full)) { ctx.MissingImports.Add(full); continue; }
                try { ReadFile(full, props, ctx, depth + 1); }
                catch (System.Xml.XmlException) { }
            }
        }
        foreach (var (k, v) in undecided) props.TryAdd(k, v);
    }

    /// <summary>True/false when a condition can be decided from the properties known so far; null when it cannot.</summary>
    private static bool? Condition(string? condition, Func<string, string> local, IReadOnlyDictionary<string, string> props)
    {
        if (string.IsNullOrWhiteSpace(condition)) return true;
        var expanded = ExpandForCondition(local(condition), props);
        return expanded is null ? null : MsBuildCondition.Evaluate(expanded);
    }

    /// <summary>Expands for a condition: an undefined property is "" (MSBuild semantics). Null if something cannot be evaluated.</summary>
    private static string? ExpandForCondition(string value, IReadOnlyDictionary<string, string> props)
    {
        var unknown = false;
        var result = value;
        for (var i = 0; i < 6 && result.Contains("$(", StringComparison.Ordinal); i++)
        {
            var before = result;
            result = PropertyFunctionRegex().Replace(result, m => EvaluateFunction(m, props) ?? m.Value);
            result = PropertyRefRegex().Replace(result, m =>
            {
                var name = m.Groups[1].Value;
                if (props.TryGetValue(name, out var v)) return v.Contains($"$({name})") ? Unknown(m.Value) : v;
                return SdkDefinedProperties.Contains(name) ? Unknown(m.Value) : "";
            });
            if (result == before) break;
        }
        return unknown || result.Contains("$(", StringComparison.Ordinal) ? null : result;

        string Unknown(string text) { unknown = true; return text; }
    }

    /// <summary>Properties the .NET SDK or MSBuild defines later in evaluation: their value is not known statically.</summary>
    private static readonly HashSet<string> SdkDefinedProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "Configuration", "Platform", "TargetFramework", "TargetFrameworks", "RuntimeIdentifier", "RuntimeIdentifiers",
        "TargetPlatformIdentifier", "TargetFrameworkIdentifier", "OutputPath", "IntermediateOutputPath", "BaseIntermediateOutputPath",
        "MSBuildProjectExtensionsPath", "NuGetPackageRoot", "SolutionDir", "SolutionName", "OS", "MSBuildRuntimeType",
        "DesignTimeBuild", "BuildingInsideVisualStudio", "_IsPublishing", "PublishDir", "Version", "AssemblyName", "RootNamespace",
    };

    private static string Expand(string value, IReadOnlyDictionary<string, string> props, ISet<string> unresolved)
    {
        for (var i = 0; i < 6 && value.Contains("$("); i++)
        {
            var before = value;
            value = PropertyFunctionRegex().Replace(value, m => EvaluateFunction(m, props) ?? m.Value);
            value = PropertyRefRegex().Replace(value, m =>
            {
                var name = m.Groups[1].Value;
                if (props.TryGetValue(name, out var v) && !v.Contains($"$({name})")) return v;
                unresolved.Add(name);
                return m.Value;
            });
            if (value == before) break;
        }
        return value;
    }

    /// <summary>
    /// The path helpers MSBuild provides: pure string/path functions with no side effects. Their arguments must already
    /// be literal (no remaining $(…)); anything else is left unevaluated.
    /// </summary>
    private static string? EvaluateFunction(Match m, IReadOnlyDictionary<string, string> props)
    {
        var type = m.Groups["type"].Value;
        var name = m.Groups["name"].Value;
        var args = SplitArguments(m.Groups["args"].Value);
        if (args is null || args.Any(a => a.Contains("$(", StringComparison.Ordinal))) return null;
        var baseDir = props.TryGetValue("MSBuildProjectDirectory", out var d) ? d : Environment.CurrentDirectory;
        string Full(string p) => System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(p) ? p : System.IO.Path.Combine(baseDir, p));
        string Combine(IEnumerable<string> parts) => parts.Aggregate("", (acc, p) => acc.Length == 0 ? p : System.IO.Path.Combine(acc, p));
        string Slash(string p) => p.EndsWith('/') || p.EndsWith('\\') ? p : p + System.IO.Path.DirectorySeparatorChar;
        switch (type.ToLowerInvariant(), name.ToLowerInvariant())
        {
            case ("msbuild", "normalizedirectory") when args.Count > 0: return Slash(Full(Combine(args)));
            case ("msbuild", "normalizepath") when args.Count > 0: return Full(Combine(args));
            case ("msbuild", "ensuretrailingslash") when args.Count == 1: return args[0].Length == 0 ? "" : Slash(args[0]);
            case ("system.io.path", "combine") when args.Count > 0: return Combine(args);
            case ("system.io.path", "getfullpath") when args.Count == 1: return Full(args[0]);
            default: return null;
        }
    }

    private static List<string>? SplitArguments(string raw)
    {
        var list = new List<string>();
        foreach (var part in raw.Split(',', StringSplitOptions.TrimEntries))
        {
            if (part.Length == 0) continue;
            if (part.Length >= 2 && part[0] == '\'' && part[^1] == '\'') list.Add(part[1..^1]);
            else if (part.StartsWith("$(", StringComparison.Ordinal)) list.Add(part);
            else return null;
        }
        return list;
    }

    [GeneratedRegex(@"\$\(\[(?<type>[A-Za-z_][\w.]*)\]::(?<name>[A-Za-z_]\w*)\((?<args>[^()]*)\)\)")]
    private static partial Regex PropertyFunctionRegex();

    [GeneratedRegex(@"\$\(([A-Za-z_][\w.]*)\)")]
    private static partial Regex PropertyRefRegex();

    /// <summary>Reads PackageVersion items from Directory.Packages.props.</summary>
    public static IReadOnlyDictionary<string, string> ReadCentralVersions(string packagesPropsPath, string? workspaceRoot = null)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ctx = new ImportContext(workspaceRoot is null ? null : System.IO.Path.GetFullPath(workspaceRoot), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        void Read(string path, int depth)
        {
            if (depth > 8 || !ctx.Visited.Add(path)) return;
            XDocument doc;
            try { doc = MsBuildXml.Load(path); }
            catch (System.Xml.XmlException) { return; }
            var thisDir = System.IO.Path.GetDirectoryName(path)! + System.IO.Path.DirectorySeparatorChar;
            foreach (var e in doc.Root?.Elements() ?? [])
            {
                switch (e.Name.LocalName)
                {
                    case "PropertyGroup":
                        foreach (var p in e.Elements()) props[p.Name.LocalName] = Expand(p.Value.Replace("$(MSBuildThisFileDirectory)", thisDir, StringComparison.OrdinalIgnoreCase), props, new HashSet<string>());
                        break;
                    case "Import" when (string?)e.Attribute("Project") is { } project:
                        var target = Expand(project.Replace("$(MSBuildThisFileDirectory)", thisDir, StringComparison.OrdinalIgnoreCase), props, new HashSet<string>());
                        if (target.Contains("$(", StringComparison.Ordinal)) break;
                        var full = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(target) ? target : System.IO.Path.Combine(thisDir, target));
                        if (ctx.Root is not null && !IsWithin(ctx.Root, full)) break;
                        if (File.Exists(full)) Read(full, depth + 1);
                        break;
                    case "ItemGroup":
                        foreach (var pv in e.Elements().Where(x => x.Name.LocalName == "PackageVersion"))
                            if ((string?)pv.Attribute("Include") is { } include && (string?)pv.Attribute("Version") is { } version)
                                result[include] = Expand(version, props, new HashSet<string>());
                        break;
                }
            }
        }
        Read(System.IO.Path.GetFullPath(packagesPropsPath), 0);
        return result;
    }
}
