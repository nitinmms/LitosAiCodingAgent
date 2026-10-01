using System.Text;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Host.Runs;

namespace Litos.SoftwareFactory.Host.Api;

/// <summary>
/// Live updates for a thread as Server-Sent Events, backed by the outbox
/// (ReadMe_LitosSoftwareFactory_V1.md §12). Every event carries its durable sequence number as
/// the SSE id, so a client that reconnects — the browser sends Last-Event-ID by itself — replays
/// everything after the last event it saw and misses nothing.
/// </summary>
public static class EventStream
{
    private const int PageSize = 200;

    public static IEndpointRouteBuilder MapFactoryEvents(this IEndpointRouteBuilder app, TimeSpan? keepAlive = null)
    {
        var idle = keepAlive ?? TimeSpan.FromSeconds(15);

        app.MapGet("/api/threads/{id:guid}/events", async (
            Guid id, long? after, HttpContext context, IFactoryStore store, FactorySignals signals, CancellationToken ct) =>
        {
            if (await store.GetThreadAsync(id, ct) is null)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            // An explicit ?after= wins; otherwise the browser's own reconnect header.
            var cursor = after
                ?? (long.TryParse(context.Request.Headers["Last-Event-ID"], out var lastSeen) ? lastSeen : 0);

            context.Response.Headers.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            // Tell a reverse proxy not to buffer the stream (§13.3).
            context.Response.Headers["X-Accel-Buffering"] = "no";
            await context.Response.Body.FlushAsync(ct);

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    // Taken before reading, so an event written between the read and the wait
                    // still wakes this loop.
                    var woken = signals.NextEvents;
                    var events = await store.ReadEventsAsync(id, cursor, PageSize, ct);
                    foreach (var evt in events)
                    {
                        await context.Response.WriteAsync(Format(evt), ct);
                        cursor = evt.Sequence;
                    }

                    if (events.Count > 0)
                    {
                        await context.Response.Body.FlushAsync(ct);
                        if (events.Count == PageSize)
                            continue; // more are waiting
                    }

                    if (await Task.WhenAny(woken, Task.Delay(idle, ct)) != woken)
                    {
                        // A comment line: keeps the connection open through proxies, ignored by clients.
                        await context.Response.WriteAsync(": keep-alive\n\n", ct);
                        await context.Response.Body.FlushAsync(ct);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // The client went away.
            }
        }).RequireAuthorization();

        return app;
    }

    /// <summary>One event in SSE framing. The payload is a single line of JSON.</summary>
    internal static string Format(OutboxEvent evt) => new StringBuilder()
        .Append("id: ").Append(evt.Sequence).Append('\n')
        .Append("event: ").Append(evt.Type).Append('\n')
        .Append("data: ").Append(evt.PayloadJson.ReplaceLineEndings(" ")).Append("\n\n")
        .ToString();
}
