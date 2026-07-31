using System.Text.Json;
using Litos.Api.Channels.Telegram;

namespace Litos.Api.Tests.Channels.Telegram;

public class ToolCallSummaryTests
{
    private static JsonElement Args(object obj) => JsonSerializer.SerializeToElement(obj);

    [Fact]
    public void Describe_ShellTool_ShowsCommand()
    {
        var result = ToolCallSummary.Describe("shell", Args(new { command = "ls -la" }));

        Assert.Equal("🔧 shell ls -la", result);
    }

    [Fact]
    public void Describe_ReadFileTool_ShowsPath()
    {
        var result = ToolCallSummary.Describe("read_file", Args(new { path = "src/Foo.cs" }));

        Assert.Equal("🔧 read_file src/Foo.cs", result);
    }

    [Fact]
    public void Describe_SkillTool_ShowsName()
    {
        var result = ToolCallSummary.Describe("skill", Args(new { name = "research" }));

        Assert.Equal("🔧 skill research", result);
    }

    [Fact]
    public void Describe_UnknownTool_ShowsNameOnlyNoArg()
    {
        var result = ToolCallSummary.Describe("web_search", Args(new { query = "something" }));

        Assert.Equal("🔧 web_search", result);
    }

    [Fact]
    public void Describe_MissingExpectedProperty_ShowsNameOnly()
    {
        var result = ToolCallSummary.Describe("shell", Args(new { }));

        Assert.Equal("🔧 shell", result);
    }
}
