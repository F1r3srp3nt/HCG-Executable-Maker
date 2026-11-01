using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using HCG.ExecutableMaker.App.ViewModels;

namespace HCG.ExecutableMaker.App.Views.Pages;

public partial class MobileBuildStudioView : UserControl
{
    private MobileBuildStudioViewModel? _vm;

    public MobileBuildStudioView() => AvaloniaXamlLoader.Load(this);

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.LogAppended -= OnLogAppended;
        _vm = DataContext as MobileBuildStudioViewModel;
        if (_vm is not null) _vm.LogAppended += OnLogAppended;
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        if (_vm is not null) _vm.LogAppended -= OnLogAppended;
        _vm = null;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_vm is null && DataContext is MobileBuildStudioViewModel vm) { _vm = vm; _vm.LogAppended += OnLogAppended; }
    }

    private void OnLogAppended(object? sender, EventArgs e)
    {
        if (this.FindControl<ListBox>("LogList") is { } list && _vm is { LogLines.Count: > 0 } vm)
            list.ScrollIntoView(vm.LogLines.Count - 1);
    }
}
