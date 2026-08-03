namespace Litos.Api.Logs;

/// <summary>
/// Thread-safe in-memory ring buffer of Warning-and-above log entries, backing the /logs admin
/// page (Logs.razor). Cleared on restart — this is a live-diagnostics view, not persisted
/// history, mirroring TelegramConfigStore's constructor-injected/DI-singleton shape but without
/// TelegramConfigStore's file-backed persistence since there's nothing worth surviving a restart
/// for here.
/// </summary>
public sealed class LogStore
{
    private const int Capacity = 500;

    private readonly Lock _lock = new();
    private readonly Queue<LogEntry> _entries = new();

    public event Action<LogEntry>? Added;

    public void Add(LogEntry entry)
    {
        lock (_lock)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > Capacity)
                _entries.Dequeue();
        }

        Added?.Invoke(entry);
    }

    public IReadOnlyList<LogEntry> List()
    {
        lock (_lock) return [.. _entries];
    }
}
