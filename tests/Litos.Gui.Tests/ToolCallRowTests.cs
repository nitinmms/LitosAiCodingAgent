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

    [Theory]
    [InlineData("run_kernel_code", true)]
    [InlineData("mcp__filesystem__read_file", true)]
    [InlineData("read_file", false)]
    [InlineData("shell", false)]
    public void IsExpandable_CoversMcpAndKernelRows(string toolName, bool expected)
    {
        Assert.Equal(expected, ToolCallRow.IsExpandable(toolName));
    }

    private static JsonElement KernelArgs(string code) => JsonSerializer.SerializeToElement(new { code });

    [Fact]
    public void DescribeKernelHeader_WhileRunning_ShowsScriptSize()
    {
        Assert.Equal("Run code (2 lines)", ToolCallRow.DescribeKernelHeader(KernelArgs("var x = 1;\nx"), result: null));
    }

    [Fact]
    public void DescribeKernelHeader_Completed_ShowsOutputLineCount_NotItsFirstLine()
    {
        var result = ToolResult.Ok("ACTIVITIES:\n{\n  \"id\": 1\n}\r\n\n[kernel state changed this round: +variable a (string)]");

        Assert.Equal("Run code (2 lines) → 4 lines of output", ToolCallRow.DescribeKernelHeader(KernelArgs("var a = Get();\nConsole.WriteLine(a);"), result));
    }

    [Fact]
    public void DescribeKernelHeader_Error_ShowsTheErrorsFirstLine()
    {
        var result = ToolResult.Error("CS0103: The name 'y' does not exist\n  at line 2");

        Assert.Equal("Run code (1 line) → CS0103: The name 'y' does not exist …", ToolCallRow.DescribeKernelHeader(KernelArgs("y"), result));
    }

    [Fact]
    public void DescribeKernelDetail_WhileRunning_ShowsCodeOnly()
    {
        var (detail, note) = ToolCallRow.DescribeKernelDetail(KernelArgs("var x = 1;\n"), result: null);

        Assert.Equal("Code:\nvar x = 1;", detail);
        Assert.Null(note);
    }

    [Fact]
    public void DescribeKernelDetail_Completed_ShowsFullOutput_AndTheTrailerSeparately()
    {
        var result = ToolResult.Ok(".git/\nsrc/\r\n\n[kernel state changed this round: +variable ls (string, 91 chars)]");

        var (detail, note) = ToolCallRow.DescribeKernelDetail(KernelArgs("var ls = List(\".\");"), result);

        Assert.Equal("Code:\nvar ls = List(\".\");\n\nOutput:\n.git/\nsrc/", detail);
        Assert.Equal("[kernel state changed this round: +variable ls (string, 91 chars)]", note);
    }

    [Fact]
    public void DescribeKernelDetail_NoOutput_SaysSo()
    {
        var (detail, _) = ToolCallRow.DescribeKernelDetail(KernelArgs("var x = 1;"), ToolResult.Ok(""));

        Assert.Equal("Code:\nvar x = 1;\n\nOutput:\n(no output)", detail);
    }

    [Fact]
    public void DescribeKernelDetail_Error_LabelsTheOutputAsAnError()
    {
        var (detail, _) = ToolCallRow.DescribeKernelDetail(KernelArgs("y"), ToolResult.Error("CS0103: The name 'y' does not exist"));

        Assert.Equal("Code:\ny\n\nError:\nCS0103: The name 'y' does not exist", detail);
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

/// <summary>Same wording and threshold as the VS Code webview's slow-tool notice.</summary>
public class ToolCallRowSlowNoticeTests
{
    [Fact]
    public void SlowNoticeText_UnderTwentySeconds_IsNull() =>
        Assert.Null(ToolCallRow.SlowNoticeText(sending: true, TimeSpan.FromSeconds(19.9)));

    [Theory]
    [InlineData(true, 20, "waiting for the model to finish sending this tool call (20s)")]
    [InlineData(true, 296, "waiting for the model to finish sending this tool call (4m 56s)")]
    [InlineData(false, 65, "still running (1m 5s)")]
    [InlineData(false, 59, "still running (59s)")]
    public void SlowNoticeText_NamesWhichWaitItIs(bool sending, int seconds, string expected) =>
        Assert.Equal(expected, ToolCallRow.SlowNoticeText(sending, TimeSpan.FromSeconds(seconds)));
}
