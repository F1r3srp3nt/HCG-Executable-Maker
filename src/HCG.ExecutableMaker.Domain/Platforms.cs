namespace HCG.ExecutableMaker.Domain;

/// <summary>Operating system a build targets (or the host runs on).</summary>
public enum TargetPlatform
{
    Windows,
    MacOS,
    Linux,
    Android,
    IOS,
}

public enum TargetArchitecture
{
    X64,
    Arm64,
    /// <summary>Not tied to one CPU (e.g. Android APKs/AABs carrying several ABIs, iOS builds for "generic" destinations).</summary>
    Universal,
}

public enum BuildConfigurationKind
{
    Release,
    Debug,
}

public static class PlatformNames
{
    public static string DisplayName(this TargetPlatform platform) => platform switch
    {
        TargetPlatform.Windows => "Windows",
        TargetPlatform.MacOS => "macOS",
        TargetPlatform.Linux => "Linux",
        TargetPlatform.Android => "Android",
        TargetPlatform.IOS => "iOS",
        _ => platform.ToString(),
    };

    public static string DisplayName(this TargetArchitecture arch) => arch switch
    {
        TargetArchitecture.X64 => "x64",
        TargetArchitecture.Arm64 => "ARM64",
        TargetArchitecture.Universal => "Universal",
        _ => arch.ToString(),
    };

    /// <summary>Maps platform + architecture to a .NET runtime identifier, or null when no RID applies.</summary>
    public static string? ToRuntimeIdentifier(TargetPlatform platform, TargetArchitecture arch)
    {
        if (arch == TargetArchitecture.Universal) return null;
        var a = arch == TargetArchitecture.Arm64 ? "arm64" : "x64";
        return platform switch
        {
            TargetPlatform.Windows => $"win-{a}",
            TargetPlatform.MacOS => $"osx-{a}",
            TargetPlatform.Linux => $"linux-{a}",
            _ => null,
        };
    }
}
