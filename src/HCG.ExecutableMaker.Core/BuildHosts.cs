namespace HCG.ExecutableMaker.Core;

/// <summary>
/// Where a build runs: <see cref="Local"/> (this computer) or <see cref="HcgServerHost"/> (HCG Server's Build Host,
/// <c>hcg.buildhost/1</c>, implemented by <c>BuildEngine.BuildHost.HcgServerBuildHost</c>; docs/BUILD-HOSTS.md).
/// </summary>
public enum BuildHostKind { Local, HcgServerHost }

/// <summary>What a build host says about itself. Measured on the host; never sent anywhere by Maker on its own.</summary>
public sealed record BuildHostInfo(string Id, BuildHostKind Kind, string Label, string OperatingSystem, string Architecture, IReadOnlyDictionary<string, string> Toolchains);

/// <summary>
/// A computer that can build. Builds and packaging run through the same engines whatever the host; the host only
/// decides where. HCG Server's Build Host is the remote implementation: the owner approves every job on HCG Server, the
/// host never holds the owner's Apple credentials or signs, and it returns the exact artifact bytes with their SHA-256
/// and signed provenance (docs/BUILD-HOSTS.md).
/// </summary>
public interface IBuildHost
{
    BuildHostInfo Describe();
    /// <summary>Why this host cannot build for <paramref name="platform"/>, or null when it can.</summary>
    string? CannotBuild(Domain.TargetPlatform platform);
}

/// <summary>This computer. iOS and macOS builds need a Mac with Xcode; that is checked by the toolchain detector, not here.</summary>
public sealed class LocalBuildHost : IBuildHost
{
    public BuildHostInfo Describe() => new("local", BuildHostKind.Local, "This computer",
        System.Runtime.InteropServices.RuntimeInformation.OSDescription.Trim(),
        System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(), new Dictionary<string, string>());

    public string? CannotBuild(Domain.TargetPlatform platform) =>
        platform is Domain.TargetPlatform.IOS or Domain.TargetPlatform.MacOS && !OperatingSystem.IsMacOS() ? "iOS and macOS builds need a Mac with Xcode." : null;
}
