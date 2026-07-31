using Litos.Agent.Tools;

namespace Litos.Tools.Mcp;

/// <summary>
/// Adapts McpToolProvider's live, connection-driven tool list to the generic IToolSource seam
/// Litos.Agent/Litos.Host consume. Thin by design — all actual connect/retry/discovery logic
/// stays in McpToolProvider; this type exists only so Litos.Agent never needs to reference
/// Litos.Tools.Mcp or know MCP exists.
/// </summary>
public sealed class McpToolSource(McpToolProvider provider) : IToolSource
{
    public IReadOnlyList<ITool> CurrentTools => provider.Tools;
}
