namespace HCG.ExecutableMaker.Domain;

public enum SupportLevel
{
    /// <summary>HCG Executable Maker can build this with installed tooling.</summary>
    Supported,
    /// <summary>Supported in principle, but a toolchain is missing on this host.</summary>
    MissingToolchain,
    /// <summary>Recognized, but requires manual configuration before a build can run.</summary>
    RequiresConfiguration,
    /// <summary>Recognized, but must be built on a different host OS.</summary>
    RequiresDifferentHost,
    /// <summary>Recognized ecosystem with no build support in this version.</summary>
    Unsupported,
    /// <summary>Nothing recognizable was found.</summary>
    Unrecognized,
}

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

public sealed record AnalysisDiagnostic(DiagnosticSeverity Severity, string Message, string? Path = null);

public sealed record PackageDependency(string Name, string? Version);

/// <summary>A single buildable unit, e.g. a .csproj.</summary>
public sealed record DetectedProject
{
    public required string Name { get; init; }
    /// <summary>Path relative to the workspace source directory, using '/' separators.</summary>
    public required string RelativePath { get; init; }
    public required string Language { get; init; }
    public string? Sdk { get; init; }
    public IReadOnlyList<string> TargetFrameworks { get; init; } = [];
    public string? OutputType { get; init; }
    public string? AssemblyName { get; init; }
    public IReadOnlyList<string> RuntimeIdentifiers { get; init; } = [];
    public IReadOnlyList<PackageDependency> PackageReferences { get; init; } = [];
    public IReadOnlyList<string> ProjectReferences { get; init; } = [];
    public string? ApplicationIcon { get; init; }
    public string? Version { get; init; }
    public string? Company { get; init; }
    public string? Product { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string> EntryPointFiles { get; init; } = [];
    public required string ApplicationType { get; init; }
    public bool IsAvalonia { get; init; }
    public bool IsRunnableApplication { get; init; }
    /// <summary>Platforms this project can produce output for, per its target frameworks and UI stack.</summary>
    public IReadOnlyList<TargetPlatform> CompatiblePlatforms { get; init; } = [];
    public IReadOnlyList<AnalysisDiagnostic> Diagnostics { get; init; } = [];
}

public sealed record ToolchainRequirement(string ToolchainId, string DisplayName, string? MinimumVersion, bool IsSatisfied, string? Detail);

/// <summary>Result of inspecting an imported project. Produced without executing any imported code.</summary>
public sealed record ProjectAnalysis
{
    public required string InspectorId { get; init; }
    public required string ProjectName { get; init; }
    public required string Language { get; init; }
    public required string Framework { get; init; }
    public string? Runtime { get; init; }
    public required string ApplicationType { get; init; }
    public required SupportLevel Support { get; init; }
    public required string SupportSummary { get; init; }
    public IReadOnlyList<string> SolutionFiles { get; init; } = [];
    public IReadOnlyList<DetectedProject> Projects { get; init; } = [];
    /// <summary>Relative path of the recommended project to build (null when nothing is buildable).</summary>
    public string? PrimaryProjectPath { get; init; }
    public string? GlobalJsonSdkVersion { get; init; }
    public string? GlobalJsonRollForward { get; init; }
    public IReadOnlyList<ToolchainRequirement> RequiredToolchains { get; init; } = [];
    public IReadOnlyList<string> MissingDependencies { get; init; } = [];
    public IReadOnlyList<PackagingFormat> RecommendedFormats { get; init; } = [];
    public IReadOnlyList<string> DetectedIcons { get; init; } = [];
    public IReadOnlyList<AnalysisDiagnostic> Diagnostics { get; init; } = [];
    public required DateTimeOffset AnalyzedAt { get; init; }
}
