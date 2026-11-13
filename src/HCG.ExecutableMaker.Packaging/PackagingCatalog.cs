using HCG.ExecutableMaker.Domain;
using HCG.ExecutableMaker.Packaging.MacDistribution;

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

        if (ctx.IsElectron)
        {
            EvaluateElectron(ctx, Add);
            return list;
        }
        if (ctx.IsTheos)
        {
            Add(PackagingFormat.JailbreakDeb, "The Debian package Theos builds (make package FINALPACKAGE=1), checked: metadata, install prefix, payload, binaries and their architectures. " +
                "For jailbroken devices only; installed by you with Sileo, Zebra, Filza or dpkg.", ctx.Host.Platform is TargetPlatform.MacOS or TargetPlatform.Linux
                    ? PackagingAvailability.Available : PackagingAvailability.RequiresDifferentHost, ctx.Host.Platform is TargetPlatform.MacOS or TargetPlatform.Linux ? null : "Theos builds on macOS or Linux.");
            Add(PackagingFormat.IosIpa, "IPA.", PackagingAvailability.NotApplicable, "A Theos package is not an iOS app package: tweaks and preference bundles are injected or loaded by the jailbreak, and HCG never relabels a .deb as an IPA.");
            Add(PackagingFormat.IosArchive, "Xcode archive.", PackagingAvailability.NotApplicable, "Theos projects are built by Theos' makefiles, not archived with Xcode.");
            return list;
        }
        if (ctx.IsNative)
        {
            EvaluateNative(ctx, Add);
            return list;
        }
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
                {
                    var (pa, pr) = MacDistributionCatalog.Pkg(ctx, dotNet: true);
                    Add(PackagingFormat.MacPkg, MacDistributionCatalog.PkgDescription, pa, pr);
                    Add(PackagingFormat.MacAppStore, MacDistributionCatalog.AppStoreDescription, PackagingAvailability.NotApplicable,
                        ".NET apps are incompatible with Mac App Store preparation in this version: they need JIT/unsigned-memory exceptions and a sandbox review that HCG does not perform. Use the .pkg or DMG for direct distribution.");
                }
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
    /// <summary>
    /// C/C++ builds for this host: the executable in a portable folder or ZIP (with the project's own shared libraries
    /// and the runtime files the user listed), or — when the project builds a macOS app bundle — that .app and its ZIP.
    /// Installers are not offered: HCG does not know what a native program needs installed.
    /// </summary>
    private static void EvaluateNative(PackagingContext ctx, Action<PackagingFormat, string, PackagingAvailability, string?, string?> add)
    {
        const string notNative = "Not implemented for C/C++ builds in this version.";
        if (ctx.Target != ctx.Host.Platform)
        {
            add(PackagingFormat.PortableFolder, "C/C++ projects are built for this computer's platform.", PackagingAvailability.RequiresDifferentHost,
                $"Building {ctx.Target.DisplayName()} binaries needs a {ctx.Target.DisplayName()} toolchain; HCG builds C/C++ for {ctx.Host.Platform.DisplayName()} on this computer.", null);
            return;
        }
        if (ctx.IsNativeMacBundle)
        {
            add(PackagingFormat.MacAppBundle, "The .app the project builds (MACOSX_BUNDLE), kept as built, verified and signed as the distribution setting says.", PackagingAvailability.Available, null, null);
            add(PackagingFormat.PortableZip, "The .app compressed with ditto (keeps permissions, links and the signature).", PackagingAvailability.Available, null, null);
            add(PackagingFormat.PortableFolder, "Loose executable.", PackagingAvailability.NotApplicable, "The project builds an app bundle; its executable runs from inside the .app.", null);
        }
        else
        {
            add(PackagingFormat.PortableFolder, "The executable, the project's own shared libraries it uses, the runtime files you listed and RUNTIME-DEPENDENCIES.txt.", PackagingAvailability.Available, null, null);
            add(PackagingFormat.PortableZip, "The same folder as a ZIP with the executable bit recorded.", PackagingAvailability.Available, null, null);
            if (ctx.Target == TargetPlatform.MacOS)
                add(PackagingFormat.MacAppBundle, "App bundle.", PackagingAvailability.NotApplicable, "This target is a command-line executable. HCG does not wrap command-line programs in an .app; projects that build a GUI app bundle (MACOSX_BUNDLE) keep their own.", null);
        }
        foreach (var f in new[] { PackagingFormat.MacDmg, PackagingFormat.MacPkg, PackagingFormat.WindowsInstallerExe, PackagingFormat.LinuxAppImage, PackagingFormat.LinuxDeb })
            add(f, f.DisplayName() + ".", PackagingAvailability.NotImplemented, notNative, null);
    }

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
        if (kind == SwiftAppKind.GuiApp)
        {
            var (pa, pr) = MacDistributionCatalog.Pkg(ctx, dotNet: false);
            add(PackagingFormat.MacPkg, MacDistributionCatalog.PkgDescription, pa, pr, null);
            var (sa, sr) = MacDistributionCatalog.AppStore(ctx);
            add(PackagingFormat.MacAppStore, MacDistributionCatalog.AppStoreDescription, sa, sr, null);
        }
        else
        {
            add(PackagingFormat.MacPkg, MacDistributionCatalog.PkgDescription, PackagingAvailability.NotApplicable,
                "This product is a command-line tool; the installer package in this version installs .app bundles into Applications.", null);
            add(PackagingFormat.MacAppStore, MacDistributionCatalog.AppStoreDescription, PackagingAvailability.NotApplicable,
                "The Mac App Store accepts applications, not command-line tools.", null);
        }
    }

    /// <summary>
    /// Electron apps: electron-builder produces the app; HCG runs its dir/zip/dmg targets and verifies them. Only the
    /// combinations HCG implements (and has verified) are Available; electron-builder's other targets are listed with why.
    /// </summary>
    private static void EvaluateElectron(PackagingContext ctx, Action<PackagingFormat, string, PackagingAvailability, string?, string?> add)
    {
        const string otherTargets = "electron-builder can produce this, but HCG does not run or verify that target in this version.";
        switch (ctx.Target)
        {
            case TargetPlatform.MacOS when ctx.Host.Platform != TargetPlatform.MacOS:
                foreach (var f in new[] { PackagingFormat.MacAppBundle, PackagingFormat.PortableZip, PackagingFormat.MacDmg })
                    add(f, "macOS Electron app.", PackagingAvailability.RequiresDifferentHost,
                        "HCG packages Electron apps for macOS on a Mac (electron-builder signs the app and builds the dmg with macOS tools).", null);
                break;
            case TargetPlatform.MacOS:
                add(PackagingFormat.MacAppBundle, "The .app electron-builder builds (target dir), signed as the electron-builder configuration says (mac.identity).", PackagingAvailability.Available, null, null);
                add(PackagingFormat.PortableZip, "electron-builder's zip target: the .app as a ZIP for sharing.", PackagingAvailability.Available, null, null);
                if (ctx.Toolchains?.IsAvailable(ToolchainIds.Hdiutil) != true)
                    add(PackagingFormat.MacDmg, "electron-builder's dmg target.", PackagingAvailability.MissingTool, "hdiutil was not detected on this Mac (it ships with macOS in /usr/bin).", null);
                else
                    add(PackagingFormat.MacDmg, "electron-builder's dmg target: a disk image with the app and an Applications shortcut.", PackagingAvailability.Available, null, null);
                add(PackagingFormat.PortableFolder, "Loose folder.", PackagingAvailability.NotApplicable, "A macOS Electron app runs from its .app bundle. Use the .app or its ZIP.", null);
                add(PackagingFormat.MacPkg, MacDistributionCatalog.PkgDescription, PackagingAvailability.NotImplemented, otherTargets, null);
                add(PackagingFormat.MacAppStore, MacDistributionCatalog.AppStoreDescription, PackagingAvailability.NotImplemented, otherTargets + " (electron-builder's mas target needs a sandboxed build and App Store provisioning.)", null);
                break;
            case TargetPlatform.Linux when ctx.Host.Platform != TargetPlatform.Linux:
                add(PackagingFormat.PortableFolder, "Linux Electron app.", PackagingAvailability.RequiresDifferentHost, "HCG packages Linux Electron apps on a Linux host.", null);
                break;
            case TargetPlatform.Linux:
                add(PackagingFormat.PortableFolder, "electron-builder's unpacked app folder (target dir) with the app executable.", PackagingAvailability.Available, null, null);
                add(PackagingFormat.PortableZip, "ZIP archive.", PackagingAvailability.NotImplemented, otherTargets, null);
                add(PackagingFormat.LinuxAppImage, "AppImage.", PackagingAvailability.NotImplemented, otherTargets, null);
                add(PackagingFormat.LinuxDeb, "Debian/Ubuntu package.", PackagingAvailability.NotImplemented, otherTargets, null);
                add(PackagingFormat.LinuxRpm, "Fedora/RHEL package.", PackagingAvailability.NotImplemented, otherTargets, null);
                break;
            default:
                add(PackagingFormat.WindowsInstallerExe, "Windows installer.", PackagingAvailability.NotImplemented, "Electron packaging for Windows is not implemented in HCG in this version.", null);
                break;
        }
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
        // Electron (macOS): the .app is always produced and verified; electron-builder builds the zip/dmg from it.
        if (settings?.Electron is not null && settings.Platform == TargetPlatform.MacOS && !set.Contains(PackagingFormat.MacAppBundle))
            set.Insert(0, PackagingFormat.MacAppBundle);
        var swiftGui = settings?.Swift?.Kind == SwiftAppKind.GuiApp;
        var needsBundle = set.Contains(PackagingFormat.MacDmg) || set.Contains(PackagingFormat.MacPkg) || set.Contains(PackagingFormat.MacAppStore)
                          || (swiftGui && set.Contains(PackagingFormat.PortableZip))
                          || (settings?.Native is { IsMacBundle: true } && set.Contains(PackagingFormat.PortableZip));
        if (needsBundle && !set.Contains(PackagingFormat.MacAppBundle))
            set.Insert(0, PackagingFormat.MacAppBundle);
        // Bundles must exist before the DMG / ZIP that wraps them.
        return [.. set.Distinct().OrderBy(f => f switch
        {
            PackagingFormat.MacAppBundle => 0,
            PackagingFormat.MacDmg => 2,
            PackagingFormat.MacPkg => 3,
            PackagingFormat.MacAppStore => 4,
            _ => 1,
        })];
    }
}
