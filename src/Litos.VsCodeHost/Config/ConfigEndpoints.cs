using Litos.Host;

namespace Litos.VsCodeHost.Config;

/// <summary>
/// First-run API key setup for the webview — mirrors Litos.Gui's ApiKeysWindow (same persistence:
/// Windows user-scope env vars, else ~/.litos/config.json, both already read back by
/// LitosConfig.Load() and shared across every face) but reachable over JSON instead of a modal
/// Avalonia window.
///
/// No live reload: LitosHostBuilder.AddLitosAgent conditionally registers each keyed IChatProvider
/// once, at DI-container-build time, gated on whatever LitosConfig.GetApiKey(...) returned at that
/// instant — there's no seam to swap a provider registration into an already-built
/// IServiceProvider. So saving a key here doesn't take effect in *this* process; it mirrors
/// ApiKeysWindow's own first-run behavior ("Litos will then close so you can restart it") except
/// the restart is the extension's job, not the user's — see extension.ts, which calls POST
/// /config/keys, then kills and respawns this process so the freshly-saved key is picked up by a
/// fresh LitosConfig.Load() on the next AddLitosAgent call.
/// </summary>
public static class ConfigEndpoints
{
    public static IEndpointRouteBuilder MapConfigEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/config/status", () =>
        {
            // Re-reads from disk/env rather than closing over the startup-time `config` snapshot,
            // unlike every other field below (which can't change within this process's lifetime —
            // API keys only ever change via a save-then-respawn, see this class's own remarks).
            // DefaultModel is different: AgentWorker.SetModel/SwitchProviderAsync persist a new
            // DefaultModel to disk live, in this same process, via their own LitosConfig.Save() —
            // so reading the stale closed-over `config` here would keep reporting
            // defaultModelSet: false forever after the user's very first /model, even though the
            // choice was correctly saved.
            var current = LitosConfig.Load();
            return Results.Ok(new
            {
                configured = current.AvailableChatProviders.Count > 0,
                availableProviders = current.AvailableChatProviders,
                keyStatus = BuildKeyStatus(current),
                // Whether the user has ever explicitly run /model — distinct from AgentWorker.Model,
                // which is never null in practice (it lazily resolves to a real model on first turn,
                // see AgentWorker.EnsureModelResolvedAsync). This is the only reliable signal for the
                // VS Code extension's onboarding hint (openDefaultModelHint) to know whether the
                // agent is silently running on an auto-picked model versus one the user actually
                // chose.
                defaultModelSet = current.DefaultModel != null,
            });
        });

        app.MapPost("/config/keys", (SaveKeysRequest request) =>
        {
            if (request.Entries.Count == 0 && string.IsNullOrWhiteSpace(request.LocalBaseUrl))
                return Results.BadRequest("At least one API key or a local server URL is required.");

            SaveKeys(request);

            // Reflects what was just written to disk/env — NOT what this already-running process
            // will use for chat providers (see class remarks). The caller must restart the host
            // process for a new key to actually take effect.
            var reloaded = LitosConfig.Load();

            return Results.Ok(new
            {
                configured = IsConfiguredAfterSave(request, reloaded),
                availableProviders = reloaded.AvailableChatProviders,
                keyStatus = BuildKeyStatus(reloaded),
                restartRequired = true,
            });
        });

        return app;
    }

    // Every provider the webview's keys popup has a field for — mirrors Litos.Gui's
    // ApiKeysWindow.Fields provider list plus LocalBaseUrl, which isn't a key but gets the same
    // "already set" treatment. Kept here (not EnvVarName's switch) since it also drives what
    // /config/status reports even for a provider with no key set yet.
    private static readonly string[] KeyStatusProviders = ["anthropic", "openai", "gemini", "mesh_api", "openrouter", "local", "tavily"];

    /// <summary>
    /// Whether the just-submitted save should be reported as "configured" — deliberately not just
    /// <c>reloaded.AvailableChatProviders.Count > 0</c>. On Windows, SaveKeys writes chat-provider
    /// keys to <c>EnvironmentVariableTarget.User</c> (the registry), which
    /// Environment.GetEnvironmentVariable (process-scope, the target LitosConfig.Load reads) cannot
    /// see from this already-running process — that write is only observable after a restart. Left
    /// unaccounted for, that made the popup report "no chat provider is configured yet" immediately
    /// after a successful save, on Windows only (config.json, the non-Windows path, is re-read from
    /// disk by Load() and so shows up immediately). So: treat any chat-provider key submitted in
    /// this request as configured too, since SaveKeys either wrote it to the registry (Windows,
    /// invisible to `reloaded` until restart but written) or `reloaded` already reflects it
    /// (config.json, every other platform). Takes the plain request/config rather than reading
    /// environment/disk itself so it can be unit tested without touching real user-scope env vars —
    /// same shape as BuildKeyStatus, just below.
    /// </summary>
    internal static bool IsConfiguredAfterSave(SaveKeysRequest request, LitosConfig reloaded) =>
        reloaded.AvailableChatProviders.Count > 0
        || request.Entries.Any(e => LitosConfig.ChatProviderNames.Contains(e.Provider));

    /// <summary>
    /// Per-provider "is a key already set, and if so where" — lets the webview's keys popup show
    /// the same "ANTHROPIC_API_KEY — already set" / "already set — leave blank to keep" placeholder
    /// hints as Litos.Gui's ApiKeysWindow, without ever echoing the real secret back to the client.
    /// Internal (not private), and takes a plain LitosConfig rather than reading env vars/disk
    /// itself, purely so Litos.VsCodeHost.Tests can exercise the "env wins over config.json wins
    /// over unset" precedence without touching real environment variables or ~/.litos/config.json —
    /// same "extract the pure decision, test that" shape as ApiKeysDialog.MergeConfig.
    /// </summary>
    internal static Dictionary<string, string> BuildKeyStatus(LitosConfig config)
    {
        var status = new Dictionary<string, string>();
        foreach (var provider in KeyStatusProviders)
        {
            status[provider] = LitosConfig.IsSetByEnvironmentVariable(provider)
                ? "env"
                : config.ApiKeys.ContainsKey(provider)
                    ? "config"
                    : "unset";
        }
        status["localBaseUrl"] = string.IsNullOrEmpty(config.LocalBaseUrl) ? "unset" : "config";
        return status;
    }

    // Same persistence split as Litos.Gui's ApiKeysWindow.TrySave: Windows writes user-scope
    // environment variables (no admin elevation needed, persists across restarts, picked up by
    // every face's own LitosConfig.Load()); everywhere else — and LocalBaseUrl always, since it
    // isn't a secret and has no environment-variable convention — goes to config.json.
    private static void SaveKeys(SaveKeysRequest request)
    {
        var hasLocalBaseUrl = !string.IsNullOrWhiteSpace(request.LocalBaseUrl);

        if (OperatingSystem.IsWindows())
        {
            foreach (var entry in request.Entries)
                Environment.SetEnvironmentVariable(EnvVarName(entry.Provider), entry.ApiKey, EnvironmentVariableTarget.User);
        }

        if (hasLocalBaseUrl || !OperatingSystem.IsWindows())
        {
            var config = LitosConfig.Load();
            var apiKeys = new Dictionary<string, string>(config.ApiKeys);
            if (!OperatingSystem.IsWindows())
                foreach (var entry in request.Entries)
                    apiKeys[entry.Provider] = entry.ApiKey;

            config = config with
            {
                ApiKeys = apiKeys,
                LocalBaseUrl = hasLocalBaseUrl ? request.LocalBaseUrl!.Trim() : config.LocalBaseUrl,
            };
            config.Save();
        }
    }

    private static string EnvVarName(string provider) => provider switch
    {
        "anthropic" => "ANTHROPIC_API_KEY",
        "openai" => "OPENAI_API_KEY",
        "gemini" => "GEMINI_API_KEY",
        "mesh_api" => "MESHAPI_API_KEY",
        "openrouter" => "OPENROUTER_API_KEY",
        "local" => "LOCAL_API_KEY",
        "tavily" => "TAVILY_API_KEY",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown provider."),
    };
}

public sealed record SaveKeysRequest(IReadOnlyList<KeyEntry> Entries, string? LocalBaseUrl);

public sealed record KeyEntry(string Provider, string ApiKey);
