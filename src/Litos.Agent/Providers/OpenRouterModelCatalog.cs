using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Litos.Agent.Providers;

/// <summary>
/// Best-effort source of per-model context length for native providers (Anthropic/OpenAI/Gemini),
/// whose own ListModelsAsync calls don't return this. OpenRouter's public model catalog
/// (GET /models, unauthenticated — no API key required) lists the same underlying models under
/// normalized ids like "anthropic/claude-sonnet-5" or "google/gemini-3-flash" together with a
/// real context_length, so it's more accurate and lower-maintenance than guessing from a
/// hardcoded table. Used as the first lookup layer; ModelContextWindows' static table is the
/// fallback when this catalog can't be fetched (offline, rate-limited) or has no match.
/// </summary>
public sealed class OpenRouterModelCatalog(HttpClient client)
{
    private static readonly string[] KnownPrefixes = ["anthropic/", "openai/", "google/"];

    private Dictionary<string, int>? _cache;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);

    /// <summary>Looks up modelId's context length in OpenRouter's public catalog, fetching and caching it on first use.</summary>
    public async Task<int?> ResolveAsync(string modelId, CancellationToken ct)
    {
        Dictionary<string, int> catalog;
        try
        {
            catalog = await GetCatalogAsync(ct);
        }
        catch
        {
            // Offline, rate-limited, or the endpoint shape changed — callers fall back to the static table.
            return null;
        }

        if (catalog.TryGetValue(modelId, out var exact))
            return exact;

        foreach (var prefix in KnownPrefixes)
            if (catalog.TryGetValue(prefix + modelId, out var prefixed))
                return prefixed;

        return null;
    }

    private async Task<Dictionary<string, int>> GetCatalogAsync(CancellationToken ct)
    {
        if (_cache is { } cached)
            return cached;

        await _cacheLock.WaitAsync(ct);
        try
        {
            if (_cache is { } cachedAfterWait)
                return cachedAfterWait;

            var response = await client.GetFromJsonAsync<OpenRouterCatalogResponse>("models", ct);
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var model in response?.Data ?? [])
                if (model.ContextLength is { } length)
                    map[model.Id] = length;

            _cache = map;
            return map;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    private sealed record OpenRouterCatalogResponse(List<OpenRouterCatalogModel> Data);

    private sealed record OpenRouterCatalogModel(string Id, [property: JsonPropertyName("context_length")] int? ContextLength);
}
