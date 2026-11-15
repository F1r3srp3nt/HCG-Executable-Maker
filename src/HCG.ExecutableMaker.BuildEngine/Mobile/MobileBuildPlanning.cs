using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.BuildEngine.Mobile;

/// <summary>The build settings for a mobile selection — one function for Mobile Build Studio and its command line.</summary>
public static class MobileBuildPlanning
{
    public static BuildSettings Settings(MobileAnalysis? m, MobileBuildOptions o, string outputDirectory, string? appName, string? appVersion) => new()
    {
        ProjectRelativePath = m?.Android?.Root ?? o.Container ?? m?.Ios?.WorkspacePath ?? m?.Ios?.ProjectPath ?? m?.Flutter?.Root ?? m?.Maui?.ProjectPath ?? m?.DotNetIos?.ProjectPath ?? m?.ArtifactPath ?? "",
        Platform = o.Output.Platform(),
        Architecture = TargetArchitecture.Universal,
        Configuration = o.Output is MobileOutput.AndroidDebugApk or MobileOutput.IosSimulatorApp ? BuildConfigurationKind.Debug : BuildConfigurationKind.Release,
        OutputDirectory = outputDirectory,
        Timeout = TimeSpan.FromMinutes(60),
        Identity = new AppIdentity
        {
            ApplicationName = string.IsNullOrWhiteSpace(appName) ? "App" : appName.Trim(),
            Version = string.IsNullOrWhiteSpace(appVersion) ? "1.0.0" : appVersion.Trim(),
            ApplicationId = m?.ApplicationIds.FirstOrDefault() ?? "app",
        },
        Mobile = o,
    };
}
