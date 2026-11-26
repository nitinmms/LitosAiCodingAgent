using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Litos.Gui.Buzzies;

/// <summary>
/// A small notification in the bottom-right corner of the screen, above other windows, for a
/// routine that finished or failed while the user was elsewhere (Implement_Buzzies.md, Phase 1).
/// Drawn by Litos itself rather than through the operating system's notification centre, so it
/// looks and behaves the same on Windows and macOS and needs no extra packages; it doesn't take
/// focus, closes itself after a while, and clicking it runs <c>onClick</c> (opening the run).
/// Several stack upwards.
///
/// Clicking closes every toast first, then hands focus back to Litos: a toast is topmost and does
/// not activate, so when the window it opened was coming back from the tray it would otherwise
/// reappear underneath the toast that was just clicked.
/// </summary>
public sealed class ToastWindow : Window
{
    private const double ToastWidth = 360;
    private const double Gap = 10;
    private static readonly List<ToastWindow> Open = [];

    private readonly DispatcherTimer _autoClose;

    public ToastWindow(string title, string body, string color, Action? onClick, TimeSpan? lifetime = null)
    {
        Width = ToastWidth;
        SizeToContent = SizeToContent.Height;
        WindowDecorations = WindowDecorations.None;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        CanResize = false;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];

        var close = new Button
        {
            Content = "×",
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Foreground = new SolidColorBrush(Color.Parse("#9A9A9A")),
        };
        close.Click += (_, e) =>
        {
            e.Handled = true;
            Close();
        };

        var text = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.WhiteSmoke },
                new TextBlock
                {
                    Text = body, TextWrapping = TextWrapping.Wrap, MaxLines = 4, TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = new SolidColorBrush(Color.Parse("#C8C8C8")), FontSize = 12,
                },
            },
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        var avatar = new BuzzyAvatar(color, 40) { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 10, 0) };
        grid.Children.Add(avatar);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        Grid.SetColumn(close, 2);
        grid.Children.Add(close);

        Content = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#252526")),
            BorderBrush = new SolidColorBrush(Color.Parse(color)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10),
            Child = grid,
            Cursor = new Cursor(StandardCursorType.Hand),
        };

        PointerPressed += (_, _) =>
        {
            // Closed before the action runs, so whatever it shows is not hidden behind a topmost toast.
            var main = (Application.Current?.ApplicationLifetime as
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (main is not null)
                DismissToastsAndActivate(main);
            else
                Close();

            onClick?.Invoke();
        };

        _autoClose = new DispatcherTimer { Interval = lifetime ?? TimeSpan.FromSeconds(12) };
        _autoClose.Tick += (_, _) => Close();
        Opened += (_, _) =>
        {
            Open.Add(this);
            Restack();
            _autoClose.Start();
        };
        Closed += (_, _) =>
        {
            _autoClose.Stop();
            Open.Remove(this);
            Restack();
        };
    }

    /// <summary>
    /// Closes every open toast and brings <paramref name="window"/> to the front. A toast is topmost
    /// and never activates, so a window shown from the tray while one is up comes back behind it,
    /// leaving the user looking at the toast they just clicked and no chat.
    /// </summary>
    private static void DismissToastsAndActivate(Window window)
    {
        foreach (var toast in Open.ToArray())
            toast.Close();

        if (!window.IsVisible)
            window.Show();
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
    }

    /// <summary>Keeps open toasts stacked upwards from the bottom-right of the working area.</summary>
    private static void Restack()
    {
        double bottomOffset = Gap;
        foreach (var toast in Open)
        {
            var screen = toast.Screens.Primary ?? toast.Screens.All.FirstOrDefault();
            if (screen is null)
                continue;
            var area = screen.WorkingArea;
            var scale = screen.Scaling;
            var height = toast.Bounds.Height > 0 ? toast.Bounds.Height : 90;
            var x = area.Right - (int)((ToastWidth + Gap) * scale);
            var y = area.Bottom - (int)((height + bottomOffset) * scale);
            toast.Position = new PixelPoint(x, y);
            bottomOffset += height + Gap;
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        Restack();
    }
}
