using Litos.Agent.Providers;
using Litos.Agent.Tools;

namespace Litos.Agent.Session;

public sealed class ContextAccountant
{
    /// <param name="sessionId">
    /// Flows to providers that can use a stable per-conversation key — currently OpenRouter, whose
    /// session_id both pins sticky routing to one upstream (so a prompt cache written on an earlier
    /// round is actually reachable on the next) and groups the session in its Logs view. Optional:
    /// providers that have no such concept ignore it.
    /// </param>
    public ChatRequest BuildRequest(Transcript transcript, IReadOnlyList<ToolSchema> tools, string model, string? systemPrompt, string? sessionId = null) =>
        new(transcript.Messages, tools, model, systemPrompt, SessionId: sessionId);
}
