using System.Text.Json;
using System.Threading.Channels;
using Litos.Agent.Messages;
using Litos.Agent.Session;
using Litos.Agent.Streaming;
using Litos.Api.Auth;

namespace Litos.Api.Turns;

public static class TurnsEndpoints
{
    /// <summary>Per-file cap — generous for typical documents/images/PDFs, small enough to bound context/memory impact of a single upload.</summary>
    public const long MaxAttachmentBytes = 20 * 1024 * 1024;

    /// <summary>Max files per request — bounds how much a single turn's context can grow from attachments.</summary>
    public const int MaxAttachmentCount = 5;

    public static IEndpointRouteBuilder MapTurnsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/sessions/{id}/turns", async (
            string id, HttpRequest request, AgentWorker worker, AttachmentContentBuilder attachmentBuilder, CancellationToken requestAborted) =>
        {
            if (!request.HasFormContentType)
            {
                var turnRequest = await request.ReadFromJsonAsync<TurnRequest>(requestAborted)
                    ?? throw new BadHttpRequestException("Request body is required.");
                return StartOrSteer(worker, id, [new TextBlock(turnRequest.Input)], requestAborted, queueIfActive: false);
            }

            var form = await request.ReadFormAsync(requestAborted);
            var files = form.Files;

            var validationError = ValidateAttachments(files);
            if (validationError is not null)
                return Results.BadRequest(validationError);

            var content = await attachmentBuilder.BuildContentAsync(form["input"], files, requestAborted);

            // Attachment-bearing requests never steer into an already-running turn: steering only
            // carries text (AgentWorker.RenderForSteering), which would silently drop any
            // ImageBlock. Instead, when a turn is already active for this session, the whole
            // request — text and attachments together — is queued and picked up automatically by
            // whatever starts the *next* fresh turn for this session (AgentWorker.StartOrSteerTurn,
            // queueIfActive: true). Requests with no files keep today's immediate-steer behavior.
            var queueIfActive = files.Count > 0;
            return StartOrSteer(worker, id, content, requestAborted, queueIfActive);
        }).AddEndpointFilter<AdminTokenFilter>();

        return app;
    }

    /// <summary>
    /// Checks each file against MaxAttachmentBytes and the collection against MaxAttachmentCount.
    /// Returns a human-readable error message, or null if the attachments are within limits.
    /// Pure/static so it's directly unit-testable without standing up an HTTP request.
    /// </summary>
    public static string? ValidateAttachments(IReadOnlyList<IFormFile> files)
    {
        if (files.Count > MaxAttachmentCount)
            return $"Too many attachments: {files.Count} (max {MaxAttachmentCount}).";

        foreach (var file in files)
        {
            if (file.Length > MaxAttachmentBytes)
                return $"Attachment '{file.FileName}' is {file.Length} bytes, exceeding the {MaxAttachmentBytes}-byte limit.";
        }

        return null;
    }

    private static IResult StartOrSteer(
        AgentWorker worker, string id, IReadOnlyList<ContentBlock> content, CancellationToken ct, bool queueIfActive)
    {
        var events = worker.StartOrSteerTurn(SessionOwner.Local, id, content, ct, out var outcome, queueIfActive);

        return outcome switch
        {
            TurnOutcome.Steered => Results.Accepted(value: "Message delivered to the in-progress turn."),
            TurnOutcome.Queued => Results.Accepted(value:
                "A turn is already in progress for this session. Your message and attachments have been " +
                "queued and will be included automatically in the next turn."),
            TurnOutcome.Started => TypedResults.ServerSentEvents(ToSseData(events!, ct), eventType: "agent-event"),
            _ => Results.Problem("Unknown turn outcome."),
        };
    }

    // Yields the raw per-event JSON strings (not SseItem<string>): TypedResults.ServerSentEvents's
    // (IAsyncEnumerable<SseItem<T>>, eventType) call shape doesn't exist — passing eventType:
    // together with an SseItem<string> sequence resolves to the generic (IAsyncEnumerable<T>,
    // eventType) overload instead, which JSON-serializes each SseItem<string> wrapper itself as
    // the payload rather than writing its .Data as the "data:" line. The (IAsyncEnumerable<string>,
    // eventType) overload used here writes each string directly.
    private static async IAsyncEnumerable<string> ToSseData(
        ChannelReader<AgentEvent> events, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var evt in events.ReadAllAsync(ct))
            yield return JsonSerializer.Serialize(evt, evt.GetType(), JsonOptions);
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
}
