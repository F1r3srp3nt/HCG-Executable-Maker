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

        var projectRefs = MsBuildXml.Elements(root, "ProjectReference")
            .Select(e => (string?)e.Attribute("Include"))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => Expand(s!, props, unresolved).Replace('\\', '/'))
            // A reference built from $(MSBuildThisFileDirectory) is absolute: keep it relative to the project, as MSBuild resolves it.
            .Select(s => System.IO.Path.IsPathRooted(s) ? System.IO.Path.GetRelativePath(projectDir, s).Replace('\\', '/') : s)
            .ToList();
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
            UnresolvedProperties = unresolved.ToList(),
            Imports = ctx.Followed,
            // Shown relative to the imported folder ("../Windows-executable-Application/…"): that is how the user finds them.
            OutsideWorkspace = ctx.Outside.Select(o => ctx.Root is null ? o : System.IO.Path.GetRelativePath(ctx.Root, o).Replace('\\', '/')).ToList(),
        };
    }

    private sealed class ImportContext(string? root, ISet<string> unresolved)
    {
        public string? Root { get; } = root;
        public ISet<string> Unresolved { get; } = unresolved;
        public List<string> Followed { get; } = [];
        public SortedSet<string> Outside { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Visited { get; } = new(StringComparer.Ordinal);
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
    /// Reads PropertyGroups and follows &lt;Import Project="…"&gt; in document order, the way MSBuild layers them.
    /// $(MSBuildThisFileDirectory) and friends are substituted per file. Imports outside the workspace are not read
    /// (recorded in <see cref="OutsideWorkspace"/>); conditional imports are followed only when the condition is
    /// Exists() on the same file. Nothing is executed: no tasks, targets or property functions.
    /// </summary>
    private static void ReadDocument(XDocument doc, string path, Dictionary<string, string> props, ImportContext ctx, int depth)
    {
        var thisDir = System.IO.Path.GetDirectoryName(path)! + System.IO.Path.DirectorySeparatorChar;
        string Local(string v) => v.Replace("$(MSBuildThisFileDirectory)", thisDir, StringComparison.OrdinalIgnoreCase)
            .Replace("$(MSBuildThisFileFullPath)", path, StringComparison.OrdinalIgnoreCase)
            .Replace("$(MSBuildThisFile)", System.IO.Path.GetFileName(path), StringComparison.OrdinalIgnoreCase);
        var conditional = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in doc.Root?.Elements() ?? [])
        {
            if (e.Name.LocalName == "PropertyGroup")
            {
                var groupConditional = e.Attribute("Condition") is not null;
                foreach (var p in e.Elements())
                {
                    var isConditional = groupConditional || p.Attribute("Condition") is not null;
                    // Values are expanded as they are read, so later files see earlier values ($(Version) etc.).
                    var value = Expand(Local(p.Value), props, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    if (isConditional) conditional[p.Name.LocalName] = value; else props[p.Name.LocalName] = value;
                }
            }
            else if (e.Name.LocalName == "Import" && (string?)e.Attribute("Project") is { } project && e.Attribute("Sdk") is null)
            {
                var target = Expand(Local(project), props, ctx.Unresolved);
                if (target.Contains("$(", StringComparison.Ordinal) || target.Contains('*') || depth >= 8) continue;
                var full = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(target) ? target : System.IO.Path.Combine(thisDir, target));
                var condition = (string?)e.Attribute("Condition");
                if (condition is not null && !(condition.Contains("Exists(", StringComparison.OrdinalIgnoreCase) && Expand(Local(condition), props, ctx.Unresolved).Contains(target, StringComparison.Ordinal)))
                    continue;
                if (ctx.Root is not null && !IsWithin(ctx.Root, full)) { ctx.Outside.Add(full); continue; }
                if (!File.Exists(full)) continue;
                try { ReadFile(full, props, ctx, depth + 1); }
                catch (System.Xml.XmlException) { }
            }
        }
        foreach (var (k, v) in conditional) props.TryAdd(k, v);
    }

    private static string Expand(string value, IReadOnlyDictionary<string, string> props, ISet<string> unresolved)
    {
        for (var i = 0; i < 5 && value.Contains("$("); i++)
        {
            value = PropertyRefRegex().Replace(value, m =>
            {
                var name = m.Groups[1].Value;
                if (props.TryGetValue(name, out var v) && !v.Contains($"$({name})")) return v;
                unresolved.Add(name);
                return m.Value;
            });
        }
        return value;
    }

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
