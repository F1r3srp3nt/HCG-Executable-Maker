namespace HCG.ExecutableMaker.Domain;

/// <summary>Source-based cross-platform conversions HCG can assess. Each path has an explicit, documented scope.</summary>
public enum PortingPath
{
    /// <summary>macOS SwiftUI source → iOS (iPhone/iPad) Xcode project.</summary>
    MacOSToIOS,
    /// <summary>Linux C/C++ (CMake, POSIX) program → Android app (NDK + Gradle).</summary>
    LinuxToAndroid,
    /// <summary>Vite + React web app → Android app (web-based mobile packaging with Capacitor).</summary>
    WebToAndroid,
    /// <summary>Vite + React web app → iPhone/iPad app (web-based mobile packaging with Capacitor).</summary>
    WebToIOS,
}

/// <summary>Mobile platform a conversion produces. "iOS" means iPhone and iPad (iPadOS).</summary>
public enum MobileTarget
{
    Ios,
    Android,
}

/// <summary>How a desktop project becomes a mobile one.</summary>
public enum ConversionMode
{
    /// <summary>Reuse supported application logic; adapt navigation, layouts, input and platform integrations.</summary>
    MobileAdaptation,
    /// <summary>Package a compatible web app in an established mobile runtime (Capacitor); native integrations handled explicitly.</summary>
    WebMobilePackaging,
    /// <summary>A mobile interface that talks to the desktop service (which keeps running the workload) through a documented API.</summary>
    MobileCompanion,
}

/// <summary>The four classes every inspected feature falls into.</summary>
public enum FeatureClass
{
    Reusable,
    RequiresAdaptation,
    RequiresBackendOrCompanion,
    Unsupported,
}

/// <summary>Whether HCG implements a source framework × target × mode combination in this version.</summary>
public enum SupportStatus
{
    /// <summary>Implemented: assess, generate, validate and build (each level still reported separately).</summary>
    Implemented,
    /// <summary>Planned: shown with its precise requirements; nothing is generated.</summary>
    Planned,
    /// <summary>No automated conversion is planned (e.g. the UI must be rewritten); the reason is shown.</summary>
    NotPlanned,
}

/// <summary>One row of the support matrix.</summary>
public sealed record SupportRow(string SourceFramework, string Languages, MobileTarget Target, ConversionMode Mode, SupportStatus Status,
    PortingPath? Path, string Summary, IReadOnlyList<string> Requirements);

public static class PortingPaths
{
    public static MobileTarget Target(this PortingPath path) => path is PortingPath.MacOSToIOS or PortingPath.WebToIOS ? MobileTarget.Ios : MobileTarget.Android;

    public static ConversionMode Mode(this PortingPath path) => path is PortingPath.WebToAndroid or PortingPath.WebToIOS ? ConversionMode.WebMobilePackaging : ConversionMode.MobileAdaptation;

    public static string DisplayName(this MobileTarget target) => target == MobileTarget.Ios ? "iOS / iPadOS" : "Android";

    public static string DisplayName(this ConversionMode mode) => mode switch
    {
        ConversionMode.MobileAdaptation => "Mobile adaptation",
        ConversionMode.WebMobilePackaging => "Web-based mobile packaging",
        _ => "Mobile companion",
    };

    public static string DisplayName(this PortingPath path) => path switch
    {
        PortingPath.MacOSToIOS => "macOS SwiftUI → iOS / iPadOS",
        PortingPath.LinuxToAndroid => "Linux C/C++ program → Android",
        PortingPath.WebToAndroid => "Vite + React web app → Android (Capacitor)",
        _ => "Vite + React web app → iOS / iPadOS (Capacitor)",
    };

    /// <summary>Short folder suffix for the output, e.g. "android-port".</summary>
    public static string FolderSuffix(this PortingPath path) => path switch
    {
        PortingPath.MacOSToIOS => "ios-port",
        PortingPath.LinuxToAndroid => "android-port",
        PortingPath.WebToAndroid => "android-mobile",
        _ => "ios-mobile",
    };

    public static FeatureClass Class(this FindingDisposition d) => d switch
    {
        FindingDisposition.Compatible => FeatureClass.Reusable,
        FindingDisposition.AutoAdapted or FindingDisposition.NeedsReview => FeatureClass.RequiresAdaptation,
        FindingDisposition.RequiresCompanion => FeatureClass.RequiresBackendOrCompanion,
        _ => FeatureClass.Unsupported,
    };

    public static string DisplayName(this FeatureClass c) => c switch
    {
        FeatureClass.Reusable => "Reusable",
        FeatureClass.RequiresAdaptation => "Requires adaptation",
        FeatureClass.RequiresBackendOrCompanion => "Requires a backend or companion computer",
        _ => "Unsupported by the current converter",
    };
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
    /// <summary>
    /// Works only with a server or a companion computer (an API the app calls, a desktop service, programs the phone
    /// cannot run). The generated app keeps the calls; the server side is not generated or faked.
    /// </summary>
    RequiresCompanion,
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
    public FeatureClass Class => Disposition.Class();
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
    public MobileTarget Target => Path.Target();
    public ConversionMode Mode => Path.Mode();
    /// <summary>Exact versions the generated project pins (e.g. Capacitor), recorded for reproducibility.</summary>
    public IReadOnlyDictionary<string, string> Pins { get; init; } = new Dictionary<string, string>();
    /// <summary>Steps only a person can do (accounts, signing, provider registrations, ...), listed in the migration report.</summary>
    public IReadOnlyList<string> ManualSteps { get; init; } = [];
}

/// <summary>
/// What exactly was converted, so a result can be reproduced: the original's version-control revision (when it is a
/// Git working copy) and a SHA-256 fingerprint of the converted files.
/// </summary>
public sealed record SourceRevision
{
    /// <summary>"git" when the original location is a Git working copy; null otherwise.</summary>
    public string? Vcs { get; init; }
    public string? Commit { get; init; }
    public string? Branch { get; init; }
    /// <summary>SHA-256 over the sorted (path, file SHA-256) list of the files that were converted.</summary>
    public required string ContentSha256 { get; init; }
    public required int FileCount { get; init; }
    public required DateTimeOffset CapturedAt { get; init; }
    /// <summary>The folder HCG converted (its imported copy of the project); fingerprinted by <see cref="ContentSha256"/>.</summary>
    public string? ConvertedDirectory { get; init; }
    /// <summary>The user's original folder and its fingerprint at conversion time (null for ZIP imports).</summary>
    public string? OriginalDirectory { get; init; }
    public string? OriginalSha256 { get; init; }
    public string Describe() => (Commit is null ? "no Git revision" : $"git {Commit[..Math.Min(12, Commit.Length)]}{(Branch is null ? "" : $" ({Branch})")}") +
                                $", content sha256 {ContentSha256[..12]}… over {FileCount} file(s)";
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
    /// <summary>
    /// The original source is unchanged and the generated project passes its checks; for web packaging also the web
    /// build and the native project created from Capacitor's template. Not a mobile build.
    /// </summary>
    Validated,
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

/// <summary>One check of the Validate step.</summary>
public sealed record ValidationCheck(string Name, bool Passed, string Detail);

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
    /// <summary>The source that was converted (null for reports written before this was recorded).</summary>
    public SourceRevision? Source { get; init; }
    /// <summary>HCG Executable Maker version that generated the project.</summary>
    public string? GeneratorVersion { get; init; }
    /// <summary>The checks of the last Validate step.</summary>
    public IReadOnlyList<ValidationCheck> ValidationChecks { get; init; } = [];

    public VerificationEntry Level(VerificationLevel level) =>
        Verification.LastOrDefault(v => v.Level == level) ?? new VerificationEntry(level, LevelStatus.NotPerformed, "Not performed.");

    public PortReport With(VerificationEntry entry) => this with { Verification = [.. Verification.Where(v => v.Level != entry.Level), entry] };
}

/// <summary>A project (or part of one) found in an imported workspace, whether or not a converter supports it.</summary>
public sealed record DetectedSource
{
    /// <summary>Stable key, e.g. "web:" or "dotnet:App/App.csproj"; equals <see cref="PortSource.Key"/> for convertible sources.</summary>
    public required string Key { get; init; }
    public required string Framework { get; init; }
    public required string Language { get; init; }
    /// <summary>Workspace-relative directory ("" for the root).</summary>
    public required string Directory { get; init; }
    public IReadOnlyList<string> EntryPoints { get; init; } = [];
    public IReadOnlyList<string> Dependencies { get; init; } = [];
    /// <summary>Platform-specific functionality found (file system, windows, processes, sign-in, ...).</summary>
    public IReadOnlyList<string> PlatformFunctionality { get; init; } = [];
    /// <summary>Why it was recognized this way.</summary>
    public string Evidence { get; init; } = "";
}

/// <summary>One inspected feature, classified, with a plain-language explanation.</summary>
public sealed record CompatibilityFeature(string SourceKey, FeatureClass Class, string Area, string Title, string Explanation,
    string? Location = null, string? Guidance = null);

/// <summary>What converting one source to one target means today: the mode, whether it is implemented, and what it needs.</summary>
public sealed record TargetOption
{
    public required string SourceKey { get; init; }
    public required MobileTarget Target { get; init; }
    public required ConversionMode Mode { get; init; }
    public required SupportStatus Status { get; init; }
    /// <summary>The implemented path to use (null when planned or not planned).</summary>
    public PortingPath? Path { get; init; }
    /// <summary>For implemented paths: the assessment's capability for this source.</summary>
    public PortingCapability? Capability { get; init; }
    public required string Summary { get; init; }
    public IReadOnlyList<string> Requirements { get; init; } = [];
    public bool CanGenerate => Status == SupportStatus.Implemented && Capability is PortingCapability.GenerateTarget or PortingCapability.BlockedNeedsPorting;
}

/// <summary>
/// The compatibility report for an imported project: what was found, how each feature classifies, and which target and
/// mode combinations exist. Inspection is read-only. A successful inspection is not a conversion.
/// </summary>
public sealed record CompatibilityReport
{
    public required IReadOnlyList<DetectedSource> Sources { get; init; }
    public required IReadOnlyList<CompatibilityFeature> Features { get; init; }
    public required IReadOnlyList<TargetOption> Options { get; init; }
    /// <summary>Plain-language blockers for the whole project (e.g. "only compiled executables were imported").</summary>
    public IReadOnlyList<string> Blockers { get; init; } = [];
    /// <summary>True when the import contains compiled programs but no source code HCG can convert.</summary>
    public bool ExecutableOnly { get; init; }
    public IReadOnlyList<string> Executables { get; init; } = [];
    public SourceRevision? Revision { get; init; }
    public required DateTimeOffset InspectedAt { get; init; }
    public int Count(FeatureClass c) => Features.Count(f => f.Class == c);
}
