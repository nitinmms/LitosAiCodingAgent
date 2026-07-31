using Litos.Agent.Messages;
using Litos.Agent.Tools;

namespace Litos.Agent.Tests.Messages;

public class ChatMessageTests
{
    [Fact]
    public void User_WithText_WrapsAsSingleTextBlock()
    {
        var message = ChatMessage.User("hello");

        Assert.Equal(Role.User, message.Role);
        var block = Assert.Single(message.Content);
        Assert.Equal("hello", Assert.IsType<TextBlock>(block).Text);
    }

    [Fact]
    public void User_WithContentList_PassesThroughUnchanged()
    {
        IReadOnlyList<ContentBlock> content = [new TextBlock("a"), new ImageBlock("image/png", [1])];

        var message = ChatMessage.User(content);

        Assert.Equal(Role.User, message.Role);
        Assert.Same(content, message.Content);
    }

    [Fact]
    public void Assistant_WrapsGivenContent()
    {
        IReadOnlyList<ContentBlock> content = [new TextBlock("response")];

        var message = ChatMessage.Assistant(content);

        Assert.Equal(Role.Assistant, message.Role);
        Assert.Same(content, message.Content);
    }

    [Fact]
    public void ToolResult_ProducesUserRoleWithToolResultBlock()
    {
        var message = ChatMessage.ToolResult("call_1", ToolResult.Ok("file contents"));

        Assert.Equal(Role.User, message.Role);
        var block = Assert.IsType<ToolResultBlock>(Assert.Single(message.Content));
        Assert.Equal("call_1", block.CallId);
        Assert.Equal("file contents", block.Text);
        Assert.False(block.IsError);
    }

    [Fact]
    public void ToolResult_PropagatesIsErrorFromToolResult()
    {
        var message = ChatMessage.ToolResult("call_1", ToolResult.Error("file not found"));

        var block = Assert.IsType<ToolResultBlock>(Assert.Single(message.Content));
        Assert.True(block.IsError);
    }

    [Fact]
    public void CompactionSummary_ProducesUserRoleWithCompactionSummaryBlock()
    {
        var message = ChatMessage.CompactionSummary("summary", tokensBefore: 500);

        Assert.Equal(Role.User, message.Role);
        var block = Assert.IsType<CompactionSummaryBlock>(Assert.Single(message.Content));
        Assert.Equal("summary", block.Summary);
        Assert.Equal(500, block.TokensBefore);
    }
}
