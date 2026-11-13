using HCG.ExecutableMaker.Core;

namespace HCG.ExecutableMaker.Tests;

public class TextDiffTests
{
    [Fact]
    public void A_new_file_is_all_additions()
    {
        Assert.Equal(["+ a", "+ b"], TextDiff.Lines(null, "a\nb\n"));
    }

    [Fact]
    public void Changed_lines_show_removed_and_added_with_context_and_collapse_the_rest()
    {
        var before = string.Join("\n", Enumerable.Range(1, 20).Select(i => $"line {i}"));
        var after = before.Replace("line 10", "line ten") + "\nline 21";
        var diff = TextDiff.Lines(before, after, context: 2);
        Assert.Equal("… 7 unchanged lines", diff[0]);
        Assert.Equal(["  line 8", "  line 9", "- line 10", "+ line ten", "  line 11", "  line 12"], diff.Skip(1).Take(6));
        Assert.Contains("… 6 unchanged lines", diff);
        Assert.Equal("+ line 21", diff[^1]);
        Assert.Equal(["  line 19", "  line 20"], diff.Skip(diff.Count - 3).Take(2));
    }

    [Fact]
    public void Identical_text_and_line_endings_produce_no_changes()
    {
        var diff = TextDiff.Lines("a\r\nb\r\n", "a\nb\n");
        Assert.DoesNotContain(diff, l => l.StartsWith("+ ", StringComparison.Ordinal) || l.StartsWith("- ", StringComparison.Ordinal));
    }
}
