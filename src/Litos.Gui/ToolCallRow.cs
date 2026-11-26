using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Litos.Agent.Streaming;
using Litos.Agent.Tools;
using Litos.Hosting.Files;

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
///
/// run_kernel_code rows use the same collapsible shape. Its output is whatever the script printed,
/// often many lines, and the single-line form showed only the first of them, which read as an
/// agent message cut off halfway. Its header gives line counts ("Run code (14 lines) → 10 lines
/// of output"); expanded, it shows the script, its full output, and, dimmed, the kernel's
/// state-change trailer, which is there for the model rather than the user.
/// </summary>
internal sealed class ToolCallRow
{
    private readonly string _toolName;
    private JsonElement _arguments;
    private readonly IBrush _runningAndSuccessBrush;
    private readonly IBrush _errorBrush;

    // Non-null only for built-in-tool rows: their single summary line.
    private readonly TextBlock? _lineText;
    // Non-null only for expandable rows (MCP, run_kernel_code) — other built-in-tool rows are a
    // single TextBlock with no header/detail split.
    private readonly TextBlock? _headerText;
    private readonly TextBlock? _detailText;
    // The kernel's state-change trailer, shown dimmed under a run_kernel_code row's detail.
    private readonly TextBlock? _noteText;
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
    /// Raised when the user clicks an "Open ↗" link the row shows for a share or artifact link in
    /// the tool's result (share_file, artifact) — the row's own text isn't clickable, and the model
    /// doesn't always repeat the link in its reply. MainWindow routes it through the same OpenUrl
    /// as markdown links (stale-port rewrite, http/https only).
    /// </summary>
    public event Action<string>? LinkClicked;

    /// <summary>
    /// Raised after an MCP row's expand/collapse toggle changes Root's height — MainWindow hooks
    /// this to re-run its ScrollToEndAfterLayout/NudgeTranscriptLayout workaround (see that
    /// method's own remarks on Avalonia's ScrollViewer extent-staleness bug), the same fix already
    /// applied after every other TranscriptPanel content change. Never raised for a one-line row
    /// (its Root height never changes after creation).
    /// </summary>
    public event Action? Toggled;

    private readonly StackPanel _panel;

    // Slow-call notice, at parity with the VS Code webview (webviewContent.ts addToolEntry): a row
    // can sit at "running" for minutes with nothing wrong, and two different waits cause it —
    // the model still sending the call (ToolCallStarted -> ToolCallCompleted; some local servers
    // send nothing until the whole argument blob is ready) or the tool itself executing
    // (ToolCallCompleted -> ToolCallResult). The notice names which, and counts from the start.
    internal static readonly TimeSpan SlowNoticeAfter = TimeSpan.FromSeconds(20);
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private readonly TextBlock _slowNotice = new()
    {
        FontStyle = FontStyle.Italic,
        Opacity = 0.7,
        Margin = new Avalonia.Thickness(16, 0, 4, 0),
        TextWrapping = TextWrapping.Wrap,
        IsVisible = false,
    };
    private DispatcherTimer? _slowTimer;
    private bool _sending;

    private ToolCallRow(
        string toolName, JsonElement arguments, IBrush runningAndSuccessBrush, IBrush errorBrush,
        StackPanel panel, TextBlock? lineText, TextBlock? headerText, TextBlock? detailText, TextBlock? noteText = null)
    {
        _toolName = toolName;
        _arguments = arguments;
        _runningAndSuccessBrush = runningAndSuccessBrush;
        _errorBrush = errorBrush;
        Root = _panel = panel;
        _lineText = lineText;
        _headerText = headerText;
        _detailText = detailText;
        _noteText = noteText;
        _slowNotice.Foreground = runningAndSuccessBrush;
        _panel.Children.Add(_slowNotice);
    }

    public static ToolCallRow Create(string toolName, JsonElement arguments, IBrush runningAndSuccessBrush, IBrush errorBrush)
    {
        if (!IsExpandable(toolName))
        {
            var textBlock = NewMonospaceTextBlock();
            textBlock.Foreground = runningAndSuccessBrush;
            var linePanel = new StackPanel { Spacing = 2 };
            linePanel.Children.Add(textBlock);
            return new ToolCallRow(toolName, arguments, runningAndSuccessBrush, errorBrush, linePanel, textBlock, headerText: null, detailText: null);
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

        TextBlock? note = null;
        if (IsKernelTool(toolName))
        {
            note = NewMonospaceTextBlock();
            note.Foreground = runningAndSuccessBrush;
            note.Opacity = 0.55;
            note.Margin = new Avalonia.Thickness(16, 0, 4, 4);
            note.IsVisible = false;
            panel.Children.Add(note);
        }

        var row = new ToolCallRow(toolName, arguments, runningAndSuccessBrush, errorBrush, panel, lineText: null, header, detail, note);
        header.PointerPressed += (_, _) => row.ToggleExpanded();
        return row;
    }

    /// <summary>MCP tools are always named mcp__{server}__{tool} (McpToolProxy.Name) — no fixed
    /// per-tool schema exists for them the way built-in tools have, which is what drives every
    /// other MCP-specific branch in this class. Pure and internal so it's testable without an
    /// Avalonia Application (this codebase has no headless UI test infrastructure).</summary>
    internal static bool IsMcpTool(string toolName) => toolName.StartsWith("mcp__", StringComparison.Ordinal);

    internal static bool IsKernelTool(string toolName) => toolName == ReservedToolNames.KernelCode;

    /// <summary>Rows drawn as a clickable header over a hidden detail block, rather than one line.</summary>
    internal static bool IsExpandable(string toolName) => IsMcpTool(toolName) || IsKernelTool(toolName);

    private static TextBlock NewMonospaceTextBlock() => new()
    {
        FontFamily = new FontFamily("Cascadia Code,Consolas,monospace"),
        Margin = new Avalonia.Thickness(4, 0),
        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
    };

    /// <summary>
    /// The row's state between ToolCallStarted and ToolCallCompleted: the model has named the tool
    /// but is still sending its arguments. <see cref="SetArguments"/> moves it on to executing.
    /// </summary>
    public void RenderSending()
    {
        _sending = true;
        StartSlowTimer();
        if (_lineText is not null)
            _lineText.Text = $"▶ {_toolName}";
        else
            RenderMcpHeader(glyph: "▶", brush: _runningAndSuccessBrush);
    }

    /// <summary>The call has fully arrived (ToolCallCompleted) and the tool is now executing.</summary>
    public void SetArguments(JsonElement arguments)
    {
        _arguments = arguments;
        _sending = false;
        RenderRunning();
    }

    /// <summary>Stops the slow-call timer and hides its notice — every exit from "running" (a
    /// result, or the turn ending without one) must come through here.</summary>
    public void StopSlowTimer()
    {
        _slowTimer?.Stop();
        _slowTimer = null;
        _slowNotice.IsVisible = false;
    }

    private void StartSlowTimer()
    {
        if (_slowTimer is not null)
            return;
        _slowTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            if (SlowNoticeText(_sending, DateTimeOffset.UtcNow - _startedAt) is not { } text)
                return;
            _slowNotice.Text = text;
            _slowNotice.IsVisible = true;
        });
        _slowTimer.Start();
    }

    /// <summary>The notice for a call <paramref name="elapsed"/> into running, or null while it's
    /// still under <see cref="SlowNoticeAfter"/>. Same wording as the VS Code webview. Pure and
    /// internal so it's testable without an Avalonia Application.</summary>
    internal static string? SlowNoticeText(bool sending, TimeSpan elapsed)
    {
        if (elapsed < SlowNoticeAfter)
            return null;

        var total = (int)elapsed.TotalSeconds;
        var formatted = total >= 60 ? $"{total / 60}m {total % 60}s" : $"{total}s";
        return sending
            ? $"waiting for the model to finish sending this tool call ({formatted})"
            : $"still running ({formatted})";
    }

    /// <param name="startSlowTimer">False for a row restored from history (/resume, /branch) whose
    /// call never got a result — nothing is running any more, so no "still running" notice.</param>
    public void RenderRunning(bool startSlowTimer = true)
    {
        if (startSlowTimer)
            StartSlowTimer();
        if (_lineText is not null)
        {
            _lineText.Text = $"▶ {ToolCallSummary.DescribeCall(_toolName, _arguments)}";
            return;
        }

        RenderMcpHeader(glyph: "▶", brush: _runningAndSuccessBrush);
        RenderMcpDetail();
    }

    public void RenderCompleted(ToolResult result)
    {
        StopSlowTimer();
        var glyph = result.IsError ? "✗" : "✓";
        var brush = result.IsError ? _errorBrush : _runningAndSuccessBrush;

        if (!result.IsError)
            AddLinks(HostLinks.FindAll(result.Text));

        if (_lineText is not null)
        {
            var outcome = ToolCallSummary.DescribeResult(_toolName, result);
            _lineText.Text = $"{glyph} {ToolCallSummary.DescribeCall(_toolName, _arguments)}  ({outcome})";
            _lineText.Foreground = brush;
            return;
        }

        _result = result;
        RenderMcpHeader(glyph, brush);
        RenderMcpDetail();
    }

    /// <summary>A call AgentLoop skipped because the user steered mid-batch: it already has a row
    /// (its ToolCallCompleted arrived) but will never get a result.</summary>
    public void RenderSkipped(string reason)
    {
        StopSlowTimer();
        var text = $"⏩ {_toolName} skipped ({reason})";
        if (_lineText is not null)
            _lineText.Text = text;
        else
        {
            _lastGlyph = "⏩";
            _headerText!.Text = text;
        }
    }

    private void AddLinks(IReadOnlyList<string> urls)
    {
        foreach (var url in urls)
        {
            var link = NewMonospaceTextBlock();
            link.Text = $"  ↗ Open {url}";
            link.Foreground = _runningAndSuccessBrush;
            link.TextDecorations = TextDecorations.Underline;
            link.Cursor = new Cursor(StandardCursorType.Hand);
            link.PointerPressed += (_, _) => LinkClicked?.Invoke(url);
            _panel.Children.Add(link);
        }
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
        var hint = _expanded ? "click to collapse" : "click to expand";
        _headerText!.Text = IsKernelTool(_toolName)
            ? $"{glyph} {DescribeKernelHeader(_arguments, _result)}  ({hint})"
            : $"{glyph} {_toolName} ({hint})";
        _headerText.Foreground = brush;
    }

    private void RenderMcpDetail()
    {
        if (_noteText is not null)
            _noteText.IsVisible = false;
        if (!_expanded)
            return;

        _detailText!.Foreground = _lastBrush;
        if (!IsKernelTool(_toolName))
        {
            _detailText.Text = DescribeMcpDetail(_arguments, _result);
            return;
        }

        var (detail, note) = DescribeKernelDetail(_arguments, _result);
        _detailText.Text = detail;
        if (_noteText is not null && note is not null)
        {
            _noteText.Text = note;
            _noteText.IsVisible = true;
        }
    }

    /// <summary>
    /// A run_kernel_code row's header: the script's size, then once it has finished the outcome
    /// ToolCallSummary gives it (an output line count, or an error's first line). Pure and internal
    /// so it's testable without an Avalonia Application.
    /// </summary>
    internal static string DescribeKernelHeader(JsonElement arguments, ToolResult? result)
    {
        var call = ToolCallSummary.DescribeCall(ReservedToolNames.KernelCode, arguments);
        return result is null ? call : $"{call} → {ToolCallSummary.DescribeResult(ReservedToolNames.KernelCode, result)}";
    }

    /// <summary>
    /// A run_kernel_code row's expanded detail: the script exactly as the model wrote it, then its
    /// full output once it has finished. The kernel's state-change trailer comes back separately as
    /// Note, so the row can show it dimmed. Pure and internal so it's testable without an Avalonia
    /// Application.
    /// </summary>
    internal static (string Detail, string? Note) DescribeKernelDetail(JsonElement arguments, ToolResult? result)
    {
        var code = arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty("code", out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()!.TrimEnd()
                : "";
        var codeSection = $"Code:\n{(code.Length > 0 ? code : "(not received yet)")}";
        if (result is null)
            return (codeSection, null);

        var (output, note) = ToolCallSummary.SplitKernelStateNote(result.Text);
        var label = result.IsError ? "Error" : "Output";
        return ($"{codeSection}\n\n{label}:\n{(output.Length > 0 ? output : "(no output)")}", note);
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
