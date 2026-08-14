using Litos.Examples.BlazorChat;
using Litos.Examples.BlazorChat.Auth;
using Litos.Examples.BlazorChat.Components;
using Litos.Examples.BlazorChat.LitosClient;
using Microsoft.AspNetCore.Authentication.Cookies;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<LitosApiOptions>()
    .Bind(builder.Configuration.GetSection(LitosApiOptions.SectionName));

// LitosUserSession reads/writes the cookie via HttpContext — needs IHttpContextAccessor since
// it's injected into services (JwtRefreshHandler) that run as part of HttpClient's send
// pipeline, not just into components that already have ambient HttpContext access.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<LitosUserSession>();
builder.Services.AddScoped<JwtRefreshHandler>();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.Cookie.Name = "litos_chat_session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        // Matches the refresh token's own 30-day lifetime (JwtTokenService.RefreshTokenLifetime)
        // — no point outliving the credential it carries.
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

// Separate, unauthenticated HttpClient for the login/refresh calls themselves — LitosApiClient's
// HttpClient (below) exists specifically to attach/refresh a bearer token per request, and
// wiring JwtRefreshHandler onto the very calls that mint/refresh tokens would be circular (see
// LitosLoginService's doc comment).
builder.Services.AddHttpClient<LitosLoginService>((sp, client) =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<LitosApiOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
});

builder.Services.AddHttpClient<LitosApiClient>((sp, client) =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<LitosApiOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");

    // A turn's SSE response stays open for as long as the agent keeps working (tool calls, model
    // latency, ...) — the default 100s HttpClient timeout would tear down a long-running turn
    // mid-stream. No per-request cap is imposed on this example's side; Litos.Api itself is the
    // authority on how long a turn may run.
    client.Timeout = Timeout.InfiniteTimeSpan;
}).AddHttpMessageHandler<JwtRefreshHandler>();

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

var app = builder.Build();

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// .NET 10's fingerprinted static web assets (including the framework-provided
// _framework/blazor.web.js) live outside wwwroot when running from source (dotnet run/build) —
// only `dotnet publish` flattens them into a physical wwwroot/_framework, which is how Litos.Api's
// own Dockerfile ends up serving them via plain UseStaticFiles(). Running this example directly
// from source needs the manifest-aware MapStaticAssets() instead; UseStaticFiles() alone 404s on
// blazor.web.js, the SignalR circuit never connects, and every InteractiveServer component
// silently stays non-interactive (bound inputs never update, buttons never re-enable).
//
// MapStaticAssets() itself only resolves correctly under ASPNETCORE_ENVIRONMENT=Development when
// running from source: its dev-time "runtime patching" handler stats the asset's on-disk path from
// the staticwebassets manifest (the NuGet-packaged shared-framework assets, for blazor.web.js) —
// under any other environment name it instead expects the publish-flattened physical file at
// wwwroot/_framework/blazor.web.js, which doesn't exist for a source build, and 500s. See
// launchSettings.json, which must set ASPNETCORE_ENVIRONMENT=Development for this reason.
app.MapStaticAssets();

app.MapAuthEndpoints();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
