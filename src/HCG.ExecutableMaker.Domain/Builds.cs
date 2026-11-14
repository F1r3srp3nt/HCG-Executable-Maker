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
    /// <summary>Options for the Windows Setup.exe installer (used when <see cref="PackagingFormat.WindowsInstallerExe"/> is selected).</summary>
    public WindowsInstallerOptions WindowsInstaller { get; init; } = new();
    /// <summary>Set for Mobile Build Studio builds; null for desktop builds.</summary>
    public MobileBuildOptions? Mobile { get; init; }
    /// <summary>Set for Swift package builds; null otherwise.</summary>
    public SwiftBuildOptions? Swift { get; init; }
    /// <summary>Set for Electron desktop builds; null otherwise.</summary>
    public ElectronBuildOptions? Electron { get; init; }
    /// <summary>Set for C/C++ builds (CMake, Make, sources); null otherwise.</summary>
    public NativeBuildOptions? Native { get; init; }
    /// <summary>macOS signing (ad hoc / Developer ID), installer and Mac App Store settings.</summary>
    public MacDistributionOptions MacDistribution { get; init; } = new();

    public string? RuntimeIdentifier => PlatformNames.ToRuntimeIdentifier(Platform, Architecture);
}

public enum InstallScope
{
    /// <summary>Installs for the current user only (no administrator rights; %LOCALAPPDATA%\Programs).</summary>
    CurrentUser,
    /// <summary>Installs for all users (requires administrator rights; Program Files).</summary>
    AllUsers,
}

/// <summary>Windows Setup.exe settings. Name, publisher, version and icon come from <see cref="AppIdentity"/>.</summary>
public sealed record WindowsInstallerOptions
{
    /// <summary>Folder name under Program Files / %LOCALAPPDATA%\Programs. Defaults to the application name.</summary>
    public string? InstallFolderName { get; init; }
    public InstallScope Scope { get; init; } = InstallScope.CurrentUser;
    /// <summary>Offer an optional "Create a desktop shortcut" task in the setup wizard.</summary>
    public bool OfferDesktopShortcut { get; init; } = true;
    /// <summary>Whether that task is checked by default.</summary>
    public bool DesktopShortcutCheckedByDefault { get; init; }
    /// <summary>Offer "Launch &lt;app&gt;" on the final wizard page.</summary>
    public bool OfferLaunchAfterInstall { get; init; } = true;
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
    /// <summary>
    /// What produced this build, fixed when it starts (source revision, scheme, configuration, SDK, destination, signing,
    /// toolchain) and completed once with the artifact paths. Results are compared with the current selection through it.
    /// </summary>
    public BuildProvenance? Provenance { get; set; }

    public TimeSpan? Duration => EndedAt is { } end ? end - StartedAt : null;
}

/// <summary>
/// Immutable record of the inputs that produced a build. Two selections that differ in any of these fields would not
/// produce the same artifact, so a result whose provenance differs from the current selection is a "previous build".
/// </summary>
public sealed record BuildProvenance
{
    /// <summary>"git 1a2b3c4d5e6f (main)" or "import of 2026-11-13 21:10 UTC" when the source is not a git checkout.</summary>
    public required string SourceRevision { get; init; }
    public required string Output { get; init; }
    public string? ProjectPath { get; init; }
    public string? Scheme { get; init; }
    public string? Configuration { get; init; }
    /// <summary>"iphoneos" / "iphonesimulator" for iOS; null elsewhere.</summary>
    public string? Sdk { get; init; }
    /// <summary>e.g. "generic/platform=iOS Simulator".</summary>
    public string? Destination { get; init; }
    public string? Signing { get; init; }
    public string? ExportMethod { get; init; }
    /// <summary>e.g. "Xcode 26.6 (/Applications/Xcode.app/Contents/Developer)".</summary>
    public string? Toolchain { get; init; }
    public IReadOnlyList<string> ArtifactPaths { get; init; } = [];
    /// <summary>A build-only minimum iOS the user accepted (IPHONEOS_DEPLOYMENT_TARGET passed to xcodebuild); null = the project's own.</summary>
    public string? DeploymentTargetOverride { get; init; }

    /// <summary>Fields that differ from <paramref name="current"/>, as "Scheme: HCGSentinelModern (built) → HCGSentinel (now)". Artifact paths are not compared.</summary>
    public IReadOnlyList<string> DifferencesFrom(BuildProvenance current)
    {
        var list = new List<string>();
        void Compare(string label, string? built, string? now)
        {
            if (!string.Equals(built ?? "", now ?? "", StringComparison.Ordinal)) list.Add($"{label}: {built ?? "—"} (built) → {now ?? "—"} (now)");
        }
        Compare("Source", SourceRevision, current.SourceRevision);
        Compare("Output", Output, current.Output);
        Compare("Project", ProjectPath, current.ProjectPath);
        Compare("Scheme", Scheme, current.Scheme);
        Compare("Configuration", Configuration, current.Configuration);
        Compare("SDK", Sdk, current.Sdk);
        Compare("Destination", Destination, current.Destination);
        Compare("Signing", Signing, current.Signing);
        Compare("Export", ExportMethod, current.ExportMethod);
        Compare("Toolchain", Toolchain, current.Toolchain);
        Compare("Minimum iOS override", DeploymentTargetOverride, current.DeploymentTargetOverride);
        return list;
    }

    public string Describe() => string.Join(" · ", new[]
    {
        Scheme is null ? null : "scheme " + Scheme, Configuration, Sdk is null ? null : "SDK " + Sdk, Destination, Signing is null ? null : "signing " + Signing,
        ExportMethod is null ? null : "export " + ExportMethod, Toolchain,
        DeploymentTargetOverride is null ? null : $"minimum iOS raised to {DeploymentTargetOverride} for this build only", SourceRevision,
    }.Where(s => s is not null));
}
