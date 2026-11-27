using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HCG.ExecutableMaker.Account;
using HCG.ExecutableMaker.BuildEngine;
using HCG.ExecutableMaker.BuildEngine.BuildHost;
using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.Tests;

/// <summary>
/// A stand-in for HCG Server's Build Host client API (hcg.buildhost/1): one job that waits for approval for a few polls,
/// then runs and succeeds, with a build.log artifact, a validation report and ES256-signed provenance. Knobs let tests
/// change the contract header, corrupt the artifact or tamper with the provenance.
/// </summary>
internal sealed class FakeBuildHost : HttpMessageHandler
{
    public const string Token = "bhc_test_token_0123456789abcdef";
    public const string JobId = "bj-20261125-120000-abcdef0123";
    public const string Commit = "21326ef1bf1f31d9f2a4a79797cb5bd95a60622b";

    public readonly ECDsa Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public byte[] Artifact = Encoding.UTF8.GetBytes("swift build\nBuild complete!\n");
    public string ContractHeader = "hcg.buildhost/1";
    public int AwaitingPolls = 2;
    public bool CorruptArtifactBytes;
    public string? Sha256HeaderOverride;
    public Func<byte[], byte[]>? TamperPayload;
    public string? FingerprintOverride;
    public bool DirtySource;
    public string ArtifactName = "build.log", ArtifactKind = "log";
    /// <summary>The fingerprint inside the signed payload; null = the signing key's (HCG Server 417a9cd and later).</summary>
    public string? PayloadFingerprint;
    public readonly List<string> Requests = [];
    private int _polls;

    public string Fingerprint => Convert.ToHexStringLower(SHA256.HashData(Key.ExportSubjectPublicKeyInfo()));
    public string ArtifactSha => Convert.ToHexStringLower(SHA256.HashData(Artifact));
    private BuildArtifact ArtifactRecord => new() { Name = ArtifactName, Kind = ArtifactKind, Size = Artifact.Length, Sha256 = ArtifactSha };

    public string State => _polls < AwaitingPolls ? BuildJobStates.AwaitingApproval : _polls == AwaitingPolls ? BuildJobStates.Running : BuildJobStates.Succeeded;

    private object Job() => new
    {
        id = JobId, state = State, request = new { projectId = "lifeos", commit = Commit, operations = new[] { "build" }, options = new { }, client = "executable-maker" },
        project = new { id = "lifeos", name = "HCG LifeOS", source = new { repository = "F1r3srp3nt/HCG-LifeOS", remote = "/src/lifeos" }, provider = "swiftpm", operations = new[] { "build" } },
        plan = new[] { new { operation = "build", argv = new[] { "swift", "build" }, network = false, exitCode = State == BuildJobStates.Succeeded ? (int?)0 : null } },
        toolchains = new[] { new { provider = "swiftpm", tool = "swift", path = "/usr/bin/swift", version = "Swift version 6.0.3" } },
        decidedBy = State == BuildJobStates.AwaitingApproval ? null : "owner",
        endedAt = State == BuildJobStates.Succeeded ? "2026-11-25T12:01:00Z" : null,
        artifacts = State == BuildJobStates.Succeeded ? new[] { ArtifactRecord } : [],
    };

    public SignedDocument Provenance()
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = BuildHostContract.ProvenanceFormat,
            buildHost = new { deviceId = "dev_test", name = "test-mac", keyFingerprint = PayloadFingerprint ?? Fingerprint, agentVersion = "0.1.0", os = "darwin", architecture = "arm64" },
            source = new { repository = "F1r3srp3nt/HCG-LifeOS", remote = "/src/lifeos", commit = Commit, tree = new string('a', 40), dirty = DirtySource },
            project = new { id = "lifeos", name = "HCG LifeOS", productId = "hcg.lifeos", source = new { repository = "F1r3srp3nt/HCG-LifeOS", remote = "/src/lifeos" }, provider = "swiftpm", operations = new[] { "build" } },
            job = new { id = JobId, client = "executable-maker", state = "succeeded", approvedBy = "owner", approvedAt = "2026-11-25T12:00:30Z", operations = new[] { "build" }, permissions = Array.Empty<string>() },
            isolation = new { backend = "seatbelt", rules = new[] { "filesystem: only the workspace is writable" }, gaps = new[] { "UDP" } },
            steps = new[] { new { operation = "build", argv = new[] { "swift", "build" }, network = false, exitCode = 0, durationMs = 1500 } },
            tests = new { source = "xunit", passed = 3, failed = 0, skipped = 0 },
            toolchains = new[] { new { provider = "swiftpm", tool = "swift", path = "/usr/bin/swift", version = "Swift version 6.0.3" } },
            artifacts = new[] { ArtifactRecord },
            submission = new
            {
                source = new { repository = "F1r3srp3nt/HCG-LifeOS", commit = Commit, dirty = DirtySource },
                build = new { provider = "hcg-server-build-host", toolchain = "Swift version 6.0.3", builtAt = "2026-11-25T12:01:00Z" },
                signing = new { method = "unsigned" },
            },
            issuedAt = "2026-11-25T12:01:01Z",
        });
        var signature = Key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        if (TamperPayload is not null) payload = TamperPayload(payload);
        return new SignedDocument
        {
            PayloadType = BuildHostContract.ProvenancePayloadType, Payload = payload, Algorithm = "ES256-ASN1",
            KeyFingerprint = FingerprintOverride ?? Fingerprint, PublicKey = Convert.ToBase64String(Key.ExportSubjectPublicKeyInfo()), Signature = Convert.ToBase64String(signature),
        };
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.PathAndQuery;
        lock (Requests) Requests.Add($"{request.Method} {path}");
        HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(JsonSerializer.Serialize(body, BuildHostContract.Json), Encoding.UTF8, "application/json") };
        HttpResponseMessage response;
        if (request.Headers.Authorization?.ToString() != "Bearer " + Token)
            return Task.FromResult(Json(new { error = "unknown or revoked build-client token", code = "unauthorized" }, HttpStatusCode.Unauthorized));
        const string p = "/buildhost/v1/";
        if (path == p + "status") response = Json(new { contract = ContractHeader, isolation = "seatbelt", enforced = true, toolchains = Array.Empty<object>(), providers = Array.Empty<object>(), queued = 0 });
        else if (path == p + "jobs" && request.Method == HttpMethod.Post) response = Json(Job(), HttpStatusCode.Accepted);
        else if (path == p + $"jobs/{JobId}") { response = Json(Job()); _polls++; }
        else if (path.StartsWith(p + $"jobs/{JobId}/logs", StringComparison.Ordinal))
        {
            var offset = int.Parse(path.Split("offset=")[1]);
            var all = Encoding.UTF8.GetBytes("Compiling LifeOSCore\nBuild complete!\n");
            response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(all[Math.Min(offset, all.Length)..]) };
            response.Headers.Add(BuildHostContract.NextOffsetHeader, all.Length.ToString());
        }
        else if (path == p + $"jobs/{JobId}/artifacts/{ArtifactName}")
        {
            var bytes = CorruptArtifactBytes ? Artifact.Select(b => (byte)(b ^ 1)).ToArray() : Artifact;
            response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            response.Headers.Add(BuildHostContract.Sha256Header, Sha256HeaderOverride ?? ArtifactSha);
        }
        else if (path == p + $"jobs/{JobId}/provenance") response = Json(Provenance());
        else if (path == p + $"jobs/{JobId}/validation")
            response = Json(new { format = BuildHostContract.ValidationFormat, jobId = JobId, overall = "passed",
                checks = new[] { new { name = "owner approval", status = "passed", detail = "approved by owner" }, new { name = "artifact " + ArtifactName, status = "passed", detail = "sha256 " + ArtifactSha } } });
        else if (path == p + $"jobs/{JobId}/cancel") response = Json(Job());
        else response = Json(new { error = "no such route", code = "not_found" }, HttpStatusCode.NotFound);
        response.Headers.Add(BuildHostContract.ContractHeader, ContractHeader);
        return Task.FromResult(response);
    }

    protected override void Dispose(bool disposing) { if (disposing) Key.Dispose(); base.Dispose(disposing); }
}

public class BuildHostTests : IDisposable
{
    private readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    private static async Task<HcgBuildHostClient> Client(HttpMessageHandler host, string? pin = null, string token = FakeBuildHost.Token)
    {
        var credentials = new BuildHostCredentials(new SessionOnlyCredentialStore());
        await credentials.SaveAsync(token);
        return new HcgBuildHostClient(new HttpClient(host), credentials, new HcgBuildHostSettings { PollInterval = TimeSpan.FromMilliseconds(1), ExpectedKeyFingerprint = pin });
    }

    private static BuildJob SucceededJob(FakeBuildHost host) => new()
    {
        Id = FakeBuildHost.JobId, State = BuildJobStates.Succeeded,
        Artifacts = [new BuildArtifact { Name = "build.log", Kind = "log", Size = host.Artifact.Length, Sha256 = host.ArtifactSha }],
    };

    [Theory]
    [InlineData("http://192.168.1.20:7600/buildhost/v1")]
    [InlineData("https://build.example.com/buildhost/v1")]
    [InlineData("http://127.0.0.1.example.com/buildhost/v1")]
    [InlineData("ftp://127.0.0.1/buildhost/v1")]
    public void Only_loopback_Build_Host_URLs_are_accepted(string url)
    {
        Assert.Throws<ArgumentException>(() => HcgBuildHostClient.ValidateBaseUrl(new Uri(url)));
        Assert.Equal("http://127.0.0.1:7600/buildhost/v1/", HcgBuildHostClient.ValidateBaseUrl(HcgBuildHostSettings.DefaultBaseUrl).ToString());
        Assert.Equal("http://[::1]:7600/buildhost/v1/", HcgBuildHostClient.ValidateBaseUrl(new Uri("http://[::1]:7600/buildhost/v1")).ToString());
    }

    [Theory]
    [InlineData("hcg.buildhost/2", "supports hcg.buildhost/1 only")]
    [InlineData("hcg.buildhost/10.1", "supports hcg.buildhost/1 only")]
    [InlineData("hcg.other/1", "not hcg.buildhost/1")]
    [InlineData("garbage", "not hcg.buildhost/1")]
    public async Task An_unknown_contract_or_major_version_is_refused(string header, string expected)
    {
        using var host = new FakeBuildHost { ContractHeader = header };
        var client = await Client(host);
        var ex = await Assert.ThrowsAsync<HcgBuildHostException>(() => client.GetStatusAsync());
        Assert.Equal(BuildHostFailure.ContractMismatch, ex.Failure);
        Assert.Contains(expected, ex.Message);
        // A minor version of the same major is fine.
        host.ContractHeader = "hcg.buildhost/1.3";
        Assert.Equal("hcg.buildhost/1.3", (await client.GetStatusAsync()).Contract);
    }

    [Fact]
    public async Task A_refused_token_is_reported_without_echoing_it_and_a_missing_token_is_not_configured()
    {
        using var host = new FakeBuildHost();
        var wrong = await Client(host, token: "bhc_wrong_token_00000000000000");
        var ex = await Assert.ThrowsAsync<HcgBuildHostException>(() => wrong.GetStatusAsync());
        Assert.Equal(BuildHostFailure.Unauthorized, ex.Failure);
        Assert.DoesNotContain("bhc_wrong_token", ex.Message);

        var none = new HcgBuildHostClient(new HttpClient(host), new BuildHostCredentials(new SessionOnlyCredentialStore()));
        Assert.Equal(BuildHostFailure.NotConfigured, (await Assert.ThrowsAsync<HcgBuildHostException>(() => none.GetStatusAsync())).Failure);
    }

    [Fact]
    public async Task Signing_and_device_permissions_and_unpinned_commits_are_refused_before_submitting()
    {
        using var host = new FakeBuildHost();
        var client = await Client(host);
        var request = new BuildJobRequest { ProjectId = "lifeos", Commit = FakeBuildHost.Commit, Operations = ["build"] };
        Assert.Equal("signing_unavailable", (await Assert.ThrowsAsync<HcgBuildHostException>(() => client.SubmitAsync(request with { Permissions = ["signing"] }))).Code);
        Assert.Equal("device_unavailable", (await Assert.ThrowsAsync<HcgBuildHostException>(() => client.SubmitAsync(request with { Permissions = ["device"] }))).Code);
        Assert.Equal("bad_commit", (await Assert.ThrowsAsync<HcgBuildHostException>(() => client.SubmitAsync(request with { Commit = "21326ef" }))).Code);
        Assert.Empty(host.Requests);
    }

    [Fact]
    public async Task A_verified_download_is_kept_and_any_checksum_or_size_mismatch_deletes_it()
    {
        using var host = new FakeBuildHost();
        var client = await Client(host);
        var job = SucceededJob(host);
        var dest = _t.Combine("dl", "build.log");
        await client.DownloadArtifactAsync(job, "build.log", dest);
        Assert.Equal(host.ArtifactSha, Checksums.Sha256File(dest));

        // The bytes received do not hash to the recorded SHA-256 (the header still claims the right one).
        File.Delete(dest);
        host.CorruptArtifactBytes = true;
        var ex = await Assert.ThrowsAsync<HcgBuildHostException>(() => client.DownloadArtifactAsync(job, "build.log", dest));
        Assert.Equal(BuildHostFailure.Integrity, ex.Failure);
        Assert.Contains("hash to", ex.Message);
        Assert.False(File.Exists(dest));
        Assert.False(File.Exists(dest + ".partial"));

        // The server's X-HCG-SHA256 disagrees with the job record.
        host.CorruptArtifactBytes = false;
        host.Sha256HeaderOverride = new string('0', 64);
        ex = await Assert.ThrowsAsync<HcgBuildHostException>(() => client.DownloadArtifactAsync(job, "build.log", dest));
        Assert.Equal(BuildHostFailure.Integrity, ex.Failure);
        Assert.Contains("X-HCG-SHA256", ex.Message);
        Assert.False(File.Exists(dest));

        // The job record and the server disagree about the size.
        host.Sha256HeaderOverride = null;
        var lying = job with { Artifacts = [job.Artifacts[0] with { Size = host.Artifact.Length + 5 }] };
        Assert.Equal(BuildHostFailure.Integrity, (await Assert.ThrowsAsync<HcgBuildHostException>(() => client.DownloadArtifactAsync(lying, "build.log", dest))).Failure);
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public async Task Provenance_verifies_only_with_an_intact_signature_matching_key_fingerprint_and_pin()
    {
        using var host = new FakeBuildHost();
        var verified = await (await Client(host)).GetProvenanceAsync(FakeBuildHost.JobId);
        Assert.Equal(host.Fingerprint, verified.KeyFingerprint);
        Assert.Equal(FakeBuildHost.Commit, verified.Document.Source.Commit);

        // Pinned to HCG Server's key (formatted in groups of four, as hcg-agent prints it): accepted.
        var grouped = string.Join(' ', Enumerable.Range(0, 16).Select(i => host.Fingerprint.Substring(i * 4, 4)));
        Assert.Equal(host.Fingerprint, (await (await Client(host, pin: grouped)).GetProvenanceAsync(FakeBuildHost.JobId)).KeyFingerprint);

        // One changed byte in the signed payload.
        host.TamperPayload = b => { var c = (byte[])b.Clone(); var i = Encoding.UTF8.GetString(c).IndexOf("2132", StringComparison.Ordinal); c[i] = (byte)'9'; return c; };
        var ex = await Assert.ThrowsAsync<HcgBuildHostException>(() => Client(host).Result.GetProvenanceAsync(FakeBuildHost.JobId));
        Assert.Equal(BuildHostFailure.SignatureInvalid, ex.Failure);
        Assert.Contains("signature does not match", ex.Message);
        host.TamperPayload = null;

        // The envelope names another key.
        host.FingerprintOverride = new string('a', 64);
        Assert.Equal(BuildHostFailure.SignatureInvalid, (await Assert.ThrowsAsync<HcgBuildHostException>(() => Client(host).Result.GetProvenanceAsync(FakeBuildHost.JobId))).Failure);
        host.FingerprintOverride = null;

        // A valid signature by a key other than the pinned one.
        ex = await Assert.ThrowsAsync<HcgBuildHostException>(() => Client(host, pin: new string('b', 64)).Result.GetProvenanceAsync(FakeBuildHost.JobId));
        Assert.Contains("pinned", ex.Message);

        // A signature by another key with that key's own (consistent) fingerprint still verifies only against its key.
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var doc = host.Provenance() with { PublicKey = Convert.ToBase64String(other.ExportSubjectPublicKeyInfo()), KeyFingerprint = Convert.ToHexStringLower(SHA256.HashData(other.ExportSubjectPublicKeyInfo())) };
        Assert.Throws<HcgBuildHostException>(() => HcgBuildHostClient.VerifyProvenance(doc));
    }

    [Fact]
    public async Task The_provider_waits_for_the_owner_never_approves_and_maps_the_result_and_evidence()
    {
        using var host = new FakeBuildHost { AwaitingPolls = 3 };
        var client = await Client(host);
        var paths = new AppPaths(_t.Combine("app"));
        paths.EnsureCreated();
        var provider = new HcgServerBuildHost(client, paths, TimeProvider.System);
        Assert.Equal((BuildProviderIds.HcgServerBuildHost, BuildHostKind.HcgServerHost), (provider.Describe().Id, provider.Describe().Kind));
        var observer = new Observer();
        var outcome = await provider.RunAsync(new BuildHostBuildRequest
        {
            ProjectId = Guid.NewGuid(), ProjectName = "HCG LifeOS", BuildHostProjectId = "lifeos", Commit = FakeBuildHost.Commit, Operations = ["build"],
            Settings = new BuildSettings
            {
                ProjectRelativePath = ".", Platform = TargetPlatform.MacOS, Architecture = TargetArchitecture.Arm64, OutputDirectory = paths.DefaultOutputDirectory,
                Identity = new AppIdentity { ApplicationName = "HCG LifeOS", Version = "0.1.0", ApplicationId = "hcg.lifeos" },
            },
        }, observer, CancellationToken.None);

        // Waiting for approval was shown, with the owner's exact command; no request ever tried to approve.
        var instruction = $"Waiting for approval on HCG Server (hcg-agent build approve {FakeBuildHost.JobId} --confirm {FakeBuildHost.JobId}).";
        Assert.Equal([instruction], observer.Approvals);
        Assert.Contains(observer.Lines, l => l == instruction);
        Assert.DoesNotContain(host.Requests, r => r.Contains("approve", StringComparison.OrdinalIgnoreCase) || r.Contains("decide", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(host.Requests, r => r.Contains("/admin", StringComparison.Ordinal));

        var b = outcome.Build;
        Assert.True(b.Status == BuildStatus.Succeeded, b.FailureReason + "\n" + File.ReadAllText(b.LogPath!));
        Assert.Equal(0, b.ExitCode);
        Assert.Equal("hcg-server-build-host", b.ToolchainVersions["build-provider"]);
        Assert.Equal("Swift version 6.0.3", b.ToolchainVersions["buildhost:swift"]);
        Assert.Equal(host.Fingerprint, b.ToolchainVersions["buildhost-key"]);
        var a = Assert.Single(b.Artifacts);
        Assert.Equal(PackagingFormat.BuildHostOutput, a.Format);
        Assert.Equal(host.ArtifactSha, a.Sha256);
        Assert.Equal(host.Artifact.Length, a.SizeBytes);
        Assert.Equal(VerificationStatus.Passed, a.Verification);
        Assert.Contains(a.Checks, c => c.Name == "Signed provenance" && c.Status == VerificationStatus.Passed);
        Assert.Contains(a.Checks, c => c.Name == "Build Host validation" && c.Status == VerificationStatus.Passed);
        Assert.Contains("Build complete!", File.ReadAllText(b.LogPath!)); // streamed log
        Assert.DoesNotContain(FakeBuildHost.Token, File.ReadAllText(b.LogPath!));

        // Evidence for HCG Repo: provider, toolchain, builtAt, source; never a CI run.
        var e = outcome.Evidence!;
        Assert.Equal(("F1r3srp3nt/HCG-LifeOS", FakeBuildHost.Commit, false), (e.Repository, e.Commit, e.Dirty));
        Assert.Equal(("hcg-server-build-host", "Swift version 6.0.3", "2026-11-25T12:01:00Z", "owner"), (e.Provider, e.Toolchain, e.BuiltAt, e.ApprovedBy));
        var prov = Packaging.Jailbreak.PackageProvenance.FromBuildHost(e);
        Assert.Equal(("hcg-server-build-host", null, null, null), (prov.BuildProvider, prov.CiRunId, prov.ImportRepository, prov.CiArtifact));

        // The shared native build-provider contract, as the local Xcode provider writes it.
        var dir = Path.GetDirectoryName(b.LogPath!)!;
        var request = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "native-build-request.json")))!;
        var result = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "native-build-result.json")))!;
        Assert.Empty(Packaging.Distribution.JsonSchemaLite.Validate(Schema("native-build-request-v1.schema.json"), request));
        Assert.Empty(Packaging.Distribution.JsonSchemaLite.Validate(Schema("native-build-result-v1.schema.json"), result));
        Assert.Equal("hcg-server-build-host", request["provider"]!["kind"]!.GetValue<string>());
        Assert.False(request["provider"]!["dependsOnGitHubActions"]!.GetValue<bool>());
        Assert.StartsWith("owner (the owner, on HCG Server", request["approval"]!["approvedBy"]!.GetValue<string>());
        Assert.Equal("2026-11-25T12:00:30Z", request["approval"]!["approvedAt"]!.GetValue<string>());
        Assert.Contains("no signing, no notarization, no upload, no publication", request["approval"]!["scope"]!.GetValue<string>());
        Assert.Equal((FakeBuildHost.Commit, false), (request["source"]!["commit"]!.GetValue<string>(), request["source"]!["dirty"]!.GetValue<bool>()));
        Assert.Equal(("swift-package", "unsigned", "seatbelt"), (request["project"]!["kind"]!.GetValue<string>(), request["signing"]!["mode"]!.GetValue<string>(), request["limits"]!["isolation"]!["backend"]!.GetValue<string>()));
        Assert.Equal(["build"], request["actions"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(dir, "native-build-request.json")))), result["request"]!.GetValue<string>());
        Assert.Equal(("succeeded", "swift build", 0), (result["status"]!.GetValue<string>(), result["steps"]![0]!["command"]!.GetValue<string>(), result["steps"]![0]!["exitCode"]!.GetValue<int>()));
        Assert.Equal(host.ArtifactSha, result["artifacts"]![0]!["sha256"]!.GetValue<string>());
        Assert.Null(result["artifacts"]![0]!["passport"]); // a log is not a distributable: no passport
        Assert.Equal(host.Fingerprint, result["buildHost"]!["keyFingerprint"]!.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(dir, "buildhost-provenance.json")));
        Assert.True(File.Exists(Path.Combine(dir, "buildhost-validation.json")));
    }

    [Fact]
    public async Task A_distributable_output_gets_a_Release_Passport_that_Ship_to_Store_reads()
    {
        using var host = new FakeBuildHost { AwaitingPolls = 0, ArtifactName = "LifeOSCore.zip", ArtifactKind = "archive" };
        var paths = new AppPaths(_t.Combine("app3"));
        paths.EnsureCreated();
        var outcome = await new HcgServerBuildHost(await Client(host), paths, TimeProvider.System).RunAsync(Request(paths), new Observer(), CancellationToken.None);
        Assert.Equal(BuildStatus.Succeeded, outcome.Build.Status);
        var file = outcome.Build.Artifacts.Single().Path;
        var store = BuildEngine.Distribution.StoreProvenance.ForFile(file, host.ArtifactSha, host.Artifact.Length);
        Assert.True(store.Complete, string.Join("; ", store.Notes));
        Assert.Equal(("hcg-server-build-host", FakeBuildHost.Commit), (store.BuildProvider, store.SourceCommit));
        var passport = JsonNode.Parse(File.ReadAllText(file + ".release-passport.json"))!;
        Assert.Null(passport["build"]!["run_id"]);
        var result = JsonNode.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(outcome.Build.LogPath!)!, "native-build-result.json")))!;
        Assert.Equal("LifeOSCore.zip.release-passport.json", result["artifacts"]![0]!["passport"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_rejected_job_writes_no_native_request_because_nothing_was_approved()
    {
        using var host = new RejectingHost();
        var paths = new AppPaths(_t.Combine("app4"));
        paths.EnsureCreated();
        var outcome = await new HcgServerBuildHost(await Client(host), paths, TimeProvider.System).RunAsync(Request(paths), new Observer(), CancellationToken.None);
        Assert.Equal(BuildStatus.Failed, outcome.Build.Status);
        Assert.Contains("rejected", outcome.Build.FailureReason);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(outcome.Build.LogPath!)!, "native-build-request.json")));
    }

    private sealed class RejectingHost : HttpMessageHandler
    {
        private readonly FakeBuildHost _inner = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await new HttpMessageInvoker(_inner).SendAsync(request, cancellationToken);
            if (request.RequestUri!.AbsolutePath.EndsWith(FakeBuildHost.JobId, StringComparison.Ordinal))
            {
                var job = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))!;
                job["state"] = "rejected";
                response.Content = new StringContent(job.ToJsonString(), Encoding.UTF8, "application/json");
            }
            return response;
        }
    }

    private static BuildHostBuildRequest Request(IAppPaths paths) => new()
    {
        ProjectId = Guid.NewGuid(), ProjectName = "HCG LifeOS", BuildHostProjectId = "lifeos", Commit = FakeBuildHost.Commit, Operations = ["build"],
        Options = new BuildHostOptions { Targets = ["LifeOSCore"] },
        Settings = new BuildSettings
        {
            ProjectRelativePath = ".", Platform = TargetPlatform.MacOS, Architecture = TargetArchitecture.Arm64, OutputDirectory = paths.DefaultOutputDirectory,
            Identity = new AppIdentity { ApplicationName = "HCG LifeOS", Version = "0.1.0", ApplicationId = "hcg.lifeos" },
        },
    };

    private static JsonNode Schema(string file) => JsonNode.Parse(File.ReadAllText(Path.Combine(Fixtures.RepoRoot, "docs", "contracts", file)))!;

    [Fact]
    public async Task A_tampered_provenance_or_corrupted_artifact_fails_the_build()
    {
        using var host = new FakeBuildHost { AwaitingPolls = 0, CorruptArtifactBytes = true };
        var client = await Client(host);
        var paths = new AppPaths(_t.Combine("app2"));
        paths.EnsureCreated();
        var request = new BuildHostBuildRequest
        {
            ProjectId = Guid.NewGuid(), ProjectName = "p", BuildHostProjectId = "lifeos", Commit = FakeBuildHost.Commit, Operations = ["build"],
            Settings = new BuildSettings
            {
                ProjectRelativePath = ".", Platform = TargetPlatform.Linux, Architecture = TargetArchitecture.X64, OutputDirectory = paths.DefaultOutputDirectory,
                Identity = new AppIdentity { ApplicationName = "p", Version = "1", ApplicationId = "p" },
            },
        };
        var corrupted = await new HcgServerBuildHost(client, paths, TimeProvider.System).RunAsync(request, new Observer(), CancellationToken.None);
        Assert.Equal(BuildStatus.Failed, corrupted.Build.Status);
        Assert.Empty(corrupted.Build.Artifacts);
        Assert.False(Directory.EnumerateFiles(paths.BuildsDirectory, "build.log.partial", SearchOption.AllDirectories).Any());

        using var host2 = new FakeBuildHost { AwaitingPolls = 0, TamperPayload = b => [.. b, (byte)' '] };
        var tampered = await new HcgServerBuildHost(await Client(host2), paths, TimeProvider.System).RunAsync(request, new Observer(), CancellationToken.None);
        Assert.Equal(BuildStatus.Failed, tampered.Build.Status);
        Assert.Null(tampered.Provenance);
        Assert.Null(tampered.Evidence);
        Assert.Contains("does not verify", tampered.Build.FailureReason);
    }

    [Fact]
    public async Task A_dirty_or_mismatched_build_cannot_back_a_submission()
    {
        using var host = new FakeBuildHost { DirtySource = true };
        var verified = await (await Client(host)).GetProvenanceAsync(FakeBuildHost.JobId);
        Assert.Contains("dirty", Assert.Throws<HcgBuildHostException>(() => BuildHostSubmission.FromProvenance(verified)).Message);
        host.DirtySource = false;
        verified = await (await Client(host)).GetProvenanceAsync(FakeBuildHost.JobId);
        Assert.Contains("requested", Assert.Throws<HcgBuildHostException>(() => BuildHostSubmission.FromProvenance(verified, new string('0', 40))).Message);
        Assert.Equal(FakeBuildHost.JobId, BuildHostSubmission.FromProvenance(verified, FakeBuildHost.Commit).JobId);
    }

    [Fact]
    public async Task The_Build_Host_is_an_IBuildHost_next_to_this_computer()
    {
        using var host = new FakeBuildHost();
        var buildHost = new HcgServerBuildHost(await Client(host), new AppPaths(_t.Combine("app5")), TimeProvider.System);
        IBuildHost[] hosts = [new LocalBuildHost(), buildHost];
        Assert.Equal([BuildHostKind.Local, BuildHostKind.HcgServerHost], hosts.Select(h => h.Describe().Kind).ToArray());
        Assert.Contains("not been contacted", buildHost.CannotBuild(TargetPlatform.Linux));
        await buildHost.RefreshAsync();
        Assert.Null(buildHost.CannotBuild(TargetPlatform.Linux));
        Assert.Contains("no Xcode", buildHost.CannotBuild(TargetPlatform.IOS)); // the fake has no xcode provider
    }

    [Fact]
    public async Task Provenance_whose_payload_does_not_name_the_signing_key_is_refused()
    {
        using var host = new FakeBuildHost { PayloadFingerprint = "" };
        var ex = await Assert.ThrowsAsync<HcgBuildHostException>(async () => await (await Client(host)).GetProvenanceAsync(FakeBuildHost.JobId));
        Assert.Contains("does not name its key", ex.Message);
        host.PayloadFingerprint = new string('c', 64);
        ex = await Assert.ThrowsAsync<HcgBuildHostException>(async () => await (await Client(host)).GetProvenanceAsync(FakeBuildHost.JobId));
        Assert.Contains("the payload names key", ex.Message);
    }

    private sealed class Observer : IBuildHostObserver
    {
        public readonly List<string> Approvals = [];
        public readonly List<string> Lines = [];
        public void OnAwaitingApproval(BuildJob job, string instruction) { lock (Approvals) Approvals.Add(instruction); }
        public void OnLog(BuildEngine.BuildLogLine line) { lock (Lines) Lines.Add(line.Text); }
    }
}
