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
