namespace HCG.ExecutableMaker.Core;

/// <summary>
/// A small line diff for showing proposed file changes before they are applied ("- " removed, "+ " added, "  " context,
/// "…" for skipped unchanged lines). It is for people to read, not a patch format.
/// </summary>
public static class TextDiff
{
    /// <summary>Above this many line pairs the diff is not computed; the whole old and new text are listed instead.</summary>
    private const long MaxCells = 4_000_000;

    public static IReadOnlyList<string> Lines(string? before, string after, int context = 3)
    {
        var a = Split(before ?? "");
        var b = Split(after);
        if (before is null) return b.Select(l => "+ " + l).ToList();
        if ((long)a.Length * b.Length > MaxCells)
            return [.. a.Select(l => "- " + l), .. b.Select(l => "+ " + l)];

        // Longest common subsequence table, filled from the end so the walk below goes forwards.
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var ops = new List<(char Op, string Text)>();
        int x = 0, y = 0;
        while (x < a.Length || y < b.Length)
        {
            if (x < a.Length && y < b.Length && a[x] == b[y]) { ops.Add((' ', a[x])); x++; y++; }
            else if (x < a.Length && (y == b.Length || lcs[x + 1, y] >= lcs[x, y + 1])) { ops.Add(('-', a[x])); x++; }
            else { ops.Add(('+', b[y])); y++; }
        }

        // Keep `context` unchanged lines around each change; collapse the rest.
        var keep = new bool[ops.Count];
        for (var i = 0; i < ops.Count; i++)
            if (ops[i].Op != ' ')
                for (var k = Math.Max(0, i - context); k <= Math.Min(ops.Count - 1, i + context); k++) keep[k] = true;
        var result = new List<string>();
        var skipped = 0;
        for (var i = 0; i < ops.Count; i++)
        {
            if (!keep[i]) { skipped++; continue; }
            if (skipped > 0) { result.Add($"… {skipped} unchanged line{(skipped == 1 ? "" : "s")}"); skipped = 0; }
            result.Add(ops[i].Op + " " + ops[i].Text);
        }
        if (skipped > 0) result.Add($"… {skipped} unchanged line{(skipped == 1 ? "" : "s")}");
        return result;
    }

    private static string[] Split(string text) =>
        text.Length == 0 ? [] : text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
}
