namespace HCG.ExecutableMaker.ProjectInspection.DotNet;

/// <summary>
/// Evaluates an already-expanded MSBuild condition: quoted strings compared with == and != (case-insensitive, as in
/// MSBuild), Exists('path'), !, And, Or and parentheses. Anything else (numeric comparisons, HasTrailingSlash, unknown
/// functions) returns null: the caller treats the condition as undecided rather than guessing.
/// </summary>
public static class MsBuildCondition
{
    public static bool? Evaluate(string condition)
    {
        try
        {
            var p = new Parser(Tokenize(condition));
            var result = p.ParseOr();
            return p.AtEnd ? result : null;
        }
        catch (FormatException) { return null; }
    }

    private enum Kind { Str, Word, Op, LParen, RParen }
    private readonly record struct Token(Kind Kind, string Text);

    private static List<Token> Tokenize(string s)
    {
        var list = new List<Token>();
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '\'')
            {
                var end = s.IndexOf('\'', i + 1);
                if (end < 0) throw new FormatException("unterminated string");
                list.Add(new(Kind.Str, s[(i + 1)..end])); i = end + 1; continue;
            }
            if (c == '(') { list.Add(new(Kind.LParen, "(")); i++; continue; }
            if (c == ')') { list.Add(new(Kind.RParen, ")")); i++; continue; }
            if (c is '=' or '!' or '<' or '>')
            {
                var two = i + 1 < s.Length ? s.Substring(i, 2) : "";
                if (two is "==" or "!=" or "<=" or ">=") { list.Add(new(Kind.Op, two)); i += 2; continue; }
                list.Add(new(Kind.Op, c.ToString())); i++; continue;
            }
            if (char.IsLetterOrDigit(c) || c is '_' or '.' or '-')
            {
                var start = i;
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] is '_' or '.' or '-')) i++;
                list.Add(new(Kind.Word, s[start..i])); continue;
            }
            throw new FormatException($"unexpected '{c}'");
        }
        return list;
    }

    private sealed class Parser(List<Token> tokens)
    {
        private int _i;
        public bool AtEnd => _i >= tokens.Count;
        private Token? Peek => _i < tokens.Count ? tokens[_i] : null;
        private bool IsWord(string w) => Peek is { Kind: Kind.Word } t && t.Text.Equals(w, StringComparison.OrdinalIgnoreCase);

        public bool? ParseOr()
        {
            var left = ParseAnd();
            while (IsWord("Or")) { _i++; var right = ParseAnd(); left = left == true || right == true ? true : left == false && right == false ? false : null; }
            return left;
        }

        private bool? ParseAnd()
        {
            var left = ParseUnary();
            while (IsWord("And")) { _i++; var right = ParseUnary(); left = left == false || right == false ? false : left == true && right == true ? true : null; }
            return left;
        }

        private bool? ParseUnary()
        {
            if (Peek is { Kind: Kind.Op, Text: "!" }) { _i++; var v = ParseUnary(); return v is null ? null : !v; }
            if (Peek is { Kind: Kind.LParen }) { _i++; var v = ParseOr(); Expect(Kind.RParen); return v; }
            if (IsWord("Exists"))
            {
                _i++; Expect(Kind.LParen);
                var arg = Next(Kind.Str).Text;
                Expect(Kind.RParen);
                if (arg.Length == 0) return false;
                return File.Exists(arg) || Directory.Exists(arg);
            }
            var left = Operand();
            if (Peek is { Kind: Kind.Op } op && op.Text is "==" or "!=")
            {
                _i++;
                var right = Operand();
                var eq = string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
                return op.Text == "==" ? eq : !eq;
            }
            if (Peek is { Kind: Kind.Op }) throw new FormatException("unsupported comparison");
            return left.ToLowerInvariant() switch { "true" => true, "false" => false, _ => throw new FormatException("not a boolean") };
        }

        private string Operand() => Peek switch
        {
            { Kind: Kind.Str } t => Take(t).Text,
            { Kind: Kind.Word } t when !t.Text.Equals("And", StringComparison.OrdinalIgnoreCase) && !t.Text.Equals("Or", StringComparison.OrdinalIgnoreCase) => Take(t).Text,
            _ => throw new FormatException("operand expected"),
        };

        private Token Take(Token t) { _i++; return t; }
        private Token Next(Kind k) => Peek is { } t && t.Kind == k ? Take(t) : throw new FormatException($"{k} expected");
        private void Expect(Kind k) => Next(k);
    }
}
