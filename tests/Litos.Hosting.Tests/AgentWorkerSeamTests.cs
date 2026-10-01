using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Litos.Agent;
using Litos.Agent.Messages;
using Litos.Agent.Session;
using Litos.Agent.Streaming;
using Litos.Agent.Tools;
using Litos.Host;
using Litos.Hosting.Tests.Fakes;

namespace Litos.Hosting.Tests;

/// <summary>
/// AgentWorker driven through its seam constructor — the one the software factory's worker uses.
/// The defaults-wiring constructor is covered by Litos.VsCodeHost.Tests' AgentWorkerTests.
/// </summary>
public class AgentWorkerSeamTests
{
    private sealed class NoopSystemPromptProvider : ISystemPromptProvider
    {
        public Task<SystemPromptSections?> BuildAsync(ToolRegistry tools, string? workingDirectory, CancellationToken ct)
        {
            WorkingDirectories.Add(workingDirectory);
            return Task.FromResult<SystemPromptSections?>(null);
        }

        public ConcurrentBag<string?> WorkingDirectories { get; } = [];
    }

    private sealed class FakeTool(string name) : ITool
    {
        public string Name { get; } = name;
        public string Description => "fake";
        public JsonElement ParameterSchema { get; } = JsonSerializer.SerializeToElement(new { type = "object" });
        public Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken ct) => Task.FromResult(ToolResult.Ok("ok"));
    }

    /// <summary>Records what it was asked for and answers with a tool named after the turn kind.</summary>
    private sealed class RecordingToolSetPolicy : IToolSetPolicy
    {
        public ConcurrentQueue<(string SessionId, string? TurnKind)> Calls { get; } = [];

        public ToolRegistry Create(string sessionId, string? turnKind)
        {
            Calls.Enqueue((sessionId, turnKind));
            return new ToolRegistry([new FakeTool($"tool_for_{turnKind ?? "none"}")]);
        }
    }

    private sealed class FixedWorkingDirectoryResolver(string directory) : IWorkingDirectoryResolver
    {
        public List<string?> Seen { get; } = [];

        public string Resolve(string? transcriptWorkingDirectory)
        {
            Seen.Add(transcriptWorkingDirectory);
            return directory;
        }
    }

    private sealed record Harness(
        AgentWorker Worker, FakeChatProvider Provider, RecordingToolSetPolicy Policy, FakeTranscriptStore Store,
        NoopSystemPromptProvider SystemPrompt);

    private static Harness Create(IModelSelection? selection = null, IWorkingDirectoryResolver? resolver = null)
    {
        var provider = new FakeChatProvider();
        var store = new FakeTranscriptStore();
        var systemPrompt = new NoopSystemPromptProvider();
        var loopFactory = new AgentLoopFactory(store, new ContextAccountant(), systemPrompt, new Compactor(new CompactionSettings()));
        var policy = new RecordingToolSetPolicy();
        var worker = new AgentWorker(
            new FakeChatProviderFactory(provider), loopFactory, store,
            selection ?? new FixedModelSelection("fake", "fixed-model", 100_000),
            policy, resolver ?? new TranscriptWorkingDirectoryResolver());
        return new Harness(worker, provider, policy, store, systemPrompt);
    }

    private static async Task<List<AgentEvent>> DrainAsync(ChannelReader<AgentEvent> reader)
    {
        var events = new List<AgentEvent>();
        await foreach (var evt in reader.ReadAllAsync())
            events.Add(evt);
        return events;
    }

    [Fact]
    public async Task StartOrSteerTurn_PassesSessionAndTurnKindToTheToolSetPolicy()
    {
        var h = Create();

        var events = h.Worker.StartOrSteerTurn(
            SessionOwner.Local, "session-1", [new TextBlock("go")], CancellationToken.None, out _, turnKind: "Review");
        await DrainAsync(events!);

        Assert.Equal(("session-1", "Review"), Assert.Single(h.Policy.Calls));
    }

    [Fact]
    public async Task StartOrSteerTurn_NoTurnKind_PassesNullToTheToolSetPolicy()
    {
        var h = Create();

        var events = h.Worker.StartOrSteerTurn(SessionOwner.Local, "session-1", [new TextBlock("go")], CancellationToken.None, out _);
        await DrainAsync(events!);

        Assert.Null(Assert.Single(h.Policy.Calls).TurnKind);
    }

    [Fact]
    public async Task RunTurn_ModelSeesExactlyThePolicysTools()
    {
        var h = Create();

        var events = h.Worker.StartOrSteerTurn(
            SessionOwner.Local, "session-1", [new TextBlock("go")], CancellationToken.None, out _, turnKind: "Implement");
        await DrainAsync(events!);

        Assert.Equal(["tool_for_Implement"], Assert.Single(h.Provider.ReceivedToolLists).Select(t => t.Name));
    }

    [Fact]
    public async Task RunTurn_EachTurnAsksThePolicyAgain_SoDifferentKindsGetDifferentTools()
    {
        var h = Create();

        await DrainAsync(h.Worker.StartOrSteerTurn(
            SessionOwner.Local, "session-1", [new TextBlock("a")], CancellationToken.None, out _, turnKind: "Implement")!);
        // A second session: the first session's turn is removed from the active set just after
        // its event stream completes, so re-using its id here could steer instead of start.
        await DrainAsync(h.Worker.StartOrSteerTurn(
            SessionOwner.Local, "session-2", [new TextBlock("b")], CancellationToken.None, out _, turnKind: "Review")!);

        Assert.Equal(
            ["tool_for_Implement", "tool_for_Review"],
            h.Provider.ReceivedToolLists.Select(tools => tools.Single().Name));
    }

    [Fact]
    public async Task RunTurn_PtcOffWithNoKernelManager_UsesThePolicysToolsDirectly()
    {
        // No KernelSessionManager was supplied, so even a session whose transcript says PTC is on
        // must take the plain path rather than fail.
        var h = Create();
        await h.Worker.SetKernelModeEnabledAsync(SessionOwner.Local, "session-1", enabled: true, CancellationToken.None);

        var events = h.Worker.StartOrSteerTurn(
            SessionOwner.Local, "session-1", [new TextBlock("go")], CancellationToken.None, out _, turnKind: "Implement");
        var results = await DrainAsync(events!);

        Assert.False(h.Worker.IsKernelModeAvailable);
        Assert.Contains(results, e => e is MessageCompleted);
        Assert.Equal(["tool_for_Implement"], h.Provider.ReceivedToolLists.Single().Select(t => t.Name));
    }

    [Fact]
    public void ModelProperties_ComeFromTheModelSelection()
    {
        var h = Create(new FixedModelSelection("openrouter", "vendor/model", 200_000));

        Assert.Equal("openrouter", h.Worker.ProviderName);
        Assert.Equal("vendor/model", h.Worker.Model);
        Assert.Equal(200_000, h.Worker.ContextLength);
        Assert.Equal(["openrouter"], h.Worker.AvailableProviders);
    }

    [Fact]
    public async Task RunTurn_FixedSelection_NeverListsModels()
    {
        // A worker with a fixed selection has nothing to resolve; listing models would be a
        // wasted provider call on every run.
        var h = Create(new FixedModelSelection("fake", "fixed-model", 100_000));
        h.Provider.ModelsToReturn = [];

        var results = await DrainAsync(
            h.Worker.StartOrSteerTurn(SessionOwner.Local, "session-1", [new TextBlock("go")], CancellationToken.None, out _)!);

        Assert.Contains(results, e => e is MessageCompleted);
    }

    [Fact]
    public async Task SwitchProviderAsync_FixedSelection_IsRefused()
    {
        var h = Create();

        await Assert.ThrowsAsync<NotSupportedException>(() => h.Worker.SwitchProviderAsync("other", CancellationToken.None));
    }

    [Fact]
    public async Task RunTurn_WorkingDirectoryComesFromTheResolver()
    {
        var directory = Path.Combine(Path.GetTempPath(), "litos-hosting-working-copy");
        var resolver = new FixedWorkingDirectoryResolver(directory);
        var h = Create(resolver: resolver);

        await DrainAsync(h.Worker.StartOrSteerTurn(SessionOwner.Local, "session-1", [new TextBlock("go")], CancellationToken.None, out _)!);

        Assert.Null(Assert.Single(resolver.Seen)); // a new session has recorded no directory yet
        Assert.Contains(directory, h.SystemPrompt.WorkingDirectories);
    }

    [Fact]
    public async Task RunTurn_ResolverOverridesTheDirectoryAnEarlierTurnRecorded()
    {
        var first = Path.Combine(Path.GetTempPath(), "litos-hosting-first-run");
        var second = Path.Combine(Path.GetTempPath(), "litos-hosting-second-run");
        var provider = new FakeChatProvider();
        var store = new FakeTranscriptStore();
        var systemPrompt = new NoopSystemPromptProvider();
        var loopFactory = new AgentLoopFactory(store, new ContextAccountant(), systemPrompt, new Compactor(new CompactionSettings()));

        AgentWorker WorkerIn(string directory) => new(
            new FakeChatProviderFactory(provider), loopFactory, store, new FixedModelSelection("fake", "m", 100_000),
            new RecordingToolSetPolicy(), new FixedWorkingDirectoryResolver(directory));

        await DrainAsync(WorkerIn(first).StartOrSteerTurn(SessionOwner.Local, "s", [new TextBlock("a")], CancellationToken.None, out _)!);
        var secondResolver = new FixedWorkingDirectoryResolver(second);
        var secondWorker = new AgentWorker(
            new FakeChatProviderFactory(provider), loopFactory, store, new FixedModelSelection("fake", "m", 100_000),
            new RecordingToolSetPolicy(), secondResolver);
        await DrainAsync(secondWorker.StartOrSteerTurn(SessionOwner.Local, "s", [new TextBlock("b")], CancellationToken.None, out _)!);

        Assert.Equal(first, Assert.Single(secondResolver.Seen)); // it was shown what the first run recorded
        Assert.Contains(second, systemPrompt.WorkingDirectories);
    }
}

/// <summary>
/// Failures that happen outside AgentLoop's own stream handling. The turn's task is awaited by
/// nobody, so before these were caught the event stream just ended — indistinguishable from a
/// turn that finished with nothing to say.
/// </summary>
public class AgentWorkerTurnFailureTests
{
    private sealed class NoopSystemPromptProvider : ISystemPromptProvider
    {
        public Task<SystemPromptSections?> BuildAsync(ToolRegistry tools, string? workingDirectory, CancellationToken ct) =>
            Task.FromResult<SystemPromptSections?>(null);
    }

    private sealed class ThrowingToolSetPolicy : IToolSetPolicy
    {
        public ToolRegistry Create(string sessionId, string? turnKind) =>
            throw new InvalidOperationException("Unknown turn kind 'Mystery'.");
    }

    private sealed class EmptyToolSetPolicy : IToolSetPolicy
    {
        public ToolRegistry Create(string sessionId, string? turnKind) => new([]);
    }

    private sealed class UnresolvableModelSelection : IModelSelection
    {
        public string ProviderName => "fake";
        public string? Model => null;
        public int? ContextLength => null;
        public IReadOnlyList<string> AvailableProviders => ["fake"];
        public ModelSelectionSnapshot Snapshot() => new("fake", "", null);
        public Task SwitchProviderAsync(string providerName, CancellationToken ct) => Task.CompletedTask;
        public void SetModel(string modelId, int? contextLength) { }
        public Task EnsureResolvedAsync(CancellationToken ct) => throw new InvalidOperationException("The provider returned no models.");
    }

    /// <summary>Answers ordinary turns, but fails the summarization call compaction makes — the
    /// one request that carries no tools and ends with the summarization prompt.</summary>
    private sealed class CompactionFailingProvider : Litos.Agent.Providers.IChatProvider
    {
        public string ProviderName => "fake";

        public int OrdinaryCalls { get; private set; }

        public Task<IReadOnlyList<Litos.Agent.Providers.ModelInfo>> ListModelsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Litos.Agent.Providers.ModelInfo>>([]);

        public async IAsyncEnumerable<AgentEvent> StreamAsync(
            Litos.Agent.Providers.ChatRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            var lastText = request.Messages[^1].Content.OfType<TextBlock>().LastOrDefault()?.Text ?? "";
            if (lastText.Contains("context checkpoint summary"))
                throw new InvalidOperationException("The gateway refused the call: budget_exhausted.");

            OrdinaryCalls++;
            yield return new MessageCompleted(ChatMessage.Assistant([new TextBlock("ok")]), new UsageInfo(1, 1));
        }
    }

    private sealed class SingleProviderFactory(Litos.Agent.Providers.IChatProvider provider) : Litos.Agent.Providers.IChatProviderFactory
    {
        public Litos.Agent.Providers.IChatProvider Resolve(string providerName) => provider;
    }

    private static AgentWorker Worker(
        FakeTranscriptStore store, Litos.Agent.Providers.IChatProvider provider, IModelSelection? selection = null, IToolSetPolicy? policy = null) => new(
        new SingleProviderFactory(provider),
        new AgentLoopFactory(store, new ContextAccountant(), new NoopSystemPromptProvider(), new Compactor(new CompactionSettings())),
        store, selection ?? new FixedModelSelection("fake", "model", 200_000), policy ?? new EmptyToolSetPolicy(),
        new TranscriptWorkingDirectoryResolver());

    private static async Task<List<AgentEvent>> DrainAsync(ChannelReader<AgentEvent> reader)
    {
        var events = new List<AgentEvent>();
        await foreach (var evt in reader.ReadAllAsync())
            events.Add(evt);
        return events;
    }

    [Fact]
    public async Task CompactionFails_TheTurnReportsTheError_InsteadOfEndingSilently()
    {
        // A session already past its compaction threshold: the turn's first act is to compact.
        var store = new FakeTranscriptStore();
        await store.AppendAsync(SessionOwner.Local, "s", TranscriptEntry.FromMessage(ChatMessage.User(new string('a', 400_000))), default);
        await store.AppendAsync(
            SessionOwner.Local, "s",
            TranscriptEntry.FromMessage(ChatMessage.Assistant([new TextBlock(new string('b', 400_000))]), new UsageInfo(190_000, 1_000)), default);
        var provider = new CompactionFailingProvider();
        var worker = Worker(store, provider);

        var events = await DrainAsync(worker.StartOrSteerTurn(SessionOwner.Local, "s", [new TextBlock("continue")], default, out _)!);

        var error = Assert.IsType<ErrorOccurred>(Assert.Single(events));
        Assert.Contains("budget_exhausted", error.Exception.Message);
        Assert.Equal(0, provider.OrdinaryCalls); // the turn did not carry on with an uncompacted context
    }

    [Fact]
    public async Task ToolSetPolicyThrows_TheTurnReportsTheError()
    {
        var worker = Worker(new FakeTranscriptStore(), new CompactionFailingProvider(), policy: new ThrowingToolSetPolicy());

        var events = await DrainAsync(worker.StartOrSteerTurn(SessionOwner.Local, "s", [new TextBlock("go")], default, out _, turnKind: "Mystery")!);

        Assert.Contains("Unknown turn kind 'Mystery'.", Assert.IsType<ErrorOccurred>(Assert.Single(events)).Exception.Message);
    }

    [Fact]
    public async Task ModelCannotBeResolved_TheTurnReportsTheError()
    {
        var worker = Worker(new FakeTranscriptStore(), new CompactionFailingProvider(), selection: new UnresolvableModelSelection());

        var events = await DrainAsync(worker.StartOrSteerTurn(SessionOwner.Local, "s", [new TextBlock("go")], default, out _)!);

        Assert.Contains("returned no models", Assert.IsType<ErrorOccurred>(Assert.Single(events)).Exception.Message);
    }

    [Fact]
    public async Task AfterAFailedTurn_TheSessionCanStartANewTurn()
    {
        var store = new FakeTranscriptStore();
        var failing = Worker(store, new CompactionFailingProvider(), policy: new ThrowingToolSetPolicy());
        await DrainAsync(failing.StartOrSteerTurn(SessionOwner.Local, "s", [new TextBlock("go")], default, out _)!);

        // Give the failed turn's cleanup a moment: it leaves the active set just after its
        // event stream completes.
        ChannelReader<AgentEvent>? retry = null;
        for (var attempt = 0; attempt < 100 && retry is null; attempt++)
        {
            retry = failing.StartOrSteerTurn(SessionOwner.Local, "s", [new TextBlock("again")], default, out _);
            if (retry is null)
                await Task.Delay(10);
        }

        Assert.NotNull(retry);
        Assert.IsType<ErrorOccurred>(Assert.Single(await DrainAsync(retry)));
    }

    /// <summary>Cancellation is not a failure: it must still end the stream with no error event.</summary>
    [Fact]
    public async Task CancelledTurn_ReportsNoError()
    {
        var provider = new FakeChatProvider();
        provider.EnqueueAwaiting(new TaskCompletionSource().Task, new TextDelta("never"));
        var store = new FakeTranscriptStore();
        var worker = new AgentWorker(
            new FakeChatProviderFactory(provider),
            new AgentLoopFactory(store, new ContextAccountant(), new NoopSystemPromptProvider(), new Compactor(new CompactionSettings())),
            store, new FixedModelSelection("fake", "model", 200_000), new EmptyToolSetPolicy(), new TranscriptWorkingDirectoryResolver());
        var events = worker.StartOrSteerTurn(SessionOwner.Local, "s", [new TextBlock("go")], default, out _)!;
        while (provider.ReceivedMessageLists.Count == 0)
            await Task.Delay(10);

        worker.CancelTurn(SessionOwner.Local, "s");

        Assert.DoesNotContain(await DrainAsync(events), e => e is ErrorOccurred);
    }
}

public class DefaultSeamTests
{
    private sealed class FakeTool(string name) : ITool
    {
        public string Name { get; } = name;
        public string Description => "fake";
        public JsonElement ParameterSchema { get; } = JsonSerializer.SerializeToElement(new { type = "object" });
        public Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken ct) => Task.FromResult(ToolResult.Ok("ok"));
    }

    private sealed class FakeToolSource : IToolSource
    {
        public IReadOnlyList<ITool> CurrentTools { get; set; } = [];
    }

    [Fact]
    public void DefaultToolSetPolicy_ReturnsStaticAndSourcedTools_WhateverTheTurnKind()
    {
        var source = new FakeToolSource { CurrentTools = [new FakeTool("mcp__server__read")] };
        var policy = new DefaultToolSetPolicy(new ToolRegistryFactory([new FakeTool("read_file")], [source]));

        var withoutKind = policy.Create("session-1", null);
        var withKind = policy.Create("session-2", "Review");

        Assert.Equal(["read_file", "mcp__server__read"], withoutKind.Schemas.Select(s => s.Name));
        Assert.Equal(["read_file", "mcp__server__read"], withKind.Schemas.Select(s => s.Name));
    }

    [Fact]
    public void DefaultToolSetPolicy_RebuildsPerCall_SoLateToolsAppear()
    {
        var source = new FakeToolSource();
        var policy = new DefaultToolSetPolicy(new ToolRegistryFactory([], [source]));
        Assert.Empty(policy.Create("s", null).Schemas);

        source.CurrentTools = [new FakeTool("mcp__late__tool")];

        Assert.Equal(["mcp__late__tool"], policy.Create("s", null).Schemas.Select(s => s.Name));
    }

    [Fact]
    public void TranscriptWorkingDirectoryResolver_KeepsTheTranscriptsDirectory()
    {
        var resolver = new TranscriptWorkingDirectoryResolver();

        Assert.Equal(@"C:\some\project", resolver.Resolve(@"C:\some\project"));
    }

    [Fact]
    public void TranscriptWorkingDirectoryResolver_NoneRecorded_UsesTheProcessDirectory()
    {
        var resolver = new TranscriptWorkingDirectoryResolver();

        Assert.Equal(Directory.GetCurrentDirectory(), resolver.Resolve(null));
    }

    [Fact]
    public void ProcessWorkingDirectoryResolver_AlwaysUsesTheProcessDirectory()
    {
        var resolver = new ProcessWorkingDirectoryResolver();

        Assert.Equal(Directory.GetCurrentDirectory(), resolver.Resolve(null));
        Assert.Equal(Directory.GetCurrentDirectory(), resolver.Resolve(@"C:\an\earlier\run"));
    }
}
