using Litos.Agent.Messages;
using Litos.Agent.Tools;

namespace Litos.Agent.Providers;

public sealed record ChatRequest(
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<ToolSchema> Tools,
    string Model,
    string? SystemPrompt = null,
    double? Temperature = null,
    int? MaxOutputTokens = null);
