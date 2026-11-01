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
    Task<IReadOnlyList<PackagedOutput>> PackageAsync(PackagingRequest request, IPackagingLog log, CancellationToken cancellationToken);
}

/// <summary>Context used to decide which packaging formats are offered for a build.</summary>
public sealed record PackagingContext(TargetPlatform Target, HostEnvironment Host, ToolchainReport? Toolchains, bool IsGuiApplication, bool SelfContained = true);
