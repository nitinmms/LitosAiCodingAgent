using Litos.SoftwareFactory.Core.Settings;
using Litos.SoftwareFactory.Core.Store;

namespace Litos.SoftwareFactory.Core.Tests.Settings;

/// <summary>The model catalog: retired models, "allow every model", and what it does to the offer (m3-architecture.md §4.3).</summary>
public class ModelCatalogTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 10, 9, 0, 0, TimeSpan.Zero);

    private static ModelCatalogEntry Listed(string provider, string id, int? contextLength = 128_000, bool? tools = null) =>
        new() { Provider = provider, ModelId = id, ContextLength = contextLength, SupportsTools = tools };

    private static ModelCatalogFetch Fetched(string provider) => new() { Provider = provider, AttemptedAt = T0, FetchedAt = T0 };

    private static ModelCatalogFetch NeverWorked(string provider) => new() { Provider = provider, AttemptedAt = T0, Error = "401 (Unauthorized)" };

    private static ModelCatalog Catalog(params (string Provider, string[] Models)[] providers) => new(
        providers.SelectMany(p => p.Models.Select(m => Listed(p.Provider, m))),
        providers.Select(p => Fetched(p.Provider)));

    private static ProviderEntry Entry(string name, string? byDefault, params string[] models) => new()
    {
        Name = name,
        Enabled = true,
        Models = [.. models.Select(m => new AllowedModel(m, 128_000))],
        DefaultModel = byDefault,
        BaseUrl = name == "local" ? "http://localhost:1234/v1" : null,
    };

    private static bool AllUsable(ProviderEntry _) => true;

    // ---- Retired models ----

    [Fact]
    public void AnAllowedModelTheProviderNoLongerLists_IsRetired()
    {
        var catalog = Catalog(("openai", ["gpt-6-luna"]));

        Assert.False(catalog.IsRetired("openai", "gpt-6-luna"));
        Assert.True(catalog.IsRetired("openai", "gpt-5"));
    }

    [Fact]
    public void AProviderNeverFetched_OrWhoseFetchNeverWorked_RetiresNothing()
    {
        var catalog = new ModelCatalog([], [NeverWorked("openai")]);

        Assert.False(catalog.IsRetired("openai", "gpt-5"));
        Assert.False(catalog.IsRetired("anthropic", "claude-sonnet-5"));
        Assert.False(ModelCatalog.Empty.IsRetired("openai", "gpt-5"));
    }

    [Fact]
    public void ALocalServer_NeverRetiresAModel_SinceItListsOnlyWhatItHasLoaded()
    {
        Assert.False(Catalog(("local", ["qwen3"])).IsRetired("local", "llama-4"));
    }

    [Theory]
    [InlineData("gemini-3-pro")]
    [InlineData("models/gemini-3-pro")]
    public void AGeminiModel_IsTheSame_WithOrWithoutItsPrefix(string allowed)
    {
        var catalog = Catalog(("gemini", ["gemini-3-pro"]));

        Assert.False(catalog.IsRetired("gemini", allowed));
        Assert.Equal("gemini-3-pro", ModelCatalog.Normalize("gemini", "models/gemini-3-pro"));
        Assert.Equal("models/x", ModelCatalog.Normalize("openai", "models/x")); // only Gemini's
    }

    [Fact]
    public void With_ReplacesOneProvider_AndKeepsTheOthers()
    {
        var catalog = Catalog(("openai", ["gpt-5"]), ("anthropic", ["claude-sonnet-5"]));

        var replaced = catalog.With(new ProviderCatalog(Fetched("openai"), [Listed("openai", "gpt-6-luna")]));

        Assert.Equal(["gpt-6-luna"], replaced.Of("openai")!.Models.Select(m => m.ModelId));
        Assert.Equal(["claude-sonnet-5"], replaced.Of("anthropic")!.Models.Select(m => m.ModelId));
        Assert.Equal(["gpt-5"], catalog.Of("openai")!.Models.Select(m => m.ModelId)); // the original is unchanged
    }

    // ---- Every model ----

    [Fact]
    public void EveryModel_LeavesOutThoseThatCannotTakeTools_OrHaveNoUsableContextLength()
    {
        var catalog = new ModelCatalog(
        [
            Listed("openrouter", "anthropic/claude-sonnet-5", 200_000, tools: true),
            Listed("openrouter", "some/chat-only", 32_000, tools: false),
            Listed("openrouter", "some/unreported", 64_000, tools: null),
            Listed("openrouter", "some/unknown-window", null, tools: true),
            Listed("openrouter", "some/tiny", 2_048, tools: true),
        ], [Fetched("openrouter")]);

        Assert.Equal(
            [new AllowedModel("anthropic/claude-sonnet-5", 200_000), new AllowedModel("some/unreported", 64_000)],
            catalog.Every("openrouter"));
        Assert.Empty(catalog.Every("openai"));
    }

    // ---- What a person is offered ----

    [Fact]
    public void ARetiredModel_IsNotOffered_AndADefaultOnOne_FallsBackToTheFirstStillListed()
    {
        var settings = new ProviderSettings { Providers = [Entry("openai", "gpt-5", "gpt-5", "gpt-6-luna", "gpt-6-sol")] };
        var catalog = Catalog(("openai", ["gpt-6-luna", "gpt-6-sol"]));

        var openai = Assert.Single(settings.Offered(isAdmin: false, AllUsable, catalog));

        Assert.Equal(["gpt-6-luna", "gpt-6-sol"], openai.Models.Select(m => m.Id));
        Assert.Equal("gpt-6-luna", openai.DefaultModel);
        Assert.Equal("gpt-6-luna", settings.Choose(null, null, false, AllUsable, out _, catalog)!.Value.Model.Id);
    }

    [Fact]
    public void AProviderWhoseModelsAreAllRetired_IsNotOffered()
    {
        var settings = new ProviderSettings { Providers = [Entry("openai", "gpt-5", "gpt-5"), Entry("anthropic", "claude-sonnet-5", "claude-sonnet-5")] };

        var offered = settings.Offered(isAdmin: true, AllUsable, Catalog(("openai", ["gpt-6-luna"])));

        Assert.Equal(["anthropic"], offered.Select(p => p.Name));
    }

    [Fact]
    public void ChoosingARetiredModel_IsRefused_SayingTheProviderNoLongerOffersIt()
    {
        var settings = new ProviderSettings { Providers = [Entry("openai", "gpt-6-luna", "gpt-5", "gpt-6-luna")] };
        var catalog = Catalog(("openai", ["gpt-6-luna"]));

        Assert.Null(settings.Choose("openai", "gpt-5", isAdmin: true, AllUsable, out var refusal, catalog));
        Assert.Equal("\"gpt-5\" is no longer offered by OpenAI. Choose another model.", refusal);

        Assert.Null(settings.Choose("openai", "gpt-4", isAdmin: true, AllUsable, out refusal, catalog));
        Assert.Equal("\"gpt-4\" is not a model you can choose on OpenAI.", refusal);
    }

    [Fact]
    public void AllowingEveryModel_OffersTheCatalogAfterTheAllowedModels_EachOnce()
    {
        var settings = new ProviderSettings
        {
            Providers = [Entry("openrouter", "qwen/qwen3-coder", "qwen/qwen3-coder") with { AllowEveryModel = true }],
        };
        var catalog = new ModelCatalog(
            [Listed("openrouter", "anthropic/claude-sonnet-5", 200_000, tools: true), Listed("openrouter", "qwen/qwen3-coder", 262_144, tools: true)],
            [Fetched("openrouter")]);

        var openrouter = Assert.Single(settings.Offered(isAdmin: false, AllUsable, catalog));

        // The allowed model keeps the context length the Admin gave it.
        Assert.Equal([new AllowedModel("qwen/qwen3-coder", 128_000), new AllowedModel("anthropic/claude-sonnet-5", 200_000)], openrouter.Models);
        var chosen = settings.Choose("openrouter", "anthropic/claude-sonnet-5", false, AllUsable, out _, catalog);
        Assert.Equal(200_000, chosen!.Value.Model.ContextLength);
    }

    [Fact]
    public void AGeminiModelAllowedWithItsPrefix_IsNotOfferedAgain_ByEveryModel()
    {
        var settings = new ProviderSettings
        {
            Providers = [Entry("gemini", "models/gemini-3-pro", "models/gemini-3-pro") with { AllowEveryModel = true }],
        };
        var catalog = new ModelCatalog([Listed("gemini", "gemini-3-pro", 1_048_576), Listed("gemini", "gemini-3-flash", 1_048_576)], [Fetched("gemini")]);

        var gemini = Assert.Single(settings.Offered(isAdmin: false, AllUsable, catalog));

        Assert.Equal(["models/gemini-3-pro", "gemini-3-flash"], gemini.Models.Select(m => m.Id));
    }

    [Fact]
    public void WithoutAllowEveryModel_ACatalogModel_IsNotOffered()
    {
        var settings = new ProviderSettings { Providers = [Entry("openrouter", "qwen/qwen3-coder", "qwen/qwen3-coder")] };
        var catalog = new ModelCatalog(
            [Listed("openrouter", "anthropic/claude-sonnet-5", tools: true), Listed("openrouter", "qwen/qwen3-coder", tools: true)],
            [Fetched("openrouter")]);

        Assert.Null(settings.Choose("openrouter", "anthropic/claude-sonnet-5", false, AllUsable, out var refusal, catalog));
        Assert.Contains("is not a model you can choose on OpenRouter", refusal);
    }

    [Fact]
    public void WithNoCatalog_TheOfferIsTheAllowedModels_AsBefore()
    {
        var settings = new ProviderSettings { Providers = [Entry("openai", "gpt-5", "gpt-5", "gpt-6-luna") with { AllowEveryModel = true }] };

        var openai = Assert.Single(settings.Offered(isAdmin: false, AllUsable));

        Assert.Equal(["gpt-5", "gpt-6-luna"], openai.Models.Select(m => m.Id));
        Assert.Equal("gpt-5", openai.DefaultModel);
    }
}
