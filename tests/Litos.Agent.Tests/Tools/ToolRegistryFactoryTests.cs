using Litos.Agent.Tests.Fakes;
using Litos.Agent.Tools;

namespace Litos.Agent.Tests.Tools;

public class ToolRegistryFactoryTests
{
    [Fact]
    public void Create_WithNoSources_ReturnsOnlyStaticTools()
    {
        var factory = new ToolRegistryFactory([new FakeTool("read_file"), new FakeTool("shell")], []);

        var registry = factory.Create();

        Assert.Equal(["read_file", "shell"], registry.Schemas.Select(s => s.Name));
    }

    [Fact]
    public void Create_IncludesCurrentToolsFromEverySource()
    {
        var sourceA = new FakeToolSource { CurrentTools = [new FakeTool("mcp__a__tool1")] };
        var sourceB = new FakeToolSource { CurrentTools = [new FakeTool("mcp__b__tool1")] };
        var factory = new ToolRegistryFactory([new FakeTool("read_file")], [sourceA, sourceB]);

        var registry = factory.Create();

        Assert.Equal(
            ["read_file", "mcp__a__tool1", "mcp__b__tool1"],
            registry.Schemas.Select(s => s.Name));
    }

    [Fact]
    public void Create_CalledTwice_ReflectsSourceChangeBetweenCalls()
    {
        // This is the crux of "next-turn-only" dynamic tool discovery: a source's contents
        // changing between two Create() calls (e.g. an MCP server connecting after the first
        // turn started) is picked up by the second call — simulating a turn boundary — without
        // requiring the factory itself to be reconstructed.
        var source = new FakeToolSource { CurrentTools = [] };
        var factory = new ToolRegistryFactory([], [source]);

        var beforeConnect = factory.Create();
        Assert.Empty(beforeConnect.Schemas);

        source.CurrentTools = [new FakeTool("mcp__newserver__read")];
        var afterConnect = factory.Create();

        Assert.Equal(["mcp__newserver__read"], afterConnect.Schemas.Select(s => s.Name));
    }

    [Fact]
    public void Create_EarlierSnapshot_IsUnaffectedByLaterSourceChange()
    {
        // The other half of "next-turn-only": a ToolRegistry already handed to an in-flight turn
        // must not observe a source mutating afterward — Create() freezes a snapshot, it doesn't
        // return a live view.
        var source = new FakeToolSource { CurrentTools = [new FakeTool("mcp__a__tool1")] };
        var factory = new ToolRegistryFactory([], [source]);

        var turnInFlight = factory.Create();
        source.CurrentTools = [new FakeTool("mcp__a__tool1"), new FakeTool("mcp__b__tool1")];

        Assert.Equal(["mcp__a__tool1"], turnInFlight.Schemas.Select(s => s.Name));
    }

    [Fact]
    public void Create_ResolvesToolsFromASource_ByName()
    {
        var mcpTool = new FakeTool("mcp__filesystem__read");
        var source = new FakeToolSource { CurrentTools = [mcpTool] };
        var factory = new ToolRegistryFactory([], [source]);

        var registry = factory.Create();

        Assert.Same(mcpTool, registry.Resolve("mcp__filesystem__read"));
    }
}
