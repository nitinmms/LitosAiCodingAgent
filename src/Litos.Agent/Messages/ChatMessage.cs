using Litos.Agent.Tools;

namespace Litos.Agent.Messages;

public sealed record ChatMessage(Role Role, IReadOnlyList<ContentBlock> Content)
{
    public static ChatMessage User(string text) =>
        new(Role.User, [new TextBlock(text)]);

    public static ChatMessage User(IReadOnlyList<ContentBlock> content) =>
        new(Role.User, content);

    public static ChatMessage Assistant(IReadOnlyList<ContentBlock> content) =>
        new(Role.Assistant, content);

    public static ChatMessage ToolResult(string callId, ToolResult result) =>
        new(Role.User, [new ToolResultBlock(callId, result.Text, result.IsError)]);

    public static ChatMessage CompactionSummary(string summary, int tokensBefore) =>
        new(Role.User, [new CompactionSummaryBlock(summary, tokensBefore)]);
}
