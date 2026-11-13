using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;

namespace HCG.ExecutableMaker.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        // Coming back to the app after an HCG checkout or plan page: ask HCG again (the return itself proves nothing).
        Activated += (_, _) =>
        {
            if (App.Services?.GetService<HCG.ExecutableMaker.Account.AccountPlanService>() is { } plan)
                _ = plan.OnReturnedAsync();
        };
    }
}
