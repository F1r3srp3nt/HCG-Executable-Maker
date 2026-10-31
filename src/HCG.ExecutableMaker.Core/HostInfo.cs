using System.Runtime.InteropServices;
using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.Core;

public static class HostInfo
{
    public static TargetPlatform CurrentPlatform =>
        OperatingSystem.IsWindows() ? TargetPlatform.Windows :
        OperatingSystem.IsMacOS() ? TargetPlatform.MacOS : TargetPlatform.Linux;

    public static TargetArchitecture CurrentArchitecture =>
        RuntimeInformation.OSArchitecture == Architecture.Arm64 ? TargetArchitecture.Arm64 : TargetArchitecture.X64;

    public static HostEnvironment Detect() => new(
        CurrentPlatform,
        CurrentArchitecture,
        RuntimeInformation.OSDescription,
        RuntimeInformation.FrameworkDescription,
        Environment.MachineName);

    public static string ProductVersion =>
        typeof(HostInfo).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "0.1.0";
}
