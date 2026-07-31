using System.Text.Json;
using Litos.Agent.Messages;

namespace Litos.Agent.Tests.Messages;

public class ContentBlockTests
{
    [Fact]
    public void TextBlock_RoundTrips_WithTypeDiscriminator()
    {
        ContentBlock original = new TextBlock("hello world");

        var json = JsonSerializer.Serialize(original);
        var roundTripped = JsonSerializer.Deserialize<ContentBlock>(json);

        Assert.Contains("\"type\":\"text\"", json);
        Assert.IsType<TextBlock>(roundTripped);
        Assert.Equal("hello world", ((TextBlock)roundTripped!).Text);
    }

    [Fact]
    public void ImageBlock_RoundTrips_WithTypeDiscriminator()
    {
        ContentBlock original = new ImageBlock("image/png", [1, 2, 3, 4]);

        var json = JsonSerializer.Serialize(original);
        var roundTripped = JsonSerializer.Deserialize<ContentBlock>(json);

        Assert.Contains("\"type\":\"image\"", json);
        var image = Assert.IsType<ImageBlock>(roundTripped);
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal([1, 2, 3, 4], image.Data);
    }

    [Fact]
    public void ImageBlock_Equals_IsFalse_ForContentEqualButDifferentArrayInstances()
    {
        // record-generated equality does NOT deep-compare arrays — this documents that
        // gotcha explicitly rather than letting it be an implicit, undocumented surprise.
        var a = new ImageBlock("image/png", [1, 2, 3]);
        var b = new ImageBlock("image/png", [1, 2, 3]);

        Assert.NotEqual(a, b);
        Assert.True(a.Data.SequenceEqual(b.Data));
    }

    [Fact]
    public void ToolUseBlock_RoundTrips_WithTypeDiscriminator()
    {
        var args = JsonDocument.Parse("""{"path":"foo.cs"}""").RootElement;
        ContentBlock original = new ToolUseBlock("call_1", "read_file", args);

        var json = JsonSerializer.Serialize(original);
        var roundTripped = JsonSerializer.Deserialize<ContentBlock>(json);

        Assert.Contains("\"type\":\"tool_use\"", json);
        var toolUse = Assert.IsType<ToolUseBlock>(roundTripped);
        Assert.Equal("call_1", toolUse.CallId);
        Assert.Equal("read_file", toolUse.ToolName);
        Assert.Equal("foo.cs", toolUse.Arguments.GetProperty("path").GetString());
    }

    [Fact]
    public void ToolResultBlock_RoundTrips_WithTypeDiscriminator_AndIsErrorFlag()
    {
        ContentBlock original = new ToolResultBlock("call_1", "file not found", IsError: true);

        var json = JsonSerializer.Serialize(original);
        var roundTripped = JsonSerializer.Deserialize<ContentBlock>(json);

        Assert.Contains("\"type\":\"tool_result\"", json);
        var toolResult = Assert.IsType<ToolResultBlock>(roundTripped);
        Assert.Equal("call_1", toolResult.CallId);
        Assert.Equal("file not found", toolResult.Text);
        Assert.True(toolResult.IsError);
    }

    [Fact]
    public void ToolResultBlock_DefaultsIsErrorToFalse()
    {
        var block = new ToolResultBlock("call_1", "ok");

        Assert.False(block.IsError);
    }

    [Fact]
    public void CompactionSummaryBlock_RoundTrips_WithTypeDiscriminator()
    {
        ContentBlock original = new CompactionSummaryBlock("summary text", TokensBefore: 12345);

        var json = JsonSerializer.Serialize(original);
        var roundTripped = JsonSerializer.Deserialize<ContentBlock>(json);

        Assert.Contains("\"type\":\"compaction_summary\"", json);
        var summary = Assert.IsType<CompactionSummaryBlock>(roundTripped);
        Assert.Equal("summary text", summary.Summary);
        Assert.Equal(12345, summary.TokensBefore);
    }

    [Fact]
    public void TextBlock_AllowsEmptyText()
    {
        var block = new TextBlock("");

        Assert.Equal("", block.Text);
    }
}
