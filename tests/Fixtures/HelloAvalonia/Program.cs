using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace HelloAvalonia;

public static class Program
{
    [System.STAThread]
    public static void Main(string[] args) =>
        AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
}

public sealed class App : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new Window
            {
                Title = "Hello Avalonia",
                Width = 480,
                Height = 240,
                Content = new TextBlock { Text = "Built by HCG Executable Maker", Margin = new Thickness(24) },
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
