using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.BuildEngine.BuildHost;

/// <summary>
/// Turns verified Build Host provenance into HCG Repo artifact-submission evidence
/// (<c>docs/schemas/artifact-submission-v1.schema.json</c>): <c>source</c> {repository, commit, dirty=false} and
/// <c>build</c> {provider="hcg-server-build-host", toolchain, builtAt}. A Build Host build has no CI run, so the
/// result never has <c>runId</c>, <c>runUrl</c>, <c>artifactName</c> or <c>runRepository</c> — HCG Repo refuses those
/// for every non-GitHub provider.
/// </summary>
public static partial class BuildHostSubmission
{
    /// <summary>
    /// The evidence of a verified provenance, or an exception explaining why it cannot back a submission (not a
    /// succeeded job, a dirty tree, a commit other than the one requested, a provider other than the Build Host).
    /// </summary>
    public static BuildHostEvidence FromProvenance(VerifiedProvenance verified, string? expectedCommit = null)
    {
        var p = verified.Document;
        var s = p.Submission;
        var problems = new List<string>();
        if (p.Job.State != BuildJobStates.Succeeded) problems.Add($"the job ended {p.Job.State}, not succeeded");
        if (s.Build.Provider != BuildHostContract.SubmissionProvider) problems.Add($"provider {s.Build.Provider}, expected {BuildHostContract.SubmissionProvider}");
        if (!RepositoryName().IsMatch(s.Source.Repository)) problems.Add($"repository {s.Source.Repository} is not OWNER/NAME");
        if (!Commit40().IsMatch(s.Source.Commit)) problems.Add($"commit {s.Source.Commit} is not a full lowercase 40-hex commit");
        if (s.Source.Dirty || p.Source.Dirty) problems.Add("the checked-out tree was dirty");
        if (s.Source.Commit != p.Source.Commit || s.Source.Repository != p.Source.Repository) problems.Add("the submission block and the provenance source disagree");
        if (expectedCommit is { } want && !string.Equals(want, s.Source.Commit, StringComparison.OrdinalIgnoreCase)) problems.Add($"built {s.Source.Commit}, but {want} was requested");
        if (string.IsNullOrWhiteSpace(s.Build.Toolchain)) problems.Add("no toolchain recorded");
        if (!DateTimeOffset.TryParse(s.Build.BuiltAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _)) problems.Add($"builtAt {s.Build.BuiltAt} is not an ISO 8601 time");
        if (s.Signing.Method != "unsigned") problems.Add($"signing method {s.Signing.Method}: the Build Host only builds unsigned");
        if (problems.Count > 0)
            throw new HcgBuildHostException(BuildHostFailure.Invalid, $"Job {p.Job.Id} cannot back an HCG Repo submission: " + string.Join("; ", problems) + ".");
        return new BuildHostEvidence
        {
            Repository = s.Source.Repository, Commit = s.Source.Commit, Dirty = false,
            Toolchain = s.Build.Toolchain, BuiltAt = s.Build.BuiltAt, JobId = p.Job.Id,
            KeyFingerprint = verified.KeyFingerprint, DeviceId = p.BuildHost.DeviceId, DeviceName = p.BuildHost.Name,
            ApprovedBy = p.Job.ApprovedBy, SigningMethod = s.Signing.Method,
        };
    }

    /// <summary>The submission's <c>source</c> object.</summary>
    public static JsonObject Source(BuildHostEvidence e, string? branch = null)
    {
        var o = new JsonObject { ["repository"] = e.Repository, ["commit"] = e.Commit, ["dirty"] = false };
        if (branch is not null) o["branch"] = branch;
        return o;
    }

    /// <summary>
    /// The submission's <c>build</c> object. <paramref name="packagedWith"/> names what Executable Maker added after the
    /// Build Host (e.g. "HCG Executable Maker 0.4.0, ldid 2.1.5"); it is appended to the Build Host's toolchain.
    /// </summary>
    public static JsonObject Build(BuildHostEvidence e, string? packagedWith = null) => new()
    {
        ["provider"] = e.Provider,
        ["toolchain"] = packagedWith is { Length: > 0 } extra ? $"{e.Toolchain}, {extra}" : e.Toolchain,
        ["builtAt"] = e.BuiltAt,
    };

    /// <summary>Both fields, as they go into submission.json.</summary>
    public static JsonObject Fields(BuildHostEvidence e, string? branch = null, string? packagedWith = null) => new()
    {
        ["source"] = Source(e, branch),
        ["build"] = Build(e, packagedWith),
    };

    [GeneratedRegex("^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")] private static partial Regex RepositoryName();
    [GeneratedRegex("^[0-9a-f]{40}$")] private static partial Regex Commit40();
}
