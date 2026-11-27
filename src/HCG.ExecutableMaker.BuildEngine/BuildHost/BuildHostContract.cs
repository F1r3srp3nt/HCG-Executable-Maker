using System.Text.Json;
using System.Text.Json.Serialization;

namespace HCG.ExecutableMaker.BuildEngine.BuildHost;

// HCG Server's Build Host client API, contract hcg.buildhost/1 (HCG-Server docs/BUILD-HOST.md,
// contracts/v1/buildhost.go). These records mirror the Go types field by field; JSON names are camelCase.

public static class BuildHostContract
{
    public const string Name = "hcg.buildhost";
    public const int MajorVersion = 1;
    public const string Contract = "hcg.buildhost/1";
    public const string ContractHeader = "X-HCG-Contract";
    public const string Sha256Header = "X-HCG-SHA256";
    public const string NextOffsetHeader = "X-HCG-Next-Offset";

    public const string ProvenanceFormat = "hcg.buildhost.provenance/1";
    public const string ValidationFormat = "hcg.buildhost.validation/1";
    public const string ProvenancePayloadType = "application/vnd.hcg.buildhost.provenance+json";
    public const string SignatureAlgorithm = "ES256-ASN1";
    /// <summary>HCG Repo's artifact-submission provider id for Build Host builds.</summary>
    public const string SubmissionProvider = "hcg-server-build-host";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

public static class BuildOperations
{
    public const string Resolve = "resolve";
    public const string Build = "build";
    public const string Test = "test";
    public const string Package = "package";
}

public static class BuildPermissions
{
    public const string Network = "network.dependencies";
    public const string Simulator = "simulator";
    /// <summary>Refused by the Build Host: signing stays in Executable Maker.</summary>
    public const string Signing = "signing";
    /// <summary>Refused by the Build Host: physical devices are not available.</summary>
    public const string Device = "device";
}

public static class BuildJobStates
{
    public const string AwaitingApproval = "awaiting-approval";
    public const string Rejected = "rejected";
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    public static bool IsTerminal(string state) => state is Succeeded or Failed or Cancelled or Rejected;
}

public sealed record BuildSource
{
    public string Repository { get; init; } = "";
    public string Remote { get; init; } = "";
}

public sealed record BuildProject
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? ProductId { get; init; }
    public BuildSource Source { get; init; } = new();
    public string Provider { get; init; } = "";
    public string? Path { get; init; }
    public IReadOnlyList<string> Operations { get; init; } = [];
    public DateTimeOffset RegisteredAt { get; init; }
}

public sealed record BuildHostOptions
{
    public string? Configuration { get; init; }
    public IReadOnlyList<string>? Products { get; init; }
    /// <summary>SwiftPM library targets (built with --target; packaged as module + objects).</summary>
    public IReadOnlyList<string>? Targets { get; init; }
    public string? TestFilter { get; init; }
    public string? Project { get; init; }
    public string? Scheme { get; init; }
    public string? Destination { get; init; }
    public string? Requirements { get; init; }
}

public sealed record BuildJobRequest
{
    public required string ProjectId { get; init; }
    /// <summary>Full 40-hex commit (pinned).</summary>
    public required string Commit { get; init; }
    public required IReadOnlyList<string> Operations { get; init; }
    public BuildHostOptions Options { get; init; } = new();
    public IReadOnlyList<string>? Permissions { get; init; }
    /// <summary>Shown to the owner at approval.</summary>
    public string? Purpose { get; init; }
    /// <summary>Set by the server from the client's token; never sent by the client.</summary>
    public string? Client { get; init; }
    /// <summary>Executable Maker's own build id (BuildRecord.Id).</summary>
    public string? ClientRef { get; init; }
}

public sealed record BuildHostStep
{
    public string Operation { get; init; } = "";
    public IReadOnlyList<string> Argv { get; init; } = [];
    public bool Network { get; init; }
    public int? ExitCode { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public long DurationMs { get; init; }
    public ulong PeakRssBytes { get; init; }
    public double CpuSeconds { get; init; }
}

public sealed record BuildToolchain
{
    public string Provider { get; init; } = "";
    public string Tool { get; init; } = "";
    public string Path { get; init; } = "";
    public string Version { get; init; } = "";
}

public sealed record BuildArtifact
{
    public string Name { get; init; } = "";
    /// <summary>executable | library | archive | package | test-report | log</summary>
    public string Kind { get; init; } = "";
    public long Size { get; init; }
    public string Sha256 { get; init; } = "";
}

public sealed record BuildTestResult
{
    public string Source { get; init; } = "";
    public int Passed { get; init; }
    public int Failed { get; init; }
    public int Skipped { get; init; }
}

public sealed record BuildJob
{
    public string Id { get; init; } = "";
    public BuildJobRequest? Request { get; init; }
    public BuildProject Project { get; init; } = new();
    public string State { get; init; } = "";
    public string? Detail { get; init; }
    public IReadOnlyList<BuildHostStep> Plan { get; init; } = [];
    public IReadOnlyList<BuildToolchain> Toolchains { get; init; } = [];
    public DateTimeOffset SubmittedAt { get; init; }
    public DateTimeOffset? DecidedAt { get; init; }
    public string? DecidedBy { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public BuildTestResult? Tests { get; init; }
    public IReadOnlyList<BuildArtifact> Artifacts { get; init; } = [];
    public ulong PeakRssBytes { get; init; }

    public bool IsTerminal => BuildJobStates.IsTerminal(State);
}

public sealed record BuildProvenanceHost
{
    public string DeviceId { get; init; } = "";
    public string Name { get; init; } = "";
    public string KeyFingerprint { get; init; } = "";
    public string AgentVersion { get; init; } = "";
    public string Os { get; init; } = "";
    public string Architecture { get; init; } = "";
}

public sealed record BuildProvenanceSource
{
    public string Repository { get; init; } = "";
    public string Remote { get; init; } = "";
    public string Commit { get; init; } = "";
    public string Tree { get; init; } = "";
    public bool Dirty { get; init; }
}

public sealed record BuildProvenanceJob
{
    public string Id { get; init; } = "";
    public string Client { get; init; } = "";
    public string? ClientRef { get; init; }
    public IReadOnlyList<string> Operations { get; init; } = [];
    public IReadOnlyList<string> Permissions { get; init; } = [];
    public string ApprovedBy { get; init; } = "";
    public DateTimeOffset? ApprovedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public string State { get; init; } = "";
}

public sealed record BuildProvenanceIsolation
{
    public string Backend { get; init; } = "";
    public IReadOnlyList<string> Rules { get; init; } = [];
    public IReadOnlyList<string>? Gaps { get; init; }
}

/// <summary>The <c>submission</c> block: what HCG Repo's artifact submission may take from the Build Host.</summary>
public sealed record BuildSubmissionBlock
{
    public SubmissionSource Source { get; init; } = new();
    public SubmissionBuild Build { get; init; } = new();
    public SubmissionSigning Signing { get; init; } = new();

    public sealed record SubmissionSource
    {
        public string Repository { get; init; } = "";
        public string Commit { get; init; } = "";
        public bool Dirty { get; init; }
    }

    public sealed record SubmissionBuild
    {
        public string Provider { get; init; } = "";
        public string Toolchain { get; init; } = "";
        public string BuiltAt { get; init; } = "";
    }

    public sealed record SubmissionSigning
    {
        public string Method { get; init; } = "";
    }
}

public sealed record BuildProvenance
{
    public string Format { get; init; } = "";
    public BuildProvenanceHost BuildHost { get; init; } = new();
    public BuildProject Project { get; init; } = new();
    public BuildProvenanceSource Source { get; init; } = new();
    public BuildProvenanceJob Job { get; init; } = new();
    public IReadOnlyList<BuildToolchain> Toolchains { get; init; } = [];
    public BuildProvenanceIsolation Isolation { get; init; } = new();
    public IReadOnlyList<BuildHostStep> Steps { get; init; } = [];
    public BuildTestResult? Tests { get; init; }
    public IReadOnlyList<BuildArtifact> Artifacts { get; init; } = [];
    public BuildSubmissionBlock Submission { get; init; } = new();
    public DateTimeOffset IssuedAt { get; init; }
}

/// <summary>A document signed with HCG Server's device key. <see cref="Payload"/> is the exact signed bytes (base64 in JSON).</summary>
public sealed record SignedDocument
{
    public string PayloadType { get; init; } = "";
    public byte[] Payload { get; init; } = [];
    public string Algorithm { get; init; } = "";
    public string KeyFingerprint { get; init; } = "";
    /// <summary>PKIX (SubjectPublicKeyInfo) DER, base64.</summary>
    public string PublicKey { get; init; } = "";
    /// <summary>ASN.1 DER ECDSA signature over SHA-256(Payload), base64.</summary>
    public string Signature { get; init; } = "";
}

public sealed record BuildValidationCheck
{
    public string Name { get; init; } = "";
    /// <summary>passed | failed | not-run | not-applicable</summary>
    public string Status { get; init; } = "";
    public string Detail { get; init; } = "";
}

public sealed record BuildValidation
{
    public string Format { get; init; } = "";
    public string JobId { get; init; } = "";
    /// <summary>passed | failed</summary>
    public string Overall { get; init; } = "";
    public IReadOnlyList<BuildValidationCheck> Checks { get; init; } = [];
}

public sealed record BuildProviderInfo
{
    public string Id { get; init; } = "";
    public bool Available { get; init; }
    public string Detail { get; init; } = "";
    public IReadOnlyList<string>? Operations { get; init; }
}

public sealed record BuildHostStatus
{
    public string Contract { get; init; } = "";
    public string Isolation { get; init; } = "";
    public bool Enforced { get; init; }
    public IReadOnlyList<BuildToolchain> Toolchains { get; init; } = [];
    public IReadOnlyList<BuildProviderInfo> Providers { get; init; } = [];
    [JsonPropertyName("queued")] public int Queued { get; init; }
    public string? Running { get; init; }
}

/// <summary>The Build Host's error envelope (<c>{"error": "…", "code": "…"}</c>).</summary>
internal sealed record BuildHostError
{
    public string? Error { get; init; }
    public string? Code { get; init; }
}
