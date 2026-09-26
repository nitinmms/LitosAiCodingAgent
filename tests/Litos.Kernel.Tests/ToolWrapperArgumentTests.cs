using System.Text.Json;
using Litos.Kernel;

namespace Litos.Kernel.Tests;

/// <summary>
/// End-to-end coverage for the name/value-pair tool wrappers: a real script, compiled by the real
/// ScriptSession, producing a real ToolCallRequest whose arguments the fixture captures.
///
/// These assert the thing that actually broke in a live session — a Windows path reaching the tool
/// intact — rather than only that KernelArgs.Json is correct in isolation. The wrappers are
/// generated C# source, so the overload set has to genuinely compile and bind inside Roslyn
/// scripting; a unit test of KernelArgs alone would not catch an ambiguous-overload regression.
/// </summary>
public sealed class ToolWrapperArgumentTests
{
    private static readonly BridgedToolSchema ReadFile = new(
        "read_file",
        "Reads a file.",
        JsonSerializer.SerializeToElement(new { type = "object", properties = new { path = new { type = "string" } } }));

    private static readonly BridgedToolSchema WriteFile = new(
        "write_file",
        "Writes a file.",
        JsonSerializer.SerializeToElement(new { type = "object", properties = new { path = new { type = "string" }, content = new { type = "string" } } }));

    private static readonly BridgedToolSchema Shell = new(
        "shell",
        "Runs a shell command.",
        JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { command = new { type = "string" } },
            required = new[] { "command" },
        }));

    private static string Arg(ToolCallRequest call, string property) =>
        call.Arguments.GetProperty(property).GetString()!;

    /// <summary>
    /// The exact shape that failed live. Hand-written as
    /// list_directory("{\"path\":\"c:\\temp\\snake1\"}") this produced
    /// "'s' is an invalid escapable character within a JSON string"; via the pairs overload with a
    /// verbatim string it must arrive byte-for-byte.
    /// </summary>
    [Fact]
    public async Task NameValuePairs_WindowsPath_ReachesTheToolIntact()
    {
        await using var fixture = new InProcessKernelHostFixture([ReadFile]);
        await fixture.InitializeAsync();

        var result = await fixture.EvalAsync(
            """await read_file("path", @"c:\temp\snake1\snake.py")""",
            toolResponse: ("file contents", false));

        Assert.False(result.IsError, result.ReturnValueText);
        var call = Assert.Single(fixture.ObservedToolCalls);
        Assert.Equal("read_file", call.ToolName);
        Assert.Equal(@"c:\temp\snake1\snake.py", Arg(call, "path"));
    }

    [Fact]
    public async Task NameValuePairs_MultipleArguments_AllReachTheTool()
    {
        await using var fixture = new InProcessKernelHostFixture([WriteFile]);
        await fixture.InitializeAsync();

        var result = await fixture.EvalAsync(
            """await write_file("path", @"c:\dir\out.txt", "content", "line1\nline2")""",
            toolResponse: ("ok", false));

        Assert.False(result.IsError, result.ReturnValueText);
        var call = Assert.Single(fixture.ObservedToolCalls);
        Assert.Equal(@"c:\dir\out.txt", Arg(call, "path"));
        Assert.Equal("line1\nline2", Arg(call, "content"));
    }

    /// <summary>
    /// Content containing quotes and backslashes is the other half of the escaping problem — it is
    /// what a script writing C# or JSON source into a file actually produces.
    /// </summary>
    [Fact]
    public async Task NameValuePairs_ContentWithQuotesAndBackslashes_ReachesTheToolIntact()
    {
        await using var fixture = new InProcessKernelHostFixture([WriteFile]);
        await fixture.InitializeAsync();

        var result = await fixture.EvalAsync(
            """"
            await write_file("path", @"c:\a.json", "content", @"{""k"": ""v\path""}")
            """",
            toolResponse: ("ok", false));

        Assert.False(result.IsError, result.ReturnValueText);
        var call = Assert.Single(fixture.ObservedToolCalls);
        Assert.Equal(@"{""k"": ""v\path""}".Replace("\"\"", "\""), Arg(call, "content"));
    }

    /// <summary>
    /// The raw-JSON overload must keep working: scripts that already hold a JSON document, and any
    /// model that writes the old form, must not break. This is also the overload-resolution guard —
    /// a single string argument has to bind to the string overload, not the params one.
    /// </summary>
    [Fact]
    public async Task RawJsonOverload_StillWorks()
    {
        await using var fixture = new InProcessKernelHostFixture([ReadFile]);
        await fixture.InitializeAsync();

        var result = await fixture.EvalAsync(
            """await read_file("{\"path\":\"a.txt\"}")""",
            toolResponse: ("file contents", false));

        Assert.False(result.IsError, result.ReturnValueText);
        var call = Assert.Single(fixture.ObservedToolCalls);
        Assert.Equal("a.txt", Arg(call, "path"));
    }

    [Fact]
    public async Task NoArguments_SendsAnEmptyObject()
    {
        await using var fixture = new InProcessKernelHostFixture([ReadFile]);
        await fixture.InitializeAsync();

        var result = await fixture.EvalAsync("await read_file()", toolResponse: ("ok", false));

        Assert.False(result.IsError, result.ReturnValueText);
        var call = Assert.Single(fixture.ObservedToolCalls);
        Assert.Equal(JsonValueKind.Object, call.Arguments.ValueKind);
        Assert.Empty(call.Arguments.EnumerateObject());
    }

    /// <summary>
    /// A zero-argument tool ("properties": {} and an empty "required") shipped in 0.1.26 and failed
    /// every kernel start with "Operation is not valid due to the current state of the object" —
    /// generating its positional-call hint read .Name off a default JsonProperty. Init must succeed
    /// with such a tool bridged, and calling it must still work.
    /// </summary>
    [Theory]
    [InlineData("""{"type":"object","properties":{}}""")]
    [InlineData("""{"type":"object","properties":{},"required":[]}""")]
    [InlineData("""{"type":"object"}""")]
    public async Task ZeroArgumentTool_DoesNotBreakInit_AndIsCallable(string schemaJson)
    {
        var noArgs = new BridgedToolSchema("list_sessions", "Lists sessions.", JsonDocument.Parse(schemaJson).RootElement.Clone());
        await using var fixture = new InProcessKernelHostFixture([noArgs, ReadFile]);
        await fixture.InitializeAsync();

        var result = await fixture.EvalAsync("await list_sessions()", toolResponse: ("ok", false));

        Assert.False(result.IsError, result.ReturnValueText);
        Assert.Equal("list_sessions", Assert.Single(fixture.ObservedToolCalls).ToolName);
    }

    /// <summary>
    /// A mistake in pair count must surface as a readable script-level error the model can correct,
    /// not a malformed call reaching the tool.
    /// </summary>
    [Fact]
    public async Task OddNumberOfValues_FailsTheEvalWithoutCallingTheTool()
    {
        await using var fixture = new InProcessKernelHostFixture([WriteFile]);
        await fixture.InitializeAsync();

        var result = await fixture.EvalAsync("""await write_file("path", @"c:\a.txt", "content")""");

        Assert.True(result.IsError);
        Assert.Contains("name, value pairs", result.ReturnValueText ?? "");
        Assert.Empty(fixture.ObservedToolCalls);
    }

    /// <summary>
    /// The other live failure: shell("pwd &amp;&amp; ls"), a positional value where the tool expects
    /// named arguments. A lone string binds to the RAW-JSON overload (the `string` parameter is
    /// applicable without params expansion, so it beats `params object?[]`), which is why the pair-count
    /// check never saw it — 1 is odd, and would have produced good guidance had it got that far.
    /// Instead the value reached ToolBridge's JsonSerializer as "pwd &amp;&amp; ls" and the model was
    /// told only "'p' is an invalid start of a value. Path: $ | LineNumber: 0 | BytePositionInLine: 0."
    ///
    /// Asserted end-to-end rather than on KernelArgs alone because the argument name in the suggestion
    /// is baked into the generated wrapper from the tool's schema — a unit test could not catch that
    /// codegen passing the wrong name, or none at all.
    /// </summary>
    [Fact]
    public async Task PositionalSingleValue_FailsTheEvalWithGuidanceNamingTheArgument_WithoutCallingTheTool()
    {
        await using var fixture = new InProcessKernelHostFixture([Shell]);
        await fixture.InitializeAsync();

        var result = await fixture.EvalAsync("""await shell("pwd && ls")""");

        Assert.True(result.IsError);
        Assert.Contains("name, value pairs", result.ReturnValueText ?? "");
        Assert.Contains("""shell("command", "pwd && ls")""", result.ReturnValueText ?? "");
        Assert.Empty(fixture.ObservedToolCalls);
    }

    /// <summary>
    /// Task.WhenAll over several bridged calls is the batching Programmatic Tool Calling exists to
    /// enable, and it failed live with "CS0103: The name 'Task' does not exist in the current context"
    /// — System.Threading.Tasks was absent from ScriptSession's imports. Nothing else noticed, because
    /// `await` on a wrapper compiles without that import (the awaited type comes from the generated
    /// signature's own fully-qualified return type); only NAMING Task breaks. The model was reading
    /// three files concurrently and was pushed back to three sequential awaits.
    /// </summary>
    [Fact]
    public async Task TaskWhenAll_OverSeveralBridgedCalls_CompilesAndIssuesEveryCall()
    {
        await using var fixture = new InProcessKernelHostFixture([ReadFile]);
        await fixture.InitializeAsync();

        var result = await fixture.EvalAsync(
            """
            var texts = await Task.WhenAll(new[] { "a.txt", "b.txt", "c.txt" }.Select(f => read_file("path", f)));
            Console.WriteLine(texts.Length);
            """,
            toolResponse: ("file contents", false));

        Assert.False(result.IsError, result.ReturnValueText);
        Assert.Equal(3, fixture.ObservedToolCalls.Count);
        Assert.Equal(
            ["a.txt", "b.txt", "c.txt"],
            fixture.ObservedToolCalls.Select(call => Arg(call, "path")).OrderBy(path => path, StringComparer.Ordinal).ToArray());
        Assert.Contains("3", result.Output ?? "");
    }
}
