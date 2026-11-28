using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace HCG.ExecutableMaker.App.Views.Pages;

/// <summary>The HCG Server Build Host panel of the New Build wizard's Build Provider; DataContext is a BuildHostPanelViewModel.</summary>
public partial class BuildHostPanel : UserControl
{
    public BuildHostPanel() => AvaloniaXamlLoader.Load(this);
}
