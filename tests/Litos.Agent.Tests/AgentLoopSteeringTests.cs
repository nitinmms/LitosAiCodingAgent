using System.Text.Json;
using System.Threading.Channels;
using Litos.Agent.Messages;
using Litos.Agent.Session;
using Litos.Agent.Streaming;
using Litos.Agent.Tests.Fakes;
using Litos.Agent.Tools;

namespace Litos.Agent.Tests;

public class AgentLoopSteeringTests
{
    private static readonly SessionOwner Owner = SessionOwner.Local;
    private const string SessionId = "session-1";
    private const string Model = "model";

    private static AgentLoop CreateLoop(FakeChatProvider provider, IEnumerable<ITool>? tools, FakeTranscriptStore store) =>
        new(provider, new ToolRegistry(tools ?? []), store, new ContextAccountant());

    private static async Task<List<AgentEvent>> RunToCompletionAsync(
        AgentLoop loop, Transcript transcript, string userInput, ChannelReader<SteeringMessage>? steering)
    {
        var events = new List<AgentEvent>();
        await foreach (var evt in loop.RunTurnAsync(Owner, SessionId, transcript, Model, userInput, CancellationToken.None, steering))
            events.Add(evt);
        return events;
    }

    [Fact]
    public async Task RunTurnAsync_NullSteering_BehavesExactlyAsBeforeTheFeature()
    {
        var store = new FakeTranscriptStore();
        var transcript = Transcript.CreateNew("/repo");
        var provider = new FakeChatProvider();
        var args = JsonDocument.Parse("{}").RootElement;
        provider.Enqueue(
            new ToolCallCompleted("call_1", "tool_a", args),
            new ToolCallCompleted("call_2", "tool_a", args));
        provider.Enqueue(new MessageCompleted(ChatMessage.Assistant([new TextBlock("done")]), new UsageInfo(1, 1)));

        var invocationCount = 0;
        var tool = new FakeTool("tool_a") { Handler = (_, _) => { invocationCount++; return Task.FromResult(ToolResult.Ok("ok")); } };
        var loop = CreateLoop(provider, [tool], store);

        var events = await RunToCompletionAsync(loop, transcript, "hi", steering: null);

        Assert.Equal(2, invocationCount);
        Assert.DoesNotContain(events, e => e is ToolCallSkipped);
        Assert.Equal(2, provider.ReceivedRequests.Count);
    }

    [Fact]
    public async Task RunTurnAsync_SteerArrivesAfterFirstToolCall_SkipsRemainingCallsInThatRound()
    {
        var store = new FakeTranscriptStore();
        var transcript = Transcript.CreateNew("/repo");
        var provider = new FakeChatProvider();
        var args = JsonDocument.Parse("{}").RootElement;
        provider.Enqueue(
            new ToolCallCompleted("call_1", "tool_a", args),
            new ToolCallCompleted("call_2", "tool_a", args),
            new ToolCallCompleted("call_3", "tool_a", args));
        provider.Enqueue(new MessageCompleted(ChatMessage.Assistant([new TextBlock("reacting to steer")]), new UsageInfo(1, 1)));

        var channel = Channel.CreateUnbounded<SteeringMessage>();
        var invocationOrder = new List<string>();
        var tool = new FakeTool("tool_a")
        {
            Handler = (_, _) =>
            {
                invocationOrder.Add("invoked");
                // Simulate the steering message arriving while the first call is running.
                channel.Writer.TryWrite(new SteeringMessage("stop and look at this instead", SteeringMode.Steer));
                return Task.FromResult(ToolResult.Ok("ok"));
            },
        };
        var loop = CreateLoop(provider, [tool], store);

        var events = await RunToCompletionAsync(loop, transcript, "hi", channel.Reader);

        // Only the first call actually ran; calls 2 and 3 were skipped, not invoked.
        Assert.Single(invocationOrder);

        var skipped = events.OfType<ToolCallSkipped>().ToList();
        Assert.Equal(2, skipped.Count);
        Assert.Equal(["call_2", "call_3"], skipped.Select(s => s.CallId));

        var toolResults = transcript.Messages.SelectMany(m => m.Content).OfType<ToolResultBlock>().ToList();
        Assert.Equal(3, toolResults.Count);
        Assert.False(toolResults.Single(t => t.CallId == "call_1").IsError);
        Assert.Equal("ok", toolResults.Single(t => t.CallId == "call_1").Text);
        Assert.False(toolResults.Single(t => t.CallId == "call_2").IsError);
        Assert.Equal("Skipped due to steering message", toolResults.Single(t => t.CallId == "call_2").Text);
        Assert.Equal("Skipped due to steering message", toolResults.Single(t => t.CallId == "call_3").Text);

        // The steered-in message was appended as a user turn and sent to the model, framed as an
        // interruption (not the raw text verbatim) so the model is nudged to react to it rather
        // than treating it as ordinary chat mid-plan.
        Assert.Contains(transcript.Messages, m => m.Role == Role.User && m.Content.OfType<TextBlock>().Any(t => t.Text.Contains("stop and look at this instead")));
        Assert.Equal(2, provider.ReceivedRequests.Count);
        var secondRequestMessages = provider.ReceivedRequests[1].Messages;
        Assert.Contains(secondRequestMessages, m => m.Content.OfType<TextBlock>().Any(t => t.Text.Contains("stop and look at this instead")));
    }

    [Fact]
    public async Task RunTurnAsync_DuplicateCallIdBeforeSteeredCall_SteersByPosition_NotByCallIdValue()
    {
        // Regression test: pendingToolCalls can contain a duplicate CallId (a misbehaving/
        // fake provider emitting the same id twice — already tolerated by AgentLoopTests'
        // RunTurnAsync_DuplicateToolCallIds_BothInvoked_BothPersisted). Steering must skip by
        // position (everything strictly after the call that was just invoked), not by
        // matching CallId value — matching by value would stop at the first occurrence of
        // "call_1" and incorrectly re-mark the just-invoked second "call_1" (and everything
        // after it) as skipped, corrupting an already-real ToolResult.
        var store = new FakeTranscriptStore();
        var transcript = Transcript.CreateNew("/repo");
        var provider = new FakeChatProvider();
        var args = JsonDocument.Parse("{}").RootElement;
        provider.Enqueue(
            new ToolCallCompleted("call_1", "tool_a", args),
            new ToolCallCompleted("call_1", "tool_b", args),
            new ToolCallCompleted("call_2", "tool_c", args));
        provider.Enqueue(new MessageCompleted(ChatMessage.Assistant([new TextBlock("done")]), new UsageInfo(1, 1)));

        var channel = Channel.CreateUnbounded<SteeringMessage>();
        var invocationOrder = new List<string>();
        var toolA = new FakeTool("tool_a") { Handler = (_, _) => { invocationOrder.Add("tool_a"); return Task.FromResult(ToolResult.Ok("first result")); } };
        var toolB = new FakeTool("tool_b")
        {
            Handler = (_, _) =>
            {
                invocationOrder.Add("tool_b");
                channel.Writer.TryWrite(new SteeringMessage("steer now", SteeringMode.Steer));
                return Task.FromResult(ToolResult.Ok("second result"));
            },
        };
        var toolC = new FakeTool("tool_c") { Handler = (_, _) => { invocationOrder.Add("tool_c"); return Task.FromResult(ToolResult.Ok("third result")); } };
        var loop = CreateLoop(provider, [toolA, toolB, toolC], store);

        var events = await RunToCompletionAsync(loop, transcript, "hi", channel.Reader);

        // tool_a and tool_b both ran (steering arrived while tool_b, the second call, was
        // executing); only tool_c (strictly after tool_b's position) was skipped.
        Assert.Equal(["tool_a", "tool_b"], invocationOrder);
        Assert.Equal(["call_2"], events.OfType<ToolCallSkipped>().Select(s => s.CallId));

        var toolResults = transcript.Messages.SelectMany(m => m.Content).OfType<ToolResultBlock>().ToList();
        Assert.Equal(3, toolResults.Count);
        // Both call_1 results (from tool_a and tool_b) must survive unmodified — neither was
        // overwritten/duplicated by the skip logic.
        var call1Results = toolResults.Where(t => t.CallId == "call_1").ToList();
        Assert.Equal(2, call1Results.Count);
        Assert.Contains(call1Results, t => t.Text == "first result" && !t.IsError);
        Assert.Contains(call1Results, t => t.Text == "second result" && !t.IsError);
        Assert.Equal("Skipped due to steering message", toolResults.Single(t => t.CallId == "call_2").Text);
    }

    [Fact]
    public async Task RunTurnAsync_SteerOnLastPendingCall_NoCallsSkipped_ButSteerMessageStillAppended()
    {
        var store = new FakeTranscriptStore();
        var transcript = Transcript.CreateNew("/repo");
        var provider = new FakeChatProvider();
        var args = JsonDocument.Parse("{}").RootElement;
        provider.Enqueue(new ToolCallCompleted("call_1", "tool_a", args));
        provider.Enqueue(new MessageCompleted(ChatMessage.Assistant([new TextBlock("done")]), new UsageInfo(1, 1)));

        var channel = Channel.CreateUnbounded<SteeringMessage>();
        channel.Writer.TryWrite(new SteeringMessage("one more thing", SteeringMode.Steer));
        var tool = new FakeTool("tool_a") { Handler = (_, _) => Task.FromResult(ToolResult.Ok("ok")) };
        var loop = CreateLoop(provider, [tool], store);

        var events = await RunToCompletionAsync(loop, transcript, "hi", channel.Reader);

        Assert.DoesNotContain(events, e => e is ToolCallSkipped);
        Assert.Contains(transcript.Messages, m => m.Role == Role.User && m.Content.OfType<TextBlock>().Any(t => t.Text.Contains("one more thing")));
    }

    [Fact]
    public async Task RunTurnAsync_SteerMidBatch_IsFramedAsAnInterruption_NotSentAsRawText()
    {
        // Regression test: an unframed steer message reads to the model as ordinary chat
        // dropped mid-conversation, not as an instruction to react to. Observed in practice —
        // the model kept issuing more of the same tool calls the very next round instead of
        // acknowledging the steer. The message sent to the model must therefore carry explicit
        // framing, not just the user's raw text verbatim (see the Contains-based text match:
        // it must still carry the original words, just wrapped).
        var store = new FakeTranscriptStore();
        var transcript = Transcript.CreateNew("/repo");
        var provider = new FakeChatProvider();
        var args = JsonDocument.Parse("{}").RootElement;
        provider.Enqueue(
            new ToolCallCompleted("call_1", "tool_a", args),
            new ToolCallCompleted("call_2", "tool_a", args));
        provider.Enqueue(new MessageCompleted(ChatMessage.Assistant([new TextBlock("done")]), new UsageInfo(1, 1)));

        var channel = Channel.CreateUnbounded<SteeringMessage>();
        var tool = new FakeTool("tool_a")
        {
            Handler = (_, _) =>
            {
                channel.Writer.TryWrite(new SteeringMessage("Hang on", SteeringMode.Steer));
                return Task.FromResult(ToolResult.Ok("ok"));
            },
        };
        var loop = CreateLoop(provider, [tool], store);

        await RunToCompletionAsync(loop, transcript, "hi", channel.Reader);

        var steeredMessage = transcript.Messages
            .Single(m => m.Role == Role.User && m.Content.OfType<TextBlock>().Any(t => t.Text.Contains("Hang on")));
        var text = steeredMessage.Content.OfType<TextBlock>().Single().Text;

        Assert.NotEqual("Hang on", text);
        Assert.Contains("interrupted", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunTurnAsync_FollowUpQueuedDuringToolBatch_NotDeliveredUntilBatchCompletes()
    {
        var store = new FakeTranscriptStore();
        var transcript = Transcript.CreateNew("/repo");
        var provider = new FakeChatProvider();
        var args = JsonDocument.Parse("{}").RootElement;
        provider.Enqueue(new ToolCallCompleted("call_1", "tool_a", args));
        provider.Enqueue(new MessageCompleted(ChatMessage.Assistant([new TextBlock("first round done")]), new UsageInfo(1, 1)));

        var channel = Channel.CreateUnbounded<SteeringMessage>();
        channel.Writer.TryWrite(new SteeringMessage("do this after you finish", SteeringMode.FollowUp));
        var tool = new FakeTool("tool_a") { Handler = (_, _) => Task.FromResult(ToolResult.Ok("ok")) };
        var loop = CreateLoop(provider, [tool], store);

        var events = await RunToCompletionAsync(loop, transcript, "hi", channel.Reader);

        // FollowUp is not a Steer, so it must not skip the pending tool call, and the tool
        // still ran normally.
        Assert.DoesNotContain(events, e => e is ToolCallSkipped);
        var toolResult = transcript.Messages.SelectMany(m => m.Content).OfType<ToolResultBlock>().Single();
        Assert.Equal("ok", toolResult.Text);
    }

    [Fact]
    public async Task RunTurnAsync_FollowUpDeliveredAfterNoPendingToolCalls_TriggersAnotherRound()
    {
        var store = new FakeTranscriptStore();
        var transcript = Transcript.CreateNew("/repo");
        var provider = new FakeChatProvider();
        provider.Enqueue(new MessageCompleted(ChatMessage.Assistant([new TextBlock("first round done")]), new UsageInfo(1, 1)));
        provider.Enqueue(new MessageCompleted(ChatMessage.Assistant([new TextBlock("second round done")]), new UsageInfo(1, 1)));

        var channel = Channel.CreateUnbounded<SteeringMessage>();
        channel.Writer.TryWrite(new SteeringMessage("now do this too", SteeringMode.FollowUp));
        var loop = CreateLoop(provider, tools: [], store);

        await RunToCompletionAsync(loop, transcript, "hi", channel.Reader);

        Assert.Equal(2, provider.ReceivedRequests.Count);
        Assert.Contains(transcript.Messages, m => m.Role == Role.User && m.Content.OfType<TextBlock>().Any(t => t.Text == "now do this too"));
        Assert.Equal(2, transcript.Messages.Count(m => m.Role == Role.Assistant));
    }

    [Fact]
    public async Task RunTurnAsync_EmptySteeringChannel_NoOp_TurnEndsNormally()
    {
        var store = new FakeTranscriptStore();
        var transcript = Transcript.CreateNew("/repo");
        var provider = new FakeChatProvider();
        provider.Enqueue(new MessageCompleted(ChatMessage.Assistant([new TextBlock("done")]), new UsageInfo(1, 1)));

        var channel = Channel.CreateUnbounded<SteeringMessage>();
        var loop = CreateLoop(provider, tools: [], store);

        var events = await RunToCompletionAsync(loop, transcript, "hi", channel.Reader);

        Assert.Single(provider.ReceivedRequests);
        Assert.DoesNotContain(events, e => e is ToolCallSkipped);
    }
}
