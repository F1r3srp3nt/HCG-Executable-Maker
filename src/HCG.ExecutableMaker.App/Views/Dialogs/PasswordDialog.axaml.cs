using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace HCG.ExecutableMaker.App.Views.Dialogs;

/// <summary>Asks for a .p12 password. Returns the text, or null when cancelled. The box is cleared when the dialog closes.</summary>
public partial class PasswordDialog : Window
{
    public PasswordDialog() : this("", "") { }

    public PasswordDialog(string title, string message)
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<TextBlock>("TitleText")!.Text = title;
        this.FindControl<TextBlock>("MessageText")!.Text = message;
        var box = this.FindControl<TextBox>("PasswordBox")!;
        this.FindControl<Button>("ConfirmButton")!.Click += (_, _) =>
        {
            var text = box.Text ?? "";
            box.Text = null;
            Close(text);
        };
        this.FindControl<Button>("CancelButton")!.Click += (_, _) => { box.Text = null; Close(null); };
        Opened += (_, _) => box.Focus();
    }
}
