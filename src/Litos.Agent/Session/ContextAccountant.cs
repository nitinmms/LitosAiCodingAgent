using Litos.Agent.Providers;
using Litos.Agent.Tools;

namespace Litos.Agent.Session;

public sealed class ContextAccountant
{
    public ChatRequest BuildRequest(Transcript transcript, IReadOnlyList<ToolSchema> tools, string model, string? systemPrompt) =>
        new(transcript.Messages, tools, model, systemPrompt);
}
