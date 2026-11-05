using System.Text;

namespace HCG.ExecutableMaker.ProjectInspection.Mobile;

/// <summary>
/// Parser for the old-style (OpenStep/NeXTSTEP) ASCII property-list format used by Xcode's project.pbxproj.
/// Produces <see cref="Dictionary{TKey,TValue}"/>, <see cref="List{T}"/> and <see cref="string"/> values. Pure parsing — nothing is evaluated.
/// </summary>
public sealed class OpenStepPlist
{
    private readonly string _s;
    private int _i;
    // Only supplied by the port editor. Offsets let it replace individual settings while
    // preserving every other byte, including comments and escaped or binary plist values.
    private readonly Action<Dictionary<string, object>, string?, int, int>? _entry;
    private const int MaxDepth = 64;

    private OpenStepPlist(string text, Action<Dictionary<string, object>, string?, int, int>? entry)
    {
        _s = text;
        _entry = entry;
    }

    public static object Parse(string text) => Parse(text, null);

    internal static object Parse(string text, Action<Dictionary<string, object>, string?, int, int>? entry)
    {
        var p = new OpenStepPlist(text, entry);
        if (p._s.StartsWith("// !$*UTF8*$!", StringComparison.Ordinal)) p._i = p._s.IndexOf('\n') + 1;
        var value = p.ParseValue(0);
        p.SkipTrivia();
        return value;
    }

    private object ParseValue(int depth)
    {
        if (depth > MaxDepth) throw new FormatException("Property list nesting is too deep.");
        SkipTrivia();
        if (_i >= _s.Length) throw new FormatException("Unexpected end of property list.");
        return _s[_i] switch
        {
            '{' => ParseDict(depth),
            '(' => ParseArray(depth),
            '"' => ParseQuoted(),
            '<' => ParseData(),
            _ => ParseUnquoted(),
        };
    }

    private Dictionary<string, object> ParseDict(int depth)
    {
        _i++;
        var d = new Dictionary<string, object>(StringComparer.Ordinal);
        _entry?.Invoke(d, null, _i, _i);
        while (true)
        {
            SkipTrivia();
            if (_i >= _s.Length) throw new FormatException("Unterminated dictionary.");
            if (_s[_i] == '}') { _i++; return d; }
            var start = _i;
            var key = ParseValue(depth + 1) as string ?? throw new FormatException("Dictionary key must be a string.");
            SkipTrivia();
            Expect('=');
            var value = ParseValue(depth + 1);
            SkipTrivia();
            Expect(';');
            d[key] = value;
            _entry?.Invoke(d, key, start, _i);
        }
    }

    private List<object> ParseArray(int depth)
    {
        _i++;
        var list = new List<object>();
        while (true)
        {
            SkipTrivia();
            if (_i >= _s.Length) throw new FormatException("Unterminated array.");
            if (_s[_i] == ')') { _i++; return list; }
            list.Add(ParseValue(depth + 1));
            SkipTrivia();
            if (_i < _s.Length && _s[_i] == ',') _i++;
        }
    }

    private string ParseQuoted()
    {
        _i++;
        var sb = new StringBuilder();
        while (_i < _s.Length && _s[_i] != '"')
        {
            if (_s[_i] == '\\' && _i + 1 < _s.Length)
            {
                _i++;
                sb.Append(_s[_i] switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => _s[_i] });
            }
            else sb.Append(_s[_i]);
            _i++;
        }
        Expect('"');
        return sb.ToString();
    }

    private string ParseData()
    {
        var end = _s.IndexOf('>', _i);
        if (end < 0) throw new FormatException("Unterminated data value.");
        var v = _s[(_i + 1)..end];
        _i = end + 1;
        return v;
    }

    private string ParseUnquoted()
    {
        var start = _i;
        while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || "_$+/:.-".Contains(_s[_i]))) _i++;
        if (_i == start) throw new FormatException($"Unexpected character '{_s[_i]}' at offset {_i}.");
        return _s[start.._i];
    }

    private void Expect(char c)
    {
        if (_i >= _s.Length || _s[_i] != c) throw new FormatException($"Expected '{c}' at offset {_i}.");
        _i++;
    }

    private void SkipTrivia()
    {
        while (_i < _s.Length)
        {
            if (char.IsWhiteSpace(_s[_i])) { _i++; continue; }
            if (_s[_i] == '/' && _i + 1 < _s.Length && _s[_i + 1] == '*')
            {
                var end = _s.IndexOf("*/", _i + 2, StringComparison.Ordinal);
                _i = end < 0 ? _s.Length : end + 2;
                continue;
            }
            if (_s[_i] == '/' && _i + 1 < _s.Length && _s[_i + 1] == '/')
            {
                var end = _s.IndexOf('\n', _i);
                _i = end < 0 ? _s.Length : end + 1;
                continue;
            }
            break;
        }
    }
}
