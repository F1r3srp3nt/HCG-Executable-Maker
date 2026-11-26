namespace HCG.ExecutableMaker.Core;

/// <summary>Where a build runs. Today only <see cref="Local"/> exists; <see cref="HcgServerHost"/> is a proposal (docs/BUILD-HOSTS.md).</summary>
public enum BuildHostKind { Local, HcgServerHost }

/// <summary>What a build host says about itself. Measured on the host; never sent anywhere by Maker on its own.</summary>
public sealed record BuildHostInfo(string Id, BuildHostKind Kind, string Label, string OperatingSystem, string Architecture, IReadOnlyDictionary<string, string> Toolchains);

/// <summary>
/// A computer that can build. Builds and packaging run through the same engines whatever the host; the host only
/// decides where. A remote host (an owner-authorized Mac registered with HCG Server) is a later implementation of this
/// interface: it must authenticate through HCG Connect, never hold the owner's Apple credentials, and return the exact
/// artifact bytes with their SHA-256 and provenance (docs/BUILD-HOSTS.md).
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
