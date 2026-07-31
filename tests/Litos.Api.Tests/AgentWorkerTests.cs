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
        public Task<string?> BuildAsync(ToolRegistry tools, string? workingDirectory, CancellationToken ct) => Task.FromResult<string?>(null);
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
