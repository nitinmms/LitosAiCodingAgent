using Litos.Gui;
using Litos.Tools.Shell;

namespace Litos.Gui.Tests;

public class ApprovalCardTests
{
    [Fact]
    public void Headline_JoinsToolAndSummary() =>
        Assert.Equal("mcp__fs__write — write notes.txt",
            ApprovalCard.Headline(new ToolInvocationPreview("mcp__fs__write", "write notes.txt", null)));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("mcp__fs__write")] // a summary that only repeats the tool name adds nothing
    public void Headline_WithNoUsefulSummary_IsJustTheToolName(string summary) =>
        Assert.Equal("mcp__fs__write", ApprovalCard.Headline(new ToolInvocationPreview("mcp__fs__write", summary, null)));

    // ApprovalOutcome is internal, so a public theory can't take it as a parameter — by name instead.
    [Theory]
    [InlineData(nameof(ApprovalOutcome.Approved), "✓ Approved")]
    [InlineData(nameof(ApprovalOutcome.Denied), "✗ Denied")]
    [InlineData(nameof(ApprovalOutcome.TimedOut), "⏱ Timed out — denied automatically")]
    public void OutcomeLabel_NamesEachOutcome(string outcome, string expected) =>
        Assert.Equal(expected, ApprovalCard.OutcomeLabel(Enum.Parse<ApprovalOutcome>(outcome)));
}
