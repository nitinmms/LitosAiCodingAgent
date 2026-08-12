using System.Text.Json;

namespace Litos.Examples.BlazorChat.LitosClient;

/// <summary>
/// Local mirror of Litos.Agent.Streaming.AgentEvent's JSON shape. Deliberately not shared via a
/// project reference — a real external client has only the wire format to go on, and that format
/// carries no type discriminator (AgentEvent is serialized via JsonSerializer.Serialize(evt,
/// evt.GetType(), ...) with no [JsonPolymorphic] attribute). AgentEventStreamParser infers the
/// variant from which properties are present on each parsed object.
/// </summary>
public abstract record AgentEventDto
{
    public static AgentEventDto Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("Text", out var textProp))
        {
            // TextDelta and ReasoningDelta share the exact same {"Text": "..."} shape; the SSE
            // stream gives no way to tell them apart from content alone, and this example treats
            // reasoning as ordinary assistant text if a reasoning-capable model is configured.
            return new TextDeltaDto(textProp.GetString() ?? "");
        }

        if (root.TryGetProperty("Result", out var resultProp))
        {
            // ToolCallResult(string CallId, string ToolName, ToolResult Result) where
            // ToolResult(string Text, bool IsError = false).
            var isError = resultProp.TryGetProperty("IsError", out var isErrorProp) && isErrorProp.GetBoolean();
            var text = resultProp.TryGetProperty("Text", out var textResultProp) ? textResultProp.GetString() ?? "" : resultProp.GetRawText();
            return new ToolCallResultDto(
                root.GetProperty("CallId").GetString() ?? "",
                root.GetProperty("ToolName").GetString() ?? "",
                !isError,
                text);
        }

        if (root.TryGetProperty("Arguments", out var argsProp))
        {
            // ToolCallCompleted(string CallId, string ToolName, JsonElement Arguments).
            return new ToolCallCompletedDto(
                root.GetProperty("CallId").GetString() ?? "",
                root.GetProperty("ToolName").GetString() ?? "",
                argsProp.GetRawText());
        }

        if (root.TryGetProperty("Reason", out var reasonProp) && root.TryGetProperty("CallId", out var skippedCallIdProp))
            return new ToolCallSkippedDto(skippedCallIdProp.GetString() ?? "", reasonProp.GetString() ?? "");

        if (root.TryGetProperty("ToolName", out var startedToolNameProp) && root.TryGetProperty("CallId", out var startedCallIdProp))
            return new ToolCallStartedDto(startedCallIdProp.GetString() ?? "", startedToolNameProp.GetString() ?? "");

        if (root.TryGetProperty("Message", out _) && root.TryGetProperty("Usage", out _))
            return new MessageCompletedDto();

        if (root.TryGetProperty("Exception", out var exceptionProp))
        {
            var message = exceptionProp.TryGetProperty("Message", out var msgProp) ? msgProp.GetString() : null;
            return new ErrorOccurredDto(message ?? "An error occurred.");
        }

        if (root.TryGetProperty("TokensBefore", out _))
            return new CompactionOccurredDto();

        return new UnknownEventDto(json);
    }
}

public sealed record TextDeltaDto(string Text) : AgentEventDto;

public sealed record ToolCallStartedDto(string CallId, string ToolName) : AgentEventDto;

public sealed record ToolCallCompletedDto(string CallId, string ToolName, string ArgumentsJson) : AgentEventDto;

public sealed record ToolCallResultDto(string CallId, string ToolName, bool Success, string ResultText) : AgentEventDto;

public sealed record ToolCallSkippedDto(string CallId, string Reason) : AgentEventDto;

public sealed record MessageCompletedDto : AgentEventDto;

public sealed record ErrorOccurredDto(string Message) : AgentEventDto;

public sealed record CompactionOccurredDto : AgentEventDto;

/// <summary>Fallback for any event shape this example doesn't specifically render — keeps the parser from throwing on a future AgentEvent variant it hasn't been taught about.</summary>
public sealed record UnknownEventDto(string RawJson) : AgentEventDto;
