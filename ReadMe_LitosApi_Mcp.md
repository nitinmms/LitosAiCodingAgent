# Litos.Api — Generic MCP Server Integration Blueprint

**Status: Implemented (2026-07-31), including live/dynamic tool discovery beyond original v1
scope.** See §10 for what shipped, what changed from this plan during implementation, and what
remains explicitly out of scope. §§1-9 below are kept as the historical design record (blueprint →
resolved pre-implementation plan); where §10 conflicts with them, §10 is current truth.

Evaluates and blueprints generic [Model Context Protocol](https://modelcontextprotocol.io) server
integration for `Litos.Api` — connecting to external MCP servers (local stdio child processes or
remote HTTP/SSE endpoints) so their tools appear alongside `read_file`, `shell`, `web_search`, etc.
in the model's tool list. Written before any implementation; no code has changed as a result of
this document. Builds on `ReadMe_AgentDesign.md` (§4.3.2 unconfigured-tool pattern, §9 composition
root) and `ReadMe_Extensibility.md`, which already studied the general "third-party tool
registration" problem and evaluated compiled-DLL, Roslyn-scripting, and out-of-process-stdio
loading strategies — see §1 below for how this document's scope relates to that one.

**Confirmed decisions** (resolved during scoping, before design):

- **Transport**: support both stdio (local child process) and remote Streamable HTTP/SSE from the
  start; stdio ships first since most existing public MCP servers use it.
- **Scope**: global, deployment-wide — one admin-configured set of MCP servers, shared by every
  session on this `Litos.Api` instance. Not per-session, not per-user.
- **Approval**: MCP tool calls are gated through the existing `IToolApprovalGate` seam, mirroring
  Claude Code's own model — a per-server `Deny`/`Ask`/`Full` default with optional per-tool
  overrides, and the tool-call preview shown to the approver includes the tool's arguments.
- **Config & admin UI**: a new `McpConfigStore`, following `TelegramConfigStore`'s shape exactly —
  its own JSON file under the state directory, a live in-memory singleton, and a new Blazor admin
  page for adding/editing/enabling/disabling servers.

## 1. Relationship to `ReadMe_Extensibility.md`

That document asked a broader question — "can *arbitrary third-party code* add tools, commands,
and event hooks to Litos, pi.dev-extension-style?" — and surveyed three loading strategies
(compiled DLL, Roslyn scripting, out-of-process stdio), recommending compiled DLLs *for that
purpose* while explicitly deferring the out-of-process route "until a non-.NET extension author
becomes a real, stated need" (§5, §6).

MCP is that need, but it is a narrower, better-specified problem than general extensibility:

| | General extensions (`ReadMe_Extensibility.md`) | MCP integration (this document) |
|---|---|---|
| Protocol | None — bespoke manifest + loader, invented here | Standardized (JSON-RPC 2.0 over stdio/HTTP), external spec |
| Surface area | Tools, commands, event hooks, custom rendering | Tools only (MCP also defines resources/prompts — out of scope here, see §7) |
| Author ergonomics | Would need a bespoke Litos manifest format | Already-published servers (`npx @modelcontextprotocol/server-*`, `uvx mcp-server-*`, etc.) work unmodified |
| Isolation | Open design question (§5 of that doc) | Settled by the protocol itself — always a separate process or remote endpoint |
| Event hooks (intercept/mutate a tool call) | In scope, and shown to require in-process loading (§4.2–4.3) | Out of scope — MCP has no concept of this; an MCP tool is called, and returns a result, exactly like any other `ITool` |

Because MCP tools sit purely at the `ITool` boundary — call in with JSON arguments, get a
`ToolResult` back — this is squarely the out-of-process option `ReadMe_Extensibility.md` §5
evaluated and deferred, now with a concrete, externally-standardized protocol instead of an
invented one. Building it does not foreclose that document's compiled-DLL recommendation for
*general* extensibility later; they are additive (§5 of that doc anticipated exactly this:
"an extension registry could support both 'local DLL' and 'stdio subprocess' as two
`ExtensionSource` kinds later").

## 2. Where Litos stands today (confirmed by inspection)

- **`ITool` is minimal and MCP-shaped already**: `Name`, `Description`, `ParameterSchema`
  (`JsonElement`, i.e. already JSON Schema), `InvokeAsync(JsonElement arguments, CancellationToken)`
  (`src/Litos.Agent/Tools/ITool.cs:5-11`). This is essentially MCP's own `tools/call` shape — an
  MCP tool's `name`/`description`/`inputSchema` map onto `ITool`'s properties with no impedance
  mismatch, and its JSON-RPC result maps onto `ToolResult(Text, IsError)`
  (`src/Litos.Agent/Tools/ToolResult.cs:3-8`).
- **`ToolRegistry` is built once, synchronously, at container-build time** from whatever
  `IEnumerable<ITool>` DI resolves (`src/Litos.Agent/Tools/ToolRegistry.cs:3-14`). Every existing
  tool is a compile-time `services.AddSingleton<ITool, XTool>()` call in
  `LitosHostBuilder.AddLitosAgent` (`src/Litos.Host/LitosHostBuilder.cs:35-58`). Nothing today adds
  or removes a tool after the container is built. MCP servers need an async handshake
  (`initialize` → `tools/list`) that cannot complete synchronously during DI registration, and can
  fail, reconnect, or be reconfigured at runtime — this is the one real structural gap and §4.1
  below is the design for closing it.
- **`ShellTool`** (`src/Litos.Tools/Shell/ShellTool.cs`) is the only existing child-process
  precedent, and it is *not* directly reusable for MCP stdio transport:
  - It does **not** redirect stdin (`RedirectStandardInput` is never set) — its own doc comment
    (lines 10-22) explains this is a known gap for interactive commands. MCP's stdio transport
    requires a persistent, bidirectional stdin/stdout pipe, so an MCP client needs its own
    `ProcessStartInfo`, not `ShellTool`'s.
  - It combines stdout and stderr into one buffer (lines 66-67) — fine for a one-shot shell
    command, fatal for MCP where stdout must be a clean, uninterleaved JSON-RPC message stream and
    stderr must be captured separately (for logging only).
  - It is one-shot (start, wait for exit, return) — an MCP server process is long-lived and must
    survive across many tool calls within a turn and across turns.
  - Its hard-timeout-plus-`Kill(entireProcessTree: true)` pattern (lines 73-115) **is** directly
    reusable: killing the whole tree matters exactly the same way here, since `npx`/`uvx` spawn a
    grandchild (`node`/`python`) that survives if only the immediate `cmd.exe`/`sh` child is
    killed.
- **`IToolApprovalGate`** (`src/Litos.Tools/Shell/IToolApprovalGate.cs:3-15`) is the seam every
  risky tool call goes through — `RequestAsync(ToolInvocationPreview, ct)` returning
  `Approve`/`ApproveAlways`/`Deny`. Today only `ShellTool`, `WriteFileTool`, `EditFileTool`, and
  `SendFileTool` call it; read-only tools don't. It is deliberately left unregistered by
  `AddLitosAgent` — each face binds its own implementation
  (`LitosHostBuilder.cs:53-55`). In `Litos.Api` specifically, gating is Telegram-only today:
  `TelegramGatingApprovalGate` (`src/Litos.Api/Channels/Telegram/TelegramGatingApprovalGate.cs`)
  auto-approves any turn where `ChannelContext.Current != "telegram"` — meaning **the HTTP API's
  own sessions currently have no approval gating at all**. `AutoApprovalGate` is used instead when
  no Telegram token is configured.
- **`TelegramConfig`/`TelegramConfigStore`**
  (`src/Litos.Api/Channels/Telegram/TelegramConfig.cs`) is the closest and best precedent for MCP
  server configuration: a standalone JSON file (`telegram.json`) under the state directory
  (`LITOS_STATE_DIR` or `~/.litos`, distinct from `LitosConfig`'s `config.json`), a thread-safe
  in-memory singleton (`TelegramConfigStore`, lines 118-147) that is the single source of truth for
  every read and write, and a `Deny`/`Ask`/`Full` `ToolPermission` enum
  (`TelegramConfig.cs:17-22`) keyed by tool name, defaulting to `Deny` for anything unlisted — this
  is the exact shape §5 below extends for MCP.
- **Docker** (`src/Litos.Api/Dockerfile`) has **no Node.js/npm/npx and no `uv`/`uvx`** — it has
  the full .NET SDK and Python3/pip (added for the `shell` tool's own use, lines 24-31). Nearly
  every published reference MCP server is launched via `npx` or `uvx`, so this is a hard blocker
  for stdio MCP servers in the containerized deployment until addressed (§6).
- **Logging**: `LogStore`/`InMemoryLoggerProvider`
  (`src/Litos.Api/Logs/LogStore.cs`, `InMemoryLoggerProvider.cs`) already surface any
  `ILogger<T>.LogWarning`-and-above call on the `/logs` admin page for free — MCP connection/health
  errors should log through `ILogger`, not `Console.WriteLine`, to get this automatically.
- **No existing NuGet dependency** for MCP, JSON-RPC, or `StreamJsonRpc` anywhere in the repo — this
  is a from-scratch integration (§3 below addresses the SDK-vs-hand-rolled choice).

## 3. Transport & SDK choice

Two viable options for the JSON-RPC/MCP protocol layer itself:

- **Official `ModelContextProtocol` C# SDK** (Microsoft/Anthropic-maintained,
  `ModelContextProtocol` + `ModelContextProtocol.Client` NuGet packages) — provides
  `McpClientFactory.ConnectAsync` with built-in stdio and Streamable-HTTP transport
  implementations, `initialize`/`tools/list`/`tools/call` handled internally, and typed
  `McpClientTool` objects exposing `Name`/`Description`/`JsonSchema` directly.
- **Hand-rolled JSON-RPC** over `Process` stdio / `HttpClient` SSE — full control, zero new
  dependency, but reimplements a spec that already has a maintained reference implementation for
  no architectural benefit here (there's no unusual constraint in Litos that the SDK wouldn't fit).

**Recommendation: adopt the official SDK.** It directly produces objects shaped like `ITool`
already needs (name, description, JSON Schema, invoke-with-arguments-returning-result), handles
both transports this document commits to (§0), and MCP's wire protocol has enough edge-case detail
(capability negotiation, cancellation notifications, progress notifications, protocol version
negotiation) that hand-rolling it is pure risk with no offsetting benefit. This mirrors the
existing pattern of using vendor SDKs for provider integration (`Anthropic.SDK`, `OpenAI`,
`Google_GenerativeAI` in `Litos.Providers.*`) rather than hand-rolling each vendor's REST API.

Add the dependency to a **new `Litos.Tools.Mcp` project** (sibling to `Litos.Tools`, referenced
from `Litos.Host`), not into `Litos.Tools` itself — MCP client/connection code is a distinct
concern from the hand-written tools already in `Litos.Tools`, and isolating it keeps
`Litos.Tools.csproj` from picking up a dependency the filesystem/shell tools don't need.

## 4. Architecture

### 4.1 Closing the static-`ToolRegistry` gap

`ToolRegistry` stays exactly as it is (`src/Litos.Agent/Tools/ToolRegistry.cs` — no changes to
`Litos.Agent`, preserving its zero-dependency, provider/UI-neutral status per
`ReadMe_AgentDesign.md` §2). The fix is to make the `IEnumerable<ITool>` DI resolves from
*already reflect* every connected MCP server's tools by the time `ToolRegistry` is constructed —
without blocking container startup on a network/process handshake that could hang or fail.

**Design**: introduce `McpToolProvider` (in `Litos.Tools.Mcp`), an `IHostedService`-independent
singleton that:

1. On construction, reads `McpConfigStore.Current` (§5) — the list of configured servers.
2. Kicks off a connect+`tools/list` handshake per **enabled** server as a background `Task`,
   not on the constructor's calling thread — DI container construction must stay synchronous
   and fast, and a slow/unresponsive MCP server must not block Litos.Api's startup or any other
   face's container build.
3. Exposes `IReadOnlyList<ITool> Tools` — starts empty, populated as each server's handshake
   completes. Whichever face needs it (`AddLitosAgent`) resolves `McpToolProvider` and registers
   a **`McpToolProxy : ITool`** per discovered remote tool via `services.AddSingleton<ITool>(...)`
   — this still fits the "N `ITool`s in the DI collection" model `ToolRegistry` already expects.

The remaining question is *when* the DI collection is finalized relative to *when* the async
handshake completes — a normal DI container is built once and is immutable after
`BuildServiceProvider()`/`app.Build()`. Two sub-options:

- **(a) Block startup on MCP handshakes, with a timeout.** `Program.cs` `await`s
  `McpToolProvider.InitializeAsync(timeout: 10s)` before `services.AddLitosAgent(...)` finishes
  registering tools, so `ToolRegistry`'s one-time snapshot is already complete and correct. A
  server that doesn't respond within the timeout is marked `Unreachable` (visible on the admin
  page, §5.2) and simply contributes zero tools for this process lifetime — consistent with the
  `WebSearchTool`-without-a-key precedent (`ReadMe_AgentDesign.md` §4.3.2: register unconditionally,
  report the failure at the point of use... except here there's no "point of use" possible since
  the tool was never discovered, so the failure surfaces on `/logs` and the admin page instead).
- **(b) Register a small, fixed set of tools eagerly** (e.g. a single generic `mcp_call` tool that
  proxies by name) and resolve the target server lazily inside `InvokeAsync`. Avoids blocking
  startup entirely, but loses per-tool schemas in the model's tool list — the model would see one
  opaque `mcp_call(server, tool, arguments)` entry instead of each real tool with its own
  description and typed schema, materially hurting tool-selection quality (this is exactly the
  problem MCP's own `tools/list` exists to solve — collapsing it back to one generic entry throws
  away the reason to expose typed schemas at all).

**Recommendation: (a), block startup with a bounded timeout (~10s total, run per-server
handshakes concurrently not sequentially).** Litos.Api already has a "restart required" UX
precedent for provider API keys (`Settings.razor:53`) — a short, bounded wait during container
start for MCP servers to come up is consistent with that, and each face already accepts some
startup cost (loading skills, project instructions) before serving its first request. A server
that's down at startup shows as `Unreachable` until the process is restarted (mirroring how a
changed API key needs a restart today) — reconnect-without-restart is a reasonable v2 refinement,
not required for v1 (§7).

`McpToolProxy : ITool` itself is thin: constructed with a reference to the already-connected
`McpClientTool` (or equivalent SDK handle) plus the owning server's name; `Name` returns
`mcp__{serverName}__{toolName}` (§4.2); `InvokeAsync` calls `approvalGate.RequestAsync(...)` (§4.3)
then forwards to the SDK's `CallToolAsync`, mapping the MCP result content back to `ToolResult`.

### 4.2 Naming

Adopt Claude Code's own convention directly: `mcp__{serverName}__{toolName}`. This was a deliberate
answer to the clarifying question about approval gating (§0) — reusing a convention the user is
already familiar with from Claude Code, rather than inventing a new one, and it has a real
structural benefit: the double-underscore-delimited name **is** the permission-rule key, so
server-level and tool-level rules (§4.3) both fall out of simple string matching on `ITool.Name`,
with no separate "which server owns this tool" lookup needed anywhere downstream (the approval
gate, the admin UI, `/logs`) once the tool is constructed.

Collision handling: reject registration of a second server using a `serverName` already in use
(config-level validation in `McpConfigStore`, §5.1) rather than a runtime collision — this mirrors
`ReadMe_Extensibility.md` §3.2's "closer root wins" being explicitly flagged there as one option
among several; for MCP there's no "closer" concept (servers aren't hierarchical), so reject-on-add
is the simpler, unsurprising choice consistent with this codebase's stated preference for
transparency over cleverness (`ReadMe_AgentDesign.md` §2).

### 4.3 Approval gating

Extends `TelegramConfig.ToolPermissions`' `Deny`/`Ask`/`Full` model (§2) rather than inventing a
new one, per the confirmed decision (§0):

- `McpConfig` (§5.1) carries a `Deny`/`Ask`/`Full` **default permission per server**, plus an
  optional `IReadOnlyDictionary<string, ToolPermission>` of **per-tool overrides** keyed by the
  full `mcp__{server}__{tool}` name — checked first, falling back to the server default, falling
  back to `Deny` if the server itself has no explicit default (safe-by-default, matching
  `TelegramConfig.PermissionFor`'s existing fallback).
- `McpToolProxy.InvokeAsync` calls `approvalGate.RequestAsync(new ToolInvocationPreview(Name,
  summary, argsPreview), ct)` before forwarding to the SDK — same call shape `ShellTool` already
  uses (`ShellTool.cs:45-46`). `ToolInvocationPreview.DiffOrCommand` carries the tool's JSON
  arguments (pretty-printed) so an approver sees exactly what Claude Code shows: tool name +
  arguments before approving (§0 research finding).
- **This requires generalizing the gate itself**, since today `IToolApprovalGate` has exactly one
  real implementation path in `Litos.Api` — `TelegramGatingApprovalGate`, which only consults
  `TelegramConfig` and only gates Telegram-originated turns (auto-approving everything else,
  including the HTTP API's own sessions). MCP tool calls need to be gated **regardless of which
  channel originated the turn** — an HTTP-API-only deployment with no Telegram bridge configured
  must still gate MCP tools if the user configured `Ask` for a server. Two ways to reach this,
  both compatible with the existing seam:
  - Extend `TelegramGatingApprovalGate` to also consult `McpConfigStore` for
    `mcp__*`-prefixed tool names, independent of its `ChannelContext.Current == "telegram"` check
    — i.e. the Telegram short-circuit applies to Telegram-specific tools
    (`shell`/`write_file`/`edit_file`) but MCP tools are channel-agnostic.
  - Introduce a small composable `McpAwareApprovalGate` decorator wrapping whichever gate a face
    already registers (`AutoApprovalGate` or `TelegramGatingApprovalGate`), consulted first for
    `mcp__`-prefixed names and delegating everything else unchanged.

  **Recommendation: the decorator.** It keeps `TelegramGatingApprovalGate` scoped to what its name
  says (Telegram-specific gating) rather than growing a second, unrelated responsibility, and it
  composes correctly regardless of which face or which base gate is in play — `Litos.Gui`'s
  `GuiApprovalGate` gets MCP gating for free by wrapping it the same way, with no Telegram
  dependency pulled in. Registered in `LitosHostBuilder.AddLitosAgent` itself (wrapping whatever
  each face's own `IToolApprovalGate` registration provides), the one piece of MCP wiring that
  belongs in the shared composition root rather than a specific face — since gating must be
  consistent across `Litos.Api`/`Litos.Gui`/`Litos.Console` if MCP servers are ever face-agnostic
  (a boundary this document doesn't need to cross yet, since scope is `Litos.Api`-only, §0 — but
  the gate placement should not foreclose it).
- `Ask`-mode MCP calls in `Litos.Api` reuse `PendingApprovalStore`'s existing async
  request/response bridge (`TaskCompletionSource` keyed by `Guid`, 10-minute auto-deny timeout,
  `src/Litos.Api/Approvals/PendingApprovalStore.cs`) — surfaced today only via Telegram's inline
  keyboard. A `Litos.Api`-only, non-Telegram deployment that wants `Ask`-mode MCP gating needs
  *some* UI to approve from; §5.2/§7 addresses this gap.

### 4.4 Process/connection lifecycle

- **stdio servers**: `McpToolProvider` owns one long-lived child process per enabled stdio server,
  started once (during the bounded startup window, §4.1) and kept alive for the process lifetime
  of `Litos.Api` — not restarted per tool call, unlike `ShellTool`'s one-shot model. Uses the SDK's
  stdio transport (`ProcessStartInfo` with `RedirectStandardInput = true` in addition to
  `ShellTool`'s existing `RedirectStandardOutput`/`RedirectStandardError = true`), with stdout
  reserved exclusively for JSON-RPC framing and stderr piped to `ILogger` for diagnostics — the
  clean-separation requirement §2 flagged `ShellTool` as unable to provide.
  `TryKillProcessTree`'s `Kill(entireProcessTree: true)` pattern (`ShellTool.cs:104-115`) is reused
  verbatim for graceful shutdown (`IHostApplicationLifetime.ApplicationStopping`, the same signal
  `AgentWorker` already links into, `AgentWorker.cs:41`) and for an admin-triggered "disable this
  server" action (§5.2) — killing an `npx`-spawned tree is exactly the scenario that comment was
  written for.
- **Remote HTTP/SSE servers**: no child process — the SDK's Streamable-HTTP transport holds a
  persistent connection with its own reconnect semantics; `McpToolProvider` just needs to surface
  connection-lost state to `/logs` and the admin page, not manage a process tree.
- **Cancellation**: an MCP `tools/call` in flight when a turn is cancelled should propagate
  cancellation to the server via the SDK (MCP defines a `notifications/cancelled` message for
  this) rather than only cancelling the local `await` — leaving a remote tool call running
  server-side after the local turn gave up would be a silent resource leak on the MCP server's
  side. No hard-timeout-independent-of-`ct` is needed the way `ShellTool` has one (§2) — MCP tool
  calls should respect the turn's own cancellation and the provider's existing idle-stream timeout
  the same way any other tool does; a *separate* per-server "server took too long to start"
  timeout is still needed at the §4.1 handshake step specifically.

## 5. Configuration & admin UI

### 5.1 `McpConfigStore`

New file, `src/Litos.Api/Mcp/McpConfig.cs` (or `Litos.Tools.Mcp/McpConfig.cs` if the store should
be shared across faces later — kept in `Litos.Api` for v1 per the confirmed `Litos.Api`-only scope,
§0), mirroring `TelegramConfig`/`TelegramConfigStore` exactly:

```csharp
public sealed record McpServerDefinition(
    string Name,                          // unique key; also the mcp__{Name}__ prefix
    McpTransportKind Transport,            // Stdio | Http
    string? Command,                       // Stdio: e.g. "npx"
    IReadOnlyList<string>? Args,           // Stdio: e.g. ["-y", "@modelcontextprotocol/server-filesystem", "/workspace"]
    IReadOnlyDictionary<string, string>? Env,  // Stdio: extra env vars for the child process
    string? Url,                           // Http: the server's endpoint
    bool Enabled,
    ToolPermission DefaultPermission,      // Deny | Ask | Full — mirrors TelegramConfig.ToolPermission
    IReadOnlyDictionary<string, ToolPermission>? ToolOverrides);  // keyed by mcp__{Name}__{tool}

public sealed record McpConfig(IReadOnlyList<McpServerDefinition> Servers)
{
    public static McpConfig Empty { get; } = new([]);
    // Load(path) / Save(path) — identical shape to TelegramConfig, same JsonSerializerOptions
    // (WriteIndented, JsonStringEnumConverter for McpTransportKind/ToolPermission).
}

public sealed class McpConfigStore  // identical shape to TelegramConfigStore: Lock, Current, Update(mutate)
```

Persisted to `{LITOS_STATE_DIR or ~/.litos}/mcp.json` — its own file, not folded into
`LitosConfig`/`config.json`, for the same reason `TelegramConfig` isn't: it's `Litos.Api`-specific
runtime state (§0 scope), not something `Litos.Gui`/`Litos.Console` need to know about today, and
keeping it separate avoids growing `LitosConfig` (which `Litos.Host.LitosConfig.cs` documents as
env-var-first, chat-provider-and-tool-key-focused) with an unrelated concern.

`ToolPermission` itself (`Deny`/`Ask`/`Full`) should move to a shared location (e.g.
`Litos.Tools.Shell` alongside `IToolApprovalGate`, or a new small shared file) rather than staying
defined inside `TelegramConfig.cs` — both `TelegramConfig` and `McpConfig` need the identical enum,
and duplicating it would invite drift.

### 5.2 Admin UI — `Components/Pages/McpServers.razor`

New page, `@page "/mcp"`, following `Settings.razor`'s exact shape (inject the store singleton
directly, bind form fields, no HTTP round-trip since it's server-rendered Blazor):

- A table of configured servers: name, transport, command/URL, enabled toggle, status
  (`Connected` / `Unreachable` / `Connecting…`, sourced from `McpToolProvider`'s live state — not
  `McpConfigStore`, since "configured" and "actually connected" are different facts, the same
  distinction `AgentWorker.IsTurnActive` vs. `LitosConfig` already draws for provider/model),
  default permission (`Deny`/`Ask`/`Full` dropdown, same control shape as
  `TelegramSetup.razor`'s "Tool access" section already uses per §2's cross-reference).
- Add/edit form: name, transport radio (stdio vs. HTTP), command+args+env (stdio) or URL (HTTP),
  enabled checkbox, default permission.
- Per-tool override list, populated once a server is `Connected` and its tools are known (empty/
  disabled until then) — lets an admin set e.g. `mcp__github__delete_repository` to `Full`→`Deny`
  even if the server default is `Ask`.
- Same "changes to a *disabled→enabled* transition or a command/args/env edit require restarting
  the container" messaging `Settings.razor:53` already uses for API keys — consistent with §4.1's
  decision to resolve MCP connections once at startup rather than hot-reload them for v1. A
  `Deny`/`Ask`/`Full` permission change, by contrast, **can** apply live (`McpConfigStore.Update`
  writes through immediately, and `McpAwareApprovalGate` reads `McpConfigStore.Current` per-call,
  not a startup snapshot) — the UI should say so, matching the precision `Settings.razor` already
  applies to distinguishing "restart required" (API keys) from "applies to the next turn"
  (provider/model).
- **`Ask`-mode approval surface**: since `PendingApprovalStore`'s only current UI is Telegram's
  inline keyboard (§4.3), a `Litos.Api`-only deployment needs *something* here — at minimum, a
  "Pending approvals" section on this page (or a shared one) listing outstanding
  `PendingApprovalStore` entries with Approve/Deny buttons, reusing the store as-is (it's already
  channel-agnostic — a `Guid`-keyed `TaskCompletionSource`, nothing Telegram-specific about the
  store itself, only about how `TelegramGatingApprovalGate` populates it). Without this, `Ask`-mode
  MCP gating on a non-Telegram deployment has no way to ever resolve — a real gap the design must
  close, not defer, since `Ask` is one of the three approval options this document commits to (§0).

## 6. Docker

Add to `src/Litos.Api/Dockerfile`'s runtime stage, alongside the existing `python3`/`pip3` install
(lines 30-31):

- **Node.js + npm** (covers `npx`-launched servers — the majority of the published MCP server
  ecosystem today).
- **`uv`** (covers `uvx`-launched Python MCP servers — the other common ecosystem convention).

Both are widely-referenced enough patterns (`nodesource` apt repo or the official Node Docker
install script; `uv`'s official installer script) that no novel packaging decision is needed here —
this is additive to the existing multi-stage build, not a restructuring of it. Confirm the final
image size increase is acceptable (Node + npm is a non-trivial addition) — worth a quick check
during implementation, not a blocker to this design.

**Filesystem confinement**: per the existing `/workspace` (bind-mounted, `LITOS_WORKSPACE`) +
`/data` (`HOME`, state) contract (`ReadMe_HeadlessServiceTool.md` §5.5/§5.6), a filesystem-oriented
MCP server (e.g. `@modelcontextprotocol/server-filesystem`) should be configured with `/workspace`
as its root, not an arbitrary path — the admin UI (§5.2) could default the `Args` field for a
recognized `server-filesystem` command to `$LITOS_WORKSPACE`, but this is a UX nicety, not a
structural requirement; the config format (§5.1) places no constraint on `Args` and the operator is
free to point a stdio server anywhere the container can reach.

## 7. Explicitly out of scope for v1

- **MCP resources and prompts** — MCP defines two other primitives beyond tools (`resources/list`+
  `resources/read` for read-only data sources, `prompts/list`+`prompts/get` for reusable prompt
  templates). Neither maps onto an existing Litos concept as cleanly as tools do onto `ITool`
  (resources are closer to `SkillDiscovery`'s progressive-disclosure content model, §2 of
  `ReadMe_Extensibility.md`; prompts have no analog at all today). Worth a follow-up document once
  tool integration has shipped and proven the MCP client plumbing, not designed speculatively here.
- **Hot reconnect / live config-change without restart** — §4.1 and §5.2 both commit to
  "connections resolved once at startup, config edits need a restart," consistent with the existing
  API-key precedent. Live reconnect (detecting a `Command`/`Args`/`Url` edit and restarting just
  that server's connection without a full container restart) is a reasonable v2 improvement once
  the basic lifecycle is proven.
- **Per-session or per-user MCP server scope** — confirmed out of scope (§0); would require
  `ToolRegistry` to become session-scoped rather than the current process-wide singleton, a
  materially larger change than this document's global-scope design.
- **`Litos.Gui`/`Litos.Console` support** — confirmed `Litos.Api`-only scope (§0). §4.3 notes where
  the design deliberately avoids foreclosing this (gate composition in the shared
  `LitosHostBuilder`), but `McpConfigStore`/the admin UI are `Litos.Api`-specific for v1.
  Extending to the other faces is a follow-up that would need its own UI per face (Avalonia,
  Terminal.Gui) — no shared-UI shortcut, the same conclusion `ReadMe_Extensibility.md` §4.4 reached
  for custom rendering generally.
- **MCP server marketplace/discovery** (e.g. browsing a catalog of known servers to one-click
  install) — v1 is manual config entry only, matching how provider API keys and Telegram setup
  work today.

## 8. Suggested build sequence

1. **`Litos.Tools.Mcp` project** — add the `ModelContextProtocol` SDK dependency (§3),
   `McpToolProxy : ITool` (§4.1, §4.2), and the connect/handshake logic for a single stdio server
   with no config UI yet (hardcode one server for local testing). Proves the SDK integration and
   the `ITool`-shaped proxy end to end.
2. **`McpConfig`/`McpConfigStore`** (§5.1) + wiring multiple servers from config into
   `McpToolProvider` at startup (§4.1), including the bounded-timeout/`Unreachable` handling.
   Remote HTTP/SSE transport added alongside stdio here, not deferred to a later milestone (§0
   commits to both from the start).
3. **Approval gating** — `McpAwareApprovalGate` decorator (§4.3), per-server/per-tool permission
   resolution, `PendingApprovalStore` reuse for `Ask` mode.
4. **`McpServers.razor` admin page** (§5.2), including the pending-approvals surface for non-
   Telegram deployments — this is what makes `Ask` mode actually usable end to end, so it belongs
   with the gating milestone's completion, not deferred further.
5. **Dockerfile** (§6) — Node/npm/`uv` additions, verified against at least one real published
   stdio server (e.g. `@modelcontextprotocol/server-filesystem` against `/workspace`) and one real
   remote HTTP MCP server if one is available for testing.

## 9. Implementation plan (as-decided, supersedes conflicting detail above)

Everything above (§1-§8) is the original pre-implementation blueprint, kept as-is for its
reasoning. This section is the concrete, resolved plan produced by a full clarifying-questions →
codebase-exploration → three-way architecture-comparison pass done immediately before
implementation started. Where this section conflicts with §1-§8 above, **this section wins** —
the conflicts are noted inline with why.

### 9.0 Scope confirmed

Full feature, all 5 milestones from §8, in one implementation pass (not a single-milestone slice).

### 9.1 Corrections to §1-§8 found during codebase re-verification

- **§4.3's "wraps whatever each face's own `IToolApprovalGate` registration provides... registered
  in `LitosHostBuilder.AddLitosAgent` itself" does not work.** Confirmed by reading
  `LitosHostBuilder.cs:53-55` and every face's `Program.cs`: `AddLitosAgent` always runs *before*
  any face registers its `IToolApprovalGate` (each face registers its gate on the
  `IServiceCollection` `AddLitosAgent` returns, then calls `Build()`). At the moment
  `AddLitosAgent` executes, no gate has been registered yet by anyone, so it has nothing to wrap.
  **Resolution**: `McpAwareApprovalGate` is wired directly in `Litos.Api/Program.cs`, not
  `LitosHostBuilder`. Concretely: edit the two existing gate-construction call sites (the
  `new TelegramGatingApprovalGate(...)` and `new AutoApprovalGate()` inside `Program.cs`'s
  `if (telegramToken is not null) { ... } else { ... }` block, currently lines 45-83) so each
  wraps its result in `new McpAwareApprovalGate(innerGate, mcpConfigStore, pendingApprovalStore)`
  before registering it as `IToolApprovalGate`. No `LitosHostBuilder` changes. This also means the
  doc's framing of this as "the one piece of MCP wiring that belongs in the shared composition
  root" (§4.3) is **not** how it's actually built — it's Api-only wiring, consistent with the
  confirmed Api-only scope (§7).
- **§4.4's claim that `ShellTool.TryKillProcessTree`'s `Kill(entireProcessTree: true)` pattern is
  "reused verbatim" for MCP shutdown does not hold.** `ShellTool` owns its `Process` object
  directly; the MCP SDK's stdio transport owns the child process internally and does not expose a
  raw `Process` handle in the same way. **Resolution**: shutdown is `IMcpClient.DisposeAsync()`
  (or equivalent) per connected server, called from `IHostApplicationLifetime.ApplicationStopping`.
  Verify at implementation time whether the SDK's own dispose reliably kills the whole process tree
  (most JSON-RPC stdio clients close stdin and wait for the child to exit on its own, which is
  usually sufficient for well-behaved MCP servers) — if grandchild processes (e.g. `npx` → `node`)
  survive disposal in testing, that's a real follow-up, not something to silently assume away.
- **`ModelContextProtocol.Client` is not a separate NuGet package today.** Confirmed via NuGet API:
  current stable is `ModelContextProtocol` **2.0.0** (published 2026-07-28), which pulls in
  `ModelContextProtocol.Core` 2.0.0 as a transitive dependency; there is no
  `ModelContextProtocol.Client` package. **Resolution**: `Litos.Tools.Mcp.csproj` references only
  `ModelContextProtocol` (version `2.0.0`), targeting `net10.0`. Re-verify the client-type
  namespace (`ModelContextProtocol.Client`) still matches at implementation time since the SDK
  reorganized package boundaries once already between preview and 2.0.
- **`Approvals.razor` was not merely superseded — it was deliberately deleted.** A comment in
  `TelegramGatingApprovalGate.cs` states the browser `/approvals` page was removed once Telegram's
  in-chat buttons became the only gating surface needed. §5.2's "Pending approvals... reusing the
  store as-is" is accurate for the *store* (`PendingApprovalStore` has zero Telegram-specific code)
  but the *UI* needs to be rebuilt, not resurrected from source control.
- **No existing precedent for pre-`Build()`, timeout-bounded async startup work.** The only
  existing async-startup precedent (`TelegramBridge.StartAsync`, `Program.cs:128-129`) runs *after*
  `app.Build()` with no timeout at all. §4.1's recommended option (a) is new territory for this
  codebase, not a mirrored pattern — implement carefully, no existing code to copy the shape from.

### 9.2 Decisions confirmed with the user (2026-07-31)

1. **Scope**: all 5 milestones, one pass.
2. **Gate wiring**: `McpAwareApprovalGate` instantiated directly in `Litos.Api/Program.cs` (§9.1
   above), not via `LitosHostBuilder`.
3. **Startup timing**: bounded (~10s) MCP handshake runs before `app.Build()`, blocking — required
   because `ToolRegistry` snapshots `IEnumerable<ITool>` at container-build time, and per-tool
   schemas (not a collapsed generic `mcp_call` proxy) are required for tool-selection quality.
4. **Approvals UI**: build a pending-approvals section so `Ask` mode is usable without Telegram
   configured — rebuilding the concept, not the deleted file.
5. **SDK**: `ModelContextProtocol` 2.0.0 (latest stable at decision time; re-check for newer stable
   releases before implementing).
6. **`McpConfigStore`/`McpConfig` location**: **`Litos.Tools.Mcp`**, not `Litos.Api/Mcp/` — a
   deliberate deviation from §5.1's stated v1 default, chosen for future cross-face (`Litos.Gui`/
   `Litos.Console`) reuse even though only `Litos.Api` wires it up today. `Litos.Tools.Mcp` itself
   stays fully face-agnostic; only `Litos.Api/Program.cs` and `McpServers.razor` are Api-specific.
7. **`ToolPermission` enum**: moves out of `TelegramConfig.cs` into `Litos.Tools/Shell/`, alongside
   `IToolApprovalGate`/`ApprovalDecision`/`ToolInvocationPreview` (same file,
   `IToolApprovalGate.cs`, or a new sibling file in that folder — implementer's call, no need for a
   new folder). `TelegramConfig.cs` keeps using it via `using Litos.Tools.Shell;`.
8. **Name-collision handling**: reject server names containing `"__"` at `McpConfigStore.Update`
   time (not just at the UI layer), plus reject duplicate server names in the same call. No
   documented Claude Code precedent was found for this specific edge case (double underscore
   inside a server/tool name) — this is Litos's own choice, matching §4.2's original
   reject-on-add recommendation.
9. **`PendingApprovalStore` must move.** Discovered during architecture design, not originally
   anticipated: `Litos.Tools.Mcp` cannot reference `Litos.Api` (wrong direction in the project
   graph — `Litos.Api → Litos.Host → Litos.Tools.Mcp`, never the reverse), so
   `McpAwareApprovalGate` (which needs `PendingApprovalStore` for `Ask`-mode) cannot depend on it
   at its current location (`src/Litos.Api/Approvals/PendingApprovalStore.cs`). Since the class has
   zero actual Telegram-specific code (confirmed by full read — only depends on
   `Litos.Tools.Shell.ApprovalDecision`/`ToolInvocationPreview`), **move it to
   `src/Litos.Tools/Shell/PendingApprovalStore.cs`** (namespace `Litos.Tools.Shell`), updating all
   call sites (`TelegramGatingApprovalGate`, `TelegramSessionDriver.HandleApprovalCallbackAsync`,
   `Program.cs` registration). Pure namespace move, zero logic changes. This also means Telegram's
   own pending approvals and MCP's `Ask`-mode approvals share one store/one UI list for free.
10. **DI mechanics for getting MCP tools into the container before `Build()`**: **plain
    construction, not a scratch/throwaway `IServiceProvider`.** `McpConfigStore` and
    `McpToolProvider` are constructed directly with `new(...)` in `Program.cs` (not resolved via
    DI) before `builder.Build()`; `InitializeAsync` is awaited; the resulting already-initialized
    `McpToolProvider` instance and each discovered `McpToolProxy` are then registered into
    `builder.Services` as pre-built singleton instances (`AddSingleton(instance)`, not a factory).
    Two of three independently-produced architecture proposals reached for a throwaway
    `BuildServiceProvider()` here and both had to patch a real bug in it (the scratch container's
    connections get disposed unless *instances*, not factories, are carefully re-registered into
    the real container) — plain construction avoids the whole hazard. `IToolApprovalGate` for the
    MCP tools' gate dependency is threaded through explicitly (constructed before this block, same
    as today) rather than resolved from a container.
11. **Connection abstraction**: **no `IMcpServerConnection` interface** — a concrete
    `McpServerConnection` class handles one server's connect/handshake/state, matching this
    codebase's existing style (`ShellTool` itself isn't tested against a fake process; there's no
    established fakes-over-interfaces pattern here). Tests for the crash/timeout/success states use
    a tiny real test-fixture MCP server script, not a hand-maintained fake of the SDK.
12. **Pending-approvals UI placement**: a small reusable
    `src/Litos.Api/Components/Approvals/PendingApprovalsPanel.razor` component, embedded inside
    `McpServers.razor` — not inlined directly as markup in that page. Genuinely shared
    infrastructure (Telegram's own `Ask`-mode approvals become visible on the same panel
    automatically, useful for debugging a stuck Telegram turn), costs one extra small file.

### 9.3 Concrete file plan

**New project**: `src/Litos.Tools.Mcp/Litos.Tools.Mcp.csproj` — `net10.0`, references
`Litos.Agent` + `Litos.Tools`, package reference `ModelContextProtocol` `2.0.0`. Referenced
directly by `Litos.Api.csproj` (not transitively via `Litos.Host`, since `LitosHostBuilder` is
deliberately untouched per decision 2 — only `Litos.Api/Program.cs` and `McpServers.razor` touch
`Litos.Tools.Mcp` types).

**New files**:
- `src/Litos.Tools.Mcp/McpConfig.cs` — `McpTransportKind` enum, `McpServerDefinition` record
  (fields per §5.1's original sketch), `McpConfig` record with `Empty`/`Load`/`Save`
  (mirrors `TelegramConfig` exactly: `WriteIndented`, `JsonStringEnumConverter`, tolerant-of-
  missing/corrupt-file `Load` returning `Empty`; no migration needed, net-new file format).
- `src/Litos.Tools.Mcp/McpConfigStore.cs` — mirrors `TelegramConfigStore` exactly (`Lock`,
  `_stateFilePath` defaulted to `{LITOS_STATE_DIR or ~/.litos}/mcp.json`, `Current` getter under
  lock, `Update(Func<McpConfig,McpConfig> mutate)` under lock + `Save`) plus decision 8's
  validation (reject `"__"` in any server name, reject duplicate names) run inside `Update` before
  persisting, so every mutation path goes through it with no separate UI-layer duplicate check.
- `src/Litos.Tools.Mcp/McpServerConnection.cs` — one server's connection lifecycle: `Connecting`/
  `Connected`/`Unreachable` status (decision 11's concrete-class approach), `ConnectAsync(ct)`
  performing connect + `ListToolsAsync`, stdio transport with `RedirectStandardInput` in addition
  to output/error (unlike `ShellTool`), stderr piped line-by-line to `ILogger` (not combined with
  stdout), HTTP transport via the SDK's Streamable-HTTP client. Both transports from the start
  (§0's confirmed decision). Crash and timeout both collapse to `Unreachable` with the distinguishing
  detail preserved in an `Error` string for `/logs`/the admin page — no separate 4th state.
- `src/Litos.Tools.Mcp/McpToolProxy.cs` — `ITool` implementation: `Name` = `mcp__{server}__{tool}`,
  `Description`/`ParameterSchema` proxied from the discovered `McpClientTool`, `InvokeAsync` calls
  `approvalGate.RequestAsync(...)` once (same shape as `ShellTool`) with pretty-printed JSON
  arguments as the preview detail, then forwards to the connection's `CallToolAsync`, mapping SDK
  result content back to `ToolResult.Ok`/`Error`. Catches connection-level exceptions (crashed/
  unreachable server mid-call) and turns them into `ToolResult.Error` rather than an unhandled
  exception, propagates `OperationCanceledException` on genuine cancellation so the SDK's own
  `notifications/cancelled` path fires.
- `src/Litos.Tools.Mcp/McpToolProvider.cs` — orchestrator: `InitializeAsync(TimeSpan
  perServerTimeout, ct)` runs one `ConnectAsync` per *enabled* configured server concurrently
  (`Task.WhenAll`, individually bounded via a linked/timeout `CancellationTokenSource` per server —
  a slow server must not delay a fast one), never throws (a failed/timed-out server contributes
  zero tools and an `Unreachable` state, everything else still starts normally). Exposes
  `IReadOnlyList<ITool> Tools` (populated only from `Connected` servers) and per-server state for
  the admin page. `ShutdownAsync()` disposes every connection; wired to
  `IHostApplicationLifetime.ApplicationStopping` in `Program.cs`, bounded by its own short timeout
  so a hung child process can't block container shutdown past Docker's SIGTERM grace period.
- `src/Litos.Tools.Mcp/McpAwareApprovalGate.cs` — decorator: `RequestAsync` delegates unchanged to
  `inner` for any non-`mcp__`-prefixed tool name; for `mcp__{server}__{tool}` names, parses the
  server name via `Split("__", 3)`, looks up the server's `DefaultPermission`/per-tool
  `ToolOverrides` (override checked first, then server default, then `Deny` if the server isn't
  found in config at all — e.g. a stale name from a since-removed server), and resolves
  `Deny`/`Full` synchronously or bridges `Ask` through `PendingApprovalStore.Add(preview)` (moved
  location, decision 9). Constructor takes `IToolApprovalGate inner, McpConfigStore configStore,
  PendingApprovalStore approvals` — lives in `Litos.Tools.Mcp` (face-agnostic code) but is only
  ever *instantiated* in `Litos.Api/Program.cs` today (decision 2).
- `src/Litos.Tools/Shell/PendingApprovalStore.cs` — **moved** from `src/Litos.Api/Approvals/`
  (decision 9), namespace `Litos.Api.Approvals` → `Litos.Tools.Shell`, zero logic changes.
- `src/Litos.Tools/Shell/ToolPermission.cs` (or appended to `IToolApprovalGate.cs`) — the enum
  moved from `TelegramConfig.cs` verbatim (decision 7).
- `src/Litos.Api/Components/Approvals/PendingApprovalsPanel.razor` — reusable component (decision
  12): renders `PendingApprovalStore.List()`, subscribes to `Added`/`Resolved` for live updates
  (mirrors `Logs.razor`'s existing `LogStore.Added` subscription pattern exactly — subscribe in
  `OnInitialized`, `InvokeAsync(StateHasChanged)` in the handler, unsubscribe in `Dispose`), each
  row has Approve/Deny buttons calling `PendingApprovalStore.Resolve(id, decision)` directly (no
  HTTP round-trip, server-rendered Blazor).
- `src/Litos.Api/Components/Pages/McpServers.razor` — `@page "/mcp"`, `[Authorize]`,
  `@rendermode InteractiveServer`, following `Settings.razor`'s inject-the-store-directly shape:
  server table (name/transport/command-or-url/live status from `McpToolProvider`/enabled/default
  permission), add-edit form (name/transport radio/command+args+env or url/enabled/default
  permission — reuses `TelegramSetup.razor`'s `RenderPermissionButtons` three-button pattern,
  copy-paste is fine for a second call site per the "extract on the third use" convention already
  implied by this codebase's style), per-tool override rows once a server is `Connected`,
  "restart required for command/args/enabled changes, live for permission changes" messaging
  matching `Settings.razor:53`'s precedent, and `<PendingApprovalsPanel />` embedded at the bottom.
- Nav link to `/mcp` added to whichever layout file holds the existing `/settings`/`/logs`/
  `/telegram` links (`MainLayout.razor` or equivalent — confirm exact file at implementation time).

**Modified existing files**:
- `src/Litos.Api/Channels/Telegram/TelegramConfig.cs` — remove the `ToolPermission` enum
  definition, add `using Litos.Tools.Shell;`.
- `src/Litos.Api/Channels/Telegram/TelegramGatingApprovalGate.cs` — update `using` for
  `PendingApprovalStore`'s new namespace; no logic change.
- `src/Litos.Api/Channels/Telegram/TelegramSessionDriver.cs` — same `using` update for
  `HandleApprovalCallbackAsync`'s `PendingApprovalStore` reference.
- `src/Litos.Api/Components/Pages/TelegramSetup.razor` — add `@using Litos.Tools.Shell;` for
  `ToolPermission`.
- `src/Litos.Api/Litos.Api.csproj` — add `<ProjectReference>` to `Litos.Tools.Mcp.csproj`.
- `src/Litos.Api/Program.cs` — the substantial change (see §9.4 below for exact sequencing).
- `src/Litos.Api/Dockerfile` — add, in the runtime stage after the existing `python3`/`pip3`
  install: Node.js + npm via the NodeSource setup script, and `uv`/`uvx` via the official installer
  script (relocated to a PATH-visible location since its default install dir is user-local and this
  runs as root in the container). Single additive `RUN` block or two, matching the existing
  multi-stage structure. Verify final image size delta and note it in the PR description — not a
  blocker, per §6's original framing.
- Solution file — add `Litos.Tools.Mcp` (and, if tests are written, a `Litos.Tools.Mcp.Tests`
  project mirroring the existing `*.Tests` pattern).

**Explicitly not modified**: `src/Litos.Agent/**` (zero changes anywhere in this project — the
whole point of the `ITool`-shaped proxy, per §4.1); `src/Litos.Host/LitosHostBuilder.cs` (zero
changes — decision 2 keeps all MCP DI wiring inside `Litos.Api/Program.cs`, not the shared
composition root).

### 9.4 `Program.cs` sequencing (exact order)

1. `LitosConfig.Load()`, `AddLitosAgent(config)` — unchanged, as today.
2. Construct `LogStore` **earlier than today** (move it up if needed) so MCP startup-handshake logs
   land on `/logs` even though the handshake runs before `app.Build()`/DI is available.
3. `PendingApprovalStore` registered **unconditionally** (today it's only inside the
   `telegramToken is not null` branch) — MCP `Ask`-mode gating needs it even with no Telegram
   token configured.
4. Construct `McpConfigStore` directly (`new McpConfigStore()`, decision 10 — not via DI) and
   register the instance into `builder.Services`.
5. Existing `if (telegramToken is not null) { ... } else { ... }` gate block runs as today, **except**
   each branch's final `IToolApprovalGate` registration wraps its gate in `McpAwareApprovalGate`
   (decision 2/9.1) — e.g. `new McpAwareApprovalGate(new TelegramGatingApprovalGate(...),
   mcpConfigStore, pendingApprovalStore)` and `new McpAwareApprovalGate(new AutoApprovalGate(),
   mcpConfigStore, pendingApprovalStore)` respectively.
6. Construct `McpToolProvider` directly (`new McpToolProvider(mcpConfigStore, ...)`, decision 10)
   using the already-constructed `IToolApprovalGate` chain from step 5 (threaded explicitly, not
   resolved via DI, since no container exists yet). `await
   mcpToolProvider.InitializeAsync(TimeSpan.FromSeconds(10), ct)`.
7. Register the already-initialized `mcpToolProvider` instance into `builder.Services`
   (`AddSingleton(mcpToolProvider)`), and register each of its discovered `McpToolProxy` instances
   as `ITool` (`AddSingleton<ITool>(proxy)` per tool, already-constructed instances — not
   factories).
8. `var app = builder.Build();` — unchanged position, now with MCP tools already present in the
   `ITool` collection `ToolRegistry` will snapshot on first resolution.
9. After `app.Build()`: register `app.Lifetime.ApplicationStopping` (or
   `IHostApplicationLifetime` resolved from `app.Services`) to call
   `mcpToolProvider.ShutdownAsync()`, bounded by its own short timeout so a hung disposal can't
   block container shutdown.
10. Rest of `Program.cs` (middleware, endpoint mapping, `app.Run()`) unchanged.

### 9.5 Build order for implementation

1. `Litos.Tools.Mcp` project skeleton + `ToolPermission`/`PendingApprovalStore` relocation (§9.3) —
   pure refactor, verify existing Telegram gating/tests still pass unchanged before adding any new
   functionality.
2. `McpConfig`/`McpConfigStore` (with decision 8 validation) + `McpServerConnection` +
   `McpToolProxy` + `McpToolProvider` (both transports) — testable in isolation against a real
   published stdio server (e.g. `npx @modelcontextprotocol/server-everything` or
   `@modelcontextprotocol/server-filesystem`) before touching `Program.cs`.
3. `Program.cs` wiring (§9.4) + `McpAwareApprovalGate` — this is the milestone where MCP tools
   first reach a real running `Litos.Api` and go through real gating. Land gating together with
   config-driven multi-server wiring in the same PR/release if at all possible — shipping
   real-server connections without the gate wrapping in place would mean configured MCP tools
   silently inherit whichever *base* gate is active (i.e. auto-approved under `AutoApprovalGate`),
   a real safety gap worth avoiding even as an intermediate commit state.
4. `PendingApprovalsPanel.razor` + `McpServers.razor` + nav link — makes the feature self-service
   and makes `Ask` mode actually usable without hand-editing `mcp.json`.
5. `Dockerfile` — Node/npm/`uv`, verified against at least one real stdio server and, if available,
   one real remote HTTP MCP server; record the image-size delta.

## 10. Implementation status (2026-07-31)

All 5 milestones from §8/§9.5 shipped in one pass, then extended same-day with live/dynamic tool
discovery (originally deferred in §7 as "Hot reconnect / live config-change without restart... a
reasonable v2 improvement") after live Docker testing surfaced the restart-required gap as worth
closing immediately rather than deferring. This section records what actually exists on disk today;
§§1-9 remain as the design-reasoning record.

### 10.1 What shipped as designed (§9 plan, built as specified)

- **`Litos.Tools.Mcp` project** (`src/Litos.Tools.Mcp/`): `McpConfig.cs`, `McpConfigStore.cs`,
  `McpServerConnection.cs`, `McpToolProxy.cs`, `McpAwareApprovalGate.cs` — all built per §9.3's file
  plan. References `ModelContextProtocol` **2.0.0** (confirmed still current stable at
  implementation time, per decision 5).
- **`ToolPermission`/`PendingApprovalStore` relocated** to `Litos.Tools.Shell` exactly per decisions
  7/9 — pure namespace moves, verified via full existing-test-suite pass before new code was added.
- **Name-collision validation** (decision 8) — `McpConfigStore.Update` rejects `"__"` in a server
  name and duplicate names, enforced in the store itself, not just the UI.
- **`PendingApprovalsPanel.razor`** + **`McpServers.razor`** (`@page "/mcp"`) + nav link in
  `MainLayout.razor` — built per §9.3/decision 12, `PendingApprovalsPanel` embedded in
  `McpServers.razor` and reused for Telegram's own `Ask`-mode approvals for free.
- **Dockerfile**: Node.js 22 + npm (via NodeSource) and `uv`/`uvx` (via the official installer,
  relocated to `/usr/local/bin`) added to the runtime stage. **Verified with a real `docker build`**
  (Docker wasn't available in the original implementation session; confirmed working in a follow-up
  session) — image builds clean, both `npx` and `uv`/`uvx` work inside the container. Image size
  delta was not precisely isolated (no side-by-side pre/MCP baseline build was done), but the build
  itself succeeds with no errors.
- **Live end-to-end verification against a real MCP server**: `@modelcontextprotocol/server-everything`
  (stdio, via `npx`) connected successfully inside the built container, discovered its 13 tools, and
  the `/mcp` admin page correctly showed live `Connected`/`Unreachable` status.

### 10.2 Corrections found during live testing (beyond §9.1's pre-implementation corrections)

- **§4.1/§9.2 decision 3's ~10s handshake timeout was too short in practice.** A live test against
  `@modelcontextprotocol/server-everything` measured **~18s** end-to-end (process spawn + MCP
  `initialize` handshake + `tools/list`) even with a warm npm cache, since `npx` still performs a
  registry version-resolution check on every invocation. 10s made every `npx`-based server — the
  majority of the public MCP ecosystem per §6 — fail as `Unreachable` on a first real run.
  **Resolution**: raised to a **30s** default (`mcpHandshakeTimeout` in `Program.cs`), sourced from
  direct measurement, not from Claude Code's or Cursor's own defaults (research found Claude Code
  uses a documented, user-configurable `MCP_TIMEOUT`, default 10s, but doesn't block startup on it
  at all — see §10.3; Cursor's timeout behavior isn't publicly documented). Not yet exposed as an
  env var/config option — a hardcoded constant for v1, flagged as a reasonable follow-up.

### 10.3 Beyond original v1 scope: live/dynamic tool discovery

§7 explicitly deferred this ("Hot reconnect... a reasonable v2 improvement once the basic lifecycle
is proven"). It was pulled forward into the same implementation pass after the user asked what
happens when a server is added while the API is running — the honest answer under the §9 design was
"saved to config, but genuinely inert until restart," which didn't match user expectations once
surfaced. A full clarifying-questions → three-way architecture comparison (minimal-change /
clean-architecture / pragmatic-balance) pass was run for this specific sub-feature; **clean
architecture** was chosen and built. Confirmed requirements (user-approved):

1. Add/enable/disable/edit/remove of an MCP server via `/mcp` takes effect **without a container
   restart**.
2. **Next-turn-only visibility** — a turn already in progress keeps the tool list it started with;
   only a turn started after a change observes it.
3. `Unreachable` servers are **retried periodically in the background with exponential backoff**
   (15s → 5min cap), not left terminal for the process's life as §9's design left them.
4. Built-in tools (`read_file`, `shell`, etc.) are **not** dynamic — only the MCP-sourced portion of
   the tool list varies at runtime.
5. `Litos.Gui`/`Litos.Console` remain out of scope for *using* this (confirmed, matching §7's
   original `Litos.Api`-only decision) — but the new abstraction is face-agnostic so they could
   adopt it later without rework.

**This materially changes §4.1's "`ToolRegistry` stays exactly as it is... no changes to
`Litos.Agent`" claim and §9.2 decision 3's "block `app.Build()` on the handshake, snapshot once."**
What actually shipped:

- **`Litos.Agent` gained two new, MCP-ignorant files**: `Tools/IToolSource.cs` (a generic
  `IReadOnlyList<ITool> CurrentTools` seam — no MCP concept anywhere in `Litos.Agent`, preserving
  its zero-dependency status in spirit, if not in the letter of "zero changes") and
  `Tools/ToolRegistryFactory.cs` (builds a fresh `ToolRegistry` — itself still **byte-for-byte
  unchanged** — from static tools + every registered `IToolSource.CurrentTools`, on demand).
- **`ToolRegistry` is no longer a DI singleton.** `LitosHostBuilder.AddLitosAgent` now registers
  `ToolRegistryFactory` instead. `AgentLoopFactory.Create` and `ISystemPromptProvider.BuildAsync`
  both take `ToolRegistry` as a **per-call parameter** rather than a constructor-captured field —
  this is the actual mechanism behind requirement 2: `AgentWorker.RunTurnAsync` calls
  `ToolRegistryFactory.Create()` fresh at the start of every turn (`src/Litos.Api/AgentWorker.cs`),
  right alongside its existing provider/model snapshot-at-turn-start pattern.
- **This rippled into `Litos.Gui` and `Litos.Console`**, both of which call `AgentLoopFactory.Create`
  directly (outside `AgentWorker`). Both now build one `ToolRegistry` via
  `ToolRegistryFactory.Create()` once at startup and reuse it across `/provider` switches — matching
  their pre-existing static, non-dynamic behavior exactly (dynamic MCP tool discovery itself was
  *not* extended to these faces, per requirement 5), but both needed call-site updates to compile
  against the new signature. `Litos.Console.Tests` was also found missing from `LitosAiAgent.slnx`
  (a pre-existing, unrelated gap) and added while making this correction.
- **`McpToolProvider` gained a `RefreshAsync` method** (generalizing the old one-shot
  `InitializeAsync`, which now delegates to it) that diffs live connections against
  `McpConfigStore.Current` — connects new/re-enabled/edited servers, disposes removed/disabled ones,
  retries `Unreachable` connections whose backoff window has elapsed. `DefaultPermission`/
  `ToolOverrides` changes deliberately do **not** trigger a reconnect, since `McpAwareApprovalGate`
  already reads `McpConfigStore.Current` fresh per call.
- **`McpServerConnection` gained backoff state** (`NextRetryAt`, capped exponential backoff,
  15s → 5min) for requirement 3.
- **New `McpToolSource : IToolSource`** (thin adapter, `Litos.Tools.Mcp`) and **new
  `McpToolRefreshService : BackgroundService`** (5s poll loop calling `RefreshAsync`, sibling to
  `AgentWorker`'s existing `BackgroundService` pattern) — both new files in `Litos.Tools.Mcp`,
  registered in `Program.cs` alongside the existing pre-`Build()` MCP wiring. The pre-`Build()`
  `InitializeAsync` call (§9.2 decision 3) is **kept** — it's what makes the *first* turn have real
  tools immediately rather than waiting out the first background poll — but it's no longer the
  *only* mechanism; `McpToolRefreshService` takes over from there.
- **`McpServers.razor`'s "restart required" messaging was removed** — replaced with
  "Connecting…"/"Disabled." status text reflecting the new live-reconcile behavior, since every
  admin-page mutation (add/enable/disable/edit/remove) now genuinely applies without a restart.
- **A real bug was found and fixed during this work**: `McpServerDefinition.PermissionFor` used
  `ToolOverrides?.GetValueOrDefault(name) ?? DefaultPermission` — `GetValueOrDefault` on an
  enum-valued dictionary returns `default(ToolPermission)` (`Deny`, enum value 0) rather than `null`
  on a miss, so the `??` never fell through to `DefaultPermission`. Every MCP tool without an
  explicit per-tool override would have silently resolved to `Deny` regardless of the server's
  configured default. Caught by a new unit test, fixed with an explicit `TryGetValue`.

**Research note on prior art**: Claude Code's own MCP timeout/discovery model was checked against
official docs (`code.claude.com/docs/en/mcp.md`) before this redesign — it doesn't block startup on
MCP connections at all; discovery is lazy and per-request, gated by a user-configurable
`MCP_TIMEOUT` (default 10s) only at the point something actually needs a not-yet-connected server's
tools. Litos's chosen design (bounded block-on-startup for the *first* turn, background
poll-and-reconcile thereafter) is a deliberate middle ground — full lazy-per-request discovery would
require `ToolRegistry`/`Resolve` to become async, which was rejected as unjustified complexity for a
single current consumer (`Litos.Api`) per the architecture-comparison pass.

### 10.4 Test coverage added

- `Litos.Tools.Mcp.Tests`: `McpConfigStoreTests`, `McpAwareApprovalGateTests` (from the original
  §9 pass), plus new `McpToolProviderTests` (23 tests total across the project) covering
  `RefreshAsync`'s add/remove/disable/edit/backoff-retry/permission-only-no-reconnect diff logic
  against a deliberately-nonexistent command (deterministic fast failure, no real server needed),
  and `McpToolSourceTests`.
- `Litos.Agent.Tests`: new `ToolRegistryFactoryTests` proving the next-turn-only invariant directly
  (a `ToolRegistry` snapshot from an earlier `Create()` call is provably unaffected by a later
  `IToolSource` mutation; a later `Create()` call provably picks the mutation up).
- `Litos.Api.Tests`: new `AgentWorkerTests.RunTurnAsync_ToolSourceGainsATool_NextTurnSeesIt` —
  genuine end-to-end proof through the real `AgentWorker` → `AgentLoopFactory` → `AgentLoop` path,
  asserting the actual `ChatRequest.Tools` a fake provider received differs between two turns after
  a tool source changed in between.
- `Litos.Gui.Tests`/`Litos.Console.Tests`: new `AgentLoopFactoryToolRegistryTests` in each,
  confirming the new `Create(provider, tools)` signature compiles and correctly threads a
  `ToolRegistry` through to a real request in both faces' own usage pattern.
- Two pre-existing, unrelated flaky tests were identified and confirmed independent of all of this
  work (both fail even in isolation, on files this implementation never touched):
  `Litos.Agent.Tests.AgentLoopTests.RunTurnAsync_ProviderThrows_PropagatesUncaught_UnlikeToolExceptions`
  and `Litos.Api.Tests.PendingApprovalStoreTests.Add_NobodyResolves_RaisesTimedOutAndResolvedEvents`
  (a 20ms-timing race). Full solution test suite is otherwise green.

### 10.5 Still out of scope (updated from §7)

- **MCP resources and prompts** — unchanged from §7, still not designed.
- **Per-session/per-user MCP server scope** — unchanged from §7, still global/deployment-wide only.
- **`Litos.Gui`/`Litos.Console` actually *using* dynamic MCP discovery** — the plumbing
  (`IToolSource`, `ToolRegistryFactory`) is now face-agnostic and lives in `Litos.Agent`, so this is
  additive future work, not a rework, but neither face registers an `IToolSource` today.
- **MCP server marketplace/discovery** — unchanged from §7.
- **True per-connection health-check beyond backoff-retry-on-`Unreachable`** — a `Connected` server
  that silently stops responding mid-session (as opposed to a `tools/call` failing loudly) has no
  active health check; it would only be caught the next time a tool call against it fails.
- **Handshake timeout is still a hardcoded constant** (30s, §10.2), not user-configurable — flagged
  as a reasonable follow-up, not built.
