using HCG.ExecutableMaker.Core;

namespace HCG.ExecutableMaker.Tests;

public class PathGuardTests
{
    [Theory]
    [InlineData("../evil.txt", "parent-directory")]
    [InlineData("a/../../evil.txt", "parent-directory")]
    [InlineData(@"..\evil.txt", "parent-directory")]
    [InlineData("/etc/passwd", "absolute")]
    [InlineData(@"C:\Windows\evil.dll", "absolute")]
    [InlineData("C:evil", "absolute")]
    [InlineData(@"\\server\share\x", "absolute")]
    [InlineData("a/b:c", "reserved")]
    [InlineData("a/trailing. ", "space or dot")]
    public void Rejects_unsafe_archive_entry_names(string name, string reasonFragment)
    {
        Assert.False(PathGuard.TryNormalizeArchiveEntry(name, out _, out var reason));
        Assert.Contains(reasonFragment, reason);
    }

    [Fact]
    public void Rejects_nul_characters() => Assert.False(PathGuard.TryNormalizeArchiveEntry("a\0b", out _, out _));

    [Theory]
    [InlineData("src/App.cs", new[] { "src", "App.cs" })]
    [InlineData("./src//./App.cs", new[] { "src", "App.cs" })]
    [InlineData(@"src\App.cs", new[] { "src", "App.cs" })]
    public void Normalizes_safe_entries(string name, string[] expected)
    {
        Assert.True(PathGuard.TryNormalizeArchiveEntry(name, out var segments, out _));
        Assert.Equal(expected, segments);
    }

    [Fact]
    public void CombineWithin_blocks_escape()
    {
        using var t = new TempDir();
        Assert.Throws<ArgumentException>(() => PathGuard.CombineWithin(t.Path, "../x"));
        Assert.Throws<ArgumentException>(() => PathGuard.CombineWithin(t.Path, "/abs"));
        Assert.Equal(Path.Combine(t.Path, "a", "b"), PathGuard.CombineWithin(t.Path, "a/b"));
    }

    [Fact]
    public void IsWithin_does_not_match_sibling_prefix()
    {
        Assert.False(PathGuard.IsWithin("/tmp/work", "/tmp/workspace/file"));
        Assert.True(PathGuard.IsWithin("/tmp/work", "/tmp/work/file"));
        Assert.True(PathGuard.IsWithin("/tmp/work", "/tmp/work"));
    }

    [UnixFact]
    public void IsWithin_handles_filesystem_root() => Assert.True(PathGuard.IsWithin("/", "/tmp/work"));

    [Theory]
    [InlineData("My App", "My App")]
    [InlineData("a/b\\c:d", "a-b-c-d")]
    [InlineData("..", "item")]
    public void SanitizeFileName(string input, string expected) => Assert.Equal(expected, PathGuard.SanitizeFileName(input));
}
