using System.Text.Json;
using System.Text.Json.Nodes;

namespace Litos.SoftwareFactory.Core.Store;

/// <summary>Where an @factory message to a working task has got to (m2-architecture.md §5.4).</summary>
public enum SteerStatus
{
    /// <summary>No agent turn could take it yet: it goes into the next work turn's brief.</summary>
    Held,

    /// <summary>A work turn has it.</summary>
    Delivered,

    /// <summary>The work stopped before any turn could take it; the payload says why.</summary>
    Dropped,
}

/// <summary>An @factory message to a working task, still held for the agent.</summary>
public sealed record HeldSteer(Guid MessageId, string Text);

/// <summary>
/// A steer's status lives in its message's payload, as <c>{"steer":"held"}</c>, so the thread
/// shows it beside the message and no other record is needed.
/// </summary>
public static class SteerPayload
{
    public static string For(SteerStatus status, string? reason = null)
    {
        var payload = new JsonObject { ["steer"] = status.ToString().ToLowerInvariant() };
        if (reason is not null)
            payload["reason"] = reason;
        return payload.ToJsonString();
    }

    /// <summary>The steer status a message's payload carries; null for any other message.</summary>
    public static SteerStatus? Read(string? payloadJson)
    {
        if (string.IsNullOrEmpty(payloadJson))
            return null;
        try
        {
            return JsonNode.Parse(payloadJson) is JsonObject payload
                && payload["steer"] is JsonValue value
                && value.TryGetValue<string>(out var text)
                && Enum.TryParse<SteerStatus>(text, ignoreCase: true, out var status)
                ? status
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
