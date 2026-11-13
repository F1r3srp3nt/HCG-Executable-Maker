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

    private static string? Metadata(string key) =>
        typeof(HostInfo).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value;

    /// <summary>Git commit this build was made from ("+uncommitted" when the working tree had changes); "unknown" without git.</summary>
    public static string ProductCommit => Metadata("HcgCommit") is { Length: > 0 } c ? c : "unknown";

    /// <summary>UTC build time (ISO 8601).</summary>
    public static string BuildDate => Metadata("HcgBuildDate") ?? "unknown";

    /// <summary>The running executable (inside the .app on macOS).</summary>
    public static string ExecutablePath => Environment.ProcessPath ?? AppContext.BaseDirectory;

    /// <summary>The enclosing .app bundle when running from one (macOS), otherwise null.</summary>
    public static string? AppBundlePath
    {
        get
        {
            var dir = Path.GetDirectoryName(ExecutablePath);
            // …/Name.app/Contents/MacOS/executable
            var contents = dir is null ? null : Path.GetDirectoryName(dir);
            var bundle = contents is null ? null : Path.GetDirectoryName(contents);
            return bundle is not null && bundle.EndsWith(".app", StringComparison.Ordinal) && Path.GetFileName(contents) == "Contents" ? bundle : null;
        }
    }

    /// <summary>One-line identity, e.g. "HCG Executable Maker 0.2.0 (commit 8cd38ae…, built 2026-11-08T05:00:00Z)".</summary>
    public static string BuildIdentity => $"HCG Executable Maker {ProductVersion} (commit {ProductCommit}, built {BuildDate})";
}
