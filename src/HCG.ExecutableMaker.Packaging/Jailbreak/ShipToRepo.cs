using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HCG.ExecutableMaker.Core;

namespace HCG.ExecutableMaker.Packaging.Jailbreak;

/// <summary>
/// Ship to Repo: hands a validated Unleashify package to HCG Repo's Candidate Release Manager (HCG Repo Distribution v2,
/// <c>bin/hcgrepo release …</c>) on the owner's own HCG Repo checkout. It runs HCG Repo's code and never re-implements
/// its decisions:
/// <list type="number">
/// <item><b>Submit</b>: copies the exact .deb into HCG Repo's private incoming store (outside Git) and lets HCG Repo validate it
/// (manifest claims vs the package, static checks, secrets, scripts, simulated dpkg lifecycle, repository consistency).</item>
/// <item><b>Review</b>: HCG Repo's review, with the exact SHA-256 and the three validation levels.</item>
/// <item><b>Approve</b>: records the owner's approval of that exact SHA-256 — only when the owner typed that SHA-256, their name and
/// their words; Maker never approves on its own.</item>
/// <item><b>Prepare</b>: imports the approved bytes into the checkout's pool/ for Candidate. Nothing is committed, pushed or
/// published: the owner commits on a branch, and publication stays HCG Repo's manual Publish workflow.</item>
/// </list>
/// </summary>
public static partial class ShipToRepo
{
    public enum Action { Submit, Show, Approve, Prepare }

    public sealed record Step(Action Action, bool Ran, int ExitCode, string Output, string? SubmissionId, string? State, JsonObject? Review)
    {
        public bool Succeeded => Ran && ExitCode == 0;
    }

    /// <summary>HCG Repo's submission id for a package: identifier_version_architecture_first-12-of-sha256 (tools/hcgrepo/submission.py).</summary>
    public static string SubmissionId(string package, string version, string architecture, string sha256) =>
        UnsafeId().Replace($"{package}_{version}_{architecture}_{sha256[..12]}", "-");

    [GeneratedRegex(@"[^A-Za-z0-9._-]")] private static partial Regex UnsafeId();

    /// <summary>Whether the checkout has HCG Repo's Release Manager (Distribution v2).</summary>
    public static string? CheckoutProblem(string checkout) =>
        !File.Exists(Path.Combine(checkout, "bin", "hcgrepo")) ? $"{checkout} is not an HCG Repo checkout (no bin/hcgrepo)."
        : !File.Exists(Path.Combine(checkout, "tools", "hcgrepo", "release.py")) ? $"{checkout} has no Release Manager (tools/hcgrepo/release.py): update it to HCG Repo main with Distribution v2."
        : null;

    public static Task<Step> SubmitAsync(IProcessRunner runner, string python, string checkout, string handoff, string? incoming, Action<string>? log, CancellationToken ct) =>
        RunAsync(Action.Submit, runner, python, checkout, incoming, ["submit", "--handoff", handoff], log, ct);

    public static async Task<Step> ShowAsync(IProcessRunner runner, string python, string checkout, string id, string? incoming, CancellationToken ct)
    {
        var step = await RunAsync(Action.Show, runner, python, checkout, incoming, ["show", id, "--json"], null, ct).ConfigureAwait(false);
        JsonObject? review = null;
        try { review = step.Succeeded ? JsonNode.Parse(step.Output) as JsonObject : null; } catch (System.Text.Json.JsonException) { }
        return step with { SubmissionId = id, Review = review, State = review?["state"]?.GetValue<string>() };
    }

    /// <summary>Records the owner's approval. Refused unless <paramref name="approvedSha256"/> is the reviewed SHA-256 and who and what are given.</summary>
    public static async Task<Step> ApproveAsync(IProcessRunner runner, string python, string checkout, string id, string approvedSha256, string by, string record, string? incoming, CancellationToken ct)
    {
        var shown = await ShowAsync(runner, python, checkout, id, incoming, ct).ConfigureAwait(false);
        string Refuse(string why) => why;
        string? problem = shown.Review is null ? Refuse("HCG Repo has no review for this submission; submit it first.")
            : shown.State != "validated" ? Refuse($"the submission is {shown.State}; only a validated submission can be approved.")
            : !string.Equals(shown.Review["sha256"]?.GetValue<string>(), approvedSha256.Trim(), StringComparison.OrdinalIgnoreCase) ? Refuse($"the approved SHA-256 {approvedSha256.Trim()} is not the reviewed package's {shown.Review["sha256"]}.")
            : string.IsNullOrWhiteSpace(by) || string.IsNullOrWhiteSpace(record) ? Refuse("an approval needs who approved and their approval, verbatim.")
            : null;
        if (problem is not null) return new(Action.Approve, false, 1, "REFUSED by HCG Executable Maker: " + problem, id, shown.State, shown.Review);
        return await RunAsync(Action.Approve, runner, python, checkout, incoming,
            ["approve", id, "--sha256", approvedSha256.Trim().ToLowerInvariant(), "--by", by.Trim(), "--record", record.Trim()], null, ct).ConfigureAwait(false) with { SubmissionId = id };
    }

    /// <param name="viaLocalFile">
    /// false: HCG Repo imports into the checkout's pool/ here. true: HCG Repo stages the approved bytes under imports/local/
    /// with its own local-file request, so its Prepare packages workflow imports them in CI.
    /// </param>
    public static async Task<Step> PrepareAsync(IProcessRunner runner, string python, string checkout, string id, string? freeDemoApproval, bool createPackageMetadata, string? incoming, Action<string>? log, CancellationToken ct,
        bool viaLocalFile = false)
    {
        List<string> args = ["prepare", id];
        if (viaLocalFile) args.AddRange(["--via", "local-file"]);
        if (freeDemoApproval is { Length: > 0 }) args.AddRange(["--free-demo-approval", freeDemoApproval.Trim()]);
        if (createPackageMetadata) args.Add("--create-package-metadata");
        return await RunAsync(Action.Prepare, runner, python, checkout, incoming, args, log, ct).ConfigureAwait(false) with { SubmissionId = id };
    }

    /// <summary>
    /// Import preview: HCG Repo's full review of the handoff (manifest vs package, static checks, secrets, scripts, simulated
    /// dpkg lifecycle, repository rules) in a throwaway incoming store that is deleted afterwards. Nothing is kept, approved,
    /// imported or published; the owner's real incoming store is not touched.
    /// </summary>
    public static async Task<Step> PreviewAsync(IProcessRunner runner, string python, string checkout, string handoff, Action<string>? log, CancellationToken ct)
    {
        var store = Path.Combine(Path.GetTempPath(), "hcg-ship-preview-" + Guid.NewGuid().ToString("N")[..10]);
        try { return await SubmitAsync(runner, python, checkout, handoff, store, log, ct).ConfigureAwait(false); }
        finally
        {
            try { if (Directory.Exists(store)) Directory.Delete(store, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Whether HCG Repo already has metadata for the package (otherwise Prepare must create it from the handoff's draft).</summary>
    public static bool HasPackageMetadata(string checkout, string package) => File.Exists(Path.Combine(checkout, "packages", package, "package.toml"));

    private static async Task<Step> RunAsync(Action action, IProcessRunner runner, string python, string checkout, string? incoming, IReadOnlyList<string> args, Action<string>? log, CancellationToken ct)
    {
        if (CheckoutProblem(checkout) is { } bad) return new(action, false, 2, bad, null, null, null);
        List<string> all = ["-I", Path.Combine(checkout, "bin", "hcgrepo"), "release"];
        if (incoming is { Length: > 0 }) all.AddRange(["--incoming", incoming]);
        all.AddRange(args);
        var output = new StringBuilder();
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var k in new[] { "PATH", "HOME", "LANG", "TMPDIR" })
            if (Environment.GetEnvironmentVariable(k) is { } v) env[k] = v;
        // dpkg-deb from Homebrew on Macs where the app was not started from a shell.
        env["PATH"] = string.Join(':', new[] { env.GetValueOrDefault("PATH"), "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/bin" }.Where(x => !string.IsNullOrEmpty(x)));
        var r = await runner.RunAsync(new ProcessSpec { Executable = python, Arguments = all, WorkingDirectory = checkout, Environment = env, Timeout = TimeSpan.FromMinutes(15) },
            (_, line) => { lock (output) output.AppendLine(line); log?.Invoke(line); }, ct).ConfigureAwait(false);
        var text = output.ToString();
        var id = SubmissionLine().Match(text) is { Success: true } m ? m.Groups[1].Value : null;
        var state = StateLine().Match(text) is { Success: true } s ? s.Groups[1].Value.ToLowerInvariant() : null;
        return new(action, true, r.ExitCode, text, id, state, null);
    }

    [GeneratedRegex(@"(?m)^Submission\s+(\S+)")] private static partial Regex SubmissionLine();
    [GeneratedRegex(@"(?m)^State\s+(\S+)")] private static partial Regex StateLine();
}
