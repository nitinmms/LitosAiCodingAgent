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
    int? ShellCommandTimeoutSeconds = null,
    int? StreamIdleTimeoutSeconds = null)
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

    /// <summary>
    /// How long AgentLoop waits for the NEXT chunk of a provider's response stream before treating
    /// the connection as stalled — see AgentLoop.DefaultStreamIdleTimeout's own doc comment for why
    /// it resets per-chunk rather than capping the whole request. That 60s default is generous
    /// enough for the hosted providers it was tuned against, but a local model (LM Studio, Ollama,
    /// ...) running on memory-constrained hardware can legitimately take longer than that just to
    /// produce its first token once the transcript's context grows — confirmed live: a local
    /// qwen3.8-27b-mlx run on a 24GB Mac hit exactly this at 54% context usage. Unset means "use
    /// AgentLoop's own 60s default"; same int-seconds-not-TimeSpan reasoning as
    /// ShellCommandTimeoutSeconds above.
    /// </summary>
    public TimeSpan? StreamIdleTimeout =>
        StreamIdleTimeoutSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null;

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

    // internal set, not init: a test that exercises Save() (e.g. via AgentWorker.SwitchProviderAsync/
    // SetModel) must redirect this to a scratch path first, or it silently overwrites the real
    // developer's ~/.litos/config.json — this happened for real (a "fake"/"new-default" test
    // fixture value ended up as someone's actual DefaultProvider/DefaultModel). No production code
    // ever reassigns this; it exists purely so a test fixture can point it at a temp file for the
    // duration of one test and restore it afterward.
    public static string ConfigFilePath { get; internal set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".litos", "config.json");

    public static LitosConfig Load()
    {
        var onDisk = LoadFromDisk();

        var apiKeys = new Dictionary<string, string>();
        foreach (var (provider, envVar) in EnvVarNames)
        {
            // Environment variable always wins; the config file is only a fallback
            // for a provider whose env var is absent.
            var value = GetEnvironmentVariable(envVar)
                ?? (provider == "gemini" ? GetEnvironmentVariable("GOOGLE_API_KEY") : null)
                ?? onDisk?.ApiKeys.GetValueOrDefault(provider);

            if (!string.IsNullOrEmpty(value))
                apiKeys[provider] = value;
        }

        return new LitosConfig(
            DefaultProvider: onDisk?.DefaultProvider ?? "anthropic",
            DefaultModel: onDisk?.DefaultModel,
            LastWorkingDirectory: onDisk?.LastWorkingDirectory,
            ApiKeys: apiKeys,
            LocalBaseUrl: GetEnvironmentVariable("LOCAL_BASE_URL") ?? onDisk?.LocalBaseUrl,
            ShellCommandTimeoutSeconds: onDisk?.ShellCommandTimeoutSeconds,
            StreamIdleTimeoutSeconds: onDisk?.StreamIdleTimeoutSeconds);
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
        (!string.IsNullOrEmpty(GetEnvironmentVariable(envVar))
            || (providerName == "gemini" && !string.IsNullOrEmpty(GetEnvironmentVariable("GOOGLE_API_KEY"))));

    /// <summary>
    /// Reads an environment variable the way a value just saved by ConfigEndpoints.cs/
    /// ApiKeysWindow's Windows path needs to be observed: process-scope first (so a real value the
    /// user's own shell/session/CI exported before launching Litos always wins), then — Windows
    /// only — falling back to EnvironmentVariableTarget.User. That fallback is not a snapshot the
    /// way the parameterless overload is: .NET reads it live from the registry on every call, in
    /// any process, regardless of when that process started. Without it, a key saved via
    /// SetEnvironmentVariable(..., EnvironmentVariableTarget.User) — which persists to the registry
    /// correctly — stayed invisible to every already-running process, including a freshly spawned
    /// Litos.VsCodeHost.exe child of a VS Code "Reload Window", which restarts the extension host
    /// but not the underlying OS process whose environment block was captured at VS Code's own
    /// launch. That forced a full VS Code quit-and-relaunch (not just a reload) for a newly saved
    /// key to ever take effect. EnvironmentVariableTarget.User throws
    /// PlatformNotSupportedException on non-Windows, hence the OperatingSystem.IsWindows() guard —
    /// non-Windows platforms have no such fallback to make and don't need one, since SaveKeys
    /// writes config.json there instead, which LoadFromDisk already re-reads fresh every call.
    /// </summary>
    private static string? GetEnvironmentVariable(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? (OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User) : null);

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
