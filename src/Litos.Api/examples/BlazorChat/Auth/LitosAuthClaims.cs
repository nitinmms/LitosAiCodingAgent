namespace Litos.Examples.BlazorChat.Auth;

/// <summary>
/// Claim types on BlazorChat's own sign-in cookie — separate from Litos.Api's litos_admin cookie
/// (BlazorChat is a distinct app/origin talking to Litos.Api over HTTP, per this project's own
/// "genuinely external app" comment in the .csproj). Carries the JWT pair issued by Litos.Api's
/// POST /auth/token.
/// </summary>
public static class LitosAuthClaims
{
    public const string AccessToken = "litos_access_token";
    public const string RefreshToken = "litos_refresh_token";
}
