using System.Net;
using System.Net.Http.Headers;

namespace Litos.Examples.BlazorChat.Auth;

/// <summary>
/// Attaches the signed-in user's Litos.Api access token to every outgoing LitosApiClient request,
/// and transparently refreshes once on a 401 — mirrors the access+refresh token pattern
/// Litos.Api's own JwtTokenService implements server-side (short-lived access token, longer-lived
/// rotating refresh token). A second 401 (refresh itself failed — refresh token expired/revoked)
/// is left to propagate; Chat.razor's existing catch surfaces it, and the next real navigation's
/// [Authorize] check (cookie now stale) redirects to /login.
///
/// See LitosUserSession.UpdateTokensForRestOfRequest's doc comment for what a refresh here does
/// and doesn't persist — short version: good for the rest of this request/circuit, not written
/// back to the cookie.
/// </summary>
public sealed class JwtRefreshHandler(LitosUserSession session, LitosLoginService loginService) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);

        var response = await base.SendAsync(request, ct);
        if (response.StatusCode != HttpStatusCode.Unauthorized || session.RefreshToken is null)
            return response;

        var refreshed = await loginService.RefreshAsync(session.RefreshToken, ct);
        if (refreshed is null)
            return response;

        session.UpdateTokensForRestOfRequest(refreshed);

        response.Dispose();
        using var retryRequest = Clone(request);
        retryRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", refreshed.AccessToken);
        return await base.SendAsync(retryRequest, ct);
    }

    // HttpRequestMessage can't be sent twice (SendAsync throws "already sent") — a shallow clone
    // with the same headers and Content reference is sent instead, rather than a deep copy.
    // Safe here specifically because LitosApiClient only ever builds in-memory, re-readable
    // content (JsonContent/StringContent/ByteArrayContent — see SendTurnAsync), never a
    // forward-only stream that a first send attempt could have already drained. A handler meant
    // to retry arbitrary caller-supplied content would need to buffer Content up front instead.
    private static HttpRequestMessage Clone(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri) { Content = request.Content };
        foreach (var header in request.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return clone;
    }
}
