using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace HCG.ExecutableMaker.App.Views.Pages;

/// <summary>The Build Readiness Center panel (Toolchains page and New Build wizard); DataContext is a BuildReadinessPanelViewModel.</summary>
public partial class BuildReadinessPanel : UserControl
{
    public BuildReadinessPanel() => AvaloniaXamlLoader.Load(this);
}
