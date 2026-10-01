using Litos.Agent.Tools;
using Litos.Host;
using Litos.Hosting;
using Litos.Hosting.Turns;
using Litos.Tools.Mcp;
using Litos.VsCodeHost;
using Litos.VsCodeHost.Config;
using Litos.VsCodeHost.Files;
using Litos.VsCodeHost.Mcp;
using Litos.VsCodeHost.Skills;
using Litos.VsCodeHost.Turns;

// Local, single-user, no-auth face for the VS Code extension: the extension spawns this process,
// reads the port handshake below off stdout, and talks to it over loopback SSE. See
// ReadMe_VsCodeExtension.md for why this is a separate project rather than reusing Litos.Api
// (Postgres/JWT/multi-tenant auth are hard requirements there, unwanted here) and why every
// request acts as SessionOwner.Local rather than resolving a per-caller identity.
var builder = WebApplication.CreateBuilder(args);
// Port 0 on loopback, with Kestrel's response data-rate watchdog off — see LoopbackHost.
LoopbackHost.ConfigureLoopback(builder);

var config = LitosConfig.Load();
var isConfigured = config.AvailableChatProviders.Count > 0;

// Deliberately does NOT exit early when unconfigured (unlike this project's first version) — the
// extension needs /config/status and /config/keys reachable precisely when no provider key
// exists yet, so it can drive an in-webview first-run key-entry screen instead of just seeing this
// process die. AddLitosAgent itself tolerates an unconfigured LitosConfig fine (it conditionally
// skips registering keyed IChatProviders with no key rather than throwing); only actually starting
// a turn with zero available providers would fail, and no turn can be started before the webview's
// first-run screen (gated on GET /config/status) lets the user past it.
builder.Services.AddLitosAgent(config);

// MCP tools, the approval gate that guards them (Deny/Full/Ask per server) and the background
// reconnect service — see AddLitosMcp for why initialization is not awaited here.
builder.Services.AddLitosMcp();

// LoopbackBaseUrl.Value is only known after app.StartAsync() resolves the OS-assigned port (see
// below) — but ITool registrations, including ShareFileTool's, must happen before Build(). This
// holder is registered now and populated after StartAsync(); ShareFileTool reads .Value lazily
// inside InvokeAsync (never at construction), and no tool call can happen before StartAsync()
// returns and this process reports its port, so by the time any turn actually runs, the real
// value is always already set — unlike Litos.Api's ShareFileTool, this host is always
// loopback-only, so its own base URL is always knowable and never "operator hasn't configured
// PUBLIC_BASE_URL yet."
var loopbackBaseUrl = new LoopbackBaseUrl();
builder.Services.AddSingleton(loopbackBaseUrl);
builder.Services.AddSingleton<SharedFileStore>();
builder.Services.AddSingleton<ITool>(sp => new ShareFileTool(sp.GetRequiredService<SharedFileStore>(), loopbackBaseUrl));

// AgentWorker's constructor throws if no provider is configured (it resolves a default
// provider/model eagerly) — only construct/register it, and only map the turns endpoints, once
// there's actually at least one usable provider. When unconfigured, /config/status and
// /config/keys are the only endpoints this process serves, which is exactly what the webview's
// first-run screen needs; the extension respawns this process (see ConfigEndpoints' remarks) once
// a key has been saved, and the respawned process takes the branch below.
if (isConfigured)
{
    builder.Services.AddSingleton(sp => new AgentWorker(
        sp.GetRequiredService<Litos.Agent.Providers.IChatProviderFactory>(),
        sp.GetRequiredService<AgentLoopFactory>(),
        sp.GetRequiredService<ToolRegistryFactory>(),
        sp.GetRequiredService<Litos.Agent.Session.ITranscriptStore>(),
        config,
        BuildKernelSessionManager(sp)));
    builder.Services.AddHostedService(sp => sp.GetRequiredService<AgentWorker>());
}

// The bridge's tool source is always the FULL (OFF-equivalent) registry regardless of the
// model-facing toggle state — see KernelHosting.TryCreateSessionManager. The sessionId parameter
// is unused today (one registry for the whole process) but keeps the factory shape session-scoped
// per ReadMe_PTCPersistentKernel.md §4.4.
static Litos.Kernel.KernelSessionManager? BuildKernelSessionManager(IServiceProvider sp)
{
    var toolRegistryFactory = sp.GetRequiredService<ToolRegistryFactory>();
    return KernelHosting.TryCreateSessionManager(_ => toolRegistryFactory.Create(), sp.GetRequiredService<McpToolProvider>());
}

var app = builder.Build();
app.MapConfigEndpoints();
app.MapFilesEndpoints();
app.MapSkillsEndpoints();
app.MapAttachEndpoints();
app.MapMcpEndpoints();
if (isConfigured)
{
    app.MapLitosTurnEndpoints(AttachEndpoints.ReadTurnRequestAsync);
    app.MapAgentSettingsEndpoints();
    app.MapSessionActionsEndpoints();
    app.MapReflectEndpoints();
    app.MapContextEndpoints();
    app.MapLitosKernelEndpoints();
}

// The base URL is set before the handshake is written, so it is already in place by the time
// the extension can send its first request.
await LoopbackHost.StartAndAnnounceAsync(app, port => loopbackBaseUrl.Value = $"http://127.0.0.1:{port}");

await app.WaitForShutdownAsync();
return 0;

namespace Litos.VsCodeHost
{
    /// <summary>Mutable holder for this process's own loopback base URL — see the wiring
    /// comment above Program.cs's ShareFileTool registration for why this indirection exists.</summary>
    public sealed class LoopbackBaseUrl
    {
        public string Value { get; set; } = "";
    }
}
