using System.Text.RegularExpressions;

namespace HCG.ExecutableMaker.ProjectInspection.Swift;

/// <summary>
/// App identity configured in a package's own packaging script (e.g. scripts/build-macos.sh writing Info.plist), read
/// as text — scripts are never executed. Used only to prefill the macOS app options so configured bundle identifiers,
/// versions and privacy texts are preserved; every value stays editable and its source is shown.
/// </summary>
public sealed partial record PackagingScriptHints
{
    public required string Script { get; init; }
    public string? Product { get; init; }
    public string? BundleId { get; init; }
    public string? DisplayName { get; init; }
    public string? Version { get; init; }
    public string? BuildNumber { get; init; }
    public string? MinimumMacOS { get; init; }
    public string? Category { get; init; }
    public IReadOnlyDictionary<string, string> UsageDescriptions { get; init; } = new Dictionary<string, string>();
    /// <summary>Custom Info.plist keys the script computes at build time (e.g. a git commit) that HCG does not reproduce.</summary>
    public IReadOnlyList<string> ComputedCustomKeys { get; init; } = [];

    private const int MaxScriptBytes = 256 * 1024;

    /// <summary>Reads hints for <paramref name="product"/> from shell scripts in the package (folder and scripts/).</summary>
    public static PackagingScriptHints? Read(SourceIndex index, string packageDir, string product)
    {
        var prefix = packageDir.Length == 0 ? "" : packageDir + "/";
        var scripts = index.Files
            .Where(f => f.StartsWith(prefix, StringComparison.Ordinal) && f.EndsWith(".sh", StringComparison.Ordinal))
            .Where(f => f[prefix.Length..] is var rel && (!rel.Contains('/') || (rel.StartsWith("scripts/", StringComparison.Ordinal) && rel.Count(c => c == '/') == 1)))
            .Order(StringComparer.Ordinal).Take(20);
        PackagingScriptHints? best = null;
        foreach (var script in scripts)
        {
            var path = index.FullPath(script);
            if (new FileInfo(path).Length > MaxScriptBytes) continue;
            var hints = Parse(File.ReadAllText(path), script);
            if (hints.BundleId is null) continue;
            if (hints.Product is not null && hints.Product != product) continue; // a script for another product
            if (best is null || (hints.Product == product && best.Product != product)) best = hints;
        }
        return best;
    }

    public static PackagingScriptHints Parse(string text, string script)
    {
        var vars = new Dictionary<string, string>(StringComparer.Ordinal);
        var computed = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Assignment().Matches(text))
        {
            var value = m.Groups["v"].Value;
            if (value.Contains("$(", StringComparison.Ordinal) || value.Contains('`')) { computed.Add(m.Groups["k"].Value); continue; }
            vars.TryAdd(m.Groups["k"].Value, value.Trim('"', '\''));
        }
        string? Resolve(string? raw)
        {
            if (raw is null) return null;
            var unresolved = false;
            var value = VarRef().Replace(raw, r =>
            {
                var name = r.Groups[1].Success && r.Groups[1].Length > 0 ? r.Groups[1].Value : r.Groups[2].Value;
                if (vars.TryGetValue(name, out var v)) return v;
                unresolved = true;
                return r.Value;
            });
            return unresolved ? null : value.Trim();
        }
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        var customComputed = new List<string>();
        foreach (Match m in PlistString().Matches(text))
        {
            var key = m.Groups["k"].Value;
            var raw = System.Net.WebUtility.HtmlDecode(m.Groups["v"].Value);
            var refs = VarRef().Matches(raw).Select(r => r.Groups[1].Success && r.Groups[1].Length > 0 ? r.Groups[1].Value : r.Groups[2].Value).ToList();
            if (refs.Any(computed.Contains) && !key.StartsWith("CF", StringComparison.Ordinal) && !key.StartsWith("LS", StringComparison.Ordinal))
            {
                customComputed.Add(key);
                continue;
            }
            if (Resolve(raw) is { Length: > 0 } value) keys.TryAdd(key, value);
        }
        return new PackagingScriptHints
        {
            Script = script,
            Product = keys.GetValueOrDefault("CFBundleExecutable") ?? (vars.TryGetValue("PRODUCT", out var p) ? p : null),
            BundleId = keys.GetValueOrDefault("CFBundleIdentifier"),
            DisplayName = keys.GetValueOrDefault("CFBundleDisplayName") ?? keys.GetValueOrDefault("CFBundleName"),
            Version = keys.GetValueOrDefault("CFBundleShortVersionString"),
            BuildNumber = keys.GetValueOrDefault("CFBundleVersion"),
            MinimumMacOS = keys.GetValueOrDefault("LSMinimumSystemVersion"),
            Category = keys.GetValueOrDefault("LSApplicationCategoryType"),
            UsageDescriptions = keys.Where(k => k.Key.StartsWith("NS", StringComparison.Ordinal) && k.Key.EndsWith("UsageDescription", StringComparison.Ordinal))
                .ToDictionary(k => k.Key, k => k.Value),
            ComputedCustomKeys = customComputed,
        };
    }

    [GeneratedRegex(@"(?m)^\s*(?:export\s+|readonly\s+|local\s+)?(?<k>[A-Za-z_][A-Za-z0-9_]*)=(?<v>""[^""\n]*""|'[^'\n]*'|[^\s#;]*)")]
    private static partial Regex Assignment();

    [GeneratedRegex(@"<key>(?<k>[A-Za-z0-9_.\-]+)</key>\s*<string>(?<v>[^<]*)</string>")]
    private static partial Regex PlistString();

    [GeneratedRegex(@"\$\{(\w+)\}|\$(\w+)")]
    private static partial Regex VarRef();
}
