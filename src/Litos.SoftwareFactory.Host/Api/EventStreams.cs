using System.Collections.Concurrent;
using Litos.SoftwareFactory.Core.Ports;

namespace Litos.SoftwareFactory.Host.Api;

/// <summary>
/// The event streams this host has open, and when each last caught up with the outbox (m4-architecture.md
/// §7). Streams read the outbox themselves, so nothing queues to be published; the backlog worth watching
/// is a stream that has fallen behind, which shows as a long time since it last read a page that was not full.
/// </summary>
public sealed class EventStreams(IClock clock)
{
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _caughtUp = new();

    private DateTimeOffset Now => clock.UtcNow;

    /// <summary>A stream opened; dispose the result when it closes.</summary>
    public Stream Open()
    {
        var stream = new Stream(this, Guid.NewGuid());
        _caughtUp[stream.Id] = Now;
        return stream;
    }

    /// <summary>How many are open, and how long since the slowest caught up; zero with none.</summary>
    public (int Open, TimeSpan SlowestBehind) Snapshot()
    {
        var times = _caughtUp.Values.ToList();
        return (times.Count, times.Count == 0 ? TimeSpan.Zero : Now - times.Min());
    }

    public sealed class Stream(EventStreams owner, Guid id) : IDisposable
    {
        public Guid Id => id;

        /// <summary>It read everything there was to read.</summary>
        public void CaughtUp() => owner._caughtUp[id] = owner.Now;

        public void Dispose() => owner._caughtUp.TryRemove(id, out _);
    }
}
