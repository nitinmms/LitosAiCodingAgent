using System.Globalization;
using Litos.Agent.Session;
using Litos.Console.Terminal;

namespace Litos.Console.Tests;

/// <summary>
/// Tests for ContextBreakdownDialog.RenderLines, the pure text-table rendering behind /context —
/// split out so it's testable without a Terminal.Gui control tree (mirrors PickerDialog/
/// McpServersWindow's pure/UI-free convention). ContextBreakdown.Compute itself (Litos.Agent) is
/// covered elsewhere; these tests only exercise the rendering layer.
///
/// RenderLines formats numbers via the thread's CurrentCulture (":N0", same as Litos.Gui's own
/// ViewContextWindow) — assertions run under InvariantCulture explicitly so they don't depend on
/// the test host's OS locale (e.g. a locale that groups digits as "2,00,000" instead of
/// "200,000").
/// </summary>
public class ContextBreakdownDialogTests : IDisposable
{
    private readonly CultureInfo _originalCulture = CultureInfo.CurrentCulture;

    public ContextBreakdownDialogTests() => CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

    public void Dispose() => CultureInfo.CurrentCulture = _originalCulture;

    [Fact]
    public void RenderLines_IncludesTotalAndContextLengthSummary()
    {
        var snapshot = new ContextBreakdownSnapshot(
            [new ContextBreakdownEntry(ContextCategory.SystemPrompt, "System prompt", 1000)], 1000, null);

        var lines = ContextBreakdownDialog.RenderLines(snapshot, 200_000);

        Assert.Contains(lines, l => l.Contains("1,000") && l.Contains("200,000"));
    }

    [Fact]
    public void RenderLines_ZeroContextLength_DoesNotThrow_AndShowsZeroPercent()
    {
        var snapshot = new ContextBreakdownSnapshot(
            [new ContextBreakdownEntry(ContextCategory.SystemPrompt, "System prompt", 1000)], 1000, null);

        var lines = ContextBreakdownDialog.RenderLines(snapshot, contextLength: 0);

        Assert.Contains(lines, l => l.Contains("(0%)"));
    }

    [Fact]
    public void RenderLines_EachEntry_ShowsLabelTokensAndPercentage()
    {
        var snapshot = new ContextBreakdownSnapshot(
            [
                new ContextBreakdownEntry(ContextCategory.SystemPrompt, "System prompt", 750),
                new ContextBreakdownEntry(ContextCategory.History, "Conversation history", 250),
            ], 1000, null);

        var lines = ContextBreakdownDialog.RenderLines(snapshot, 200_000);

        Assert.Contains(lines, l => l.Contains("System prompt") && l.Contains("750") && l.Contains("75"));
        Assert.Contains(lines, l => l.Contains("Conversation history") && l.Contains("250") && l.Contains("25"));
    }

    [Fact]
    public void RenderLines_SubItems_AreIndentedUnderParentCategory()
    {
        var snapshot = new ContextBreakdownSnapshot(
            [
                new ContextBreakdownEntry(ContextCategory.ToolResults, "Tool results", 300,
                    [new ContextBreakdownSubItem("shell", 200), new ContextBreakdownSubItem("read_file", 100)]),
            ], 300, null);

        var lines = ContextBreakdownDialog.RenderLines(snapshot, 200_000);

        var parentIndex = lines.ToList().FindIndex(l => l.Contains("Tool results"));
        var shellIndex = lines.ToList().FindIndex(l => l.Contains("shell") && l.Contains("200"));
        Assert.True(parentIndex >= 0 && shellIndex > parentIndex);
    }

    [Fact]
    public void RenderLines_WithLastRealUsage_ShowsScaledCaption()
    {
        var snapshot = new ContextBreakdownSnapshot(
            [new ContextBreakdownEntry(ContextCategory.SystemPrompt, "System prompt", 1000)], 1000, LastRealUsageTokens: 950);

        var lines = ContextBreakdownDialog.RenderLines(snapshot, 200_000);

        Assert.Contains(lines, l => l.Contains("scaled to match last real usage: 950 tokens"));
    }

    [Fact]
    public void RenderLines_WithoutLastRealUsage_ShowsEstimateOnlyCaption()
    {
        var snapshot = new ContextBreakdownSnapshot(
            [new ContextBreakdownEntry(ContextCategory.SystemPrompt, "System prompt", 1000)], 1000, LastRealUsageTokens: null);

        var lines = ContextBreakdownDialog.RenderLines(snapshot, 200_000);

        Assert.Contains(lines, l => l.Contains("no real usage reported yet"));
    }

    [Fact]
    public void RenderLines_EmptyEntries_StillProducesSummaryAndCaption()
    {
        var snapshot = new ContextBreakdownSnapshot([], 0, null);

        var lines = ContextBreakdownDialog.RenderLines(snapshot, 200_000);

        Assert.Contains(lines, l => l.Contains("0 / 200,000"));
    }
}
