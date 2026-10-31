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

    public string? Get(string name) => Properties.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
    public bool IsTrue(string name) => string.Equals(Get(name), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Parses a project file, layering properties from Directory.Build.props files (outermost first) and resolving
    /// central package versions from Directory.Packages.props.
    /// </summary>
    public static MsBuildProjectFile Parse(string projectPath, IReadOnlyList<string>? inheritedPropsFiles = null, IReadOnlyDictionary<string, string>? centralVersions = null)
    {
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MSBuildProjectName"] = System.IO.Path.GetFileNameWithoutExtension(projectPath),
        };
        foreach (var propsFile in inheritedPropsFiles ?? [])
        {
            try { ReadProperties(MsBuildXml.Load(propsFile), props); }
            catch (System.Xml.XmlException) { /* malformed props: ignored, reported by build */ }
        }

        var doc = MsBuildXml.Load(projectPath);
        var root = doc.Root ?? throw new System.Xml.XmlException("Project file has no root element.");
        var sdk = (string?)root.Attribute("Sdk")
                  ?? MsBuildXml.Elements(root, "Sdk").Select(e => (string?)e.Attribute("Name")).FirstOrDefault(n => n is not null)
                  ?? MsBuildXml.Elements(root, "Import").Select(e => (string?)e.Attribute("Sdk")).FirstOrDefault(n => n is not null);
        ReadProperties(doc, props);

        var unresolved = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
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
            .Select(s => s!.Replace('\\', '/'))
            .ToList();

        return new MsBuildProjectFile
        {
            Path = projectPath,
            Sdk = sdk,
            IsSdkStyle = sdk is not null,
            Properties = props,
            PackageReferences = packages,
            ProjectReferences = projectRefs,
            UnresolvedProperties = unresolved.ToList(),
        };
    }

    /// <summary>Reads PropertyGroup values. Unconditional values win over conditional ones.</summary>
    private static void ReadProperties(XDocument doc, Dictionary<string, string> props)
    {
        var conditional = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in MsBuildXml.Elements(doc, "PropertyGroup"))
        {
            var groupConditional = group.Attribute("Condition") is not null;
            foreach (var p in group.Elements())
            {
                var isConditional = groupConditional || p.Attribute("Condition") is not null;
                var target = isConditional ? conditional : props;
                target[p.Name.LocalName] = p.Value;
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
    public static IReadOnlyDictionary<string, string> ReadCentralVersions(string packagesPropsPath)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var e in MsBuildXml.Elements(MsBuildXml.Load(packagesPropsPath), "PackageVersion"))
            {
                var include = (string?)e.Attribute("Include");
                var version = (string?)e.Attribute("Version");
                if (include is not null && version is not null) result[include] = version;
            }
        }
        catch (System.Xml.XmlException) { }
        return result;
    }
}
