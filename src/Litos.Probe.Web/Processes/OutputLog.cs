namespace Litos.Probe.Web.Processes;

public enum OutputStream { Out, Err }

/// <param name="Sequence">Increases by one per line, for <see cref="OutputLog.Since"/>.</param>
public sealed record OutputLine(long Sequence, OutputStream Stream, string Text);

/// <summary>
/// The last lines a process wrote, both streams interleaved in arrival order. A check takes a
/// <see cref="Mark"/> before it acts and reads <see cref="Since"/> afterwards, which is how a
/// server error line is tied to the page visit that caused it.
/// </summary>
public sealed class OutputLog(int capacity = 5_000)
{
    private readonly LinkedList<OutputLine> _lines = new();
    private readonly Lock _lock = new();
    private long _next;

    public void Append(OutputStream stream, string text)
    {
        lock (_lock)
        {
            _lines.AddLast(new OutputLine(_next++, stream, text));
            if (_lines.Count > capacity)
                _lines.RemoveFirst();
        }
    }

    public long Mark()
    {
        lock (_lock)
            return _next;
    }

    public IReadOnlyList<OutputLine> Since(long mark)
    {
        lock (_lock)
            return [.. _lines.Where(l => l.Sequence >= mark)];
    }

    public IReadOnlyList<OutputLine> Tail(int count)
    {
        lock (_lock)
            return [.. _lines.Skip(Math.Max(0, _lines.Count - count))];
    }
}
