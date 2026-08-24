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
/// only ever *reads* Environment.GetEnvironmentVariable at process scope, which
/// Environment.SetEnvironmentVariable(name, value) — no target argument, i.e. Process-scoped, not
/// User-scoped — can set and clear safely within a single test's lifetime.
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

    /// <summary>
    /// Saves and restores the exact set of env vars BuildKeyStatus/IsSetByEnvironmentVariable
    /// consults, process-scoped only (Environment.SetEnvironmentVariable with no
    /// EnvironmentVariableTarget defaults to Process) — never touches the real per-user persisted
    /// values SaveKeys' Windows path writes, and restores whatever was there (usually nothing) once
    /// the test ends, so tests can run in any order without leaking state into each other.
    /// </summary>
    private sealed class ClearedEnvironment : IDisposable
    {
        private readonly Dictionary<string, string?> _originalValues = new();

        public ClearedEnvironment(IEnumerable<string> names, params (string Name, string Value)[] set)
        {
            foreach (var name in names)
            {
                _originalValues[name] = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, null);
            }
            foreach (var (name, value) in set)
                Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            foreach (var (name, value) in _originalValues)
                Environment.SetEnvironmentVariable(name, value);
        }
    }
}
