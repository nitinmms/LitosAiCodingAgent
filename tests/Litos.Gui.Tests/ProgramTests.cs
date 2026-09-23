using Litos.Agent.Providers;
using Litos.Host;

namespace Litos.Gui.Tests;

/// <summary>
/// Covers ResolveStartupWorkingDirectory, the decision behind setting Environment.CurrentDirectory
/// at GUI startup — see the fix in Program.cs for why this matters: file/shell tools resolve
/// relative paths against the real process CWD, not against the status bar's tracked working
/// directory, so the two must be kept in sync. Also covers ResolveStartupProvider/
/// ResolveStartupModel — extracted after a real user-reported bug: a corrupted/stale
/// config.json with an unconfigured DefaultProvider ("fake") correctly fell back to a real
/// provider, but the unrelated DefaultModel ("new-default") was still passed through verbatim
/// instead of being invalidated alongside the provider it no longer matched.
/// </summary>
public class ProgramTests
{
    private static LitosConfig ConfigWith(string defaultProvider, string? defaultModel, params string[] configuredProviders) =>
        new(
            DefaultProvider: defaultProvider,
            DefaultModel: defaultModel,
            LastWorkingDirectory: null,
            ApiKeys: configuredProviders.ToDictionary(p => p, _ => "key"));

    // ---- ResolveStartupProvider ----

    [Fact]
    public void ResolveStartupProvider_UsesSavedProvider_WhenStillConfigured()
    {
        var config = ConfigWith("openrouter", null, "anthropic", "openrouter");

        var result = Program.ResolveStartupProvider(config);

        Assert.Equal("openrouter", result);
    }

    [Fact]
    public void ResolveStartupProvider_FallsBackToFirstAvailable_WhenSavedProviderIsNotConfigured()
    {
        // Reproduces the reported bug's actual config.json shape: DefaultProvider "fake" has no
        // real API key, so it's not in AvailableChatProviders at all.
        var config = ConfigWith("fake", "new-default", "anthropic");

        var result = Program.ResolveStartupProvider(config);

        Assert.Equal("anthropic", result);
    }

    // ---- ResolveStartupModel ----

    [Fact]
    public void ResolveStartupModel_KeepsSavedModel_WhenItIsInTheProvidersModelList()
    {
        var models = new[] { new ModelInfo("gpt-5", "GPT-5", IsDefault: false), new ModelInfo("gpt-5-mini", "GPT-5 mini", IsDefault: true) };

        var result = Program.ResolveStartupModel("gpt-5", models);

        Assert.Equal("gpt-5", result);
    }

    [Fact]
    public void ResolveStartupModel_DiscardsSavedModel_WhenItIsNotInTheProvidersModelList()
    {
        // Reproduces the reported bug: "new-default" was carried over from a config that no longer
        // matches the resolved provider — it must not be trusted just because it's non-null.
        var models = new[] { new ModelInfo("gpt-5", "GPT-5", IsDefault: false), new ModelInfo("gpt-5-mini", "GPT-5 mini", IsDefault: true) };

        var result = Program.ResolveStartupModel("new-default", models);

        Assert.Equal("gpt-5-mini", result); // falls through to the provider's own IsDefault model
    }

    [Fact]
    public void ResolveStartupModel_FallsBackToFirstModel_WhenNoneAreMarkedDefault()
    {
        var models = new[] { new ModelInfo("model-a", "Model A", IsDefault: false), new ModelInfo("model-b", "Model B", IsDefault: false) };

        var result = Program.ResolveStartupModel(null, models);

        Assert.Equal("model-a", result);
    }

    [Fact]
    public void ResolveStartupModel_TrustsSavedModel_WhenTheProviderModelListIsEmpty()
    {
        // An empty list means ListModelsAsync failed/returned nothing (offline, rate-limited) —
        // there's nothing to validate against, so the saved model is trusted rather than discarded.
        var result = Program.ResolveStartupModel("gpt-5", []);

        Assert.Equal("gpt-5", result);
    }

    [Fact]
    public void ResolveStartupModel_ReturnsNull_WhenNoSavedModelAndProviderListIsEmpty()
    {
        var result = Program.ResolveStartupModel(null, []);

        Assert.Null(result); // Main turns this into a startup-halting exception — no model to start with at all.
    }

    [Fact]
    public void ResolveStartupWorkingDirectory_UsesLastWorkingDirectory_WhenItStillExists()
    {
        var result = Program.ResolveStartupWorkingDirectory(
            lastWorkingDirectory: @"C:\repo",
            directoryExists: dir => dir == @"C:\repo",
            getCurrentDirectory: () => @"C:\fallback");

        Assert.Equal(@"C:\repo", result);
    }

    [Fact]
    public void ResolveStartupWorkingDirectory_FallsBackToCurrentDirectory_WhenLastWorkingDirectoryNoLongerExists()
    {
        var result = Program.ResolveStartupWorkingDirectory(
            lastWorkingDirectory: @"C:\deleted-repo",
            directoryExists: _ => false,
            getCurrentDirectory: () => @"C:\fallback");

        Assert.Equal(@"C:\fallback", result);
    }

    [Fact]
    public void ResolveStartupWorkingDirectory_FallsBackToCurrentDirectory_WhenNoLastWorkingDirectoryConfigured()
    {
        var result = Program.ResolveStartupWorkingDirectory(
            lastWorkingDirectory: null,
            directoryExists: _ => throw new InvalidOperationException("Should not check existence of a null path."),
            getCurrentDirectory: () => @"C:\fallback");

        Assert.Equal(@"C:\fallback", result);
    }
}
