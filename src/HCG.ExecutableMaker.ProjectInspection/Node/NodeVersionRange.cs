using System.Text.RegularExpressions;

namespace HCG.ExecutableMaker.ProjectInspection.Node;

/// <summary>
/// Evaluates npm semver ranges as used in package.json "engines" (">=22.12", "^20.11.0", "18.x || 20.x",
/// ">=18 <21", "~20.10", "20.11.0 - 22", "*"). Pre-release tags are ignored. A range HCG cannot read is reported as
/// unreadable, never assumed to match.
/// </summary>
public static partial class NodeVersionRange
{
    public enum Result { Satisfied, NotSatisfied, Unreadable }

    public static Result Check(string version, string? range)
    {
        if (string.IsNullOrWhiteSpace(range)) return Result.Satisfied;
        if (!TryParse(version, out var v)) return Result.Unreadable;
        var any = false;
        foreach (var alternative in range.Split("||"))
        {
            var comparators = Comparators(alternative.Trim());
            if (comparators is null) return Result.Unreadable;
            if (comparators.All(c => c(v))) any = true;
        }
        return any ? Result.Satisfied : Result.NotSatisfied;
    }

    /// <summary>"v22.12.0" / "22.12" → (22, 12, 0).</summary>
    public static bool TryParse(string text, out (int Major, int Minor, int Patch) version)
    {
        version = default;
        var m = VersionRegex().Match(text.Trim());
        if (!m.Success) return false;
        version = (int.Parse(m.Groups[1].Value), m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0, m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
        return true;
    }

    private sealed record Partial(int Major, int? Minor, int? Patch)
    {
        public (int, int, int) Low => (Major, Minor ?? 0, Patch ?? 0);
    }

    private static Partial? ParsePartial(string text)
    {
        var m = PartialRegex().Match(text.Trim());
        if (!m.Success) return null;
        static int? Part(Group g) => g.Success && g.Value is not ("x" or "X" or "*") ? int.Parse(g.Value) : null;
        if (m.Groups[1].Value is "x" or "X" or "*") return new Partial(-1, null, null);
        return new Partial(int.Parse(m.Groups[1].Value), Part(m.Groups[2]), Part(m.Groups[3]));
    }

    private static int Cmp((int, int, int) a, (int, int, int) b) => a.CompareTo(b);

    private static List<Func<(int, int, int), bool>>? Comparators(string set)
    {
        var list = new List<Func<(int, int, int), bool>>();
        if (set is "" or "*" or "x" or "X" or "latest") { list.Add(_ => true); return list; }

        // Hyphen range: "1.2.3 - 2.3"
        var hyphen = HyphenRegex().Match(set);
        if (hyphen.Success)
        {
            var lo = ParsePartial(hyphen.Groups[1].Value);
            var hi = ParsePartial(hyphen.Groups[2].Value);
            if (lo is null || hi is null) return null;
            list.Add(v => Cmp(v, lo.Low) >= 0);
            list.Add(UpperBoundInclusive(hi));
            return list;
        }

        // Normalise ">= 22" → ">=22" so tokens split on whitespace.
        var tokens = OperatorSpaceRegex().Replace(set, "$1").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in tokens)
        {
            var m = ComparatorRegex().Match(token);
            if (!m.Success) return null;
            var op = m.Groups[1].Value;
            var p = ParsePartial(m.Groups[2].Value);
            if (p is null) return null;
            if (p.Major < 0) { list.Add(_ => true); continue; }
            switch (op)
            {
                case "^":
                {
                    var lo = p.Low;
                    (int, int, int) hi = p.Major > 0 || p.Minor is null ? (p.Major + 1, 0, 0)
                        : p.Minor > 0 || p.Patch is null ? (0, p.Minor.Value + 1, 0)
                        : (0, 0, p.Patch.Value + 1);
                    list.Add(v => Cmp(v, lo) >= 0 && Cmp(v, hi) < 0);
                    break;
                }
                case "~" or "~>":
                {
                    var lo = p.Low;
                    (int, int, int) hi = p.Minor is null ? (p.Major + 1, 0, 0) : (p.Major, p.Minor.Value + 1, 0);
                    list.Add(v => Cmp(v, lo) >= 0 && Cmp(v, hi) < 0);
                    break;
                }
                case ">=": list.Add(v => Cmp(v, p.Low) >= 0); break;
                case ">":
                    list.Add(p.Minor is null ? v => v.Item1 > p.Major
                        : p.Patch is null ? v => Cmp(v, (p.Major, p.Minor.Value + 1, 0)) >= 0
                        : v => Cmp(v, p.Low) > 0);
                    break;
                case "<": list.Add(v => Cmp(v, p.Low) < 0); break;
                case "<=": list.Add(UpperBoundInclusive(p)); break;
                case "" or "=" or "v":
                    if (p.Minor is null) list.Add(v => v.Item1 == p.Major);
                    else if (p.Patch is null) list.Add(v => v.Item1 == p.Major && v.Item2 == p.Minor);
                    else list.Add(v => Cmp(v, p.Low) == 0);
                    break;
                default: return null;
            }
        }
        return list;
    }

    private static Func<(int, int, int), bool> UpperBoundInclusive(Partial p) =>
        p.Minor is null ? v => v.Item1 <= p.Major
        : p.Patch is null ? v => Cmp(v, (p.Major, p.Minor.Value + 1, 0)) < 0
        : v => Cmp(v, p.Low) <= 0;

    [GeneratedRegex(@"^v?(\d+)(?:\.(\d+))?(?:\.(\d+))?")]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"^v?(\d+|[xX*])(?:\.(\d+|[xX*]))?(?:\.(\d+|[xX*]))?(?:[-+][0-9A-Za-z.-]*)?$")]
    private static partial Regex PartialRegex();

    [GeneratedRegex(@"^(\S+)\s+-\s+(\S+)$")]
    private static partial Regex HyphenRegex();

    [GeneratedRegex(@"(>=|<=|>|<|=|\^|~)\s+")]
    private static partial Regex OperatorSpaceRegex();

    [GeneratedRegex(@"^(>=|<=|>|<|=|\^|~>?|v)?(.+)$")]
    private static partial Regex ComparatorRegex();
}
