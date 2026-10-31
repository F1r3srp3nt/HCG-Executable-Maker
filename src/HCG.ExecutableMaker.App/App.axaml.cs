using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using HCG.ExecutableMaker.App.Services;
using HCG.ExecutableMaker.App.ViewModels;
using HCG.ExecutableMaker.App.Views;
using HCG.ExecutableMaker.Core;
using Microsoft.Extensions.DependencyInjection;

namespace HCG.ExecutableMaker.App;

public partial class App : Application
{
    public static IServiceProvider? Services { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var paths = AppPaths.CreateDefault();
        paths.EnsureCreated();
        var services = Composition.Build(paths);
        Services = services;
        DataTemplates.Add(new ViewLocator());

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow { DataContext = services.GetRequiredService<MainWindowViewModel>() };
            services.GetRequiredService<MainWindowProvider>().Window = window;
            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) => services.Dispose();
            _ = services.GetRequiredService<ThemeService>().ApplySavedAsync();
            _ = services.GetRequiredService<MainWindowViewModel>().InitializeAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
