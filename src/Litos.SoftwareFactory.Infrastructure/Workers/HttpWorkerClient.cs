using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Ports;

namespace Litos.SoftwareFactory.Infrastructure.Workers;

/// <summary>
/// The host's calls into one worker over loopback HTTP (docs/software-factory/m1-architecture.md
/// §3). Every request carries the per-launch secret.
///
/// The turn's event stream is read here only to know when the turn ends, to count tool calls
/// against the per-turn cap, to capture an error, and to keep the text of its last message, which
/// is a chat turn's answer. What a work turn *achieved* is never taken from it: that comes from
/// the completion tools' own callbacks to the host.
/// </summary>
public sealed class HttpWorkerClient : IWorkerClient
{
    private readonly HttpClient _http;

    public HttpWorkerClient(HttpClient http, Uri baseAddress, string secret)
    {
        _http = http;
        _http.BaseAddress = baseAddress;
        // A turn can legitimately run for a long time; the caller's token bounds it, not HttpClient.
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _http.DefaultRequestHeaders.Remove(FactoryWire.SecretHeader);
        _http.DefaultRequestHeaders.Add(FactoryWire.SecretHeader, secret);
    }

    public async Task<TurnStreamResult> RunTurnAsync(string sessionId, TurnKind kind, string brief, int maxToolCalls, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TurnsPath(sessionId))
        {
            Content = JsonContent.Create(new { input = brief, turnKind = kind.ToString() }),
        };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        // 202 means a turn was already running and this became a steering message — for a brief,
        // that is a host bug: the host starts a turn only when none is running.
        if (response.StatusCode == HttpStatusCode.Accepted)
            return new TurnStreamResult(Completed: false, ToolCalls: 0, "A turn was already running for this session.");
        if (!response.IsSuccessStatusCode)
            return new TurnStreamResult(Completed: false, ToolCalls: 0, $"The worker refused the turn (HTTP {(int)response.StatusCode}).");

        var toolCalls = 0;
        string? error = null;
        string? reply = null;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            switch (Classify(line.AsSpan(5).Trim().ToString(), out var message))
            {
                case TurnEvent.Message:
                    reply = message ?? reply;
                    break;
                case TurnEvent.ToolResult:
                    toolCalls++;
                    if (toolCalls > maxToolCalls)
                    {
                        await CancelAsync(sessionId, CancellationToken.None);
                        return new TurnStreamResult(Completed: false, toolCalls, $"The turn exceeded {maxToolCalls} tool calls.");
                    }

                    break;
                case TurnEvent.Error:
                    error = message;
                    break;
            }
        }

        return new TurnStreamResult(Completed: error is null, toolCalls, error, reply);
    }

    internal enum TurnEvent
    {
        Other,
        ToolResult,
        Error,

        /// <summary>A model call's completed message; its text, when it has any, is the message.</summary>
        Message,
    }

    /// <summary>
    /// The worker serializes each AgentEvent by its runtime type with no discriminator, so events
    /// are told apart by shape: a tool result carries CallId and Result, an error carries only an
    /// Exception with a Message, and a completed message carries Message and Usage. A completed
    /// message's text is its text blocks joined; reasoning is never in them.
    /// </summary>
    internal static TurnEvent Classify(string json, out string? message)
    {
        message = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return TurnEvent.Other;

            if (root.TryGetProperty("Exception", out var exception))
            {
                message = exception.ValueKind == JsonValueKind.Object && exception.TryGetProperty("Message", out var text)
                    ? text.GetString()
                    : "The turn failed.";
                return TurnEvent.Error;
            }

            if (root.TryGetProperty("Message", out var completed) && root.TryGetProperty("Usage", out _))
            {
                message = TextOf(completed);
                return TurnEvent.Message;
            }

            return root.TryGetProperty("CallId", out _) && root.TryGetProperty("Result", out _) ? TurnEvent.ToolResult : TurnEvent.Other;
        }
        catch (JsonException)
        {
            return TurnEvent.Other;
        }
    }

    /// <summary>The text blocks of a serialized ChatMessage, joined; null when it has none.</summary>
    private static string? TextOf(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object || !TryGet(message, "Content", out var content) || content.ValueKind != JsonValueKind.Array)
            return null;

        var texts = content.EnumerateArray()
            .Where(block => block.ValueKind == JsonValueKind.Object
                && TryGet(block, "type", out var type) && type.GetString() == "text"
                && TryGet(block, "Text", out _))
            .Select(block => { TryGet(block, "Text", out var text); return text.GetString(); })
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToList();
        return texts.Count == 0 ? null : string.Join("\n\n", texts);
    }

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    public async Task SteerAsync(string sessionId, string message, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(TurnsPath(sessionId), new { input = message }, ct);

        // With no turn running the worker would start one from the steering text and answer with
        // an event stream; that is not what steering means, so stop it straight away.
        if (response.StatusCode != HttpStatusCode.Accepted)
            await CancelAsync(sessionId, ct);
    }

    public async Task<bool> CancelAsync(string sessionId, CancellationToken ct)
    {
        using var response = await _http.PostAsync($"sessions/{Uri.EscapeDataString(sessionId)}/cancel", content: null, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> CompactAsync(string sessionId, string instruction, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(
            FactoryWire.CompactPath(sessionId).TrimStart('/'), new CompactRequest(instruction), FactoryWire.Json, ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<CompactResponse>(FactoryWire.Json, ct);
        return result?.Compacted ?? false;
    }

    public async Task ShutdownAsync(CancellationToken ct)
    {
        try
        {
            using var response = await _http.PostAsync(FactoryWire.ShutdownPath.TrimStart('/'), content: null, ct);
        }
        catch (HttpRequestException)
        {
            // The worker can close the connection as it exits; that is the outcome asked for.
        }
    }

    private static string TurnsPath(string sessionId) => $"sessions/{Uri.EscapeDataString(sessionId)}/turns";
}
