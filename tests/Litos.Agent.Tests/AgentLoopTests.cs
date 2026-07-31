using System.Text.Json;
using Litos.Agent.Messages;
using Litos.Agent.Session;
using Litos.Agent.Streaming;
using Litos.Agent.Tests.Fakes;
using Litos.Agent.Tools;

namespace Litos.Agent.Tests;

public class AgentLoopTests
{
    private static readonly SessionOwner Owner = SessionOwner.Local;
    private const string SessionId = "session-1";
    private const string Model = "model";

    private static AgentLoop CreateLoop(
        FakeChatProvider provider,
        IEnumerable<ITool>? tools = null,
        FakeTranscriptStore? store = null,
        ISystemPromptProvider? systemPromptProvider = null,
        Compactor? compactor = null,
        TimeSpan? streamIdleTimeout = null) =>
        new(
            provider,
            new ToolRegistry(tools ?? []),
            store ?? new FakeTranscriptStore(),
            new ContextAccountant(),
            systemPromptProvider,
            compactor,
            streamIdleTimeout);

    private static async Task<List<AgentEvent>> RunToCompletionAsync(AgentLoop loop, Transcript transcript, string userInput)
    {
        var events = new List<AgentEvent>();
        await foreach (var evt in loop.RunTurnAsync(Owner, SessionId, transcript, Model, userInput, CancellationToken.None))
            events.Add(evt);
        return events;
    }

    // ---- Session header ----

    [Fact]
    public async Task RunTurnAsync_WritesSessionHeader_OnFirstTurnOfNewSessionWithWorkingDirectory()
    {
        var store = new FakeTranscriptStore();
        var transcript = Transcript.CreateNew("/repo");
        var provider = new FakeChatProvider();
        provider.Enqueue(new TextDelta("hi"), new MessageCompleted(ChatMessage.Assistant([new TextBlock("hi")]), new UsageInfo(1, 1)));
        var loop = CreateLoop(provider, store: store);

        await RunToCompletionAsync(loop, transcript, "hello");

        var header = Assert.Single(store.AppendedEntries, e => e.Kind == "session");
        Assert.Equal("/repo", header.WorkingDirectory);
    }

    [Fact]
    public async Task RunTurnAsync_DoesNotWriteSessionHeader_OnSecondTurn()
    {
        var store = new FakeTranscriptStore();
        var transcript = Transcript.CreateNew("/repo");
        var provider = new FakeChatProvider();
        provider.Enqueue(new MessageCompleted(ChatMessage.Assistant([new TextBlock("hi")]), new UsageInfo(1, 1)));
        var loop = CreateLoop(provider, store: store);
        await RunToCompletionAsync(loop, transcript, "first");

        store.AppendedEntries.Clear();
        provider.Enqueue(new MessageCompleted(ChatMessage.Assistant([new TextBlock("again")]), new UsageInfo(1, 1)));
        await RunToCompletionAsync(loop, transcript, "second");

        Assert.DoesNotContain(store.AppendedEntries, e => e.Kind == "session");
    }

    [Fact]
    public async Task RunTurnAsync_DoesNotWriteSessionHeader_WhenWorkingDirectoryIsNull()
    {
        var store = new FakeTranscriptStore();
        var transcript = new TranscriptWithNoWorkingDirectoryFactory().Create();
        var provider = new FakeChatProvider();
        provider.Enqueue(new MessageCompleted(ChatMessage.Assistant([new TextBlock("hi")]), new UsageInfo(1, 1)));
        var loop = CreateLoop(provider, store: store);

        await RunToCompletionAsync(loop, transcript, "hello");

        Assert.DoesNotContain(store.AppendedEntries, e => e.Kind == "session");
    }

    // ---- User message persistence ----

    [Fact]
    public async Task RunTurnAsync_AlwaysAppendsAndPersistsUserMessage()
    {
        var store = new FakeTranscriptStore();
        var transcript = Transcript.CreateNew("/repo");
        var provider = new FakeChatProvider();
        provider.Enqueue(new MessageCompleted(ChatMessage.Assistant([new TextBlock("hi")]), new UsageInfo(1, 1)));
        var loop = CreateLoop(provider, store: store);

        await RunToCompletionAsync(loop, transcript, "hello there");

        Assert.Contains(transcript.Messages, m => m.Role == Role.User && m.Content.OfType<TextBlock>().Any(t => t.Text == "hello there"));
        Assert.Contains(store.AppendedEntries, e => e.Kind == "user" && e.Message!.Content.OfType<TextBlock>().Any(t => t.Text == "hello there"));
    }

    [Fact]
    public async Task RunTurnAsync_StringOverload_WrapsInputAsSingleTextBlock()
    {
        var transcript = Transcript.CreateNew("/repo");
        var provider = new FakeChatProvider();
        provider.Enqueue(new MessageCompleted(ChatMessage.Assistant([new TextBlock("hi")]), new UsageInfo(1, 1)));
        var loop = CreateLoop(provider);

        await RunToCompletionAsync(loop, transcript, "plain text");

        var userMessage = transcript.Messages[0];
        var block = Assert.IsType<TextBlock>(Assert.Single(userMessage.Content));
        Assert.Equal("plain text", block.Text);
    }

    [Fact]
    public async Task RunTurnAsync_ContentBlockOverload_PassesContentThrough()
    {
        var transcript = Transcript.CreateNew("/repo");
        var provider = new FakeChatProvider();
        provider.Enqueue(new MessageCompleted(ChatMessage.Assistant([new TextBlock("hi")]), new UsageInfo(1, 1)));
        var loop = CreateLoop(provider);

        IReadOnlyList<ContentBlock> content = [new TextBlock("caption"), new ImageBlock("image/png", [1, 2, 3])];
        var events = new List<AgentEvent>();
        await foreach (var evt in loop.RunTurnAsync(Owner, SessionId, transcript, Model, content, CancellationToken.None))
            events.Add(evt);

        var userMessage = transcript.Messages[0];
        Assert.Equal(2, userMessage.Content.Count);
        Assert.IsType<TextBlock>(userMessage.Content[0]);
        Assert.IsType<ImageBlock>(userMessage.Content[1]);
    }

    // ---- Compaction ----

    [Fact]
    public async Task RunTurnAsync_SkipsCompaction_WhenCompactorIsNull()
    {
        var transcript = Transcript.CreateNew("/repo");
        var provider = new FakeChatProvider();
        provider.Enqueue(new MessageCompleted(ChatMessage.Assistant([new TextBlock("hi")]), new UsageInfo(1, 1)));
        var loop = CreateLoop(provider, compactor: null);

        var events = await RunToCompletionAsync(loop, transcript, "hi");

        Assert.DoesNotContain(events, e => e is CompactionOccurred);
    }

    [Fact]
    public async Task RunTurnAsync_SkipsCompaction_WhenTryCompactAsyncReturnsFalse()
    {
        var transcript = Transcript.CreateNew("/repo");
        transcript.Append(ChatMessage.User("not enough usage to trigger compaction"));
        var provider = new FakeChatProvider();
        provider.Enqueue(new MessageCompleted(ChatMessage.Assistant([new TextBlock("hi")]), new UsageInfo(1, 1)));
        var compactor = new Compactor(new CompactionSettings());
        var loop = CreateLoop(provider, compactor: compactor);

        var events = await RunToCompletionAsync(loop, transcript, "hi");

        Assert.DoesNotContain(events, e => e is CompactionOccurred);
    }

    [Fact]
    public async Task RunTurnAsync_OnSuccessfulCompaction_EmitsCompactionOccurred_AndPersistsSummary()
    {
        var store = new FakeTranscriptStore();
        var transcript = Transcript.CreateNew("/repo");
        transcript.Append(ChatMessage.User(new string('a', 100_000)));
        transcript.Append(ChatMessage.Assistant([new TextBlock(new string('b', 100_000))]), new UsageInfo(190_000, 0));

        var provider = new FakeChatProvider();
        provider.Enqueue(new TextDelta("compacted summary")); // consumed by Compactor's SummarizeAsync
        provider.Enqueue(new MessageCompleted(ChatMessage.Assistant([new TextBlock("ok")]), new UsageInfo(1, 1))); // the actual turn's response
        var compactor = new Compactor(new CompactionSettings());
        var loop = CreateLoop(provider, store: store, compactor: compactor);

        var events = await RunToCompletionAsync(loop, transcript, "trigger compaction");

        // TokensBefore reflects EstimatedTokensUsed at the moment compaction fires, i.e. after
        // the new user message is appended: 190_000 (LastUsage) + 4 (the appended "trigger
        // compaction" message's estimated chars/4 cost, since it has no real usage recorded yet).
        var compactionEvent = Assert.IsType<CompactionOccurred>(Assert.Single(events, e => e is CompactionOccurred));
        Assert.Equal(190_004, compactionEvent.TokensBefore);
        Assert.Contains(store.AppendedEntries, e => e.Message?.Content.OfType<CompactionSummaryBlock>().Any() == true);
    }

    // ---- System prompt ----

    [Fact]
    public async Task RunTurnAsync_WithNullSystemPromptProvider_DoesNotBuildSystemPrompt_AndSendsNull()
    {
        var transcript = Transcript.CreateNew("/repo");
        var provider = new FakeChatProvider();
        provider.Enqueue(new MessageCompleted(ChatMessage.Assistant([new TextBlock("hi")]), new UsageInfo(1, 1)));
        var loop = CreateLoop(provider, systemPromptProvider: null);

        await RunToCompletionAsync(loop, transcript, "hi");

        Assert.Null(provider.ReceivedRequests[0].SystemPrompt);
    }

    [Fact]
    public async Task RunTurnAsync_WithSystemPromptProvider_PassesWorkingDirectory_AndUsesReturnedPrompt()
    {
        var transcript = Transcript.CreateNew("/repo");
        var provider = new FakeChatProvider();
        provider.Enqueue(new MessageCompleted(ChatMessage.Assistant([new TextBlock("hi")]), new UsageInfo(1, 1)));
        var promptProvider = new FakeSystemPromptProvider("you are a helpful agent");
        var loop = CreateLoop(provider, systemPromptProvider: promptProvider);

        await RunToCompletionAsync(loop, transcript, "hi");

        Assert.Equal(["/repo"], promptProvider.ReceivedWorkingDirectories);
        Assert.Equal("you are a helpful agent", provider.ReceivedRequests[0].SystemPrompt);
    }

    [Fact]
    public async Task RunTurnAsync_SystemPromptProviderReturningNull_ResultsInNullSystemPrompt()
    {
        var transcript = Transcript.CreateNew("/repo");
        var provider = new FakeChatProvider();
        provider.Enqueue(new MessageCompleted(ChatMessage.Assistant([new TextBlock("hi")]), new UsageInfo(1, 1)));
        var promptProvider = new FakeSystemPromptProvider(null);
        var loop = CreateLoop(provider, systemPromptProvider: promptProvider);

        await RunToCompletionAsync(loop, transcript, "hi");

        Assert.Null(provider.ReceivedRequests[0].SystemPrompt);
    }

    // ---- Single-round, no tools ----

    [Fact]
    public async Task RunTurnAsync_NoToolCalls_EndsTurn_AfterOneRound()
    {
        var transcript = Transcript.CreateNew("/repo");
        var provider = new FakeChatProvider();
        provider.Enqueue(new TextDelta("hello"), new MessageCompleted(ChatMessage.Assistant([new TextBlock("hello")]), new UsageInfo(5, 3)));
        var loop = CreateLoop(provider);

        var events = await RunToCompletionAsync(loop, transcript, "hi");

        Assert.Single(provider.ReceivedRequests);
        Assert.Contains(events, e => e is TextDelta { Text: "hello" });
        Assert.Contains(events, e => e is MessageCompleted);
    }

    [Fact]
    public async Task RunTurnAsync_ProviderThrows_PropagatesUncaught_UnlikeToolExceptions()
    {
        // Unlike a tool's exceptions (guarded by InvokeToolSafelyAsync, see the
        // InvokeToolSafelyAsync guard tests below), nothing wraps provider.StreamAsync
        // itself — a provider-level failure (network error, auth failure) is expected to
        // fault the whole IAsyncEnumerable rather than becoming a recoverable result.
        var transcript = Transcript.CreateNew("/repo");
        var provider = new FakeChatProvider();
        provider.EnqueueThrow(new InvalidOperationException("connection reset"));
        var loop = CreateLoop(provider);

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunToCompletionAsync(loop, transcript, "hi"));
    }

    [Fact]
    public async Task RunTurnAsync_YieldsProviderEvents_InOrder()
    {
        var transcript = Transcript.CreateNew("/repo");
        var provider = new FakeChatProvider();
        var messageCompleted = new MessageCompleted(ChatMessage.Assistant([new TextBlock("hello")]), new UsageInfo(5, 3));
        provider.Enqueue(new TextDelta("he"), new TextDelta("llo"), messageCompleted);
        var loop = CreateLoop(provider);

        var events = await RunToCompletionAsync(loop, transcript, "hi");

        Assert.Equal(
            [typeof(TextDelta), typeof(TextDelta), typeof(MessageCompleted)],
            events.Select(e => e.GetType()));
    }

    [Fact]
    public async Task RunTurnAsync_ErrorOccurredWithNoToolCallsOrMessageCompleted_EndsTurnCleanly()
    {
        var transcript = Transcript.CreateNew("/repo");
        var provider = new FakeChatProvider();
        provider.Enqueue(new ErrorOccurred(new InvalidOperationException("provider failed")));
        var loop = CreateLoop(provider);

        var events = await RunToCompletionAsync(loop, transcript, "hi");

        Assert.Single(events);
        Assert.IsType<ErrorOccurred>(events[0]);
        // Only the initial user message was persisted; no assistant message exists to append.
        Assert.Single(transcript.Messages);
    }

    // ---- Stream idle timeout ----

    [Fact]
    public async Task RunTurnAsync_ProviderStreamHangs_YieldsErrorOccurred_AfterIdleTimeout_InsteadOfHangingForever()
    {
        var transcript = Transcript.CreateNew("/repo");
        var provider = new FakeChatProvider();
        provider.EnqueueHang();
        var loop = CreateLoop(provider, streamIdleTimeout: TimeSpan.FromMilliseconds(50));

        var events = await RunToCompletionAsync(loop, transcript, "hi");

        var errorEvent = Assert.IsType<ErrorOccurred>(Assert.Single(events));
        Assert.IsType<TimeoutException>(errorEvent.Exception);
    }

    [Fact]
    public async Task RunTurnAsync_ProviderStreamHangsMidResponse_YieldsPartialEvents_ThenErrorOccurred()
    {
        // Confirms this is an idle-GAP timeout, not an absolute cap: events that already
        // streamed before the hang are preserved, and only the silence after them times out.
        var transcript = Transcript.CreateNew("/repo");
        var provider = new FakeChatProvider();
        provider.EnqueueHang(new TextDelta("partial reply"));
        var loop = CreateLoop(provider, streamIdleTimeout: TimeSpan.FromMilliseconds(50));

        var events = await RunToCompletionAsync(loop, transcript, "hi");

        Assert.Equal(2, events.Count);
        Assert.IsType<TextDelta>(events[0]);
        Assert.IsType<ErrorOccurred>(events[1]);
    }

    [Fact]
    public async Task RunTurnAsync_ProviderRespondsWithinIdleTimeout_DoesNotTimeOut()
    {
        var transcript = Transcript.CreateNew("/repo");
        var provider = new FakeChatProvider();
        provider.Enqueue(new MessageCompleted(ChatMessage.Assistant([new TextBlock("hi")]), new UsageInfo(1, 1)));
        var loop = CreateLoop(provider, streamIdleTimeout: TimeSpan.FromSeconds(30));

        var events = await RunToCompletionAsync(loop, transcript, "hi");

        Assert.DoesNotContain(events, e => e is ErrorOccurred);
        Assert.Contains(events, e => e is MessageCompleted);
    }

    [Fact]
    public async Task RunTurnAsync_DefaultConstructor_UsesSixtySecondIdleTimeout()
    {
        // Regression guard for the default: if this silently regressed to e.g. 0 or a tiny
        // value, a normal (non-hanging) turn would spuriously fail with a TimeoutException.
        var transcript = Transcript.CreateNew("/repo");
        var provider = new FakeChatProvider();
        provider.Enqueue(new MessageCompleted(ChatMessage.Assistant([new TextBlock("hi")]), new UsageInfo(1, 1)));
        var loop = CreateLoop(provider); // no streamIdleTimeout override — exercises the default

        var events = await RunToCompletionAsync(loop, transcript, "hi");

        Assert.DoesNotContain(events, e => e is ErrorOccurred);
    }

    [Fact]
    public async Task RunTurnAsync_UserCancelsWhileStreamIsWaiting_ThrowsOperationCanceled_NotTimeoutErrorEvent()
    {
        // Regression test: cancelling the caller's token while a MoveNextAsync call is
        // in-flight ALSO cancels the linked streamCts used to race the idle timer (see
        // MoveNextWithIdleTimeoutAsync), so Task.Delay(idleTimeout, streamCts.Token) wins that
        // race instantly — indistinguishable, without the callerCt check, from a genuine idle
        // timeout. Previously this meant pressing Cancel produced a misleading "connection
        // appears stalled" TimeoutException wrapped in ErrorOccurred instead of a clean abort,
        // and — because TimeoutException doesn't match RunTurnAsync's
        // `catch (OperationCanceledException) when (ct.IsCancellationRequested)` — the turn
        // didn't propagate cancellation to the caller the way a real user-abort must.
        var transcript = Transcript.CreateNew("/repo");
        var provider = new FakeChatProvider();
        provider.EnqueueHang();
        // Deliberately much longer than the cancel delay below, so only a real idle timeout
        // (not this test's cancellation) could otherwise make the race resolve via the timer.
        var loop = CreateLoop(provider, streamIdleTimeout: TimeSpan.FromSeconds(30));

        using var cts = new CancellationTokenSource();
        var enumerator = loop.RunTurnAsync(Owner, SessionId, transcript, Model, "hi", cts.Token).GetAsyncEnumerator();
        cts.CancelAfter(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync());
    }

    // ---- Multi-round tool calling ----

    [Fact]
    public async Task RunTurnAsync_ToolCall_InvokesTool_AppendsResult_AndContinuesToSecondRound()
    {
        var transcript = Transcript.CreateNew("/repo");
        var provider = new FakeChatProvider();
        var toolArgs = JsonDocument.Parse("""{"path":"foo.cs"}""").RootElement;
        provider.Enqueue(new ToolCallCompleted("call_1", "read_file", toolArgs));
        provider.Enqueue(new MessageCompleted(ChatMessage.Assistant([new TextBlock("done")]), new UsageInfo(1, 1)));

        var tool = new FakeTool("read_file");
        tool.Handler = (_, _) => Task.FromResult(ToolResult.Ok("file contents"));
        var loop = CreateLoop(provider, tools: [tool]);

        var events = await RunToCompletionAsync(loop, transcript, "read foo.cs");

        Assert.Equal(2, provider.ReceivedRequests.Count);
        Assert.Single(tool.ReceivedArguments);
        Assert.Contains(transcript.Messages, m => m.Content.OfType<ToolResultBlock>().Any(t => t.Text == "file contents"));
        Assert.Contains(events, e => e is ToolCallCompleted);
    }

    [Fact]
    public async Task RunTurnAsync_ToolCall_YieldsToolCallResult_AfterToolCallCompleted_WithMatchingCallId()
    {
        var transcript = Transcript.CreateNew("/repo");
        var provider = new FakeChatProvider();
        var toolArgs = JsonDocument.Parse("""{"path":"foo.cs"}""").RootElement;
        provider.Enqueue(new ToolCallCompleted("call_1", "read_file", toolArgs));
        provider.Enqueue(new MessageCompleted(ChatMessage.Assistant([new TextBlock("done")]), new UsageInfo(1, 1)));

        var tool = new FakeTool("read_file") { Handler = (_, _) => Task.FromResult(ToolResult.Ok("file contents")) };
        var loop = CreateLoop(provider, tools: [tool]);

        var events = await RunToCompletionAsync(loop, transcript, "read foo.cs");

        var completedIndex = events.FindIndex(e => e is ToolCallCompleted);
        var resultIndex = events.FindIndex(e => e is ToolCallResult);
        Assert.True(completedIndex >= 0 && resultIndex > completedIndex);

        var resultEvent = Assert.IsType<ToolCallResult>(events[resultIndex]);
        Assert.Equal("call_1", resultEvent.CallId);
        Assert.Equal("read_file", resultEvent.ToolName);
        Assert.False(resultEvent.Result.IsError);
        Assert.Equal("file contents", resultEvent.Result.Text);
    }

    [Fact]
    public async Task RunTurnAsync_ToolCallFails_YieldsToolCallResult_WithIsErrorTrue()
    {
        var transcript = Transcript.CreateNew("/repo");
        var provider = new FakeChatProvider();
        var args = JsonDocument.Parse("{}").RootElement;
        provider.Enqueue(new ToolCallCompleted("call_1", "read_file", args));
        provider.Enqueue(new MessageCompleted(ChatMessage.Assistant([new TextBlock("done")]), new UsageInfo(1, 1)));

        var tool = new FakeTool("read_file") { Handler = (_, _) => Task.FromResult(ToolResult.Error("File not found: missing.txt")) };
        var loop = CreateLoop(provider, tools: [tool]);

        var events = await RunToCompletionAsync(loop, transcript, "read missing.txt");

        var resultEvent = Assert.IsType<ToolCallResult>(Assert.Single(events, e => e is ToolCallResult));
        Assert.True(resultEvent.Result.IsError);
        Assert.Equal("File not found: missing.txt", resultEvent.Result.Text);
    }

    [Fact]
    public async Task RunTurnAsync_SequentialToolCalls_EachYieldsItsOwnToolCallResult_InOrder()
    {
        var transcript = Transcript.CreateNew("/repo");
        var provider = new FakeChatProvider();
        var args = JsonDocument.Parse("{}").RootElement;
        provider.Enqueue(
            new ToolCallCompleted("call_1", "tool_a", args),
            new ToolCallCompleted("call_2", "tool_b", args));
        provider.Enqueue(new MessageCompleted(ChatMessage.Assistant([new TextBlock("done")]), new UsageInfo(1, 1)));

        var toolA = new FakeTool("tool_a") { Handler = (_, _) => Task.FromResult(ToolResult.Ok("a result")) };
        var toolB = new FakeTool("tool_b") { Handler = (_, _) => Task.FromResult(ToolResult.Ok("b result")) };
        var loop = CreateLoop(provider, tools: [toolA, toolB]);

        var events = await RunToCompletionAsync(loop, transcript, "run both");

        var results = events.OfType<ToolCallResult>().ToList();
        Assert.Equal(2, results.Count);
        Assert.Equal(["call_1", "call_2"], results.Select(r => r.CallId));
        Assert.Equal("a result", results[0].Result.Text);
        Assert.Equal("b result", results[1].Result.Text);
    }

    [Fact]
    public async Task RunTurnAsync_SecondRoundRequest_IncludesToolResultFromFirstRound()
    {
        var transcript = Transcript.CreateNew("/repo");
        var provider = new FakeChatProvider();
        var toolArgs = JsonDocument.Parse("{}").RootElement;
        provider.Enqueue(new ToolCallCompleted("call_1", "read_file", toolArgs));
        provider.Enqueue(new MessageCompleted(ChatMessage.Assistant([new TextBlock("done")]), new UsageInfo(1, 1)));

        var tool = new FakeTool("read_file");
        tool.Handler = (_, _) => Task.FromResult(ToolResult.Ok("file contents"));
        var loop = CreateLoop(provider, tools: [tool]);

        await RunToCompletionAsync(loop, transcript, "read it");

        var secondRequest = provider.ReceivedRequests[1];
        Assert.Contains(secondRequest.Messages, m => m.Content.OfType<ToolResultBlock>().Any(t => t.Text == "file contents"));
    }

    [Fact]
    public async Task RunTurnAsync_SequentialToolCalls_ExecuteInOrder_AndAllPersist()
    {
        var transcript = Transcript.CreateNew("/repo");
        var provider = new FakeChatProvider();
        var args = JsonDocument.Parse("{}").RootElement;
        provider.Enqueue(
            new ToolCallCompleted("call_1", "tool_a", args),
            new ToolCallCompleted("call_2", "tool_b", args));
        provider.Enqueue(new MessageCompleted(ChatMessage.Assistant([new TextBlock("done")]), new UsageInfo(1, 1)));

        var invocationOrder = new List<string>();
        var toolA = new FakeTool("tool_a") { Handler = (_, _) => { invocationOrder.Add("tool_a"); return Task.FromResult(ToolResult.Ok("a result")); } };
        var toolB = new FakeTool("tool_b") { Handler = (_, _) => { invocationOrder.Add("tool_b"); return Task.FromResult(ToolResult.Ok("b result")); } };
        var loop = CreateLoop(provider, tools: [toolA, toolB]);

        await RunToCompletionAsync(loop, transcript, "run both");

        Assert.Equal(["tool_a", "tool_b"], invocationOrder);
        Assert.Contains(transcript.Messages, m => m.Content.OfType<ToolResultBlock>().Any(t => t.CallId == "call_1" && t.Text == "a result"));
        Assert.Contains(transcript.Messages, m => m.Content.OfType<ToolResultBlock>().Any(t => t.CallId == "call_2" && t.Text == "b result"));
    }

    [Fact]
    public async Task RunTurnAsync_DuplicateToolCallIds_BothInvoked_BothPersisted()
    {
        var transcript = Transcript.CreateNew("/repo");
        var provider = new FakeChatProvider();
        var args = JsonDocument.Parse("{}").RootElement;
        // A misbehaving/fake provider emitting the same CallId twice â€” AgentLoop has no dedup guard.
        provider.Enqueue(
            new ToolCallCompleted("call_1", "tool_a", args),
            new ToolCallCompleted("call_1", "tool_a", args));
        provider.Enqueue(new MessageCompleted(ChatMessage.Assistant([new TextBlock("done")]), new UsageInfo(1, 1)));

        var invocationCount = 0;
        var tool = new FakeTool("tool_a") { Handler = (_, _) => { invocationCount++; return Task.FromResult(ToolResult.Ok("result")); } };
        var loop = CreateLoop(provider, tools: [tool]);

        await RunToCompletionAsync(loop, transcript, "trigger dup");

        Assert.Equal(2, invocationCount);
        Assert.Equal(2, transcript.Messages.Count(m => m.Content.OfType<ToolResultBlock>().Any(t => t.CallId == "call_1")));
    }

    [Fact]
    public async Task RunTurnAsync_MultipleMessageCompletedInOneStream_EachAppendedSeparately()
    {
        var transcript = Transcript.CreateNew("/repo");
        var provider = new FakeChatProvider();
        var first = new MessageCompleted(ChatMessage.Assistant([new TextBlock("first")]), new UsageInfo(1, 1));
        var second = new MessageCompleted(ChatMessage.Assistant([new TextBlock("second")]), new UsageInfo(2, 2));
        provider.Enqueue(first, second);
        var loop = CreateLoop(provider);

        await RunToCompletionAsync(loop, transcript, "hi");

        Assert.Equal(2, transcript.Messages.Count(m => m.Role == Role.Assistant));
    }

    [Fact]
    public async Task RunTurnAsync_TranscriptReflectsMessage_AtTheMomentMessageCompletedIsYielded()
    {
        // Regression test: transcript.Append(m.Message) must happen BEFORE MessageCompleted is
        // yielded, not after — a consumer reacting to the yielded event (e.g. a GUI reading
        // transcript.LastUsage to refresh a status-bar meter) needs the transcript to already
        // include this message's usage, not the state from before it. Getting this backwards
        // previously left such a consumer one message behind until the next turn.
        var transcript = Transcript.CreateNew("/repo");
        var provider = new FakeChatProvider();
        provider.Enqueue(new MessageCompleted(ChatMessage.Assistant([new TextBlock("hi")]), new UsageInfo(42, 7)));
        var loop = CreateLoop(provider);

        UsageInfo? lastUsageAtYieldTime = null;
        await foreach (var evt in loop.RunTurnAsync(Owner, SessionId, transcript, Model, "hi", CancellationToken.None))
        {
            if (evt is MessageCompleted)
                lastUsageAtYieldTime = transcript.LastUsage;
        }

        Assert.NotNull(lastUsageAtYieldTime);
        Assert.Equal(42, lastUsageAtYieldTime!.InputTokens);
        Assert.Equal(7, lastUsageAtYieldTime.OutputTokens);
    }

    [Fact]
    public async Task RunTurnAsync_MultipleToolRounds_ContinueUntilNoPendingToolCalls()
    {
        var transcript = Transcript.CreateNew("/repo");
        var provider = new FakeChatProvider();
        var args = JsonDocument.Parse("{}").RootElement;
        provider.Enqueue(new ToolCallCompleted("call_1", "tool_a", args));
        provider.Enqueue(new ToolCallCompleted("call_2", "tool_a", args));
        provider.Enqueue(new MessageCompleted(ChatMessage.Assistant([new TextBlock("done")]), new UsageInfo(1, 1)));

        var tool = new FakeTool("tool_a") { Handler = (_, _) => Task.FromResult(ToolResult.Ok("ok")) };
        var loop = CreateLoop(provider, tools: [tool]);

        await RunToCompletionAsync(loop, transcript, "multi-round");

        Assert.Equal(3, provider.ReceivedRequests.Count);
    }

    // ---- InvokeToolSafelyAsync guard ----

    [Fact]
    public async Task RunTurnAsync_UnknownToolName_ProducesComposedErrorResult_WithoutCrashingTurn()
    {
        var transcript = Transcript.CreateNew("/repo");
        var provider = new FakeChatProvider();
        var args = JsonDocument.Parse("{}").RootElement;
        provider.Enqueue(new ToolCallCompleted("call_1", "hallucinated_tool", args));
        provider.Enqueue(new MessageCompleted(ChatMessage.Assistant([new TextBlock("done")]), new UsageInfo(1, 1)));
        var loop = CreateLoop(provider, tools: []); // no tools registered

        await RunToCompletionAsync(loop, transcript, "call unknown tool");

        var resultBlock = transcript.Messages
            .SelectMany(m => m.Content)
            .OfType<ToolResultBlock>()
            .Single(t => t.CallId == "call_1");
        Assert.True(resultBlock.IsError);
        Assert.Equal("Tool 'hallucinated_tool' failed: Unknown tool 'hallucinated_tool'.", resultBlock.Text);
    }

    [Fact]
    public async Task RunTurnAsync_ToolThrowsGenericException_ProducesErrorResult_TurnContinues()
    {
        var transcript = Transcript.CreateNew("/repo");
        var provider = new FakeChatProvider();
        var args = JsonDocument.Parse("{}").RootElement;
        provider.Enqueue(new ToolCallCompleted("call_1", "flaky_tool", args));
        provider.Enqueue(new MessageCompleted(ChatMessage.Assistant([new TextBlock("recovered")]), new UsageInfo(1, 1)));

        var tool = new FakeTool("flaky_tool")
        {
            Handler = (_, _) => throw new IOException("The process cannot access the file because it is being used by another process."),
        };
        var loop = CreateLoop(provider, tools: [tool]);

        var events = await RunToCompletionAsync(loop, transcript, "read locked file");

        var resultBlock = transcript.Messages.SelectMany(m => m.Content).OfType<ToolResultBlock>().Single();
        Assert.True(resultBlock.IsError);
        Assert.Equal("Tool 'flaky_tool' failed: The process cannot access the file because it is being used by another process.", resultBlock.Text);
        // The turn continued to a second round rather than crashing.
        Assert.Equal(2, provider.ReceivedRequests.Count);
        Assert.Contains(events, e => e is MessageCompleted);
    }

    [Fact]
    public async Task RunTurnAsync_ToolThrowsOperationCanceledException_WhenTokenIsCanceled_PropagatesOutOfTurn()
    {
        var transcript = Transcript.CreateNew("/repo");
        var provider = new FakeChatProvider();
        var args = JsonDocument.Parse("{}").RootElement;
        provider.Enqueue(new ToolCallCompleted("call_1", "cancelable_tool", args));

        using var cts = new CancellationTokenSource();
        var tool = new FakeTool("cancelable_tool")
        {
            Handler = (_, ct) =>
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(ToolResult.Ok("unreachable"));
            },
        };
        var loop = CreateLoop(provider, tools: [tool]);

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in loop.RunTurnAsync(Owner, SessionId, transcript, Model, "hi", cts.Token))
            {
            }
        });
    }

    [Fact]
    public async Task RunTurnAsync_ToolThrowsOperationCanceledException_WhenTokenIsNotCanceled_IsCaughtAsGenericError()
    {
        // Distinguishes "the caller's token was canceled" (rethrow) from "the tool threw
        // OperationCanceledException for its own unrelated reason" (caught, becomes a
        // ToolResult.Error) â€” the `when (ct.IsCancellationRequested)` guard on the catch.
        var transcript = Transcript.CreateNew("/repo");
        var provider = new FakeChatProvider();
        var args = JsonDocument.Parse("{}").RootElement;
        provider.Enqueue(new ToolCallCompleted("call_1", "self_timeout_tool", args));
        provider.Enqueue(new MessageCompleted(ChatMessage.Assistant([new TextBlock("done")]), new UsageInfo(1, 1)));

        var tool = new FakeTool("self_timeout_tool")
        {
            Handler = (_, _) => throw new OperationCanceledException("internal timeout, unrelated to caller's token"),
        };
        var loop = CreateLoop(provider, tools: [tool]);

        await RunToCompletionAsync(loop, transcript, "hi");

        var resultBlock = transcript.Messages.SelectMany(m => m.Content).OfType<ToolResultBlock>().Single();
        Assert.True(resultBlock.IsError);
        Assert.Contains("internal timeout, unrelated to caller's token", resultBlock.Text);
        // Confirms the turn was NOT aborted: it reached the second round's MessageCompleted.
        Assert.Equal(2, provider.ReceivedRequests.Count);
    }

    [Fact]
    public async Task RunTurnAsync_ToolSucceeds_ResultTextAndIsErrorPassThroughUnchanged()
    {
        var transcript = Transcript.CreateNew("/repo");
        var provider = new FakeChatProvider();
        var args = JsonDocument.Parse("{}").RootElement;
        provider.Enqueue(new ToolCallCompleted("call_1", "tool_a", args));
        provider.Enqueue(new MessageCompleted(ChatMessage.Assistant([new TextBlock("done")]), new UsageInfo(1, 1)));

        var tool = new FakeTool("tool_a") { Handler = (_, _) => Task.FromResult(ToolResult.Ok("clean result")) };
        var loop = CreateLoop(provider, tools: [tool]);

        await RunToCompletionAsync(loop, transcript, "hi");

        var resultBlock = transcript.Messages.SelectMany(m => m.Content).OfType<ToolResultBlock>().Single();
        Assert.False(resultBlock.IsError);
        Assert.Equal("clean result", resultBlock.Text);
    }

    [Fact]
    public async Task RunTurnAsync_ToolReturnsItsOwnErrorResult_PassesThroughUnchanged_NotDoubleWrapped()
    {
        // A tool's own ToolResult.Error (e.g. "file not found") is unrelated to the
        // InvokeToolSafelyAsync guard and must not be re-wrapped as "Tool 'x' failed: ...".
        var transcript = Transcript.CreateNew("/repo");
        var provider = new FakeChatProvider();
        var args = JsonDocument.Parse("{}").RootElement;
        provider.Enqueue(new ToolCallCompleted("call_1", "read_file", args));
        provider.Enqueue(new MessageCompleted(ChatMessage.Assistant([new TextBlock("done")]), new UsageInfo(1, 1)));

        var tool = new FakeTool("read_file") { Handler = (_, _) => Task.FromResult(ToolResult.Error("File not found: missing.txt")) };
        var loop = CreateLoop(provider, tools: [tool]);

        await RunToCompletionAsync(loop, transcript, "hi");

        var resultBlock = transcript.Messages.SelectMany(m => m.Content).OfType<ToolResultBlock>().Single();
        Assert.True(resultBlock.IsError);
        Assert.Equal("File not found: missing.txt", resultBlock.Text);
    }
}

/// <summary>
/// Transcript has no public way to produce a WorkingDirectory-less instance directly
/// (CreateNew always sets it); this mirrors what Transcript.LoadAsync produces for a
/// session whose store has no "session" kind entry at all.
/// </summary>
file sealed class TranscriptWithNoWorkingDirectoryFactory
{
    public Transcript Create()
    {
        var store = new FakeTranscriptStore();
        return Transcript.LoadAsync(store, SessionOwner.Local, "empty-session", CancellationToken.None).GetAwaiter().GetResult();
    }
}
