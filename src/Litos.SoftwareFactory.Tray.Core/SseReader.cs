using System.Runtime.CompilerServices;
using System.Text;

namespace Litos.SoftwareFactory.Tray;

/// <summary>
/// One block of a Server-Sent Events stream. <see cref="Data"/> is null for a block with no data
/// lines, such as the id-only blocks the board stream sends to move a reader past events it may
/// not see (EventStream.cs): such a block still moves the cursor.
/// </summary>
public sealed record SseEvent(string? Id, string Type, string? Data);

/// <summary>
/// Reads Server-Sent Events (the WHATWG event-stream format) as the factory's board stream writes
/// them. Comment lines (the host's ": keep-alive") are skipped, but they count as the stream being
/// alive: if no line at all arrives within <c>idleTimeout</c>, the connection is taken as dead (a
/// laptop that slept, a VPN that dropped) and a <see cref="TimeoutException"/> ends the read, so
/// the caller reconnects instead of waiting forever on a half-open socket.
/// </summary>
public static class SseReader
{
    public static async IAsyncEnumerable<SseEvent> ReadAsync(Stream stream, TimeSpan idleTimeout, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);

        string? id = null;
        var type = "message";
        StringBuilder? data = null;
        var any = false;

        while (true)
        {
            string? line;
            idle.CancelAfter(idleTimeout);
            try
            {
                line = await reader.ReadLineAsync(idle.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"The factory sent nothing for {idleTimeout.TotalSeconds:0} seconds.");
            }

            // Reset the timer for the next line; a source that cannot be reset has fired already.
            if (!idle.TryReset())
                throw new TimeoutException($"The factory sent nothing for {idleTimeout.TotalSeconds:0} seconds.");

            if (line is null)
                yield break;

            if (line.Length == 0)
            {
                if (any)
                    yield return new SseEvent(id, type, data?.ToString());
                id = null;
                type = "message";
                data = null;
                any = false;
                continue;
            }

            if (line[0] == ':')
                continue;

            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon + 1)..];
            if (value.StartsWith(' '))
                value = value[1..];

            switch (field)
            {
                case "id":
                    id = value;
                    any = true;
                    break;
                case "event":
                    type = value;
                    any = true;
                    break;
                case "data":
                    data = data is null ? new StringBuilder(value) : data.Append('\n').Append(value);
                    any = true;
                    break;
            }
        }
    }
}
