using Litos.Host;
using Litos.VsCodeHost.Config;

namespace Litos.VsCodeHost.Tests;

/// <summary>
/// Covers ConfigEndpoints.BuildKeyStatus only — the pure "env wins over config.json wins over
/// unset" precedence the webview's /keys popup renders its per-field "already set" hints from.
/// SaveKeys itself is deliberately not covered here: per ReadMe_VsCodeExtension.md §8's own
/// caution, its Windows path writes real user-scope environment variables
/// (EnvironmentVariableTarget.User) that cannot be sandboxed the way a test process's own
/// environment can, so exercising it live already caused one accidental real-machine mutation
/// during development. BuildKeyStatus's own env-var check (LitosConfig.IsSetByEnvironmentVariable)
/// reads process-scope first, then — Windows only — EnvironmentVariableTarget.User as a live
/// registry fallback (see LitosConfig.GetEnvironmentVariable's own remarks for why: a key saved to
/// the registry must be visible to an already-running process, not just a freshly launched one).
/// ClearedEnvironment below has to account for that fallback on Windows too, or a real key actually
/// present in this machine's user environment (exactly the kind SaveKeys itself writes) leaks
/// through into an "unset" assertion — save-and-restore only, via TrySetUserScope, never a bare
/// clear, so a test run never permanently deletes a real value that happened to be set going in.
/// </summary>
public sealed class ConfigEndpointsTests
{
    private static readonly string[] EnvVarsUnderTest =
        ["ANTHROPIC_API_KEY", "OPENAI_API_KEY", "GEMINI_API_KEY", "GOOGLE_API_KEY", "OPENROUTER_API_KEY", "MESHAPI_API_KEY", "LOCAL_API_KEY", "TAVILY_API_KEY"];

    private static LitosConfig EmptyConfig() =>
        new(DefaultProvider: "anthropic", DefaultModel: null, LastWorkingDirectory: null, ApiKeys: new Dictionary<string, string>());

    [Fact]
    public void Reports_unset_for_every_provider_when_nothing_is_configured()
    {
        using var _ = new ClearedEnvironment(EnvVarsUnderTest);

        var status = ConfigEndpoints.BuildKeyStatus(EmptyConfig());

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
        using var _ = new ClearedEnvironment(EnvVarsUnderTest);
        var config = EmptyConfig() with { ApiKeys = new Dictionary<string, string> { ["anthropic"] = "sk-from-disk" } };

        var status = ConfigEndpoints.BuildKeyStatus(config);

        Assert.Equal("config", status["anthropic"]);
        Assert.Equal("unset", status["openai"]);
    }

    [Fact]
    public void Reports_config_for_a_populated_local_base_url()
    {
        using var _ = new ClearedEnvironment(EnvVarsUnderTest);
        var config = EmptyConfig() with { LocalBaseUrl = "http://localhost:1234/v1" };

        var status = ConfigEndpoints.BuildKeyStatus(config);

        Assert.Equal("config", status["localBaseUrl"]);
    }

    [Fact]
    public void Env_var_wins_over_a_same_provider_key_also_present_in_config_json()
    {
        using var _ = new ClearedEnvironment(EnvVarsUnderTest, set: ("ANTHROPIC_API_KEY", "sk-from-env"));
        // Mirrors LitosConfig.Load()'s own precedence — a provider can have stale config.json
        // content that the running process's env var already shadows; BuildKeyStatus must say so
        // rather than reporting "config", which would misleadingly imply saving a blank field here
        // keeps the config.json value in effect.
        var config = EmptyConfig() with { ApiKeys = new Dictionary<string, string> { ["anthropic"] = "sk-from-disk" } };

        var status = ConfigEndpoints.BuildKeyStatus(config);

        Assert.Equal("env", status["anthropic"]);
    }

    [Fact]
    public void Gemini_accepts_GOOGLE_API_KEY_as_the_env_fallback_name()
    {
        using var _ = new ClearedEnvironment(EnvVarsUnderTest, set: ("GOOGLE_API_KEY", "sk-google"));

        var status = ConfigEndpoints.BuildKeyStatus(EmptyConfig());

        Assert.Equal("env", status["gemini"]);
    }

    [Fact]
    public void Every_provider_field_the_popup_renders_is_present_in_the_result()
    {
        using var _ = new ClearedEnvironment(EnvVarsUnderTest);

        var status = ConfigEndpoints.BuildKeyStatus(EmptyConfig());

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

    /// <summary>
    /// Saves and restores the env vars BuildKeyStatus/IsSetByEnvironmentVariable consults, at every
    /// scope LitosConfig.GetEnvironmentVariable actually reads: process-scope always
    /// (Environment.SetEnvironmentVariable with no EnvironmentVariableTarget defaults to Process,
    /// safely test-local), plus — Windows only — EnvironmentVariableTarget.User, since that's now a
    /// live fallback LitosConfig reads on every call, not a snapshot. The User-scope leg is real
    /// per-user registry state (the same place SaveKeys' Windows path and a real Litos install
    /// write to), so this only ever saves-then-restores it — via TrySetUserScope, swallowing the
    /// SecurityException a locked-down CI runner could throw on a registry write — never leaving it
    /// cleared, so a test run can't permanently delete a real value that was already there, and a
    /// real value already there can't make an "unset" assertion fail (see the failure this was
    /// added to fix: a real OPENROUTER_API_KEY on the dev machine leaked into
    /// Reports_unset_for_every_provider_when_nothing_is_configured once GetEnvironmentVariable
    /// started reading User-scope too).
    /// </summary>
    private sealed class ClearedEnvironment : IDisposable
    {
        private readonly Dictionary<string, string?> _originalProcessValues = new();
        private readonly Dictionary<string, string?> _originalUserValues = new();

        public ClearedEnvironment(IEnumerable<string> names, params (string Name, string Value)[] set)
        {
            foreach (var name in names)
            {
                _originalProcessValues[name] = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, null);

                if (OperatingSystem.IsWindows())
                {
                    _originalUserValues[name] = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
                    TrySetUserScope(name, null);
                }
            }
            foreach (var (name, value) in set)
                Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            foreach (var (name, value) in _originalProcessValues)
                Environment.SetEnvironmentVariable(name, value);

            if (OperatingSystem.IsWindows())
                foreach (var (name, value) in _originalUserValues)
                    TrySetUserScope(name, value);
        }

        private static void TrySetUserScope(string name, string? value)
        {
            try
            {
                Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.User);
            }
            catch (System.Security.SecurityException)
            {
                // A CI runner without registry-write permission can't touch User-scope at all —
                // nothing to save/restore in that case, and GetEnvironmentVariable's own
                // User-scope read would presumably fail/return null there too.
            }
        }
    }
}
