namespace Litos.Agent.Session;

public interface ITranscriptStore
{
    Task AppendAsync(SessionOwner owner, string sessionId, TranscriptEntry entry, CancellationToken ct);

    IAsyncEnumerable<TranscriptEntry> ReadAsync(SessionOwner owner, string sessionId, CancellationToken ct);

    Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(SessionOwner owner, CancellationToken ct);

    Task<string> BranchAsync(SessionOwner owner, string sourceSessionId, int uptoEntryIndex, CancellationToken ct);

    /// <summary>
    /// Where a kernel-mode script's own file writes should land — under the session's storage
    /// root, never inside the user's project (ReadMe_PTCPersistentKernel.md §4.5). Does not create
    /// the directory; callers create it lazily on first use the same way AppendAsync already does
    /// for a session's transcript file.
    /// </summary>
    string GetScratchDirectory(SessionOwner owner, string sessionId);
}
