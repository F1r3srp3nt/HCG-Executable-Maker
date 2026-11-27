namespace HCG.ExecutableMaker.Domain;

/// <summary>
/// Where a build ran, as HCG Repo's artifact submission names it (<c>build.provider</c> in
/// <c>docs/schemas/artifact-submission-v1.schema.json</c>). Only the GitHub providers carry a CI run.
/// </summary>
public static class BuildProviderIds
{
    /// <summary>This computer, through Executable Maker's own build worker.</summary>
    public const string ExecutableMakerLocal = "executable-maker-local";
    /// <summary>A GitHub Actions run (public repository): needs runId, artifactName and runRepository.</summary>
    public const string GitHubActions = "github-actions";
    /// <summary>A GitHub Actions run in a private repository.</summary>
    public const string GitHubActionsPrivate = "github-actions-private";
    /// <summary>HCG Server's Build Host (<c>hcg.buildhost/1</c>): owner-approved, sandboxed, unsigned. Never has a CI run.</summary>
    public const string HcgServerBuildHost = "hcg-server-build-host";

    public static bool HasCiRun(string provider) => provider is GitHubActions or GitHubActionsPrivate;
}

/// <summary>
/// What HCG Server's Build Host vouches for in its signed provenance (<c>hcg.buildhost.provenance/1</c>, the
/// <c>submission</c> block), after Executable Maker verified the signature. These become the <c>source</c> and
/// <c>build</c> fields of an HCG Repo artifact submission. There is no CI run: no run ID, artifact name or run
/// repository exists for this provider, and none may be invented.
/// </summary>
public sealed record BuildHostEvidence
{
    /// <summary>OWNER/NAME of the source repository the owner approved for the project.</summary>
    public required string Repository { get; init; }
    /// <summary>The full 40-hex commit the Build Host checked out and verified.</summary>
    public required string Commit { get; init; }
    /// <summary>The tree after checkout was clean (always false for an evidence record; dirty sources are refused).</summary>
    public bool Dirty { get; init; }
    /// <summary>The toolchain versions the Build Host recorded (e.g. "Swift version 6.0.3 (swift-6.0.3-RELEASE)").</summary>
    public required string Toolchain { get; init; }
    /// <summary>When the job ended, exactly as the provenance states it (RFC 3339).</summary>
    public required string BuiltAt { get; init; }
    /// <summary>The Build Host job (e.g. bj-20261125-…). Not a CI run.</summary>
    public required string JobId { get; init; }
    /// <summary>SHA-256 of HCG Server's device key (PKIX DER) that signed the provenance, lowercase hex.</summary>
    public required string KeyFingerprint { get; init; }
    public string? DeviceId { get; init; }
    public string? DeviceName { get; init; }
    /// <summary>Who approved the job on HCG Server (the owner).</summary>
    public string? ApprovedBy { get; init; }
    /// <summary>"unsigned": the Build Host never signs; Executable Maker signs afterwards with its own approval.</summary>
    public string SigningMethod { get; init; } = "unsigned";

    public string Provider => BuildProviderIds.HcgServerBuildHost;
}

/// <summary>
/// What a Build Host's signed provenance said about it, remembered after Executable Maker verified the signature: shown
/// with the Build Provider before the next build (operating system, architecture, isolation and the device key).
/// hcg.buildhost/1's status does not publish these, so they are known after the first verified build.
/// </summary>
public sealed record KnownBuildHost
{
    public required string BaseUrl { get; init; }
    public required string KeyFingerprint { get; init; }
    public string DeviceId { get; init; } = "";
    public string DeviceName { get; init; } = "";
    public string AgentVersion { get; init; } = "";
    public string OperatingSystem { get; init; } = "";
    public string Architecture { get; init; } = "";
    public string IsolationBackend { get; init; } = "";
    public IReadOnlyList<string> IsolationRules { get; init; } = [];
    public IReadOnlyList<string> IsolationGaps { get; init; } = [];
    public DateTimeOffset LastVerifiedAt { get; init; }
}
