using System.Net.Http.Json;
using Litos.SoftwareFactory.Contracts;

namespace Litos.SoftwareFactory.Worker;

/// <summary>
/// The worker's calls back into the factory host (docs/software-factory/m1-architecture.md §3):
/// completion-tool submissions, the "ready" signal, and — through <see cref="Http"/> — the model
/// gateway. Every request carries the per-launch secret.
/// </summary>
public sealed class FactoryHostClient
{
    public FactoryHostClient(HttpClient http, WorkerOptions options)
    {
        Http = http;
        RunId = options.RunId;
        Http.BaseAddress = options.HostUrl;
        // Model calls stream for as long as the model takes; cancellation bounds them, not this.
        Http.Timeout = Timeout.InfiniteTimeSpan;
        Http.DefaultRequestHeaders.Remove(FactoryWire.SecretHeader);
        Http.DefaultRequestHeaders.Add(FactoryWire.SecretHeader, options.Secret);
    }

    public HttpClient Http { get; }

    public string RunId { get; }

    /// <summary>Posts a completion tool's payload and returns the host's verdict.</summary>
    /// <exception cref="HttpRequestException">The host could not be reached or answered with an error.</exception>
    public async Task<SubmissionResponse> SubmitAsync(string sessionId, Submission submission, CancellationToken ct)
    {
        using var response = await Http.PostAsJsonAsync(
            FactoryWire.SubmissionsPath(RunId), new SubmissionRequest(sessionId, submission), FactoryWire.Json, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SubmissionResponse>(FactoryWire.Json, ct)
            ?? throw new HttpRequestException("The factory host returned an empty response to a submission.");
    }

    /// <summary>Asks the host to search the web; it holds the key, and says why when it will not.</summary>
    /// <exception cref="HttpRequestException">The host could not be reached or answered with an error.</exception>
    public async Task<WebSearchResponse> WebSearchAsync(WebSearchRequest request, CancellationToken ct)
    {
        using var response = await Http.PostAsJsonAsync(FactoryWire.WebSearchPath(RunId), request, FactoryWire.Json, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<WebSearchResponse>(FactoryWire.Json, ct)
            ?? throw new HttpRequestException("The factory host returned an empty response to a web search.");
    }

    public async Task ReadyAsync(WorkerReady ready, CancellationToken ct)
    {
        using var response = await Http.PostAsJsonAsync(FactoryWire.ReadyPath(RunId), ready, FactoryWire.Json, ct);
        response.EnsureSuccessStatusCode();
    }
}
