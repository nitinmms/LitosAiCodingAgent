using System.Threading.Channels;
using Litos.Agent;
using Litos.Agent.Messages;
using Litos.Agent.Session;
using Litos.Agent.Streaming;
using Litos.Agent.Tools;
using Litos.Api.Tests.Fakes;
using Litos.Host;

namespace Litos.Api.Tests;

public class AgentWorkerTests
{
    private sealed class NoopSystemPromptProvider : ISystemPromptProvider
    {
        public Task<SystemPromptSections?> BuildAsync(ToolRegistry tools, string? workingDirectory, CancellationToken ct) => Task.FromResult<SystemPromptSections?>(null);
    }

    private sealed class FakeToolSource : IToolSource
    {
        public IReadOnlyList<ITool> CurrentTools { get; set; } = [];
    }

    private static (AgentWorker Worker, FakeChatProvider Provider) CreateWorker(ToolRegistryFactory? toolRegistryFactory = null)
    {
        var provider = new FakeChatProvider();
        var factory = new FakeChatProviderFactory(provider);
        var loopFactory = new AgentLoopFactory(
            new FakeTranscriptStore(), new ContextAccountant(),
            new NoopSystemPromptProvider(), new Compactor(new CompactionSettings()));
        var config = new LitosConfig(
            DefaultProvider: "fake", DefaultModel: "fake-model", LastWorkingDirectory: null,
            ApiKeys: new Dictionary<string, string> { ["fake"] = "unused" });

        var worker = new AgentWorker(factory, loopFactory, toolRegistryFactory ?? new ToolRegistryFactory([], []), new FakeTranscriptStore(), config);
        return (worker, provider);
    }

    [Fact]
    public void StartOrSteerTurn_NewSession_ReturnsStartedWithAnEventReader()
    {
        var (worker, _) = CreateWorker();

        var events = worker.StartOrSteerTurn(
            SessionOwner.Local, "session-1", [new TextBlock("hi")], CancellationToken.None, out var outcome);

        Assert.Equal(TurnOutcome.Started, outcome);
        Assert.NotNull(events);
    }

    [Fact]
    public async Task StartOrSteerTurn_SameSessionWhileRunning_Steers()
    {
        var (worker, provider) = CreateWorker();
        var gate = new TaskCompletionSource();
        provider.EnqueueAwaiting(gate.Task, new TextDelta("hi"), new MessageCompleted(ChatMessage.Assistant([new TextBlock("hi")]), new UsageInfo(1, 1)));

        var events = worker.StartOrSteerTurn(
            SessionOwner.Local, "session-1", [new TextBlock("first")], CancellationToken.None, out var firstOutcome);
        Assert.Equal(TurnOutcome.Started, firstOutcome);

        // Wait for the turn to actually be registered as active before steering it — otherwise
        // this races StartOrSteerTurn's own async Task.Run-backed startup.
        await WaitUntilAsync(() => worker.IsTurnActiveFor(SessionOwner.Local, "session-1"));

        var events2 = worker.StartOrSteerTurn(
            SessionOwner.Local, "session-1", [new TextBlock("steer-in")], CancellationToken.None, out var secondOutcome);

        Assert.Equal(TurnOutcome.Steered, secondOutcome);
        Assert.Null(events2);

        gate.SetResult();
        await DrainAsync(events!);
    }

    [Fact]
    public async Task StartOrSteerTurn_DifferentSessions_RunConcurrently_NeitherBlocksTheOther()
    {
        var (worker, provider) = CreateWorker();

        // Both sessions get a default (non-hanging) scripted reply — the assertion that matters
        // is that starting session B while A is still draining doesn't return TurnOutcome.Steered
        // or block: each key gets its own independently-running Task (AgentWorker's whole point).
        var eventsA = worker.StartOrSteerTurn(SessionOwner.Local, "session-a", [new TextBlock("a")], CancellationToken.None, out var outcomeA);
        var eventsB = worker.StartOrSteerTurn(SessionOwner.Telegram, "session-b", [new TextBlock("b")], CancellationToken.None, out var outcomeB);

        Assert.Equal(TurnOutcome.Started, outcomeA);
        Assert.Equal(TurnOutcome.Started, outcomeB);

        var resultsA = await DrainAsync(eventsA!);
        var resultsB = await DrainAsync(eventsB!);

        Assert.NotEmpty(resultsA);
        Assert.NotEmpty(resultsB);
    }

    [Fact]
    public async Task IsTurnActiveFor_ReflectsRunningState_AndClearsOnCompletion()
    {
        var (worker, provider) = CreateWorker();
        var gate = new TaskCompletionSource();
        provider.EnqueueAwaiting(gate.Task, new TextDelta("hi"), new MessageCompleted(ChatMessage.Assistant([new TextBlock("hi")]), new UsageInfo(1, 1)));

        Assert.False(worker.IsTurnActiveFor(SessionOwner.Local, "session-x"));

        var events = worker.StartOrSteerTurn(SessionOwner.Local, "session-x", [new TextBlock("hi")], CancellationToken.None, out var outcome);
        Assert.Equal(TurnOutcome.Started, outcome);

        // Poll briefly for the turn to register as active — StartOrSteerTurn's Task.Run-backed
        // turn starts asynchronously.
        await WaitUntilAsync(() => worker.IsTurnActiveFor(SessionOwner.Local, "session-x"));
        Assert.True(worker.IsTurnActive);

        gate.SetResult();
        await DrainAsync(events!);

        Assert.False(worker.IsTurnActiveFor(SessionOwner.Local, "session-x"));
    }

    // Regression coverage for the HTTP attachment endpoint's queueIfActive path: RenderForSteering
    // only extracts TextBlock, so steering an ImageBlock into a running turn would silently drop
    // it. queueIfActive: true must skip steering entirely and hold the content for the next turn.
    [Fact]
    public async Task StartOrSteerTurn_QueueIfActiveAndTurnRunning_ReturnsQueued_DoesNotSteer()
    {
        var (worker, provider) = CreateWorker();
        var gate = new TaskCompletionSource();
        provider.EnqueueAwaiting(gate.Task, new TextDelta("hi"), new MessageCompleted(ChatMessage.Assistant([new TextBlock("hi")]), new UsageInfo(1, 1)));

        var firstEvents = worker.StartOrSteerTurn(SessionOwner.Local, "session-1", [new TextBlock("first")], CancellationToken.None, out var firstOutcome);
        Assert.Equal(TurnOutcome.Started, firstOutcome);
        await WaitUntilAsync(() => worker.IsTurnActiveFor(SessionOwner.Local, "session-1"));

        var queuedContent = new ContentBlock[] { new TextBlock("with an image"), new ImageBlock("image/png", [1, 2, 3]) };
        var queuedEvents = worker.StartOrSteerTurn(
            SessionOwner.Local, "session-1", queuedContent, CancellationToken.None, out var queuedOutcome, queueIfActive: true);

        Assert.Equal(TurnOutcome.Queued, queuedOutcome);
        Assert.Null(queuedEvents);

        gate.SetResult();
        await DrainAsync(firstEvents!);

        // The first (still-running) turn's own request must not have seen the queued image —
        // proves queueIfActive genuinely skipped steering rather than delivering it late into the
        // same turn.
        Assert.Single(provider.ReceivedMessageLists);
    }

    [Fact]
    public async Task StartOrSteerTurn_QueuedContent_IsPrependedToTheNextTurnThatStartsForThatKey()
    {
        var (worker, provider) = CreateWorker();
        var gate = new TaskCompletionSource();
        provider.EnqueueAwaiting(gate.Task, new TextDelta("hi"), new MessageCompleted(ChatMessage.Assistant([new TextBlock("hi")]), new UsageInfo(1, 1)));

        var firstEvents = worker.StartOrSteerTurn(SessionOwner.Local, "session-1", [new TextBlock("first")], CancellationToken.None, out var firstOutcome);
        Assert.Equal(TurnOutcome.Started, firstOutcome);
        await WaitUntilAsync(() => worker.IsTurnActiveFor(SessionOwner.Local, "session-1"));

        var image = new ImageBlock("image/png", [1, 2, 3]);
        worker.StartOrSteerTurn(
            SessionOwner.Local, "session-1", [new TextBlock("queued text"), image], CancellationToken.None, out var queuedOutcome, queueIfActive: true);
        Assert.Equal(TurnOutcome.Queued, queuedOutcome);

        gate.SetResult();
        await DrainAsync(firstEvents!);
        await WaitUntilAsync(() => !worker.IsTurnActiveFor(SessionOwner.Local, "session-1"));

        // A fresh turn for the same key now picks up the queued content, prepended ahead of
        // whatever this new call supplies.
        var secondEvents = worker.StartOrSteerTurn(
            SessionOwner.Local, "session-1", [new TextBlock("second")], CancellationToken.None, out var secondOutcome);
        Assert.Equal(TurnOutcome.Started, secondOutcome);
        await DrainAsync(secondEvents!);

        Assert.Equal(2, provider.ReceivedMessageLists.Count);
        var secondTurnUserContent = provider.ReceivedMessageLists[1].Last(m => m.Role == Role.User).Content;
        Assert.Contains(secondTurnUserContent, b => b is TextBlock t && t.Text == "queued text");
        Assert.Contains(secondTurnUserContent, b => b is ImageBlock);
        Assert.Contains(secondTurnUserContent, b => b is TextBlock t && t.Text == "second");
    }

    [Fact]
    public async Task StartOrSteerTurn_QueueIfActiveButNoTurnRunning_StartsImmediatelyInstead()
    {
        var (worker, _) = CreateWorker();

        var events = worker.StartOrSteerTurn(
            SessionOwner.Local, "session-fresh", [new TextBlock("hi")], CancellationToken.None, out var outcome, queueIfActive: true);

        Assert.Equal(TurnOutcome.Started, outcome);
        Assert.NotNull(events);
        await DrainAsync(events!);
    }

    [Fact]
    public async Task StartOrSteerTurn_MultipleQueuedCallsWhileBusy_AllAccumulateForTheNextTurn()
    {
        var (worker, provider) = CreateWorker();
        var gate = new TaskCompletionSource();
        provider.EnqueueAwaiting(gate.Task, new TextDelta("hi"), new MessageCompleted(ChatMessage.Assistant([new TextBlock("hi")]), new UsageInfo(1, 1)));

        var firstEvents = worker.StartOrSteerTurn(SessionOwner.Local, "session-1", [new TextBlock("first")], CancellationToken.None, out _);
        await WaitUntilAsync(() => worker.IsTurnActiveFor(SessionOwner.Local, "session-1"));

        worker.StartOrSteerTurn(SessionOwner.Local, "session-1", [new TextBlock("queued-a")], CancellationToken.None, out var outcomeA, queueIfActive: true);
        worker.StartOrSteerTurn(SessionOwner.Local, "session-1", [new TextBlock("queued-b")], CancellationToken.None, out var outcomeB, queueIfActive: true);
        Assert.Equal(TurnOutcome.Queued, outcomeA);
        Assert.Equal(TurnOutcome.Queued, outcomeB);

        gate.SetResult();
        await DrainAsync(firstEvents!);
        await WaitUntilAsync(() => !worker.IsTurnActiveFor(SessionOwner.Local, "session-1"));

        var secondEvents = worker.StartOrSteerTurn(SessionOwner.Local, "session-1", [new TextBlock("second")], CancellationToken.None, out var secondOutcome);
        Assert.Equal(TurnOutcome.Started, secondOutcome);
        await DrainAsync(secondEvents!);

        var secondTurnUserContent = provider.ReceivedMessageLists[1].Last(m => m.Role == Role.User).Content;
        Assert.Contains(secondTurnUserContent, b => b is TextBlock t && t.Text == "queued-a");
        Assert.Contains(secondTurnUserContent, b => b is TextBlock t && t.Text == "queued-b");
        Assert.Contains(secondTurnUserContent, b => b is TextBlock t && t.Text == "second");
    }

    // Regression test for a real race found in review: _activeTurns membership and _pendingContent
    // used to be two independently-locked structures, so content queued right as a turn finished
    // could be stranded — a concurrent fresh-turn start could dequeue (finding nothing) before the
    // racing enqueue for the same key ran, with nothing left to ever drain it afterward. Fixed by
    // putting both under one lock (_turnsLock) so the "is this key active" decision and the
    // enqueue/dequeue are atomic with the turn's own removal on completion. This test fires the
    // queueIfActive call and the first turn's completion essentially simultaneously, many times, to
    // make the race window actually get hit rather than relying on timing to avoid it.
    [Fact]
    public async Task StartOrSteerTurn_QueueRacesTurnCompletion_NeverStrandsContent()
    {
        for (var iteration = 0; iteration < 200; iteration++)
        {
            var (worker, provider) = CreateWorker();
            var gate = new TaskCompletionSource();
            provider.EnqueueAwaiting(gate.Task, new TextDelta("hi"), new MessageCompleted(ChatMessage.Assistant([new TextBlock("hi")]), new UsageInfo(1, 1)));

            var firstEvents = worker.StartOrSteerTurn(SessionOwner.Local, "session-race", [new TextBlock("first")], CancellationToken.None, out _);
            await WaitUntilAsync(() => worker.IsTurnActiveFor(SessionOwner.Local, "session-race"));

            // Release the first turn and attempt to queue content "at the same time" — racing the
            // turn's own _activeTurns removal (triggered by DrainAsync completing) against the
            // queueIfActive call from another logical caller, on two concurrently-running tasks.
            ChannelReader<AgentEvent>? raceEvents = null;
            var completeFirstTurn = Task.Run(async () =>
            {
                gate.SetResult();
                await DrainAsync(firstEvents!);
            });
            var queueAttempt = Task.Run(() =>
            {
                raceEvents = worker.StartOrSteerTurn(
                    SessionOwner.Local, "session-race", [new TextBlock($"queued-{iteration}")], CancellationToken.None,
                    out var outcome, queueIfActive: true);
                return outcome;
            });
            await Task.WhenAll(completeFirstTurn, queueAttempt);
            var raceOutcome = await queueAttempt;

            // Whichever outcome StartOrSteerTurn legitimately returned under the race, the content
            // must be observable somewhere — never silently stranded in _pendingContent forever:
            if (raceOutcome == TurnOutcome.Started)
            {
                // The lock resolved this as "no turn was active" (the first turn had already been
                // removed) — content ran immediately as its own turn. Drain it and move on.
                await DrainAsync(raceEvents!);
                continue;
            }

            Assert.Equal(TurnOutcome.Queued, raceOutcome);
            await WaitUntilAsync(() => !worker.IsTurnActiveFor(SessionOwner.Local, "session-race"));

            var followUpEvents = worker.StartOrSteerTurn(
                SessionOwner.Local, "session-race", [new TextBlock("follow-up")], CancellationToken.None, out var followUpOutcome);
            Assert.Equal(TurnOutcome.Started, followUpOutcome);
            await DrainAsync(followUpEvents!);

            var lastUserContent = provider.ReceivedMessageLists.Last(m => m.Any(msg => msg.Role == Role.User))
                .Last(m => m.Role == Role.User).Content;
            Assert.Contains(lastUserContent, b => b is TextBlock t && t.Text == $"queued-{iteration}");
        }
    }

    [Fact]
    public async Task StartOrSteerTurn_SameSessionIdDifferentOwner_TreatedAsDistinctSessions()
    {
        var (worker, _) = CreateWorker();

        var localEvents = worker.StartOrSteerTurn(SessionOwner.Local, "same-id", [new TextBlock("a")], CancellationToken.None, out var localOutcome);
        var telegramEvents = worker.StartOrSteerTurn(SessionOwner.Telegram, "same-id", [new TextBlock("b")], CancellationToken.None, out var telegramOutcome);

        // Same sessionId string but different SessionOwner — must not steer each other.
        Assert.Equal(TurnOutcome.Started, localOutcome);
        Assert.Equal(TurnOutcome.Started, telegramOutcome);

        await DrainAsync(localEvents!);
        await DrainAsync(telegramEvents!);
    }

    // Regression coverage for dynamic MCP tool discovery: AgentWorker.RunTurnAsync must build a
    // fresh ToolRegistry (via ToolRegistryFactory.Create()) at the start of EVERY turn, not once
    // at construction — otherwise a server connecting after startup would never reach a turn
    // without a container restart.
    [Fact]
    public async Task RunTurnAsync_ToolSourceGainsATool_NextTurnSeesIt()
    {
        var toolSource = new FakeToolSource();
        var toolRegistryFactory = new ToolRegistryFactory([], [toolSource]);
        var (worker, provider) = CreateWorker(toolRegistryFactory);

        // Two distinct session ids (not the same key reused) — this test is about
        // ToolRegistryFactory being re-invoked per RunTurnAsync call, not about session-key
        // reuse/steering semantics, so there's no need to race-guard against the first turn's
        // _activeTurns entry still being present when the second one starts.
        var firstEvents = worker.StartOrSteerTurn(SessionOwner.Local, "session-1", [new TextBlock("first")], CancellationToken.None, out var firstOutcome);
        Assert.Equal(TurnOutcome.Started, firstOutcome);
        await DrainAsync(firstEvents!);

        Assert.Single(provider.ReceivedToolLists);
        Assert.Empty(provider.ReceivedToolLists[0]);

        toolSource.CurrentTools = [new FakeTool("mcp__newserver__read")];

        var secondEvents = worker.StartOrSteerTurn(SessionOwner.Local, "session-2", [new TextBlock("second")], CancellationToken.None, out var secondOutcome);
        Assert.Equal(TurnOutcome.Started, secondOutcome);
        await DrainAsync(secondEvents!);

        Assert.Equal(2, provider.ReceivedToolLists.Count);
        Assert.Equal(["mcp__newserver__read"], provider.ReceivedToolLists[1].Select(t => t.Name));
    }

    private sealed class FakeTool(string name) : ITool
    {
        public string Name { get; } = name;
        public string Description => "fake";
        public System.Text.Json.JsonElement ParameterSchema { get; } =
            System.Text.Json.JsonSerializer.SerializeToElement(new { type = "object" });
        public Task<ToolResult> InvokeAsync(System.Text.Json.JsonElement arguments, CancellationToken ct) =>
            Task.FromResult(ToolResult.Ok("ok"));
    }

    private static async Task<List<AgentEvent>> DrainAsync(System.Threading.Channels.ChannelReader<AgentEvent> reader)
    {
        var events = new List<AgentEvent>();
        await foreach (var evt in reader.ReadAllAsync())
            events.Add(evt);
        return events;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition was not met in time.");
            await Task.Delay(10);
        }
    }
}
