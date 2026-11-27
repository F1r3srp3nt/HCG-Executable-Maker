using Avalonia;

namespace HCG.ExecutableMaker.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // `HCGExecutableMaker --version`: identify exactly which build this is, without starting the UI.
        if (args.Contains("--version"))
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
        // `HCGExecutableMaker --rootless-deb …`: package an iOS app a build produced as a rootless jailbreak .deb (see RootlessDebCommand).
        if (args.Contains("--rootless-deb") || args.Contains("--rootless-deb-validate"))
            return RootlessDebCommand.Run(args);
        // `HCGExecutableMaker --build-host status|projects|jobs|token|run …`: HCG Server's Build Host without the UI (see BuildHostCommand).
        if (args.Contains("--build-host"))
            return BuildHostCommand.Run(args);
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
