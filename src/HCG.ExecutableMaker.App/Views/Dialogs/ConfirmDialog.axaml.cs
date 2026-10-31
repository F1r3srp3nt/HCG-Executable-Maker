using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace HCG.ExecutableMaker.App.Views.Dialogs;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog() : this("", "", "OK", null, false) { }

    public ConfirmDialog(string title, string message, string confirmText, string? cancelText, bool destructive)
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<TextBlock>("TitleText")!.Text = title;
        this.FindControl<SelectableTextBlock>("MessageText")!.Text = message;
        var confirm = this.FindControl<Button>("ConfirmButton")!;
        var cancel = this.FindControl<Button>("CancelButton")!;
        confirm.Content = confirmText;
        if (destructive)
        {
            confirm.Classes.Remove("primary");
            confirm.Classes.Add("danger");
        }
        confirm.Click += (_, _) => Close(true);
        if (cancelText is null) cancel.IsVisible = false;
        else
        {
            cancel.Content = cancelText;
            cancel.IsCancel = true;
            cancel.Click += (_, _) => Close(false);
        }
    }
}
