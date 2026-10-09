using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Litos.SoftwareFactory.Core.Settings;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Host.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>A thread's provider and model, chosen from what the settings allow (m3-architecture.md §4.4).</summary>
public sealed class ThreadProviderTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private FactorySettings _settings = null!;

    public async Task InitializeAsync()
    {
        _host = await TestHost.StartAsync(startCoordinator: false);
        _settings = _host.App.Services.GetRequiredService<FactorySettings>();
    }

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    private static ProviderEntry Entry(string name, string model, int contextLength) => new()
    {
        Name = name,
        Enabled = true,
        Models = [new AllowedModel(model, contextLength)],
        DefaultModel = model,
        BaseUrl = name == "local" ? "http://localhost:1234/v1" : null,
    };

    /// <summary>OpenRouter as seeded, Anthropic with a key, and MeshApi (estimated) with a key.</summary>
    private async Task SeveralProvidersAsync()
    {
        await _settings.SetSecretAsync(SecretNames.Provider("anthropic"), "sk-ant", null, default);
        await _settings.SetSecretAsync(SecretNames.Provider("mesh_api"), "mesh-key", null, default);
        await _settings.SaveAsync(SettingsSections.Providers, _settings.Providers with
        {
            Providers =
            [
                .. _settings.Providers.Providers,
                Entry("anthropic", "claude-sonnet-5", 200_000),
                Entry("mesh_api", "mesh-large", 64_000),
            ],
        }, _settings.RevisionOf(SettingsSections.Providers), null, default);
    }

    private async Task<HttpResponseMessage> CreateAsync(Guid projectId, object? provider = null, object? model = null, HttpClient? client = null) =>
        await (client ?? _host.Client).PostAsJsonAsync("api/threads", new { projectId, title = "Add CSV export", typeLabel = "feature", provider, model });

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task NoChoice_GetsTheDefaultModel_AndItsContextLength()
    {
        var threadId = await _host.CreateThreadAsync(await _host.RegisterProjectAsync());

        var thread = (await _host.ThreadAsync(threadId)).Thread;
        Assert.Equal(("openrouter", _host.Options.Model, (int?)_host.Options.ContextLength), (thread.Provider, thread.Model, thread.ContextLength));
    }

    [Fact]
    public async Task AnAllowedProviderAndModel_AreTheThreads_WithTheirContextLength()
    {
        await SeveralProvidersAsync();

        using var created = await CreateAsync(await _host.RegisterProjectAsync(), "anthropic", "claude-sonnet-5");

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var view = await BodyAsync(created);
        Assert.Equal(("anthropic", "claude-sonnet-5", "strict"),
            (view.GetProperty("provider").GetString(), view.GetProperty("model").GetString(), view.GetProperty("budgetPrecision").GetString()));
        Assert.Equal(200_000, (await _host.ThreadAsync(view.GetProperty("id").GetGuid())).Thread.ContextLength);
    }

    [Theory]
    [InlineData("anthropic", "claude-opus-5", "\"claude-opus-5\" is not a model you can choose on Anthropic.")]
    [InlineData("bedrock", null, "\"bedrock\" is not a provider you can choose.")]
    [InlineData("gemini", null, "\"gemini\" is not a provider you can choose.")]
    public async Task AChoiceThatIsNotAllowed_Is400_InWords(string provider, string? model, string reason)
    {
        await SeveralProvidersAsync();

        using var refused = await CreateAsync(await _host.RegisterProjectAsync(), provider, model);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains(reason, (await BodyAsync(refused)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task UnderStrictOnly_AMemberCannotChooseAnEstimatedProvider_ButAnAdminCan()
    {
        await SeveralProvidersAsync();
        var projectId = await _host.RegisterProjectAsync();
        await _host.CreateMemberAsync("ben", projectId);
        using var ben = await _host.SignedInAsync("ben");

        using var member = await CreateAsync(projectId, "mesh_api", client: ben);
        using var admin = await CreateAsync(projectId, "mesh_api");

        Assert.Equal(HttpStatusCode.BadRequest, member.StatusCode);
        Assert.Equal(HttpStatusCode.Created, admin.StatusCode);
        Assert.Equal("estimated", (await BodyAsync(admin)).GetProperty("budgetPrecision").GetString());
    }

    [Fact]
    public async Task TheSettings_ListWhatEachPersonMayChoose()
    {
        await SeveralProvidersAsync();
        var projectId = await _host.RegisterProjectAsync();
        await _host.CreateMemberAsync("ben", projectId);
        using var ben = await _host.SignedInAsync("ben");

        var forAdmin = await _host.GetAsync("api/settings");
        var forMember = await ben.GetFromJsonAsync<JsonElement>("api/settings");

        Assert.Equal(["openrouter", "anthropic", "mesh_api"], forAdmin.GetProperty("providers").EnumerateArray().Select(p => p.GetProperty("name").GetString()));
        Assert.Equal(["openrouter", "anthropic"], forMember.GetProperty("providers").EnumerateArray().Select(p => p.GetProperty("name").GetString()));
        var anthropic = forMember.GetProperty("providers")[1];
        Assert.Equal(("Anthropic", "strict", "claude-sonnet-5"),
            (anthropic.GetProperty("displayName").GetString(), anthropic.GetProperty("budgetPrecision").GetString(), anthropic.GetProperty("defaultModel").GetString()));
        Assert.Equal(200_000, anthropic.GetProperty("models")[0].GetProperty("contextLength").GetInt32());
        Assert.Equal(("openrouter", _host.Options.Model), (forMember.GetProperty("provider").GetString(), forMember.GetProperty("model").GetString()));
    }

    /// <summary>A factory with no usable provider starts, and says what to set (m3-architecture.md §3.3).</summary>
    [Fact]
    public async Task WithNoProviderReady_ANewThreadIsRefused_SayingWhatAnAdminMustDo()
    {
        await _settings.ClearSecretAsync(SecretNames.Provider("openrouter"), await _host.AdminIdAsync(), default);

        using var refused = await CreateAsync(await _host.RegisterProjectAsync());

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("No model provider is ready", (await BodyAsync(refused)).GetProperty("error").GetString());
        var settings = await _host.GetAsync("api/settings");
        Assert.Equal(JsonValueKind.Null, settings.GetProperty("provider").ValueKind);
        Assert.Empty(settings.GetProperty("providers").EnumerateArray());
    }

    [Fact]
    public async Task AWorker_IsLaunchedWithItsThreadsModel_AndContextLength()
    {
        // This class's host claims no work; a host whose coordinator runs launches the worker.
        await _host.DisposeAsync();
        _host = await TestHost.StartAsync();
        _settings = _host.App.Services.GetRequiredService<FactorySettings>();
        await SeveralProvidersAsync();
        using var created = await CreateAsync(await _host.RegisterProjectAsync(), "anthropic");

        await _host.DelegateAsync((await BodyAsync(created)).GetProperty("id").GetGuid());

        var launch = await WaitForLaunchAsync();
        Assert.Equal(("anthropic", "claude-sonnet-5", (int?)200_000), (launch.Provider, launch.Model, launch.ContextLength));
    }

    [Fact]
    public void AThreadFromBeforeM3_IsGivenTheFactorysOldContextLength()
    {
        var options = new FactoryOptions { ContextLength = 131_072 };
        TaskThread Thread(int? contextLength) => new() { Provider = "openrouter", Model = "m", SessionId = "s", Title = "t", TypeLabel = "feature", ContextLength = contextLength };

        Assert.Equal(131_072, options.ContextLengthOf(Thread(null)));
        Assert.Equal(200_000, options.ContextLengthOf(Thread(200_000)));
    }

    private async Task<Core.Ports.WorkerLaunch> WaitForLaunchAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (_host.Workers.Workers.IsEmpty)
        {
            Assert.True(DateTime.UtcNow < deadline, "No worker was launched.");
            await Task.Delay(25);
        }

        return _host.Workers.Workers.First().Launch;
    }
}
