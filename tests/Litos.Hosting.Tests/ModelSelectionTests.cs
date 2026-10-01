using Litos.Agent.Providers;
using Litos.Host;
using Litos.Hosting.Tests.Fakes;

namespace Litos.Hosting.Tests;

/// <summary>
/// IDisposable to redirect LitosConfig.ConfigFilePath to a scratch file — PersistedModelSelection
/// saves on every switch, and without the redirect these tests would overwrite the developer's
/// real ~/.litos/config.json (see Litos.VsCodeHost.Tests' AgentWorkerTests for the incident).
/// </summary>
public class PersistedModelSelectionTests : IDisposable
{
    private readonly string _originalConfigFilePath = LitosConfig.ConfigFilePath;
    private readonly string _scratchConfigFilePath = Path.Combine(Path.GetTempPath(), $"litos-hosting-test-config-{Guid.NewGuid():n}.json");

    public PersistedModelSelectionTests() => LitosConfig.ConfigFilePath = _scratchConfigFilePath;

    public void Dispose()
    {
        LitosConfig.ConfigFilePath = _originalConfigFilePath;
        if (File.Exists(_scratchConfigFilePath))
            File.Delete(_scratchConfigFilePath);
    }

    private static LitosConfig Config(string defaultProvider = "anthropic", string? defaultModel = "model-a", params string[] keyedProviders) =>
        new(
            DefaultProvider: defaultProvider, DefaultModel: defaultModel, LastWorkingDirectory: null,
            ApiKeys: (keyedProviders.Length == 0 ? ["anthropic"] : keyedProviders).ToDictionary(p => p, _ => "unused"));

    private static (PersistedModelSelection Selection, FakeChatProvider Provider) Create(LitosConfig config)
    {
        var provider = new FakeChatProvider();
        return (new PersistedModelSelection(new FakeChatProviderFactory(provider), config), provider);
    }

    [Fact]
    public void Constructor_DefaultProviderConfigured_StartsFromConfigDefaults()
    {
        var (selection, _) = Create(Config());

        Assert.Equal("anthropic", selection.ProviderName);
        Assert.Equal("model-a", selection.Model);
        Assert.Null(selection.ContextLength);
    }

    [Fact]
    public void Constructor_DefaultProviderNotConfigured_FallsBackToFirstAvailableProvider()
    {
        var (selection, _) = Create(Config(defaultProvider: "openai", keyedProviders: "openrouter"));

        Assert.Equal("openrouter", selection.ProviderName);
    }

    [Fact]
    public void Constructor_NoProviderConfigured_Throws()
    {
        var config = new LitosConfig("anthropic", null, null, new Dictionary<string, string>());

        Assert.Throws<InvalidOperationException>(() => Create(config));
    }

    [Fact]
    public void Model_NoDefaultModel_IsNullUntilResolved()
    {
        var (selection, _) = Create(Config(defaultModel: null));

        Assert.Null(selection.Model);
    }

    [Fact]
    public void AvailableProviders_ListsConfiguredChatProviders()
    {
        var (selection, _) = Create(Config(keyedProviders: ["anthropic", "openrouter"]));

        Assert.Equal(["anthropic", "openrouter"], selection.AvailableProviders);
    }

    [Fact]
    public async Task EnsureResolvedAsync_NoModel_PicksTheProvidersDefaultAndItsContextLength()
    {
        var (selection, provider) = Create(Config(defaultModel: null));
        provider.ModelsToReturn =
        [
            new ModelInfo("first", "First", IsDefault: false, ContextLength: 1_000),
            new ModelInfo("default", "Default", IsDefault: true, ContextLength: 64_000),
        ];

        await selection.EnsureResolvedAsync(CancellationToken.None);

        Assert.Equal("default", selection.Model);
        Assert.Equal(64_000, selection.ContextLength);
    }

    [Fact]
    public async Task EnsureResolvedAsync_ModelSetButContextLengthUnknown_ResolvesOnlyTheContextLength()
    {
        var (selection, provider) = Create(Config(defaultModel: "model-a"));
        provider.ModelsToReturn =
        [
            new ModelInfo("other", "Other", IsDefault: true, ContextLength: 1_000),
            new ModelInfo("model-a", "A", IsDefault: false, ContextLength: 32_000),
        ];

        await selection.EnsureResolvedAsync(CancellationToken.None);

        Assert.Equal("model-a", selection.Model);
        Assert.Equal(32_000, selection.ContextLength);
    }

    [Fact]
    public async Task EnsureResolvedAsync_NoModelAndProviderListsNone_Throws()
    {
        var (selection, provider) = Create(Config(defaultModel: null));
        provider.ModelsToReturn = [];

        await Assert.ThrowsAsync<InvalidOperationException>(() => selection.EnsureResolvedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SwitchProviderAsync_ResetsToTheNewProvidersDefaultModel_AndSavesIt()
    {
        var (selection, provider) = Create(Config(keyedProviders: ["anthropic", "openrouter"]));
        provider.ModelsToReturn = [new ModelInfo("or-default", "Default", IsDefault: true, ContextLength: 200_000)];

        await selection.SwitchProviderAsync("openrouter", CancellationToken.None);

        Assert.Equal(new ModelSelectionSnapshot("openrouter", "or-default", 200_000), selection.Snapshot());
        var saved = File.ReadAllText(_scratchConfigFilePath);
        Assert.Contains("openrouter", saved);
        Assert.Contains("or-default", saved);
    }

    [Fact]
    public async Task SwitchProviderAsync_NoModels_ThrowsAndKeepsTheCurrentSelection()
    {
        var (selection, provider) = Create(Config());
        provider.ModelsToReturn = [];

        await Assert.ThrowsAsync<InvalidOperationException>(() => selection.SwitchProviderAsync("openrouter", CancellationToken.None));

        Assert.Equal("anthropic", selection.ProviderName);
        Assert.False(File.Exists(_scratchConfigFilePath));
    }

    [Fact]
    public void SetModel_KeepsProvider_AndSavesTheModel()
    {
        var (selection, _) = Create(Config());

        selection.SetModel("model-b", 128_000);

        Assert.Equal(new ModelSelectionSnapshot("anthropic", "model-b", 128_000), selection.Snapshot());
        Assert.Contains("model-b", File.ReadAllText(_scratchConfigFilePath));
    }
}

public class FixedModelSelectionTests
{
    [Fact]
    public void Properties_ReflectTheLaunchValues()
    {
        var selection = new FixedModelSelection("openrouter", "vendor/model", 200_000);

        Assert.Equal("openrouter", selection.ProviderName);
        Assert.Equal("vendor/model", selection.Model);
        Assert.Equal(200_000, selection.ContextLength);
        Assert.Equal(["openrouter"], selection.AvailableProviders);
        Assert.Equal(new ModelSelectionSnapshot("openrouter", "vendor/model", 200_000), selection.Snapshot());
    }

    [Fact]
    public async Task EnsureResolvedAsync_DoesNothing()
    {
        var selection = new FixedModelSelection("openrouter", "vendor/model");

        await selection.EnsureResolvedAsync(CancellationToken.None);

        Assert.Null(selection.ContextLength);
    }

    [Fact]
    public async Task SwitchProviderAsync_IsRefused()
    {
        var selection = new FixedModelSelection("openrouter", "vendor/model");

        await Assert.ThrowsAsync<NotSupportedException>(() => selection.SwitchProviderAsync("anthropic", CancellationToken.None));
        Assert.Equal("openrouter", selection.ProviderName);
    }

    [Fact]
    public void SetModel_IsRefused()
    {
        var selection = new FixedModelSelection("openrouter", "vendor/model");

        Assert.Throws<NotSupportedException>(() => selection.SetModel("another", null));
        Assert.Equal("vendor/model", selection.Model);
    }

    /// <summary>The point of the fixed selection: nothing it does may reach the config file every
    /// other Litos face shares.</summary>
    [Fact]
    public async Task NothingIsEverWrittenToTheSharedConfigFile()
    {
        var original = LitosConfig.ConfigFilePath;
        var scratch = Path.Combine(Path.GetTempPath(), $"litos-hosting-fixed-config-{Guid.NewGuid():n}.json");
        LitosConfig.ConfigFilePath = scratch;
        try
        {
            var selection = new FixedModelSelection("openrouter", "vendor/model");
            await selection.EnsureResolvedAsync(CancellationToken.None);
            Assert.Throws<NotSupportedException>(() => selection.SetModel("another", null));

            Assert.False(File.Exists(scratch));
        }
        finally
        {
            LitosConfig.ConfigFilePath = original;
        }
    }
}
