using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using HCG.ExecutableMaker.App.ViewModels;

namespace HCG.ExecutableMaker.App.Views.Pages;

public partial class BuildWizardView : UserControl
{
    private BuildWizardViewModel? _vm;

    public BuildWizardView() => AvaloniaXamlLoader.Load(this);

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.LogAppended -= OnLogAppended;
        _vm = DataContext as BuildWizardViewModel;
        if (_vm is not null) _vm.LogAppended += OnLogAppended;
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_vm is null && DataContext is BuildWizardViewModel vm)
        {
            _vm = vm;
            _vm.LogAppended += OnLogAppended;
        }
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        if (_vm is not null) _vm.LogAppended -= OnLogAppended;
        _vm = null;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Keeps the live log scrolled to the newest line (pure view behaviour).</summary>
    private void OnLogAppended(object? sender, EventArgs e)
    {
        if (this.FindControl<ListBox>("LogList") is { } list && _vm is { LogLines.Count: > 0 } vm)
            list.ScrollIntoView(vm.LogLines.Count - 1);
    }
}
