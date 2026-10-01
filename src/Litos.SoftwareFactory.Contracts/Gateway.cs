using System.Text.Json;
using System.Text.Json.Serialization;
using Litos.Agent.Messages;
using Litos.Agent.Providers;
using Litos.Agent.Streaming;

namespace Litos.SoftwareFactory.Contracts;

/// <summary>
/// Body of POST /internal/runs/{runId}/gateway — one model call, sent by the worker and made by
/// the host, which holds the provider keys. RequestKey is unique per call so a repeated request
/// can never be charged twice (ReadMe_LitosSoftwareFactory_V1.md §9.2).
/// </summary>
public sealed record GatewayRequest(string RequestKey, ChatRequest ChatRequest);

/// <summary>
/// One line of the gateway's NDJSON response. A closed union mirroring what a provider emits,
/// plus an error the worker can tell apart by code.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "event")]
[JsonDerivedType(typeof(GatewayTextDelta), "text_delta")]
[JsonDerivedType(typeof(GatewayReasoningDelta), "reasoning_delta")]
[JsonDerivedType(typeof(GatewayToolCallStarted), "tool_call_started")]
[JsonDerivedType(typeof(GatewayToolCallArgsDelta), "tool_call_args_delta")]
[JsonDerivedType(typeof(GatewayToolCallCompleted), "tool_call_completed")]
[JsonDerivedType(typeof(GatewayMessageCompleted), "message_completed")]
[JsonDerivedType(typeof(GatewayHeartbeat), "heartbeat")]
[JsonDerivedType(typeof(GatewayError), "error")]
public abstract record GatewayEvent
{
    /// <summary>
    /// Maps a provider event onto the wire. Returns null for events a provider never emits
    /// (tool results, compaction and steering notices belong to the agent loop, which runs in
    /// the worker, not behind the gateway).
    /// </summary>
    public static GatewayEvent? FromAgentEvent(AgentEvent evt) => evt switch
    {
        TextDelta e => new GatewayTextDelta(e.Text),
        ReasoningDelta e => new GatewayReasoningDelta(e.Text),
        ToolCallStarted e => new GatewayToolCallStarted(e.CallId, e.ToolName),
        ToolCallArgsDelta e => new GatewayToolCallArgsDelta(e.CallId, e.JsonFragment),
        ToolCallCompleted e => new GatewayToolCallCompleted(e.CallId, e.ToolName, e.Arguments),
        MessageCompleted e => new GatewayMessageCompleted(e.Message, e.Usage),
        StreamHeartbeat => new GatewayHeartbeat(),
        ErrorOccurred e => new GatewayError(GatewayErrorCodes.ProviderError, e.Exception.Message),
        _ => null,
    };
}

public sealed record GatewayTextDelta(string Text) : GatewayEvent;

public sealed record GatewayReasoningDelta(string Text) : GatewayEvent;

public sealed record GatewayToolCallStarted(string CallId, string ToolName) : GatewayEvent;

public sealed record GatewayToolCallArgsDelta(string CallId, string JsonFragment) : GatewayEvent;

public sealed record GatewayToolCallCompleted(string CallId, string ToolName, JsonElement Arguments) : GatewayEvent;

public sealed record GatewayMessageCompleted(ChatMessage Message, UsageInfo Usage) : GatewayEvent;

/// <summary>Sent while the host waits (on the provider, or on a rate limit with the reservation
/// kept) so the worker's stream-idle watchdog doesn't mistake waiting for a dead connection.</summary>
public sealed record GatewayHeartbeat : GatewayEvent;

public sealed record GatewayError(string Code, string Message) : GatewayEvent;

public static class GatewayErrorCodes
{
    /// <summary>The call did not fit the task's remaining allowance; nothing was sent.</summary>
    public const string BudgetExhausted = "budget_exhausted";

    /// <summary>The call did not fit the user's quota; nothing was sent.</summary>
    public const string QuotaExhausted = "quota_exhausted";

    /// <summary>The provider failed. The call may have incurred usage.</summary>
    public const string ProviderError = "provider_error";

    /// <summary>True when the host refused the call before sending it — the run is already
    /// paused, and retrying would only be refused again.</summary>
    public static bool IsRefusal(string code) => code is BudgetExhausted or QuotaExhausted;
}
