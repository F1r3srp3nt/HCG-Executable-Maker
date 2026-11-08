using System.Text;

namespace HCG.ExecutableMaker.Porting.Apple;

/// <summary>
/// Lightweight Swift source model for porting analysis: original lines, the same lines with comments and string
/// contents blanked out (so API matches are not fooled by text), and whether each line is compiled for iOS/macOS
/// given the <c>#if</c> directives around it. Read-only; never executes anything.
/// </summary>
public sealed class SwiftSource
{
    public string Path { get; }
    public string Text { get; }
    public IReadOnlyList<string> Lines { get; }
    /// <summary>Lines with comments and string literal contents replaced by spaces.</summary>
    public IReadOnlyList<string> Code { get; }
    public IReadOnlyList<bool> ActiveOnIOS { get; }
    public IReadOnlyList<bool> ActiveOnMacOS { get; }
    public bool IsDirective(int line) => Code[line].TrimStart().StartsWith('#');

    public SwiftSource(string path, string text)
    {
        Path = path;
        Text = text.Replace("\r\n", "\n");
        Lines = Text.Split('\n');
        Code = Strip(Text).Split('\n');
        (ActiveOnIOS, ActiveOnMacOS) = Conditions(Code);
    }

    /// <summary>Blanks comments (incl. nested block comments) and string contents, keeping line structure.</summary>
    internal static string Strip(string s)
    {
        var sb = new StringBuilder(s.Length);
        var i = 0;
        var blockDepth = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (blockDepth > 0)
            {
                if (c == '/' && Peek(s, i + 1) == '*') { blockDepth++; sb.Append("  "); i += 2; continue; }
                if (c == '*' && Peek(s, i + 1) == '/') { blockDepth--; sb.Append("  "); i += 2; continue; }
                sb.Append(c == '\n' ? '\n' : ' '); i++; continue;
            }
            if (c == '/' && Peek(s, i + 1) == '/')
            {
                while (i < s.Length && s[i] != '\n') { sb.Append(' '); i++; }
                continue;
            }
            if (c == '/' && Peek(s, i + 1) == '*') { blockDepth = 1; sb.Append("  "); i += 2; continue; }
            if (c == '"' || (c == '#' && RawStringStart(s, i) > 0))
            {
                i = SkipString(s, i, sb);
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    private static char Peek(string s, int i) => i < s.Length ? s[i] : '\0';

    private static int RawStringStart(string s, int i)
    {
        var hashes = 0;
        while (i + hashes < s.Length && s[i + hashes] == '#') hashes++;
        return i + hashes < s.Length && s[i + hashes] == '"' ? hashes : 0;
    }

    /// <summary>Copies the delimiters, blanks the contents (keeps interpolated code, which can contain API calls).</summary>
    private static int SkipString(string s, int i, StringBuilder sb)
    {
        var hashes = s[i] == '#' ? RawStringStart(s, i) : 0;
        for (var h = 0; h < hashes; h++) { sb.Append('#'); i++; }
        var multi = string.CompareOrdinal(s, i, "\"\"\"", 0, 3) == 0;
        var quote = multi ? "\"\"\"" : "\"";
        sb.Append(quote);
        i += quote.Length;
        var terminator = quote + new string('#', hashes);
        while (i < s.Length)
        {
            if (string.CompareOrdinal(s, i, terminator, 0, terminator.Length) == 0)
            {
                sb.Append(terminator);
                return i + terminator.Length;
            }
            var c = s[i];
            if (!multi && c == '\n') { sb.Append('\n'); return i + 1; } // unterminated: stop at line end
            if (c == '\\' && string.CompareOrdinal(s, i + 1, new string('#', hashes), 0, hashes) == 0)
            {
                var after = i + 1 + hashes;
                if (after < s.Length && s[after] == '(')
                {
                    // String interpolation: keep the code inside the parentheses.
                    sb.Append(new string(' ', after - i)).Append('(');
                    i = after + 1;
                    var depth = 1;
                    while (i < s.Length && depth > 0)
                    {
                        if (s[i] == '"') { i = SkipString(s, i, sb); continue; }
                        if (s[i] == '(') depth++;
                        else if (s[i] == ')') depth--;
                        sb.Append(s[i] == '\n' ? '\n' : s[i]);
                        i++;
                    }
                    continue;
                }
                sb.Append(new string(' ', Math.Min(2 + hashes, s.Length - i)));
                i += 2 + hashes;
                continue;
            }
            sb.Append(c == '\n' ? '\n' : ' ');
            i++;
        }
        return i;
    }

    private sealed class Frame(bool ios, bool mac, bool parentIos, bool parentMac)
    {
        public bool Ios = ios, Mac = mac, TakenIos = ios, TakenMac = mac;
        public readonly bool ParentIos = parentIos, ParentMac = parentMac;
    }

    private static (bool[] Ios, bool[] Mac) Conditions(IReadOnlyList<string> code)
    {
        var ios = new bool[code.Count];
        var mac = new bool[code.Count];
        var stack = new Stack<Frame>();
        bool curIos = true, curMac = true;
        for (var n = 0; n < code.Count; n++)
        {
            var t = code[n].Trim();
            if (t.StartsWith("#if ", StringComparison.Ordinal) || t == "#if")
            {
                var cond = t.Length > 3 ? t[3..] : "";
                var ci = Evaluate(cond, "iOS");
                var cm = Evaluate(cond, "macOS");
                stack.Push(new Frame(ci, cm, curIos, curMac));
                ios[n] = curIos; mac[n] = curMac;
                curIos = curIos && ci;
                curMac = curMac && cm;
                continue;
            }
            if ((t.StartsWith("#elseif ", StringComparison.Ordinal) || t == "#else") && stack.Count > 0)
            {
                var f = stack.Peek();
                var cond = t == "#else" ? "true" : t[8..];
                var ci = !f.TakenIos && Evaluate(cond, "iOS");
                var cm = !f.TakenMac && Evaluate(cond, "macOS");
                f.TakenIos |= ci; f.TakenMac |= cm;
                ios[n] = f.ParentIos; mac[n] = f.ParentMac;
                curIos = f.ParentIos && ci;
                curMac = f.ParentMac && cm;
                continue;
            }
            if (t.StartsWith("#endif", StringComparison.Ordinal) && stack.Count > 0)
            {
                var f = stack.Pop();
                curIos = f.ParentIos; curMac = f.ParentMac;
                ios[n] = curIos; mac[n] = curMac;
                continue;
            }
            ios[n] = curIos;
            mac[n] = curMac;
        }
        return (ios, mac);
    }

    /// <summary>
    /// Evaluates a Swift compilation condition for a platform. Platform checks are exact; unknown flags (DEBUG, custom
    /// -D flags, swift(>=…), arch, simulator) are assumed true so code is analysed rather than silently skipped.
    /// </summary>
    internal static bool Evaluate(string condition, string platform)
    {
        var p = new CondParser(condition, platform);
        try { return p.ParseOr(); }
        catch (FormatException) { return true; }
    }

    private sealed class CondParser(string s, string platform)
    {
        private int _i;

        public bool ParseOr()
        {
            var v = ParseAnd();
            while (Match("||")) v = ParseAnd() | v;
            return v;
        }

        private bool ParseAnd()
        {
            var v = ParseUnary();
            while (Match("&&")) v = ParseUnary() & v;
            return v;
        }

        private bool ParseUnary()
        {
            Skip();
            if (Match("!")) return !ParseUnary();
            if (Match("("))
            {
                var v = ParseOr();
                if (!Match(")")) throw new FormatException();
                return v;
            }
            var name = Identifier();
            Skip();
            if (_i < s.Length && s[_i] == '(')
            {
                var start = ++_i;
                var depth = 1;
                while (_i < s.Length && depth > 0) { if (s[_i] == '(') depth++; else if (s[_i] == ')') depth--; _i++; }
                var arg = s[start..(_i - 1)].Trim();
                return name switch
                {
                    "os" => arg switch
                    {
                        "macOS" or "OSX" => platform == "macOS",
                        "iOS" => platform == "iOS",
                        _ => false,
                    },
                    "canImport" => arg switch
                    {
                        "AppKit" or "Cocoa" or "Carbon" => platform == "macOS",
                        "UIKit" => platform == "iOS",
                        _ => true,
                    },
                    "targetEnvironment" => arg != "macCatalyst",
                    _ => true,
                };
            }
            return name switch { "false" => false, _ => true };
        }

        private string Identifier()
        {
            Skip();
            var start = _i;
            while (_i < s.Length && (char.IsLetterOrDigit(s[_i]) || s[_i] == '_')) _i++;
            if (_i == start) throw new FormatException();
            return s[start.._i];
        }

        private bool Match(string token)
        {
            Skip();
            if (string.CompareOrdinal(s, _i, token, 0, token.Length) != 0) return false;
            _i += token.Length;
            return true;
        }

        private void Skip() { while (_i < s.Length && char.IsWhiteSpace(s[_i])) _i++; }
    }

    /// <summary>
    /// The last line of the balanced construct starting on <paramref name="line"/> (braces and parentheses), extended
    /// over a following member-access chain (<c>.modifier(…)</c> lines). Returns -1 if it never balances.
    /// </summary>
    public int EndOfConstruct(int line, bool includeModifierChain = true)
    {
        var depth = 0;
        var opened = false;
        for (var n = line; n < Code.Count; n++)
        {
            foreach (var c in Code[n])
            {
                if (c is '{' or '(' or '[') { depth++; opened = true; }
                else if (c is '}' or ')' or ']') depth--;
            }
            if (depth < 0) return -1;
            if (depth == 0 && (opened || n == line))
            {
                if (!includeModifierChain) return n;
                var next = n + 1;
                while (next < Code.Count && Code[next].Trim().Length == 0) next++;
                if (next < Code.Count && Code[next].TrimStart().StartsWith('.') && !IsDirective(next))
                {
                    var end = EndOfConstruct(next, includeModifierChain: true);
                    return end < 0 ? -1 : end;
                }
                return n;
            }
        }
        return -1;
    }

    public static string Indent(string line) => line[..(line.Length - line.TrimStart().Length)];
}
