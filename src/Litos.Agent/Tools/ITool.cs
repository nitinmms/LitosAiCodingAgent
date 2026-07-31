using System.Text.Json;

namespace Litos.Agent.Tools;

public interface ITool
{
    string Name { get; }
    string Description { get; }
    JsonElement ParameterSchema { get; }
    Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken ct);
}
