using System.Text.Json;
using System.Threading.Channels;
using Litos.Agent.Messages;
using Litos.Agent.Session;
using Litos.Agent.Streaming;
using Litos.Tools.Shell;
using Litos.VsCodeHost.Approvals;

namespace Litos.VsCodeHost.Turns;

/// <summary>
/// Litos.Api's Turns/TurnsEndpoints.cs, stripped for a local, single-user, no-auth host: every
/// request acts as SessionOwner.Local (no ClaimsPrincipal, no CurrentSessionOwner), no
/// [RequireAuthorization], and no multipart/attachment path (JSON text-only turns for v1 — see
/// ReadMe_VsCodeExtension.md for the attachment-support follow-up). The SSE construction, turn
/// lifecycle, and wire format are otherwise identical, so the AngularChat-derived TypeScript
/// client (Litos.VsCode's sseClient.ts) parses this exactly like Litos.Api's own stream, plus one
/// addition Litos.Api doesn't have: PendingApprovalRequested/Resolved wire events for MCP tools
/// gated Ask by McpAwareApprovalGate (see Program.cs), merged onto the same stream — see ToSseData.
/// </summary>
public static class TurnsEndpoints
{
    public static IEndpointRouteBuilder MapTurnsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/sessions/{id}/cancel", (string id, AgentWorker worker) =>
            worker.CancelTurn(SessionOwner.Local, id)
                ? Results.Ok()
                : Results.NotFound("No turn is currently running for this session."));

        app.MapPost("/sessions/{id}/approvals/{approvalId}/resolve", (
            string id, Guid approvalId, ResolveApprovalRequest request, PendingApprovalStore approvals) =>
        {
            var resolved = approvals.Resolve(approvalId, request.Decision);
            return resolved ? Results.Ok() : Results.NotFound("No pending approval with that id (it may have already resolved or timed out).");
        });

        app.MapGet("/sessions", async (Litos.Agent.Session.ITranscriptStore store, CancellationToken ct) =>
        {
            var sessions = await store.ListSessionsAsync(SessionOwner.Local, ct);
            return Results.Ok(sessions.OrderByDescending(s => s.LastUpdatedAt));
        });

        app.MapGet("/sessions/{id}/history", async (string id, Litos.Agent.Session.ITranscriptStore store, CancellationToken ct) =>
        {
            var messages = new List<object>();
            await foreach (var entry in store.ReadAsync(SessionOwner.Local, id, ct))
            {
                if (entry.Message is null)
                    continue;

                // Only the FIRST TextBlock is the user's own typed message — /sessions/{id}/turns
                // always builds a turn's content as [TextBlock(typed input), ...attachments] (see
                // that endpoint's own comment), so any TextBlock after the first is a document
                // attachment's converted content (UntrustedContent-wrapped Markdown, potentially
                // huge), not something the user wrote. Concatenating every TextBlock here used to
                // glue that wrapped document text directly onto the displayed message on history
                // replay — fixed by only ever taking the first and folding every TextBlock past it
                // into the same attachment count ImageBlock already contributes to.
                var textBlocks = entry.Message.Content.OfType<Litos.Agent.Messages.TextBlock>().ToList();
                var text = textBlocks.Count > 0 ? textBlocks[0].Text : "";
                var attachments = entry.Message.Content.OfType<Litos.Agent.Messages.ImageBlock>().Count() + Math.Max(0, textBlocks.Count - 1);
                if (entry.Message.Role == Litos.Agent.Messages.Role.User &&
                    entry.Message.Content.OfType<Litos.Agent.Messages.ToolResultBlock>().Any())
                    continue;

                messages.Add(new { role = entry.Message.Role.ToString().ToLowerInvariant(), text, attachments, timestamp = entry.Timestamp });
            }
            return Results.Ok(messages);
        });

        app.MapPost("/sessions/{id}/turns", async (
            string id, HttpRequest request, AgentWorker worker, PendingApprovalRelay approvalRelay, CancellationToken requestAborted) =>
        {
            var turnRequest = await request.ReadFromJsonAsync<TurnRequest>(requestAborted)
                ?? throw new BadHttpRequestException("Request body is required.");

            // Attachments (from /attach's file picker or clipboard paste) travel as opaque
            // AttachedContent the extension already fetched from /attachments/from-{path,bytes} —
            // this endpoint just converts each back to the ContentBlock AgentLoop needs and folds
            // it in alongside the typed text, same order Litos.Api's AttachmentContentBuilder uses
            // (leading text, then attachments).
            List<ContentBlock> content = [new TextBlock(turnRequest.Input)];
            if (turnRequest.Attachments is { Count: > 0 } attachments)
                content.AddRange(attachments.Select(AttachEndpoints.ToContentBlock));

            var events = worker.StartOrSteerTurn(SessionOwner.Local, id, content, requestAborted, out var outcome);

            return outcome switch
            {
                TurnOutcome.Steered => Results.Accepted(value: "Message delivered to the in-progress turn."),
                TurnOutcome.Started => TypedResults.ServerSentEvents(ToSseData(id, events!, approvalRelay, requestAborted), eventType: "agent-event"),
                _ => Results.Problem("Unknown turn outcome."),
            };
        });

        return app;
    }

    // Merges the turn's own AgentEvent channel with this session's PendingApprovalRelay
    // subscription onto one SSE stream — an MCP tool call gated Ask blocks inside AgentLoop (a
    // ShellTool/McpToolProxy call awaiting approvalGate.RequestAsync) without producing any
    // AgentEvent of its own until it resolves, so without this merge the webview would see the
    // stream go silent with no indication a decision is needed. Both sources funnel into one
    // internal Channel<string> (already-serialized JSON, so ToSseData doesn't need to know either
    // payload shape) and this yields from that shared channel, matching Litos.Api's own
    // ToSseData's reason for yielding raw JSON strings rather than SseItem<string> — the
    // (IAsyncEnumerable<SseItem<T>>, eventType) overload serializes the wrapper itself instead of
    // writing .Data as the "data:" line.
    /// <param name="keepAliveInterval">
    /// Overridable only so tests don't have to sit through the real KeepAliveInterval to observe
    /// a keep-alive; production callers leave it null.
    /// </param>
    internal static async IAsyncEnumerable<string> ToSseData(
        string sessionId, ChannelReader<AgentEvent> events, PendingApprovalRelay approvalRelay,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct,
        TimeSpan? keepAliveInterval = null)
    {
        var idleInterval = keepAliveInterval ?? KeepAliveInterval;
        var merged = Channel.CreateUnbounded<string>();

        using var subscription = approvalRelay.Subscribe(
            sessionId,
            onRequested: evt => merged.Writer.TryWrite(JsonSerializer.Serialize(evt, JsonOptions)),
            onResolved: evt => merged.Writer.TryWrite(JsonSerializer.Serialize(evt, JsonOptions)));

        var pumpAgentEvents = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in events.ReadAllAsync(ct))
                    await merged.Writer.WriteAsync(SerializeEvent(evt), ct);
            }
            finally
            {
                // The turn ending is what ends this whole merged stream — approval notifications
                // are only ever relevant while their originating turn is still live.
                merged.Writer.TryComplete();
            }
        }, ct);

        try
        {
            // Deliberately NOT `await foreach (... merged.Reader.ReadAllAsync(ct))`: that yields
            // only when a real event fires, so a quiet gap (a slow tool call, or a local model
            // still working on its first token) puts ZERO bytes on this response for however long
            // it lasts. Every idle watchdog between here and the webview then treats a perfectly
            // healthy turn as a dead connection — Kestrel's own MinResponseDataRate did exactly
            // that (hence its disabling in Program.cs), and so does the VS Code extension's
            // Node-side fetch, whose undici default aborts after 300s of no body data (measured
            // live, and reproduced: a silent stream dies while an otherwise identical one carrying
            // periodic keep-alives survives well past the same timeout). Rather than disabling
            // each watchdog in turn as it's discovered, keep the connection legitimately busy:
            // emit a keep-alive whenever the merged stream goes quiet, which is the ordinary SSE
            // answer to this and resets every one of those idle timers at once.
            Task<string>? pendingRead = null;
            while (true)
            {
                pendingRead ??= merged.Reader.ReadAsync(ct).AsTask();

                using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var idle = Task.Delay(idleInterval, idleCts.Token);
                if (await Task.WhenAny(pendingRead, idle) != pendingRead)
                {
                    yield return KeepAlivePayload;
                    continue;
                }

                // Stop the losing timer rather than leaving it to fire into nothing — a turn can
                // run for thousands of events, and each abandoned Delay would hold a live timer
                // until its interval elapsed.
                await idleCts.CancelAsync();

                // ReadAsync throws when the channel completes and is drained (unlike
                // ReadAllAsync, which just ends its enumeration) — that's this loop's exit.
                // Caught rather than pre-checked: Completion racing a read is exactly the case
                // TryRead/WaitToRead would leave ambiguous. Assigned out here, not yield-returned
                // inside the try, since a try WITH a catch can't contain a yield.
                string? json = null;
                var drained = false;
                try
                {
                    json = await pendingRead;
                }
                catch (ChannelClosedException)
                {
                    drained = true;
                }

                pendingRead = null;
                if (drained)
                    break;

                yield return json!;
            }
        }
        finally
        {
            await pumpAgentEvents.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// How long the merged stream may stay quiet before a keep-alive is emitted. Comfortably under
    /// the tightest idle watchdog known to sit on this connection (the extension's Node-side fetch,
    /// which undici defaults to aborting after 300s without body data), with enough margin that a
    /// machine briefly too busy to run the timer on schedule still can't drift into one.
    /// </summary>
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Carried as an ordinary event payload rather than a raw SSE comment line (": keep-alive"),
    /// which is what an SSE stream would normally use for this: TypedResults.ServerSentEvents
    /// writes each yielded string as that event's own "data:" line, so a comment can't be emitted
    /// through it without bypassing the helper entirely. The extension drops these before they
    /// reach any consumer (see agentEvents.ts's readAgentEvents) — the payload exists purely to
    /// put bytes on the wire, not to be read.
    /// </summary>
    internal const string KeepAlivePayload = """{"KeepAlive":true}""";

    // AgentEvent.ErrorOccurred carries the raw .NET Exception a provider/tool threw. Serializing it
    // generically like every other AgentEvent variant throws System.NotSupportedException on
    // properties System.Text.Json can't handle (e.g. Exception.TargetSite, a MethodBase) — crashing
    // this SSE write entirely and turning a normal, reportable error (confirmed live: AgentLoop's
    // own 60s stream-idle TimeoutException) into an opaque 500 with no body, since nothing had been
    // flushed to the client yet. agentEvents.ts's parser only ever reads Exception.Message anyway
    // (see its `obj.Exception?.Message` check), so that's all this needs to send.
    private static string SerializeEvent(AgentEvent evt) =>
        evt is ErrorOccurred error
            ? JsonSerializer.Serialize(new { Exception = new { error.Exception.Message } }, JsonOptions)
            : JsonSerializer.Serialize(evt, evt.GetType(), JsonOptions);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
}

public sealed record TurnRequest(string Input, IReadOnlyList<AttachedContent>? Attachments = null);

public sealed record ResolveApprovalRequest(ApprovalDecision Decision);
