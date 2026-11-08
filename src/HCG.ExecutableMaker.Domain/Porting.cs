namespace HCG.ExecutableMaker.Domain;

/// <summary>Source-based cross-platform conversions HCG can assess. Each path has an explicit, documented scope.</summary>
public enum PortingPath
{
    /// <summary>macOS SwiftUI source → iOS (iPhone/iPad) Xcode project.</summary>
    MacOSToIOS,
    /// <summary>Linux C/C++ (CMake, POSIX) program → Android app (NDK + Gradle).</summary>
    LinuxToAndroid,
}

/// <summary>What HCG can do for one source on one path. These are deliberately distinct.</summary>
public enum PortingCapability
{
    /// <summary>The project already has a target for the destination platform: build it (Mobile Build Studio).</summary>
    BuildExistingTarget,
    /// <summary>HCG can generate a new destination target; every change is a supported, reviewable adaptation.</summary>
    GenerateTarget,
    /// <summary>The source is in scope, but specific code needs manual porting first (see the blockers).</summary>
    BlockedNeedsPorting,
    /// <summary>The source is outside this path's supported languages/frameworks.</summary>
    NotSupported,
}

/// <summary>How a finding is handled by the generator.</summary>
public enum FindingDisposition
{
    /// <summary>Works unchanged on the destination platform.</summary>
    Compatible,
    /// <summary>Changed by a supported adaptation; the change is shown in the diff.</summary>
    AutoAdapted,
    /// <summary>Generated, but behaviour differs or needs a decision: review before shipping.</summary>
    NeedsReview,
    /// <summary>No supported adaptation: manual porting is required. Never replaced by a stub.</summary>
    Blocker,
}

public enum FindingCategory
{
    PlatformApi,
    UiAdaptation,
    Lifecycle,
    Dependency,
    BuildSystem,
    FileSystem,
    Permission,
}

public sealed record PortingFinding
{
    public required FindingCategory Category { get; init; }
    public required FindingDisposition Disposition { get; init; }
    public required string Title { get; init; }
    public required string Detail { get; init; }
    /// <summary>Workspace-relative file ('/' separated), if the finding is tied to source.</summary>
    public string? File { get; init; }
    public int? Line { get; init; }
    public string? Evidence { get; init; }
    /// <summary>What to do (for blockers and review items).</summary>
    public string? Guidance { get; init; }
    public string Location => File is null ? "" : Line is { } l ? $"{File}:{l}" : File;
}

/// <summary>A unit of code and how much of it carries over unchanged.</summary>
public sealed record ReusableUnit(string Name, string Kind, int Files, int Lines, int FilesWithBlockers, int FilesAdapted)
{
    public int FilesUnchanged => Files - FilesWithBlockers - FilesAdapted;
}

/// <summary>A convertible thing found in a workspace, e.g. a Swift package app target or a CMake executable.</summary>
public sealed record PortSource
{
    public required PortingPath Path { get; init; }
    /// <summary>Stable key within the workspace, e.g. "swiftpm:relay:RelayApp" or "cmake:primes".</summary>
    public required string Key { get; init; }
    public required string Kind { get; init; }
    public required string DisplayName { get; init; }
    /// <summary>Workspace-relative directory of the project (package, .xcodeproj parent or CMake root).</summary>
    public required string Directory { get; init; }
    /// <summary>Target/product/project file this source refers to.</summary>
    public required string Target { get; init; }
}

public sealed record PortingAssessment
{
    public required PortingPath Path { get; init; }
    public required PortSource Source { get; init; }
    public required PortingCapability Capability { get; init; }
    public required string Summary { get; init; }
    public IReadOnlyList<ReusableUnit> Reusable { get; init; } = [];
    public IReadOnlyList<PortingFinding> Findings { get; init; } = [];
    public IReadOnlyList<ToolchainRequirement> Toolchains { get; init; } = [];
    public IReadOnlyList<string> Signing { get; init; } = [];
    /// <summary>What this path supports in this version (shown so the scope is never implied to be universal).</summary>
    public IReadOnlyList<string> Scope { get; init; } = [];
    /// <summary>Inputs the generator needs from the user, e.g. NSMicrophoneUsageDescription.</summary>
    public IReadOnlyList<string> RequiredTexts { get; init; } = [];
    public DateTimeOffset AssessedAt { get; init; }
    /// <summary>Prefill for the port options (from the source project where available).</summary>
    public string? SuggestedAppName { get; init; }
    public string? SuggestedAppId { get; init; }
    public IEnumerable<PortingFinding> Blockers => Findings.Where(f => f.Disposition == FindingDisposition.Blocker);
}

/// <summary>User choices for generation.</summary>
public sealed record PortOptions
{
    public required string AppName { get; init; }
    /// <summary>iOS bundle identifier or Android applicationId.</summary>
    public required string AppId { get; init; }
    public string Version { get; init; } = "1.0.0";
    public int BuildNumber { get; init; } = 1;
    /// <summary>Info.plist privacy keys (iOS) → user-facing text.</summary>
    public IReadOnlyDictionary<string, string> UsageDescriptions { get; init; } = new Dictionary<string, string>();
    /// <summary>Default command-line arguments for console programs (Android).</summary>
    public string DefaultArguments { get; init; } = "";
    /// <summary>Android NDK / SDK CMake versions to pin (from detection), so Gradle never downloads SDK components itself.</summary>
    public string? NdkVersion { get; init; }
    public string? CmakeVersion { get; init; }
    /// <summary>
    /// Generate even though blockers exist. Each blocker site gets an explicit compile-time error marker, so the draft
    /// cannot be mistaken for a working port (and never silently stubs missing functionality).
    /// </summary>
    public bool Draft { get; init; }
}

public enum FileChangeKind
{
    Added,
    Modified,
    CopiedUnchanged,
}

public sealed record PlannedFileChange(string Path, FileChangeKind Kind, string Reason, string Diff);

public sealed record MigrationPlan
{
    public required IReadOnlyList<string> Steps { get; init; }
    public required IReadOnlyList<PlannedFileChange> Changes { get; init; }
    public bool IsDraft { get; init; }
    public int Added => Changes.Count(c => c.Kind == FileChangeKind.Added);
    public int Modified => Changes.Count(c => c.Kind == FileChangeKind.Modified);
    public int Unchanged => Changes.Count(c => c.Kind == FileChangeKind.CopiedUnchanged);
}

/// <summary>The four levels are reported separately: one never implies another.</summary>
public enum VerificationLevel
{
    Generated,
    Compiled,
    LaunchedInSimulatorOrEmulator,
    TestedOnDevice,
}

public enum LevelStatus
{
    Passed,
    Failed,
    NotPerformed,
    /// <summary>Cannot be done by HCG here (e.g. iOS compile on Windows, device testing).</summary>
    NotPossibleHere,
}

public sealed record VerificationEntry(VerificationLevel Level, LevelStatus Status, string Detail, DateTimeOffset? At = null, string? Evidence = null);

/// <summary>One generated port: written as HCG-PORT/report.json in the output folder (the original is never modified).</summary>
public sealed record PortReport
{
    public required Guid Id { get; init; }
    public required string SourceProject { get; init; }
    public required string SourceDirectory { get; init; }
    public required string OutputDirectory { get; init; }
    /// <summary>Folder (inside <see cref="OutputDirectory"/>) to build: the generated Xcode project's folder or Gradle root.</summary>
    public required string BuildRoot { get; init; }
    public required PortingAssessment Assessment { get; init; }
    public required MigrationPlan Plan { get; init; }
    public required PortOptions Options { get; init; }
    public required IReadOnlyList<VerificationEntry> Verification { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    public VerificationEntry Level(VerificationLevel level) =>
        Verification.LastOrDefault(v => v.Level == level) ?? new VerificationEntry(level, LevelStatus.NotPerformed, "Not performed.");

    public PortReport With(VerificationEntry entry) => this with { Verification = [.. Verification.Where(v => v.Level != entry.Level), entry] };
}
