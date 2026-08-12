namespace Litos.Examples.BlazorChat.LitosClient;

/// <summary>
/// Mirrors the three outcomes POST /sessions/{id}/turns can produce (TurnsEndpoints.StartOrSteer):
/// a fresh turn started and is streaming, or an already-running turn absorbed this request either
/// by steering (text merged into the live turn) or queuing (attachments held for the next turn).
/// </summary>
public abstract record TurnOutcome
{
    public sealed record Started(IAsyncEnumerable<AgentEventDto> Events) : TurnOutcome;

    public sealed record Steered(string Message) : TurnOutcome;

    public sealed record Queued(string Message) : TurnOutcome;
}
