using Litos.Agent.Messages;
using Litos.Agent.Session;
using Litos.Agent.Streaming;
using Litos.Agent.Tests.Fakes;

namespace Litos.Agent.Tests.Session;

public class ReflectorTests
{
    [Fact]
    public async Task ReflectAsync_SendsNoTools_AndAppendsTranscriptPlusPrompt()
    {
        var messages = new List<ChatMessage> { ChatMessage.User("hi"), ChatMessage.Assistant([new TextBlock("hello")]) };
        var provider = new FakeChatProvider();
        provider.Enqueue(new TextDelta("# AGENTS.md"));
        var reflector = new Reflector();

        await reflector.ReflectAsync(messages, existingAgentsMd: null, provider, "model", CancellationToken.None);

        var request = Assert.Single(provider.ReceivedRequests);
        Assert.Empty(request.Tools);
        Assert.Equal("model", request.Model);
        Assert.Equal(3, request.Messages.Count); // the 2 transcript messages + the appended reflector prompt
        Assert.Same(messages[0], request.Messages[0]);
        Assert.Same(messages[1], request.Messages[1]);
        Assert.Equal(Role.User, request.Messages[2].Role);
    }

    [Fact]
    public async Task ReflectAsync_OnlyAccumulatesTextDeltaEvents_IgnoringOthers()
    {
        var provider = new FakeChatProvider();
        provider.Enqueue(
            new ToolCallStarted("call_1", "ignored"),
            new TextDelta("actual "),
            new ErrorOccurred(new InvalidOperationException("ignored error event")),
            new TextDelta("content"));
        var reflector = new Reflector();

        var result = await reflector.ReflectAsync([], existingAgentsMd: null, provider, "model", CancellationToken.None);

        Assert.Equal("actual content", result);
    }

    [Fact]
    public async Task ReflectAsync_FallsBackToPlaceholder_WhenNoTextDeltaEventsAreYielded()
    {
        var provider = new FakeChatProvider();
        provider.Enqueue(new ErrorOccurred(new InvalidOperationException("provider failed")));
        var reflector = new Reflector();

        var result = await reflector.ReflectAsync([], existingAgentsMd: "# AGENTS.md\n", provider, "model", CancellationToken.None);

        Assert.Equal("(reflection unavailable)", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ReflectAsync_PromptsForANewFile_WhenExistingAgentsMdIsMissingOrBlank(string? existing)
    {
        var provider = new FakeChatProvider();
        provider.Enqueue(new TextDelta("new content"));
        var reflector = new Reflector();

        await reflector.ReflectAsync([], existing, provider, "model", CancellationToken.None);

        var request = Assert.Single(provider.ReceivedRequests);
        var promptBlock = Assert.IsType<TextBlock>(Assert.Single(request.Messages[^1].Content));
        Assert.Contains("does not exist yet", promptBlock.Text);
    }

    [Fact]
    public async Task ReflectAsync_IncludesExistingAgentsMdContent_WhenPresent()
    {
        var provider = new FakeChatProvider();
        provider.Enqueue(new TextDelta("new content"));
        var reflector = new Reflector();

        await reflector.ReflectAsync([], "## Conventions\n- Use tabs", provider, "model", CancellationToken.None);

        var request = Assert.Single(provider.ReceivedRequests);
        var promptBlock = Assert.IsType<TextBlock>(Assert.Single(request.Messages[^1].Content));
        Assert.Contains("## Conventions\n- Use tabs", promptBlock.Text);
    }
}
