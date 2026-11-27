using Avalonia;

namespace HCG.ExecutableMaker.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Commands are matched before `--version`: a command's own `--version` (e.g. `--package-windows … --version 1.2.0`) is
        // the product's version, never a request for Maker's (see Core.CommandLine).
        var command = Core.CommandLine.CommandOf(args);
        // `HCGExecutableMaker --version`: identify exactly which build this is, without starting the UI.
        if (command == Core.CommandLine.MakerVersion)
        {
            Console.WriteLine(Core.HostInfo.BuildIdentity);
            Console.WriteLine(Account.HcgConnectConfiguration.BuiltInDescription(Account.HcgConnectConfiguration.SelectedEnvironment()));
            Console.WriteLine("Executable: " + Core.HostInfo.ExecutablePath);
            if (Core.HostInfo.AppBundlePath is { } app) Console.WriteLine("App bundle: " + app);
            return 0;
        }
        // `HCGExecutableMaker --toolchain-report`: print what HCG detects (the same detection the Toolchains page uses),
        // e.g. to compare a Finder launch with a Terminal launch. Read-only; applies HCG's saved Xcode choice like the app.
        if (args.Contains("--toolchain-report"))
            return ToolchainReportCommand.Run();
        // `HCGExecutableMaker --ship-to-store …`: upload a release to HCG Store as a private draft (see ShipToStoreCommand).
        if (args.Contains("--ship-to-store"))
            return ShipToStoreCommand.Run(args);
        // `HCGExecutableMaker --release-passport --app X.app --file X.app.zip …`: the build half for an existing macOS app archive.
        if (args.Contains("--release-passport"))
            return ReleasePassportCommand.Run(args);
        // `HCGExecutableMaker --ship-to-repo submit|show|approve|prepare …`: HCG Repo's Candidate Release Manager for a handoff (see RootlessDebCommand).
        if (args.Contains("--ship-to-repo"))
            return RootlessDebCommand.ShipToRepoCli(args);
        // `HCGExecutableMaker --rootless-deb …`: package an iOS app a build produced as a rootless jailbreak .deb (see RootlessDebCommand).
        if (args.Contains("--rootless-deb") || args.Contains("--rootless-deb-validate"))
            return RootlessDebCommand.Run(args);
        // `HCGExecutableMaker --xcode-mac <folder|zip> …`: native macOS Xcode builds without the UI (see XcodeMacCommand).
        if (args.Contains("--xcode-mac"))
            return XcodeMacCommand.Run(args);
        // `HCGExecutableMaker --package-windows …`: package an app (built here, or an already-built folder) with its own identity.
        if (args.Contains("--package-windows"))
            return WindowsPackagingCommand.Run(args);
        // `HCGExecutableMaker --windows-signing-doctor <file|zip|folder>`: read-only Authenticode report.
        if (args.Contains("--windows-signing-doctor"))
            return WindowsPackagingCommand.SigningDoctor(args);
        // `HCGExecutableMaker --authenticode-sign <file> --thumbprint … --timestamp-url … --approve <sha256>`: sign with explicit approval.
        if (args.Contains("--authenticode-sign"))
            return WindowsPackagingCommand.Sign(args);
        // `HCGExecutableMaker --mac-signing-doctor <App.app>`: read-only signing report of a macOS app.
        if (args.Contains("--mac-signing-doctor"))
            return XcodeMacCommand.SigningDoctor(args);
        // `HCGExecutableMaker --impactor <command> …`: HCG Impactor (inspect, sign, devices, install, diagnose) without the UI.
        if (args.Contains("--impactor"))
            return ImpactorCommand.Run(args);
        // `HCGExecutableMaker --hcg-session status|sign-in|sign-out`: the HCG account services without the UI (no tokens printed).
        if (args.Contains("--hcg-session"))
            return HcgSessionCommand.Run(args);
        // `--page <key>` opens that page first (e.g. `open HCG\ Executable\ Maker.app --args --page account`).
        var page = args.SkipWhile(a => a != "--page").Skip(1).FirstOrDefault();
        if (page is not null && System.Text.RegularExpressions.Regex.IsMatch(page, "^[a-z-]{1,32}$")) StartPage = page;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>The page shown first (dashboard unless <c>--page</c> names another).</summary>
    public static string StartPage { get; private set; } = "dashboard";

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
