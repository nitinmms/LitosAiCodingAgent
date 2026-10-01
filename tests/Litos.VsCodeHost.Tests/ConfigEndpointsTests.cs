using Litos.Host;
using Litos.VsCodeHost.Config;

namespace Litos.VsCodeHost.Tests;

/// <summary>
/// Covers ConfigEndpoints.BuildKeyStatus only — the pure "env wins over config.json wins over
/// unset" precedence the webview's /keys popup renders its per-field "already set" hints from.
/// SaveKeys itself is deliberately not covered here: per ReadMe_VsCodeExtension.md §8's own
/// caution, its Windows path writes real user-scope environment variables
/// (EnvironmentVariableTarget.User) that cannot be sandboxed the way a test process's own
/// environment can.
///
/// Every test hands BuildKeyStatus its own environment (a dictionary lookup) and never reads or
/// changes the real one. These tests used to clear the real variables — on Windows the user's own
/// registry entries, since that is where LitosConfig looks — and restore them afterwards; a run
/// interrupted in between left real API keys deleted, which happened.
/// </summary>
public sealed class ConfigEndpointsTests
{
    private static LitosConfig EmptyConfig() =>
        new(DefaultProvider: "anthropic", DefaultModel: null, LastWorkingDirectory: null, ApiKeys: new Dictionary<string, string>());

    [Fact]
    public void Reports_unset_for_every_provider_when_nothing_is_configured()
    {
        var status = ConfigEndpoints.BuildKeyStatus(EmptyConfig(), EnvironmentWith());

        Assert.Equal("unset", status["anthropic"]);
        Assert.Equal("unset", status["openai"]);
        Assert.Equal("unset", status["gemini"]);
        Assert.Equal("unset", status["openrouter"]);
        Assert.Equal("unset", status["local"]);
        Assert.Equal("unset", status["tavily"]);
        Assert.Equal("unset", status["localBaseUrl"]);
    }

    [Fact]
    public void Reports_config_for_a_provider_whose_key_is_only_in_config_json()
    {
        var config = EmptyConfig() with { ApiKeys = new Dictionary<string, string> { ["anthropic"] = "sk-from-disk" } };

        var status = ConfigEndpoints.BuildKeyStatus(config, EnvironmentWith());

        Assert.Equal("config", status["anthropic"]);
        Assert.Equal("unset", status["openai"]);
    }

    [Fact]
    public void Reports_config_for_a_populated_local_base_url()
    {
        var config = EmptyConfig() with { LocalBaseUrl = "http://localhost:1234/v1" };

        var status = ConfigEndpoints.BuildKeyStatus(config, EnvironmentWith());

        Assert.Equal("config", status["localBaseUrl"]);
    }

    [Fact]
    public void Env_var_wins_over_a_same_provider_key_also_present_in_config_json()
    {
                // Mirrors LitosConfig.Load()'s own precedence — a provider can have stale config.json
        // content that the running process's env var already shadows; BuildKeyStatus must say so
        // rather than reporting "config", which would misleadingly imply saving a blank field here
        // keeps the config.json value in effect.
        var config = EmptyConfig() with { ApiKeys = new Dictionary<string, string> { ["anthropic"] = "sk-from-disk" } };

        var status = ConfigEndpoints.BuildKeyStatus(config, EnvironmentWith(("ANTHROPIC_API_KEY", "sk-from-env")));

        Assert.Equal("env", status["anthropic"]);
    }

    [Fact]
    public void Gemini_accepts_GOOGLE_API_KEY_as_the_env_fallback_name()
    {
                var status = ConfigEndpoints.BuildKeyStatus(EmptyConfig(), EnvironmentWith(("GOOGLE_API_KEY", "sk-google")));

        Assert.Equal("env", status["gemini"]);
    }

    [Fact]
    public void Every_provider_field_the_popup_renders_is_present_in_the_result()
    {
        var status = ConfigEndpoints.BuildKeyStatus(EmptyConfig(), EnvironmentWith());

        // Matches KEY_FIELDS in webviewContent.ts plus the separate local-base-url field — a
        // missing key here would leave that field's hint silently blank in the popup instead of
        // throwing, which is exactly the kind of drift this test exists to catch.
        var expected = new[] { "anthropic", "openai", "gemini", "openrouter", "mesh_api", "local", "tavily", "localBaseUrl" };
        Assert.Equal(expected.OrderBy(k => k, StringComparer.Ordinal), status.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void Configured_after_save_when_reloaded_config_already_shows_a_provider()
    {
        var reloaded = EmptyConfig() with { ApiKeys = new Dictionary<string, string> { ["anthropic"] = "sk-from-disk" } };
        var request = new SaveKeysRequest(Entries: [], LocalBaseUrl: null);

        Assert.True(ConfigEndpoints.IsConfiguredAfterSave(request, reloaded));
    }

    [Fact]
    public void Configured_after_save_when_a_chat_provider_key_was_just_submitted_even_if_reloaded_cannot_see_it_yet()
    {
        // Reproduces the Windows bug: SaveKeys wrote the OpenRouter key to
        // EnvironmentVariableTarget.User, which this same process's LitosConfig.Load() (process-scope
        // env only) cannot see yet — reloaded is empty even though the save succeeded.
        var reloaded = EmptyConfig();
        var request = new SaveKeysRequest(Entries: [new KeyEntry("openrouter", "sk-or-123")], LocalBaseUrl: null);

        Assert.True(ConfigEndpoints.IsConfiguredAfterSave(request, reloaded));
    }

    [Fact]
    public void Not_configured_after_save_when_only_a_non_chat_provider_key_was_submitted_and_nothing_else_is_set()
    {
        // Tavily is tool-only, not a chat provider (LitosConfig.ChatProviderNames) — submitting only
        // that key should not be reported as "a chat provider is configured".
        var reloaded = EmptyConfig();
        var request = new SaveKeysRequest(Entries: [new KeyEntry("tavily", "tvly-123")], LocalBaseUrl: null);

        Assert.False(ConfigEndpoints.IsConfiguredAfterSave(request, reloaded));
    }

    /// <summary>A stand-in environment holding exactly the given variables.</summary>
    private static Func<string, string?> EnvironmentWith(params (string Name, string Value)[] variables)
    {
        var values = variables.ToDictionary(v => v.Name, v => v.Value);
        return name => values.GetValueOrDefault(name);
    }

    /// <summary>The tests above must leave the machine exactly as they found it.</summary>
    [Fact]
    public void BuildKeyStatus_with_a_supplied_environment_reads_nothing_from_the_real_one()
    {
        var asked = new List<string>();

        var status = ConfigEndpoints.BuildKeyStatus(EmptyConfig(), name =>
        {
            asked.Add(name);
            return null;
        });

        // Every provider was decided from the supplied lookup alone: whatever keys this machine
        // really has, none shows up as "env".
        Assert.DoesNotContain("env", status.Values);
        Assert.Contains("ANTHROPIC_API_KEY", asked);
        Assert.Contains("GOOGLE_API_KEY", asked);
    }
}
