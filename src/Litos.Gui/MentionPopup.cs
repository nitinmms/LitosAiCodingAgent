using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;

namespace Litos.Gui;

/// <summary>
/// Live-filtered "@"-mention dropdown shown in a Flyout anchored above InputBox — same shape as
/// CommandMenuPopup (see that file's remarks for why Flyout.ShowAt(Control) is used instead of a
/// bare Popup: a hand-rolled Popup targeting an unrelated UserControl never measured or placed
/// correctly in testing). Kept as a separate control from CommandMenuPopup rather than a shared
/// base: the two are triggered by different characters, never open at the same time, and the row
/// template differs (icon+path here vs icon+title+subtitle there) — the duplication is a handful
/// of near-identical members, not enough to justify a shared abstraction.
///
/// As with CommandMenuPopup, there is no FilterBox here — filtering is driven by InputBox's own
/// text (whatever follows the active "@"), so the user's caret and typing never leave InputBox.
/// </summary>
public sealed partial class MentionPopup : UserControl
{
    internal sealed record MentionEntry(string Icon, string Path)
    {
        public override string ToString() => Path;
    }

    private readonly Flyout _host;

    /// <summary>Raised when the user accepts an entry via Enter/Tab/click/double-tap.</summary>
    internal event Action<MentionEntry>? Accepted;

    public MentionPopup()
    {
        InitializeComponent();
        ItemsList.DoubleTapped += (_, _) => AcceptSelected();

        _host = new Flyout
        {
            Content = this,
            Placement = PlacementMode.Top,
            ShowMode = FlyoutShowMode.Standard,
        };
        _host.Closed += (_, _) => _isOpen = false;
    }

    private static IReadOnlyList<MentionEntry> ToEntries(IReadOnlyList<string> paths) =>
        paths.Select(p => new MentionEntry(p.EndsWith('/') ? "📁" : "📄", p)).ToList();

    /// <summary>
    /// Re-applies the filter for the given typed token (InputBox's text between the active "@"
    /// and the caret) and shows/hides the popup accordingly. Called by MainWindow on every
    /// keystroke while the popup is open; an empty result set closes it instead of showing an
    /// empty list, same as CommandMenuPopup.UpdateFilter.
    /// </summary>
    public void UpdateFilter(IReadOnlyList<string> index, string token)
    {
        var filtered = FileMentionIndex.Filter(index, token);
        if (filtered.Count == 0)
        {
            Close();
            return;
        }

        ItemsList.ItemsSource = ToEntries(filtered);
        ItemsList.SelectedIndex = 0;
    }

    private bool _isOpen;

    public void Open(Control anchor)
    {
        _isOpen = true;
        _host.ShowAt(anchor);
    }

    public void Close() => _host.Hide();

    public bool IsOpen => _isOpen;

    /// <summary>
    /// Lets InputBox's own KeyDown handler forward navigation keys to the open popup. Returns
    /// true if the key was consumed (caller should set e.Handled), mirroring
    /// CommandMenuPopup.HandleFilterKeyDown.
    /// </summary>
    public bool HandleFilterKeyDown(Key key)
    {
        if (!IsOpen)
            return false;

        switch (key)
        {
            case Key.Down:
                ItemsList.SelectedIndex = Math.Min(ItemsList.SelectedIndex + 1, ItemsList.ItemCount - 1);
                return true;
            case Key.Up:
                ItemsList.SelectedIndex = Math.Max(ItemsList.SelectedIndex - 1, 0);
                return true;
            case Key.Enter:
            case Key.Tab:
                AcceptSelected();
                return true;
            case Key.Escape:
                Close();
                return true;
            default:
                return false;
        }
    }

    private void AcceptSelected()
    {
        if (ItemsList.SelectedItem is MentionEntry entry)
        {
            Close();
            Accepted?.Invoke(entry);
        }
        else
        {
            Close();
        }
    }
}
