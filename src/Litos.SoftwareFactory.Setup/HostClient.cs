using System.Net.Http.Json;
using System.Text.Json;

namespace Litos.SoftwareFactory.Setup;

/// <summary>What litos-factory asks the running host; <see cref="HostClient"/> is the real one, tests fake it.</summary>
public interface IHostControl : IDisposable
{
    /// <summary>The host's /readyz answer, or why there is none.</summary>
    Task<(bool Ready, string Detail)> ReadinessAsync(CancellationToken ct);

    Task<bool> WaitUntilReadyAsync(TimeSpan limit, CancellationToken ct);

    /// <summary>Asks the host to stop as on Ctrl+C; false if it did not take the request.</summary>
    Task<bool> RequestStopAsync(CancellationToken ct);

    /// <summary>Whether the seeded admin still has the generated password; null when the host cannot say.</summary>
    Task<bool?> AdminMustChangePasswordAsync(CancellationToken ct);
}

/// <summary>
/// The host's /readyz, and its control endpoints with the token from factory.env (Host/Api/ControlApi.cs).
/// </summary>
public sealed class HostClient(string url, string? controlToken) : IHostControl
{
    /// <summary>Keep in step with ControlApi.TokenHeader in the host, which this program does not reference.</summary>
    public const string ControlHeader = "X-Factory-Control";

    private readonly HttpClient _http = new() { BaseAddress = new Uri(url + "/"), Timeout = TimeSpan.FromSeconds(10) };

    public static HostClient For(FactoryEnv env) => new(env.Url, env[FactoryEnv.ControlToken]);

    public async Task<(bool Ready, string Detail)> ReadinessAsync(CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync("readyz", ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            return (response.IsSuccessStatusCode, body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return (false, "not answering");
        }
    }

    public async Task<bool> WaitUntilReadyAsync(TimeSpan limit, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + limit;
        while (!(await ReadinessAsync(ct)).Ready)
        {
            if (DateTime.UtcNow >= deadline)
                return false;
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        return true;
    }

    public async Task<bool> RequestStopAsync(CancellationToken ct)
    {
        try
        {
            using var request = Control(HttpMethod.Post, "control/stop");
            using var response = await _http.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    public async Task<bool?> AdminMustChangePasswordAsync(CancellationToken ct)
    {
        try
        {
            using var request = Control(HttpMethod.Get, "control/status");
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return null;
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            return body.GetProperty("adminMustChangePassword").GetBoolean();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private HttpRequestMessage Control(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add(ControlHeader, controlToken ?? "");
        return request;
    }

    public void Dispose() => _http.Dispose();
}
