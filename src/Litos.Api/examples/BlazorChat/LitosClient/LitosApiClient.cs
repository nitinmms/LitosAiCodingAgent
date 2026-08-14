using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Litos.Examples.BlazorChat.LitosClient;

/// <summary>One attachment ready to POST — filename, content type, and bytes already read into memory by the caller (Chat.razor reads each IBrowserFile before this point, since IBrowserFile streams can't outlive the render that produced them).</summary>
public sealed record PendingAttachment(string FileName, string ContentType, byte[] Content);
public sealed record SessionSummaryDto(string SessionId, DateTimeOffset CreatedAt, DateTimeOffset LastUpdatedAt, string? FirstUserMessagePreview, int MessageCount);
public sealed record HistoryMessageDto(string Role, string Text, int Attachments, DateTimeOffset Timestamp);

/// <summary>
/// Thin wrapper over POST /sessions/{id}/turns — the one endpoint that drives a whole
/// conversation with Litos.Api (ReadMe_AgentDesign.md §10.3). Chooses JSON vs multipart based on
/// whether attachments are present, matching TurnsEndpoints' own content-type branch.
/// </summary>
public sealed class LitosApiClient(HttpClient httpClient)
{
    public async Task<IReadOnlyList<SessionSummaryDto>> ListSessionsAsync(CancellationToken ct) =>
        await httpClient.GetFromJsonAsync<List<SessionSummaryDto>>("sessions", ct) ?? [];

    public async Task<IReadOnlyList<HistoryMessageDto>> GetHistoryAsync(string sessionId, CancellationToken ct) =>
        await httpClient.GetFromJsonAsync<List<HistoryMessageDto>>($"sessions/{Uri.EscapeDataString(sessionId)}/history", ct) ?? [];

    public async Task<TurnOutcome> SendTurnAsync(
        string sessionId, string text, IReadOnlyList<PendingAttachment> attachments, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"sessions/{Uri.EscapeDataString(sessionId)}/turns")
        {
            Content = attachments.Count == 0 ? JsonContent.Create(new { input = text }) : BuildMultipartContent(text, attachments),
        };

        var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.Accepted)
        {
            var message = await response.Content.ReadAsStringAsync(ct);
            response.Dispose();
            // TurnsEndpoints distinguishes Steered from Queued only by response text — there's no
            // separate status code or header for it, so this example matches on the same wording
            // AgentWorker.StartOrSteerTurn produces (TurnsEndpoints.cs's StartOrSteer switch).
            return message.Contains("queued", StringComparison.OrdinalIgnoreCase)
                ? new TurnOutcome.Queued(message)
                : new TurnOutcome.Steered(message);
        }

        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(ct);
        return new TurnOutcome.Started(ReadEventsAndDisposeAsync(response, stream, ct));
    }

    private static async IAsyncEnumerable<AgentEventDto> ReadEventsAndDisposeAsync(
        HttpResponseMessage response, Stream stream, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var _ = response;
        await using var __ = stream;
        await foreach (var evt in AgentEventStreamParser.ReadEventsAsync(stream, ct))
            yield return evt;
    }

    private static MultipartFormDataContent BuildMultipartContent(string text, IReadOnlyList<PendingAttachment> attachments)
    {
        var form = new MultipartFormDataContent();
        if (!string.IsNullOrEmpty(text))
            form.Add(new StringContent(text), "input");

        foreach (var attachment in attachments)
        {
            var fileContent = new ByteArrayContent(attachment.Content);
            fileContent.Headers.ContentType = MediaTypeHeaderValue.TryParse(attachment.ContentType, out var mt)
                ? mt
                : new MediaTypeHeaderValue("application/octet-stream");
            form.Add(fileContent, "file", attachment.FileName);
        }

        return form;
    }
}
