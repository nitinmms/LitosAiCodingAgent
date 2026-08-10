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

public sealed record UsageInfo(int InputTokens, int OutputTokens);
