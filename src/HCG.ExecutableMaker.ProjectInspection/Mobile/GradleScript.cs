using System.Text;
using System.Text.RegularExpressions;

namespace HCG.ExecutableMaker.ProjectInspection.Mobile;

/// <summary>
/// Heuristic, non-evaluating reader for Gradle build scripts (Groovy and Kotlin DSL). Gradle scripts are programs, so
/// anything computed at configuration time cannot be known statically; such values are reported as unknown.
/// </summary>
public static partial class GradleScript
{
    /// <summary>Removes // and /* */ comments while keeping string literals intact.</summary>
    public static string StripComments(string text)
    {
        var sb = new StringBuilder(text.Length);
        var i = 0;
        char? quote = null;
        while (i < text.Length)
        {
            var c = text[i];
            if (quote is { } q)
            {
                sb.Append(c);
                if (c == '\\' && i + 1 < text.Length) { sb.Append(text[i + 1]); i += 2; continue; }
                if (c == q) quote = null;
                i++;
                continue;
            }
            if (c is '"' or '\'') { quote = c; sb.Append(c); i++; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 2;
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>Returns the body of the first <c>name { … }</c> block (brace-matched), or null.</summary>
    public static string? Block(string text, string name)
    {
        var m = Regex.Match(text, @"(?<![\w.])" + Regex.Escape(name) + @"\s*\{");
        if (!m.Success) return null;
        var depth = 0;
        for (var i = m.Index + m.Length - 1; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}' && --depth == 0) return text[(m.Index + m.Length)..i];
        }
        return null;
    }

    /// <summary>Names of the direct child blocks: <c>name {</c>, <c>create("name") {</c>, <c>getByName("name") {</c>, <c>register("name") {</c>, <c>maybeCreate("name")</c>.</summary>
    public static IReadOnlyList<string> ChildBlockNames(string body)
    {
        var names = new List<string>();
        var depth = 0;
        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] == '{')
            {
                if (depth == 0)
                {
                    var head = body[..i];
                    var line = head[(head.LastIndexOfAny(['\n', ';', '}']) + 1)..].Trim();
                    var m = ChildNameRegex().Match(line);
                    if (m.Success) names.Add(m.Groups["q"].Success && m.Groups["q"].Value.Length > 0 ? m.Groups["q"].Value : m.Groups["id"].Value);
                }
                depth++;
            }
            else if (body[i] == '}') depth--;
        }
        return names.Distinct(StringComparer.Ordinal).ToList();
    }

    [GeneratedRegex(@"^(?:(?:create|getByName|register|maybeCreate|named)\s*\(\s*[""'](?<q>[\w-]+)[""']\s*\)|(?<id>[A-Za-z_][\w]*))$")]
    private static partial Regex ChildNameRegex();

    /// <summary>Finds <c>key "value"</c>, <c>key = "value"</c>, <c>key 'value'</c> or <c>key("value")</c>.</summary>
    public static string? StringValue(string text, string key)
    {
        var m = Regex.Match(text, @"(?<![\w.])" + Regex.Escape(key) + @"\s*(?:=\s*|\(\s*|\s)\s*[""']([^""'$]+)[""']");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Finds an integer literal assigned to any of the given keys (e.g. minSdk / minSdkVersion).</summary>
    public static int? IntValue(string text, params string[] keys)
    {
        foreach (var key in keys)
        {
            var m = Regex.Match(text, @"(?<![\w.])" + Regex.Escape(key) + @"\s*(?:=\s*|\(\s*|\s)\s*(\d+)\b");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var v)) return v;
            // AGP 8.13+ DSL: compileSdk { version = release(36) }
            var block = Block(text, key);
            if (block is not null && Regex.Match(block, @"release\s*\(\s*(\d+)") is { Success: true } r && int.TryParse(r.Groups[1].Value, out var rv)) return rv;
        }
        return null;
    }

    /// <summary>Gradle project paths from settings.gradle[.kts] include statements.</summary>
    public static IReadOnlyList<string> IncludedProjects(string settings)
    {
        var result = new List<string>();
        foreach (Match m in IncludeRegex().Matches(settings))
            foreach (Match s in Regex.Matches(m.Groups[1].Value, @"[""']([^""']+)[""']"))
            {
                var p = s.Groups[1].Value.Trim();
                result.Add(p.StartsWith(':') ? p : ":" + p);
            }
        return result.Distinct(StringComparer.Ordinal).ToList();
    }

    [GeneratedRegex(@"(?<![\w.])include\s*\(?((?:\s*[""'][^""']+[""']\s*,?)+)\)?")]
    private static partial Regex IncludeRegex();
}
