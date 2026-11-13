using HCG.ExecutableMaker.Core;
using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.Icons;

namespace HCG.ExecutableMaker.Packaging;

/// <summary>Inputs for packaging an already-published, verified build output.</summary>
public sealed record PackagingRequest
{
    public required BuildSettings Settings { get; init; }
    /// <summary>Self-contained publish output (HCG-owned staging; never modified by packagers).</summary>
    public required string PublishDirectory { get; init; }
    /// <summary>File name of the native app host inside <see cref="PublishDirectory"/>.</summary>
    public required string ExecutableFileName { get; init; }
    /// <summary>Per-build output folder inside the user's chosen output directory.</summary>
    public required string OutputRoot { get; init; }
    /// <summary>Base file name for artifacts, e.g. <c>MyApp-1.2.0-osx-arm64</c>.</summary>
    public required string ArtifactBaseName { get; init; }
    public GeneratedIcons? Icons { get; init; }
    public required HostEnvironment Host { get; init; }
    public required ToolchainReport Toolchains { get; init; }
}

public sealed record PackagedOutput(PackagingFormat Format, ArtifactKind Kind, string Path, IReadOnlyList<VerificationCheck> Checks);

public interface IPackagingLog
{
    void Info(string message);
    void Warning(string message);
}

/// <summary>A packaging backend. Packaging is separate from compilation and only consumes publish output.</summary>
public interface IPackager
{
    PackagingFormat Format { get; }
    /// <summary>
    /// Whether this packager produces <see cref="Format"/> for these settings. Several packagers may share a format
    /// (e.g. the .NET and Swift .app bundle packagers); the registry picks the one that handles the build.
    /// </summary>
    bool Handles(BuildSettings settings) => true;
    Task<IReadOnlyList<PackagedOutput>> PackageAsync(PackagingRequest request, IPackagingLog log, CancellationToken cancellationToken);
}

/// <summary>Context used to decide which packaging formats are offered for a build.</summary>
/// <param name="SwiftKind">Set for Swift packages: GUI apps become .app bundles, command-line tools portable folders/ZIPs.</param>
/// <param name="Signing">Signing material on this Mac (null when not read yet / not a Mac).</param>
/// <param name="Distribution">The current distribution choices (signing mode, identities, profile).</param>
/// <param name="BundleId">CFBundleIdentifier, to match provisioning profiles.</param>
/// <param name="HasRootResourceBundles">SwiftPM resource bundles end up at the .app root (outside the signature seal).</param>
/// <param name="Evidence">Sandbox / hardened-runtime evidence from the product's sources.</param>
public sealed record PackagingContext(TargetPlatform Target, HostEnvironment Host, ToolchainReport? Toolchains, bool IsGuiApplication, bool SelfContained = true, SwiftAppKind? SwiftKind = null)
{
    public MacSigningInventory? Signing { get; init; }
    public MacDistributionOptions? Distribution { get; init; }
    public string? BundleId { get; init; }
    public bool HasRootResourceBundles { get; init; }
    public IReadOnlyList<EntitlementEvidence> Evidence { get; init; } = [];
    public DateTimeOffset Now { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>An Electron app packaged by electron-builder (only the combinations HCG implements are offered).</summary>
    public bool IsElectron { get; init; }
    /// <summary>A C/C++ build (CMake, Make or sources) for this host.</summary>
    public bool IsNative { get; init; }
    /// <summary>The C/C++ project produces its own macOS application bundle.</summary>
    public bool IsNativeMacBundle { get; init; }
    /// <summary>A Theos project: the only output is its Debian package for jailbroken devices.</summary>
    public bool IsTheos { get; init; }
}
