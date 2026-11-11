using System.Text.RegularExpressions;

namespace HCG.ExecutableMaker.Porting.Apple;

/// <summary>
/// Static reading of a Package.swift's targets, products and platforms for the porting assessment. Package.swift is
/// never executed here; constructs this reader does not understand are reported, not guessed.
/// </summary>
internal sealed partial class SwiftManifestText
{
    public sealed record Target(string Name, string Kind, string? Path, IReadOnlyList<Dependency> Dependencies, bool DeclaresResources);
    /// <summary>A target dependency: by name (local target or product), or an explicit product of another package.</summary>
    public sealed record Dependency(string Name, string? Package);
    public sealed record Product(string Name, string Kind, IReadOnlyList<string> Targets);

    public string Text { get; }
    public string Code { get; }
    public IReadOnlyList<Target> Targets { get; }
    public IReadOnlyList<Product> Products { get; }
    public bool HasRemoteDependencies { get; }

    public SwiftManifestText(string text)
    {
        Text = text;
        Code = StripCommentsKeepLength(text);
        var targets = new List<Target>();
        foreach (Match m in TargetRegex().Matches(Code))
        {
            var args = Balanced(Code, m.Index + m.Length - 1);
            if (args is null) continue;
            var name = NameArg().Match(args) is { Success: true } n ? n.Groups[1].Value : null;
            if (name is null) continue;
            var deps = new List<Dependency>();
            if (DependenciesArg().Match(args) is { Success: true } d && Balanced(args, d.Index + d.Length - 1, '[', ']') is { } list)
            {
                foreach (Match p in ProductDep().Matches(list)) deps.Add(new Dependency(p.Groups["name"].Value, p.Groups["pkg"].Value));
                var withoutProducts = ProductDep().Replace(list, "");
                foreach (Match t in TargetDep().Matches(withoutProducts)) deps.Add(new Dependency(t.Groups["name"].Value, null));
                foreach (Match q in QuotedName().Matches(TargetDep().Replace(withoutProducts, ""))) deps.Add(new Dependency(q.Groups[1].Value, null));
            }
            var path = PathArg().Match(args) is { Success: true } pa ? pa.Groups[1].Value : null;
            targets.Add(new Target(name, m.Groups["kind"].Value, path, deps, args.Contains("resources:", StringComparison.Ordinal)));
        }
        Targets = targets;
        var products = new List<Product>();
        foreach (Match m in ProductRegex().Matches(Code))
        {
            var args = Balanced(Code, m.Index + m.Length - 1);
            if (args is null || NameArg().Match(args) is not { Success: true } n) continue;
            var ts = TargetsArg().Match(args) is { Success: true } t && Balanced(args, t.Index + t.Length - 1, '[', ']') is { } tl
                ? QuotedName().Matches(tl).Select(q => q.Groups[1].Value).ToList() : [];
            products.Add(new Product(n.Groups[1].Value, m.Groups["kind"].Value, ts));
        }
        Products = products;
        HasRemoteDependencies = RemotePackage().IsMatch(Code);
    }

    public Target? FindTarget(string name) => Targets.FirstOrDefault(t => t.Name == name);

    /// <summary>Library product that exposes <paramref name="target"/>, preferring one named after it.</summary>
    public Product? LibraryFor(string target) =>
        Products.Where(p => p.Kind == "library" && p.Targets.Contains(target)).OrderBy(p => p.Name == target ? 0 : 1).FirstOrDefault();

    /// <summary>Replaces comments with spaces (newlines kept) so offsets in <see cref="Code"/> match <see cref="Text"/>.</summary>
    internal static string StripCommentsKeepLength(string s)
    {
        var chars = s.ToCharArray();
        var i = 0;
        while (i < chars.Length)
        {
            if (chars[i] == '"')
            {
                var multi = i + 2 < chars.Length && chars[i + 1] == '"' && chars[i + 2] == '"';
                i += multi ? 3 : 1;
                while (i < chars.Length)
                {
                    if (chars[i] == '\\') { i += 2; continue; }
                    if (multi ? i + 2 < chars.Length && chars[i] == '"' && chars[i + 1] == '"' && chars[i + 2] == '"' : chars[i] == '"' || chars[i] == '\n')
                    {
                        i += multi ? 3 : 1;
                        break;
                    }
                    i++;
                }
                continue;
            }
            if (chars[i] == '/' && i + 1 < chars.Length && chars[i + 1] == '/')
            {
                while (i < chars.Length && chars[i] != '\n') chars[i++] = ' ';
                continue;
            }
            if (chars[i] == '/' && i + 1 < chars.Length && chars[i + 1] == '*')
            {
                var depth = 0;
                while (i < chars.Length)
                {
                    if (chars[i] == '/' && i + 1 < chars.Length && chars[i + 1] == '*') { depth++; chars[i] = chars[i + 1] = ' '; i += 2; continue; }
                    if (chars[i] == '*' && i + 1 < chars.Length && chars[i + 1] == '/') { depth--; chars[i] = chars[i + 1] = ' '; i += 2; if (depth == 0) break; continue; }
                    if (chars[i] != '\n') chars[i] = ' ';
                    i++;
                }
                continue;
            }
            i++;
        }
        return new string(chars);
    }

    /// <summary>Contents between the bracket at <paramref name="open"/> and its match (null when unbalanced).</summary>
    internal static string? Balanced(string s, int open, char o = '(', char c = ')')
    {
        if (open < 0 || open >= s.Length || s[open] != o) return null;
        var depth = 0;
        for (var i = open; i < s.Length; i++)
        {
            if (s[i] == o) depth++;
            else if (s[i] == c && --depth == 0) return s[(open + 1)..i];
        }
        return null;
    }

    /// <summary>
    /// Adds <c>.iOS(.vNN)</c> to <c>platforms:</c> (or inserts a platforms list). Returns null when the manifest is not in
    /// a shape this tool edits confidently.
    /// </summary>
    public string? WithIOSPlatform(int iosMajor, out bool alreadyPresent)
    {
        alreadyPresent = false;
        var m = PlatformsArg().Match(Code);
        if (m.Success)
        {
            var open = m.Index + m.Length - 1;
            var inner = Balanced(Code, open, '[', ']');
            if (inner is null) return null;
            if (inner.Contains(".iOS(", StringComparison.Ordinal)) { alreadyPresent = true; return Text; }
            var close = open + inner.Length + 1;
            var trimmed = inner.TrimEnd();
            var sep = trimmed.Length == 0 ? "" : trimmed.EndsWith(',') ? " " : ", ";
            var insertAt = open + 1 + trimmed.Length;
            return Text[..insertAt] + sep + $".iOS(.v{iosMajor})" + Text[insertAt..close] + Text[close..];
        }
        var name = PackageName().Match(Code);
        if (!name.Success) return null;
        var at = name.Index + name.Length;
        return Text[..at] + $"\n    platforms: [.iOS(.v{iosMajor})]," + Text[at..];
    }

    [GeneratedRegex(@"\.(?<kind>executableTarget|target)\s*\(")]
    private static partial Regex TargetRegex();

    [GeneratedRegex(@"\.(?<kind>executable|library)\s*\(")]
    private static partial Regex ProductRegex();

    [GeneratedRegex(@"^\s*name:\s*""([^""]+)""")]
    private static partial Regex NameArg();

    [GeneratedRegex(@"\bdependencies:\s*\[")]
    private static partial Regex DependenciesArg();

    [GeneratedRegex(@"\btargets:\s*\[")]
    private static partial Regex TargetsArg();

    [GeneratedRegex(@"\bpath:\s*""([^""]+)""")]
    private static partial Regex PathArg();

    [GeneratedRegex(@"\.product\(\s*name:\s*""(?<name>[^""]+)""\s*,\s*package:\s*""(?<pkg>[^""]+)""[^)]*\)")]
    private static partial Regex ProductDep();

    [GeneratedRegex(@"\.(target|byName)\(\s*name:\s*""(?<name>[^""]+)""[^)]*\)")]
    private static partial Regex TargetDep();

    [GeneratedRegex(@"""([^""]+)""")]
    private static partial Regex QuotedName();

    [GeneratedRegex(@"\bplatforms:\s*\[")]
    private static partial Regex PlatformsArg();

    [GeneratedRegex(@"Package\s*\(\s*name:\s*""[^""]*""\s*,")]
    private static partial Regex PackageName();

    [GeneratedRegex(@"\.package\s*\(\s*(url|name:\s*""[^""]*""\s*,\s*url)\s*:")]
    private static partial Regex RemotePackage();
}
