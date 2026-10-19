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

/// <param name="MustChangePassword">The account must choose a new password before anything else.</param>
public sealed record CurrentUser(Guid Id, string UserName, string? DisplayName, IReadOnlyList<string> Roles, bool MustChangePassword = false);

public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

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

    /// <summary>Carried in the session of an account that must choose a new password.</summary>
    public const string MustChangePasswordClaim = "factory:must_change_password";

    /// <summary>What such an account may still call: who it is, the change itself, and signing out.</summary>
    private static readonly string[] AllowedBeforePasswordChange = ["/api/auth/me", "/api/auth/password", "/api/auth/logout"];

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
            .AddSignInManager()
            .AddClaimsPrincipalFactory<FactoryClaimsPrincipalFactory>();

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
            limiter.AddPolicy(InvitationsApi.RateLimit, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
        });

        return services;
    }

    /// <summary>
    /// Until an account that must change its password has done so, every other API call answers
    /// 403 with a reason the app shows: the one-time Admin password an installer generated works
    /// only to choose a new one (SoftwareFactoryInstallation.md §4.4). Runs after authentication.
    /// </summary>
    public static IApplicationBuilder UseFactoryPasswordChangeGate(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (context.User.HasClaim(MustChangePasswordClaim, "true")
            && context.Request.Path.StartsWithSegments("/api")
            && !AllowedBeforePasswordChange.Any(path => context.Request.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "Choose a new password before going on.", mustChangePassword = true });
            return;
        }

        await next(context);
    });

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

            return Results.Ok(new CurrentUser(user.Id, user.UserName!, user.DisplayName, [.. await users.GetRolesAsync(user)], user.MustChangePassword));
        }).AllowAnonymous().RequireRateLimiting(LoginRateLimit);

        // Any signed-in account changes its own password; the current one is required, so a
        // session left open cannot be used to take the account over.
        app.MapPost("/api/auth/password", async (
            ChangePasswordRequest request, ClaimsPrincipal principal, UserManager<FactoryUser> users, SignInManager<FactoryUser> signIn) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null)
                return Results.Unauthorized();
            if (string.IsNullOrEmpty(request.CurrentPassword) || string.IsNullOrEmpty(request.NewPassword))
                return Results.BadRequest(new { error = "Give the current password and a new one." });
            if (request.NewPassword == request.CurrentPassword)
                return Results.BadRequest(new { error = "The new password must differ from the current one." });

            var changed = await users.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
            if (!changed.Succeeded)
            {
                var wrong = changed.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.PasswordMismatch));
                return Results.BadRequest(new { error = wrong ? "The current password is incorrect." : string.Join(" ", changed.Errors.Select(e => e.Description)) });
            }

            if (user.MustChangePassword)
            {
                user.MustChangePassword = false;
                await users.UpdateAsync(user);
            }

            // A new session without the must-change mark; the password change rotated the
            // security stamp, so other sessions of this account end at their next check.
            await signIn.RefreshSignInAsync(user);
            return Results.Ok(new CurrentUser(user.Id, user.UserName!, user.DisplayName, [.. await users.GetRolesAsync(user)]));
        });

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
                : Results.Ok(new CurrentUser(user.Id, user.UserName!, user.DisplayName, [.. await users.GetRolesAsync(user)], user.MustChangePassword));
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

        var admin = new FactoryUser
        {
            Id = Guid.NewGuid(), UserName = options.AdminUser, DisplayName = options.AdminUser,
            // A generated password shown once works once: the Admin chooses their own at first sign-in.
            MustChangePassword = options.AdminPasswordOneTime,
        };
        var created = await users.CreateAsync(admin, options.AdminPassword);
        if (!created.Succeeded)
            return "The admin account could not be created: " + string.Join(" ", created.Errors.Select(e => e.Description));

        await users.AddToRoleAsync(admin, FactoryRoles.Admin);
        return null;
    }
}

/// <summary>Adds the must-change-password mark to the session of an account that carries it.</summary>
public sealed class FactoryClaimsPrincipalFactory(
    UserManager<FactoryUser> users, RoleManager<IdentityRole<Guid>> roles, Microsoft.Extensions.Options.IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<FactoryUser, IdentityRole<Guid>>(users, roles, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(FactoryUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        if (user.MustChangePassword)
            identity.AddClaim(new Claim(FactoryAuth.MustChangePasswordClaim, "true"));
        return identity;
    }
}
