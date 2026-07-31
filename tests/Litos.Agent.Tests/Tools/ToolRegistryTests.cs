using Litos.Agent.Tests.Fakes;
using Litos.Agent.Tools;

namespace Litos.Agent.Tests.Tools;

public class ToolRegistryTests
{
    [Fact]
    public void Schemas_ReflectsRegisteredTools_InEnumerationOrder()
    {
        var registry = new ToolRegistry([new FakeTool("first"), new FakeTool("second")]);

        Assert.Equal(["first", "second"], registry.Schemas.Select(s => s.Name));
    }

    [Fact]
    public void Schemas_IsEmpty_WhenNoToolsRegistered()
    {
        var registry = new ToolRegistry([]);

        Assert.Empty(registry.Schemas);
    }

    [Fact]
    public void Constructor_Throws_OnDuplicateToolNames()
    {
        Assert.Throws<ArgumentException>(() => new ToolRegistry([new FakeTool("dup"), new FakeTool("dup")]));
    }

    [Fact]
    public void Resolve_ReturnsRegisteredTool_ByExactName()
    {
        var tool = new FakeTool("read_file");
        var registry = new ToolRegistry([tool]);

        Assert.Same(tool, registry.Resolve("read_file"));
    }

    [Fact]
    public void Resolve_Throws_ForUnknownToolName()
    {
        var registry = new ToolRegistry([new FakeTool("read_file")]);

        var ex = Assert.Throws<ToolInvocationException>(() => registry.Resolve("write_file"));
        Assert.Equal("Unknown tool 'write_file'.", ex.Message);
    }

    [Fact]
    public void Resolve_Throws_ForUnknownTool_WhenRegistryIsEmpty()
    {
        var registry = new ToolRegistry([]);

        Assert.Throws<ToolInvocationException>(() => registry.Resolve("anything"));
    }

    [Fact]
    public void Resolve_IsCaseSensitive()
    {
        var registry = new ToolRegistry([new FakeTool("read_file")]);

        Assert.Throws<ToolInvocationException>(() => registry.Resolve("READ_FILE"));
    }
}
