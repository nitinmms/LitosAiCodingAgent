using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Litos.Agent.Tools;

namespace Litos.Tools.Web;

/// <summary>Where a <see cref="WebSearchTool"/>'s query goes: Tavily directly, or a host that searches on the agent's behalf.</summary>
public interface IWebSearchBackend
{
    /// <summary>Searches, and returns what the model is shown: the results, or why there are none.</summary>
    Task<ToolResult> SearchAsync(string query, int maxResults, CancellationToken ct);
}

/// <summary>
/// Provider-agnostic web search backed by Tavily, so it behaves identically no matter which
/// chat provider (Anthropic/OpenAI/Gemini/OpenRouter) is active, rather than depending on a
/// given provider's own hosted search tool (only Anthropic/OpenAI offer one, each with a
/// different, server-executed shape that bypasses Litos's normal tool-execution/transcript path).
/// The tool reads the model's arguments; its backend does the search, so the factory's worker
/// can search through its host, which holds the key, with exactly the tool the other faces have.
/// </summary>
public sealed class WebSearchTool(IWebSearchBackend backend) : ITool
{
    public const int DefaultMaxResults = 5;

    /// <summary>Tavily called directly with <paramref name="apiKey"/>; without one, every search says how to set it.</summary>
    public WebSearchTool(HttpClient httpClient, string? apiKey) : this(new TavilyWebSearchBackend(httpClient, apiKey))
    {
    }

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

    public Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken ct)
    {
        var query = arguments.TryGetProperty("query", out var queryProp) ? queryProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(query))
            return Task.FromResult(ToolResult.Error("A 'query' argument is required."));

        var maxResults = arguments.TryGetProperty("max_results", out var maxResultsProp) && maxResultsProp.ValueKind == JsonValueKind.Number
            ? maxResultsProp.GetInt32()
            : DefaultMaxResults;
        maxResults = maxResults <= 0 ? DefaultMaxResults : maxResults;

        return backend.SearchAsync(query, maxResults, ct);
    }
}

/// <summary>Searching Tavily with a key of the process's own (TAVILY_API_KEY).</summary>
public sealed class TavilyWebSearchBackend(HttpClient httpClient, string? apiKey) : IWebSearchBackend
{
    private readonly TavilySearchClient _client = new(httpClient);

    public async Task<ToolResult> SearchAsync(string query, int maxResults, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(apiKey))
            return ToolResult.Error("Web search is not configured. Set the TAVILY_API_KEY environment variable and restart.");

        try
        {
            return ToolResult.Ok(TavilySearchClient.Format(await _client.SearchAsync(apiKey, query, maxResults, ct)));
        }
        catch (WebSearchException ex)
        {
            return ToolResult.Error(ex.Message);
        }
    }
}

/// <summary>One web search result.</summary>
public sealed record WebSearchResult(string Title, string Url, string Content);

/// <summary>A search that failed; its message is what the model is shown.</summary>
public sealed class WebSearchException(string message) : Exception(message);

/// <summary>Tavily's search API: the results as data, for a caller that also records them.</summary>
public sealed class TavilySearchClient(HttpClient httpClient)
{
    /// <exception cref="WebSearchException">Tavily refused the search or could not be reached.</exception>
    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(string apiKey, string query, int maxResults, CancellationToken ct)
    {
        TavilyResponse? response;
        try
        {
            using var httpResponse = await httpClient.PostAsJsonAsync("search", new TavilyRequest(apiKey, query, maxResults), ct);

            if (!httpResponse.IsSuccessStatusCode)
                throw new WebSearchException($"Web search failed: {(int)httpResponse.StatusCode} {httpResponse.ReasonPhrase}");

            response = await httpResponse.Content.ReadFromJsonAsync<TavilyResponse>(ct);
        }
        catch (HttpRequestException ex)
        {
            throw new WebSearchException($"Web search failed: {ex.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new WebSearchException("Web search failed: the search service did not answer in time.");
        }

        return [.. (response?.Results ?? []).Select(r => new WebSearchResult(r.Title, r.Url, r.Content))];
    }

    /// <summary>The results as the model reads them: title, URL and excerpt, one block each.</summary>
    public static string Format(IReadOnlyList<WebSearchResult> results) =>
        results.Count == 0
            ? "No results found."
            : string.Join("\n\n", results.Select(result => $"{result.Title} — {result.Url}\n{result.Content}"));

    private sealed record TavilyRequest(
        [property: JsonPropertyName("api_key")] string ApiKey,
        [property: JsonPropertyName("query")] string Query,
        [property: JsonPropertyName("max_results")] int MaxResults);

    private sealed record TavilyResponse(List<TavilyResult>? Results);

    private sealed record TavilyResult(string Title, string Url, string Content);
}
