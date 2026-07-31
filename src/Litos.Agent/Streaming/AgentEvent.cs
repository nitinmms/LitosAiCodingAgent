using System.Text.Json;
using Litos.Agent.Messages;
using Litos.Agent.Tools;

namespace Litos.Agent.Streaming;

public abstract record AgentEvent;

public sealed record TextDelta(string Text) : AgentEvent;

public sealed record ToolCallStarted(string CallId, string ToolName) : AgentEvent;

public sealed record ToolCallArgsDelta(string CallId, string JsonFragment) : AgentEvent;

public sealed record ToolCallCompleted(string CallId, string ToolName, JsonElement Arguments) : AgentEvent;

public sealed record ToolCallResult(string CallId, string ToolName, ToolResult Result) : AgentEvent;

public sealed record MessageCompleted(ChatMessage Message, UsageInfo Usage) : AgentEvent;

public sealed record ErrorOccurred(Exception Exception) : AgentEvent;

public sealed record CompactionOccurred(int TokensBefore) : AgentEvent;

public sealed record ToolCallSkipped(string CallId, string Reason) : AgentEvent;

public sealed record UsageInfo(int InputTokens, int OutputTokens);
