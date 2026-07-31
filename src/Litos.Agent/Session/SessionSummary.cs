namespace Litos.Agent.Session;

public sealed record SessionSummary(
    string SessionId,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastUpdatedAt,
    string? FirstUserMessagePreview,
    int MessageCount);
