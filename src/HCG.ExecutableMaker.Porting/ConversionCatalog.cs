using HCG.ExecutableMaker.Domain;

namespace HCG.ExecutableMaker.Porting;

/// <summary>
/// The support matrix: every source framework HCG recognizes, per mobile target and conversion mode, with what is
/// implemented today and the precise requirements of what is not. Shown as-is in the conversion workspace, so an option
/// that is listed is never mistaken for one that works.
/// </summary>
public static class ConversionCatalog
{
    public const string SwiftUi = "SwiftUI app (Swift package or macOS Xcode project)";
    public const string AppKit = "AppKit / Objective-C macOS app";
    public const string CmakeConsole = "C/C++ program (CMake, POSIX)";
    public const string ViteReact = "Web app (Vite + React)";
    public const string OtherWeb = "Web app (Vue, Svelte, Angular, other Vite apps)";
    public const string ServerRendered = "Server-rendered web app (Next.js, Nuxt, Remix, SvelteKit, Astro)";
    public const string Electron = "Electron desktop app";
    public const string NodeServer = "Node.js server or command-line tool";
    public const string DotNetWindows = ".NET Windows desktop app (WinForms, WPF)";
    public const string Avalonia = ".NET Avalonia desktop app";
    public const string DotNetConsole = ".NET console or server app";
    public const string Python = "Python application";
    public const string GoRust = "Go or Rust application";
    public const string Java = "Java desktop application (Swing, JavaFX)";
    public const string GtkQt = "C/C++ desktop app (GTK, Qt, SDL)";
    public const string HcgServer = "HCG Server (desktop agent and workloads)";

    private static SupportRow Row(string source, string lang, MobileTarget target, ConversionMode mode, SupportStatus status, PortingPath? path, string summary, params string[] requirements) =>
        new(source, lang, target, mode, status, path, summary, requirements);

    private static readonly string[] CompanionRequirements =
    [
        "A documented HTTP API on the desktop service (an OpenAPI description of the operations the phone may call). HCG generates the mobile interface from that contract; it does not invent endpoints.",
        "Pairing: the phone and the computer are linked by the signed-in HCG account (HCG Connect), never by a shared password or a key embedded in the app.",
        "Authorization stays on the desktop service and HCG's central API: every request is checked there; the app holds only the user's own session in the OS secure store.",
        "Transport: HTTPS through an HCG relay or the local network, with the computer's identity verified (no plain HTTP, no disabled certificate checks).",
        "No server secrets in the mobile app: service keys, database credentials and signing material stay on the computer.",
    ];

    public static IReadOnlyList<SupportRow> Matrix { get; } =
    [
        Row(SwiftUi, "Swift", MobileTarget.Ios, ConversionMode.MobileAdaptation, SupportStatus.Implemented, PortingPath.MacOSToIOS,
            "A new iPhone/iPad Xcode project (iOS 17+). Shared SwiftUI views and logic are reused; macOS-only APIs are adapted behind platform guards or reported as blockers."),
        Row(SwiftUi, "Swift", MobileTarget.Android, ConversionMode.MobileAdaptation, SupportStatus.NotPlanned, null,
            "SwiftUI does not run on Android. The interface would have to be rewritten (for example in Jetpack Compose); only platform-neutral logic could be shared, through Swift for Android, which HCG does not build."),
        Row(AppKit, "Objective-C, Swift (AppKit)", MobileTarget.Ios, ConversionMode.MobileAdaptation, SupportStatus.NotPlanned, null,
            "AppKit interfaces (NSView, NSWindow, XIBs, storyboards) have no automated UIKit equivalent. Move the interface to SwiftUI first; the SwiftUI path then applies."),
        Row(CmakeConsole, "C, C++", MobileTarget.Android, ConversionMode.MobileAdaptation, SupportStatus.Implemented, PortingPath.LinuxToAndroid,
            "An Android Gradle project built with the NDK. The program's own main() runs unchanged in a touch console (output, input field, editable arguments)."),
        Row(CmakeConsole, "C, C++", MobileTarget.Ios, ConversionMode.MobileAdaptation, SupportStatus.Planned, null,
            "Planned: the same console host as Android, as an Xcode project that compiles the CMake target's sources.",
            "An Xcode project generator for C/C++ sources (the macOS → iOS path already writes Xcode projects).",
            "A SwiftUI console host that runs main() on a thread and redirects stdin/stdout.",
            "Rules for iOS's POSIX subset: no fork/exec/system(), sandboxed file paths."),
        Row(ViteReact, "TypeScript, JavaScript", MobileTarget.Android, ConversionMode.WebMobilePackaging, SupportStatus.Implemented, PortingPath.WebToAndroid,
            "The production web build packaged in Capacitor's Android runtime (WebView). Server calls, sign-in redirects, storage and device APIs are classified and listed, not faked."),
        Row(ViteReact, "TypeScript, JavaScript", MobileTarget.Ios, ConversionMode.WebMobilePackaging, SupportStatus.Implemented, PortingPath.WebToIOS,
            "The production web build packaged in Capacitor's iOS runtime (WKWebView, Swift Package Manager). Compiling needs a Mac with Xcode."),
        Row(OtherWeb, "TypeScript, JavaScript", MobileTarget.Android, ConversionMode.WebMobilePackaging, SupportStatus.Planned, null,
            "Planned: the same Capacitor packaging; only the framework detection and build checks are missing.",
            "Framework rules for Vue, Svelte and Angular builds (output folder, base path, router mode).",
            "Fixtures and CI runs per framework before it is offered."),
        Row(OtherWeb, "TypeScript, JavaScript", MobileTarget.Ios, ConversionMode.WebMobilePackaging, SupportStatus.Planned, null,
            "Planned: as for Android, plus a Mac with Xcode to compile.",
            "Framework rules for Vue, Svelte and Angular builds.", "Fixtures and CI runs per framework."),
        Row(ServerRendered, "TypeScript, JavaScript", MobileTarget.Android, ConversionMode.MobileCompanion, SupportStatus.Planned, null,
            "Pages are rendered by a server, which a phone does not run. Either export a static build (then web packaging applies) or keep the server and use companion mode.",
            CompanionRequirements),
        Row(ServerRendered, "TypeScript, JavaScript", MobileTarget.Ios, ConversionMode.MobileCompanion, SupportStatus.Planned, null,
            "As for Android: static export → web packaging, or keep the server → companion mode.", CompanionRequirements),
        Row(Electron, "TypeScript, JavaScript", MobileTarget.Android, ConversionMode.WebMobilePackaging, SupportStatus.Planned, null,
            "Planned: package the renderer like a web app.",
            "Separate the renderer build from the main process (the renderer must build without Electron).",
            "Replace each preload/IPC call (file system, shell, menus, windows) with a web or Capacitor equivalent, or move it to a companion service.",
            "Work the main process does (Node.js, child processes) needs companion mode."),
        Row(Electron, "TypeScript, JavaScript", MobileTarget.Ios, ConversionMode.WebMobilePackaging, SupportStatus.Planned, null,
            "Planned: as for Android; compiling needs a Mac with Xcode.",
            "Separate the renderer build from the main process.", "Replace preload/IPC calls; main-process work needs companion mode."),
        Row(NodeServer, "TypeScript, JavaScript", MobileTarget.Android, ConversionMode.MobileCompanion, SupportStatus.Planned, null,
            "A server or command-line tool keeps running on the computer; the phone gets a companion interface.", CompanionRequirements),
        Row(NodeServer, "TypeScript, JavaScript", MobileTarget.Ios, ConversionMode.MobileCompanion, SupportStatus.Planned, null,
            "As for Android.", CompanionRequirements),
        Row(HcgServer, "—", MobileTarget.Android, ConversionMode.MobileCompanion, SupportStatus.Planned, null,
            "Intended companion: phones manage connected computers and open permitted files; the desktop agents keep running the server workloads.",
            [.. CompanionRequirements, "The HCG Server agent API (computers, workloads, file listing and transfer with per-path permissions) published as a versioned contract.",
             "File access limited to folders the computer's owner shares, enforced by the agent; large transfers resumable."]),
        Row(HcgServer, "—", MobileTarget.Ios, ConversionMode.MobileCompanion, SupportStatus.Planned, null,
            "As for Android.", [.. CompanionRequirements, "The HCG Server agent API published as a versioned contract."]),
        Row(Avalonia, "C#", MobileTarget.Android, ConversionMode.MobileAdaptation, SupportStatus.Planned, null,
            "Planned: Avalonia runs on Android with a mobile head project.",
            "An Android head (Avalonia.Android) and a single-view application lifetime instead of the desktop lifetime.",
            "Rules for desktop-only APIs (windows, dialogs, file pickers → storage provider) and for touch layouts.",
            "The .NET Android workload (Mobile Build Studio builds .NET mobile projects)."),
        Row(Avalonia, "C#", MobileTarget.Ios, ConversionMode.MobileAdaptation, SupportStatus.Planned, null,
            "Planned: an iOS head (Avalonia.iOS); compiling needs a Mac with Xcode and the .NET iOS workload.",
            "An iOS head and a single-view lifetime.", "Rules for desktop-only APIs and touch layouts."),
        Row(DotNetWindows, "C#, VB.NET", MobileTarget.Android, ConversionMode.MobileAdaptation, SupportStatus.NotPlanned, null,
            "WinForms and WPF run only on Windows. The interface would be rewritten in .NET MAUI or Avalonia; class libraries without UI can be reused there. Mobile Build Studio builds existing .NET MAUI projects."),
        Row(DotNetWindows, "C#, VB.NET", MobileTarget.Ios, ConversionMode.MobileAdaptation, SupportStatus.NotPlanned, null,
            "As for Android: rewrite the interface in .NET MAUI or Avalonia."),
        Row(DotNetConsole, "C#", MobileTarget.Android, ConversionMode.MobileCompanion, SupportStatus.Planned, null,
            "A console or server program keeps running on the computer; companion mode gives the phone an interface to it.", CompanionRequirements),
        Row(DotNetConsole, "C#", MobileTarget.Ios, ConversionMode.MobileCompanion, SupportStatus.Planned, null, "As for Android.", CompanionRequirements),
        Row(GtkQt, "C, C++", MobileTarget.Android, ConversionMode.MobileAdaptation, SupportStatus.NotPlanned, null,
            "GTK, X11 and SDL desktop interfaces are not converted. Qt apps can target Android with Qt's own toolchain (Qt for Android), which HCG does not build."),
        Row(GtkQt, "C, C++", MobileTarget.Ios, ConversionMode.MobileAdaptation, SupportStatus.NotPlanned, null,
            "As for Android (Qt for iOS is Qt's own toolchain)."),
        Row(Python, "Python", MobileTarget.Android, ConversionMode.MobileCompanion, SupportStatus.Planned, null,
            "Python programs keep running on the computer; companion mode gives the phone an interface. (Packaging Python itself for mobile, e.g. with BeeWare, is not planned.)", CompanionRequirements),
        Row(Python, "Python", MobileTarget.Ios, ConversionMode.MobileCompanion, SupportStatus.Planned, null, "As for Android.", CompanionRequirements),
        Row(GoRust, "Go, Rust", MobileTarget.Android, ConversionMode.MobileCompanion, SupportStatus.Planned, null,
            "Not converted to a mobile app. As a server or tool it can keep running on the computer with a companion interface.", CompanionRequirements),
        Row(GoRust, "Go, Rust", MobileTarget.Ios, ConversionMode.MobileCompanion, SupportStatus.Planned, null, "As for Android.", CompanionRequirements),
        Row(Java, "Java, Kotlin", MobileTarget.Android, ConversionMode.MobileAdaptation, SupportStatus.NotPlanned, null,
            "Swing and JavaFX do not run on Android. Non-UI Java code can be reused in a native Android app written by hand."),
        Row(Java, "Java, Kotlin", MobileTarget.Ios, ConversionMode.MobileAdaptation, SupportStatus.NotPlanned, null, "Swing and JavaFX do not run on iOS."),
    ];

    public static IEnumerable<SupportRow> For(string sourceFramework) => Matrix.Where(r => r.SourceFramework == sourceFramework);

    public static SupportRow? For(PortingPath path) => Matrix.FirstOrDefault(r => r.Path == path);
}
