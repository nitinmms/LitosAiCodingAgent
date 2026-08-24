using System.Text.Json;
using System.Text.Json.Serialization;

namespace Litos.Host;

public sealed record LitosConfig(
    string DefaultProvider,
    string? DefaultModel,
    string? LastWorkingDirectory,
    IReadOnlyDictionary<string, string> ApiKeys,
    // Base URL of a local OpenAI-compatible server (LM Studio, Ollama, vLLM, ...) for the
    // "local" chat provider. Unlike every other provider, "local" needs no API key to be
    // usable — it's gated on this being set rather than on ApiKeys containing "local" (see
    // IsProviderConfigured) — so this is its own field rather than living in ApiKeys.
    string? LocalBaseUrl = null,
    int? ShellCommandTimeoutSeconds = null)
{
    /// <summary>
    /// Hard wall-clock cap on a single `shell` tool command — see ShellTool's own doc comment
    /// for why this exists (a command with no timeout can hang a turn forever, e.g. a CLI
    /// blocking on an interactive prompt that will never receive input). Exposed as a raw
    /// int-seconds config field (rather than TimeSpan, which System.Text.Json doesn't round-trip
    /// as a plain human-editable number) so a user can override it in config.json without a
    /// code change; unset means "use ShellTool's own 5-minute default".
    /// </summary>
    public TimeSpan? ShellCommandTimeout =>
        ShellCommandTimeoutSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null;

    private static readonly IReadOnlyDictionary<string, string> EnvVarNames = new Dictionary<string, string>
    {
        ["anthropic"] = "ANTHROPIC_API_KEY",
        ["openai"] = "OPENAI_API_KEY",
        ["gemini"] = "GEMINI_API_KEY",
        ["mesh_api"] = "MESHAPI_API_KEY",
        ["openrouter"] = "OPENROUTER_API_KEY",
        ["local"] = "LOCAL_API_KEY",
        ["tavily"] = "TAVILY_API_KEY",
        ["telegram"] = "TELEGRAM_BOT_TOKEN",
    };

    /// <summary>
    /// The subset of ApiKeys that are chat providers (as opposed to tool-only keys like
    /// "tavily") — callers that pick/prompt-for an active chat provider filter to these
    /// rather than assuming every key in ApiKeys is one.
    /// </summary>
    public static readonly IReadOnlyList<string> ChatProviderNames = ["anthropic", "openai", "gemini", "mesh_api", "openrouter", "local"];

    /// <summary>
    /// True if <paramref name="providerName"/> has enough configuration to be usable. Every
    /// provider but "local" means "has an API key"; "local" means "has a base URL" instead —
    /// a local OpenAI-compatible server (LM Studio, Ollama, ...) typically needs no real key at
    /// all, so gating it on ApiKeys the way every other provider is gated would make it
    /// unreachable without a key it doesn't need.
    /// </summary>
    public bool IsProviderConfigured(string providerName) =>
        providerName == "local" ? !string.IsNullOrEmpty(LocalBaseUrl) : ApiKeys.ContainsKey(providerName);

    /// <summary>
    /// Chat providers with enough configuration to actually be selected — the candidate list
    /// every face (Console/Gui/Api) builds its provider picker from, in one place instead of
    /// each face re-deriving "has a key, or is local with a base url" independently.
    /// </summary>
    public IReadOnlyList<string> AvailableChatProviders => [.. ChatProviderNames.Where(IsProviderConfigured)];

    public static string ConfigFilePath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".litos", "config.json");

    public static LitosConfig Load()
    {
        var onDisk = LoadFromDisk();

        var apiKeys = new Dictionary<string, string>();
        foreach (var (provider, envVar) in EnvVarNames)
        {
            // Environment variable always wins; the config file is only a fallback
            // for a provider whose env var is absent.
            var value = Environment.GetEnvironmentVariable(envVar)
                ?? (provider == "gemini" ? Environment.GetEnvironmentVariable("GOOGLE_API_KEY") : null)
                ?? onDisk?.ApiKeys.GetValueOrDefault(provider);

            if (!string.IsNullOrEmpty(value))
                apiKeys[provider] = value;
        }

        return new LitosConfig(
            DefaultProvider: onDisk?.DefaultProvider ?? "anthropic",
            DefaultModel: onDisk?.DefaultModel,
            LastWorkingDirectory: onDisk?.LastWorkingDirectory,
            ApiKeys: apiKeys,
            LocalBaseUrl: Environment.GetEnvironmentVariable("LOCAL_BASE_URL") ?? onDisk?.LocalBaseUrl,
            ShellCommandTimeoutSeconds: onDisk?.ShellCommandTimeoutSeconds);
    }

    public string? GetApiKey(string providerName) =>
        ApiKeys.TryGetValue(providerName, out var key) ? key : null;

    /// <summary>
    /// True if <paramref name="providerName"/>'s key came from an environment variable rather
    /// than config.json — an env var always wins over the file (Load's own precedence, above),
    /// so a Settings UI editing config.json should say so rather than implying the edit will
    /// take effect when it silently won't.
    /// </summary>
    public static bool IsSetByEnvironmentVariable(string providerName) =>
        EnvVarNames.TryGetValue(providerName, out var envVar) &&
        (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(envVar))
            || (providerName == "gemini" && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GOOGLE_API_KEY"))));

    public void Save()
    {
        var directory = Path.GetDirectoryName(ConfigFilePath)!;
        Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(ConfigFilePath, json);
    }

    private static LitosConfig? LoadFromDisk()
    {
        if (!File.Exists(ConfigFilePath))
            return null;

        try
        {
            var json = File.ReadAllText(ConfigFilePath);
            return JsonSerializer.Deserialize<LitosConfig>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
