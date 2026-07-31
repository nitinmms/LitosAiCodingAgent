using System.Text.Json;
using Litos.Agent.Tools;

namespace Litos.Agent.Tests.Fakes;

/// <summary>
/// Scriptable ITool. Defaults to returning ToolResult.Ok("ok") for whatever's invoked;
/// tests override Handler to script success, an error result, or a thrown exception.
/// Records every invocation's arguments for assertion.
/// </summary>
public sealed class FakeTool(string name = "fake_tool") : ITool
{
    public string Name { get; } = name;

    public string Description => "A fake tool for tests.";

    public JsonElement ParameterSchema { get; } = JsonSerializer.SerializeToElement(new { type = "object" });

    public Func<JsonElement, CancellationToken, Task<ToolResult>> Handler { get; set; } =
        (_, _) => Task.FromResult(ToolResult.Ok("ok"));

    public List<JsonElement> ReceivedArguments { get; } = [];

    public Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken ct)
    {
        ReceivedArguments.Add(arguments);
        return Handler(arguments, ct);
    }
}
