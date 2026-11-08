using System.Text;

namespace HCG.ExecutableMaker.ProjectInspection.Node;

/// <summary>
/// Reads the YAML subset configuration files use (block mappings and sequences, flow [..]/{..} collections, quoted and
/// plain scalars, comments, literal/folded block scalars). Values become <see cref="Dictionary{TKey,TValue}"/>,
/// <see cref="List{T}"/> and string (null for empty or ~/null). Anchors, aliases, tags and multi-document streams are
/// not supported: <see cref="Parse"/> throws <see cref="FormatException"/> rather than guessing.
/// </summary>
public static class MiniYaml
{
    private sealed record Line(int Number, int Indent, string Text);

    public static object? Parse(string text)
    {
        var lines = new List<Line>();
        var raw = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < raw.Length; i++)
        {
            var l = raw[i];
            if (l.TrimStart().StartsWith("---", StringComparison.Ordinal) && lines.Count == 0 && l.Trim() == "---") continue;
            if (l.Trim() == "...") break;
            var content = StripComment(l).TrimEnd();
            if (content.Trim().Length == 0) { lines.Add(new Line(i + 1, -1, "")); continue; }
            if (content.Contains('\t') && content.TrimStart(' ').StartsWith('\t'))
                throw new FormatException($"Line {i + 1}: tabs are not allowed for indentation.");
            var indent = content.Length - content.TrimStart(' ').Length;
            var t = content.Trim();
            if (t.StartsWith('&') || t.StartsWith('*') || t.StartsWith("!!", StringComparison.Ordinal) || t.Contains(": &") || t.Contains(": *") || t.StartsWith("<<", StringComparison.Ordinal))
                throw new FormatException($"Line {i + 1}: anchors, aliases and tags are not supported.");
            lines.Add(new Line(i + 1, indent, t) with { Text = content[indent..] });
        }
        var rawLines = raw;
        var pos = 0;
        Skip(lines, ref pos);
        if (pos >= lines.Count) return null;
        var value = ParseBlock(lines, rawLines, ref pos, lines[pos].Indent);
        Skip(lines, ref pos);
        if (pos < lines.Count) throw new FormatException($"Line {lines[pos].Number}: unexpected indentation.");
        return value;
    }

    private static void Skip(List<Line> lines, ref int pos)
    {
        while (pos < lines.Count && lines[pos].Indent < 0) pos++;
    }

    private static object? ParseBlock(List<Line> lines, string[] raw, ref int pos, int indent)
    {
        Skip(lines, ref pos);
        var first = lines[pos];
        return IsSequenceItem(first.Text) ? ParseSequence(lines, raw, ref pos, indent) : ParseMapping(lines, raw, ref pos, indent);
    }

    private static bool IsSequenceItem(string t) => t == "-" || t.StartsWith("- ", StringComparison.Ordinal);

    private static List<object?> ParseSequence(List<Line> lines, string[] raw, ref int pos, int indent)
    {
        var list = new List<object?>();
        while (true)
        {
            Skip(lines, ref pos);
            if (pos >= lines.Count || lines[pos].Indent != indent || !IsSequenceItem(lines[pos].Text)) break;
            var line = lines[pos];
            var rest = line.Text.Length > 1 ? line.Text[1..] : "";
            var offset = 1 + (rest.Length - rest.TrimStart(' ').Length);
            rest = rest.Trim();
            if (rest.Length == 0)
            {
                pos++;
                Skip(lines, ref pos);
                list.Add(pos < lines.Count && lines[pos].Indent > indent ? ParseBlock(lines, raw, ref pos, lines[pos].Indent) : null);
            }
            else if (IsSequenceItem(rest) || FindMappingColon(rest) >= 0)
            {
                // "- key: value" (or a nested "- - x"): the item is a block that starts on this line.
                lines[pos] = line with { Indent = indent + offset, Text = rest };
                list.Add(ParseBlock(lines, raw, ref pos, indent + offset));
            }
            else
            {
                list.Add(ParseScalarOrFlow(rest, line.Number));
                pos++;
            }
        }
        return list;
    }

    private static Dictionary<string, object?> ParseMapping(List<Line> lines, string[] raw, ref int pos, int indent)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        while (true)
        {
            Skip(lines, ref pos);
            if (pos >= lines.Count || lines[pos].Indent != indent || IsSequenceItem(lines[pos].Text)) break;
            var line = lines[pos];
            var colon = FindMappingColon(line.Text);
            if (colon < 0) throw new FormatException($"Line {line.Number}: expected 'key: value'.");
            var key = Unquote(line.Text[..colon].Trim());
            var value = line.Text[(colon + 1)..].Trim();
            pos++;
            if (value.Length == 0)
            {
                Skip(lines, ref pos);
                if (pos < lines.Count && (lines[pos].Indent > indent || (lines[pos].Indent == indent && IsSequenceItem(lines[pos].Text))))
                    map[key] = ParseBlock(lines, raw, ref pos, lines[pos].Indent);
                else
                    map[key] = null;
            }
            else if (value[0] is '|' or '>')
            {
                var folded = value[0] == '>';
                var sb = new StringBuilder();
                var blockIndent = -1;
                while (pos < lines.Count && (lines[pos].Indent < 0 || lines[pos].Indent > indent))
                {
                    var rawLine = raw[lines[pos].Number - 1];
                    if (lines[pos].Indent >= 0 && blockIndent < 0) blockIndent = lines[pos].Indent;
                    var piece = blockIndent >= 0 && rawLine.Length >= blockIndent ? rawLine[blockIndent..] : rawLine.Trim();
                    sb.Append(piece).Append(folded ? ' ' : '\n');
                    pos++;
                }
                map[key] = sb.ToString().TrimEnd();
            }
            else
            {
                map[key] = ParseScalarOrFlow(value, line.Number);
            }
        }
        return map;
    }

    /// <summary>Index of the ':' that separates a mapping key (followed by a space or end of line, outside quotes).</summary>
    private static int FindMappingColon(string t)
    {
        char quote = '\0';
        var depth = 0;
        for (var i = 0; i < t.Length; i++)
        {
            var c = t[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (i == 0 && c is '"' or '\'') { quote = c; continue; }
            if (c is '[' or '{') { if (i == 0) return -1; depth++; }
            else if (c is ']' or '}') depth--;
            else if (c == ':' && depth == 0 && (i + 1 == t.Length || t[i + 1] == ' ')) return i;
        }
        return -1;
    }

    private static string StripComment(string line)
    {
        char quote = '\0';
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'' && (i == 0 || line[i - 1] is ' ' or ':' or '[' or '{' or ',' or '-')) quote = c;
            else if (c == '#' && (i == 0 || line[i - 1] is ' ' or '\t')) return line[..i];
        }
        return line;
    }

    private static object? ParseScalarOrFlow(string value, int lineNumber)
    {
        if (value[0] is '[' or '{')
        {
            var i = 0;
            var result = ParseFlow(value, ref i, lineNumber);
            if (value[i..].Trim().Length > 0) throw new FormatException($"Line {lineNumber}: unexpected text after a flow collection.");
            return result;
        }
        return Scalar(value);
    }

    private static object? ParseFlow(string s, ref int i, int lineNumber)
    {
        SkipSpaces(s, ref i);
        if (i >= s.Length) throw new FormatException($"Line {lineNumber}: unterminated flow collection (multi-line flow collections are not supported).");
        if (s[i] == '[')
        {
            i++;
            var list = new List<object?>();
            while (true)
            {
                SkipSpaces(s, ref i);
                if (i >= s.Length) throw new FormatException($"Line {lineNumber}: unterminated '['.");
                if (s[i] == ']') { i++; return list; }
                list.Add(ParseFlow(s, ref i, lineNumber));
                SkipSpaces(s, ref i);
                if (i < s.Length && s[i] == ',') i++;
            }
        }
        if (s[i] == '{')
        {
            i++;
            var map = new Dictionary<string, object?>(StringComparer.Ordinal);
            while (true)
            {
                SkipSpaces(s, ref i);
                if (i >= s.Length) throw new FormatException($"Line {lineNumber}: unterminated '{{'.");
                if (s[i] == '}') { i++; return map; }
                var key = ReadFlowToken(s, ref i, keyMode: true);
                SkipSpaces(s, ref i);
                object? v = null;
                if (i < s.Length && s[i] == ':')
                {
                    i++;
                    SkipSpaces(s, ref i);
                    v = i < s.Length && s[i] is '[' or '{' ? ParseFlow(s, ref i, lineNumber) : Scalar(ReadFlowToken(s, ref i, keyMode: false));
                }
                map[Unquote(key)] = v;
                SkipSpaces(s, ref i);
                if (i < s.Length && s[i] == ',') i++;
            }
        }
        return Scalar(ReadFlowToken(s, ref i, keyMode: false));
    }

    private static string ReadFlowToken(string s, ref int i, bool keyMode)
    {
        var start = i;
        if (i < s.Length && s[i] is '"' or '\'')
        {
            var q = s[i++];
            while (i < s.Length && s[i] != q) i++;
            i = Math.Min(i + 1, s.Length);
            return s[start..i];
        }
        while (i < s.Length && s[i] is not (',' or ']' or '}') && !(keyMode && s[i] == ':')) i++;
        return s[start..i].Trim();
    }

    private static void SkipSpaces(string s, ref int i)
    {
        while (i < s.Length && s[i] == ' ') i++;
    }

    private static string? Scalar(string v)
    {
        v = v.Trim();
        if (v.Length == 0 || v is "~" or "null" or "Null" or "NULL") return null;
        return Unquote(v);
    }

    private static string Unquote(string v)
    {
        if (v.Length >= 2 && v[0] == '"' && v[^1] == '"')
            return v[1..^1].Replace("\\\"", "\"").Replace("\\n", "\n").Replace("\\\\", "\\");
        if (v.Length >= 2 && v[0] == '\'' && v[^1] == '\'')
            return v[1..^1].Replace("''", "'");
        return v;
    }
}
