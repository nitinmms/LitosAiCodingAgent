using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Media;
using Litos.Agent.Streaming;
using Litos.Agent.Tools;

namespace Litos.Gui;

/// <summary>
/// Tracks one tool-call transcript line from ToolCallCompleted through to its matching
/// ToolCallResult, so the same TextBlock is updated in place instead of a second line being
/// appended. Not a ViewModel — no INotifyPropertyChanged/binding; MainWindow still pushes into
/// TextBlock.Text directly, same as every other row in this file. Brushes are constructor
/// parameters (not read back from MainWindow) so this type has no dependency on it.
/// </summary>
internal sealed class ToolCallRow(
    string toolName,
    JsonElement arguments,
    TextBlock textBlock,
    IBrush runningAndSuccessBrush,
    IBrush errorBrush)
{
    public TextBlock TextBlock { get; } = textBlock;

    public void RenderRunning() =>
        TextBlock.Text = $"▶ {ToolCallSummary.DescribeCall(toolName, arguments)}";

    public void RenderCompleted(ToolResult result)
    {
        var glyph = result.IsError ? "✗" : "✓";
        var outcome = ToolCallSummary.DescribeResult(toolName, result);
        TextBlock.Text = $"{glyph} {ToolCallSummary.DescribeCall(toolName, arguments)}  ({outcome})";
        TextBlock.Foreground = result.IsError ? errorBrush : runningAndSuccessBrush;
    }
}
