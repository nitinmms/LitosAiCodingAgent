using Avalonia.Controls;

namespace Litos.Gui;

/// <summary>
/// /ptc (and the status-bar PTC button): the per-session Programmatic Tool Calling switch, the Gui
/// counterpart of the VS Code extension's /ptc popup. The warning is always on screen next to the
/// switch, so turning it on is itself the informed-consent moment (ReadMe_PTCPersistentKernel.md
/// §5.3) — no separate confirmation dialog. The window never decides the state itself: each flip
/// calls <c>setEnabled</c>, which persists the kernel_toggle entry, and the switch then shows
/// whatever that call reports back.
/// </summary>
public sealed partial class PtcWindow : Window
{
    public PtcWindow()
    {
        InitializeComponent();
        // Parameterless constructor required by the Avalonia XAML previewer/loader.
    }

    /// <param name="available">False when Litos.Kernel.Host is missing: the switch is disabled
    /// and the unavailable notice replaces the warning.</param>
    /// <param name="setEnabled">Applies a flip and returns the session's resulting state.</param>
    public static Task ShowAsync(Window owner, bool enabled, bool available, Func<bool, Task<bool>> setEnabled)
    {
        var window = new PtcWindow();
        window.PtcSwitch.IsChecked = enabled && available;
        window.PtcSwitch.IsEnabled = available;
        window.WarningText.IsVisible = available;
        window.UnavailableText.IsVisible = !available;

        var applying = false;
        window.PtcSwitch.IsCheckedChanged += async (_, _) =>
        {
            if (applying)
                return;

            applying = true;
            var requested = window.PtcSwitch.IsChecked == true;
            try
            {
                window.ErrorText.IsVisible = false;
                window.PtcSwitch.IsChecked = await setEnabled(requested);
            }
            catch (Exception ex)
            {
                // The flip wasn't saved (setEnabled persists before changing state), so show the
                // switch back where it was and say why.
                window.PtcSwitch.IsChecked = !requested;
                window.ErrorText.Text = $"Couldn't change Programmatic Tool Calling: {ex.Message}";
                window.ErrorText.IsVisible = true;
            }
            finally
            {
                applying = false;
            }
        };
        window.CloseButton.Click += (_, _) => window.Close();
        return window.ShowDialog(owner);
    }
}
