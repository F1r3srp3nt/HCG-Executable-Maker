using System.Text.RegularExpressions;

namespace HCG.ExecutableMaker.ProjectInspection.DotNet;

public static partial class SolutionParser
{
    private static readonly string[] ProjectExtensions = [".csproj", ".fsproj", ".vbproj"];

    /// <summary>Returns project paths referenced by a .sln or .slnx, relative to the solution directory, '/' separated.</summary>
    public static IReadOnlyList<string> GetProjectPaths(string solutionPath)
    {
        if (solutionPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            return MsBuildXml.Elements(MsBuildXml.Load(solutionPath), "Project")
                .Select(e => (string?)e.Attribute("Path"))
                .Where(p => p is not null && IsProjectFile(p))
                .Select(p => Normalize(p!))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var list = new List<string>();
        foreach (var line in File.ReadLines(solutionPath))
        {
            var m = SlnProjectRegex().Match(line);
            if (m.Success && IsProjectFile(m.Groups[2].Value))
                list.Add(Normalize(m.Groups[2].Value));
        }
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsProjectFile(string p) => ProjectExtensions.Any(x => p.EndsWith(x, StringComparison.OrdinalIgnoreCase));
    private static string Normalize(string p)
    {
        var s = p.Trim().Replace('\\', '/');
        while (s.StartsWith("./", StringComparison.Ordinal)) s = s[2..];
        return s;
    }

    [GeneratedRegex(@"^\s*Project\(""\{[^}]+\}""\)\s*=\s*""([^""]*)""\s*,\s*""([^""]+)""")]
    private static partial Regex SlnProjectRegex();
}
