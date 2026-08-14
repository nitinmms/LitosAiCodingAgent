using System.Net.Http.Json;

namespace Litos.Examples.BlazorChat.Auth;

public sealed record LitosTokenPair(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);

/// <summary>
/// Wraps Litos.Api's POST /auth/token and /auth/token/refresh (Litos.Api's JWT REST auth surface
/// — see src/Litos.Api/Auth/AdminAuthEndpoints.cs). Deliberately given its own named HttpClient
/// (registered in Program.cs without JwtRefreshHandler attached) rather than reusing
/// LitosApiClient's HttpClient — that one exists specifically to attach/refresh a bearer token per
/// request, and wiring it onto the very calls that MINT/refresh tokens would be circular.
/// </summary>
public sealed class LitosLoginService(HttpClient httpClient)
{
    public async Task<LitosTokenPair?> LoginAsync(string username, string password, CancellationToken ct)
    {
        var response = await httpClient.PostAsync("auth/token",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = username, ["password"] = password }), ct);

        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<LitosTokenPair>(cancellationToken: ct)
            : null;
    }

    public async Task<LitosTokenPair?> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        var response = await httpClient.PostAsync("auth/token/refresh",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["refreshToken"] = refreshToken }), ct);

        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<LitosTokenPair>(cancellationToken: ct)
            : null;
    }
}
