using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Litos.Agent.Providers;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Settings;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Host.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>Model lists a test hands out, or a failure, per provider.</summary>
public sealed class FakeModelCatalogSource : IModelCatalogSource
{
    public Dictionary<string, Func<IReadOnlyList<ModelCatalogEntry>>> Lists { get; } = [];

    public List<string> Asked { get; } = [];

    public void Set(string provider, params (string Id, int? ContextLength, bool? Tools)[] models) =>
        Lists[provider] = () => [.. models.Select(m => new ModelCatalogEntry { Provider = provider, ModelId = m.Id, ContextLength = m.ContextLength, SupportsTools = m.Tools })];

    public void Fail(string provider, string message) => Lists[provider] = () => throw new HttpRequestException(message);

    public Task<IReadOnlyList<ModelCatalogEntry>> ListAsync(string provider, CancellationToken ct)
    {
        Asked.Add(provider);
        return Task.FromResult(Lists.TryGetValue(provider, out var list) ? list() : []);
    }
}

/// <summary>The model catalog and what it changes about the offer (m3-architecture.md §4.3, §4.4).</summary>
public sealed class ModelCatalogApiTests : IAsyncLifetime
{
    private readonly FakeModelCatalogSource _source = new();
    private TestHost _host = null!;
    private FactorySettings _settings = null!;

    public async Task InitializeAsync()
    {
        _host = await TestHost.StartAsync(startCoordinator: false, services: s => s.AddSingleton<IModelCatalogSource>(_source));
        _settings = _host.App.Services.GetRequiredService<FactorySettings>();
    }

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    private async Task<JsonElement> RefreshAsync(string provider, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await _host.Client.PostAsync($"api/admin/models/catalog/{provider}/refresh", null);
        Assert.Equal(expected, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<JsonElement> CatalogOfAsync(string provider) =>
        (await _host.GetAsync("api/admin/models/catalog")).GetProperty("providers").EnumerateArray().Single(p => p.GetProperty("name").GetString() == provider);

    /// <summary>OpenAI with a key, offering two models with gpt-5 the default.</summary>
    private async Task OpenAiAsync(bool allowEveryModel = false)
    {
        await _settings.SetSecretAsync(SecretNames.Provider("openai"), "sk-openai", null, default);
        await _settings.SaveAsync(SettingsSections.Providers, _settings.Providers with
        {
            Providers =
            [
                .. _settings.Providers.Providers,
                new ProviderEntry
                {
                    Name = "openai", Enabled = true, DefaultModel = "gpt-5", AllowEveryModel = allowEveryModel,
                    Models = [new AllowedModel("gpt-5", 400_000), new AllowedModel("gpt-6-luna", 1_050_000)],
                },
            ],
            DefaultProvider = "openai",
        }, _settings.RevisionOf(SettingsSections.Providers), null, default);
    }

    private static IEnumerable<string> Ids(JsonElement models) => models.EnumerateArray().Select(m => m.GetProperty("id").GetString()!);

    // ---- Fetching ----

    [Fact]
    public async Task EveryProvider_IsListed_NoneFetchedYet()
    {
        var providers = (await _host.GetAsync("api/admin/models/catalog")).GetProperty("providers").EnumerateArray().ToList();

        Assert.Equal(KnownProviders.All.Select(p => p.Name), providers.Select(p => p.GetProperty("name").GetString()));
        Assert.All(providers, p =>
        {
            Assert.Equal(JsonValueKind.Null, p.GetProperty("fetchedAt").ValueKind);
            Assert.Empty(p.GetProperty("models").EnumerateArray());
        });
    }

    [Fact]
    public async Task ARefresh_FetchesTheProvidersList_StoresIt_AndReturnsIt()
    {
        _source.Set("openrouter", ("anthropic/claude-sonnet-5", 200_000, true), ("some/chat-only", 32_000, false));

        var refreshed = await RefreshAsync("openrouter");

        Assert.Equal(["anthropic/claude-sonnet-5", "some/chat-only"], Ids(refreshed.GetProperty("models")));
        Assert.NotEqual(JsonValueKind.Null, refreshed.GetProperty("fetchedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, refreshed.GetProperty("error").ValueKind);
        var sonnet = refreshed.GetProperty("models")[0];
        Assert.Equal((200_000, true), (sonnet.GetProperty("contextLength").GetInt32(), sonnet.GetProperty("supportsTools").GetBoolean()));

        Assert.Equal(["anthropic/claude-sonnet-5", "some/chat-only"], Ids((await CatalogOfAsync("openrouter")).GetProperty("models")));

        // Stored: a host that starts afterwards loads it.
        var restarted = new ModelCatalogService(_host.Store, _source, new SystemClock(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ModelCatalogService>.Instance);
        await restarted.LoadAsync(default);
        Assert.Equal(["anthropic/claude-sonnet-5", "some/chat-only"], restarted.Current.Of("openrouter")!.Models.Select(m => m.ModelId));
    }

    [Fact]
    public async Task AGeminiModel_IsStoredWithoutItsPrefix()
    {
        _source.Set("gemini", ("models/gemini-3-pro", 1_048_576, null));

        var refreshed = await RefreshAsync("gemini");

        Assert.Equal(["gemini-3-pro"], Ids(refreshed.GetProperty("models")));
    }

    [Fact]
    public async Task AFailedRefresh_SaysWhy_AndKeepsTheLastList()
    {
        _source.Set("openai", ("gpt-6-luna", 1_050_000, null));
        await RefreshAsync("openai");
        _source.Fail("openai", "Response status code does not indicate success: 401 (Unauthorized).");

        var refreshed = await RefreshAsync("openai");

        Assert.Contains("401 (Unauthorized)", refreshed.GetProperty("error").GetString());
        Assert.Equal(["gpt-6-luna"], Ids(refreshed.GetProperty("models")));
        Assert.NotEqual(JsonValueKind.Null, refreshed.GetProperty("fetchedAt").ValueKind);
    }

    [Fact]
    public async Task ARefreshOfAProviderWithNoKey_SaysSo()
    {
        // The real source asks the provider factory, which refuses a provider with no key.
        var real = new ProviderModelCatalogSource(new FactoryProviders(_settings), new HttpClient());

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => real.ListAsync("anthropic", default));

        Assert.Equal("Anthropic has no key. An Admin sets it under Settings, Providers.", failure.Message);
    }

    [Fact]
    public async Task AnUnknownProvider_IsNotFound()
    {
        var refused = await RefreshAsync("bedrock", HttpStatusCode.NotFound);

        Assert.Contains("not a provider the factory knows", refused.GetProperty("error").GetString());
        Assert.Empty(_source.Asked);
    }

    [Fact]
    public async Task OnlyAnAdmin_SeesOrRefreshesTheCatalog()
    {
        await _host.CreateMemberAsync("maria");
        var member = await _host.SignedInAsync("maria");

        using var listed = await member.GetAsync("api/admin/models/catalog");
        using var refreshed = await member.PostAsync("api/admin/models/catalog/openrouter/refresh", null);

        Assert.Equal(HttpStatusCode.Forbidden, listed.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, refreshed.StatusCode);
        Assert.Empty(_source.Asked);
    }

    // ---- Retired models ----

    [Fact]
    public async Task AnAllowedModelTheProviderNoLongerLists_IsShownRetired_AndNotOffered()
    {
        await OpenAiAsync();
        _source.Set("openai", ("gpt-6-luna", 1_050_000, null));

        var refreshed = await RefreshAsync("openai");

        Assert.Equal(["gpt-5"], refreshed.GetProperty("retired").EnumerateArray().Select(m => m.GetString()));
        var settings = await _host.GetAsync("api/settings");
        var openai = settings.GetProperty("providers").EnumerateArray().Single(p => p.GetProperty("name").GetString() == "openai");
        Assert.Equal(["gpt-6-luna"], Ids(openai.GetProperty("models")));
        // The default was gpt-5: it falls back to the first allowed model still listed.
        Assert.Equal("gpt-6-luna", openai.GetProperty("defaultModel").GetString());
        Assert.Equal(("openai", "gpt-6-luna"), (settings.GetProperty("provider").GetString(), settings.GetProperty("model").GetString()));
    }

    [Fact]
    public async Task ANewThreadOnARetiredModel_IsRefused_AndOneByDefaultGetsTheFallback()
    {
        await OpenAiAsync();
        _source.Set("openai", ("gpt-6-luna", 1_050_000, null));
        await RefreshAsync("openai");
        var projectId = await _host.RegisterProjectAsync();

        using var refused = await _host.Client.PostAsJsonAsync("api/threads", new { projectId, title = "Export", provider = "openai", model = "gpt-5" });
        var byDefault = (await _host.ThreadAsync(await _host.CreateThreadAsync(projectId))).Thread;

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("\"gpt-5\" is no longer offered by OpenAI", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Equal(("openai", "gpt-6-luna", (int?)1_050_000), (byDefault.Provider, byDefault.Model, byDefault.ContextLength));
    }

    // ---- Every model ----

    [Fact]
    public async Task WithEveryModelAllowed_AThreadCanRunOnAnyToolCapableCatalogModel_WithItsContextLength()
    {
        await OpenAiAsync(allowEveryModel: true);
        _source.Set("openai", ("gpt-5", 400_000, null), ("gpt-6-luna", 1_050_000, null), ("gpt-6-sol", 1_050_000, null));
        await RefreshAsync("openai");

        using var created = await _host.Client.PostAsJsonAsync("api/threads",
            new { projectId = await _host.RegisterProjectAsync(), title = "Export", provider = "openai", model = "gpt-6-sol" });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var thread = (await _host.ThreadAsync((await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid())).Thread;
        Assert.Equal(("gpt-6-sol", (int?)1_050_000), (thread.Model, thread.ContextLength));
    }

    // ---- Recent models ----

    [Fact]
    public async Task RecentModels_AreThePersonsNewestThreads_StillOffered()
    {
        await OpenAiAsync();
        var projectId = await _host.RegisterProjectAsync();
        foreach (var (provider, model) in new[] { ("openrouter", _host.Options.Model), ("openai", "gpt-5"), ("openai", "gpt-6-luna"), ("openai", "gpt-5") })
        {
            using var created = await _host.Client.PostAsJsonAsync("api/threads", new { projectId, title = "t", provider, model });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        var recent = (await _host.GetAsync("api/settings")).GetProperty("recentModels").EnumerateArray()
            .Select(r => $"{r.GetProperty("provider").GetString()}/{r.GetProperty("model").GetString()}").ToList();
        Assert.Equal(["openai/gpt-5", "openai/gpt-6-luna", $"openrouter/{_host.Options.Model}"], recent);

        // gpt-5 is retired: it is no longer pinned.
        _source.Set("openai", ("gpt-6-luna", 1_050_000, null));
        await RefreshAsync("openai");
        recent = [.. (await _host.GetAsync("api/settings")).GetProperty("recentModels").EnumerateArray().Select(r => r.GetProperty("model").GetString()!)];
        Assert.Equal(["gpt-6-luna", _host.Options.Model], recent);
    }
}

/// <summary>OpenRouter's /models, read by the factory itself for tool support and price.</summary>
public sealed class OpenRouterCatalogSourceTests
{
    private sealed class Canned(string json) : HttpMessageHandler
    {
        public Uri? Asked { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Asked = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task OpenRoutersList_GivesToolSupport_AndPricePerMillionTokens()
    {
        var handler = new Canned("""
            {"data":[
              {"id":"anthropic/claude-sonnet-5","name":"Anthropic: Claude Sonnet 5","context_length":200000,
               "supported_parameters":["tools","temperature"],"pricing":{"prompt":"0.000003","completion":"0.000015"}},
              {"id":"some/chat-only","name":"Chat only","context_length":32000,"supported_parameters":["temperature"],"pricing":{"prompt":"0","completion":"0"}},
              {"id":"openrouter/auto","context_length":2000000,"pricing":{"prompt":"-1","completion":"-1"}}
            ]}
            """);
        var source = new ProviderModelCatalogSource(new ScriptedProvider(), new HttpClient(handler) { BaseAddress = new Uri("https://openrouter.ai/api/v1/") });

        var models = await source.ListAsync("openrouter", default);

        Assert.Equal("https://openrouter.ai/api/v1/models", handler.Asked!.ToString());
        Assert.Equal(3, models.Count);
        var sonnet = models[0];
        Assert.Equal(("anthropic/claude-sonnet-5", "Anthropic: Claude Sonnet 5", (int?)200_000, (bool?)true, (decimal?)3m, (decimal?)15m),
            (sonnet.ModelId, sonnet.DisplayName, sonnet.ContextLength, sonnet.SupportsTools, sonnet.InputPricePerMillion, sonnet.OutputPricePerMillion));
        Assert.Equal(((bool?)false, (decimal?)0m), (models[1].SupportsTools, models[1].InputPricePerMillion));
        // No parameters reported: not known. A varying price: no price.
        Assert.Equal(((bool?)null, (decimal?)null), (models[2].SupportsTools, models[2].InputPricePerMillion));
    }

    [Fact]
    public async Task AnotherProvidersList_ComesFromTheEnginesProvider()
    {
        var provider = new ListingProvider([new ModelInfo("gpt-6-luna", "gpt-6-luna", false, ContextLength: 1_050_000), new ModelInfo("o5", "OpenAI o5", false)]);
        var source = new ProviderModelCatalogSource(provider, new HttpClient());

        var models = await source.ListAsync("openai", default);

        Assert.Equal(["openai"], provider.Resolved);
        Assert.Equal(("gpt-6-luna", (string?)null, (int?)1_050_000), (models[0].ModelId, models[0].DisplayName, models[0].ContextLength));
        Assert.Equal(("o5", "OpenAI o5", (bool?)null), (models[1].ModelId, models[1].DisplayName, models[1].SupportsTools));
    }

    private sealed class ListingProvider(IReadOnlyList<ModelInfo> models) : IChatProvider, IChatProviderFactory
    {
        public List<string> Resolved { get; } = [];

        public string ProviderName => "openai";

        public IChatProvider Resolve(string providerName)
        {
            Resolved.Add(providerName);
            return this;
        }

        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct) => Task.FromResult(models);

        public IAsyncEnumerable<Litos.Agent.Streaming.AgentEvent> StreamAsync(ChatRequest request, CancellationToken ct) => throw new NotSupportedException();
    }
}
