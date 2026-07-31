namespace Litos.Agent.Session;

public interface ITranscriptStore
{
    Task AppendAsync(SessionOwner owner, string sessionId, TranscriptEntry entry, CancellationToken ct);

    IAsyncEnumerable<TranscriptEntry> ReadAsync(SessionOwner owner, string sessionId, CancellationToken ct);

    Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(SessionOwner owner, CancellationToken ct);

    Task<string> BranchAsync(SessionOwner owner, string sourceSessionId, int uptoEntryIndex, CancellationToken ct);
}
