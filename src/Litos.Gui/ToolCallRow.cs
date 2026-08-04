using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Litos.Agent.Streaming;
using Litos.Agent.Tools;

namespace Litos.Gui;

/// <summary>
/// Tracks one tool-call transcript line from ToolCallCompleted through to its matching
/// ToolCallResult, so the same row is updated in place instead of a second line being appended.
/// Not a ViewModel — no INotifyPropertyChanged/binding; MainWindow still pushes into control
/// properties directly, same as every other row in this file. Brushes are constructor parameters
/// (not read back from MainWindow) so this type has no dependency on it.
///
/// Built-in tools (read_file, shell, etc.) render as a single plain TextBlock via
/// ToolCallSummary's per-tool "verb + target" summaries — unchanged from before MCP support.
/// MCP tools (name starts with "mcp__") have no per-tool summary to draw on (arbitrary JSON,
/// no fixed schema), so ToolCallSummary's default case would otherwise render either nothing
/// useful (DescribeCall falls back to the bare tool name) or a silently truncated first line of a
/// multiline JSON result (DescribeResult's FirstLine fallback). Instead, an MCP row is a small
/// collapsed-by-default control: a clickable header (glyph + tool name) that expands, on click,
/// into the full pretty-printed arguments and result text.
/// </summary>
internal sealed class ToolCallRow
{
    private readonly string _toolName;
    private readonly JsonElement _arguments;
    private readonly IBrush _runningAndSuccessBrush;
    private readonly IBrush _errorBrush;

    // Non-null only for MCP rows — built-in-tool rows are a single TextBlock with no header/detail split.
    private readonly TextBlock? _headerText;
    private readonly TextBlock? _detailText;
    private bool _expanded;
    private ToolResult? _result; // null while running
    // Tracks the glyph/brush RenderRunning/RenderCompleted last drew, so ToggleExpanded can
    // redraw the header (its "(click to expand/collapse)" hint depends on _expanded) without
    // needing to re-derive running/success/error state from _result.
    private string _lastGlyph = "▶";
    private IBrush _lastBrush = Brushes.Transparent;

    /// <summary>The control MainWindow adds to TranscriptPanel.</summary>
    public Control Root { get; }

    /// <summary>
    /// Raised after an MCP row's expand/collapse toggle changes Root's height — MainWindow hooks
    /// this to re-run its ScrollToEndAfterLayout/NudgeTranscriptLayout workaround (see that
    /// method's own remarks on Avalonia's ScrollViewer extent-staleness bug), the same fix already
    /// applied after every other TranscriptPanel content change. Never raised for a non-MCP row
    /// (its Root height never changes after creation).
    /// </summary>
    public event Action? Toggled;

    private ToolCallRow(
        string toolName, JsonElement arguments, IBrush runningAndSuccessBrush, IBrush errorBrush,
        Control root, TextBlock? headerText, TextBlock? detailText)
    {
        _toolName = toolName;
        _arguments = arguments;
        _runningAndSuccessBrush = runningAndSuccessBrush;
        _errorBrush = errorBrush;
        Root = root;
        _headerText = headerText;
        _detailText = detailText;
    }

    public static ToolCallRow Create(string toolName, JsonElement arguments, IBrush runningAndSuccessBrush, IBrush errorBrush)
    {
        if (!IsMcpTool(toolName))
        {
            var textBlock = NewMonospaceTextBlock();
            textBlock.Foreground = runningAndSuccessBrush;
            return new ToolCallRow(toolName, arguments, runningAndSuccessBrush, errorBrush, textBlock, headerText: null, detailText: null);
        }

        var header = NewMonospaceTextBlock();
        header.Foreground = runningAndSuccessBrush;
        header.Cursor = new Cursor(StandardCursorType.Hand);

        var detail = NewMonospaceTextBlock();
        detail.Foreground = runningAndSuccessBrush;
        detail.Margin = new Avalonia.Thickness(16, 4, 4, 4);
        detail.IsVisible = false;

        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(header);
        panel.Children.Add(detail);

        var row = new ToolCallRow(toolName, arguments, runningAndSuccessBrush, errorBrush, panel, header, detail);
        header.PointerPressed += (_, _) => row.ToggleExpanded();
        return row;
    }

    /// <summary>MCP tools are always named mcp__{server}__{tool} (McpToolProxy.Name) — no fixed
    /// per-tool schema exists for them the way built-in tools have, which is what drives every
    /// other MCP-specific branch in this class. Pure and internal so it's testable without an
    /// Avalonia Application (this codebase has no headless UI test infrastructure).</summary>
    internal static bool IsMcpTool(string toolName) => toolName.StartsWith("mcp__", StringComparison.Ordinal);

    private static TextBlock NewMonospaceTextBlock() => new()
    {
        FontFamily = new FontFamily("Cascadia Code,Consolas,monospace"),
        Margin = new Avalonia.Thickness(4, 0),
        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
    };

    public void RenderRunning()
    {
        if (_headerText is null)
        {
            ((TextBlock)Root).Text = $"▶ {ToolCallSummary.DescribeCall(_toolName, _arguments)}";
            return;
        }

        RenderMcpHeader(glyph: "▶", brush: _runningAndSuccessBrush);
        RenderMcpDetail();
    }

    public void RenderCompleted(ToolResult result)
    {
        var glyph = result.IsError ? "✗" : "✓";
        var brush = result.IsError ? _errorBrush : _runningAndSuccessBrush;

        if (_headerText is null)
        {
            var outcome = ToolCallSummary.DescribeResult(_toolName, result);
            ((TextBlock)Root).Text = $"{glyph} {ToolCallSummary.DescribeCall(_toolName, _arguments)}  ({outcome})";
            ((TextBlock)Root).Foreground = brush;
            return;
        }

        _result = result;
        RenderMcpHeader(glyph, brush);
        RenderMcpDetail();
    }

    private void ToggleExpanded()
    {
        _expanded = !_expanded;
        _detailText!.IsVisible = _expanded;
        // Re-render the header (its "(click to expand/collapse)" hint depends on _expanded) using
        // whatever glyph/brush the last RenderRunning/RenderCompleted call left in place.
        RenderMcpHeader(_lastGlyph, _lastBrush);
        RenderMcpDetail();
        Toggled?.Invoke();
    }

    private void RenderMcpHeader(string glyph, IBrush brush)
    {
        _lastGlyph = glyph;
        _lastBrush = brush;
        _headerText!.Text = $"{glyph} {_toolName} ({(_expanded ? "click to collapse" : "click to expand")})";
        _headerText.Foreground = brush;
    }

    private void RenderMcpDetail()
    {
        if (!_expanded)
            return;

        _detailText!.Foreground = _lastBrush;
        _detailText.Text = DescribeMcpDetail(_arguments, _result);
    }

    /// <summary>
    /// Composes an MCP row's expanded detail text: pretty-printed arguments, plus the raw result
    /// text once the call has completed (result text is left as-is, not re-pretty-printed — most
    /// MCP servers return either plain text or JSON already formatted for a human/LLM reader, and
    /// re-parsing+reformatting it here could quietly mangle a non-JSON payload). Pure and internal
    /// so it's testable without an Avalonia Application.
    /// </summary>
    internal static string DescribeMcpDetail(JsonElement arguments, ToolResult? result) =>
        result is null
            ? $"Arguments:\n{PrettyPrint(arguments)}"
            : $"Arguments:\n{PrettyPrint(arguments)}\n\nResult:\n{result.Text}";

    private static string PrettyPrint(JsonElement element)
    {
        try
        {
            return JsonSerializer.Serialize(element, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return element.ToString();
        }
    }
}
