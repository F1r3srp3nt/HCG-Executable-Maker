namespace HCG.ExecutableMaker.Domain;

public enum PackagingFormat
{
    /// <summary>Self-contained publish directory (no installer).</summary>
    PortableFolder,
    /// <summary>ZIP archive of the self-contained publish directory with executable permissions preserved.</summary>
    PortableZip,
    MacAppBundle,
    MacDmg,
    MacPkg,
    MacAppStore,
    WindowsInstallerExe,
    WindowsMsi,
    WindowsMsix,
    LinuxAppImage,
    LinuxDeb,
    LinuxRpm,
    AndroidApk,
    AndroidAab,
}

public enum PackagingAvailability
{
    Available,
    MissingTool,
    RequiresDifferentHost,
    NotImplemented,
    NotApplicable,
}

public sealed record PackagingOption(
    PackagingFormat Format,
    string DisplayName,
    string Description,
    PackagingAvailability Availability,
    string? Reason,
    string? PlannedMilestone)
{
    public bool IsAvailable => Availability == PackagingAvailability.Available;
}

public static class PackagingFormatNames
{
    public static string DisplayName(this PackagingFormat format) => format switch
    {
        PackagingFormat.PortableFolder => "Portable folder (self-contained)",
        PackagingFormat.PortableZip => "Portable ZIP archive",
        PackagingFormat.MacAppBundle => "macOS application bundle (.app)",
        PackagingFormat.MacDmg => "macOS disk image (.dmg)",
        PackagingFormat.MacPkg => "macOS installer package (.pkg)",
        PackagingFormat.MacAppStore => "Mac App Store submission",
        PackagingFormat.WindowsInstallerExe => "Windows installer (.exe)",
        PackagingFormat.WindowsMsi => "Windows Installer (.msi)",
        PackagingFormat.WindowsMsix => "MSIX (Microsoft Store)",
        PackagingFormat.LinuxAppImage => "AppImage",
        PackagingFormat.LinuxDeb => "Debian package (.deb)",
        PackagingFormat.LinuxRpm => "RPM package (.rpm)",
        PackagingFormat.AndroidApk => "Android APK",
        PackagingFormat.AndroidAab => "Android App Bundle (.aab)",
        _ => format.ToString(),
    };
}
