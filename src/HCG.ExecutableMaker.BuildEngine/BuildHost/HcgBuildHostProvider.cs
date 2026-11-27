using System.Text;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Security;

namespace HCG.ExecutableMaker.BuildEngine.BuildHost;

/// <summary>A build on HCG Server's Build Host: a registered project, a pinned commit and predefined operations.</summary>
public sealed record BuildHostBuildRequest
{
    /// <summary>Executable Maker's project (the BuildRecord's owner).</summary>
    public required Guid ProjectId { get; init; }
    public required string ProjectName { get; init; }
    /// <summary>What Executable Maker records with the build (identity, platform, output folder).</summary>
    public required BuildSettings Settings { get; init; }
    /// <summary>The project id the owner registered on HCG Server (<c>hcg-agent build project add --id …</c>).</summary>
    public required string BuildHostProjectId { get; init; }
    /// <summary>Full 40-hex commit.</summary>
    public required string Commit { get; init; }
    public required IReadOnlyList<string> Operations { get; init; }
    public BuildHostOptions Options { get; init; } = new();
    public IReadOnlyList<string>? Permissions { get; init; }
    /// <summary>Shown to the owner when they approve.</summary>
    public string? Purpose { get; init; }
    /// <summary>Artifact names to download; null downloads every artifact of the job.</summary>
    public IReadOnlyList<string>? Artifacts { get; init; }
}

/// <summary>Everything a Build Host build produced, after Executable Maker verified it.</summary>
public sealed record BuildHostOutcome(
    BuildRecord Build,
    BuildJob? Job,
    VerifiedProvenance? Provenance,
    BuildValidation? Validation,
    BuildHostEvidence? Evidence,
    string? EvidenceProblem);

/// <summary>Build events specific to the Build Host. Called from background threads.</summary>
public interface IBuildHostObserver : IBuildObserver
{
    /// <summary>The job waits for the owner. Show <paramref name="instruction"/>; Executable Maker never approves.</summary>
    void OnAwaitingApproval(BuildJob job, string instruction) { }
    void OnJobUpdated(BuildJob job) { }
}

/// <summary>
/// HCG Server's Build Host as a build provider (<c>hcg-server-build-host</c>). Submits the job, shows that it waits
/// for the owner's approval on HCG Server, follows it and its log, then downloads the artifacts (each checked against
/// the job record, the server's checksum header and the bytes received), verifies the ES256-signed provenance and
/// reads the validation report. The result is an ordinary <see cref="BuildRecord"/> with <see cref="ArtifactRecord"/>s,
/// plus the evidence for HCG Repo. The Build Host builds unsigned; signing stays in Executable Maker.
/// </summary>
public sealed class HcgBuildHostProvider(HcgBuildHostClient client, IAppPaths paths, TimeProvider time, IBuildRepository? builds = null) : IBuildProvider
{
    public string Id => BuildProviderIds.HcgServerBuildHost;
    public string DisplayName => "HCG Server Build Host";
    public IsolationLevel Isolation => IsolationLevel.Isolated;
    public string Description =>
        "HCG Server builds the pinned commit on your computer, each step a predefined operation inside its isolation sandbox " +
        "(Seatbelt on macOS, Landlock on Linux): no network except approved dependency downloads, no access to your home folder, keychains or SSH keys. Builds are unsigned.";
    public string Approval => "The owner, on HCG Server: hcg-agent build approve <job> --confirm <job>. Executable Maker cannot approve.";
    public HcgBuildHostClient Client => client;

    public async Task<BuildHostOutcome> RunAsync(BuildHostBuildRequest request, IBuildObserver observer, CancellationToken cancellationToken)
    {
        var hostObserver = observer as IBuildHostObserver;
        var build = new BuildRecord
        {
            Id = Guid.NewGuid(), ProjectId = request.ProjectId, ProjectName = request.ProjectName, Settings = request.Settings,
            Status = BuildStatus.Running, StartedAt = time.GetUtcNow(),
        };
        var buildDir = Path.Combine(paths.BuildsDirectory, build.Id.ToString("N"));
        var artifactDir = Path.Combine(buildDir, "buildhost-artifacts");
        Directory.CreateDirectory(buildDir);
        build.LogPath = Path.Combine(buildDir, "build.log");
        build.ToolchainVersions["hcg-executable-maker"] = HostInfo.ProductVersion;
        build.ToolchainVersions["build-provider"] = Id;

        await using var logWriter = new StreamWriter(build.LogPath, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        var gate = new object();
        void Log(BuildLogKind kind, string text)
        {
            text = SecretRedactor.Redact(text);
            var line = new BuildLogLine(time.GetUtcNow(), kind, text);
            lock (gate) logWriter.WriteLine($"[{line.Timestamp:HH:mm:ss}] {(kind is BuildLogKind.Output ? "  " : kind.ToString().ToUpperInvariant() + " ")}{text}");
            observer.OnLog(line);
        }
        void AddMessage(BuildMessage m)
        {
            lock (build.Messages)
            {
                build.Messages.Add(m);
                if (m.Severity == DiagnosticSeverity.Warning) build.WarningCount++;
                if (m.Severity == DiagnosticSeverity.Error) build.ErrorCount++;
            }
            observer.OnMessage(m);
        }
        void Fail(BuildStatus status, string reason)
        {
            build.Status = status;
            build.FailureReason ??= reason;
            Log(BuildLogKind.Error, reason);
        }

        if (builds is not null) await builds.InsertAsync(build, CancellationToken.None).ConfigureAwait(false);
        observer.OnStarted(build);

        BuildJob? job = null;
        VerifiedProvenance? provenance = null;
        BuildValidation? validation = null;
        BuildHostEvidence? evidence = null;
        string? evidenceProblem = null;
        try
        {
            observer.OnStage(BuildStage.Preparing, "Submitting to HCG Server's Build Host");
            Log(BuildLogKind.Info, $"HCG Executable Maker {HostInfo.ProductVersion} — build {build.Id} on {DisplayName} ({client.BaseUrl})");
            var status = await client.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            build.ToolchainVersions["buildhost-isolation"] = $"{status.Isolation}{(status.Enforced ? " (enforced)" : " (NOT enforced)")}";
            Log(status.Enforced ? BuildLogKind.Info : BuildLogKind.Warning, $"Isolation: {status.Isolation}{(status.Enforced ? ", enforced" : ", NOT enforced on this computer")}");

            job = await client.SubmitAsync(new BuildJobRequest
            {
                ProjectId = request.BuildHostProjectId, Commit = request.Commit, Operations = request.Operations, Options = request.Options,
                Permissions = request.Permissions, Purpose = request.Purpose, ClientRef = build.Id.ToString(),
            }, cancellationToken).ConfigureAwait(false);
            build.ToolchainVersions["buildhost-job"] = job.Id;
            Log(BuildLogKind.Info, $"Job {job.Id}: {job.Project.Name} ({job.Project.Source.Repository}) commit {request.Commit} · provider {job.Project.Provider}");
            foreach (var step in job.Plan)
                Log(BuildLogKind.Command, $"{step.Operation}: {string.Join(' ', step.Argv)}{(step.Network ? " (network)" : "")}");

            job = await FollowAsync(job, build, Log, hostObserver, observer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException || ex is HcgBuildHostException { Failure: BuildHostFailure.Cancelled })
        {
            if (job is not null)
            {
                // Stopping in Executable Maker stops the job on HCG Server too (before or while it runs).
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try { job = await client.CancelAsync(job.Id, cts.Token).ConfigureAwait(false); Log(BuildLogKind.Warning, $"Job {job.Id} cancelled on HCG Server."); }
                catch (HcgBuildHostException cancelError) { Log(BuildLogKind.Warning, $"Could not cancel job {job.Id} on HCG Server: {cancelError.Message}"); }
            }
            Fail(BuildStatus.Cancelled, "Cancelled in Executable Maker.");
            return await FinishAsync().ConfigureAwait(false);
        }
        catch (HcgBuildHostException ex)
        {
            Fail(BuildStatus.Failed, ex.Message);
            return await FinishAsync().ConfigureAwait(false);
        }

        // The job ended. Record its outcome, then verify everything it produced.
        build.ExitCode = job.Plan.FirstOrDefault(s => s.ExitCode is not null and not 0)?.ExitCode ?? (job.State == BuildJobStates.Succeeded ? 0 : null);
        foreach (var t in job.Toolchains) build.ToolchainVersions[$"buildhost:{t.Tool}"] = t.Version;
        if (job.DecidedBy is { Length: > 0 } by) build.ToolchainVersions["buildhost-approved-by"] = by;
        if (job.Tests is { } tests)
            AddMessage(new BuildMessage(tests.Failed > 0 ? DiagnosticSeverity.Error : DiagnosticSeverity.Info,
                $"Tests: {tests.Passed} passed, {tests.Failed} failed, {tests.Skipped} skipped ({tests.Source})"));
        switch (job.State)
        {
            case BuildJobStates.Rejected:
                Fail(BuildStatus.Failed, $"The owner rejected job {job.Id} on HCG Server{(job.Detail is { Length: > 0 } d ? ": " + d : "")}.");
                return await FinishAsync().ConfigureAwait(false);
            case BuildJobStates.Cancelled:
                Fail(BuildStatus.Cancelled, $"Job {job.Id} was cancelled on HCG Server{(job.Detail is { Length: > 0 } cd ? ": " + cd : "")}.");
                break;
            case BuildJobStates.Failed:
                Fail(BuildStatus.Failed, $"Job {job.Id} failed on HCG Server: {job.Detail ?? "see the log"}.");
                break;
        }

        try
        {
            observer.OnStage(BuildStage.VerifyingOutput, "Verifying the Build Host's provenance and validation report");
            try
            {
                provenance = await client.GetProvenanceAsync(job.Id, cancellationToken).ConfigureAwait(false);
                var p = provenance.Document;
                Log(BuildLogKind.Success, $"Provenance verified: ES256 signature by HCG Server key {provenance.KeyFingerprint} ({p.BuildHost.Name}, {p.BuildHost.DeviceId}).");
                build.ToolchainVersions["buildhost-key"] = provenance.KeyFingerprint;
                build.ToolchainVersions["buildhost-device"] = $"{p.BuildHost.Name} ({p.BuildHost.DeviceId}, {p.BuildHost.Os}/{p.BuildHost.Architecture}, hcg-agent {p.BuildHost.AgentVersion})";
                foreach (var t in p.Toolchains) build.ToolchainVersions[$"buildhost:{t.Tool}"] = t.Version;
                if (p.Source.Commit != request.Commit.ToLowerInvariant())
                    Fail(BuildStatus.Failed, $"The provenance records commit {p.Source.Commit}, not the requested {request.Commit}.");
                if (!SameArtifacts(p.Artifacts, job.Artifacts))
                    Fail(BuildStatus.Failed, "The signed provenance and the job record list different artifacts.");
            }
            catch (HcgBuildHostException ex) when (ex.Failure is BuildHostFailure.NotFound or BuildHostFailure.NotReady)
            {
                Fail(BuildStatus.Failed, $"No provenance for job {job.Id}: {ex.Message}");
            }
            catch (HcgBuildHostException ex) when (ex.Failure is BuildHostFailure.SignatureInvalid)
            {
                Fail(BuildStatus.Failed, ex.Message);
            }

            try
            {
                validation = await client.GetValidationAsync(job.Id, cancellationToken).ConfigureAwait(false);
                foreach (var c in validation.Checks)
                    AddMessage(new BuildMessage(c.Status switch { "passed" or "not-applicable" => DiagnosticSeverity.Info, "failed" => DiagnosticSeverity.Error, _ => DiagnosticSeverity.Warning },
                        $"Build Host check {c.Name}: {c.Status} — {c.Detail}", Code: "buildhost"));
                Log(validation.Overall == "passed" ? BuildLogKind.Success : BuildLogKind.Error, $"Build Host validation: {validation.Overall}");
                if (validation.Overall != "passed") Fail(BuildStatus.Failed, $"The Build Host's validation report is {validation.Overall}.");
            }
            catch (HcgBuildHostException ex) when (ex.Failure is BuildHostFailure.NotFound or BuildHostFailure.NotReady)
            {
                Fail(BuildStatus.Failed, $"No validation report for job {job.Id}: {ex.Message}");
            }

            observer.OnStage(BuildStage.Checksumming, "Downloading and verifying artifacts");
            foreach (var a in job.Artifacts.Where(a => request.Artifacts is null || request.Artifacts.Contains(a.Name)))
            {
                var dest = Path.Combine(artifactDir, a.Name);
                try
                {
                    await client.DownloadArtifactAsync(job, a.Name, dest, cancellationToken).ConfigureAwait(false);
                }
                catch (HcgBuildHostException ex) when (ex.Failure is BuildHostFailure.Integrity or BuildHostFailure.NotFound)
                {
                    Fail(BuildStatus.Failed, ex.Message);
                    continue;
                }
                var record = ToArtifactRecord(build.Id, a, dest, provenance, validation, time.GetUtcNow());
                build.Artifacts.Add(record);
                Log(record.Verification == VerificationStatus.Failed ? BuildLogKind.Error : BuildLogKind.Success, $"{a.Name} ({a.Kind}, {a.Size:N0} bytes) SHA-256 {a.Sha256}: {record.Verification}");
                if (record.Verification == VerificationStatus.Failed) Fail(BuildStatus.Failed, $"{a.Name} did not pass verification.");
            }
            foreach (var missing in request.Artifacts?.Where(n => job.Artifacts.All(a => a.Name != n)) ?? [])
                Fail(BuildStatus.Failed, $"Job {job.Id} produced no artifact named {missing}.");

            if (provenance is not null && job.State == BuildJobStates.Succeeded)
            {
                try
                {
                    evidence = BuildHostSubmission.FromProvenance(provenance, request.Commit);
                    Log(BuildLogKind.Info, $"HCG Repo evidence: provider {evidence.Provider}, {evidence.Repository}@{evidence.Commit}, toolchain {evidence.Toolchain}, built {evidence.BuiltAt} (no CI run).");
                }
                catch (HcgBuildHostException ex) { evidenceProblem = ex.Message; Log(BuildLogKind.Warning, ex.Message); }
            }
            else evidenceProblem = job.State == BuildJobStates.Succeeded ? "The provenance did not verify." : $"The job ended {job.State}.";
        }
        catch (Exception ex) when (ex is OperationCanceledException || ex is HcgBuildHostException { Failure: BuildHostFailure.Cancelled })
        {
            Fail(BuildStatus.Cancelled, "Cancelled in Executable Maker while verifying the Build Host's output.");
        }
        catch (HcgBuildHostException ex)
        {
            Fail(BuildStatus.Failed, ex.Message);
        }

        if (build.Status == BuildStatus.Running) build.Status = BuildStatus.Succeeded;
        return await FinishAsync().ConfigureAwait(false);

        async Task<BuildHostOutcome> FinishAsync()
        {
            build.EndedAt = time.GetUtcNow();
            observer.OnStage(BuildStage.Completed, build.Status.ToString());
            Log(build.Status == BuildStatus.Succeeded ? BuildLogKind.Success : BuildLogKind.Error,
                $"Build {build.Status}{(build.FailureReason is null ? "" : ": " + build.FailureReason)}");
            if (builds is not null) await builds.UpdateAsync(build, CancellationToken.None).ConfigureAwait(false);
            return new BuildHostOutcome(build, job, provenance, validation, evidence, evidenceProblem);
        }
    }

    private async Task<BuildJob> FollowAsync(BuildJob job, BuildRecord build, Action<BuildLogKind, string> log, IBuildHostObserver? hostObserver, IBuildObserver observer, CancellationToken ct)
    {
        string? lastState = null;
        long offset = 0;
        var pending = new StringBuilder();
        while (true)
        {
            if (job.State != lastState)
            {
                lastState = job.State;
                switch (job.State)
                {
                    case BuildJobStates.AwaitingApproval:
                        var instruction = HcgBuildHostClient.ApprovalInstruction(job.Id);
                        observer.OnStage(BuildStage.Preparing, instruction);
                        log(BuildLogKind.Warning, instruction);
                        hostObserver?.OnAwaitingApproval(job, instruction);
                        break;
                    case BuildJobStates.Queued:
                        observer.OnStage(BuildStage.Preparing, $"Approved by {job.DecidedBy ?? "the owner"}; queued on HCG Server (jobs run one at a time)");
                        log(BuildLogKind.Info, $"Job {job.Id} approved by {job.DecidedBy ?? "the owner"}; queued.");
                        break;
                    case BuildJobStates.Running:
                        observer.OnStage(BuildStage.Compiling, "Building on HCG Server's Build Host");
                        log(BuildLogKind.Info, $"Job {job.Id} running.");
                        break;
                    default:
                        log(job.State == BuildJobStates.Succeeded ? BuildLogKind.Success : BuildLogKind.Error, $"Job {job.Id} {job.State}{(job.Detail is { Length: > 0 } d ? ": " + d : "")}.");
                        break;
                }
            }
            hostObserver?.OnJobUpdated(job);
            if (job.State is BuildJobStates.Running || job.IsTerminal && job.State != BuildJobStates.Rejected)
            {
                var (bytes, next) = await client.GetLogsAsync(job.Id, offset, ct).ConfigureAwait(false);
                if (next >= offset) { offset = next; pending.Append(Encoding.UTF8.GetString(bytes)); }
                var text = pending.ToString();
                var end = text.LastIndexOf('\n');
                if (end >= 0)
                {
                    foreach (var line in text[..end].Split('\n')) log(BuildLogKind.Output, line.TrimEnd('\r'));
                    pending.Clear().Append(text[(end + 1)..]);
                }
                if (job.IsTerminal && pending.Length > 0) { log(BuildLogKind.Output, pending.ToString()); pending.Clear(); }
            }
            if (job.IsTerminal) return job;
            await Task.Delay(client.Settings.PollInterval, ct).ConfigureAwait(false);
            job = await client.GetJobAsync(job.Id, ct).ConfigureAwait(false);
        }
    }

    private static bool SameArtifacts(IReadOnlyList<BuildArtifact> a, IReadOnlyList<BuildArtifact> b) =>
        a.Count == b.Count && a.OrderBy(x => x.Name, StringComparer.Ordinal).Zip(b.OrderBy(x => x.Name, StringComparer.Ordinal))
            .All(p => p.First.Name == p.Second.Name && p.First.Size == p.Second.Size && string.Equals(p.First.Sha256, p.Second.Sha256, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// An artifact Executable Maker downloaded and verified. Its checks: the download (job record, X-HCG-SHA256 and the
    /// bytes received agree), the signed provenance (lists it with the same SHA-256 and size) and the Build Host's own
    /// validation line for it.
    /// </summary>
    public static ArtifactRecord ToArtifactRecord(Guid buildId, BuildArtifact a, string path, VerifiedProvenance? provenance, BuildValidation? validation, DateTimeOffset now)
    {
        var checks = new List<VerificationCheck>
        {
            new("Checksum", VerificationStatus.Passed, $"SHA-256 {a.Sha256}, {a.Size:N0} bytes: the job record, {BuildHostContract.Sha256Header} and the bytes received agree"),
        };
        if (provenance is null)
            checks.Add(new("Signed provenance", VerificationStatus.NotVerifiable, "no verified provenance for this job"));
        else if (provenance.Document.Artifacts.Any(p => p.Name == a.Name && p.Size == a.Size && string.Equals(p.Sha256, a.Sha256, StringComparison.OrdinalIgnoreCase)))
            checks.Add(new("Signed provenance", VerificationStatus.Passed, $"listed with this SHA-256 in provenance signed by {provenance.KeyFingerprint}"));
        else
            checks.Add(new("Signed provenance", VerificationStatus.Failed, "the signed provenance does not list this artifact with this SHA-256 and size"));
        if (validation?.Checks.FirstOrDefault(c => c.Name == "artifact " + a.Name) is { } v)
            checks.Add(new("Build Host validation", MapStatus(v.Status), v.Detail));
        checks.Add(new("Code signature", VerificationStatus.NotVerifiable, "unsigned: the Build Host never signs; Executable Maker signs afterwards"));
        return new ArtifactRecord
        {
            Id = Guid.NewGuid(), BuildId = buildId, Format = PackagingFormat.BuildHostOutput, Kind = ArtifactKind.File, Path = path,
            SizeBytes = a.Size, Sha256 = a.Sha256.ToLowerInvariant(),
            Verification = checks.Any(c => c.Status == VerificationStatus.Failed) ? VerificationStatus.Failed
                : checks.Any(c => c.Status == VerificationStatus.PassedWithWarnings) ? VerificationStatus.PassedWithWarnings : VerificationStatus.Passed,
            Checks = checks, CreatedAt = now,
        };
    }

    /// <summary>Build Host check status → Executable Maker's verification status.</summary>
    public static VerificationStatus MapStatus(string status) => status switch
    {
        "passed" => VerificationStatus.Passed,
        "failed" => VerificationStatus.Failed,
        _ => VerificationStatus.NotVerifiable, // not-run, not-applicable
    };
}
