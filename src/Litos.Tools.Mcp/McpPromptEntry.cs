using ModelContextProtocol.Client;

namespace Litos.Tools.Mcp;

/// <summary>
/// One prompt discovered from a connected MCP server, paired with the connection that can fetch
/// it (McpServerConnection.GetPromptAsync) — the GUI-only, command-menu-facing counterpart to
/// McpToolProxy, which wraps a tool as an ITool. A prompt is never registered as an ITool/
/// ToolRegistry entry: its fetched result is injected as ordinary turn content (see
/// MainWindow.HandleMcpPromptAsync), not invoked mid-turn by the LLM the way a tool is.
/// </summary>
public sealed record McpPromptEntry(string ServerName, McpClientPrompt Prompt, McpServerConnection Connection)
{
    /// <summary>mcp__{server}__{prompt} — the same naming convention McpToolProxy.Name uses for
    /// tools, so a connected server can't collide the two namespaces (McpConfigStore already
    /// rejects server names containing "__").</summary>
    public string FullName => $"mcp__{ServerName}__{Prompt.Name}";
}
