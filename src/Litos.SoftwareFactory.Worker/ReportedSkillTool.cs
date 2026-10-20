using System.Text.Json;
using Litos.Agent.Tools;
using Litos.SoftwareFactory.Contracts;

namespace Litos.SoftwareFactory.Worker;

/// <summary>
/// The engine's skill tool, which also tells the host each skill it loaded, so the skill report can
/// say which skills runs use (m4-architecture.md §12.5). The agent sees only the skill tool's own
/// answer: a host that cannot be told never fails the load.
/// </summary>
public sealed class ReportedSkillTool(ITool inner, FactoryHostClient host, string sessionId) : ITool
{
    public string Name => inner.Name;

    public string Description => inner.Description;

    public JsonElement ParameterSchema => inner.ParameterSchema;

    public async Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken ct)
    {
        var result = await inner.InvokeAsync(arguments, ct);
        if (!result.IsError && arguments.GetStringOrNull("name") is { Length: > 0 } name)
        {
            try
            {
                await host.SkillLoadedAsync(new SkillLoadReport(sessionId, name), ct);
            }
            catch (HttpRequestException)
            {
                // Counting the load is the host's bookkeeping; the agent already has its skill.
            }
        }

        return result;
    }
}
