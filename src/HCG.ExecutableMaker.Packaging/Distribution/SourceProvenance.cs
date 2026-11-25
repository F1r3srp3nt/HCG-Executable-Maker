using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Packaging.Jailbreak;

namespace HCG.ExecutableMaker.Packaging.Distribution;

/// <summary>
/// Where a build's source came from, as Git states it for the folder that was imported: the commit, the branch, whether
/// the working copy had uncommitted changes, and the GitHub repository (OWNER/NAME) from the origin remote, without any
/// credential a remote URL may hold. Read-only Git commands only. Nothing is invented: a folder that is not a Git working
/// copy has no commit, and a ZIP import uses the commit GitHub writes into its archive comment, when present.
/// </summary>
public sealed record SourceProvenance(string? Repository, string? Commit, string? Branch, bool? Dirty, string Basis)
{
    public static readonly SourceProvenance Unknown = new(null, null, null, null, "no Git revision is known for this source");

    public bool IsPinned => Commit is { Length: 40 } && Dirty == false;

    public static async Task<SourceProvenance> ReadAsync(IProcessRunner runner, string? git, string folder, CancellationToken ct)
    {
        if (git is null || !Directory.Exists(folder)) return Unknown;
        async Task<(bool Ok, string Out)> Git(params string[] args)
        {
            var (r, o, _) = await runner.CaptureAsync(new ProcessSpec { Executable = git, Arguments = ["-C", folder, .. args], Timeout = TimeSpan.FromSeconds(30) }, ct).ConfigureAwait(false);
            return (r.Succeeded, o.Trim());
        }
        var head = await Git("rev-parse", "--verify", "HEAD").ConfigureAwait(false);
        if (!head.Ok || head.Out.Length != 40) return Unknown with { Basis = $"{Path.GetFileName(folder)} is not a Git working copy with a commit" };
        var branch = await Git("rev-parse", "--abbrev-ref", "HEAD").ConfigureAwait(false);
        var status = await Git("status", "--porcelain", "--untracked-files=normal").ConfigureAwait(false);
        var remote = await Git("remote", "get-url", "origin").ConfigureAwait(false);
        return new SourceProvenance(remote.Ok ? LocalBuild.RepositoryFromRemote(remote.Out) : null, head.Out.ToLowerInvariant(),
            branch.Ok && branch.Out is { Length: > 0 } b && b != "HEAD" ? b : null, status.Ok ? status.Out.Length > 0 : null,
            $"git in {Path.GetFileName(folder)}");
    }

    /// <summary>A GitHub source archive's ZIP comment is the commit it was made from (40 hex characters).</summary>
    public static SourceProvenance FromArchiveComment(string? comment) =>
        comment is { Length: 40 } c && c.All(Uri.IsHexDigit)
            ? new SourceProvenance(null, c.ToLowerInvariant(), null, false, "the source archive's ZIP comment (GitHub writes the commit there)")
            : Unknown;
}
