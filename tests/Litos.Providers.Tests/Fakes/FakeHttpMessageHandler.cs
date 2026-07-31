using System.Text;

namespace Litos.Providers.Tests.Fakes;

/// <summary>
/// Intercepts outgoing HTTP calls so a provider's message-mapping logic can be exercised
/// through its real public StreamAsync/ListModelsAsync surface — via a real vendor SDK
/// or hand-rolled HttpClient wired to this handler — without hitting the network. Records
/// every request's body for assertion and serves a queue of canned responses, one per call.
/// </summary>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new();

    public List<CapturedRequest> CapturedRequests { get; } = [];

    public void Enqueue(HttpResponseMessage response) => _responses.Enqueue(response);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        CapturedRequests.Add(new CapturedRequest(request.Method, request.RequestUri, body));

        if (_responses.Count == 0)
            throw new InvalidOperationException("FakeHttpMessageHandler received a request but has no queued response.");

        return _responses.Dequeue();
    }

    public static HttpResponseMessage JsonResponse(string json) => new(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    public static HttpResponseMessage SseResponse(string body) => new(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
    };
}

public sealed record CapturedRequest(HttpMethod Method, Uri? Uri, string? Body);
