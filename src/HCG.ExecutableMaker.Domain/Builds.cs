namespace HCG.ExecutableMaker.Domain;

public enum BuildStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    TimedOut,
}

/// <summary>Identity of the application being produced. Independent from HCG Executable Maker's own branding.</summary>
public sealed record AppIdentity
{
    public required string ApplicationName { get; init; }
    public string Publisher { get; init; } = "";
    public required string Version { get; init; }
    public string Description { get; init; } = "";
    /// <summary>Reverse-DNS identifier, e.g. <c>com.example.myapp</c>.</summary>
    public required string ApplicationId { get; init; }
    /// <summary>User-selected icon source (PNG/ICO/ICNS). Never modified.</summary>
    public string? IconSourcePath { get; init; }
}

/// <summary>Everything needed to reproduce a build. Persisted as JSON with each build record.</summary>
public sealed record BuildSettings
{
    /// <summary>Project file relative to the workspace source directory.</summary>
    public required string ProjectRelativePath { get; init; }
    public required TargetPlatform Platform { get; init; }
    public required TargetArchitecture Architecture { get; init; }
    public BuildConfigurationKind Configuration { get; init; } = BuildConfigurationKind.Release;
    public bool SelfContained { get; init; } = true;
    public bool SingleFile { get; init; }
    public bool StampVersion { get; init; } = true;
    public required string OutputDirectory { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(30);
    public required AppIdentity Identity { get; init; }
    public IReadOnlyList<PackagingFormat> PackagingFormats { get; init; } = [PackagingFormat.PortableFolder];
    /// <summary>Set for Mobile Build Studio builds; null for desktop builds.</summary>
    public MobileBuildOptions? Mobile { get; init; }

    public string? RuntimeIdentifier => PlatformNames.ToRuntimeIdentifier(Platform, Architecture);
}

public sealed record BuildMessage(DiagnosticSeverity Severity, string Text, string? Code = null, string? File = null, int? Line = null);

public sealed class BuildRecord
{
    public required Guid Id { get; init; }
    public required Guid ProjectId { get; init; }
    public string ProjectName { get; set; } = "";
    public required BuildSettings Settings { get; init; }
    public BuildStatus Status { get; set; } = BuildStatus.Pending;
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; set; }
    public int? ExitCode { get; set; }
    public string? FailureReason { get; set; }
    public string? LogPath { get; set; }
    public int WarningCount { get; set; }
    public int ErrorCount { get; set; }
    public bool TrustAcknowledged { get; set; }
    public IDictionary<string, string> ToolchainVersions { get; init; } = new Dictionary<string, string>();
    public List<BuildMessage> Messages { get; init; } = [];
    public List<ArtifactRecord> Artifacts { get; init; } = [];

    public TimeSpan? Duration => EndedAt is { } end ? end - StartedAt : null;
}
