namespace HCG.ExecutableMaker.Domain;

/// <summary>What a Build Doctor finding is about. Used for grouping and for the readiness summary.</summary>
public enum DoctorCategory
{
    ImportScope,
    ProjectReferences,
    MsBuildProperties,
    Imports,
    SourceFiles,
    Sdk,
    TargetFramework,
    Workload,
    Xcode,
    Packages,
    RuntimeIdentifier,
    BundleIdentifier,
    Signing,
    Provisioning,
    Configuration,
    Architecture,
    Host,
    BuildInput,
}

public enum DoctorSeverity
{
    /// <summary>The selected build cannot succeed until this is resolved.</summary>
    Error,
    /// <summary>The build may succeed, but the result or a later stage is affected.</summary>
    Warning,
    /// <summary>A fact worth knowing; nothing to fix.</summary>
    Info,
}

/// <summary>How a repair is carried out. Nothing that changes files, installs software or runs project code is automatic.</summary>
public enum RepairMode
{
    /// <summary>Changes nothing outside HCG's own state (re-evaluate, re-detect): runs without asking.</summary>
    Automatic,
    /// <summary>Changes project files, imports, installs or runs project code: shown first, then applied only after approval.</summary>
    RequiresApproval,
    /// <summary>Only the user can do it (outside HCG, or an owner decision).</summary>
    Manual,
}

public enum RepairKind
{
    /// <summary>Re-import the repository root that contains the app and its shared projects (preserves the layout).</summary>
    ImportRepository,
    /// <summary>Edit project files (a broken relative reference with exactly one possible target).</summary>
    EditFiles,
    /// <summary>dotnet restore of the app project (runs the project's restore targets).</summary>
    RestorePackages,
    /// <summary>Install the .NET workload the project needs (dotnet workload install …).</summary>
    InstallWorkload,
    /// <summary>Re-run toolchain detection and Build Doctor.</summary>
    Recheck,
    /// <summary>Evaluate the projects with MSBuild itself (dotnet msbuild -getProperty/-getItem; no targets run).</summary>
    EvaluateWithMsBuild,
    /// <summary>Open Signing &amp; Security to choose or add a certificate/profile.</summary>
    ConfigureSigning,
    /// <summary>Instructions only.</summary>
    Instructions,
}

public enum DoctorVerification
{
    NotRun,
    Passed,
    Failed,
    NotVerifiable,
}

/// <summary>One file a repair would change, relative to the workspace source folder.</summary>
public sealed record DoctorFileChange(string Path, string Before, string After);

public sealed record DoctorRepair(
    string Id,
    RepairKind Kind,
    RepairMode Mode,
    string Title,
    string Description,
    string? Command = null,
    IReadOnlyList<DoctorFileChange>? Changes = null,
    /// <summary>For <see cref="RepairKind.ImportRepository"/>: the folder to import.</summary>
    string? Target = null);

public sealed record DoctorFinding(
    string Id,
    DoctorCategory Category,
    DoctorSeverity Severity,
    string Problem,
    string? RootCause,
    string? AffectedFile,
    string Recommendation,
    DoctorRepair? Repair = null,
    /// <summary>Technical details (evaluated values, raw MSBuild text, tool output). Never contains secret values.</summary>
    string? Technical = null)
{
    public DoctorVerification Verification { get; init; } = DoctorVerification.NotRun;
    public string? VerificationDetail { get; init; }
}

/// <summary>The result of one Build Doctor run for one project and output.</summary>
public sealed record DoctorReport(
    string Subject,
    MobileOutput Output,
    DateTimeOffset CheckedAt,
    IReadOnlyList<DoctorFinding> Findings,
    /// <summary>What was checked and found in order (evaluated values, resolved references): the audit trail.</summary>
    IReadOnlyList<string> Facts)
{
    public int Errors => Findings.Count(f => f.Severity == DoctorSeverity.Error);
    public int Warnings => Findings.Count(f => f.Severity == DoctorSeverity.Warning);
    public bool ReadyToBuild => Errors == 0;
    public IEnumerable<DoctorRepair> Repairs => Findings.Select(f => f.Repair).OfType<DoctorRepair>().DistinctBy(r => r.Id);
}

/// <summary>A repair that was applied: what changed and where the backup is, so it can be rolled back.</summary>
public sealed record DoctorRepairRecord(
    string RepairId,
    string Title,
    DateTimeOffset AppliedAt,
    string? BackupFolder,
    IReadOnlyList<string> ChangedFiles,
    bool Succeeded,
    string Outcome);
