using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HCG.ExecutableMaker.Core;

namespace HCG.ExecutableMaker.Packaging.Jailbreak;

/// <summary>
/// Ship to Repo: hands a validated Unleashify package to HCG Repo's Candidate Release Manager on the owner's own HCG Repo
/// checkout, following HCG Repo's Executable Maker submission contract (docs/EXECUTABLE_MAKER_SUBMISSION.md in HCG Repo).
/// It runs HCG Repo's code and never re-implements its decisions:
/// <list type="number">
/// <item><b>Submit</b>: <c>bin/hcgrepo release submit --handoff &lt;folder&gt; --result-json &lt;file&gt;</c>. HCG Repo copies the exact .deb into
/// its private incoming store (outside Git) and validates it; the result is <c>hcg.repo.submission-result</c> v1.</item>
/// <item><b>Status</b>: <c>bin/hcgrepo release status &lt;id&gt;</c>, the same result as JSON.</item>
/// </list>
/// Approval of the exact SHA-256, Candidate preparation and publication belong to the owner. Maker shows the six evidence
/// levels separately and the owner's next commands (<see cref="OwnerCommands"/>); it never runs <c>approve</c>,
/// <c>prepare</c> or Publish.
/// </summary>
public static partial class ShipToRepo
{
    public enum Action { Submit, Status, Show }

    /// <param name="Result">HCG Repo's <c>hcg.repo.submission-result</c> v1, when it produced one.</param>
    public sealed record Step(Action Action, bool Ran, int ExitCode, string Output, string? SubmissionId, string? State, JsonObject? Result)
    {
        public bool Succeeded => Ran && ExitCode == 0;
    }

    /// <summary>HCG Repo's evidence levels, in its order, with its labels (tools/hcgrepo/levels.py).</summary>
    public static readonly IReadOnlyList<(string Key, string Label)> Levels =
    [
        ("static_inspection", "Static package inspection"),
        ("maintainer_script_review", "Maintainer script review (read, not run)"),
        ("script_free_lifecycle", "Script-free dpkg lifecycle"),
        ("device_installation", "Device installation"),
        ("application_launch", "Application launch"),
        ("runtime_verification", "Full runtime verification"),
    ];

    public const string LifecycleCaveat =
        "The script-free lifecycle ran dpkg on a copy with the maintainer scripts removed: it shows the payload installs, " +
        "reinstalls, removes and purges cleanly. It is not evidence that the maintainer scripts, an installation on a device " +
        "or the app work.";

    /// <summary>HCG Repo's submission id for a package: identifier_version_architecture_first-12-of-sha256 (tools/hcgrepo/submission.py).</summary>
    public static string SubmissionId(string package, string version, string architecture, string sha256) =>
        UnsafeId().Replace($"{package}_{version}_{architecture}_{sha256[..12]}", "-");

    [GeneratedRegex(@"[^A-Za-z0-9._-]")] private static partial Regex UnsafeId();

    /// <summary>Whether the checkout has HCG Repo's Release Manager with the submission-result contract.</summary>
    public static string? CheckoutProblem(string checkout) =>
        !File.Exists(Path.Combine(checkout, "bin", "hcgrepo")) ? $"{checkout} is not an HCG Repo checkout (no bin/hcgrepo)."
        : !File.Exists(Path.Combine(checkout, "tools", "hcgrepo", "release.py")) ? $"{checkout} has no Release Manager (tools/hcgrepo/release.py): update it to HCG Repo main with Distribution v2."
        : !File.Exists(Path.Combine(checkout, "docs", "schemas", "submission-result-v1.schema.json")) ? $"{checkout} predates HCG Repo's Executable Maker submission contract (no docs/schemas/submission-result-v1.schema.json): update it to HCG Repo main."
        : null;

    public static async Task<Step> SubmitAsync(IProcessRunner runner, string python, string checkout, string handoff, string? incoming, Action<string>? log, CancellationToken ct)
    {
        var resultFile = Path.Combine(Path.GetTempPath(), "hcg-ship-result-" + Guid.NewGuid().ToString("N")[..10] + ".json");
        try
        {
            var step = await RunAsync(Action.Submit, runner, python, checkout, incoming, ["submit", "--handoff", handoff, "--result-json", resultFile], log, ct).ConfigureAwait(false);
            var result = File.Exists(resultFile) ? ParseResult(await File.ReadAllTextAsync(resultFile, ct).ConfigureAwait(false)) : null;
            return result is null ? step : step with { Result = result, SubmissionId = Str(result, "submission") ?? step.SubmissionId, State = Str(result, "state") ?? step.State };
        }
        finally
        {
            try { File.Delete(resultFile); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary><c>release status &lt;id&gt;</c>: HCG Repo's current result for a submission (state, levels, requirements, next step).</summary>
    public static async Task<Step> StatusAsync(IProcessRunner runner, string python, string checkout, string id, string? incoming, CancellationToken ct)
    {
        var step = await RunAsync(Action.Status, runner, python, checkout, incoming, ["status", id], null, ct).ConfigureAwait(false);
        var result = step.Succeeded ? ParseResult(step.Output) : null;
        return step with { SubmissionId = id, Result = result, State = result is null ? null : Str(result, "state") };
    }

    /// <summary><c>release show &lt;id&gt;</c>: HCG Repo's human review text.</summary>
    public static async Task<Step> ShowAsync(IProcessRunner runner, string python, string checkout, string id, string? incoming, CancellationToken ct) =>
        await RunAsync(Action.Show, runner, python, checkout, incoming, ["show", id], null, ct).ConfigureAwait(false) with { SubmissionId = id };

    /// <summary>The [access] level of a handoff's package.toml draft (null when there is none).</summary>
    public static string? DraftAccessLevel(string? handoff)
    {
        var toml = handoff is null ? null : Path.Combine(handoff, "package.toml");
        if (toml is null || !File.Exists(toml)) return null;
        var inAccess = false;
        foreach (var line in File.ReadLines(toml))
        {
            var t = line.Trim();
            if (t.StartsWith('[')) inAccess = t == "[access]";
            else if (inAccess && AccessLevel().Match(t) is { Success: true } m) return m.Groups[1].Value;
        }
        return null;
    }

    [GeneratedRegex("^level\\s*=\\s*\"([a-z]+)\"")] private static partial Regex AccessLevel();

    private static JsonObject? ParseResult(string text)
    {
        try
        {
            return JsonNode.Parse(text) is JsonObject o && Str(o, "format") == "hcg.repo.submission-result" ? o : null;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private static string? Str(JsonNode? n, string key) => n?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>
    /// The six levels, one line each, in HCG Repo's order and with HCG Repo's states (never merged or inferred here),
    /// followed by the lifecycle caveat.
    /// </summary>
    public static string RenderLevels(JsonObject result)
    {
        var sb = new StringBuilder();
        var levels = result["levels"] as JsonObject;
        foreach (var (key, label) in Levels)
        {
            var lv = levels?[key] as JsonObject;
            var state = Str(lv, "state") ?? "not-recorded";
            var extra = "";
            if (key == "script_free_lifecycle" && lv?["steps"] is JsonArray steps && steps.Count > 0)
                extra = " (" + string.Join(", ", steps.Select(s => $"{Str(s, "step")} {(s?["ok"]?.GetValue<bool>() == true ? "ok" : "FAILED")}")) + ")";
            if (Str(lv, "note") is { } note && key is "device_installation" && state == "passed") extra += $" — {note}";
            sb.AppendLine($"  {label,-42} {state.ToUpperInvariant()}{extra}");
        }
        sb.AppendLine("  " + LifecycleCaveat);
        return sb.ToString();
    }

    /// <summary>
    /// The owner's next commands, from HCG Repo's result: the checksum approval (HCG Repo's own command line, with the
    /// owner's name and words left as placeholders), then the Candidate preparation with the options HCG Repo's
    /// requirements call for. Maker prints them; only the owner runs them.
    /// </summary>
    /// <param name="draftAccessLevel">
    /// The [access] level of the handoff's package.toml draft, when known. For a package HCG Repo does not have yet, its
    /// result cannot list the free-demo requirement (it has no metadata to read the level from); a draft that is not
    /// public needs it all the same.
    /// </param>
    public static IReadOnlyList<string> OwnerCommands(JsonObject result, string? draftAccessLevel = null)
    {
        var id = Str(result, "submission") ?? "<submission>";
        var state = Str(result, "state");
        var reqs = (result["requirements"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        bool Needs(string kind) => reqs.Any(r => Str(r, "kind") == kind && r["satisfied"]?.GetValue<bool>() == false);
        var lines = new List<string>();
        if (state == "validated")
            lines.Add(reqs.Where(r => Str(r, "kind") == "checksum-approval").Select(r => Str(r, "command")).FirstOrDefault(c => c is not null)
                ?? $"bin/hcgrepo release approve {id} --sha256 {Str(result["artifact"], "sha256")} --by \"<owner>\" --record \"<approval, verbatim>\"");
        if (state is "validated" or "approved")
        {
            var prepare = $"bin/hcgrepo release prepare {id}";
            if (Needs("package-metadata")) prepare += " --create-package-metadata";
            if (Needs("free-demo-approval") || (Needs("package-metadata") && draftAccessLevel != "public")) prepare += " --free-demo-approval \"<owner's words>\"";
            lines.Add(prepare + "   (add --via local-file to stage it for HCG Repo's Prepare packages workflow instead of pool/)");
        }
        return lines;
    }

    /// <summary>Everything Maker shows after a submit or status: state, SHA-256, the six levels, findings, the owner's next step and commands.</summary>
    public static string Summary(JsonObject result, string? draftAccessLevel = null)
    {
        var sb = new StringBuilder();
        var pkg = result["package"];
        sb.AppendLine($"Submission   {Str(result, "submission")}");
        sb.AppendLine($"State        {Str(result, "state")?.ToUpperInvariant()}");
        sb.AppendLine($"Package      {Str(pkg, "identifier")} {Str(pkg, "version")} {Str(pkg, "architecture")}");
        sb.AppendLine($"SHA-256      {Str(result["artifact"], "sha256")}");
        sb.AppendLine();
        sb.AppendLine("Evidence levels (HCG Repo):");
        sb.Append(RenderLevels(result));
        var findings = (result["findings"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        if (findings.Count > 0)
        {
            sb.AppendLine();
            foreach (var f in findings.OrderBy(f => Str(f, "level") == "error" ? 0 : 1))
                sb.AppendLine($"  {(Str(f, "level") == "error" ? "ERROR" : "warn ")} [{Str(f, "code")}] {Str(f, "message")}");
        }
        sb.AppendLine();
        sb.AppendLine($"Next step    {Str(result, "next_step")}");
        var open = (result["requirements"] as JsonArray ?? []).OfType<JsonObject>().Where(r => r["satisfied"]?.GetValue<bool>() == false).ToList();
        foreach (var r in open) sb.AppendLine($"  needs {Str(r, "kind")}{(Str(r, "detail") is { } d ? ": " + d : "")}");
        var cmds = OwnerCommands(result, draftAccessLevel);
        if (cmds.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("For the owner to run in the HCG Repo checkout (Executable Maker does not run these):");
            foreach (var c in cmds) sb.AppendLine("  " + c);
        }
        return sb.ToString();
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

    /// <summary>
    /// HCG Repo's own Release Passport for a release in its lock (<c>bin/hcgrepo passport &lt;package&gt; &lt;version&gt;</c>):
    /// the distribution authority's record, including its repository signing. Null with the reason when HCG Repo has none
    /// yet (not prepared, or staged via local-file and not imported), or when it is not a passport this Maker can read.
    /// </summary>
    public static async Task<(JsonObject? Passport, string Detail)> AuthorityPassportAsync(IProcessRunner runner, string python, string checkout, string package, string version, string architecture, CancellationToken ct)
    {
        var step = await RunAsync(Action.Status, runner, python, checkout, null, ["passport", package, version], null, ct, releaseCommand: false).ConfigureAwait(false);
        if (!step.Succeeded) return (null, step.Output.Contains("no matching release", StringComparison.Ordinal)
            ? $"HCG Repo has no passport for {package} {version} yet: it exists once the release is in HCG Repo's lock (after prepare into pool/, or after Prepare packages for a local-file request)"
            : "bin/hcgrepo passport failed: " + step.Output.Trim());
        JsonNode? node;
        try { node = JsonNode.Parse(step.Output); } catch (System.Text.Json.JsonException ex) { return (null, "HCG Repo's passport output is not JSON: " + ex.Message); }
        var passport = node is JsonArray many ? many.OfType<JsonObject>().FirstOrDefault(x => Str(x, "architecture") == architecture) : node as JsonObject;
        if (passport is null) return (null, $"HCG Repo has no {architecture} passport for {package} {version}");
        if (Distribution.ReleasePassport.Validate(passport) is { Count: > 0 } problems)
            return (null, "HCG Repo's passport is not used: " + string.Join("; ", problems.Take(3)));
        return (passport, "HCG Repo (bin/hcgrepo passport)");
    }

    /// <summary>The handoff's build half (release-passport.json), when it has one.</summary>
    public static JsonObject? BuildHalfOf(string? handoff)
    {
        var path = handoff is null ? null : Path.Combine(handoff, Distribution.ReleasePassport.FileName);
        if (path is null || !File.Exists(path)) return null;
        try { return JsonNode.Parse(File.ReadAllText(path)) as JsonObject; } catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>The passport section for a submission result: HCG Repo's passport is looked up once the release is prepared.</summary>
    public static async Task<string> PassportSectionAsync(IProcessRunner runner, string python, string checkout, JsonObject result, string? handoff, CancellationToken ct)
    {
        var pkg = result["package"];
        JsonObject? authority = null;
        var detail = $"none yet: HCG Repo issues its passport once the release is in its lock (state now {Str(result, "state")})";
        if (Str(result, "state") == "prepared" && Str(pkg, "identifier") is { } id && Str(pkg, "version") is { } ver && Str(pkg, "architecture") is { } arch)
            (authority, detail) = await AuthorityPassportAsync(runner, python, checkout, id, ver, arch, ct).ConfigureAwait(false);
        return PassportSummary(BuildHalfOf(handoff), authority, detail);
    }

    /// <summary>
    /// The Release Passport section of the status: the build half from the handoff, the distribution authority's
    /// passport when HCG Repo has one, and their reconciliation. Repository signing comes only from HCG Repo's passport;
    /// code signing is the Mach-O code inside the package. The two are shown separately.
    /// </summary>
    public static string PassportSummary(JsonObject? buildHalf, JsonObject? authority, string authorityDetail)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Release Passport (hcg.release-passport v1):");
        sb.AppendLine($"  Build half (Executable Maker):  {(buildHalf is null ? "none in the handoff" : $"{Distribution.ReleasePassport.FileName}, SHA-256 {Str(buildHalf["artifact"], "sha256")}")}");
        sb.AppendLine($"  Distribution authority:         {(authority is null ? authorityDetail : authorityDetail + ", " + Distribution.ReleasePassport.CheckVersion(authority).Detail)}");
        sb.AppendLine($"  Repository signing (channel metadata, not the app): {(authority is null ? "not stated: only the distribution authority states it" : Distribution.ReleasePassport.RepositorySigningText(authority))}");
        sb.AppendLine($"  Code signing (Mach-O code inside the package):      {Distribution.ReleasePassport.CodeSigningText(authority ?? buildHalf)}{(authority is null ? " (measured by Executable Maker)" : " (as HCG Repo inspected it)")}");
        if (buildHalf is not null && authority is not null)
        {
            var (findings, _) = Distribution.ReleasePassport.Reconcile(buildHalf, authority);
            sb.AppendLine(findings.Count == 0 ? "  Reconciliation: the build half and HCG Repo's passport agree (artifact, release, source, provider, code signing)" : "  Reconciliation:");
            foreach (var f in findings) sb.AppendLine($"    {(f.Level == "error" ? "ERROR" : "warn ")} {f.Field}: {f.Message}");
        }
        return sb.ToString();
    }

    private static async Task<Step> RunAsync(Action action, IProcessRunner runner, string python, string checkout, string? incoming, IReadOnlyList<string> args, Action<string>? log, CancellationToken ct,
        bool releaseCommand = true)
    {
        if (CheckoutProblem(checkout) is { } bad) return new(action, false, 2, bad, null, null, null);
        List<string> all = ["-I", Path.Combine(checkout, "bin", "hcgrepo")];
        if (releaseCommand)
        {
            all.Add("release");
            if (incoming is { Length: > 0 }) all.AddRange(["--incoming", incoming]);
        }
        all.AddRange(args);
        var output = new StringBuilder();
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        // HCG_REPO_INCOMING: the owner's private store, so Maker's submit and the owner's own approve/prepare see the same submissions.
        foreach (var k in new[] { "PATH", "HOME", "LANG", "TMPDIR", "HCG_REPO_INCOMING" })
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
