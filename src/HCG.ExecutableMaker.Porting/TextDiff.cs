using System.Text;

namespace HCG.ExecutableMaker.Porting;

/// <summary>Line-based unified diff (Myers' O(ND) algorithm) for the reviewable migration plan.</summary>
public static class TextDiff
{
    private enum Op { Equal, Delete, Insert }

    public static string Unified(string? oldText, string newText, string oldName, string newName, int context = 3)
    {
        var a = SplitLines(oldText ?? "");
        var b = SplitLines(newText);
        var ops = Diff(a, b);
        if (ops.All(o => o.Op == Op.Equal)) return "";

        var sb = new StringBuilder();
        sb.Append("--- ").Append(oldText is null ? "/dev/null" : "a/" + oldName).Append('\n');
        sb.Append("+++ b/").Append(newName).Append('\n');

        // Group operations into hunks with `context` lines around changes.
        var i = 0;
        while (i < ops.Count)
        {
            while (i < ops.Count && ops[i].Op == Op.Equal) i++;
            if (i >= ops.Count) break;
            var start = Math.Max(0, i - context);
            var end = i;
            var lastChange = i;
            while (end < ops.Count)
            {
                if (ops[end].Op != Op.Equal) lastChange = end;
                else if (end - lastChange > context * 2) break;
                end++;
            }
            end = Math.Min(ops.Count, lastChange + context + 1);
            var hunk = ops.GetRange(start, end - start);
            var aStart = hunk.FirstOrDefault(o => o.Op != Op.Insert).A;
            var bStart = hunk.FirstOrDefault(o => o.Op != Op.Delete).B;
            var aCount = hunk.Count(o => o.Op != Op.Insert);
            var bCount = hunk.Count(o => o.Op != Op.Delete);
            if (aCount == 0) aStart = hunk[0].A - 1;
            sb.Append("@@ -").Append(aStart + 1).Append(',').Append(aCount).Append(" +").Append(bStart + 1).Append(',').Append(bCount).Append(" @@\n");
            foreach (var o in hunk)
                sb.Append(o.Op switch { Op.Equal => ' ', Op.Delete => '-', _ => '+' })
                  .Append(o.Op == Op.Insert ? b[o.B] : a[o.A]).Append('\n');
            i = end;
        }
        return sb.ToString();
    }

    private static string[] SplitLines(string text)
    {
        if (text.Length == 0) return [];
        var lines = text.Replace("\r\n", "\n").Split('\n');
        return text.EndsWith('\n') ? lines[..^1] : lines;
    }

    private readonly record struct Edit(Op Op, int A, int B);

    private static List<Edit> Diff(string[] a, string[] b)
    {
        // Trim common prefix/suffix first: typical port edits touch a few lines of large files.
        var prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix]) prefix++;
        var suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[^(suffix + 1)] == b[^(suffix + 1)]) suffix++;
        var result = new List<Edit>();
        for (var k = 0; k < prefix; k++) result.Add(new Edit(Op.Equal, k, k));
        result.AddRange(Myers(a, b, prefix, a.Length - suffix, prefix, b.Length - suffix));
        for (var k = suffix; k > 0; k--) result.Add(new Edit(Op.Equal, a.Length - k, b.Length - k));
        return result;
    }

    private static List<Edit> Myers(string[] a, string[] b, int a0, int a1, int b0, int b1)
    {
        int n = a1 - a0, m = b1 - b0, max = n + m;
        var edits = new List<Edit>();
        if (max == 0) return edits;
        if (n == 0) { for (var j = b0; j < b1; j++) edits.Add(new Edit(Op.Insert, a0, j)); return edits; }
        if (m == 0) { for (var j = a0; j < a1; j++) edits.Add(new Edit(Op.Delete, j, b0)); return edits; }
        if ((long)n * m > 25_000_000)
        {
            // Too large for an interactive review diff: show a full replacement instead of an expensive search.
            for (var j = a0; j < a1; j++) edits.Add(new Edit(Op.Delete, j, b0));
            for (var j = b0; j < b1; j++) edits.Add(new Edit(Op.Insert, a1, j));
            return edits;
        }
        var v = new int[2 * max + 2];
        var trace = new List<int[]>();
        int found = -1;
        for (var d = 0; d <= max && found < 0; d++)
        {
            trace.Add((int[])v.Clone());
            for (var k = -d; k <= d; k += 2)
            {
                int x = k == -d || (k != d && v[k - 1 + max] < v[k + 1 + max]) ? v[k + 1 + max] : v[k - 1 + max] + 1;
                int y = x - k;
                while (x < n && y < m && a[a0 + x] == b[b0 + y]) { x++; y++; }
                v[k + max] = x;
                if (x >= n && y >= m) { found = d; break; }
            }
        }
        // Backtrack.
        int cx = n, cy = m;
        var reversed = new List<Edit>();
        for (var d = trace.Count - 1; d >= 0; d--)
        {
            var vv = trace[d];
            var k = cx - cy;
            int prevK = k == -d || (k != d && vv[k - 1 + max] < vv[k + 1 + max]) ? k + 1 : k - 1;
            var prevX = vv[prevK + max];
            var prevY = prevX - prevK;
            while (cx > prevX && cy > prevY) { cx--; cy--; reversed.Add(new Edit(Op.Equal, a0 + cx, b0 + cy)); }
            if (d > 0)
            {
                if (cx == prevX) { cy--; reversed.Add(new Edit(Op.Insert, a0 + cx, b0 + cy)); }
                else { cx--; reversed.Add(new Edit(Op.Delete, a0 + cx, b0 + cy)); }
            }
        }
        reversed.Reverse();
        return reversed;
    }
}
