using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Litos.Agent.Session;

namespace Litos.Gui;

/// <summary>
/// "View Context" breakdown: a segmented bar plus a table of categories (system prompt, memory,
/// tool schemas, conversation history, tool results — sub-grouped by tool name, images,
/// compaction summary), each showing an estimated token count and share of the total. Opened by
/// double-clicking the status-bar context meter (see MainWindow.ShowContextBreakdownAsync). Plain
/// Window opened via a static async factory, mirroring McpServersWindow/ListPickerWindow's
/// convention — no ViewModel, no IDialogService, this codebase has neither.
///
/// This is a live, current-turn snapshot computed once when the modal opens (see
/// ContextBreakdown.Compute) — it does not live-update while left open during an in-flight turn,
/// matching how the status-bar meter itself only refreshes between turns.
/// </summary>
public sealed partial class ViewContextWindow : Window
{
    private static readonly IBrush DimTextBrush = new SolidColorBrush(Color.Parse("#9A9A9A"));
    private static readonly IBrush RowBackgroundBrush = new SolidColorBrush(Color.Parse("#2D2D30"));

    // One fixed, distinguishable color per category — kept to ContextCategory's declared set (8
    // entries) rather than assigned per-tool, since sub-items (e.g. per-tool-name breakdown under
    // Tool results) share their parent category's color/shade instead of getting their own hue.
    private static readonly Dictionary<ContextCategory, IBrush> CategoryBrushes = new()
    {
        [ContextCategory.SystemPrompt] = new SolidColorBrush(Color.Parse("#569CD6")),
        [ContextCategory.ToolSchemas] = new SolidColorBrush(Color.Parse("#4EC9B0")),
        [ContextCategory.Memory] = new SolidColorBrush(Color.Parse("#C586C0")),
        [ContextCategory.Skills] = new SolidColorBrush(Color.Parse("#DCDCAA")),
        [ContextCategory.History] = new SolidColorBrush(Color.Parse("#CE9178")),
        [ContextCategory.ToolResults] = new SolidColorBrush(Color.Parse("#9CDCFE")),
        [ContextCategory.Images] = new SolidColorBrush(Color.Parse("#D7BA7D")),
        [ContextCategory.CompactionSummary] = new SolidColorBrush(Color.Parse("#808080")),
    };

    // Keyed by category so an expanded tool-results sub-list survives a re-render (there is
    // only ever one such category today, but this mirrors McpServersWindow's _expandedServers
    // pattern in case a future category grows sub-items too).
    private readonly HashSet<ContextCategory> _expandedCategories = [];

    private ContextBreakdownSnapshot _snapshot = null!;

    public ViewContextWindow()
    {
        InitializeComponent();
        // Parameterless constructor required by the Avalonia XAML previewer/loader.
    }

    public static async Task ShowAsync(Window owner, ContextBreakdownSnapshot snapshot, int contextLength)
    {
        var window = new ViewContextWindow { _snapshot = snapshot };
        window.CloseButton.Click += (_, _) => window.Close();
        window.Render(contextLength);
        await window.ShowDialog(owner);
    }

    private void Render(int contextLength)
    {
        RenderBar();
        RenderSummary(contextLength);
        RenderRows();
        RenderCaption();
    }

    private void RenderBar()
    {
        BarPanel.Children.Clear();
        if (_snapshot.TotalEstimatedTokens == 0)
            return;

        foreach (var entry in _snapshot.Entries)
        {
            var width = 500.0 * entry.EstimatedTokens / _snapshot.TotalEstimatedTokens;
            if (width <= 0)
                continue;

            var segment = new Border { Background = CategoryBrushes[entry.Category], Width = width };
            ToolTip.SetTip(segment, $"{entry.Label}: {entry.EstimatedTokens:N0} tokens");
            BarPanel.Children.Add(segment);
        }
    }

    private void RenderSummary(int contextLength)
    {
        var fraction = contextLength <= 0 ? 0 : (double)_snapshot.TotalEstimatedTokens / contextLength;
        SummaryText.Text = $"{_snapshot.TotalEstimatedTokens:N0} / {contextLength:N0} tokens ({fraction * 100:0}%)";
    }

    private void RenderRows()
    {
        RowsPanel.Children.Clear();
        foreach (var entry in _snapshot.Entries)
            RowsPanel.Children.Add(BuildRow(entry));
    }

    private Control BuildRow(ContextBreakdownEntry entry)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(BuildRowHeader(entry));

        if (entry.SubItems is { Count: > 0 } && _expandedCategories.Contains(entry.Category))
        {
            var subList = new StackPanel { Spacing = 2, Margin = new Avalonia.Thickness(20, 2, 0, 0) };
            foreach (var sub in entry.SubItems)
                subList.Children.Add(BuildSubRow(sub));
            panel.Children.Add(subList);
        }

        return panel;
    }

    private Control BuildRowHeader(ContextBreakdownEntry entry)
    {
        var percentage = _snapshot.TotalEstimatedTokens == 0 ? 0 : 100.0 * entry.EstimatedTokens / _snapshot.TotalEstimatedTokens;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };

        var dot = new Border
        {
            Background = CategoryBrushes[entry.Category],
            Width = 10,
            Height = 10,
            CornerRadius = new Avalonia.CornerRadius(5),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Avalonia.Thickness(0, 0, 8, 0),
        };
        Grid.SetColumn(dot, 0);

        var hasSubItems = entry.SubItems is { Count: > 0 };
        var expanded = hasSubItems && _expandedCategories.Contains(entry.Category);
        var label = new TextBlock
        {
            Text = hasSubItems ? $"{entry.Label} {(expanded ? "▾" : "▸")}" : entry.Label,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (hasSubItems)
        {
            label.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
            label.PointerPressed += (_, _) =>
            {
                if (!_expandedCategories.Add(entry.Category))
                    _expandedCategories.Remove(entry.Category);
                RenderRows();
            };
        }
        Grid.SetColumn(label, 1);

        var tokens = new TextBlock
        {
            Text = $"{entry.EstimatedTokens:N0} tokens",
            Foreground = DimTextBrush,
            Margin = new Avalonia.Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(tokens, 2);

        var percent = new TextBlock
        {
            Text = $"{percentage:0.#}%",
            Foreground = DimTextBrush,
            Margin = new Avalonia.Thickness(8, 0, 0, 0),
            Width = 44,
            TextAlignment = Avalonia.Media.TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(percent, 3);

        grid.Children.Add(dot);
        grid.Children.Add(label);
        grid.Children.Add(tokens);
        grid.Children.Add(percent);

        return new Border
        {
            Background = RowBackgroundBrush,
            CornerRadius = new Avalonia.CornerRadius(4),
            Padding = new Avalonia.Thickness(8, 6),
            Child = grid,
        };
    }

    private Control BuildSubRow(ContextBreakdownSubItem sub)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

        var label = new TextBlock { Text = sub.Label, Foreground = DimTextBrush, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 0);

        var tokens = new TextBlock { Text = $"{sub.EstimatedTokens:N0} tokens", Foreground = DimTextBrush, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(tokens, 1);

        grid.Children.Add(label);
        grid.Children.Add(tokens);
        return grid;
    }

    private void RenderCaption()
    {
        CaptionText.Text = _snapshot.LastRealUsageTokens is { } real
            ? $"Estimated from message content, scaled to match last real usage: {real:N0} tokens."
            : "Estimated from message content; no real usage reported yet this session.";
    }
}
