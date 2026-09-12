using System.Text;
using Litos.Tools;

namespace Litos.Tools.Tests;

public class OutputTruncationTests
{
    private static string Lines(int count, string text = "line") =>
        string.Join('\n', Enumerable.Range(1, count).Select(i => $"{text}{i}"));

    [Fact]
    public void Truncate_ContentWithinBothLimits_ReturnsUnchanged()
    {
        var content = Lines(10);

        var result = OutputTruncation.Truncate(content, RetainEnd.Head);

        Assert.False(result.Truncated);
        Assert.Equal(content, result.Text);
        Assert.Equal(10, result.OutputLines);
        Assert.Equal(10, result.TotalLines);
    }

    [Fact]
    public void Truncate_ExceedsLineLimit_KeepsHeadLines_AndReportsLineCause()
    {
        var result = OutputTruncation.Truncate(Lines(100), RetainEnd.Head, maxBytes: 1_000_000, maxLines: 10);

        Assert.True(result.Truncated);
        Assert.False(result.TruncatedByBytes);
        Assert.Equal(10, result.OutputLines);
        Assert.Equal(100, result.TotalLines);
        Assert.StartsWith("line1\n", result.Text);
        Assert.EndsWith("line10", result.Text);
    }

    [Fact]
    public void Truncate_ExceedsLineLimit_TailRetainKeepsLastLines()
    {
        var result = OutputTruncation.Truncate(Lines(100), RetainEnd.Tail, maxBytes: 1_000_000, maxLines: 10);

        Assert.True(result.Truncated);
        Assert.Equal(10, result.OutputLines);
        // Tail retention must preserve original order, not reversed — the lines are collected
        // back-to-front internally and flipped again before joining.
        Assert.StartsWith("line91\n", result.Text);
        Assert.EndsWith("line100", result.Text);
    }

    [Fact]
    public void Truncate_ExceedsByteLimit_ReportsByteCause_AndStaysWithinBudget()
    {
        // Well under the line limit but far over the byte limit — the exact shape (few long lines)
        // that a line-only cap misses entirely, and the reason ReadFileTool needed a byte cap.
        var content = string.Join('\n', Enumerable.Range(1, 20).Select(_ => new string('x', 1000)));

        var result = OutputTruncation.Truncate(content, RetainEnd.Head, maxBytes: 5000, maxLines: 2000);

        Assert.True(result.Truncated);
        Assert.True(result.TruncatedByBytes);
        Assert.True(Encoding.UTF8.GetByteCount(result.Text) <= 5000);
        Assert.Equal(20, result.TotalLines);
    }

    [Fact]
    public void Truncate_NeverSplitsALine_WhenOtherLinesFit()
    {
        var content = Lines(50);

        var result = OutputTruncation.Truncate(content, RetainEnd.Head, maxBytes: 30, maxLines: 2000);

        // Every kept line must be intact — no partial line at the boundary.
        foreach (var line in result.Text.Split('\n'))
            Assert.Matches(@"^line\d+$", line);
    }

    [Fact]
    public void Truncate_SingleLineLargerThanBudget_ReturnsPartialLineRatherThanNothing()
    {
        // The one case where splitting a line is correct: returning nothing at all would be
        // strictly less useful than a partial view of the one oversized line.
        var content = new string('a', 10_000);

        var result = OutputTruncation.Truncate(content, RetainEnd.Head, maxBytes: 100, maxLines: 2000);

        Assert.True(result.Truncated);
        Assert.True(result.TruncatedByBytes);
        Assert.NotEmpty(result.Text);
        Assert.True(Encoding.UTF8.GetByteCount(result.Text) <= 100);
    }

    [Fact]
    public void Truncate_MultiByteCharacters_NeverSplitsACodePoint()
    {
        // Cutting mid-code-point would emit replacement characters into the transcript; the
        // boundary must back off to a valid UTF-8 start byte.
        var content = string.Concat(Enumerable.Repeat("日本語テキスト", 500));

        var result = OutputTruncation.Truncate(content, RetainEnd.Head, maxBytes: 101, maxLines: 2000);

        Assert.DoesNotContain('�', result.Text);
        Assert.True(Encoding.UTF8.GetByteCount(result.Text) <= 101);
    }

    [Fact]
    public void Truncate_TrailingNewline_DoesNotCountAsAnExtraLine()
    {
        var result = OutputTruncation.Truncate("a\nb\nc\n", RetainEnd.Head, maxBytes: 1_000_000, maxLines: 2000);

        Assert.False(result.Truncated);
        Assert.Equal(3, result.TotalLines);
    }

    [Fact]
    public void Truncate_EmptyContent_IsNotTruncated()
    {
        var result = OutputTruncation.Truncate("", RetainEnd.Head);

        Assert.False(result.Truncated);
        Assert.Equal(0, result.TotalLines);
    }
}
