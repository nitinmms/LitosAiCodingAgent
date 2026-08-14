using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Security.Claims;
using Litos.Api.Data;

namespace Litos.Api.Auth;

public static class AdminAuthEndpoints
{
    public const string AdminClaimType = "litos_admin";

    /// <summary>Carries the authenticated User.Id (as a string) — the claim TurnsEndpoints reads to derive SessionOwner.</summary>
    public const string UserIdClaimType = "litos_user_id";

    public static IEndpointRouteBuilder MapAdminAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // Under /auth/*, not /login itself — Login.razor's own @page "/login" route already
        // claims that path for the GET-rendered page, and a Minimal API POST mapped to the same
        // path throws AmbiguousMatchException at request time (both endpoints match POST /login).
        app.MapPost("/auth/login", async (HttpContext http, AdminTokenProvider tokenProvider) =>
        {
            // Bound from the request body explicitly (Login.razor posts a plain HTML
            // <form method="post">, i.e. application/x-www-form-urlencoded) — a bare scalar
            // Minimal API parameter infers route-value/query-string binding, not form-body
            // binding, which would silently fail to read the submitted token.
            var form = await http.Request.ReadFormAsync();
            var token = form["token"].ToString();
            if (!tokenProvider.IsValid(token))
                return Results.Redirect("/login?error=1");

            var identity = new ClaimsIdentity(
                [new Claim(AdminClaimType, "true")], CookieAuthenticationDefaults.AuthenticationScheme);
            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
            return Results.Redirect("/");
        });

        // Separate route (not an overload of /auth/login) so the admin-token form and the
        // username/password form stay independently postable without content-based dispatch —
        // Login.razor's admin-token form keeps posting to /auth/login unchanged.
        app.MapPost("/auth/login-user", async (HttpContext http, UserStore users, CancellationToken ct) =>
        {
            var form = await http.Request.ReadFormAsync(ct);
            var username = form["username"].ToString();
            var password = form["password"].ToString();

            var user = await users.ValidateCredentialsAsync(username, password, ct);
            // Non-admin ("client") accounts are for API access only (JWT via POST /auth/token) —
            // they must never receive a Blazor-UI cookie session, so a correct non-admin
            // password is rejected the same way a wrong password is: no information leaked
            // about whether the account exists, only that this login path won't work for it.
            if (user is null || !user.IsAdmin)
                return Results.Redirect("/login?error=1");

            var identity = new ClaimsIdentity(
                [new Claim(UserIdClaimType, user.Id.ToString()), new Claim(AdminClaimType, "true")],
                CookieAuthenticationDefaults.AuthenticationScheme);
            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
            return Results.Redirect("/");
        });

        app.MapPost("/auth/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/login");
        });

        // API/client auth path — separate from the two cookie-issuing routes above. Works for
        // any enabled account, admin or not: an admin might also want to script against the API,
        // while a non-admin account has no other way in at all (blocked from /auth/login-user
        // above). Returns 401 with no body on failure — no distinction between "wrong password"
        // and "account disabled/unknown", same rationale as the cookie routes.
        app.MapPost("/auth/token", async (HttpContext http, UserStore users, JwtTokenService tokens, CancellationToken ct) =>
        {
            var form = await http.Request.ReadFormAsync(ct);
            var username = form["username"].ToString();
            var password = form["password"].ToString();

            var user = await users.ValidateCredentialsAsync(username, password, ct);
            if (user is null)
                return Results.Unauthorized();

            var pair = await tokens.IssueAsync(user, ct);
            return Results.Ok(new { accessToken = pair.AccessToken, refreshToken = pair.RefreshToken, expiresAt = pair.AccessTokenExpiresAt });
        });

        app.MapPost("/auth/token/refresh", async (HttpContext http, JwtTokenService tokens, CancellationToken ct) =>
        {
            var form = await http.Request.ReadFormAsync(ct);
            var refreshToken = form["refreshToken"].ToString();

            var pair = await tokens.RefreshAsync(refreshToken, ct);
            if (pair is null)
                return Results.Unauthorized();

            return Results.Ok(new { accessToken = pair.AccessToken, refreshToken = pair.RefreshToken, expiresAt = pair.AccessTokenExpiresAt });
        });

        return app;
    }
}
