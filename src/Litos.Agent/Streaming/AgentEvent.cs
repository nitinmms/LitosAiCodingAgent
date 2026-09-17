using System.Text.Json;
using Litos.Agent.Messages;
using Litos.Agent.Tools;

namespace Litos.Agent.Streaming;

public abstract record AgentEvent;

public sealed record TextDelta(string Text) : AgentEvent;

// Chain-of-thought from a "thinking" model (Qwen3, DeepSeek-R1, QwQ, ...), streamed separately
// from TextDelta so a UI can render it distinctly (e.g. muted/italic) and so consumers that
// accumulate TextDelta into a persisted message (AgentLoop, Compactor, Reflector) never do the
// same for reasoning — it has no business being replayed back to the model as if it were part
// of a past reply.
public sealed record ReasoningDelta(string Text) : AgentEvent;

public sealed record ToolCallStarted(string CallId, string ToolName) : AgentEvent;

public sealed record ToolCallArgsDelta(string CallId, string JsonFragment) : AgentEvent;

public sealed record ToolCallCompleted(string CallId, string ToolName, JsonElement Arguments) : AgentEvent;

public sealed record ToolCallResult(string CallId, string ToolName, ToolResult Result) : AgentEvent;

public sealed record MessageCompleted(ChatMessage Message, UsageInfo Usage) : AgentEvent;

public sealed record ErrorOccurred(Exception Exception) : AgentEvent;

public sealed record CompactionOccurred(int TokensBefore) : AgentEvent;

public sealed record ToolCallSkipped(string CallId, string Reason) : AgentEvent;

/// <summary>
/// Signals that the underlying connection is still alive and receiving data, without carrying any
/// content of its own — e.g. a raw SSE line from a local server that isn't parseable content (a
/// keep-alive comment, a chunk with no delta). AgentLoop's own idle-gap timeout is measured per
/// yielded AgentEvent (see MoveNextWithIdleTimeoutAsync), not per raw network byte — a provider
/// that silently skips such lines internally, never yielding anything while it does, leaves that
/// timer blind to real, ongoing server activity for however long the skipping continues. That's
/// exactly the gap this event closes: a "thinking" local model can go a long stretch producing no
/// actual content while still very much alive, and without this, AgentLoop has no way to tell that
/// apart from a genuinely stalled connection. AgentLoop discards it immediately on receipt — never
/// persisted, never forwarded to a face — purely to reset its own idle timer.
/// </summary>
public sealed record StreamHeartbeat : AgentEvent;

/// <summary>
/// Token accounting for one assistant response. InputTokens/OutputTokens are the billed
/// non-cached counts.
///
/// CacheCreationInputTokens/CacheReadInputTokens are the prompt-caching split, reported by
/// providers that support it (Anthropic explicitly; OpenAI and Gemini cache automatically but
/// surface the split differently, and currently leave these zero here). They are *not* added into
/// InputTokens: Anthropic already reports InputTokens as the non-cached remainder, so summing
/// them would double-count what the provider already separated. The three counts are mutually
/// exclusive: InputTokens covers only what follows the last cache breakpoint, so once caching is
/// active it is *not* a measure of how full the context window is. Anything reasoning about window
/// occupancy (CompactionPlanner.EstimatedTokensUsed, ContextBreakdown) must use TotalInputTokens;
/// InputTokens alone remains the right figure for billed, non-cached cost.
/// </summary>
public sealed record UsageInfo(int InputTokens, int OutputTokens, int CacheCreationInputTokens = 0, int CacheReadInputTokens = 0)
{
    /// <summary>
    /// Every input token that occupied the context window this turn, cached or not — the figure
    /// context-window accounting needs, as opposed to the billed-token figure InputTokens alone
    /// represents once caching is active.
    /// </summary>
    public int TotalInputTokens => InputTokens + CacheCreationInputTokens + CacheReadInputTokens;
}
