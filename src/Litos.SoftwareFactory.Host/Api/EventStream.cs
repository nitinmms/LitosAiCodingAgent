using System.Text;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Host.Auth;
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

            var cursor = StartCursor(after, context.Request.Headers["Last-Event-ID"]);
            StartStream(context);
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
        }).RequireAuthorization().RequireProjectAccess(ProjectScoped.Thread);

        // The board's stream (m2-architecture.md §6): every thread change in the projects the
        // user can see, as the thread's whole current view, so a board never has to re-fetch.
        // Who can see what is asked again on every pass, so a membership change takes effect.
        app.MapGet("/api/events", async (
            long? after, HttpContext context, IFactoryStore store, ProjectAccess access, FactorySignals signals, CancellationToken ct) =>
        {
            var cursor = StartCursor(after, context.Request.Headers["Last-Event-ID"]);
            StartStream(context);
            await context.Response.Body.FlushAsync(ct);

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var woken = signals.NextEvents;
                    var visible = await access.VisibleProjectsAsync(context.User, ct);
                    var events = await store.ReadBoardEventsAsync(visible, cursor, PageSize, ct);

                    // A thread changed several times in one page is sent once, as it is now.
                    foreach (var latest in events.GroupBy(e => e.ThreadId).Select(g => g.Last()).OrderBy(e => e.Sequence))
                    {
                        if (await store.FindThreadAsync(latest.ThreadId, ct) is { } thread)
                            await context.Response.WriteAsync(FormatThread(latest.Sequence, thread), ct);
                    }

                    if (events.Count > 0)
                    {
                        cursor = events[^1].Sequence;
                        // An id-only comment moves Last-Event-ID past events this user may not see.
                        await context.Response.WriteAsync($"id: {cursor}\n\n", ct);
                        await context.Response.Body.FlushAsync(ct);
                        if (events.Count == PageSize)
                            continue;
                    }

                    if (await Task.WhenAny(woken, Task.Delay(idle, ct)) != woken)
                    {
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

    private static void StartStream(HttpContext context)
    {
        context.Response.Headers.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        // Tell a reverse proxy not to buffer the stream (§13.3).
        context.Response.Headers["X-Accel-Buffering"] = "no";
    }

    /// <summary>A board event: the thread's whole current view, as the API returns it.</summary>
    internal static string FormatThread(long sequence, TaskThread thread) => new StringBuilder()
        .Append("id: ").Append(sequence).Append('\n')
        .Append("event: thread\n")
        .Append("data: ").Append(System.Text.Json.JsonSerializer.Serialize(FactoryApi.ThreadView(thread), System.Text.Json.JsonSerializerOptions.Web)).Append("\n\n")
        .ToString();

    /// <summary>
    /// Where a stream starts: after the later of ?after= and Last-Event-ID. A browser that
    /// reconnects by itself repeats the original URL, so its ?after= is where it first started
    /// and its header is where it actually got to; taking the later one replays nothing twice.
    /// </summary>
    internal static long StartCursor(long? after, string? lastEventId) =>
        Math.Max(after ?? 0, long.TryParse(lastEventId, out var lastSeen) ? lastSeen : 0);

    /// <summary>One event in SSE framing. The payload is a single line of JSON.</summary>
    internal static string Format(OutboxEvent evt) => new StringBuilder()
        .Append("id: ").Append(evt.Sequence).Append('\n')
        .Append("event: ").Append(evt.Type).Append('\n')
        .Append("data: ").Append(evt.PayloadJson.ReplaceLineEndings(" ")).Append("\n\n")
        .ToString();
}
