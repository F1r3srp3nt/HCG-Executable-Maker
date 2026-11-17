using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace HCG.ExecutableMaker.App.Views.Pages;

public partial class RootlessPackageView : UserControl
{
    public RootlessPackageView() => AvaloniaXamlLoader.Load(this);
}
