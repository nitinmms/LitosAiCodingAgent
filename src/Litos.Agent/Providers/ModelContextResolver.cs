namespace Litos.Agent.Providers;

/// <summary>
/// Shared context-length resolution for ListModelsAsync across all four providers: prefer a
/// value the provider's own SDK already returned (nativeHint — currently only Gemini's
/// Model.InputTokenLimit), then OpenRouter's public catalog, then the static fallback table.
/// Centralized so the fallback order/logic only needs to change in one place.
/// </summary>
public static class ModelContextResolver
{
    public static async Task<int> ResolveAsync(OpenRouterModelCatalog catalog, string modelId, int? nativeHint, CancellationToken ct)
    {
        if (nativeHint is > 0)
            return nativeHint.Value;

        return await catalog.ResolveAsync(modelId, ct) ?? ModelContextWindows.Resolve(modelId);
    }
}
