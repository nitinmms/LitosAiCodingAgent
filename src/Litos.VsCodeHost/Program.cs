using System.Text.Json;
using Litos.Agent.Tools;
using Litos.Host;
using Litos.Tools.Mcp;
using Litos.Tools.Shell;
using Litos.VsCodeHost;
using Litos.VsCodeHost.Approvals;
using Litos.VsCodeHost.Config;
using Litos.VsCodeHost.Files;
using Litos.VsCodeHost.Mcp;
using Litos.VsCodeHost.Skills;
using Litos.VsCodeHost.Turns;
using Microsoft.Extensions.Logging;

// Local, single-user, no-auth face for the VS Code extension: the extension spawns this process,
// reads the port handshake below off stdout, and talks to it over loopback SSE. See
// ReadMe_VsCodeExtension.md for why this is a separate project rather than reusing Litos.Api
// (Postgres/JWT/multi-tenant auth are hard requirements there, unwanted here) and why every
// request acts as SessionOwner.Local rather than resolving a per-caller identity.
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:0"); // port 0 = OS-assigned free loopback port

// TurnsEndpoints' /turns SSE response (ToSseData) only writes bytes when a real AgentEvent
// fires — it sends no heartbeat during silence — so a quiet gap with no events (a slow tool
// call, or a local model that hasn't produced its first token yet) can run well past Kestrel's
// default MinResponseDataRate (240 B/s after a 5s grace period). Kestrel then aborts the
// connection itself, which the VS Code extension's Node-side fetch (undici) surfaces as a bare
// `TypeError: terminated` — rendered in the webview as a SYSTEM "Error: terminated" bubble with
// no indication it was Kestrel, not the model or a tool, that gave up. That data-rate watchdog
// exists to protect a server from slow/malicious remote clients; this process only ever listens
// on loopback for the bundled extension, so there's nothing to protect against — disable it.
builder.WebHost.ConfigureKestrel(o => o.Limits.MinResponseDataRate = null);

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

// Built-in tools (read_file, shell, ...) are blanket-approved, matching Litos.Gui/Litos.Console's
// own auto-approve model — see ReadMe_ConsoleParityPlan.md's "Revised direction on tool approval"
// for why that's the shared convention across faces for internal tools specifically. MCP tools
// (mcp__{server}__{tool}) are gated per-server by McpConfigStore's Deny/Full/Ask instead, matching
// Litos.Api's model exactly — McpAwareApprovalGate (Litos.Tools.Mcp, already face-agnostic; no
// changes needed there or in Litos.Api/Litos.Gui to add this third consumer) decorates the inner
// blanket-approve gate and only intercepts mcp__-prefixed calls. Constructed directly (new, not via
// DI) before builder.Build() exists, mirroring Litos.Api/Program.cs's own construction order —
// McpConfigStore/PendingApprovalStore need to exist before AddSingleton<IToolApprovalGate> can
// close over them, and before any tool that resolves IToolApprovalGate from the container runs.
var mcpConfigStore = new McpConfigStore();
var pendingApprovalStore = new PendingApprovalStore();
var pendingApprovalRelay = new PendingApprovalRelay(pendingApprovalStore).Start();
var approvalGate = new McpAwareApprovalGate(new AutoApprovalGate(), mcpConfigStore, pendingApprovalStore);
builder.Services.AddSingleton(mcpConfigStore);
builder.Services.AddSingleton(pendingApprovalStore);
builder.Services.AddSingleton(pendingApprovalRelay);
builder.Services.AddSingleton<IToolApprovalGate>(approvalGate);

// McpToolProvider/McpToolSource/McpToolRefreshService, mirroring Litos.Api/Program.cs's own MCP
// wiring (same face-agnostic Litos.Tools.Mcp types, same approvalGate instance reused — no
// changes needed in Litos.Tools.Mcp/Litos.Api/Litos.Gui to add this third consumer), with one
// deliberate difference: InitializeAsync is fire-and-forget here rather than awaited before
// builder.Build(). Unlike Litos.Api/Litos.Gui, this process's own liveness signal (the stdout
// port handshake below) is itself gated on Program.cs finishing — so awaiting a slow/unreachable
// MCP server's up-to-mcpHandshakeTimeout connect here meant the VS Code webview sat unusable for
// that whole time on every window open, not just the first turn seeing an incomplete tool list.
// McpToolRefreshService's poll-with-backoff (started below) already tolerates connections still
// being in flight — RefreshAsync only adds servers not yet in _connections — so a turn sent before
// this finishes just proceeds with whatever tools are connected so far, same as any later turn
// racing a mid-session Unreachable retry already does.
var mcpLoggerFactory = LoggerFactory.Create(loggingBuilder => loggingBuilder.AddConsole());
var mcpHandshakeTimeout = TimeSpan.FromSeconds(30);
var mcpToolProvider = new McpToolProvider(mcpConfigStore, mcpLoggerFactory, approvalGate);
_ = mcpToolProvider.InitializeAsync(mcpHandshakeTimeout, CancellationToken.None);
builder.Services.AddSingleton(mcpToolProvider);
builder.Services.AddSingleton<IToolSource>(new McpToolSource(mcpToolProvider));

var mcpReconcilePollInterval = TimeSpan.FromSeconds(5);
builder.Services.AddSingleton(sp => new McpToolRefreshService(
    mcpToolProvider, mcpReconcilePollInterval, mcpHandshakeTimeout, sp.GetRequiredService<ILogger<McpToolRefreshService>>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<McpToolRefreshService>());

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
        config));
    builder.Services.AddHostedService(sp => sp.GetRequiredService<AgentWorker>());
}

var app = builder.Build();
app.MapConfigEndpoints();
app.MapFilesEndpoints();
app.MapSkillsEndpoints();
app.MapAttachEndpoints();
app.MapMcpEndpoints();
if (isConfigured)
{
    app.MapTurnsEndpoints();
    app.MapAgentSettingsEndpoints();
    app.MapSessionActionsEndpoints();
    app.MapReflectEndpoints();
    app.MapContextEndpoints();
}

// Started (not Run) so the OS-assigned port is known before this process blocks — app.Urls is
// only populated with the real, resolved address once the listener actually opens.
await app.StartAsync();

var boundPort = new Uri(app.Urls.First()).Port;
loopbackBaseUrl.Value = $"http://127.0.0.1:{boundPort}";

// The extension's only startup signal: first stdout line is this single-line JSON handshake.
// Nothing else should write to stdout before this.
Console.WriteLine(JsonSerializer.Serialize(new { port = boundPort }));

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
