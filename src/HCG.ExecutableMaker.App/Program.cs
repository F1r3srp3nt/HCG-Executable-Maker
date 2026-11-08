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
            Console.WriteLine("Executable: " + Core.HostInfo.ExecutablePath);
            if (Core.HostInfo.AppBundlePath is { } app) Console.WriteLine("App bundle: " + app);
            return 0;
        }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
