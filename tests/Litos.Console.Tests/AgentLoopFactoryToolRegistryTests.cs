using Litos.Agent;
using Litos.Agent.Messages;
using Litos.Agent.Providers;
using Litos.Agent.Session;
using Litos.Agent.Streaming;
using Litos.Agent.Tools;
using Litos.Host;

namespace Litos.Console.Tests;

/// <summary>
/// Regression coverage for Litos.Console's own use of the AgentLoopFactory.Create(provider,
/// tools) signature (ToolRegistry moved from a constructor-captured DI singleton to a per-call
/// parameter — see ReadMe_LitosApi_Mcp.md's dynamic-MCP-tool-discovery redesign). Litos.Console
/// rebuilds a fresh ToolRegistry via ToolRegistryFactory.Create() immediately before every turn
/// (Program.cs, Console Parity Plan Slice 0.3) so a server added/removed via /mcp is picked up on
/// the next send without an app restart — this test proves the tool list actually reaches a real
/// request end-to-end.
/// </summary>
public class AgentLoopFactoryToolRegistryTests
{
    private sealed class NoopSystemPromptProvider : ISystemPromptProvider
    {
        public Task<SystemPromptSections?> BuildAsync(ToolRegistry tools, string? workingDirectory, CancellationToken ct) => Task.FromResult<SystemPromptSections?>(null);
    }

    private sealed class FakeTranscriptStore : ITranscriptStore
    {
        public Task AppendAsync(SessionOwner owner, string sessionId, TranscriptEntry entry, CancellationToken ct) => Task.CompletedTask;

        public async IAsyncEnumerable<TranscriptEntry> ReadAsync(
            SessionOwner owner, string sessionId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            yield break;
#pragma warning disable CS0162
            await Task.CompletedTask;
#pragma warning restore CS0162
        }

        public Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(SessionOwner owner, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SessionSummary>>([]);

        public Task<string> BranchAsync(SessionOwner owner, string sourceSessionId, int uptoEntryIndex, CancellationToken ct) =>
            Task.FromResult(Guid.NewGuid().ToString("n"));

        public string GetScratchDirectory(SessionOwner owner, string sessionId) =>
            $"/fake-scratch/{owner.Value}/{sessionId}";
    }

    private sealed class FakeChatProvider : IChatProvider
    {
        public string ProviderName => "fake";
        public List<IReadOnlyList<ToolSchema>> ReceivedToolLists { get; } = [];

        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ModelInfo>>([new ModelInfo("fake-model", "Fake Model", IsDefault: true)]);

        public async IAsyncEnumerable<AgentEvent> StreamAsync(
            ChatRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            ReceivedToolLists.Add(request.Tools);
            yield return new TextDelta("ok");
            yield return new MessageCompleted(ChatMessage.Assistant([new TextBlock("ok")]), new UsageInfo(1, 1));
            await Task.CompletedTask;
        }
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

    [Fact]
    public async Task Create_ToolRegistryPassedPerCall_ReachesTheActualChatRequest()
    {
        var loopFactory = new AgentLoopFactory(
            new FakeTranscriptStore(), new ContextAccountant(), new NoopSystemPromptProvider(), new Compactor(new CompactionSettings()));
        var toolRegistry = new ToolRegistryFactory([new FakeTool("read_file"), new FakeTool("shell")], []).Create();
        var provider = new FakeChatProvider();

        var loop = loopFactory.Create(provider, toolRegistry);
        var transcript = Transcript.CreateNew(Directory.GetCurrentDirectory());

        await foreach (var _ in loop.RunTurnAsync(SessionOwner.Local, "session-1", transcript, "fake-model", "hi", CancellationToken.None))
        {
        }

        var sentTools = Assert.Single(provider.ReceivedToolLists);
        Assert.Equal(["read_file", "shell"], sentTools.Select(t => t.Name));
    }

    [Fact]
    public void Create_RebuiltPerTurn_ObservesToolsAddedToASourceBetweenCalls()
    {
        // Mirrors Program.cs's per-turn rebuild (Slice 0.3): toolRegistryFactory.Create() is
        // called fresh immediately before every turn, from the same ToolRegistryFactory instance,
        // so a mutable IToolSource's current contents (e.g. MCP tools connecting after startup)
        // are picked up on the very next turn with no restart.
        var mutableSource = new MutableToolSource();
        var toolRegistryFactory = new ToolRegistryFactory([new FakeTool("read_file")], [mutableSource]);
        var loopFactory = new AgentLoopFactory(
            new FakeTranscriptStore(), new ContextAccountant(), new NoopSystemPromptProvider(), new Compactor(new CompactionSettings()));

        var registryBeforeConnect = toolRegistryFactory.Create();
        Assert.Throws<ToolInvocationException>(() => registryBeforeConnect.Resolve("mcp__server__tool"));

        mutableSource.Tools = [new FakeTool("mcp__server__tool")];
        var registryAfterConnect = toolRegistryFactory.Create();

        Assert.NotSame(registryBeforeConnect, registryAfterConnect);
        Assert.NotNull(registryAfterConnect.Resolve("mcp__server__tool"));
        Assert.NotNull(loopFactory.Create(new FakeChatProvider(), registryAfterConnect));
    }

    private sealed class MutableToolSource : IToolSource
    {
        public IReadOnlyList<ITool> Tools { get; set; } = [];
        public IReadOnlyList<ITool> CurrentTools => Tools;
    }
}
