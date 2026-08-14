using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Litos.Examples.BlazorChat.Auth;

/// <summary>
/// Reads the signed-in user's Litos.Api JWT pair from BlazorChat's own auth cookie
/// (LitosAuthClaims) — separate from Litos.Api's litos_admin cookie, since BlazorChat is a
/// distinct app/origin talking to Litos.Api over HTTP (see the .csproj's "genuinely external
/// app" comment). The cookie itself is only ever written by AuthEndpoints.MapAuthEndpoints's
/// POST /login — SignInAsync only succeeds from a genuinely fresh HTTP response, which a plain
/// &lt;form method="post"&gt; submission gives you and no Blazor Server interactive component
/// event does (verified live: even the very first OnValidSubmit/@onclick in a circuit runs after
/// that circuit's initial response has already completed). See Login.razor and AuthEndpoints.cs.
/// </summary>
public sealed class LitosUserSession(IHttpContextAccessor httpContextAccessor)
{
    private ClaimsPrincipal User => httpContextAccessor.HttpContext?.User ?? new ClaimsPrincipal(new ClaimsIdentity());

    public string? Username => User.Identity?.Name;

    public string? AccessToken => User.FindFirst(LitosAuthClaims.AccessToken)?.Value;

    public string? RefreshToken => User.FindFirst(LitosAuthClaims.RefreshToken)?.Value;

    public bool IsAuthenticated => AccessToken is not null;

    /// <summary>
    /// Updates the CURRENT request's in-memory ClaimsPrincipal only — not the cookie itself, and
    /// not persisted. Used by JwtRefreshHandler after a silent mid-circuit refresh: the refreshed
    /// token is usable for the rest of this circuit's outgoing Litos.Api calls, but the browser's
    /// cookie still carries the old (now server-side-revoked) refresh token. The user's next fresh
    /// circuit — a reload, a new tab — re-authenticates off that stale cookie; if its access token
    /// has also expired by then, Litos.Api rejects it, the stale refresh token fails too (already
    /// rotated away), and the app falls through to /login rather than silently misbehaving.
    /// A production app would instead keep the cookie itself current on refresh — e.g. a
    /// same-origin client-side fetch to a dedicated refresh endpoint — which this reference
    /// example doesn't attempt.
    /// </summary>
    public void UpdateTokensForRestOfRequest(LitosTokenPair tokens)
    {
        if (httpContextAccessor.HttpContext is not { User.Identity: ClaimsIdentity identity })
            return;

        RemoveIfPresent(identity, LitosAuthClaims.AccessToken);
        RemoveIfPresent(identity, LitosAuthClaims.RefreshToken);
        identity.AddClaim(new Claim(LitosAuthClaims.AccessToken, tokens.AccessToken));
        identity.AddClaim(new Claim(LitosAuthClaims.RefreshToken, tokens.RefreshToken));
    }

    public static ClaimsPrincipal BuildPrincipal(string username, LitosTokenPair tokens)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, username),
                new Claim(LitosAuthClaims.AccessToken, tokens.AccessToken),
                new Claim(LitosAuthClaims.RefreshToken, tokens.RefreshToken),
            ], CookieAuthenticationDefaults.AuthenticationScheme);
        return new ClaimsPrincipal(identity);
    }

    private static void RemoveIfPresent(ClaimsIdentity identity, string claimType)
    {
        var claim = identity.FindFirst(claimType);
        if (claim is not null)
            identity.RemoveClaim(claim);
    }
}
