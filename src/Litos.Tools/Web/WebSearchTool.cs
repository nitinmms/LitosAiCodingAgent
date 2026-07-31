using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Litos.Agent.Tools;

namespace Litos.Tools.Web;

/// <summary>
/// Provider-agnostic web search backed by Tavily, so it behaves identically no matter which
/// chat provider (Anthropic/OpenAI/Gemini/OpenRouter) is active, rather than depending on a
/// given provider's own hosted search tool (only Anthropic/OpenAI offer one, each with a
/// different, server-executed shape that bypasses Litos's normal tool-execution/transcript path).
/// </summary>
public sealed class WebSearchTool(HttpClient httpClient, string? apiKey) : ITool
{
    private const int DefaultMaxResults = 5;

    public string Name => "web_search";

    public string Description =>
        "Search the web and return a list of results (title, URL, and a short excerpt) for a query. " +
        "Use this for current events, documentation, or anything not likely to be in the local repository.";

    public JsonElement ParameterSchema { get; } = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            query = new { type = "string", description = "The search query." },
            max_results = new { type = "integer", description = "Cap on returned results. Defaults to 5." },
        },
        required = new[] { "query" },
    });

    public async Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken ct)
    {
        var query = arguments.TryGetProperty("query", out var queryProp) ? queryProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(query))
            return ToolResult.Error("A 'query' argument is required.");

        if (string.IsNullOrEmpty(apiKey))
            return ToolResult.Error(
                "Web search is not configured. Set the TAVILY_API_KEY environment variable and restart.");

        var maxResults = arguments.TryGetProperty("max_results", out var maxResultsProp) && maxResultsProp.ValueKind == JsonValueKind.Number
            ? maxResultsProp.GetInt32()
            : DefaultMaxResults;
        maxResults = maxResults <= 0 ? DefaultMaxResults : maxResults;

        TavilyResponse? response;
        try
        {
            using var httpResponse = await httpClient.PostAsJsonAsync(
                "search", new TavilyRequest(apiKey, query, maxResults), ct);

            if (!httpResponse.IsSuccessStatusCode)
                return ToolResult.Error($"Web search failed: {(int)httpResponse.StatusCode} {httpResponse.ReasonPhrase}");

            response = await httpResponse.Content.ReadFromJsonAsync<TavilyResponse>(ct);
        }
        catch (HttpRequestException ex)
        {
            return ToolResult.Error($"Web search failed: {ex.Message}");
        }

        if (response?.Results is not { Count: > 0 })
            return ToolResult.Ok("No results found.");

        var formatted = response.Results.Select(result => $"{result.Title} — {result.Url}\n{result.Content}");
        return ToolResult.Ok(string.Join("\n\n", formatted));
    }

    private sealed record TavilyRequest(
        [property: JsonPropertyName("api_key")] string ApiKey,
        [property: JsonPropertyName("query")] string Query,
        [property: JsonPropertyName("max_results")] int MaxResults);

    private sealed record TavilyResponse(List<TavilyResult>? Results);

    private sealed record TavilyResult(string Title, string Url, string Content);
}
