using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Litos.Agent.Providers;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Settings;
using Litos.SoftwareFactory.Core.Store;

namespace Litos.SoftwareFactory.Host.Settings;

/// <summary>Where a provider's model list comes from. Replaced in tests.</summary>
public interface IModelCatalogSource
{
    /// <exception cref="Exception">The provider could not be reached, or refused the key.</exception>
    Task<IReadOnlyList<ModelCatalogEntry>> ListAsync(string provider, CancellationToken ct);
}

/// <summary>
/// Each provider's own list, through the engine's <see cref="IChatProvider.ListModelsAsync"/>,
/// except OpenRouter's: its public <c>/models</c> is read here, since it also says which models
/// take tools and what they cost, which the engine's list drops (m3-architecture.md §4.3).
/// </summary>
public sealed class ProviderModelCatalogSource(IChatProviderFactory providers, HttpClient openRouter) : IModelCatalogSource
{
    public async Task<IReadOnlyList<ModelCatalogEntry>> ListAsync(string provider, CancellationToken ct)
    {
        if (provider == "openrouter")
        {
            var response = await openRouter.GetFromJsonAsync<OpenRouterModels>("models", ct);
            return [.. (response?.Data ?? []).Select(m => new ModelCatalogEntry
            {
                Provider = provider,
                ModelId = m.Id,
                DisplayName = m.Name,
                ContextLength = m.ContextLength,
                SupportsTools = m.SupportedParameters is { } parameters ? parameters.Contains("tools") : null,
                InputPricePerMillion = PerMillion(m.Pricing?.Prompt),
                OutputPricePerMillion = PerMillion(m.Pricing?.Completion),
            })];
        }

        var models = await providers.Resolve(provider).ListModelsAsync(ct);
        return [.. models.Select(m => new ModelCatalogEntry
        {
            Provider = provider,
            ModelId = m.Id,
            DisplayName = m.DisplayName == m.Id ? null : m.DisplayName,
            ContextLength = m.ContextLength,
        })];
    }

    /// <summary>OpenRouter prices a token in dollars, as text; a router whose price varies says -1.</summary>
    private static decimal? PerMillion(string? perToken) =>
        decimal.TryParse(perToken, NumberStyles.Float, CultureInfo.InvariantCulture, out var price) && price >= 0 ? price * 1_000_000m : null;

    private sealed record OpenRouterModels(List<OpenRouterModel>? Data);

    private sealed record OpenRouterModel(
        string Id,
        string? Name,
        [property: JsonPropertyName("context_length")] int? ContextLength,
        [property: JsonPropertyName("supported_parameters")] List<string>? SupportedParameters,
        OpenRouterPricing? Pricing);

    private sealed record OpenRouterPricing(string? Prompt, string? Completion);
}

/// <summary>
/// The model catalog (m3-architecture.md §4.3): every provider's list at its last fetch, held in
/// memory as <see cref="Current"/> and stored in <c>model_catalog</c>. A fetch happens when an Admin
/// asks for one: the Providers tab asks when a key is set, when a provider is first enabled, and
/// from its Refresh button. A failed fetch keeps the list of the last one that worked.
/// </summary>
public sealed class ModelCatalogService(IFactoryStore store, IModelCatalogSource source, IClock clock, ILogger<ModelCatalogService> logger)
{
    /// <summary>As long as the column that holds it.</summary>
    private const int MaxDisplayNameLength = 300;

    private readonly SemaphoreSlim _refreshing = new(1, 1);
    private volatile ModelCatalog _current = ModelCatalog.Empty;

    public ModelCatalog Current => _current;

    public async Task LoadAsync(CancellationToken ct)
    {
        var (models, fetches) = await store.ListModelCatalogAsync(ct);
        _current = new ModelCatalog(models, fetches);
    }

    /// <summary>Fetches one provider's list and stores it, or records why it could not be fetched.</summary>
    public async Task<ProviderCatalog> RefreshAsync(string provider, CancellationToken ct)
    {
        // One fetch at a time: two Admins refreshing at once would only fetch twice.
        await _refreshing.WaitAsync(ct);
        try
        {
            IReadOnlyList<ModelCatalogEntry> listed;
            try
            {
                listed = await source.ListAsync(provider, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "The {Provider} model list could not be fetched.", provider);
                var failed = await store.RecordModelCatalogFailureAsync(provider, ex.Message, clock.UtcNow, ct);
                return Replace(new ProviderCatalog(failed, _current.Of(provider)?.Models ?? []));
            }

            var models = listed.Select(m => new ModelCatalogEntry
            {
                Provider = provider,
                ModelId = ModelCatalog.Normalize(provider, m.ModelId),
                DisplayName = m.DisplayName is { Length: > MaxDisplayNameLength } name ? name[..MaxDisplayNameLength] : m.DisplayName,
                ContextLength = m.ContextLength,
                SupportsTools = m.SupportsTools,
                InputPricePerMillion = m.InputPricePerMillion,
                OutputPricePerMillion = m.OutputPricePerMillion,
            }).Where(m => m.ModelId.Length is > 0 and <= ProviderSettings.MaxModelIdLength).DistinctBy(m => m.ModelId).ToList();

            var fetched = await store.ReplaceModelCatalogAsync(provider, models, clock.UtcNow, ct);
            return Replace(new ProviderCatalog(fetched, models));
        }
        finally
        {
            _refreshing.Release();
        }
    }

    private ProviderCatalog Replace(ProviderCatalog catalog)
    {
        _current = _current.With(catalog);
        return catalog;
    }
}
