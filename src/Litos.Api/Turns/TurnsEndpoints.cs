using System.Net.ServerSentEvents;
using System.Text.Json;
using System.Threading.Channels;
using Litos.Agent.Messages;
using Litos.Agent.Session;
using Litos.Agent.Streaming;
using Litos.Api.Auth;

namespace Litos.Api.Turns;

public static class TurnsEndpoints
{
    public static IEndpointRouteBuilder MapTurnsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/sessions/{id}/turns", (string id, TurnRequest request, AgentWorker worker, CancellationToken requestAborted) =>
        {
            var events = worker.StartOrSteerTurn(SessionOwner.Local, id, [new TextBlock(request.Input)], requestAborted, out var outcome);

            return outcome switch
            {
                TurnOutcome.Steered => Results.Accepted(value: "Message delivered to the in-progress turn."),
                TurnOutcome.Started => TypedResults.ServerSentEvents(ToSseItems(events!, requestAborted), eventType: "agent-event"),
                _ => Results.Problem("Unknown turn outcome."),
            };
        }).AddEndpointFilter<AdminTokenFilter>();

        return app;
    }

    private static async IAsyncEnumerable<SseItem<string>> ToSseItems(
        ChannelReader<AgentEvent> events, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var evt in events.ReadAllAsync(ct))
            yield return new SseItem<string>(JsonSerializer.Serialize(evt, evt.GetType(), JsonOptions));
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
}
