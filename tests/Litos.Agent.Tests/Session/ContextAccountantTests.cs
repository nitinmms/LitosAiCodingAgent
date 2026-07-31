using Litos.Agent.Messages;
using Litos.Agent.Session;
using Litos.Agent.Tools;

namespace Litos.Agent.Tests.Session;

public class ContextAccountantTests
{
    [Fact]
    public void BuildRequest_MapsTranscriptMessagesToolsModelAndSystemPrompt()
    {
        var transcript = Transcript.CreateNew("/repo");
        transcript.Append(ChatMessage.User("hi"));
        IReadOnlyList<ToolSchema> tools = [new ToolSchema("read_file", "desc", default)];
        var accountant = new ContextAccountant();

        var request = accountant.BuildRequest(transcript, tools, "gpt-5.4-mini", "you are helpful");

        Assert.Same(transcript.Messages, request.Messages);
        Assert.Same(tools, request.Tools);
        Assert.Equal("gpt-5.4-mini", request.Model);
        Assert.Equal("you are helpful", request.SystemPrompt);
    }

    [Fact]
    public void BuildRequest_LeavesTemperatureAndMaxOutputTokensUnset()
    {
        var transcript = Transcript.CreateNew("/repo");
        var accountant = new ContextAccountant();

        var request = accountant.BuildRequest(transcript, [], "model", null);

        Assert.Null(request.Temperature);
        Assert.Null(request.MaxOutputTokens);
    }

    [Fact]
    public void BuildRequest_AllowsNullSystemPrompt()
    {
        var transcript = Transcript.CreateNew("/repo");
        var accountant = new ContextAccountant();

        var request = accountant.BuildRequest(transcript, [], "model", null);

        Assert.Null(request.SystemPrompt);
    }
}
