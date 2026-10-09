using Litos.SoftwareFactory.Core.Settings;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Host.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>The providers the gateway calls, built from settings and keys (m3-architecture.md §4.1).</summary>
public sealed class FactoryProvidersTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private FactorySettings _settings = null!;
    private FactoryProviders _providers = null!;

    public async Task InitializeAsync()
    {
        _host = await TestHost.StartAsync(startCoordinator: false);
        _settings = _host.App.Services.GetRequiredService<FactorySettings>();
        // TestHost scripts the model; these build the real providers, which make no call until used.
        _providers = new FactoryProviders(_settings);
    }

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    private Task SaveProvidersAsync(ProviderSettings providers) =>
        _settings.SaveAsync(SettingsSections.Providers, providers, _settings.RevisionOf(SettingsSections.Providers), null, default);

    [Fact]
    public void TheSeededProvider_IsTheEnginesOwn_BuiltWithItsStoredKey()
    {
        Assert.Equal("openrouter", _providers.Resolve("openrouter").ProviderName);
    }

    [Fact]
    public void AProvider_IsBuiltOnce()
    {
        Assert.Same(_providers.Resolve("openrouter"), _providers.Resolve("openrouter"));
    }

    [Fact]
    public async Task ANewKey_BuildsTheProviderAgain()
    {
        var first = _providers.Resolve("openrouter");

        await _settings.SetSecretAsync(SecretNames.Provider("openrouter"), "sk-or-replaced", null, default);

        Assert.NotSame(first, _providers.Resolve("openrouter"));
    }

    [Fact]
    public async Task AProviderWithNoKey_IsRefused_SayingWhatToSet()
    {
        await SaveProvidersAsync(new ProviderSettings
        {
            Providers = [new ProviderEntry { Name = "anthropic", Enabled = true, Models = [new AllowedModel("claude-sonnet-5", 200_000)], DefaultModel = "claude-sonnet-5" }],
        });

        var refused = Assert.Throws<InvalidOperationException>(() => _providers.Resolve("anthropic"));
        Assert.Contains("Anthropic has no key", refused.Message);
    }

    [Fact]
    public async Task AKeyedProvider_ThatIsNoLongerEnabled_StillServesTheTasksOnIt()
    {
        await _settings.SetSecretAsync(SecretNames.Provider("gemini"), "gemini-key", null, default);
        await SaveProvidersAsync(new ProviderSettings { Providers = [new ProviderEntry { Name = "gemini", Enabled = false }] });

        Assert.Equal("gemini", _providers.Resolve("gemini").ProviderName);
    }

    [Fact]
    public async Task ALocalServer_NeedsItsAddress_NotAKey()
    {
        Assert.Contains("Local server has no address", Assert.Throws<InvalidOperationException>(() => _providers.Resolve("local")).Message);

        await SaveProvidersAsync(new ProviderSettings
        {
            Providers = [new ProviderEntry { Name = "local", Enabled = true, BaseUrl = "http://localhost:1234/v1", Models = [new AllowedModel("qwen3", 32_768)], DefaultModel = "qwen3" }],
        });

        Assert.Equal("local", _providers.Resolve("local").ProviderName);
    }

    [Fact]
    public void AProviderTheFactoryDoesNotKnow_IsRefused()
    {
        Assert.Throws<InvalidOperationException>(() => _providers.Resolve("bedrock"));
    }
}
