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
    private static async IAsyncEnumerable<string> ToSseData(
        string sessionId, ChannelReader<AgentEvent> events, PendingApprovalRelay approvalRelay,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
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
                    await merged.Writer.WriteAsync(JsonSerializer.Serialize(evt, evt.GetType(), JsonOptions), ct);
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
            await foreach (var json in merged.Reader.ReadAllAsync(ct))
                yield return json;
        }
        finally
        {
            await pumpAgentEvents.ConfigureAwait(false);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
}

public sealed record TurnRequest(string Input, IReadOnlyList<AttachedContent>? Attachments = null);

public sealed record ResolveApprovalRequest(ApprovalDecision Decision);
