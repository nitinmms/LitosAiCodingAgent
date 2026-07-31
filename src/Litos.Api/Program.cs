using Litos.Agent.Tools;
using Litos.Api;
using Litos.Api.Approvals;
using Litos.Api.Auth;
using Litos.Api.Channels;
using Litos.Api.Channels.Telegram;
using Litos.Api.Logs;
using Litos.Api.Turns;
using Litos.Host;
using Litos.Tools.Mcp;
using Litos.Tools.Shell;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Logging;
using Telegram.Bot;

var builder = WebApplication.CreateBuilder(args);

var config = LitosConfig.Load();
builder.Services.AddLitosAgent(config);

var logStore = new LogStore();
builder.Services.AddSingleton(logStore);
builder.Logging.AddProvider(new InMemoryLoggerProvider(logStore));

// A standalone ILoggerFactory sharing the same LogStore-backed provider as the real host logging
// pipeline — needed because MCP server connections are established below, before builder.Build()
// exists to resolve ILoggerFactory from DI. Handshake/connection-failure logs still land on
// /logs this way, same as any other component's ILogger calls.
using var mcpLoggerFactory = LoggerFactory.Create(loggingBuilder => loggingBuilder.AddProvider(new InMemoryLoggerProvider(logStore)));

builder.Services.AddSingleton(_ => new AgentWorker(
    _.GetRequiredService<Litos.Agent.Providers.IChatProviderFactory>(),
    _.GetRequiredService<AgentLoopFactory>(),
    _.GetRequiredService<ToolRegistryFactory>(),
    _.GetRequiredService<Litos.Agent.Session.ITranscriptStore>(),
    config)
{
    AvailableProviders = config.ApiKeys.Keys.Where(LitosConfig.ChatProviderNames.Contains).ToArray(),
});
builder.Services.AddHostedService(sp => sp.GetRequiredService<AgentWorker>());
builder.Services.AddSingleton<AdminTokenProvider>();
builder.Services.AddSingleton<AdminTokenFilter>();

// Channel-agnostic — MCP Ask-mode gating needs this even with no Telegram token configured, so it
// can no longer live only inside the telegramToken-is-not-null branch below.
var pendingApprovalStore = new PendingApprovalStore();
builder.Services.AddSingleton(pendingApprovalStore);

// Constructed directly (not via DI) since it's needed before builder.Build() exists — McpServers
// admin page and McpAwareApprovalGate both read/write the same live instance afterward via the
// singleton registered below.
var mcpConfigStore = new McpConfigStore();
builder.Services.AddSingleton(mcpConfigStore);

// Also constructed directly and registered as the same instance — TelegramGatingApprovalGate
// (built below, also before Build()) needs it, and TelegramSetup.razor/TelegramBridge need the
// identical live object afterward, not a second copy that would silently diverge.
var telegramConfigStore = new TelegramConfigStore();
builder.Services.AddSingleton(telegramConfigStore);

// Telegram bridge — off by default even when a token exists (ReadMe_TelegramIntegrationTool.md
// §3); TelegramSetup.razor's toggle persists TelegramConfig.Enabled and is the only way to turn
// it on. That flag is just state though, so it's re-applied at boot below (after app.Build()) by
// calling StartAsync if it was left Enabled — otherwise every process restart (e.g. a container
// redeploy) would silently stop polling Telegram while the admin UI still claims "Enabled".
// Registered only when a bot token is configured — TelegramSetup.razor checks
// config.GetApiKey("telegram") itself and shows a "set TELEGRAM_BOT_TOKEN" message when absent,
// rather than every consumer of ITelegramBotClient needing to null-check it.
var telegramToken = config.GetApiKey("telegram");
IToolApprovalGate approvalGate;
if (telegramToken is not null)
{
    // Wrapped in RetryingTelegramBotClient so every SendMessage/EditMessageText/SendDocument/etc.
    // call across the bridge gets 429 (flood control) retry-with-backoff for free — see that
    // class's remarks for why this single seam covers the whole surface. Constructed directly
    // (not a DI factory) since TelegramGatingApprovalGate needs this same instance below, before
    // builder.Build() exists — registering the instance itself (not a factory) keeps both the
    // gate and the rest of the Telegram bridge sharing one bot client, not two.
    var telegramBotClient = new RetryingTelegramBotClient(
        new TelegramBotClient(telegramToken), mcpLoggerFactory.CreateLogger<RetryingTelegramBotClient>());
    builder.Services.AddSingleton<ITelegramBotClient>(telegramBotClient);
    builder.Services.AddSingleton<TelegramPairing>();
    builder.Services.AddSingleton<TelegramSessionDriver>();
    builder.Services.AddSingleton<TelegramBridge>();
    builder.Services.AddSingleton<IChannelBridge>(sp => sp.GetRequiredService<TelegramBridge>());

    // Registered here (not in LitosHostBuilder.AddLitosAgent) since it needs ITelegramBotClient,
    // which only exists in this branch. It's still added to the *shared* ToolRegistry — reachable
    // from any turn on this deployment, not just Telegram ones — but SendFileTool itself checks
    // ChannelContext.Current and no-ops with an error outside a Telegram-originated turn, so a web
    // UI turn calling it just gets a clear "only available in a Telegram chat" ToolResult.Error
    // rather than the tool silently vanishing from the model's tool list depending on channel.
    builder.Services.AddSingleton<ITool, SendFileTool>();

    // TelegramGatingApprovalGate needs ITelegramBotClient to seed in-chat Approve/Deny buttons
    // (Ask-mode tools) — auto-approves any turn that isn't Telegram-originated, so
    // McpAwareApprovalGate (wrapped around it below) is what actually gates MCP tools on a
    // non-Telegram turn.
    var telegramGate = new TelegramGatingApprovalGate(
        telegramConfigStore,
        pendingApprovalStore,
        telegramBotClient,
        mcpLoggerFactory.CreateLogger<TelegramGatingApprovalGate>());
    approvalGate = new McpAwareApprovalGate(telegramGate, mcpConfigStore, pendingApprovalStore);
}
else
{
    // No Telegram token configured at all means ChannelContext.Current can never become
    // "telegram" — every non-MCP turn is auto-approved, mirroring Litos.Gui's own GuiApprovalGate.
    approvalGate = new McpAwareApprovalGate(new AutoApprovalGate(), mcpConfigStore, pendingApprovalStore);
}
builder.Services.AddSingleton<IToolApprovalGate>(approvalGate);

// Connections are resolved here, before builder.Build(), so the very first turn already has
// real per-tool schemas (not a collapsed generic mcp_call proxy) rather than an empty tool list
// for however long the first background reconcile takes to run. Bounded per server so a
// slow/unresponsive MCP server can't hang Litos.Api's startup; a server that doesn't respond in
// time is marked Unreachable and picked up later by McpToolRefreshService's retry-with-backoff
// once its own backoff window elapses — no restart needed (see IToolSource registration below).
//
// 30s, not the original design's 10s: measured directly against a real npx-launched reference
// server (@modelcontextprotocol/server-everything) — process spawn + MCP initialize handshake +
// tools/list took ~18s end-to-end even with a warm npm cache, since npx still does a registry
// version-resolution check on every invocation. 10s made every npx-based server (the majority of
// the public MCP ecosystem, per ReadMe_LitosApi_Mcp.md §6) fail as Unreachable on a first-hand
// test run. Per-tool overrides aren't exposed here; this is a global default for v1.
var mcpHandshakeTimeout = TimeSpan.FromSeconds(30);
var mcpToolProvider = new McpToolProvider(mcpConfigStore, mcpLoggerFactory, approvalGate);
await mcpToolProvider.InitializeAsync(mcpHandshakeTimeout, CancellationToken.None);
builder.Services.AddSingleton(mcpToolProvider);

// NOT registered as individual AddSingleton<ITool>(...) per tool — that's what used to freeze
// MCP tools into the DI-resolved IEnumerable<ITool> at container-build time (ToolRegistry's own
// backing collection). Registered as the single IToolSource instead: ToolRegistryFactory reads
// IToolSource.CurrentTools fresh on every turn (see AgentWorker.RunTurnAsync), so a server
// added/enabled/disabled/edited via /mcp after startup is picked up without a restart, and a
// turn already in flight keeps whatever ToolRegistry snapshot it started with.
builder.Services.AddSingleton<IToolSource>(new McpToolSource(mcpToolProvider));

// Reconciles McpToolProvider's connections against McpConfigStore on a poll loop, and retries
// Unreachable servers with backoff — the live counterpart to the one-shot InitializeAsync above.
// 5s, distinct from mcpHandshakeTimeout: cheap since most ticks no-op (unchanged config, no
// backoff due), so a config change is picked up promptly without making the retry itself
// impatient — perServerTimeout still governs how long any individual (re)connect attempt waits.
var mcpReconcilePollInterval = TimeSpan.FromSeconds(5);
builder.Services.AddSingleton(sp => new McpToolRefreshService(
    mcpToolProvider, mcpReconcilePollInterval, mcpHandshakeTimeout, sp.GetRequiredService<ILogger<McpToolRefreshService>>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<McpToolRefreshService>());

// Voice-message transcription (ReadMe_TelegramIntegrationTool.md §7.2) reuses the same OpenAI key
// used for chat, independent of whichever provider is actually configured as the active chat
// provider — TelegramSessionDriver accepts IAudioTranscriber as nullable and falls back to a
// placeholder message when it isn't registered.
var openAiKey = config.GetApiKey("openai");
if (openAiKey is not null)
    builder.Services.AddSingleton<IAudioTranscriber>(_ => new OpenAiAudioTranscriber(new global::OpenAI.OpenAIClient(openAiKey)));

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.Cookie.Name = "litos_admin";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddAntiforgery();

var app = builder.Build();

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapAdminAuthEndpoints();
app.MapTurnsEndpoints();

app.MapGet("/status", (AgentWorker worker) => Results.Ok(new
{
    provider = worker.ProviderName,
    model = worker.Model,
    startedAt = worker.StartedAt,
    turnActive = worker.IsTurnActive,
})).AddEndpointFilter<AdminTokenFilter>();

app.MapRazorComponents<Litos.Api.Components.App>()
    .AddInteractiveServerRenderMode();

if (telegramToken is not null && app.Services.GetRequiredService<TelegramConfigStore>().Current.Enabled)
    await app.Services.GetRequiredService<TelegramBridge>().StartAsync(CancellationToken.None);

// Bounded by its own short timeout so a hung MCP child-process disposal can't block container
// shutdown past Docker's SIGTERM grace period.
app.Lifetime.ApplicationStopping.Register(() =>
{
    using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    mcpToolProvider.ShutdownAsync().WaitAsync(shutdownCts.Token).GetAwaiter().GetResult();
});

app.Run();

// Exposed for WebApplicationFactory-style integration testing.
public partial class Program;
