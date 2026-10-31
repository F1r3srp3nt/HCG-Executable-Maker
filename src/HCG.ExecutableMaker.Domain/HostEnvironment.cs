namespace HCG.ExecutableMaker.Domain;

/// <summary>Snapshot of the machine HCG Executable Maker is running on.</summary>
public sealed record HostEnvironment(
    TargetPlatform Platform,
    TargetArchitecture Architecture,
    string OsDescription,
    string FrameworkDescription,
    string MachineName)
{
    public string DisplayName => $"{Platform.DisplayName()} {Architecture.DisplayName()}";
}
