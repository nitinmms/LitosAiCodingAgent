using System.Runtime.CompilerServices;
using System.Text;

namespace Litos.Examples.BlazorChat.LitosClient;

/// <summary>
/// Minimal Server-Sent Events reader for the "agent-event" stream POST /sessions/{id}/turns
/// returns when it starts a new turn. .NET has no built-in client-side SSE parser (the
/// System.Net.ServerSentEvents type Litos.Api itself uses is a *server*-side writer), so this
/// reads the wire format directly per the SSE spec: events are separated by a blank line, each
/// event is one or more "field: value" lines, and only the "data" field is used here.
/// </summary>
public static class AgentEventStreamParser
{
    public static async IAsyncEnumerable<AgentEventDto> ReadEventsAsync(
        Stream responseStream, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(responseStream, Encoding.UTF8);
        var dataBuilder = new StringBuilder();

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0)
            {
                if (dataBuilder.Length > 0)
                {
                    yield return AgentEventDto.Parse(dataBuilder.ToString());
                    dataBuilder.Clear();
                }
                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                // A single-line JSON payload per event (TurnsEndpoints serializes with
                // WriteIndented = false), but SSE allows multiple "data:" lines per event joined
                // by '\n' — handled here in case that ever changes server-side.
                if (dataBuilder.Length > 0)
                    dataBuilder.Append('\n');
                dataBuilder.Append(line.AsSpan(5).TrimStart());
            }
            // "event:"/"id:"/"retry:" lines and comment lines (starting with ':') are ignored —
            // this example only cares about the event payload, and TurnsEndpoints always sends
            // event type "agent-event" for every event on this stream.
        }

        if (dataBuilder.Length > 0)
            yield return AgentEventDto.Parse(dataBuilder.ToString());
    }
}
