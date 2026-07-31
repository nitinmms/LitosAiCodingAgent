using Litos.Agent.Messages;
using Litos.Agent.Session;
using Litos.Agent.Streaming;

namespace Litos.Agent.Tests.Session;

public class TranscriptEntryTests
{
    [Fact]
    public void FromMessage_UsesAssistantKind_ForAssistantRole()
    {
        var entry = TranscriptEntry.FromMessage(ChatMessage.Assistant([new TextBlock("hi")]));

        Assert.Equal("assistant", entry.Kind);
    }

    [Fact]
    public void FromMessage_UsesUserKind_ForUserRole()
    {
        var entry = TranscriptEntry.FromMessage(ChatMessage.User("hi"));

        Assert.Equal("user", entry.Kind);
    }

    [Fact]
    public void FromMessage_UsesUserKind_ForToolResultMessages_SinceToolResultsAreUserRole()
    {
        // ChatMessage.ToolResult produces Role.User, so a tool-result entry is stamped
        // "user" too — Transcript.LoadAsync relies on Content block types, not Kind, to
        // distinguish a genuine user message from a tool-result message.
        var toolResultMessage = ChatMessage.ToolResult("call_1", new Litos.Agent.Tools.ToolResult("output"));

        var entry = TranscriptEntry.FromMessage(toolResultMessage);

        Assert.Equal("user", entry.Kind);
    }

    [Fact]
    public void FromMessage_CallIdIsAlwaysNull()
    {
        var entry = TranscriptEntry.FromMessage(ChatMessage.User("hi"));

        Assert.Null(entry.CallId);
    }

    [Fact]
    public void FromMessage_PropagatesUsage_WhenProvided()
    {
        var entry = TranscriptEntry.FromMessage(ChatMessage.Assistant([new TextBlock("hi")]), new UsageInfo(10, 5));

        Assert.NotNull(entry.Usage);
        Assert.Equal(10, entry.Usage!.InputTokens);
    }

    [Fact]
    public void FromMessage_UsageIsNull_WhenNotProvided()
    {
        var entry = TranscriptEntry.FromMessage(ChatMessage.User("hi"));

        Assert.Null(entry.Usage);
    }

    [Fact]
    public void SessionHeader_HasSessionKind_NullMessage_AndGivenWorkingDirectory()
    {
        var entry = TranscriptEntry.SessionHeader("/repo");

        Assert.Equal("session", entry.Kind);
        Assert.Null(entry.Message);
        Assert.Equal("/repo", entry.WorkingDirectory);
        Assert.Null(entry.CallId);
        Assert.Null(entry.Usage);
    }
}
