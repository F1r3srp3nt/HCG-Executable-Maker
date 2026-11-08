namespace HCG.ExecutableMaker.Domain;

/// <summary>An Xcode application found on this Mac (installed, whether or not it is selected).</summary>
public sealed record XcodeInstallation(string AppPath, string DeveloperDirectory, string? Version, string? BuildVersion)
{
    public string Display => $"Xcode {Version ?? "?"}{(BuildVersion is null ? "" : $" ({BuildVersion})")} — {AppPath}";
}

/// <summary>What is wrong (or right) with Xcode for HCG, from most to least fundamental.</summary>
public enum XcodeState
{
    Ready,
    /// <summary>No Xcode application was found.</summary>
    NotInstalled,
    /// <summary>Xcode is installed, but the active developer directory is the Command Line Tools (or another Xcode).</summary>
    InstalledNotSelected,
    /// <summary>The Xcode license has not been accepted.</summary>
    LicenseNotAccepted,
    /// <summary>Xcode's first-launch component installation has not run.</summary>
    FirstLaunchRequired,
    /// <summary>Xcode works but reports no SDKs.</summary>
    SdkMissing,
    /// <summary>The selected developer directory is unusable or xcodebuild failed for another reason (see the raw error).</summary>
    ValidationFailed,
    /// <summary>Not a Mac.</summary>
    NotApplicable,
}

/// <summary>Where the active developer directory comes from.</summary>
public enum DeveloperDirectorySource
{
    /// <summary>Selected in HCG (passed to HCG's processes as DEVELOPER_DIR; the system setting is unchanged).</summary>
    HcgSelection,
    /// <summary>DEVELOPER_DIR was already set in the environment HCG was started from.</summary>
    LaunchEnvironment,
    /// <summary>The system-wide selection (xcode-select).</summary>
    XcodeSelect,
    None,
}

public sealed record XcodeStatus
{
    public required XcodeState State { get; init; }
    public string? ActiveDeveloperDirectory { get; init; }
    public DeveloperDirectorySource Source { get; init; }
    /// <summary>Verified by running xcodebuild -version with the active developer directory.</summary>
    public string? VerifiedVersion { get; init; }
    public string? VerifiedBuild { get; init; }
    public IReadOnlyList<XcodeInstallation> Installed { get; init; } = [];
    /// <summary>SDKs reported by xcodebuild -showsdks, e.g. "macosx 26.2", "iphonesimulator 26.2".</summary>
    public IReadOnlyList<string> Sdks { get; init; } = [];
    /// <summary>What was found, in plain words.</summary>
    public required string Diagnosis { get; init; }
    /// <summary>The exact next step (empty when Ready).</summary>
    public string CorrectiveAction { get; init; } = "";
    /// <summary>First line of the tool's own error output, when a probe failed.</summary>
    public string? RawError { get; init; }
    public bool IsCommandLineToolsActive => ActiveDeveloperDirectory?.Contains("CommandLineTools", StringComparison.Ordinal) == true;
}
