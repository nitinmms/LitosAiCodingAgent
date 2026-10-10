namespace Litos.SoftwareFactory.Core.Settings;

/// <summary>How far a task's budget can be trusted on a provider (m3-architecture.md §4.2).</summary>
public enum BudgetPrecision
{
    /// <summary>The provider honours the output limit and reports usage: a budget is never exceeded.</summary>
    Strict,

    /// <summary>Usage may come back as zero or approximate, so the host charges its own estimate.</summary>
    Estimated,
}

/// <summary>A provider the engine can call, by the engine's name for it.</summary>
/// <param name="UsesBaseUrl">Reached at an address an Admin gives (a local server); a key is optional.</param>
public sealed record ProviderKind(string Name, string DisplayName, BudgetPrecision Precision, bool UsesBaseUrl = false);

/// <summary>Every provider the factory can offer, in the order the app lists them.</summary>
public static class KnownProviders
{
    public static readonly IReadOnlyList<ProviderKind> All =
    [
        new("anthropic", "Anthropic", BudgetPrecision.Strict),
        new("openai", "OpenAI", BudgetPrecision.Strict),
        new("gemini", "Gemini", BudgetPrecision.Strict),
        new("openrouter", "OpenRouter", BudgetPrecision.Strict),
        new("mesh_api", "MeshApi", BudgetPrecision.Estimated),
        new("local", "Local server", BudgetPrecision.Estimated, UsesBaseUrl: true),
    ];

    public static ProviderKind? Find(string? name) => All.FirstOrDefault(p => p.Name == name);

    /// <summary>A provider the factory does not know is not trusted to keep a budget.</summary>
    public static BudgetPrecision PrecisionOf(string? name) => Find(name)?.Precision ?? BudgetPrecision.Estimated;
}

/// <summary>A model members may choose, with the context window the worker compacts against.</summary>
public sealed record AllowedModel(string Id, int ContextLength);

/// <summary>One provider's settings. Its key is a secret (SecretNames.Provider), never kept here.</summary>
public sealed record ProviderEntry
{
    public required string Name { get; init; }

    /// <summary>Offered for new threads. A thread already on it keeps working while its key is set.</summary>
    public bool Enabled { get; init; }

    /// <summary>Where a provider that UsesBaseUrl is reached, such as http://localhost:1234/v1.</summary>
    public string? BaseUrl { get; init; }

    public IReadOnlyList<AllowedModel> Models { get; init; } = [];

    public string? DefaultModel { get; init; }

    /// <summary>
    /// Offer every model the provider's catalog lists, not only the allowed ones (m3-architecture.md
    /// §4.3). Off by default: a member could then start a task on any model, at any price.
    /// </summary>
    public bool AllowEveryModel { get; init; }

    public AllowedModel? Model(string? id) => Models.FirstOrDefault(m => m.Id == id);
}

/// <summary>
/// A provider as one person is offered it: the allowed models its provider still lists, then,
/// when every model is allowed, the rest of its catalog; and the model a thread gets by default.
/// </summary>
public sealed record OfferedProvider(ProviderEntry Entry, IReadOnlyList<AllowedModel> Models, string DefaultModel)
{
    public string Name => Entry.Name;

    public AllowedModel? Model(string? id) => Models.FirstOrDefault(m => m.Id == id);
}

/// <summary>
/// The Providers tab (blueprint §8.4, m3-architecture.md §4): which providers are on, the models
/// members may choose on each, and which a new thread gets when its creator chooses none. An
/// empty section offers nothing; the first start seeds it with M2's OpenRouter model.
/// </summary>
public sealed record ProviderSettings
{
    public IReadOnlyList<ProviderEntry> Providers { get; init; } = [];

    /// <summary>The provider a new thread gets when its creator chooses none.</summary>
    public string? DefaultProvider { get; init; }

    /// <summary>Members are offered only providers whose budgets are strict; Admins see them all.</summary>
    public bool StrictOnly { get; init; } = true;

    public const int MinContextLength = 4_096;
    public const int MaxContextLength = 10_000_000;
    public const int MaxModelIdLength = 200;

    public ProviderEntry? Entry(string? name) => Providers.FirstOrDefault(p => p.Name == name);

    /// <summary>What is wrong with these settings, one sentence each; empty when they are usable.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        foreach (var duplicate in Providers.GroupBy(p => p.Name).Where(g => g.Count() > 1))
            errors.Add($"\"{duplicate.Key}\" is listed more than once.");

        foreach (var entry in Providers)
        {
            if (KnownProviders.Find(entry.Name) is not { } kind)
            {
                errors.Add($"\"{entry.Name}\" is not a provider the factory knows.");
                continue;
            }

            var name = kind.DisplayName;
            foreach (var model in entry.Models)
            {
                if (string.IsNullOrWhiteSpace(model.Id) || model.Id != model.Id.Trim() || model.Id.Length > MaxModelIdLength)
                    errors.Add($"{name}: a model id must be 1 to {MaxModelIdLength} characters, with no spaces around it.");
                if (model.ContextLength is < MinContextLength or > MaxContextLength)
                    errors.Add($"{name}: {model.Id}'s context length must be between {Tokens(MinContextLength)} and {Tokens(MaxContextLength)} tokens.");
            }

            foreach (var duplicate in entry.Models.GroupBy(m => m.Id).Where(g => g.Count() > 1))
                errors.Add($"{name}: {duplicate.Key} is allowed more than once.");
            if (entry.DefaultModel is { } byDefault && entry.Model(byDefault) is null)
                errors.Add($"{name}: the default model {byDefault} is not one of its allowed models.");
            if (entry.Models.Count > 0 && entry.DefaultModel is null)
                errors.Add($"{name}: choose which of its models is the default.");
            if (entry.Enabled && entry.Models.Count == 0)
                errors.Add($"{name}: an enabled provider needs at least one allowed model.");
            if (kind.UsesBaseUrl && entry.Enabled && !IsHttpUrl(entry.BaseUrl))
                errors.Add($"{name}: its address must be an http or https URL.");
            if (!kind.UsesBaseUrl && entry.BaseUrl is not null)
                errors.Add($"{name} is not reached at an address of its own.");
        }

        if (DefaultProvider is not null && Entry(DefaultProvider) is not { Enabled: true })
            errors.Add("The default provider must be one that is enabled.");
        return errors;
    }

    /// <summary>
    /// What one person may choose for a new thread: the enabled providers that can be called
    /// (<paramref name="usable"/>: their key, or their address, is set), and for a Member only
    /// strict ones when StrictOnly is on. A model its provider no longer lists is not offered, and
    /// a default on one falls back to the first allowed model still listed; a provider left with
    /// no model is not offered at all.
    /// </summary>
    public IReadOnlyList<OfferedProvider> Offered(bool isAdmin, Func<ProviderEntry, bool> usable, ModelCatalog? catalog = null)
    {
        catalog ??= ModelCatalog.Empty;
        var offered = new List<OfferedProvider>();
        foreach (var entry in Providers)
        {
            if (!entry.Enabled || !usable(entry)
                || (!isAdmin && StrictOnly && KnownProviders.PrecisionOf(entry.Name) != BudgetPrecision.Strict))
                continue;

            var models = entry.Models.Where(m => !catalog.IsRetired(entry.Name, m.Id)).ToList();
            if (entry.AllowEveryModel)
                models.AddRange(catalog.Every(entry.Name)
                    .Where(m => models.All(allowed => ModelCatalog.Normalize(entry.Name, allowed.Id) != m.Id)));
            if (models.Count == 0)
                continue;

            var byDefault = models.FirstOrDefault(m => m.Id == entry.DefaultModel) ?? models[0];
            offered.Add(new OfferedProvider(entry, models, byDefault.Id));
        }

        return offered;
    }

    /// <summary>
    /// The provider and model a new thread gets: what its creator chose, checked against what they
    /// are offered, or the defaults. Null with a reason when the choice is not offered, or nothing is.
    /// </summary>
    public (ProviderEntry Provider, AllowedModel Model)? Choose(
        string? provider, string? model, bool isAdmin, Func<ProviderEntry, bool> usable, out string? refusal, ModelCatalog? catalog = null)
    {
        refusal = null;
        var offered = Offered(isAdmin, usable, catalog);
        if (offered.Count == 0)
        {
            refusal = "No model provider is ready. An Admin enables one, and sets its key, under Settings.";
            return null;
        }

        var entry = provider is null
            ? offered.FirstOrDefault(p => p.Name == DefaultProvider) ?? offered[0]
            : offered.FirstOrDefault(p => p.Name == provider);
        if (entry is null)
        {
            refusal = $"\"{provider}\" is not a provider you can choose. Choose one of: {string.Join(", ", offered.Select(p => p.Name))}.";
            return null;
        }

        var chosen = entry.Model(model ?? entry.DefaultModel);
        if (chosen is null)
        {
            var name = KnownProviders.Find(entry.Name)!.DisplayName;
            refusal = model is not null && catalog?.IsRetired(entry.Name, model) == true && entry.Entry.Model(model) is not null
                ? $"\"{model}\" is no longer offered by {name}. Choose another model."
                : $"\"{model}\" is not a model you can choose on {name}.";
            return null;
        }

        return (entry.Entry, chosen);
    }

    private static bool IsHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    private static string Tokens(long count) => count.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
}
