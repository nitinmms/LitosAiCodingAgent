using Avalonia.Controls;

namespace Litos.Gui;

/// <summary>
/// Plain yes/no modal — a plain Window opened via a static async factory, same convention as
/// ListPickerWindow. Introduced for /update's "this will close and relaunch the app" confirmation,
/// the first place in Litos.Gui that needed a generic confirm rather than a picker or a form.
/// </summary>
public sealed partial class ConfirmationDialog : Window
{
    public ConfirmationDialog()
    {
        InitializeComponent();
        // Parameterless constructor required by the Avalonia XAML previewer/loader.
    }

    public static Task<bool> ShowAsync(Window owner, string title, string message)
    {
        var window = new ConfirmationDialog { Title = title };
        window.MessageText.Text = message;
        window.ConfirmButton.Click += (_, _) => window.Close(true);
        window.CancelButton.Click += (_, _) => window.Close(false);
        return window.ShowDialog<bool>(owner);
    }
}
