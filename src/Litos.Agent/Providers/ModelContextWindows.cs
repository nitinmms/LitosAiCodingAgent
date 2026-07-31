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
