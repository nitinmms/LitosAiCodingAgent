using System.Security.Claims;
using System.Threading.RateLimiting;
using Litos.SoftwareFactory.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;

namespace Litos.SoftwareFactory.Host.Auth;

public static class FactoryRoles
{
    public const string Admin = "Admin";
    public const string Member = "Member";
}

public sealed record LoginRequest(string UserName, string Password);

public sealed record CurrentUser(Guid Id, string UserName, string? DisplayName, IReadOnlyList<string> Roles);

/// <summary>
/// Sign-in for the factory (ReadMe_LitosSoftwareFactory_V1.md §13.3): local username/password
/// accounts on ASP.NET Core Identity, a same-site cookie so no token is ever stored in the
/// browser, lockout after repeated failures and a rate limit on the sign-in endpoint. M1 has one
/// seeded Admin; invitations arrive in M2.
/// </summary>
public static class FactoryAuth
{
    /// <summary>State-changing API requests must carry this header. A browser will not let
    /// another site's page send a custom header without the host's permission, so with the
    /// same-site cookie it closes cross-site request forgery.</summary>
    public const string CsrfHeader = "X-Factory-Request";

    public const string LoginRateLimit = "login";

    public static IServiceCollection AddFactoryAuth(this IServiceCollection services)
    {
        services.AddIdentityCore<FactoryUser>(options =>
            {
                options.Password.RequiredLength = 12;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireDigit = false;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                options.Lockout.AllowedForNewUsers = true;
                options.User.RequireUniqueEmail = false;
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<FactoryDbContext>()
            .AddSignInManager();

        services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
        services.ConfigureApplicationCookie(cookie =>
        {
            cookie.Cookie.Name = "factory.session";
            cookie.Cookie.HttpOnly = true;
            cookie.Cookie.SameSite = SameSiteMode.Strict;
            cookie.SlidingExpiration = true;
            cookie.ExpireTimeSpan = TimeSpan.FromHours(12);

            // This is an API for a single-page app: an unauthenticated call is a 401 for the
            // app to handle, never a redirect to an HTML login page.
            cookie.Events.OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            cookie.Events.OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
        });

        services.AddAuthorizationBuilder()
            .AddPolicy(FactoryRoles.Admin, policy => policy.RequireRole(FactoryRoles.Admin));

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.AddPolicy(LoginRateLimit, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
        });

        return services;
    }

    /// <summary>Refuses state-changing API calls that do not carry the CSRF header.</summary>
    public static IApplicationBuilder UseFactoryCsrfHeader(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var changesState = !(HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method) || HttpMethods.IsOptions(context.Request.Method));
        if (changesState && context.Request.Path.StartsWithSegments("/api") && !context.Request.Headers.ContainsKey(CsrfHeader))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = $"State-changing requests must send the {CsrfHeader} header." });
            return;
        }

        await next(context);
    });

    public static IEndpointRouteBuilder MapFactoryAuth(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/login", async (LoginRequest request, SignInManager<FactoryUser> signIn, UserManager<FactoryUser> users) =>
        {
            var user = await users.FindByNameAsync(request.UserName ?? "");
            if (user is null || user.Disabled)
                return Results.Json(new { error = "The user name or password is incorrect." }, statusCode: StatusCodes.Status401Unauthorized);

            var result = await signIn.PasswordSignInAsync(user, request.Password ?? "", isPersistent: false, lockoutOnFailure: true);
            if (result.IsLockedOut)
                return Results.Json(new { error = "This account is locked after repeated failed sign-ins. Try again later." }, statusCode: StatusCodes.Status423Locked);
            if (!result.Succeeded)
                return Results.Json(new { error = "The user name or password is incorrect." }, statusCode: StatusCodes.Status401Unauthorized);

            return Results.Ok(new CurrentUser(user.Id, user.UserName!, user.DisplayName, [.. await users.GetRolesAsync(user)]));
        }).AllowAnonymous().RequireRateLimiting(LoginRateLimit);

        app.MapPost("/api/auth/logout", async (SignInManager<FactoryUser> signIn) =>
        {
            await signIn.SignOutAsync();
            return Results.Ok();
        });

        app.MapGet("/api/auth/me", async (ClaimsPrincipal principal, UserManager<FactoryUser> users) =>
        {
            var user = await users.GetUserAsync(principal);
            return user is null
                ? Results.Unauthorized()
                : Results.Ok(new CurrentUser(user.Id, user.UserName!, user.DisplayName, [.. await users.GetRolesAsync(user)]));
        });

        return app;
    }

    /// <summary>The acting user's id; every action records who took it.</summary>
    public static Guid UserId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? id
            : throw new InvalidOperationException("The request has no signed-in user.");

    /// <summary>
    /// Creates the Admin account on first start, from FACTORY_ADMIN_USER and
    /// FACTORY_ADMIN_PASSWORD. It does nothing once any account exists, so changing those
    /// settings later never resets a password.
    /// </summary>
    public static async Task<string?> SeedAdminAsync(IServiceProvider services, FactoryOptions options)
    {
        using var scope = services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<FactoryUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();

        foreach (var role in new[] { FactoryRoles.Admin, FactoryRoles.Member })
        {
            if (!await roles.RoleExistsAsync(role))
                await roles.CreateAsync(new IdentityRole<Guid>(role) { Id = Guid.NewGuid() });
        }

        if (users.Users.Any())
            return null;
        if (string.IsNullOrWhiteSpace(options.AdminPassword))
            return "No account exists and FACTORY_ADMIN_PASSWORD is not set, so nobody can sign in.";

        var admin = new FactoryUser { Id = Guid.NewGuid(), UserName = options.AdminUser, DisplayName = options.AdminUser };
        var created = await users.CreateAsync(admin, options.AdminPassword);
        if (!created.Succeeded)
            return "The admin account could not be created: " + string.Join(" ", created.Errors.Select(e => e.Description));

        await users.AddToRoleAsync(admin, FactoryRoles.Admin);
        return null;
    }
}
