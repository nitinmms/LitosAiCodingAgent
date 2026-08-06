using System.Text.Json;
using Litos.Agent.Streaming;
using Litos.Agent.Tools;

namespace Litos.Agent.Tests.Streaming;

public class ToolCallSummaryTests
{
    private static JsonElement Args(object obj) => JsonSerializer.SerializeToElement(obj);

    // ---- DescribeCall ----

    [Fact]
    public void DescribeCall_ReadFile_ShowsPath() =>
        Assert.Equal("Read src/Foo.cs", ToolCallSummary.DescribeCall("read_file", Args(new { path = "src/Foo.cs" })));

    [Fact]
    public void DescribeCall_WriteFile_ShowsPath() =>
        Assert.Equal("Write src/Bar.cs", ToolCallSummary.DescribeCall("write_file", Args(new { path = "src/Bar.cs" })));

    [Fact]
    public void DescribeCall_EditFile_ShowsPath() =>
        Assert.Equal("Edit src/Bar.cs", ToolCallSummary.DescribeCall("edit_file", Args(new { path = "src/Bar.cs" })));

    [Fact]
    public void DescribeCall_ListDirectory_ShowsPath() =>
        Assert.Equal("List src", ToolCallSummary.DescribeCall("list_directory", Args(new { path = "src" })));

    [Fact]
    public void DescribeCall_Shell_ShowsCommandWithDollarPrefix() =>
        Assert.Equal("$ npm test", ToolCallSummary.DescribeCall("shell", Args(new { command = "npm test" })));

    [Fact]
    public void DescribeCall_Skill_ShowsName() =>
        Assert.Equal("Skill code-review", ToolCallSummary.DescribeCall("skill", Args(new { name = "code-review" })));

    [Fact]
    public void DescribeCall_SearchCode_WithGlob_ShowsPatternAndGlob() =>
        Assert.Equal("Search \"TODO\" in *.cs", ToolCallSummary.DescribeCall("search_code", Args(new { pattern = "TODO", glob = "*.cs" })));

    [Fact]
    public void DescribeCall_SearchCode_WithPathOnly_ShowsPatternAndPath() =>
        Assert.Equal("Search \"TODO\" in src", ToolCallSummary.DescribeCall("search_code", Args(new { pattern = "TODO", path = "src" })));

    [Fact]
    public void DescribeCall_SearchCode_PreferGlobOverPath_WhenBothPresent() =>
        Assert.Equal("Search \"TODO\" in *.cs", ToolCallSummary.DescribeCall("search_code", Args(new { pattern = "TODO", path = "src", glob = "*.cs" })));

    [Fact]
    public void DescribeCall_SearchCode_NoGlobOrPath_ShowsPatternOnly() =>
        Assert.Equal("Search \"TODO\"", ToolCallSummary.DescribeCall("search_code", Args(new { pattern = "TODO" })));

    [Fact]
    public void DescribeCall_WebSearch_ShowsQuery() =>
        Assert.Equal("Search web \"latest .NET release\"", ToolCallSummary.DescribeCall("web_search", Args(new { query = "latest .NET release" })));

    [Fact]
    public void DescribeCall_UnknownTool_FallsBackToToolName() =>
        Assert.Equal("hallucinated_tool", ToolCallSummary.DescribeCall("hallucinated_tool", Args(new { })));

    [Fact]
    public void DescribeCall_MissingProperty_RendersEmptyTarget() =>
        Assert.Equal("Read ", ToolCallSummary.DescribeCall("read_file", Args(new { })));

    [Fact]
    public void DescribeCall_ReadFile_WithOffsetAndLimit_ShowsRange() =>
        Assert.Equal("Read src/Foo.cs:501-600", ToolCallSummary.DescribeCall("read_file", Args(new { path = "src/Foo.cs", offset = 501, limit = 100 })));

    [Fact]
    public void DescribeCall_ReadFile_WithOffsetOnly_ShowsOpenEndedRange() =>
        Assert.Equal("Read src/Foo.cs:501+", ToolCallSummary.DescribeCall("read_file", Args(new { path = "src/Foo.cs", offset = 501 })));

    [Fact]
    public void DescribeCall_ReadFile_WithLimitOnly_ShowsRangeFromStart() =>
        Assert.Equal("Read src/Foo.cs:1-100", ToolCallSummary.DescribeCall("read_file", Args(new { path = "src/Foo.cs", limit = 100 })));

    // ---- DescribeResult: errors take priority regardless of tool ----

    [Fact]
    public void DescribeResult_IsError_ReturnsFirstLineOfText() =>
        Assert.Equal("File not found: missing.txt", ToolCallSummary.DescribeResult("read_file", ToolResult.Error("File not found: missing.txt\nmore detail")));

    // ---- DescribeResult: success per tool ----

    [Fact]
    public void DescribeResult_ReadFile_CountsLines() =>
        Assert.Equal("3 lines", ToolCallSummary.DescribeResult("read_file", ToolResult.Ok("line1\nline2\nline3")));

    [Fact]
    public void DescribeResult_ReadFile_EmptyFile_ZeroLines() =>
        Assert.Equal("0 lines", ToolCallSummary.DescribeResult("read_file", ToolResult.Ok("")));

    [Fact]
    public void DescribeResult_ReadFile_Truncated_ShowsShownAndTotalCount() =>
        Assert.Equal("2000 of 2500 lines", ToolCallSummary.DescribeResult(
            "read_file",
            ToolResult.Ok("1\tline1\n...\n2000\tline2000\n\n[Showing lines 1-2000 of 2500. Use offset=2001 to continue.]")));

    [Fact]
    public void DescribeResult_WriteFile_ParsesDiffStatMarker() =>
        Assert.Equal("+1 -0", ToolCallSummary.DescribeResult("write_file", ToolResult.Ok("Wrote 5 characters to foo.txt. [+1 -0]")));

    [Fact]
    public void DescribeResult_EditFile_ParsesDiffStatMarker() =>
        Assert.Equal("+1 -1", ToolCallSummary.DescribeResult("edit_file", ToolResult.Ok("Edited foo.cs. [+1 -1]")));

    [Fact]
    public void DescribeResult_WriteFile_NoMarker_FallsBackToDone() =>
        Assert.Equal("done", ToolCallSummary.DescribeResult("write_file", ToolResult.Ok("Wrote 5 characters to foo.txt.")));

    [Fact]
    public void DescribeResult_ListDirectory_CountsNonEmptyEntries() =>
        Assert.Equal("2 entries", ToolCallSummary.DescribeResult("list_directory", ToolResult.Ok("foo.cs\nbar/")));

    [Fact]
    public void DescribeResult_ListDirectory_EmptyDirectory_ZeroEntries() =>
        Assert.Equal("0 entries", ToolCallSummary.DescribeResult("list_directory", ToolResult.Ok("")));

    [Fact]
    public void DescribeResult_Shell_ParsesExitCodeMarker() =>
        Assert.Equal("exit 0", ToolCallSummary.DescribeResult("shell", ToolResult.Ok("[exit 0]\nsome output")));

    [Fact]
    public void DescribeResult_Shell_NoMarker_FallsBackToDone() =>
        Assert.Equal("done", ToolCallSummary.DescribeResult("shell", ToolResult.Ok("some output")));

    [Fact]
    public void DescribeResult_SearchCode_NoMatches() =>
        Assert.Equal("0 matches", ToolCallSummary.DescribeResult("search_code", ToolResult.Ok("No matches found.")));

    [Fact]
    public void DescribeResult_SearchCode_CountsMatchLines() =>
        Assert.Equal("2 matches", ToolCallSummary.DescribeResult("search_code", ToolResult.Ok("foo.cs:1:hit\nbar.cs:2:hit")));

    [Fact]
    public void DescribeResult_SearchCode_Truncated_UsesStatedCount() =>
        Assert.Equal("50+ matches", ToolCallSummary.DescribeResult(
            "search_code",
            ToolResult.Ok("foo.cs:1:hit\n[Truncated: showing 50 of 50+ matches. Narrow with `glob`, `path`, or a more specific `pattern`.]")));

    [Fact]
    public void DescribeResult_Skill_ReturnsLoaded() =>
        Assert.Equal("loaded", ToolCallSummary.DescribeResult("skill", ToolResult.Ok("skill body text")));

    [Fact]
    public void DescribeResult_WebSearch_NoResults() =>
        Assert.Equal("0 results", ToolCallSummary.DescribeResult("web_search", ToolResult.Ok("No results found.")));

    [Fact]
    public void DescribeResult_WebSearch_CountsResults() =>
        Assert.Equal("2 results", ToolCallSummary.DescribeResult(
            "web_search",
            ToolResult.Ok("Title One — https://a.example\nsnippet one\nTitle Two — https://b.example\nsnippet two\n")));

    [Fact]
    public void DescribeResult_UnknownTool_ReturnsFirstLineOfText() =>
        Assert.Equal("first line", ToolCallSummary.DescribeResult("hallucinated_tool", ToolResult.Ok("first line\nsecond line")));
}
