using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Litos.Gui;

/// <summary>
/// Filterable single-select list picker, shown modally via ShowDialog. Avalonia equivalent of
/// Console's Terminal.Gui-based PickerDialog&lt;T&gt; (src/Litos.Console/Terminal/PickerDialog.cs) —
/// same "type to filter, pick one of N" shape, backed by a plain ListBox instead of Terminal.Gui's
/// Autocomplete, and returned via ShowDialog's own awaitable rather than a hand-rolled
/// Begin/Iteration/TaskCompletionSource loop (Avalonia's Window.ShowDialog already awaits properly
/// from a caller already inside the UI event loop).
/// </summary>
public sealed partial class ListPickerWindow : Window
{
    private IReadOnlyList<object> _allItems = [];
    private Func<object, string> _labelSelector = static o => o.ToString() ?? "";

    public ListPickerWindow()
    {
        InitializeComponent();
        FilterBox.TextChanged += (_, _) => ApplyFilter();
        FilterBox.KeyDown += OnFilterBoxKeyDown;
        ItemsList.DoubleTapped += (_, _) => Accept();
    }

    public static Task<T?> PickAsync<T>(Window owner, string title, IReadOnlyList<T> items, Func<T, string> labelSelector, string? initialFilter = null)
        where T : class
    {
        var window = new ListPickerWindow
        {
            Title = title,
            _allItems = items,
            _labelSelector = o => labelSelector((T)o),
        };
        window.FilterBox.Text = initialFilter;
        window.ApplyFilter();
        return window.ShowDialog<T?>(owner);
    }

    private void ApplyFilter()
    {
        var filter = FilterBox.Text;
        var filtered = string.IsNullOrWhiteSpace(filter)
            ? _allItems
            : _allItems.Where(i => _labelSelector(i).Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        ItemsList.ItemsSource = filtered.Select(i => new ListPickerItem(i, _labelSelector(i))).ToList();
        if (ItemsList.ItemCount > 0)
            ItemsList.SelectedIndex = 0;
    }

    private void OnFilterBoxKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                ItemsList.SelectedIndex = Math.Min(ItemsList.SelectedIndex + 1, ItemsList.ItemCount - 1);
                e.Handled = true;
                break;
            case Key.Up:
                ItemsList.SelectedIndex = Math.Max(ItemsList.SelectedIndex - 1, 0);
                e.Handled = true;
                break;
            case Key.Enter:
                Accept();
                e.Handled = true;
                break;
            case Key.Escape:
                Close(null);
                e.Handled = true;
                break;
        }
    }

    private void Accept()
    {
        if (ItemsList.SelectedItem is ListPickerItem picked)
            Close(picked.Value);
        else
            Close(null);
    }

    internal sealed record ListPickerItem(object Value, string Label)
    {
        public override string ToString() => Label;
    }
}
