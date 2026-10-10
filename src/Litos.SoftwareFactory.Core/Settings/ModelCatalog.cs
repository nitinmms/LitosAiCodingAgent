using Litos.SoftwareFactory.Core.Store;

namespace Litos.SoftwareFactory.Core.Settings;

/// <summary>What one provider listed at its last fetch, and how that fetch went.</summary>
public sealed record ProviderCatalog(ModelCatalogFetch Fetch, IReadOnlyList<ModelCatalogEntry> Models)
{
    /// <summary>A fetch has worked at least once, so the list can be trusted to say what is offered.</summary>
    public bool Fetched => Fetch.FetchedAt is not null;

    public ModelCatalogEntry? Find(string modelId)
    {
        var id = ModelCatalog.Normalize(Fetch.Provider, modelId);
        return Models.FirstOrDefault(m => m.ModelId == id);
    }
}

/// <summary>
/// The model catalog as the host holds it (m3-architecture.md §4.3): every provider's models at its
/// last fetch. It decides which allowed models are retired, and which models "allow every model"
/// adds. A provider never fetched retires nothing and adds nothing.
/// </summary>
public sealed class ModelCatalog
{
    public static readonly ModelCatalog Empty = new([], []);

    private readonly Dictionary<string, ProviderCatalog> _providers;

    public ModelCatalog(IEnumerable<ModelCatalogEntry> models, IEnumerable<ModelCatalogFetch> fetches)
    {
        var byProvider = models.GroupBy(m => m.Provider).ToDictionary(g => g.Key, g => (IReadOnlyList<ModelCatalogEntry>)[.. g]);
        _providers = fetches.ToDictionary(f => f.Provider, f => new ProviderCatalog(f, byProvider.GetValueOrDefault(f.Provider) ?? []));
    }

    public ProviderCatalog? Of(string provider) => _providers.GetValueOrDefault(provider);

    /// <summary>A copy with one provider's catalog replaced, after a fetch.</summary>
    public ModelCatalog With(ProviderCatalog catalog) => new(
        _providers.Values.Where(p => p.Fetch.Provider != catalog.Fetch.Provider).SelectMany(p => p.Models).Concat(catalog.Models),
        _providers.Values.Where(p => p.Fetch.Provider != catalog.Fetch.Provider).Select(p => p.Fetch).Append(catalog.Fetch));

    /// <summary>
    /// An allowed model its provider no longer lists: members cannot choose it, and a default on
    /// it falls back. Only a provider whose list has been fetched can retire one, and a local
    /// server never does, since it lists only the models it has loaded at that moment.
    /// </summary>
    public bool IsRetired(string provider, string modelId) =>
        KnownProviders.Find(provider) is { UsesBaseUrl: false }
        && Of(provider) is { Fetched: true } catalog
        && catalog.Find(modelId) is null;

    /// <summary>
    /// What "allow every model" offers on a provider: each model it lists with a known context
    /// length the factory accepts, leaving out those it reports cannot take tools (the factory's
    /// workers need them).
    /// </summary>
    public IEnumerable<AllowedModel> Every(string provider) =>
        (Of(provider) is { Fetched: true } catalog ? catalog.Models : [])
            .Where(m => m.SupportsTools != false
                && m.ContextLength is >= ProviderSettings.MinContextLength and <= ProviderSettings.MaxContextLength
                && m.ModelId.Length <= ProviderSettings.MaxModelIdLength)
            .Select(m => new AllowedModel(m.ModelId, m.ContextLength!.Value));

    /// <summary>
    /// The id the catalog keeps a model under. Gemini lists <c>models/gemini-x</c>, and is called
    /// with <c>gemini-x</c> or with that, so the prefix is dropped on both sides.
    /// </summary>
    public static string Normalize(string provider, string modelId) =>
        provider == "gemini" && modelId.StartsWith("models/", StringComparison.Ordinal) ? modelId["models/".Length..] : modelId;
}
