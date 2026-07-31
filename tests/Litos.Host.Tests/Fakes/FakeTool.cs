using System.Text.Json;
using Litos.Agent.Tools;

namespace Litos.Host.Tests.Fakes;

public sealed class FakeTool(string name, string description = "A fake tool for tests.") : ITool
{
    public string Name { get; } = name;

    public string Description { get; } = description;

    public JsonElement ParameterSchema { get; } = JsonSerializer.SerializeToElement(new { type = "object" });

    public Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken ct) =>
        Task.FromResult(ToolResult.Ok("ok"));
}
