using Litos.Agent.Session;

namespace Litos.VsCodeHost.Tests.Fakes;

/// <summary>Local copy of Litos.Api.Tests/Fakes/FakeTranscriptStore.cs — see FakeChatProvider's own note on why each test project keeps its own fakes.</summary>
public sealed class FakeTranscriptStore : ITranscriptStore
{
    private readonly Dictionary<(SessionOwner Owner, string SessionId), List<TranscriptEntry>> _entries = [];

    public Task AppendAsync(SessionOwner owner, string sessionId, TranscriptEntry entry, CancellationToken ct)
    {
        var key = (owner, sessionId);
        if (!_entries.TryGetValue(key, out var list))
            _entries[key] = list = [];

        list.Add(entry);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<TranscriptEntry> ReadAsync(
        SessionOwner owner, string sessionId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (!_entries.TryGetValue((owner, sessionId), out var list))
            yield break;

        foreach (var entry in list)
        {
            ct.ThrowIfCancellationRequested();
            yield return entry;
            await Task.Yield();
        }
    }

    public Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(SessionOwner owner, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SessionSummary>>(
            [.. _entries.Keys.Where(k => k.Owner == owner)
                .Select(k => new SessionSummary(k.SessionId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, _entries[k].Count))]);

    public Task<string> BranchAsync(SessionOwner owner, string sourceSessionId, int uptoEntryIndex, CancellationToken ct) =>
        throw new NotSupportedException("Not needed by AgentWorker tests.");

    public string GetScratchDirectory(SessionOwner owner, string sessionId) =>
        $"/fake-scratch/{owner.Value}/{sessionId}";
}
