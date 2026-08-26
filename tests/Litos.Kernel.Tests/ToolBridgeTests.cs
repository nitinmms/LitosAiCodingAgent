using System.Text.Json;
using Litos.Kernel;

namespace Litos.Kernel.Tests;

/// <summary>
/// The tool bridge exercised end to end at the RunLoop/ScriptSession level: a generated wrapper
/// function (ToolWrapperCodeGen) calls ToolBridge.CallAsync, which emits a ToolCallRequest over the
/// same protocol stream an eval's own result travels on — InProcessKernelHostFixture answers it
/// with a canned response so the round trip can be asserted without a real ITool/subprocess.
/// </summary>
public sealed class ToolBridgeTests
{
    private static readonly BridgedToolSchema EchoTool = new(
        "echo_tool",
        "Echoes back whatever text argument it's given.",
        JsonSerializer.SerializeToElement(new { type = "object", properties = new { text = new { type = "string" } } }));

    [Fact]
    public async Task BridgedTool_GeneratedWrapperFunction_RoundTripsThroughToolCallRequestResponse()
    {
        await using var fixture = new InProcessKernelHostFixture([EchoTool]);
        await fixture.InitializeAsync();

        var result = await fixture.EvalAsync(
            "await echo_tool(\"{\\\"text\\\":\\\"hi\\\"}\")",
            toolResponse: ("hi echoed back", false));

        Assert.False(result.IsError);
        Assert.Equal("hi echoed back", result.ReturnValueText);
    }

    [Fact]
    public async Task BridgedTool_ErrorResponse_SurfacesAsARoslynExceptionTheModelCanRecoverFrom()
    {
        await using var fixture = new InProcessKernelHostFixture([EchoTool]);
        await fixture.InitializeAsync();

        var result = await fixture.EvalAsync(
            "await echo_tool(\"{}\")",
            toolResponse: ("boom", true));

        Assert.True(result.IsError);
        Assert.Contains("boom", result.ReturnValueText);

        // A failed bridged call must not corrupt ScriptState for the next eval.
        var recovered = await fixture.EvalAsync("1 + 1");
        Assert.False(recovered.IsError);
        Assert.Equal("2", recovered.ReturnValueText);
    }

    [Fact]
    public async Task BridgedTool_NameIsSanitizedButRoutesToTheOriginalToolName()
    {
        // MCP's naming convention (mcp__server__tool) is not a valid C# identifier as-is —
        // ToolWrapperCodeGen sanitizes it for the generated function name but must still pass the
        // *original* name to ToolBridge.CallAsync so KernelSession's routing (by exact tool name)
        // still resolves correctly.
        var mcpStyleTool = new BridgedToolSchema(
            "mcp__myserver__do_thing",
            "An MCP-style tool name.",
            JsonSerializer.SerializeToElement(new { type = "object" }));

        await using var fixture = new InProcessKernelHostFixture([mcpStyleTool]);
        await fixture.InitializeAsync();

        var result = await fixture.EvalAsync(
            "await mcp__myserver__do_thing(\"{}\")",
            toolResponse: ("ok", false));

        Assert.False(result.IsError);
        Assert.Equal("ok", result.ReturnValueText);
    }
}
