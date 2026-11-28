using System.Text;
using System.Text.Json.Nodes;
using HCG.ExecutableMaker.BuildEngine.MacXcode;
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
/// HCG Server's Build Host (<c>hcg.buildhost/1</c>) as an <see cref="IBuildHost"/> of kind
/// <see cref="BuildHostKind.HcgServerHost"/>, the second provider of the native build-provider contract
/// (docs/contracts/NATIVE-BUILD-PROVIDER.md, provider kind <c>hcg-server-build-host</c>). Submits the job, shows that it
/// waits for the owner's approval on HCG Server, follows it and its log, then downloads the artifacts (each checked
/// against the job record, the server's checksum header and the bytes received), verifies the ES256-signed provenance
/// and reads the validation report. It writes the same <c>native-build-request.json</c> / <c>native-build-result.json</c>
/// as the local provider (approval, source, limits and isolation from the signed provenance), keeps the signed
/// provenance and validation report next to them, and returns an ordinary <see cref="BuildRecord"/> with
/// <see cref="ArtifactRecord"/>s plus the evidence for HCG Repo. The Build Host builds unsigned; signing stays in
/// Executable Maker.
/// </summary>
public sealed class HcgServerBuildHost(HcgBuildHostClient client, IAppPaths paths, TimeProvider time, IBuildRepository? builds = null) : IBuildHost
{
    public const string ProviderKind = BuildProviderIds.HcgServerBuildHost;
    public const string ProvenanceFileName = "buildhost-provenance.json", ValidationFileName = "buildhost-validation.json";
    public string DisplayName => "HCG Server Build Host";
    public IsolationLevel Isolation => IsolationLevel.Isolated;
    public HcgBuildHostClient Client => client;

    private BuildHostStatus? _status;
    private BuildProvenanceHost? _host;

    /// <summary>Reads the Build Host's status (toolchains, providers, isolation) for <see cref="Describe"/> and <see cref="CannotBuild"/>.</summary>
    public async Task<BuildHostStatus> RefreshAsync(CancellationToken ct = default) => _status = await client.GetStatusAsync(ct).ConfigureAwait(false);

    public BuildHostInfo Describe() => new(ProviderKind, BuildHostKind.HcgServerHost, $"{DisplayName} ({client.BaseUrl})",
        _host is null ? "unknown until a job has run" : _host.Os, _host is null ? "unknown until a job has run" : _host.Architecture,
        (_status?.Toolchains ?? []).GroupBy(t => t.Tool).ToDictionary(g => g.Key, g => g.First().Version));

    public string? CannotBuild(TargetPlatform platform)
    {
        if (_status is null) return "HCG Server's Build Host has not been contacted yet (status unknown).";
        if (_status.Refusal is { } refusal) return refusal;
        return platform is TargetPlatform.IOS or TargetPlatform.MacOS && _status.Providers.FirstOrDefault(p => p.Id == "xcode") is not { Available: true }
            ? "HCG Server's Build Host has no Xcode: iOS and macOS builds need its xcode provider." : null;
    }

    public async Task<BuildHostOutcome> RunAsync(BuildHostBuildRequest request, IBuildObserver observer, CancellationToken cancellationToken)
    {
        var hostObserver = observer as IBuildHostObserver;
        var build = new BuildRecord
        {
            Id = Guid.NewGuid(), ProjectId = request.ProjectId, ProjectName = request.ProjectName, Settings = request.Settings,
            Status = BuildStatus.Running, StartedAt = time.GetUtcNow(),
        };
        var buildDir = Path.Combine(paths.BuildsDirectory, build.Id.ToString("N"));
        JsonObject? nativeRequest = null;
        var passports = new Dictionary<string, string>();
        var artifactDir = Path.Combine(buildDir, "buildhost-artifacts");
        Directory.CreateDirectory(buildDir);
        build.LogPath = Path.Combine(buildDir, "build.log");
        build.ToolchainVersions["hcg-executable-maker"] = HostInfo.ProductVersion;
        build.ToolchainVersions["build-provider"] = ProviderKind;

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
            var status = await RefreshAsync(cancellationToken).ConfigureAwait(false);
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
                _host = p.BuildHost;
                await File.WriteAllTextAsync(Path.Combine(buildDir, ProvenanceFileName),
                    System.Text.Json.JsonSerializer.Serialize(provenance.Envelope, BuildHostContract.Json) + "\n", CancellationToken.None).ConfigureAwait(false);
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
                await File.WriteAllTextAsync(Path.Combine(buildDir, ValidationFileName),
                    System.Text.Json.JsonSerializer.Serialize(validation, BuildHostContract.Json) + "\n", CancellationToken.None).ConfigureAwait(false);
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
                    // Release Passport build half for each distributable output (not logs or test reports), next to the file,
                    // so Ship to Store reads provider, commit and toolchain from it like any other build.
                    foreach (var record in build.Artifacts)
                    {
                        var a = job.Artifacts.First(x => x.Name == record.FileName);
                        if (a.Kind is "log" or "test-report") continue;
                        var passport = Passport(request, provenance, evidence, a, record);
                        if (Packaging.Distribution.ReleasePassport.ValidateBuildHalf(passport) is { Count: > 0 } problems)
                        { Log(BuildLogKind.Warning, $"No Release Passport for {a.Name}: " + string.Join("; ", problems)); continue; }
                        var file = record.Path + ".release-passport.json";
                        await File.WriteAllTextAsync(file, NativeBuildContract.Serialize(passport), CancellationToken.None).ConfigureAwait(false);
                        passports[a.Name] = Path.GetFileName(file);
                        Log(BuildLogKind.Info, $"Release Passport: {Path.GetFileName(file)} (provider {evidence.Provider}, source {evidence.Commit[..12]})");
                    }
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
            // The shared native build-provider contract. The request records what the owner approved, so it is written
            // only from verified, signed provenance: a job that was rejected, or whose provenance did not verify, has none.
            if (job is not null && provenance is not null)
            {
                nativeRequest = NativeRequest(build, request, job, provenance, client.BaseUrl);
                var result = NativeResult(build, nativeRequest, job, provenance, validation, passports);
                await File.WriteAllTextAsync(Path.Combine(buildDir, NativeBuildContract.RequestFileName), NativeBuildContract.Serialize(nativeRequest), CancellationToken.None).ConfigureAwait(false);
                await File.WriteAllTextAsync(Path.Combine(buildDir, NativeBuildContract.ResultFileName), NativeBuildContract.Serialize(result), CancellationToken.None).ConfigureAwait(false);
            }
            else Log(BuildLogKind.Warning, "No native-build-request/result files: there is no verified, signed provenance recording the owner's approval for this job.");
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

    /// <summary>Build Host provider → the request's <c>project.kind</c>.</summary>
    public static string ProjectKind(BuildProject project, BuildHostOptions options) => project.Provider switch
    {
        "xcode" => (options.Project ?? "").EndsWith(".xcworkspace", StringComparison.OrdinalIgnoreCase) ? "xcode-workspace" : "xcode-project",
        "swiftpm" => "swift-package",
        "dotnet" => "dotnet-project",
        "go" => "go-module",
        "node" => "node-package",
        "python" => "python-project",
        var other => other,
    };

    private static string Utc(DateTimeOffset? t) => t?.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ") ?? "";

    /// <summary>
    /// <c>native-build-request.json</c> for a Build Host job: provider <c>hcg-server-build-host</c>, the owner's approval
    /// and the source exactly as the signed provenance records them, the predefined operations, unsigned, and the
    /// Build Host's isolation as the limits.
    /// </summary>
    public static JsonObject NativeRequest(BuildRecord build, BuildHostBuildRequest r, BuildJob job, VerifiedProvenance verified, Uri baseUrl)
    {
        var p = verified.Document;
        var ops = p.Job.Operations.Count > 0 ? p.Job.Operations : r.Operations;
        var permissions = p.Job.Permissions;
        var targets = (r.Options.Products ?? []).Concat(r.Options.Targets ?? []).ToList();
        return new JsonObject
        {
            ["format"] = NativeBuildContract.RequestFormat,
            ["formatVersion"] = NativeBuildContract.FormatVersion,
            ["build"] = build.Id.ToString("N"),
            ["provider"] = new JsonObject
            {
                ["kind"] = ProviderKind,
                ["host"] = $"{p.BuildHost.Name} ({p.BuildHost.DeviceId}, {p.BuildHost.Os}/{p.BuildHost.Architecture}, hcg-agent {p.BuildHost.AgentVersion})",
                ["dependsOnGitHubActions"] = false,
                ["contract"] = BuildHostContract.Contract,
                ["endpoint"] = baseUrl.ToString(),
                ["job"] = p.Job.Id,
                ["keyFingerprint"] = verified.KeyFingerprint,
            },
            ["approval"] = new JsonObject
            {
                ["approvedBy"] = $"{p.Job.ApprovedBy} (the owner, on HCG Server: hcg-agent build approve {p.Job.Id} --confirm {p.Job.Id})",
                ["approvedAt"] = Utc(p.Job.ApprovedAt),
                ["trustAcknowledged"] = true,
                ["scope"] = $"build, test and package this source with the predefined operations {string.Join(", ", ops)} inside HCG Server's sandbox; no signing, no notarization, no upload, no publication",
                ["basis"] = $"provenance signed by HCG Server key {verified.KeyFingerprint}",
            },
            ["source"] = new JsonObject
            {
                ["repository"] = p.Source.Repository, ["commit"] = p.Source.Commit, ["branch"] = null, ["dirty"] = p.Source.Dirty,
                ["basis"] = $"HCG Server Build Host signed provenance: pinned commit fetched from the owner-approved remote, checked-out HEAD verified (tree {p.Source.Tree})",
            },
            ["project"] = new JsonObject
            {
                ["kind"] = ProjectKind(p.Project, r.Options),
                ["container"] = string.IsNullOrEmpty(p.Project.Path) ? "." : p.Project.Path,
                ["scheme"] = r.Options.Scheme ?? "",
                ["configuration"] = r.Options.Configuration ?? "",
                ["architecture"] = p.BuildHost.Architecture switch { "amd64" => "x86_64", var a => a },
                ["appTarget"] = string.Join(" ", targets),
                ["buildHostProject"] = p.Project.Id,
                ["buildHostProvider"] = p.Project.Provider,
            },
            ["actions"] = new JsonArray(ops.Select(o => (JsonNode?)(o == BuildOperations.Resolve ? "resolve-packages" : o)).ToArray()),
            ["signing"] = new JsonObject { ["mode"] = "unsigned", ["note"] = "the Build Host never signs (signing is refused); Executable Maker signs afterwards with its own approval" },
            ["outputs"] = new JsonArray((JsonNode?)nameof(PackagingFormat.BuildHostOutput)),
            ["limits"] = new JsonObject
            {
                ["isolatedWorkspace"] = true,
                ["workspace"] = "HCG Server's per-job workspace; writes are allowed only there",
                ["network"] = permissions.Contains(BuildPermissions.Network) ? "dependency resolution only (network.dependencies approved by the owner)" : "none",
                ["cpu"] = "a timeout per command, enforced by HCG Server",
                ["memory"] = "limited for the whole process tree by HCG Server",
                ["projectScriptsRun"] = true,
                ["isolation"] = new JsonObject
                {
                    ["backend"] = p.Isolation.Backend,
                    ["rules"] = new JsonArray(p.Isolation.Rules.Select(x => (JsonNode?)x).ToArray()),
                    ["gaps"] = new JsonArray((p.Isolation.Gaps ?? []).Select(x => (JsonNode?)x).ToArray()),
                },
            },
        };
    }

    /// <summary><c>native-build-result.json</c> for a Build Host job: its steps, toolchains and the verified artifacts.</summary>
    public static JsonObject NativeResult(BuildRecord build, JsonObject request, BuildJob job, VerifiedProvenance verified, BuildValidation? validation,
        IReadOnlyDictionary<string, string>? passports = null)
    {
        var p = verified.Document;
        var steps = p.Steps.Select(st => new NativeBuildContract.StepRecord(st.Operation, string.Join(' ', st.Argv), st.ExitCode, st.DurationMs / 1000.0)).ToList();
        var artifacts = build.Artifacts.Select(a => (a.FileName, a.Sha256, a.SizeBytes, job.Artifacts.FirstOrDefault(x => x.Name == a.FileName)?.Kind ?? "file", passports?.GetValueOrDefault(a.FileName))).ToList();
        var toolchain = p.Toolchains.GroupBy(t => t.Tool).ToDictionary(g => g.Key, g => g.First().Version);
        var result = NativeBuildContract.Result(build, request, steps, null, artifacts, toolchain);
        result["buildHost"] = new JsonObject
        {
            ["job"] = p.Job.Id, ["state"] = p.Job.State, ["keyFingerprint"] = verified.KeyFingerprint,
            ["provenance"] = ProvenanceFileName, ["validation"] = validation is null ? null : ValidationFileName, ["validationOverall"] = validation?.Overall,
            ["tests"] = p.Tests is { } t ? new JsonObject { ["source"] = t.Source, ["passed"] = t.Passed, ["failed"] = t.Failed, ["skipped"] = t.Skipped } : null,
        };
        return result;
    }

    /// <summary>The Release Passport build half of one Build Host output: provider hcg-server-build-host, no run ID, unsigned.</summary>
    public static JsonObject Passport(BuildHostBuildRequest r, VerifiedProvenance verified, BuildHostEvidence evidence, BuildArtifact a, ArtifactRecord record)
    {
        var p = verified.Document;
        return Packaging.Distribution.ReleasePassport.ForArtifact(new Packaging.Distribution.ReleasePassport.ArtifactFacts(
            Platform: p.BuildHost.Os is "darwin" ? "macos" : p.BuildHost.Os,
            Architecture: p.BuildHost.Architecture switch { "amd64" => "x86_64", var x => x },
            Package: p.Project.ProductId ?? p.Project.Id,
            Version: r.Settings.Identity.Version,
            BuildNumber: null,
            ProductName: p.Project.Name,
            FileName: a.Name, Sha256: record.Sha256, Size: record.SizeBytes, ArtifactKind: a.Kind,
            Source: new Packaging.Distribution.SourceProvenance(evidence.Repository, evidence.Commit, null, evidence.Dirty,
                $"HCG Server Build Host signed provenance (job {evidence.JobId}, key {evidence.KeyFingerprint})"),
            Provider: evidence.Provider,
            Toolchain: $"{evidence.Toolchain}, HCG Executable Maker {HostInfo.ProductVersion}",
            BuiltAt: DateTimeOffset.Parse(evidence.BuiltAt, System.Globalization.CultureInfo.InvariantCulture),
            StaticInspectionState: record.Verification == VerificationStatus.Failed ? "failed" : "passed",
            StaticInspectionNote: "checksum against the job record, X-HCG-SHA256 and the signed provenance; the Build Host's validation report",
            Checks: record.Checks.Select(c => (c.Name, c.Status.ToString(), c.Detail)).ToList(),
            Tests: p.Tests is { } t ? new JsonObject { ["run"] = true, ["passed"] = t.Passed, ["failed"] = t.Failed, ["skipped"] = t.Skipped, ["by"] = t.Source } : null,
            CodeSigning: new JsonObject
            {
                ["scope"] = "code signatures of the built output; not repository signing",
                ["method_claimed"] = "unsigned", ["binaries"] = 0, ["all_signed"] = null,
                ["note"] = "the Build Host never signs; Executable Maker signs afterwards with its own approval",
            },
            IssuedBy: "HCG Executable Maker " + HostInfo.ProductVersion));
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
