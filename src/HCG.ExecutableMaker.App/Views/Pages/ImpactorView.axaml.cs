using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using HCG.ExecutableMaker.App.ViewModels;

namespace HCG.ExecutableMaker.App.Views.Pages;

public partial class ImpactorView : UserControl
{
    public ImpactorView()
    {
        InitializeComponent();
        // Drop IPA: anywhere on the workspace, highlighted on the dashboard's drop zone.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => DropZone.Classes.Set("dragover", false));
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private static string? DroppedIpa(DragEventArgs e) =>
        e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).FirstOrDefault(p => p is not null &&
            (p.EndsWith(".ipa", StringComparison.OrdinalIgnoreCase) || (p.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && Directory.Exists(p))));

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var ok = DroppedIpa(e) is not null;
        e.DragEffects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        DropZone.Classes.Set("dragover", ok);
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        DropZone.Classes.Set("dragover", false);
        if (DroppedIpa(e) is { } path && DataContext is ImpactorViewModel vm) await vm.LoadIpaAsync(path);
    }
}
