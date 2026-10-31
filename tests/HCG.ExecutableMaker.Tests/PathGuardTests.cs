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
    public void GetRealPath_resolves_symlinked_directories_and_keeps_missing_tail()
    {
        using var t = new TempDir();
        Directory.CreateDirectory(t.Combine("real", "inner"));
        Directory.CreateSymbolicLink(t.Combine("link"), t.Combine("real"));
        // The temp folder may itself sit behind a symlink (macOS: /var -> /private/var), so compare canonical forms.
        var real = PathGuard.GetRealPath(t.Combine("real", "inner"));
        Assert.DoesNotContain("/link/", real + "/");
        Assert.Equal(real, PathGuard.GetRealPath(t.Combine("link", "inner")));
        Assert.Equal(Path.Combine(real, "not", "yet"), PathGuard.GetRealPath(t.Combine("link", "inner", "not", "yet")));
        Assert.Equal(real, PathGuard.GetRealPath(real));
    }

    [UnixFact]
    public void GetRealPath_resolves_link_whose_target_passes_through_another_link_and_detects_loops()
    {
        using var t = new TempDir();
        Directory.CreateDirectory(t.Combine("real", "inner"));
        Directory.CreateSymbolicLink(t.Combine("outer-link"), t.Combine("real"));
        // Absolute target that itself goes through outer-link (the macOS /var situation).
        Directory.CreateSymbolicLink(t.Combine("chained"), t.Combine("outer-link", "inner"));
        Directory.CreateSymbolicLink(t.Combine("relative"), Path.Combine("outer-link", "inner"));
        var expected = PathGuard.GetRealPath(t.Combine("real", "inner"));
        Assert.Equal(expected, PathGuard.GetRealPath(t.Combine("chained")));
        Assert.Equal(expected, PathGuard.GetRealPath(t.Combine("relative")));

        File.CreateSymbolicLink(t.Combine("loop-a"), t.Combine("loop-b"));
        File.CreateSymbolicLink(t.Combine("loop-b"), t.Combine("loop-a"));
        Assert.Throws<IOException>(() => PathGuard.GetRealPath(t.Combine("loop-a")));
    }

    [UnixFact]
    public void IsWithin_handles_filesystem_root() => Assert.True(PathGuard.IsWithin("/", "/tmp/work"));

    [Theory]
    [InlineData("My App", "My App")]
    [InlineData("a/b\\c:d", "a-b-c-d")]
    [InlineData("..", "item")]
    public void SanitizeFileName(string input, string expected) => Assert.Equal(expected, PathGuard.SanitizeFileName(input));
}
