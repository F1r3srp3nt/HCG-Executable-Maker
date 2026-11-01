using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.Packaging;

/// <summary>
/// The single source of truth for which packaging formats exist, which are implemented, and why a format is unavailable.
/// The UI disables anything not <see cref="PackagingAvailability.Available"/> and shows the reason.
/// </summary>
public static class PackagingCatalog
{
    public static IReadOnlyList<PackagingOption> Evaluate(PackagingContext ctx)
    {
        var list = new List<PackagingOption>();
        void Add(PackagingFormat f, string desc, PackagingAvailability a, string? reason = null, string? milestone = null) =>
            list.Add(new PackagingOption(f, f.DisplayName(), desc, a, reason, milestone));

        switch (ctx.Target)
        {
            case TargetPlatform.Windows:
                Add(PackagingFormat.PortableFolder, "Self-contained folder with the .exe and all dependencies. No installation required.", PackagingAvailability.Available);
                Add(PackagingFormat.PortableZip, "The portable folder compressed into a single ZIP for distribution.", PackagingAvailability.Available);
                const string setupDesc = "Setup wizard (.exe, Inno Setup) that installs the app, adds a Start-menu shortcut and an optional desktop shortcut, and registers an uninstaller.";
                if (!ctx.SelfContained)
                    Add(PackagingFormat.WindowsInstallerExe, setupDesc, PackagingAvailability.NotApplicable,
                        "Enable 'Self-contained' in Configure: the installer must carry the .NET runtime, otherwise the installed app cannot start on PCs without .NET.");
                else if (ctx.Host.Platform != TargetPlatform.Windows)
                    Add(PackagingFormat.WindowsInstallerExe, setupDesc, PackagingAvailability.RequiresDifferentHost,
                        "The Inno Setup compiler (ISCC.exe) runs only on Windows. Build on a Windows PC, or use the Windows CI runner (docs/WINDOWS-INSTALLER.md).");
                else if (ctx.Toolchains?.IsAvailable(ToolchainIds.InnoSetup) != true)
                    Add(PackagingFormat.WindowsInstallerExe, setupDesc, PackagingAvailability.MissingTool,
                        "Inno Setup 6 was not detected. Install it from https://jrsoftware.org/isinfo.php, then refresh Toolchains.");
                else
                    Add(PackagingFormat.WindowsInstallerExe, setupDesc, PackagingAvailability.Available);
                Add(PackagingFormat.WindowsMsi, "Windows Installer package for managed deployment.", PackagingAvailability.NotImplemented, "MSI generation with the WiX Toolset is the next milestone (M0.2b).", "M0.2b");
                Add(PackagingFormat.WindowsMsix, "MSIX package for the Microsoft Store.", PackagingAvailability.NotImplemented, "MSIX packaging and Store signing are planned for M0.2/M0.4.", "M0.4");
                break;

            case TargetPlatform.MacOS:
                if (ctx.IsGuiApplication)
                    Add(PackagingFormat.MacAppBundle, "Standard .app bundle with Info.plist, icon and correct executable permissions.", PackagingAvailability.Available);
                else
                    Add(PackagingFormat.MacAppBundle, "Standard .app bundle.", PackagingAvailability.NotApplicable, "Console applications are distributed as portable folders, not .app bundles.");
                Add(PackagingFormat.PortableZip, "Self-contained folder as ZIP with Unix permissions preserved.", PackagingAvailability.Available);
                Add(PackagingFormat.PortableFolder, "Self-contained folder with the native executable and dependencies.", PackagingAvailability.Available);
                if (!ctx.IsGuiApplication)
                    Add(PackagingFormat.MacDmg, "Disk image.", PackagingAvailability.NotApplicable, "DMG packaging requires an .app bundle.");
                else if (ctx.Host.Platform != TargetPlatform.MacOS)
                    Add(PackagingFormat.MacDmg, "Compressed disk image containing the .app and an Applications shortcut.", PackagingAvailability.RequiresDifferentHost, "DMG creation requires hdiutil, available only on a macOS build host.");
                else if (ctx.Toolchains?.IsAvailable(ToolchainIds.Hdiutil) != true)
                    Add(PackagingFormat.MacDmg, "Compressed disk image.", PackagingAvailability.MissingTool, "hdiutil was not detected on this Mac.");
                else
                    Add(PackagingFormat.MacDmg, "Compressed disk image containing the .app and an Applications shortcut.", PackagingAvailability.Available);
                Add(PackagingFormat.MacPkg, "macOS installer package.", PackagingAvailability.NotImplemented, "PKG packaging is planned for M0.3.", "M0.3");
                Add(PackagingFormat.MacAppStore, "Sandboxed, signed package for App Store Connect.", PackagingAvailability.NotImplemented, "Mac App Store packaging requires signing (M0.4).", "M0.4");
                break;

            case TargetPlatform.Linux:
                Add(PackagingFormat.PortableFolder, "Self-contained folder with the native executable and dependencies.", PackagingAvailability.Available);
                Add(PackagingFormat.PortableZip, "Self-contained folder as ZIP with Unix permissions preserved.", PackagingAvailability.Available);
                Add(PackagingFormat.LinuxAppImage, "Single-file AppImage.", PackagingAvailability.NotImplemented, "Linux packaging is a future target (M0.5+).", "M0.5");
                Add(PackagingFormat.LinuxDeb, "Debian/Ubuntu package.", PackagingAvailability.NotImplemented, "Linux packaging is a future target (M0.5+).", "M0.5");
                Add(PackagingFormat.LinuxRpm, "Fedora/RHEL package.", PackagingAvailability.NotImplemented, "Linux packaging is a future target (M0.5+).", "M0.5");
                break;

            case TargetPlatform.Android:
                Add(PackagingFormat.AndroidApk, "Android application package.", PackagingAvailability.NotImplemented, "No Android build pipeline exists yet.", "M0.5");
                Add(PackagingFormat.AndroidAab, "Android App Bundle for Google Play.", PackagingAvailability.NotImplemented, "No Android build pipeline exists yet.", "M0.5");
                break;
        }
        return list;
    }

    /// <summary>Formats that must also be produced because a selected format depends on them.</summary>
    public static IReadOnlyList<PackagingFormat> WithDependencies(IEnumerable<PackagingFormat> selected)
    {
        var set = selected.ToList();
        if (set.Contains(PackagingFormat.MacDmg) && !set.Contains(PackagingFormat.MacAppBundle))
            set.Insert(0, PackagingFormat.MacAppBundle);
        // Bundles must exist before the DMG that wraps them.
        return [.. set.Distinct().OrderBy(f => f == PackagingFormat.MacDmg ? 1 : 0)];
    }
}
