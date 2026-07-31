using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;

namespace Litos.Gui;

/// <summary>
/// Inline "+"/leading-"/" command menu shown in a Flyout anchored above InputBox — an Avalonia-
/// Flyout equivalent of ListPickerWindow's modal filter-box+ListBox shape (see that file's
/// remarks), purpose-built instead of reusing ListPickerWindow so the existing modal picker used
/// by /resume, /provider, /model, /skills, /branch isn't put at risk for the sake of this new,
/// differently-shaped (anchored, non-modal, icon+title+subtitle row) caller. Built on Flyout
/// rather than a bare Popup with Child set manually: a hand-rolled Popup targeting an unrelated
/// UserControl never measured or placed correctly in testing (rendered as an empty, mis-anchored
/// sliver), whereas Flyout's ShowAt(Control) is the same well-tested PopupFlyoutBase machinery
/// Avalonia's own ContextMenu/AutoCompleteBox rely on for anchored custom content.
///
/// Unlike ListPickerWindow there is no FilterBox here — filtering is driven by InputBox's own
/// text (whatever follows a leading "/") so the user's caret and typing never leave InputBox.
/// The popup owns only the ListBox + keyboard navigation; MainWindow feeds it filter text and
/// reads back Up/Down/Enter/Escape via HandleFilterKeyDown so InputBox's own KeyDown handler can
/// forward relevant keys to it.
/// </summary>
public sealed partial class CommandMenuPopup : UserControl
{
    /// <summary>
    /// One row in the menu: either a real SlashCommand or the synthetic "Attach file(s)…" action
    /// (Kind distinguishes the two since only a SlashCommand carries a Command to dispatch).
    /// </summary>
    internal sealed record MenuEntry(string Icon, string Title, string Subtitle, MenuEntryKind Kind, SlashCommand? Command)
    {
        public override string ToString() => Title;
    }

    internal enum MenuEntryKind { Attach, Command }

    private static readonly IReadOnlyList<MenuEntry> AllEntries = BuildAllEntries();

    private readonly Flyout _host;

    /// <summary>Raised when the user accepts an entry via Enter/click/double-tap.</summary>
    internal event Action<MenuEntry>? Accepted;

    /// <summary>Raised when the menu should close without a selection (Escape, or filter matched nothing to show).</summary>
    public event Action? Dismissed;

    public CommandMenuPopup()
    {
        InitializeComponent();
        ItemsList.DoubleTapped += (_, _) => AcceptSelected();

        // Flyout (not a hand-built Popup) is what actually anchors and sizes correctly here —
        // Avalonia's own ContextMenu/AutoCompleteBox use the same PopupFlyoutBase machinery, and
        // it resolves placement/measurement against PlacementTarget reliably, unlike a bare Popup
        // with Child set to an unrelated UserControl (that combination rendered as an empty,
        // mis-positioned sliver in manual testing — Placement/PlacementTarget were silently
        // ignored and content never measured to its real size).
        _host = new Flyout
        {
            Content = this,
            Placement = PlacementMode.Top,
            ShowMode = FlyoutShowMode.Standard,
        };
        _host.Closed += (_, _) =>
        {
            _isOpen = false;
            Dismissed?.Invoke();
        };
    }

    private static IReadOnlyList<MenuEntry> BuildAllEntries()
    {
        var entries = new List<MenuEntry> { new("📎", "Attach file(s)…", "Attach a file, folder, or URL", MenuEntryKind.Attach, Command: null) };
        entries.AddRange(SlashCommands.All
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => new MenuEntry(IconFor(c.Name), c.Name, c.Description, MenuEntryKind.Command, c)));
        return entries;
    }

    private static string IconFor(string commandName) => commandName switch
    {
        "/new" => "✨",
        "/resume" => "⏪",
        "/attach" => "📎",
        "/provider" => "🔌",
        "/model" => "🤖",
        "/skills" or "/skill" => "🧩",
        "/branch" => "🔀",
        _ => "▸",
    };

    /// <summary>
    /// Pure filter logic behind the popup's live narrowing, split out so it's testable without an
    /// Avalonia control tree (same reasoning as MainWindow.FindBranchPoints). Matches on either
    /// the command name or its description, case-insensitively, same substring-Contains idiom
    /// ListPickerWindow.ApplyFilter already uses. A blank/"/" query returns everything, including
    /// the Attach entry, so opening the menu with nothing typed yet shows the full action list.
    /// </summary>
    internal static IReadOnlyList<MenuEntry> Filter(string query)
    {
        var needle = query.TrimStart('/');
        if (string.IsNullOrEmpty(needle))
            return AllEntries;

        return AllEntries
            .Where(e => e.Title.Contains(needle, StringComparison.OrdinalIgnoreCase)
                     || e.Subtitle.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Re-applies the filter for the given raw typed text (InputBox's content from the triggering
    /// "/" onward) and shows/hides the popup accordingly. Called by MainWindow on every keystroke
    /// while the menu is open; an empty result set closes the menu instead of showing an empty list
    /// (typing something that matches nothing means the user has moved on to plain text).
    /// </summary>
    public void UpdateFilter(string query)
    {
        var filtered = Filter(query);
        if (filtered.Count == 0)
        {
            Close();
            return;
        }

        ItemsList.ItemsSource = filtered;
        ItemsList.SelectedIndex = 0;
    }

    private bool _isOpen;

    public void Open(Control anchor)
    {
        ItemsList.ItemsSource = AllEntries;
        ItemsList.SelectedIndex = 0;
        _isOpen = true;
        _host.ShowAt(anchor);
    }

    public void Close() => _host.Hide();

    public bool IsOpen => _isOpen;

    /// <summary>
    /// Lets InputBox's own KeyDown handler forward navigation keys to the open menu. Returns true
    /// if the key was consumed (caller should set e.Handled), mirroring ListPickerWindow's
    /// OnFilterBoxKeyDown switch but without owning the TextBox itself.
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
        if (ItemsList.SelectedItem is MenuEntry entry)
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
