namespace Litos.Agent.Providers;

/// <summary>
/// Maps a model id to its published context window, for providers whose ListModelsAsync
/// doesn't return this (Anthropic, OpenAI, Gemini all omit it from their model-list APIs).
/// Matched by prefix rather than exact id, since providers append dated suffixes
/// (e.g. "claude-sonnet-5-20260115") and ship new dated snapshots over time.
/// OpenRouter is exempt from this table — its API returns a real per-model context_length,
/// which OpenRouterChatProvider uses directly instead of guessing.
/// </summary>
public static class ModelContextWindows
{
    /// <summary>Used when no prefix below matches — better to under-promise than silently misreport a brand-new model.</summary>
    public const int FallbackContextLength = 128_000;

    /// <summary>
    /// Used by LocalChatProvider when a local OpenAI-compatible server's /models response omits
    /// context_length (the common case — LM Studio's plain OpenAI-compatible endpoint doesn't
    /// include it; that only appears on its separate, vendor-specific /api/v0/models), and by any
    /// UI fast path that resolves a "local" provider's context length without a ListModelsAsync
    /// round-trip (e.g. a /model &lt;id&gt; command). Deliberately conservative rather than
    /// FallbackContextLength above (128K, calibrated for hosted models): guessing too high here
    /// silently defeats the context meter and compaction for exactly the small local models most
    /// likely to hit this fallback, which is the failure mode this exists to prevent.
    /// 8_000 (the original guess) left only a 2_000-token gap between "compaction just fired" and
    /// "assumed full" (CompactionSettings.ForContextWindow caps ReserveTokens/KeepRecentTokens at
    /// contextWindowTokens/4) — observed live to be smaller than a single large tool result (e.g.
    /// a real file read), so the very next tool-calling round after a compaction could still blow
    /// straight past the assumed window before compaction got another chance to run. 32_000 gives
    /// an 8_000-token gap, comfortably covering that case, and better matches the context lengths
    /// most current local models (Llama 3.1, Qwen3, etc.) actually default-serve at in LM Studio/
    /// Ollama — still far below FallbackContextLength for the same reason as above: this is a
    /// floor to keep compaction functional, not an attempt to guess a specific model's real window.
    /// </summary>
    public const int LocalFallbackContextLength = 32_000;

    // Ordered longest/most-specific prefix first within each provider family, since e.g.
    // "o4-mini" and "gpt-4.1" must not be shadowed by a shorter, coarser prefix.
    private static readonly (string Prefix, int ContextLength)[] KnownPrefixes =
    [
        // Anthropic — modern Claude models span 200K to 1M depending on family/tier.
        ("claude-haiku", 200_000),
        ("claude-3", 200_000),
        ("claude-opus", 200_000),
        ("claude-sonnet-4-5", 200_000),
        ("claude-sonnet", 1_000_000),

        // OpenAI reasoning models.
        ("o4-mini", 200_000),
        ("o3", 200_000),
        ("o1", 200_000),

        // OpenAI GPT models.
        ("gpt-4o", 128_000),
        ("gpt-4.1", 1_000_000),
        ("gpt-5", 1_000_000),

        // Gemini — Flash is 1M except the newest low-latency 3.x Flash tier, which is 200K.
        ("gemini-3-flash", 200_000),
        ("gemini-3.5-flash", 1_000_000),
        ("gemini-2.5-flash", 1_000_000),
        ("gemini", 1_000_000),
    ];

    public static int Resolve(string modelId)
    {
        foreach (var (prefix, contextLength) in KnownPrefixes)
            if (modelId.Contains(prefix, StringComparison.OrdinalIgnoreCase))
                return contextLength;

        return FallbackContextLength;
    }
}
