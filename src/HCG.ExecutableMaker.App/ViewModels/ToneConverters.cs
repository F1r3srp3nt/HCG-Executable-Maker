using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace HCG.ExecutableMaker.App.ViewModels;

public static class ToneConverters
{
    public static readonly IValueConverter IsSuccess = new FuncValueConverter<string?, bool>(t => t == "success");

    /// <summary>Resolves an icon resource key (e.g. "Icon.Dashboard") to its geometry.</summary>
    public static readonly IValueConverter IconFromKey = new FuncValueConverter<string?, Geometry?>(key =>
        key is not null && Application.Current?.TryFindResource(key, out var res) == true ? res as Geometry : null);
}
