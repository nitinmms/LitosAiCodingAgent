using Litos.Agent.Tools;
using Litos.SoftwareFactory.Contracts;
using Litos.Tools.Web;

namespace Litos.SoftwareFactory.Worker;

/// <summary>
/// The worker's web search: the engine's own web_search tool, searching through the factory host,
/// which holds the key and records every query (m3-architecture.md §6). The worker never sees the key.
/// </summary>
public sealed class HostWebSearchBackend(FactoryHostClient host, string sessionId) : IWebSearchBackend
{
    public async Task<ToolResult> SearchAsync(string query, int maxResults, CancellationToken ct)
    {
        try
        {
            var response = await host.WebSearchAsync(new WebSearchRequest(sessionId, query, maxResults), ct);
            return response.IsError ? ToolResult.Error(response.Text) : ToolResult.Ok(response.Text);
        }
        catch (HttpRequestException ex)
        {
            return ToolResult.Error($"Web search failed: the factory host could not be reached ({ex.Message}).");
        }
    }
}
