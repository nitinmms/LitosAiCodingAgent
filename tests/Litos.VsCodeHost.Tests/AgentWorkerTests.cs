using System.Threading.Channels;
using Litos.Agent;
using Litos.Agent.Messages;
using Litos.Agent.Session;
using Litos.Agent.Streaming;
using Litos.Agent.Tools;
using Litos.Host;
using Litos.VsCodeHost.Tests.Fakes;

namespace Litos.VsCodeHost.Tests;

/// <summary>
/// Covers this project's trimmed AgentWorker (see its own remarks: no attachment queueing —
/// queueIfActive doesn't exist here since the local host is text-only in v1). Adapted from
/// Litos.Api.Tests/AgentWorkerTests.cs, dropping every case specific to the queueIfActive path.
///
/// IDisposable purely to redirect LitosConfig.ConfigFilePath (a hardcoded static pointing at the
/// real ~/.litos/config.json, with an `internal set` added only for this) to a scratch file for
/// the duration of each test and restore it afterward. Several tests below exercise
/// AgentWorker.SwitchProviderAsync/SetModel, which call LitosConfig.Save() — without this
/// redirect, those calls silently overwrote the real developer's config.json with this file's own
/// "fake"/"new-default" test fixture values, a real incident this fixes (not a hypothetical).
/// Safe without a lock: xUnit runs test methods within one class sequentially by default (no
/// [Collection]/parallelization override here), and a fresh instance of this class — so a fresh
/// constructor/Dispose pair — backs every single test method.
/// </summary>
public class AgentWorkerTests : IDisposable
{
    private readonly string _originalConfigFilePath = LitosConfig.ConfigFilePath;
    private readonly string _scratchConfigFilePath = Path.Combine(Path.GetTempPath(), $"litos-agentworker-test-config-{Guid.NewGuid():n}.json");

    public AgentWorkerTests() => LitosConfig.ConfigFilePath = _scratchConfigFilePath;

    public void Dispose()
    {
        LitosConfig.ConfigFilePath = _originalConfigFilePath;
        if (File.Exists(_scratchConfigFilePath))
            File.Delete(_scratchConfigFilePath);
    }

    private sealed class NoopSystemPromptProvider : ISystemPromptProvider
    {
        public Task<SystemPromptSections?> BuildAsync(ToolRegistry tools, string? workingDirectory, CancellationToken ct) => Task.FromResult<SystemPromptSections?>(null);
    }

    private sealed class FakeToolSource : IToolSource
    {
        public IReadOnlyList<ITool> CurrentTools { get; set; } = [];
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

        // Give StartOrSteerTurn's own background turn a moment to register before steering it.
        await Task.Delay(50);

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
        var (worker, _) = CreateWorker();

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
    public async Task StartOrSteerTurn_SameSessionIdDifferentOwner_TreatedAsDistinctSessions()
    {
        var (worker, _) = CreateWorker();

        var localEvents = worker.StartOrSteerTurn(SessionOwner.Local, "same-id", [new TextBlock("a")], CancellationToken.None, out var localOutcome);
        var telegramEvents = worker.StartOrSteerTurn(SessionOwner.Telegram, "same-id", [new TextBlock("b")], CancellationToken.None, out var telegramOutcome);

        Assert.Equal(TurnOutcome.Started, localOutcome);
        Assert.Equal(TurnOutcome.Started, telegramOutcome);

        await DrainAsync(localEvents!);
        await DrainAsync(telegramEvents!);
    }

    // Regression coverage for dynamic MCP tool discovery: RunTurnAsync must build a fresh
    // ToolRegistry (via ToolRegistryFactory.Create()) at the start of EVERY turn, not once at
    // construction — otherwise a server connecting after startup would never reach a turn without
    // a process restart.
    [Fact]
    public async Task RunTurnAsync_ToolSourceGainsATool_NextTurnSeesIt()
    {
        var toolSource = new FakeToolSource();
        var toolRegistryFactory = new ToolRegistryFactory([], [toolSource]);
        var (worker, provider) = CreateWorker(toolRegistryFactory);

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

    [Fact]
    public async Task RunTurnAsync_UsesSessionLocalProvidedModel()
    {
        var (worker, provider) = CreateWorker();

        var events = worker.StartOrSteerTurn(SessionOwner.Local, "session-1", [new TextBlock("hi")], CancellationToken.None, out var outcome);
        Assert.Equal(TurnOutcome.Started, outcome);
        var results = await DrainAsync(events!);

        Assert.Contains(results, e => e is MessageCompleted);
        Assert.Single(provider.ReceivedMessageLists);
    }

    // --- /provider, /model surface (AgentSettingsEndpoints.cs) ---

    [Fact]
    public void ProviderName_And_Model_ReflectConstructorDefaults()
    {
        var (worker, _) = CreateWorker();

        Assert.Equal("fake", worker.ProviderName);
        Assert.Equal("fake-model", worker.Model);
    }

    [Fact]
    public void AvailableProviders_ReflectsConfigsAvailableChatProviders()
    {
        // LitosConfig.AvailableChatProviders filters against a fixed provider-name allowlist
        // (ChatProviderNames: anthropic/openai/gemini/openrouter/local) — "fake" (this test
        // fixture's provider key) was never a candidate, so CreateWorker's own "fake" config
        // legitimately reports zero available providers. Uses a real chat-provider name here
        // specifically to exercise AvailableProviders' actual filtering logic.
        var config = new LitosConfig(
            DefaultProvider: "anthropic", DefaultModel: "fake-model", LastWorkingDirectory: null,
            ApiKeys: new Dictionary<string, string> { ["anthropic"] = "unused" });
        var provider = new FakeChatProvider();
        var factory = new FakeChatProviderFactory(provider);
        var loopFactory = new AgentLoopFactory(
            new FakeTranscriptStore(), new ContextAccountant(), new NoopSystemPromptProvider(), new Compactor(new CompactionSettings()));
        var worker = new AgentWorker(factory, loopFactory, new ToolRegistryFactory([], []), new FakeTranscriptStore(), config);

        Assert.Equal(["anthropic"], worker.AvailableProviders);
    }

    [Fact]
    public async Task ListModelsAsync_ReturnsTheResolvedProvidersModels()
    {
        var (worker, provider) = CreateWorker();
        provider.ModelsToReturn = [new Litos.Agent.Providers.ModelInfo("model-a", "Model A", IsDefault: true), new Litos.Agent.Providers.ModelInfo("model-b", "Model B", IsDefault: false)];

        var models = await worker.ListModelsAsync("fake", CancellationToken.None);

        Assert.Equal(["model-a", "model-b"], models.Select(m => m.Id));
    }

    [Fact]
    public async Task SwitchProviderAsync_UpdatesProviderNameAndResetsToTheNewProvidersDefaultModel()
    {
        var (worker, provider) = CreateWorker();
        provider.ModelsToReturn = [new Litos.Agent.Providers.ModelInfo("old-default", "Old", IsDefault: false), new Litos.Agent.Providers.ModelInfo("new-default", "New", IsDefault: true)];

        await worker.SwitchProviderAsync("fake", CancellationToken.None);

        // FakeChatProviderFactory resolves the same FakeChatProvider regardless of name, so this
        // only exercises "switching" in the sense of re-resolving models and re-picking a
        // default — genuinely switching providers isn't directly testable without a second fake
        // provider registered under a different key, which none of these tests need.
        Assert.Equal("fake", worker.ProviderName);
        Assert.Equal("new-default", worker.Model);
    }

    [Fact]
    public async Task SwitchProviderAsync_NoModelsReturned_Throws()
    {
        var (worker, provider) = CreateWorker();
        provider.ModelsToReturn = [];

        await Assert.ThrowsAsync<InvalidOperationException>(() => worker.SwitchProviderAsync("fake", CancellationToken.None));
    }

    [Fact]
    public void SetModel_UpdatesModelWithoutChangingProvider()
    {
        var (worker, _) = CreateWorker();

        worker.SetModel("a-different-model", contextLength: null);

        Assert.Equal("fake", worker.ProviderName);
        Assert.Equal("a-different-model", worker.Model);
    }

    // --- ContextLength caching (backs GET /sessions/{id}/context/usage's denominator) ---

    [Fact]
    public void SetModel_UpdatesContextLength()
    {
        var (worker, _) = CreateWorker();

        worker.SetModel("a-different-model", contextLength: 128_000);

        Assert.Equal(128_000, worker.ContextLength);
    }

    [Fact]
    public async Task SwitchProviderAsync_UpdatesContextLengthFromTheNewDefaultModel()
    {
        var (worker, provider) = CreateWorker();
        provider.ModelsToReturn =
        [
            new Litos.Agent.Providers.ModelInfo("old-default", "Old", IsDefault: false),
            new Litos.Agent.Providers.ModelInfo("new-default", "New", IsDefault: true, ContextLength: 200_000),
        ];

        await worker.SwitchProviderAsync("fake", CancellationToken.None);

        Assert.Equal(200_000, worker.ContextLength);
    }

    [Fact]
    public async Task RunTurnAsync_FirstTurn_ResolvesContextLengthFromTheConfiguredDefaultModel()
    {
        var (worker, provider) = CreateWorker();
        provider.ModelsToReturn = [new Litos.Agent.Providers.ModelInfo("fake-model", "Fake Model", IsDefault: true, ContextLength: 64_000)];

        Assert.Null(worker.ContextLength); // Not yet resolved — constructor doesn't call ListModelsAsync.

        var events = worker.StartOrSteerTurn(SessionOwner.Local, "session-1", [new TextBlock("hi")], CancellationToken.None, out _);
        await DrainAsync(events!);

        Assert.Equal(64_000, worker.ContextLength);
    }

    [Fact]
    public async Task SwitchProviderAsync_MidTurn_DoesNotAffectTheAlreadyRunningTurn()
    {
        var (worker, provider) = CreateWorker();
        var gate = new TaskCompletionSource();
        provider.EnqueueAwaiting(gate.Task, new TextDelta("hi"), new MessageCompleted(ChatMessage.Assistant([new TextBlock("hi")]), new UsageInfo(1, 1)));
        provider.ModelsToReturn = [new Litos.Agent.Providers.ModelInfo("new-default", "New", IsDefault: true)];

        var events = worker.StartOrSteerTurn(SessionOwner.Local, "session-1", [new TextBlock("hi")], CancellationToken.None, out var outcome);
        Assert.Equal(TurnOutcome.Started, outcome);
        await WaitUntilAsync(() => worker.Model == "fake-model"); // Turn snapshot already taken.

        // Switching mid-turn changes what the *next* turn sees, not this already-running one —
        // matches Litos.Api's AgentWorker.RunTurnCoreAsync's own snapshot-at-start comment.
        await worker.SwitchProviderAsync("fake", CancellationToken.None);
        Assert.Equal("new-default", worker.Model);

        gate.SetResult();
        var results = await DrainAsync(events!);
        Assert.Contains(results, e => e is MessageCompleted);
    }

    // --- CancelTurn (backs POST /sessions/{id}/cancel, the composer's Stop/Cancel button) ---
    //
    // A stuck tool call or provider stream is simulated the same way SwitchProviderAsync_MidTurn's
    // test above does — EnqueueAwaiting gates the fake provider's stream on a TaskCompletionSource
    // that's never released, standing in for e.g. an MCP CallToolAsync that never returns. Before
    // CancelTurn existed there was no way to unblock this short of the SSE connection itself
    // dying; these tests prove CancelTurn now does it explicitly.

    [Fact]
    public async Task CancelTurn_NoActiveTurnForSession_ReturnsFalse()
    {
        var (worker, _) = CreateWorker();

        var cancelled = worker.CancelTurn(SessionOwner.Local, "no-such-session");

        Assert.False(cancelled);
    }

    [Fact]
    public async Task CancelTurn_ActiveTurn_ReturnsTrue_AndTheTurnsEventStreamEndsWithoutFurtherRequests()
    {
        var (worker, provider) = CreateWorker();
        var gate = new TaskCompletionSource(); // never released — simulates a hung tool call/provider stream
        provider.EnqueueAwaiting(gate.Task, new TextDelta("hi"), new MessageCompleted(ChatMessage.Assistant([new TextBlock("hi")]), new UsageInfo(1, 1)));

        var events = worker.StartOrSteerTurn(SessionOwner.Local, "session-1", [new TextBlock("hi")], CancellationToken.None, out var outcome);
        Assert.Equal(TurnOutcome.Started, outcome);
        await WaitUntilAsync(() => provider.ReceivedMessageLists.Count > 0); // the stalled StreamAsync call has started

        var cancelled = worker.CancelTurn(SessionOwner.Local, "session-1");
        Assert.True(cancelled);

        // DrainAsync completing at all (rather than hanging on this test's own await) is the
        // actual assertion here — before CancelTurn existed, nothing could make this happen short
        // of the never-released gate completing, which this test deliberately never does.
        var results = await DrainAsync(events!);
        Assert.DoesNotContain(results, e => e is MessageCompleted);
        // Only the one StreamAsync call this test already waited for above — cancelling means the
        // loop never re-requests the model, matching the real bug's "zero further LLM requests".
        Assert.Single(provider.ReceivedMessageLists);
    }

    [Fact]
    public async Task CancelTurn_ActiveTurn_RemovesItFromActiveTurns_SoASubsequentSendStartsAFreshTurnInsteadOfSteeringTheDeadOne()
    {
        var (worker, provider) = CreateWorker();
        var gate = new TaskCompletionSource();
        provider.EnqueueAwaiting(gate.Task, new TextDelta("hi"), new MessageCompleted(ChatMessage.Assistant([new TextBlock("hi")]), new UsageInfo(1, 1)));

        var events = worker.StartOrSteerTurn(SessionOwner.Local, "session-1", [new TextBlock("hi")], CancellationToken.None, out _);
        await WaitUntilAsync(() => provider.ReceivedMessageLists.Count > 0);
        worker.CancelTurn(SessionOwner.Local, "session-1");
        await DrainAsync(events!);

        // Regression guard for the "second panel doesn't help either" symptom: once a stuck turn
        // is cancelled and torn down, the NEXT send for that session must start a brand-new turn
        // (Started), not silently steer into the now-dead one (which nothing reads from anymore).
        var secondEvents = worker.StartOrSteerTurn(SessionOwner.Local, "session-1", [new TextBlock("again")], CancellationToken.None, out var secondOutcome);
        Assert.Equal(TurnOutcome.Started, secondOutcome);
        var secondResults = await DrainAsync(secondEvents!);
        Assert.Contains(secondResults, e => e is MessageCompleted);
    }

    [Fact]
    public async Task CancelTurn_OneOfTwoConcurrentSessions_OnlyCancelsThatSession()
    {
        var (worker, provider) = CreateWorker();
        var gate = new TaskCompletionSource();
        provider.EnqueueAwaiting(gate.Task, new TextDelta("hi"), new MessageCompleted(ChatMessage.Assistant([new TextBlock("hi")]), new UsageInfo(1, 1)));

        var stuckEvents = worker.StartOrSteerTurn(SessionOwner.Local, "stuck-session", [new TextBlock("hi")], CancellationToken.None, out _);
        await WaitUntilAsync(() => provider.ReceivedMessageLists.Count > 0);

        var fineEvents = worker.StartOrSteerTurn(SessionOwner.Telegram, "fine-session", [new TextBlock("hi")], CancellationToken.None, out var fineOutcome);
        Assert.Equal(TurnOutcome.Started, fineOutcome);
        var fineResults = await DrainAsync(fineEvents!);
        Assert.Contains(fineResults, e => e is MessageCompleted);

        var cancelled = worker.CancelTurn(SessionOwner.Local, "stuck-session");
        Assert.True(cancelled);
        await DrainAsync(stuckEvents!);
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

    private static async Task<List<AgentEvent>> DrainAsync(ChannelReader<AgentEvent> reader)
    {
        var events = new List<AgentEvent>();
        await foreach (var evt in reader.ReadAllAsync())
            events.Add(evt);
        return events;
    }
}
