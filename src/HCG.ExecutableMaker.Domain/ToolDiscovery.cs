namespace HCG.ExecutableMaker.Domain;

/// <summary>One JDK candidate HCG examined.</summary>
/// <param name="Home">JAVA_HOME of the candidate (the folder containing bin/java).</param>
/// <param name="Source">Where it came from: "Saved in HCG", "JAVA_HOME", "Android Studio (bundled JDK)", "/usr/libexec/java_home", "PATH", …</param>
/// <param name="Problem">Why it is not usable (null when it is a working JDK).</param>
public sealed record JdkCandidate(string Home, string Source, string? Version, int Major, bool HasJavac, string? Problem)
{
    public bool IsUsable => Problem is null;
    public string JavaExecutable => System.IO.Path.Combine(Home, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
}

/// <summary>JDK discovery result: the JDK HCG uses and everything it looked at.</summary>
/// <param name="SavedHome">The JDK saved in HCG (Toolchains › Java › Browse…), if any.</param>
public sealed record JavaDiscoveryResult(JdkCandidate? Selected, IReadOnlyList<JdkCandidate> Candidates, string? SavedHome);

/// <summary>One installed Android SDK build-tools version and the tools found inside it.</summary>
public sealed record AndroidBuildToolsInfo(string Version, string Directory, string? Apksigner, string? Zipalign, string? Aapt2)
{
    public bool IsComplete => Apksigner is not null && Zipalign is not null && Aapt2 is not null;
}

/// <summary>Android SDK discovery result. Platforms (android-NN with android.jar) and build-tools are separate components.</summary>
public sealed record AndroidSdkDiscoveryResult
{
    public string? Root { get; init; }
    public string? Source { get; init; }
    public string? SavedRoot { get; init; }
    /// <summary>Why the chosen location is not a usable SDK (null when it is).</summary>
    public string? Problem { get; init; }
    /// <summary>Installed platform API levels ("35", "34", "VanillaIceCream"…), newest first; only those with android.jar.</summary>
    public IReadOnlyList<string> Platforms { get; init; } = [];
    /// <summary>Installed build-tools, newest first.</summary>
    public IReadOnlyList<AndroidBuildToolsInfo> BuildTools { get; init; } = [];
    /// <summary>The newest complete build-tools (apksigner, zipalign and aapt2 present).</summary>
    public AndroidBuildToolsInfo? SelectedBuildTools => BuildTools.FirstOrDefault(b => b.IsComplete);
    public bool HasPlatformTools { get; init; }
    /// <summary>SDK licences have been accepted (sdk/licenses contains the licence hashes). HCG never accepts them.</summary>
    public bool LicensesAccepted { get; init; }
    public IReadOnlyList<string> ExaminedLocations { get; init; } = [];
}

public enum IosProfileKind
{
    Development,
    AdHoc,
    AppStore,
    Enterprise,
    Unknown,
}

/// <summary>An iOS provisioning profile (*.mobileprovision) on this Mac.</summary>
public sealed record IosProvisioningProfile(string Path, string? Name, string? Uuid, string? TeamId, string? ApplicationIdentifier, IosProfileKind Kind, DateTimeOffset? Expiration)
{
    public bool IsExpired(DateTimeOffset now) => Expiration is { } e && e <= now;

    public bool Matches(string bundleId)
    {
        if (ApplicationIdentifier is not { } appId || TeamId is null || !appId.StartsWith(TeamId + ".", StringComparison.Ordinal)) return false;
        var pattern = appId[(TeamId.Length + 1)..];
        return pattern == "*" || pattern == bundleId || (pattern.EndsWith(".*", StringComparison.Ordinal) && bundleId.StartsWith(pattern[..^1], StringComparison.Ordinal));
    }
}

/// <summary>Apple certificate common names → certificate type (also covers the legacy iPhone names).</summary>
public static class AppleCertificateNames
{
    public static AppleCertificateKind Classify(string commonName) => commonName switch
    {
        _ when commonName.StartsWith("Developer ID Application:", StringComparison.Ordinal) => AppleCertificateKind.DeveloperIdApplication,
        _ when commonName.StartsWith("Developer ID Installer:", StringComparison.Ordinal) => AppleCertificateKind.DeveloperIdInstaller,
        _ when commonName.StartsWith("Apple Distribution:", StringComparison.Ordinal)
               || commonName.StartsWith("iPhone Distribution:", StringComparison.Ordinal) => AppleCertificateKind.AppleDistribution,
        _ when commonName.StartsWith("3rd Party Mac Developer Application:", StringComparison.Ordinal)
               || commonName.StartsWith("Mac App Distribution:", StringComparison.Ordinal) => AppleCertificateKind.MacAppDistribution,
        _ when commonName.StartsWith("3rd Party Mac Developer Installer:", StringComparison.Ordinal)
               || commonName.StartsWith("Mac Installer Distribution:", StringComparison.Ordinal) => AppleCertificateKind.MacInstallerDistribution,
        _ when commonName.StartsWith("Apple Development:", StringComparison.Ordinal)
               || commonName.StartsWith("iPhone Developer:", StringComparison.Ordinal)
               || commonName.StartsWith("Mac Developer:", StringComparison.Ordinal) => AppleCertificateKind.Development,
        _ => AppleCertificateKind.Other,
    };
}

/// <summary>Whether a tool is ready for one specific build.</summary>
public enum ReadinessState
{
    Ready,
    NotFound,
    InvalidPath,
    IncompatibleVersion,
    SetupRequired,
    /// <summary>The tool exists but cannot be confirmed to fit (e.g. a version newer than HCG's tables). Never blocks.</summary>
    NotVerifiable,
    /// <summary>This build does not use the tool.</summary>
    NotNeeded,
    /// <summary>The tool belongs to another host OS (e.g. iOS on Windows).</summary>
    OtherHost,
}

/// <summary>One row of a project's readiness: a required blocker or an optional/irrelevant tool.</summary>
public sealed record ReadinessItem(
    string Tool,
    ReadinessState State,
    bool Required,
    string Detail,
    string? Fix = null,
    string? Path = null,
    string? Version = null,
    string? SetupUrl = null)
{
    public bool Blocks => Required && State is not (ReadinessState.Ready or ReadinessState.NotNeeded or ReadinessState.NotVerifiable);
}

/// <summary>Can this project + target build on this machine, and what exactly is missing.</summary>
public sealed record BuildReadiness(string Target, IReadOnlyList<ReadinessItem> Items)
{
    public bool CanBuild => !Items.Any(i => i.Blocks);
    public IEnumerable<ReadinessItem> Blockers => Items.Where(i => i.Blocks);
    public IEnumerable<ReadinessItem> Optional => Items.Where(i => !i.Required);
    public string Summary => CanBuild
        ? $"{Target}: ready to build."
        : $"{Target}: {Blockers.Count()} required item(s) missing — " + string.Join(" ", Blockers.Select(b => b.Detail));
}
