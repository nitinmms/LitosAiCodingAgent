using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Litos.Examples.BlazorChat.Auth;

/// <summary>
/// Minimal API login/logout, mirroring Litos.Api's own Auth/AdminAuthEndpoints.cs pattern —
/// SignInAsync only succeeds while a genuinely fresh HTTP response is still open, which a plain
/// non-interactive &lt;form method="post"&gt; submission gives you and an interactive Blazor
/// Server component event (EditForm/OnValidSubmit, or any @onclick) does not: by the time any
/// interactive circuit event fires, that circuit's initial response has already completed, so
/// SignInAsync throws "Headers are read-only, response has already started" (verified live
/// against this app). Login.razor is deliberately non-interactive for exactly this reason, same
/// as Litos.Api's own Login.razor.
/// </summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // Under /auth/*, not /login itself — Login.razor's own @page "/login" route already
        // claims GET /login, and a Minimal API POST mapped to the same path throws
        // AmbiguousMatchException at request time (verified live against this app — same
        // rationale documented in Litos.Api's own AdminAuthEndpoints.cs).
        app.MapPost("/auth/login", async (HttpContext http, LitosLoginService loginService, CancellationToken ct) =>
        {
            var form = await http.Request.ReadFormAsync(ct);
            var username = form["username"].ToString();
            var password = form["password"].ToString();

            LitosTokenPair? tokens;
            try
            {
                tokens = await loginService.LoginAsync(username, password, ct);
            }
            catch (HttpRequestException)
            {
                // Litos.Api unreachable (connection refused, DNS failure, ...) — surfaced as a
                // distinct query param from a wrong password (error=1) so Login.razor can show a
                // meaningful message instead of leaving the request to fall through to ASP.NET
                // Core's default unhandled-exception response (a bare 500/stack trace, no
                // redirect back to the form at all).
                return Results.Redirect("/login?error=unreachable");
            }

            if (tokens is null)
                return Results.Redirect("/login?error=1");

            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                LitosUserSession.BuildPrincipal(username, tokens));
            return Results.Redirect("/");
        });

        app.MapPost("/auth/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/login");
        });

        return app;
    }
}
