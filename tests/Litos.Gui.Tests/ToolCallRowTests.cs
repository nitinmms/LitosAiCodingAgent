using System.Text.Json;
using Litos.Agent.Tools;
using Litos.Gui;

namespace Litos.Gui.Tests;

public class ToolCallRowTests
{
    [Theory]
    [InlineData("mcp__erpplus-database-mcp__run_select_query", true)]
    [InlineData("mcp__filesystem__read_file", true)]
    [InlineData("read_file", false)]
    [InlineData("shell", false)]
    [InlineData("", false)]
    public void IsMcpTool_DetectsMcpPrefix(string toolName, bool expected)
    {
        Assert.Equal(expected, ToolCallRow.IsMcpTool(toolName));
    }

    [Fact]
    public void DescribeMcpDetail_WhileRunning_ShowsArgumentsOnly()
    {
        var arguments = JsonDocument.Parse("""{"query":"select * from items"}""").RootElement;

        var detail = ToolCallRow.DescribeMcpDetail(arguments, result: null);

        Assert.StartsWith("Arguments:\n", detail);
        Assert.Contains("\"query\"", detail);
        Assert.DoesNotContain("Result:", detail);
    }

    [Fact]
    public void DescribeMcpDetail_Completed_AppendsRawResultTextAfterArguments()
    {
        var arguments = JsonDocument.Parse("""{"table":"view_itemmaster_es"}""").RootElement;
        var result = ToolResult.Ok("partnumber | description\nLUG-001    | Copper Lug 4AWG");

        var detail = ToolCallRow.DescribeMcpDetail(arguments, result);

        Assert.Contains("Arguments:\n", detail);
        Assert.Contains("\"table\"", detail);
        Assert.Contains("Result:\npartnumber | description\nLUG-001    | Copper Lug 4AWG", detail);
    }

    [Fact]
    public void DescribeMcpDetail_Completed_PreservesResultTextVerbatim_NotReparsedAsJson()
    {
        // Guards against DescribeMcpDetail ever trying to pretty-print result.Text as JSON: most
        // MCP servers return plain text or already-formatted content, not raw JSON meant for
        // reformatting, and a non-JSON payload must survive unmangled.
        var arguments = JsonDocument.Parse("{}").RootElement;
        var result = ToolResult.Ok("Table view_itemmaster_es not found");

        var detail = ToolCallRow.DescribeMcpDetail(arguments, result);

        Assert.EndsWith("Result:\nTable view_itemmaster_es not found", detail);
    }

    [Fact]
    public void DescribeMcpDetail_ErrorResult_StillIncludesResultText()
    {
        var arguments = JsonDocument.Parse("{}").RootElement;
        var result = ToolResult.Error("connection refused");

        var detail = ToolCallRow.DescribeMcpDetail(arguments, result);

        Assert.Contains("Result:\nconnection refused", detail);
    }
}
