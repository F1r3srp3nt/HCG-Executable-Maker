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

        if (ctx.SwiftKind is { } swiftKind)
        {
            EvaluateSwift(ctx, swiftKind, Add);
            return list;
        }

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

    /// <summary>
    /// Swift packages (SwiftPM) on macOS. A GUI product becomes a .app (and optionally the .app as ZIP or DMG); a
    /// command-line product becomes a portable folder/ZIP. Never offered for local builds on Windows/Linux.
    /// </summary>
    private static void EvaluateSwift(PackagingContext ctx, SwiftAppKind kind, Action<PackagingFormat, string, PackagingAvailability, string?, string?> add)
    {
        var offMac = ctx.Host.Platform != TargetPlatform.MacOS;
        const string hostReason = "Swift macOS apps are compiled with Xcode's toolchain and can only be built on a Mac.";
        PackagingAvailability Mac() => offMac ? PackagingAvailability.RequiresDifferentHost : PackagingAvailability.Available;
        string? MacReason() => offMac ? hostReason : null;
        if (ctx.Target != TargetPlatform.MacOS)
        {
            add(PackagingFormat.MacAppBundle, "Swift packages are built for macOS.", PackagingAvailability.NotApplicable, "Select macOS as the target platform.", null);
            return;
        }
        if (kind == SwiftAppKind.GuiApp)
        {
            add(PackagingFormat.MacAppBundle, "Name.app with Info.plist, the Swift executable, icon, SwiftPM resource bundles and an ad-hoc signature.", Mac(), MacReason(), null);
            add(PackagingFormat.PortableZip, "The .app compressed with ditto (keeps permissions and the code signature) for sharing.", Mac(), MacReason(), null);
            add(PackagingFormat.PortableFolder, "Loose executable folder.", PackagingAvailability.NotApplicable,
                "SwiftUI/AppKit apps must run from an .app bundle (Info.plist, privacy keys, signature). Use the .app or its ZIP.", null);
            if (offMac)
                add(PackagingFormat.MacDmg, "Compressed disk image containing the .app and an Applications shortcut.", PackagingAvailability.RequiresDifferentHost, hostReason, null);
            else if (ctx.Toolchains?.IsAvailable(ToolchainIds.Hdiutil) != true)
                add(PackagingFormat.MacDmg, "Compressed disk image.", PackagingAvailability.MissingTool, "hdiutil was not detected on this Mac (it ships with macOS in /usr/bin).", null);
            else
                add(PackagingFormat.MacDmg, "Compressed disk image containing the .app and an Applications shortcut.", PackagingAvailability.Available, null, null);
        }
        else
        {
            add(PackagingFormat.PortableFolder, "Folder with the command-line executable and any SwiftPM resource bundles.", Mac(), MacReason(), null);
            add(PackagingFormat.PortableZip, "The executable folder as ZIP with the executable bit recorded.", Mac(), MacReason(), null);
            add(PackagingFormat.MacAppBundle, "Standard .app bundle.", PackagingAvailability.NotApplicable,
                "This product is a command-line tool (no SwiftUI/AppKit entry point). Command-line tools are distributed as an executable, not an .app.", null);
            add(PackagingFormat.MacDmg, "Disk image.", PackagingAvailability.NotApplicable, "DMG packaging requires an .app bundle.", null);
        }
        add(PackagingFormat.MacPkg, "macOS installer package.", PackagingAvailability.NotImplemented, "PKG packaging is planned for M0.3.", "M0.3");
        add(PackagingFormat.MacAppStore, "Sandboxed, signed package for App Store Connect.", PackagingAvailability.NotImplemented,
            "App Store distribution needs Developer ID signing, sandbox entitlements and notarization, which HCG does not perform.", "M0.4");
    }

    /// <summary>Formats that must also be produced because a selected format depends on them.</summary>
    public static IReadOnlyList<PackagingFormat> WithDependencies(IEnumerable<PackagingFormat> selected) => WithDependencies(selected, null);

    /// <summary>
    /// Formats that must also be produced because a selected format depends on them, in production order. For a
    /// Swift GUI app the ZIP wraps the .app, so the bundle is produced first.
    /// </summary>
    public static IReadOnlyList<PackagingFormat> WithDependencies(IEnumerable<PackagingFormat> selected, BuildSettings? settings)
    {
        var set = selected.ToList();
        var swiftGui = settings?.Swift?.Kind == SwiftAppKind.GuiApp;
        var needsBundle = set.Contains(PackagingFormat.MacDmg) || (swiftGui && set.Contains(PackagingFormat.PortableZip));
        if (needsBundle && !set.Contains(PackagingFormat.MacAppBundle))
            set.Insert(0, PackagingFormat.MacAppBundle);
        // Bundles must exist before the DMG / ZIP that wraps them.
        return [.. set.Distinct().OrderBy(f => f == PackagingFormat.MacAppBundle ? 0 : f == PackagingFormat.MacDmg ? 2 : 1)];
    }
}
