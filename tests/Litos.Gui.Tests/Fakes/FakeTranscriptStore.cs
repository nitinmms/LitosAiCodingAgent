using Litos.Agent.Session;

namespace Litos.Gui.Tests.Fakes;

/// <summary>In-memory ITranscriptStore for tests — local copy of the same-shaped fake other test
/// projects keep.</summary>
public sealed class FakeTranscriptStore : ITranscriptStore
{
    private readonly List<TranscriptEntry> _entries = [];

    public Task AppendAsync(SessionOwner owner, string sessionId, TranscriptEntry entry, CancellationToken ct)
    {
        _entries.Add(entry);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<TranscriptEntry> ReadAsync(
        SessionOwner owner, string sessionId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var entry in _entries)
            yield return entry;
        await Task.CompletedTask;
    }

    public Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(SessionOwner owner, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SessionSummary>>([]);

    public Task<string> BranchAsync(SessionOwner owner, string sourceSessionId, int uptoEntryIndex, CancellationToken ct) =>
        Task.FromResult(Guid.NewGuid().ToString("n"));

    public string GetScratchDirectory(SessionOwner owner, string sessionId) =>
        $"/fake-scratch/{owner.Value}/{sessionId}";
}
