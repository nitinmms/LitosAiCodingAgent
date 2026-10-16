using System.Text.Json;
using Litos.Agent.Tools;

namespace Litos.SoftwareFactory.Worker;

/// <summary>
/// Stands in, inside kernel code, for an MCP tool the factory's settings deny for this run
/// (m3-architecture.md §12). Without it a call to the tool did not compile — "the name … does not
/// exist", the same message as a typo — and agents spent calls guessing at the spelling. With it
/// the call compiles and returns a refusal that names the policy. It is never run: it only ever
/// returns that refusal, and it is offered to kernel code, not as a tool of the model's own.
/// </summary>
public sealed class DeniedMcpTool(string name, string description, JsonElement parameterSchema) : ITool
{
    public string Name => name;

    public string Description => $"Denied for this run by the factory's MCP settings. {description}";

    public JsonElement ParameterSchema => parameterSchema;

    public Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken ct) => Task.FromResult(new ToolResult(
        $"{name} is denied for this run by the factory's MCP settings, so it was not called. "
        + "Carry on without it, and say in your summary what you could not do.",
        IsError: true));
}
