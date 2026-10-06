using Litos.Agent.Messages;
using Litos.Agent.Tools;

namespace Litos.Agent.Providers;

/// <param name="PreferredUpstream">
/// For a provider that routes calls to one of several upstreams: the upstream to try first, so a
/// conversation stays where its prompt cache is (UsageInfo.ServedBy says who served a call). A
/// preference, never a requirement: a provider that cannot honour it, or does not route, ignores
/// it, and a router still falls back to another upstream rather than fail the call.
/// </param>
public sealed record ChatRequest(
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<ToolSchema> Tools,
    string Model,
    string? SystemPrompt = null,
    double? Temperature = null,
    int? MaxOutputTokens = null,
    string? SessionId = null,
    string? PreferredUpstream = null);
