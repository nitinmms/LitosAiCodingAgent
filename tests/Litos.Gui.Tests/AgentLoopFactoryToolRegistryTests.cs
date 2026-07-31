using Litos.Agent;
using Litos.Agent.Messages;
using Litos.Agent.Session;
using Litos.Agent.Tools;
using Litos.Gui.Tests.Fakes;
using Litos.Host;

namespace Litos.Gui.Tests;

/// <summary>
/// Regression coverage for Litos.Gui's own use of the AgentLoopFactory.Create(provider, tools)
/// signature (ToolRegistry moved from a constructor-captured DI singleton to a per-call
/// parameter — see ReadMe_LitosApi_Mcp.md's dynamic-MCP-tool-discovery redesign). Litos.Gui
/// builds its ToolRegistry once at startup via ToolRegistryFactory.Create() (Program.cs) and
/// reuses that same instance across every AgentLoop rebuilt by a /provider switch
/// (MainWindow.axaml.cs, MainWindowSession.ToolRegistry) — dynamic MCP discovery itself is out
/// of scope for this face, but the plumbing must still compile and actually thread the tool list
/// through to a real request, which this test proves end-to-end.
/// </summary>
public class AgentLoopFactoryToolRegistryTests
{
    private sealed class NoopSystemPromptProvider : ISystemPromptProvider
    {
        public Task<string?> BuildAsync(ToolRegistry tools, string? workingDirectory, CancellationToken ct) => Task.FromResult<string?>(null);
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
    public void Create_CalledTwiceWithDifferentToolRegistries_EachAgentLoopKeepsItsOwn()
    {
        // Mirrors MainWindow.axaml.cs's provider-switch call site: a new AgentLoop is built via
        // Create(newChatProvider, session.ToolRegistry) each time — this just confirms two
        // separately-built ToolRegistry instances stay independent when passed to two separate
        // Create() calls (no shared/leaked state between them).
        var loopFactory = new AgentLoopFactory(
            new FakeTranscriptStore(), new ContextAccountant(), new NoopSystemPromptProvider(), new Compactor(new CompactionSettings()));
        var registryA = new ToolRegistry([new FakeTool("only_in_a")]);
        var registryB = new ToolRegistry([new FakeTool("only_in_b")]);

        var loopA = loopFactory.Create(new FakeChatProvider(), registryA);
        var loopB = loopFactory.Create(new FakeChatProvider(), registryB);

        Assert.NotSame(loopA, loopB);
        Assert.Same(registryA.Resolve("only_in_a"), registryA.Resolve("only_in_a"));
        Assert.Throws<ToolInvocationException>(() => registryA.Resolve("only_in_b"));
        Assert.Throws<ToolInvocationException>(() => registryB.Resolve("only_in_a"));
    }
}
