using HCG.Impactor.Devices;
using HCG.Impactor.Diagnostics;
using HCG.Impactor.Model;
using HCG.Impactor.Signing;

namespace HCG.Impactor;

/// <summary>What this computer can do with HCG Impactor. Inspection and planning work everywhere; signing and installing need a backend.</summary>
public sealed record ImpactorCapabilities(string Platform, bool CanInspect, bool CanSign, bool CanInstall, bool CanListDevices, string? Limitation)
{
    public static ImpactorCapabilities Detect(bool codesign, bool devicectl)
    {
        if (OperatingSystem.IsMacOS())
            return new("macOS", true, codesign, devicectl, devicectl,
                !codesign ? "codesign was not found (install Xcode or the Command Line Tools)."
                : !devicectl ? "devicectl was not found: device discovery and installation need Xcode 15 or later (open it once after installing)." : null);
        var os = OperatingSystem.IsWindows() ? "Windows" : "Linux";
        return new(os, true, false, false, false,
            $"On {os}, HCG Impactor inspects IPAs and checks signing plans. Signing and installing use Apple's codesign and devicectl, which run on macOS; " +
            "a future HCG Server macOS build host can do it remotely. Equivalent signing on this platform has not been implemented or tested.");
    }
}

/// <summary>Reads the signing identities and provisioning profiles available on this computer.</summary>
public interface ISigningInventorySource
{
    Task<SigningInventorySnapshot> ReadAsync(IReadOnlyList<string>? extraProfileFiles, CancellationToken cancellationToken);
    /// <summary>Problem reading the keychain or profile folders, when any (shown, never hidden).</summary>
    string? LastProblem { get; }
}

public sealed record SignJob(string SourceIpa, IpaReport Inspection, SigningPlan Plan, string OutputIpa);

public sealed record SignResult
{
    public bool Succeeded { get; init; }
    public string? OutputIpa { get; init; }
    public string? OutputSha256 { get; init; }
    /// <summary>The signed .app folder (kept for installation; deleted by <see cref="IAppSigner.Cleanup"/>).</summary>
    public string? SignedAppDirectory { get; init; }
    public string? WorkDirectory { get; init; }
    /// <summary>codesign --verify output for the signed app.</summary>
    public string? Verification { get; init; }
    /// <summary>The signed IPA, inspected again from the output file.</summary>
    public IpaReport? Reinspection { get; init; }
    public Diagnosis? Failure { get; init; }
    public IReadOnlyList<string> Log { get; init; } = [];
}

/// <summary>Signs an inspected IPA according to a <see cref="SigningPlan"/>. The source IPA is never modified.</summary>
public interface IAppSigner
{
    bool IsAvailable { get; }
    Task<SignResult> SignAsync(SignJob job, IProgress<string>? progress, CancellationToken cancellationToken);
    /// <summary>Unpacks an already signed IPA into a private folder for installation (no changes to its contents).</summary>
    Task<SignResult> UnpackAsync(string ipa, CancellationToken cancellationToken);
    void Cleanup(SignResult result);
}

public sealed record DeviceListing(IReadOnlyList<AppleDevice> Devices, Diagnosis? Problem, string? ToolVersion, string RawRedacted);

public sealed record InstalledApp(string BundleIdentifier, string? Name, string? Version, string? Build);

public sealed record InstallProgress(int? Percent, string Message);

public sealed record InstallResult
{
    public bool Succeeded { get; init; }
    public string? InstalledBundleIdentifier { get; init; }
    /// <summary>Where iOS placed the app (from devicectl), redacted of personal paths.</summary>
    public string? InstallationLocation { get; init; }
    public TimeSpan Duration { get; init; }
    public Diagnosis? Failure { get; init; }
    public string RawRedacted { get; init; } = "";
}

public sealed record LaunchResult(bool Succeeded, Diagnosis? Failure, string RawRedacted);

/// <summary>Discovers devices and installs/launches apps on them (macOS: Xcode's devicectl / CoreDevice).</summary>
public interface IDeviceService
{
    bool IsAvailable { get; }
    Task<DeviceListing> ListAsync(CancellationToken cancellationToken);
    Task<InstalledApp?> FindInstalledAsync(AppleDevice device, string bundleIdentifier, CancellationToken cancellationToken);
    Task<InstallResult> InstallAsync(AppleDevice device, string appPath, IProgress<InstallProgress>? progress, CancellationToken cancellationToken);
    Task<LaunchResult> LaunchAsync(AppleDevice device, string bundleIdentifier, CancellationToken cancellationToken);
}
