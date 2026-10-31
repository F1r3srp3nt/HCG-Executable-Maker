using Avalonia;
using Avalonia.Controls;
using HCG.ExecutableMaker.App.ViewModels;

namespace HCG.ExecutableMaker.App.Controls;

/// <summary>Pill-shaped status label. Tone maps to the Border.chip.{success|warning|error|info} styles.</summary>
public sealed class StatusChip : Border
{
    public static readonly StyledProperty<StatusTag?> StatusProperty = AvaloniaProperty.Register<StatusChip, StatusTag?>(nameof(Status));
    private static readonly string[] Tones = ["success", "warning", "error", "info"];
    private readonly TextBlock _text = new();

    public StatusChip()
    {
        Classes.Add("chip");
        Child = _text;
    }

    protected override Type StyleKeyOverride => typeof(Border);

    public StatusTag? Status
    {
        get => GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != StatusProperty) return;
        var tag = change.GetNewValue<StatusTag?>();
        _text.Text = tag?.Text ?? "";
        foreach (var t in Tones) Classes.Set(t, tag?.Tone == t);
    }
}
