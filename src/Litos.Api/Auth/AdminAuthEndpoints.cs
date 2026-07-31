using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Security.Claims;

namespace Litos.Api.Auth;

public static class AdminAuthEndpoints
{
    public const string AdminClaimType = "litos_admin";

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

        app.MapPost("/auth/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/login");
        });

        return app;
    }
}
