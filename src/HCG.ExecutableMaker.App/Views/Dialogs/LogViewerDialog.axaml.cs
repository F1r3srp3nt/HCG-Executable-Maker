using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;

namespace HCG.ExecutableMaker.App.Views.Dialogs;

public partial class LogViewerDialog : Window
{
    public LogViewerDialog() : this("", "", "") { }

    public LogViewerDialog(string title, string path, string text)
    {
        AvaloniaXamlLoader.Load(this);
        Title = title;
        this.FindControl<TextBlock>("TitleText")!.Text = title;
        this.FindControl<TextBlock>("PathText")!.Text = path;
        this.FindControl<TextBox>("LogText")!.Text = text;
        this.FindControl<Button>("CloseButton")!.Click += (_, _) => Close();
        this.FindControl<Button>("CopyButton")!.Click += async (_, _) =>
        {
            if (Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
        };
    }
}
