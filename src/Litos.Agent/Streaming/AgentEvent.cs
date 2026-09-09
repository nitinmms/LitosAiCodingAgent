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

public sealed record UsageInfo(int InputTokens, int OutputTokens);
