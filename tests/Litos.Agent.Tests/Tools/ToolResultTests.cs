using Litos.Agent.Tools;

namespace Litos.Agent.Tests.Tools;

public class ToolResultTests
{
    [Fact]
    public void Ok_ProducesNonErrorResult()
    {
        var result = ToolResult.Ok("contents");

        Assert.Equal("contents", result.Text);
        Assert.False(result.IsError);
    }

    [Fact]
    public void Error_ProducesErrorResult()
    {
        var result = ToolResult.Error("something went wrong");

        Assert.Equal("something went wrong", result.Text);
        Assert.True(result.IsError);
    }

    [Fact]
    public void RecordEquality_ComparesTextAndIsError()
    {
        Assert.Equal(ToolResult.Ok("same"), ToolResult.Ok("same"));
        Assert.NotEqual(ToolResult.Ok("same"), ToolResult.Error("same"));
    }
}
