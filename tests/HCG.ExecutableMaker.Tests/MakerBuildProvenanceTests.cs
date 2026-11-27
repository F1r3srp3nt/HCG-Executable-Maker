using HCG.ExecutableMaker.Packaging.Distribution;
using Xunit;

namespace HCG.ExecutableMaker.Tests;

/// <summary>Maker's own build provenance comes from what the build embedded, needs no git, and is never invented.</summary>
public sealed class MakerBuildProvenanceTests
{
    [Fact]
    public void An_embedded_full_commit_is_used_with_its_dirty_flag_and_repository()
    {
        var p = MakerBuildProvenance.From("FC1B7866EC18AE72312C107ABFDECEAE7268E702", false, "F1r3srp3nt/HCG-Executable-Maker");
        Assert.Equal("fc1b7866ec18ae72312c107abfdeceae7268e702", p.Commit);
        Assert.False(p.Dirty);
        Assert.True(p.IsPinned);
        Assert.Equal("F1r3srp3nt/HCG-Executable-Maker", p.Repository);
        Assert.Equal(MakerBuildProvenance.Basis, p.Basis);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("fc1b7866ec18")]                                  // a short commit is not a full commit
    [InlineData("unknown")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void Without_a_full_embedded_commit_the_source_is_unknown(string? commit)
    {
        var p = MakerBuildProvenance.From(commit, null, null);
        Assert.Null(p.Commit);
        Assert.Contains("recorded no source commit", p.Basis);
    }

    [Fact]
    public void A_build_with_uncommitted_changes_is_not_pinned() =>
        Assert.False(MakerBuildProvenance.From(new string('a', 40), true, null).IsPinned);

    [Fact]
    public void This_test_build_embeds_its_source_when_built_from_a_git_checkout()
    {
        // Built by `dotnet test` from this repository: git is available, so the full commit is embedded (or, without git,
        // nothing is: never a placeholder).
        var c = HCG.ExecutableMaker.Core.HostInfo.SourceCommit;
        Assert.True(c is null || (c.Length == 40 && c.All(Uri.IsHexDigit)));
    }
}
