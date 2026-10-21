using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Litos.SoftwareFactory.Tray;

/// <summary>
/// The factory refused the token: revoked, never valid, its account disabled, or (403) good but
/// not enough, as for an Admin who must first choose a new password. Retrying cannot help; the
/// person has to give the tray app a new token. <see cref="Exception.Message"/> is the host's reason.
/// </summary>
public sealed class TokenRefusedException(string message) : Exception(message);

/// <summary>
/// The three calls the tray app makes, all GET and all the host lets a personal access token make
/// (AccessTokensApi.cs): who the token belongs to, the task list with the event cursor to stream
/// from, and the board's event stream.
/// </summary>
public sealed class FactoryClient
{
    /// <summary>The header GET /api/threads says the stream's starting point in (FactoryApi.EventCursorHeader).</summary>
    public const string EventCursorHeader = "X-Event-Cursor";

    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;

    /// <param name="http">With <see cref="HttpClient.BaseAddress"/> and the Bearer token set, and no
    /// overall timeout: the stream stays open for hours. Use <see cref="CreateHttp"/>.</param>
    public FactoryClient(HttpClient http) => _http = http;

    /// <summary>An HttpClient for the factory at <paramref name="address"/>, signing in with <paramref name="token"/>.
    /// The Windows proxy settings and certificate store apply, as for any .NET HttpClient.</summary>
    public static HttpClient CreateHttp(Uri address, string token, HttpMessageHandler? handler = null)
    {
        var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.BaseAddress = address;
        http.Timeout = Timeout.InfiniteTimeSpan;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LitosFactoryTray", "1.0"));
        return http;
    }

    public async Task<TrayUser> MeAsync(CancellationToken ct)
    {
        using var response = await GetAsync("api/auth/me", HttpCompletionOption.ResponseContentRead, ct);
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return TrayUser.Parse(body.RootElement);
    }

    /// <summary>Every task the user can see, and the event cursor read just before the list:
    /// streaming after it misses nothing.</summary>
    public async Task<(IReadOnlyList<TrayTask> Tasks, long Cursor)> ListTasksAsync(CancellationToken ct)
    {
        using var response = await GetAsync("api/threads", HttpCompletionOption.ResponseContentRead, ct);
        var cursor = response.Headers.TryGetValues(EventCursorHeader, out var values)
            && long.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : throw new HttpRequestException($"The factory did not say where its event stream starts ({EventCursorHeader}). Is the address a Litos factory?");
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return ([.. body.RootElement.EnumerateArray().Select(TrayTask.Parse)], cursor);
    }

    /// <summary>Opens the board's stream after <paramref name="after"/>. The caller reads it with
    /// <see cref="SseReader"/> and disposes the response when done.</summary>
    public async Task<HttpResponseMessage> OpenEventsAsync(long after, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"api/events?after={after.ToString(CultureInfo.InvariantCulture)}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return await SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout: false, ct);
    }

    private Task<HttpResponseMessage> GetAsync(string path, HttpCompletionOption completion, CancellationToken ct) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Get, path), completion, timeout: true, ct);

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completion, bool timeout, CancellationToken ct)
    {
        using var _ = request;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout)
            limit.CancelAfter(CallTimeout);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, completion, limit.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"The factory did not answer within {CallTimeout.TotalSeconds:0} seconds.");
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            var reason = await ErrorOf(response, ct)
                ?? (response.StatusCode == HttpStatusCode.Unauthorized ? "The factory did not accept the access token." : "The factory refused the access token.");
            response.Dispose();
            throw new TokenRefusedException(reason);
        }

        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            var reason = await ErrorOf(response, ct);
            response.Dispose();
            throw new HttpRequestException($"The factory answered {status}{(reason is null ? "." : $": {reason}")}", null, (HttpStatusCode)status);
        }

        return response;
    }

    /// <summary>The host's own reason, from its { "error": "…" } body, when it gave one.</summary>
    private static async Task<string?> ErrorOf(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return body.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
