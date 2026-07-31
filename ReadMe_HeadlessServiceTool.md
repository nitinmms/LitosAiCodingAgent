# LitosAiAgent — Headless Service Feasibility

Evaluates running LitosAiAgent as a headless background service — no desktop window, deployable
in a Docker container on a home server/NAS/always-on box — with a small self-hosted web UI for
setup and approval tasks: registering a Telegram bot (and, later, other chat platforms — see
`ReadMe_TelegramIntegrationTool.md` §5), showing pairing QR codes, and approving/denying tool-call
and device-elevation requests. Written before any implementation; no code has changed as a result
of this document. Builds on `ReadMe_AgentDesign.md` (§9 composition root, §10 "Calling
LitosAiAgent from other apps") and `ReadMe_TelegramIntegrationTool.md` (§0 confirmed decision, §3
confirmed decisions, §7 elevation gate) — this document exists because those two together surfaced
a real limitation: an earlier draft of the Telegram design required `Litos.Gui`'s desktop process
to be running for the bridge to work at all, which rules out "always-on remote access from a
machine I don't want running a desktop session."

**Confirmed decision, settled after this document's initial version**: `Litos.Api` is not merely
an *alternative* to `Litos.Gui` for people who happen to prefer a server — it is the **sole** home
for any chat-platform integration (Telegram today; WhatsApp, Slack, or others later). `Litos.Gui`
carries no messaging-bridge code of any kind, and never will under this design. See
`ReadMe_TelegramIntegrationTool.md` §0 for the full reasoning; §3's decision table below reflects
this.

**This document is deliberately separate from `ReadMe_TelegramIntegrationTool.md`**, not a section
within it — a headless deployment mode is a bigger architectural question than Telegram alone (it
changes the composition root's caller model, not just adds one more face) and is scoped and read
independently, even though the two are designed to fit together (§6).

## 1. What this is, in one sentence

A new `Litos.Api` project — the exact one `ReadMe_AgentDesign.md` §10.3 already names and
sketches but has never built — packaged as a Docker container: a single ASP.NET Core process that
hosts `AgentLoop` as a background worker and serves a small set of server-rendered admin pages
(status, Telegram setup, pending approvals) over HTTP, with no separate frontend framework and no
desktop window anywhere in the stack.

## 2. Where Litos stands today (confirmed by inspection)

- **`Litos.Host` is already, genuinely UI-agnostic — this is not an assumption, it's verified.**
  `Litos.Host.csproj` references only `Microsoft.Extensions.DependencyInjection` plus the
  brain/environment projects — zero Avalonia, zero Terminal.Gui, zero web-framework packages.
  `LitosHostBuilder.AddLitosAgent`, `LitosConfig`, `ChatProviderFactory`, `AgentLoopFactory` are
  pure DI composition. This is independently proven twice over already: `Litos.Console` (Terminal.Gui)
  and `Litos.Gui` (Avalonia) both consume the identical `Litos.Host` surface with zero changes to
  it — per §9/§7.7 of the design doc, adding a face has so far always been additive, never a
  rewrite. A headless service is a **third proof of the same claim**, not a special case.
- **A network-caller face is already architecturally planned, just never built.**
  `ReadMe_AgentDesign.md` §10.3 names the project `Litos.Api`, sketches `POST
  /sessions/{id}/turns` streaming `AgentEvent`s back as Server-Sent Events, and specifically calls
  out `HttpApprovalGate` — a web-based `IToolApprovalGate` implementation that "suspends the tool
  call and waits for `POST /sessions/{id}/approvals/{callId}`." **This document is that milestone,
  scoped down to a single-workspace v1** (§3), not a new idea invented here.
- **No background-service or web-server infrastructure exists anywhere in the repo, confirmed by
  repo-wide search.** Zero matches for `Kestrel`, `WebApplication`, `Microsoft.AspNetCore`,
  `SignalR`, `Blazor`, `IHostedService`, `BackgroundService` in any `.cs`/`.csproj` file. No
  `packages.lock.json`, so nothing partial is hiding there either. This independently reconfirms
  `ReadMe_TelegramIntegrationTool.md` §2's identical finding — same underlying facts, same
  conclusion reached twice from two different features that both needed it.
- **No Dockerfile, docker-compose.yml, or `.dockerignore` anywhere in the repo.** `Litos.Gui.csproj`
  has self-contained single-file publish profiles for `win-x64`/`osx-x64`/`osx-arm64`, but nothing
  targets `linux-x64` or containerization — this is new packaging work, not an extension of
  existing publish tooling.
- **The `IToolApprovalGate` seam is exactly the right shape for a web-based approval bridge, and
  this codebase already has a proven reference implementation of the pattern needed.**
  `IToolApprovalGate.RequestAsync(ToolInvocationPreview, CancellationToken) : Task<ApprovalDecision>`
  (`src/Litos.Tools/Shell/IToolApprovalGate.cs`) is a plain async method with no built-in timeout.
  `ApprovalDialog` (`src/Litos.Console/Terminal/ApprovalDialog.cs`) already implements exactly the
  "suspend the call, resolve it later from an external event" pattern via a
  `TaskCompletionSource<ApprovalDecision>` — there, the external event is a Terminal.Gui button
  click marshaled onto the UI thread via `app.Invoke`; for a web version it's an HTTP `POST`
  handler calling `tcs.SetResult(...)` instead. **The concurrency pattern is proven in this exact
  codebase already** — a web `HttpApprovalGate` is a new trigger for the same bridge, not a new
  concurrency design.
- **`AgentLoop` has no timeout around tool execution, so a slow-to-approve web request is safe by
  design, not by accident.** The only timeout in `AgentLoop` (`_streamIdleTimeout`, default 60s) is
  scoped to the model-streaming phase (`MoveNextWithIdleTimeoutAsync`); the `await
  tool.InvokeAsync(...)` call itself is unbounded, exactly like a slow shell command is expected to
  run long today. An approval that takes five minutes to click "Approve" on a phone browser tab
  simply holds that one turn paused — it doesn't trip any existing timeout or degrade into an
  error path.
- **`GuiApprovalGate` is confirmed spike-only (auto-approves everything) and there is no existing
  "real" approval implementation to port from other than `ApprovalDialog`, which is terminal-bound.**
  Its own doc comment already says a real implementation "would show an Avalonia dialog... and
  await the user's click via a `TaskCompletionSource`, same pattern noted in
  `ReadMe_AgentDesign.md` §7.5" — i.e. it already points at the exact pattern this document reuses
  for the web version, just never built it for Avalonia either. A headless `HttpApprovalGate`
  doesn't need to wait on a GUI implementation that doesn't exist.
- **Session isolation across multiple untrusted callers is a known, named, *not-yet-necessary* gap.**
  `ReadMe_AgentDesign.md` §10.4 lays out three things a real multi-caller `Litos.Api` needs
  (owner-scoped transcripts — already true today via `SessionOwner`; an owner-scoped
  `WorkspaceRoot` for file/shell tools instead of the process's ambient CWD — not true today;
  per-request `Transcript`/`AgentLoop` resolution instead of a cached singleton — not true today)
  and is explicit that none of this is needed "for a single well-behaved in-process caller." Per
  §3's confirmed decision, this document's v1 is a **single workspace, single implicit caller** —
  §10.4's full isolation work becomes a hard prerequisite the moment a second workspace/session
  needs to exist side-by-side in one container, which is out of scope here (§9).

## 3. Confirmed design decisions

These were settled during scoping and are treated as fixed for the rest of this document:

| Decision | Choice | Rationale |
|---|---|---|
| Relationship to `Litos.Gui` | **Separate concerns, not a replacement — but not symmetric either.** `Litos.Gui` stays the primary desktop experience for local use, with zero chat-platform code. `Litos.Api` is the sole path to any remote/chat-platform access — not merely "opt-in for people who prefer a server," but the *only* way to get Telegram (or future WhatsApp/Slack) integration at all | Matches the design doc's "same brain, different faces" philosophy (§2) for the desktop-vs-headless split, while `ReadMe_TelegramIntegrationTool.md` §0 settles messaging-hosting specifically: splitting that across two faces created exactly the ambiguity ("which one actually runs Telegram?") a design should prevent, not preserve as a choice |
| Approval-UI scope | **General-purpose — the web UI implements `IToolApprovalGate` itself and is the one place all approvals happen when running headless**: ordinary tool calls (shell, file writes) *and* chat-platform device elevation (per `ReadMe_TelegramIntegrationTool.md` §7's elevation-gate design) both route through it | Matches §10.3's own `HttpApprovalGate` sketch directly; avoids building two separate approval mechanisms (one for "is this tool call OK" and a different one for "is this remote device OK") when one already covers both — device elevation is just one more `ToolInvocationPreview`-shaped decision |
| Workspace/session scope for v1 | **Single workspace, *multi-owner* isolation deferred.** v1 assumes one working directory for the whole service — like `Litos.Console` today — not a multi-session/multi-workspace sandboxed model | §10.4's full `WorkspaceRoot`-per-caller isolation work is real but is only a hard prerequisite once a *second* mutually-untrusted caller/workspace needs to share one running instance (§10.4's own conclusion) — v1 here has exactly one implicit caller (whoever can reach the admin UI), so it doesn't trigger that requirement yet. Named explicitly as the reason a v2 would need it (§7). **Distinct from filesystem confinement (§5.6), which is NOT deferred** — v1 does deliberately confine that single workspace to a mounted directory, keeping tools off the rest of the host |
| Docker/deployment detail | **Feasibility-level** — the shape (multi-stage build, port convention, volume mount, the exposure footgun) with an illustrative Dockerfile, not a complete copy-pasteable compose setup | Matches how `ReadMe_TelegramIntegrationTool.md` treats `Telegram.Bot`/`QRCoder` — named and reasoned about as a design decision, not fully spec'd as if this were the implementation itself |
| Container filesystem boundary | **Deliberate confinement, not incidental.** Exactly two mounted paths (workspace + state dir), nothing else of the host visible to the container — this is how "the container is the sandbox" actually holds, not a hope (§5.6) | Docker provides zero isolation benefit by default — a container mounted with `-v /:/data` has full host access regardless of "running in Docker." The confinement is a mount-policy decision this document makes explicitly, prompted by a direct question during scoping about whether Docker was being proposed as a sandbox |

## 4. Why this is `Litos.Api`, not a new architectural concept

`ReadMe_AgentDesign.md` §10.3 already specifies the shape this document fills in:

- `POST /sessions/{id}/turns` accepts `{ input, attachments }`, calls the same
  `AgentLoop.RunTurnAsync` every other face calls, streams `AgentEvent`s back as **Server-Sent
  Events** — chosen there specifically for working through most proxies, relevant here since a
  Docker deployment is likely to sit behind a reverse proxy on a home server.
  `HttpApprovalGate`, per §10.3, suspends a tool call and waits for `POST
  /sessions/{id}/approvals/{callId}` — this document's approval-list web page (§5.3) is the human
  interface to exactly that endpoint, not a different mechanism.
- §10.3 already states plainly: *"a browser-based `Litos.Web` face is a caller of this kind, just
  with a first-party frontend bundled in. A bare API without a bundled frontend and a full web app
  are the same project with or without static files."* This document's admin UI **is** that bundled
  frontend — a small one, server-rendered, living in the same `Litos.Api` project rather than a
  separate `Litos.Web`, since the UI surface here (status, Telegram setup, approvals) is small
  enough not to warrant a second project.
- Auth (§10.3: *"whose job is precisely to turn 'who is calling' into a `SessionOwner`... never
  taken from a client-supplied field"*) sits entirely in `Litos.Api`; `Litos.Host` and below stay
  auth-agnostic. For v1's single-workspace scope, this collapses to a single shared admin token
  (§5.5) rather than a real multi-tenant `SessionOwner` resolution — the architectural seam is the
  same one §10.3 already describes, just exercised with one caller instead of many.

## 5. Architecture

### 5.1 Project shape: `Litos.Api`

```
src/Litos.Api/
├── Program.cs                  # WebApplicationBuilder + AddLitosAgent(...) + AddHostedService<AgentWorker>
├── AgentWorker.cs               # BackgroundService — owns the one AgentLoop turn-driving loop (§5.2)
├── HttpApprovalGate.cs          # IToolApprovalGate — TaskCompletionSource bridge, mirrors ApprovalDialog
├── Approvals/
│   ├── PendingApprovalStore.cs  # in-memory Dictionary<Guid, TaskCompletionSource<ApprovalDecision>>
│   └── ApprovalsEndpoints.cs    # POST /approvals/{id}/{decision} (Minimal API — the decision path only;
│                                #   the pending list itself reaches the browser via SignalR, not a GET poll)
├── Components/Pages/
│   ├── Status.razor             # is the service running, current provider/model, uptime
│   ├── TelegramSetup.razor      # bot token entry, QR display — the web-UI equivalent of
│   │                            #   ReadMe_TelegramIntegrationTool.md §5.4's desktop dialog
│   └── Approvals.razor          # pending approval list, approve/deny buttons, live via SignalR circuit
├── Auth/
│   └── AdminTokenFilter.cs      # IEndpointFilter — shared-token check (§5.5)
└── Dockerfile
```

Reference direction stays inward-only, matching every other face:
`Litos.Api → Litos.Host → Litos.Tools/Providers/Persistence → Litos.Agent`. `Litos.Api` is a
sibling of `Litos.Console`/`Litos.Gui`, not a dependency of or dependency on either — exactly the
relationship §3/§9 of the design doc already establishes for any new face.

**Web framework choice: Blazor Server.** Both Blazor Server and ASP.NET Core Minimal
APIs + Razor Pages (htmx-enhanced) are viable for this UI's actual size (status, a token-entry
form, an approval list with approve/deny buttons) — this isn't a case where one option is wrong,
and the two were weighed directly rather than defaulting to either:

- Razor Pages + htmx is the leaner choice — server-rendered HTML, no build step, no
  persistent-connection infrastructure. It would need htmx polling `GET /approvals` every couple
  seconds to keep the approval list current, since Razor Pages has no live-push mechanism of its
  own.
- **Blazor Server, chosen as the primary recommendation**, trades that simplicity for a live
  per-tab SignalR circuit that Approvals.razor — the one screen that actually benefits from
  real-time updates — gets to use directly, rather than emulating push via polling. It's also the
  better fit if this admin surface grows beyond its current four screens, or if the team prefers
  writing C# components end-to-end over Razor Pages plus hand-placed htmx attributes.

The trade-off this accepts: a SignalR circuit per connected browser tab, and server-side UI-state
lifetime to reason about (reconnection handling, circuit timeout) — real infrastructure a Razor
Pages + htmx version wouldn't need, justified here by the fact that the one interactive screen
that matters most (Approvals) is exactly what Blazor Server's live-update model is built for.
`HttpApprovalGate`/`PendingApprovalStore` (§5.3) stay identical either way — the approval *bridge*
doesn't change, only how the pending-approvals list reaches the browser (SignalR push here,
instead of htmx poll).

### 5.2 Process lifecycle: one Kestrel host, one background worker, same process

Following the standard .NET generic-host pattern (not a new invention for this codebase — the
recommended shape for exactly this "web server + long-running worker in one process" scenario):

```csharp
// Litos.Api/Program.cs — shape, not final code
var builder = WebApplication.CreateBuilder(args);
var config = LitosConfig.Load();
builder.Services.AddLitosAgent(config);               // identical call every other face makes
builder.Services.AddSingleton<IToolApprovalGate, HttpApprovalGate>();
builder.Services.AddSingleton<PendingApprovalStore>();
builder.Services.AddHostedService<AgentWorker>();       // the turn-driving loop (below)
builder.Services.AddRazorPages();

var app = builder.Build();
app.MapApprovalsEndpoints();                             // POST /approvals/{id}/{decision}, etc.
app.MapRazorPages().RequireAuthorization( /* admin token filter, §5.5 */ );
app.Run();
```

`AgentWorker : BackgroundService` is the closest analog to `Litos.Console`'s
`RunNonInteractiveAsync` loop (`src/Litos.Console/Program.cs:142`) — reads one input at a time,
drives `AgentLoop.RunTurnAsync`, handles the result — except its input source is HTTP requests to
`POST /sessions/{id}/turns` (§4) rather than stdin lines, and (per §3's Telegram-bridge tie-in,
§6) can also be fed by the same `TelegramSessionDriver` pattern
`ReadMe_TelegramIntegrationTool.md` §5.3 describes, running inside this same process instead of
inside `Litos.Gui`.

Kestrel's own request-handling loop and `BackgroundService.ExecuteAsync` share the process's
lifetime — `SIGTERM` (sent by `docker stop`) triggers `IHostApplicationLifetime`'s graceful
shutdown for both, which the .NET generic host already handles without custom code.

### 5.3 The approval bridge: `HttpApprovalGate`

Directly mirrors `ApprovalDialog`'s proven shape (§2), swapping the UI-thread callback for an HTTP
handler:

```csharp
// Litos.Api/HttpApprovalGate.cs — shape
public sealed class HttpApprovalGate(PendingApprovalStore store) : IToolApprovalGate
{
    public Task<ApprovalDecision> RequestAsync(ToolInvocationPreview preview, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var tcs = new TaskCompletionSource<ApprovalDecision>();
        store.Add(id, preview, tcs);          // pushed to the Approvals.razor circuit via SignalR
        return tcs.Task;                       // AgentWorker's turn stays suspended here
    }
}

// Approvals/ApprovalsEndpoints.cs — shape
app.MapPost("/approvals/{id}/{decision}", (Guid id, ApprovalDecision decision, PendingApprovalStore store) =>
{
    store.Resolve(id, decision);               // tcs.SetResult(decision) — unblocks AgentWorker
    return Results.Ok();
});
```

`PendingApprovalStore` is the in-memory `Dictionary<Guid, (ToolInvocationPreview, TaskCompletionSource<ApprovalDecision>)>`
that makes this work — the shared state lives in one singleton service, not in either individual
HTTP request, which is exactly how a stateless request/response protocol bridges to a long-lived
suspended `await`. Per §5.1's Blazor Server choice, `PendingApprovalStore.Add` raises a C# event
(or calls an injected `IHubContext`-equivalent) that `Approvals.razor`'s live SignalR circuit
subscribes to, so a newly pending approval appears the instant it's created — no polling interval,
no staleness window. The `POST /approvals/{id}/{decision}` endpoint stays a plain Minimal API route
regardless (§5.1) — Blazor Server's own button click could call `store.Resolve` directly in-process
just as easily, but keeping it as a real HTTP endpoint means the same decision path also works from
a plain `curl`/script call, not only from the rendered page.

**Known limitation, stated plainly rather than glossed over**: a plain in-memory dictionary means
any approval still pending at process restart is lost — the suspended tool call's `Task` never
resolves, and (per AgentLoop's design, §2) that one turn simply stays blocked forever rather than
erroring. Acceptable for v1 (a restart is a rare, operator-initiated event on a personal service),
but worth fixing before treating this as production-grade: persist pending-approval requests
alongside the turn's transcript entry so they can be re-surfaced on the next startup, rather than
silently orphaned.

### 5.4 Telegram setup as a web page

The QR-pairing flow — `TelegramSetup.razor` rendering the pairing QR as an inline `<img>`
(`QRCoder`-generated PNG as a data URI), validating the `/start <code>` deep link, and recording
the linked chat — is described in full in `ReadMe_TelegramIntegrationTool.md` §6.4, which is
written directly against this `Litos.Api`/Blazor Server hosting model (not adapted from a desktop
version, since §0 there settles `Litos.Api` as the only host from the start). **Device
elevation** — the gate closing "identity and approval are the same action"
(`ReadMe_TelegramIntegrationTool.md` §7) — becomes just another entry on `Approvals.razor`: per
§3's confirmed decision, the same approval list handles both a `ShellTool` invocation and a
"elevate this Telegram chat" request, since both are just a human clicking Approve/Deny on a
`ToolInvocationPreview`-shaped card. No second UI is built for this.

### 5.5 Auth: single shared admin token, not a full identity provider

For v1's single-workspace, single-implicit-caller scope (§3), a full OIDC/identity-provider setup
would be disproportionate — ASP.NET Core's own minimal-API security guidance documents
JWT-bearer/OIDC as the built-in strategies and doesn't cover a bare shared-secret scheme at all,
confirming this is a deliberate simplification appropriate specifically because the admin UI is
reached over a private network or localhost, not the public internet — the same reasoning
`ReadMe_TelegramIntegrationTool.md` §5.5/§6 already applied to the Telegram bot token itself.

- An `ADMIN_TOKEN` env var (following `LitosConfig`'s existing env-var-first pattern, §6.1) is
  compared against a `Bearer`/cookie-carried token on every request via an `IEndpointFilter`
  (`AdminTokenFilter`), using `CryptographicOperations.FixedTimeEquals` — constant-time comparison,
  avoids leaking the token's correctness via response-time timing.
- **Exposure footgun, stated explicitly because it's easy to get wrong**: the container's Kestrel
  listener binds to `0.0.0.0` inside the container by design (required for `docker run -p` to work
  at all) — this is *not* the same as "only reachable from localhost." Publishing with bare `docker
  run -p 8080:8080` makes the admin UI reachable from anything on the host's LAN, not just the host
  itself, depending on the host's network/firewall configuration. Deployment guidance (§6.3) should
  recommend `-p 127.0.0.1:8080:8080` (loopback-only on the host) as the default, with LAN/remote
  exposure an explicit, documented opt-in via a reverse proxy — not the default `-p` invocation a
  first-time user would naturally reach for.

### 5.6 Filesystem confinement: the container boundary is the sandbox, deliberately

**This is a real, designed v1 security property, not an incidental side effect of "it happens to
run in a container."** It's also a different thing from §10.4's *multi-workspace* isolation
(§3, §9) — that's about keeping several mutually-untrusted callers' workspaces apart from each
other *inside* one running instance, which v1 doesn't have (there's only one workspace). This
section is about keeping the single workspace apart from the rest of the *host machine* — a
narrower, simpler guarantee, and one v1 should provide from day one rather than defer.

Without a deliberate mount policy, running `Litos.Console`-equivalent tools (`ShellTool`,
`WriteFileTool`, `EditFileTool`) inside Docker provides **no security benefit by default** — a
container's filesystem view is exactly whatever's mounted into it, so `docker run -v
/:/data ...` would hand the agent the entire host filesystem, container or not. The confinement
only exists if the deployment guidance makes it exist:

- **One workspace volume, one mount point, nothing else.** The Dockerfile/run instructions (§6.3)
  specify exactly two mounted paths: the workspace directory the agent's file/shell tools operate
  against (e.g. `-v ~/projects/my-repo:/workspace`), and the `~/.litos`-equivalent state directory
  for config/sessions/pending approvals (`-v ~/.litos-docker:/data`, distinct from the workspace
  itself so agent output can't accidentally overwrite its own session history). No other host path
  is mounted, ever, in the recommended configuration — this is the actual mechanism that makes
  `ShellTool` unable to `cd ..` its way onto the host, not a hope that Docker "just handles it."
- **`LITOS_WORKSPACE=/workspace` (or equivalent) is read at startup and threaded through the same
  way `Litos.Console`'s ambient `Environment.CurrentDirectory` is today** — the container's `WORKDIR`
  and the agent's own working-directory concept are kept in sync deliberately, not left to
  default to the container filesystem root.
- **What this does and doesn't protect against, stated plainly**: this confines a *rogue or
  mistaken tool call* to the mounted workspace — the agent genuinely cannot read, write, or execute
  shell commands against anything outside `/workspace` and `/data`, because nothing else exists in
  its filesystem view. It does **not** protect against a *malicious admin-token holder* deliberately
  choosing to mount something broader, and it does not add process-level sandboxing (seccomp/AppArmor
  profiles, a non-root container user, read-only root filesystem) — those are standard Docker
  hardening practices, valuable, and simply not designed here in detail; the point of this section
  is narrower and more load-bearing: making sure the *default, documented* deployment shape is
  confined, not that every possible Docker misconfiguration is prevented.
- **Relationship to §10.4's deferred multi-owner isolation**: if a later revision extends this
  service to serve more than one caller/workspace from one running container, §10.4's full
  `WorkspaceRoot`-per-owner work becomes necessary *in addition to* this section's mount-level
  confinement, not instead of it — this section confines the container from the host; §10.4 would
  confine each caller from every other caller *within* the container. Different problems, both real,
  only the first is in scope for v1.

## 6. Relationship to the Telegram integration — and messaging integrations generally

Per §3's confirmed decision (settled in `ReadMe_TelegramIntegrationTool.md` §0), this isn't two
designs that happen to fit together — `Litos.Api` is the **sole, exclusive host** for any
chat-platform bridge. There is no "runs in either, pick one" question to resolve, and no
double-polling/dual-hosting configuration to avoid, because there is only ever one place this code
runs:

- **`TelegramBridge`/`TelegramSessionDriver`/`TelegramPairing`** (`ReadMe_TelegramIntegrationTool.md`
  §6.1–6.3) live inside `Litos.Api`, as a sub-namespace of that project rather than a separate
  `Litos.Telegram` project — since `Litos.Api` is the only caller, the extra project boundary the
  original Telegram-side draft proposed doesn't earn its keep. Long-polling (§4 there) means the
  container needs no *additional* exposed port for Telegram itself beyond the admin UI's own
  (§5.5) — the bridge is started from `Litos.Api`'s `AgentWorker`/DI container, never from
  `Litos.Gui`.
- **The "explicit on/off toggle" decision** (`ReadMe_TelegramIntegrationTool.md` §3, §6.2) is a
  setting on `Status.razor`.
- **The elevation gate** (`ReadMe_TelegramIntegrationTool.md` §7) is naturally an
  `Approvals.razor` entry (§5.4 point 4) — the same approval surface every other `Litos.Api`
  action already uses, not a Telegram-specific side channel.
- **`Litos.Gui` genuinely has nothing to do with any of this.** There's no "running both at once"
  scenario to reason about, because `Litos.Gui` never hosts a bridge in the first place — someone
  running `Litos.Gui` on their desktop and `Litos.Api` in a container on a home server
  simultaneously is just "using two independent faces of the same brain for two different
  purposes" (local keyboard use vs. remote/chat access), the same relationship `Litos.Console` and
  `Litos.Gui` already have with each other today.
- **This generalizes beyond Telegram**: `ReadMe_TelegramIntegrationTool.md` §5 sketches a shared
  `IChannelBridge` shape specifically because `Litos.Api` being the sole messaging host makes a
  consistent pattern worth having before a second platform (WhatsApp, Slack) arrives — every future
  chat-platform bridge follows the same "lives inside `Litos.Api`, administered from the same
  Blazor Server UI, routed through the same `HttpApprovalGate`" shape this section describes for
  Telegram specifically.

## 7. Attachments, photos, and voice messages (Telegram/WhatsApp parity with `Litos.Gui`)

`Litos.Gui`/`Litos.Console` already treat attachments as first-class input
(`ReadMe_AgentDesign.md` §6/§6.1/§6.2): a user can attach a file (converted to Markdown via
`IAttachmentConverter`/`MarkItDownAttachmentConverter`) or a photo (read as raw bytes into an
`ImageBlock(string MediaType, byte[] Data)`, sent to the model as native vision content — no
captioning). Confirmed by inspection, **no chat-platform doc discusses this at all today** —
neither `ReadMe_TelegramIntegrationTool.md` nor this document's earlier sections mention file,
photo, or audio attachments anywhere; a remote caller messaging via Telegram or WhatsApp currently
has no designed path to send anything but text. This section closes that gap so `Litos.Api`
reaches parity with the desktop faces rather than being a text-only shadow of them.

### 7.1 Photos and file attachments: reuse the existing pipeline, don't reinvent it

Telegram and WhatsApp both deliver photos/documents as a downloadable file (a `file_id` for
Telegram, a media URL for WhatsApp Cloud API) attached to an inbound message, which is exactly the
shape `IAttachmentConverter`'s `AttachmentSource` already models:

```csharp
// src/Litos.Tools/Attachments/IAttachmentConverter.cs — existing, unchanged
public abstract record AttachmentSource;
public sealed record FilePathSource(string Path) : AttachmentSource;
public sealed record StreamSource(Stream Stream, string? Extension, string? MimeType) : AttachmentSource;
public sealed record UrlSource(Uri Url) : AttachmentSource;
```

`TelegramSessionDriver`/a future `WhatsAppSessionDriver` (`ReadMe_TelegramIntegrationTool.md` §6.3)
downloads the platform's file reference into a `StreamSource` (or, if the platform hands back a
directly fetchable URL, a `UrlSource`) and feeds it to the **same** `IAttachmentConverter` every
other face already uses — no new conversion logic, no new document-format support. The
image-vs-document split stays identical to `AttachHandler`'s existing rule (§1 of the earlier
research: images bypass MarkItDown and become an `ImageBlock`; recognized document formats go
through MarkItDown into a `DocumentMarkdown`, folded into the turn's `TextBlock` under an
`### Attachment: {Title}` header, exactly as `Litos.Console`/`Litos.Gui`'s `BuildTurnContent` do
today (`src/Litos.Console/Program.cs:418-431`, `src/Litos.Gui/MainWindow.axaml.cs:451-466`)).

**One new rule specific to remote callers**: per `ReadMe_TelegramIntegrationTool.md` §10.3's
untrusted-content boundary-marking convention (adopted from OpenClaw for Tavily search results and
`UrlSource`-derived documents generally), any `DocumentMarkdown` produced from a chat-platform
attachment gets wrapped in the same
`<<<EXTERNAL_UNTRUSTED_CONTENT source="telegram_attachment:{file_id}">>> ... <<<END_...>>>`
markers before entering the transcript. A photo/document arriving over Telegram is exactly the
"untrusted external content" case that convention exists for — the sender is a remote chat
participant, not the local keyboard user.

`TelegramSessionDriver`'s existing job (§6.3) — building the turn content passed to
`AgentLoop.RunTurnAsync` — grows one step: alongside the plain-text message, it now also builds the
`IReadOnlyList<ContentBlock>` the same way `BuildTurnContent` does locally, using the shared
`IAttachmentConverter` DI registration already wired in `LitosHostBuilder` (line 56) rather than a
new one.

### 7.2 Voice messages: transcribe via OpenAI Whisper before the turn ever starts

Telegram voice messages (`.ogg`/Opus) and WhatsApp voice notes (`.ogg`/AMR) are downloaded the same
way as any other attachment (§7.1), but **never** reach the model as audio — they're transcribed to
text first, and the transcript is what becomes the turn's content. This sidesteps needing
per-provider native-audio-input support entirely (no provider in this codebase — OpenAI, Anthropic,
Gemini, OpenRouter — has an audio-content-block mapping today; adding one would be new work in
every `IChatProvider` implementation for a capability Whisper already gives us for free at the
input boundary instead).

- **No new `AudioBlock` `ContentBlock` type.** Unlike `ImageBlock`, audio never needs to survive as
  a distinct block reaching the provider — by the time a voice message becomes part of a turn, it's
  already plain text. The transcript is folded into the turn the same way a document's
  `DocumentMarkdown.Markdown` is: as a `TextBlock`, under a clearly-labeled header (e.g.
  `### Voice message transcript:`) so the model can tell it was spoken, not typed, without needing
  a new block type to express that distinction.
- **Whisper call reuses the existing OpenAI SDK dependency, not a new HTTP client.** Confirmed by
  inspection: `src/Litos.Providers.OpenAI/Litos.Providers.OpenAI.csproj` already references the
  official `OpenAI` NuGet package (v2.12.0), and that package's `AudioClient` type already exposes
  `TranscribeAudioAsync` — the exact Whisper transcription call needed — off the same `OpenAIClient`
  instance `LitosHostBuilder.cs:73` already constructs (`new OpenAIClient(openAiKey)`), via a
  `client.GetAudioClient()`-style sub-client accessor matching the existing
  `GetOpenAIModelClient()`/`GetResponsesClient()` pattern in `OpenAiChatProvider.cs`. No new NuGet
  package, no hand-rolled Whisper HTTP wrapper.
- **A new, narrow abstraction — not folded into `IChatProvider`.** Transcription is a capability
  needed regardless of which model provider the user has configured for chat (someone running
  Anthropic as their chat provider can still receive Telegram voice messages), so it can't be a
  method on `IChatProvider` itself. Following the `WebSearchTool`/Tavily precedent
  (`LitosHostBuilder.cs:44-45` — a capability registered unconditionally, keyed on its own API key,
  independent of the configured chat provider), a small `IAudioTranscriber` seam is the right shape:
  ```csharp
  // Litos.Providers.OpenAI/IAudioTranscriber.cs — shape, not final
  public interface IAudioTranscriber
  {
      Task<string> TranscribeAsync(Stream audio, string mimeType, CancellationToken ct);
  }
  ```
  registered whenever `OPENAI_API_KEY` (or a dedicated `WHISPER_API_KEY`, open question §10) is
  present, independent of whichever provider is registered as `IChatProvider`.
- **Credential**: reuses `config.GetApiKey("openai")` (`LitosHostBuilder.cs:73`'s existing lookup)
  by default — no new credential plumbing needed if the user already has an OpenAI key configured
  for chat or otherwise. If a user runs Anthropic/Gemini as their chat provider and has never
  configured an OpenAI key at all, voice-message support is simply unavailable until they add one;
  this document doesn't propose a non-OpenAI transcription fallback (out of scope, §9).
- **Failure mode, stated explicitly**: if transcription fails (bad audio, transient API error, no
  key configured), the turn should still proceed with a clearly-marked placeholder (e.g. `[voice
  message received, transcription failed]`) rather than silently dropping the user's message or
  blocking the turn indefinitely — mirrors `AttachHandler`'s existing "skip with a warning" pattern
  for unrecognized binary attachments rather than inventing a new failure convention.

### 7.3 What doesn't change

- `Litos.Gui`/`Litos.Console`'s own attachment handling is untouched — this section is additive to
  `Litos.Api`/the chat-platform bridges only, per §0/§6's standing rule that `Litos.Gui` carries no
  messaging-bridge code and this doesn't add any attachment code to it either.
- `POST /sessions/{id}/turns`'s own `attachments` field (§4, `ReadMe_AgentDesign.md` §10.2/§10.3)
  stays exactly as unspecified as before for the *direct HTTP API* case — a caller posting directly
  to `Litos.Api` (as opposed to messaging through Telegram/WhatsApp) is a separate, still-open
  question about wire format (base64 inline? multipart? pre-uploaded reference?), not resolved by
  this section, which is scoped specifically to the chat-platform-bridge path where the platform
  itself (Telegram/WhatsApp) already defines how the file arrives.

## 8. Slash commands in the headless service

`Litos.Gui`/`Litos.Console` each hand-roll their own interactive slash-command switch
(`MainWindow.axaml.cs:553`, `Program.cs:490`) — a plain-text `/command` typed into a text box,
dispatched by a local `switch`, often backed by a modal picker (session list, model list, skill
list) for the argument. None of that UI machinery exists in `Litos.Api` — there's no keyboard, no
modal, and (per §3's confirmed decision) v1 has exactly one implicit workspace/caller rather than
an operator picking freely among many. So each command lands in one of three places: **(a)** kept
as a literal chat-text command a remote user can type (only where it's safe for *any* linked
chat/API caller to trigger), **(b)** moved to the admin UI as an operator-only action (where it's
config/security-sensitive), or **(c)** made implicit/automatic (where the original command existed
only to work around a keyboard-driven UI's lack of automation).

| Command | Litos.Gui behavior | Headless (`Litos.Api`) equivalent | Where |
|---|---|---|---|
| `/new` | Starts a fresh session | Literal chat command (`/new`), settled in full in `ReadMe_TelegramIntegrationTool.md` §6.6: generates a fresh `sessionId`, creates an empty `Transcript`, repoints the chat's `chatId → sessionId` entry at it — old session untouched on disk, just no longer current; also achievable by a caller hitting `POST /sessions/{id}/turns` with a fresh `{id}` directly | (a) Chat command *and* implicit via the API's own session-id semantics |
| `/resume` | Picker over past sessions, loads one | Literal chat command, and — unlike most rows here — **not actually picker-degraded**: `ReadMe_TelegramIntegrationTool.md` §6.6 uses Telegram's native **inline keyboard** (tappable buttons attached to the bot's reply, one per recent session, sourced from `ITranscriptStore.ListSessionsAsync`) — a real picker equivalent, not a text-argument fallback | (a) Chat command, picker-equivalent via Telegram's own UI |
| `/branch` | Picker over past *messages*, forks session at that point | Unlike `/resume`, an inline-keyboard picker is a poorer fit here — a session can hold many messages, and Telegram's inline keyboards aren't built for long, scrollable lists the way a Gui modal is. Left as a **chat command taking a message-index or short id** (`/branch 12`), with a paginated inline keyboard (a `/branch` reply offering "show more") as a possible refinement, or deferred to admin UI if the text form proves unusable in practice | (a) Chat command, degraded UX vs. Gui's picker — **open question, §11** |
| `/attach` | Explicit command opening a file picker or taking a URL arg | **Fully implicit** — any photo/document/voice message arriving on the channel is attached automatically (§7.1/§7.2); there's no picker to trigger and no reason to require a `/attach` prefix before something the platform already delivered as an attachment | (c) Implicit, no command needed |
| `/provider` | Picker over configured providers, switches active one | **Admin UI only** (`Status.razor`, extended with a provider selector) — per §5.5's trust model, switching the underlying model/API-key-billed provider is an operator/config action, not something a remote chat participant (even an elevated one, §7 of the Telegram doc) should be able to trigger; letting any linked chat flip billing-relevant config is a footgun this design avoids deliberately | (b) Admin UI, operator-only |
| `/model` | Picker over models for the active provider | **Admin UI only**, same reasoning as `/provider` — also config-not-content, and doing it per-remote-caller would fight the "one workspace, one active model" v1 scope (§3) rather than needing a picker at all | (b) Admin UI, operator-only |
| `/skills` | Picker listing discovered skills, inserts a `/skill/<name>` token | Literal chat command with **no argument**: the bridge replies with a plain-text numbered list (same `SkillDiscovery.DiscoverAsync` call, rendered as chat text instead of a modal) | (a) Chat command, text-list instead of picker |
| `/skill` | Force-loads a named skill as a pending attachment for the next message | Literal chat command **with** the name argument required (`/skill research`), since there's no two-step "browse then pick" flow over chat text — same `SkillTool.InvokeAsync` call underneath | (a) Chat command, argument-only form |
| `/compact` | Force-triggers context compaction | Literal chat command (`/compact`) — same `Compactor.ForceCompactAsync` call; also reasonable as an **admin UI button** on a session-detail view if one gets built, but the chat form needs no picker and is cheap to keep | (a) Chat command (and optionally (b) admin UI convenience button) |
| `/export` (Console-only today) | Writes transcript to a file | **Admin UI only** — exporting a transcript to a file on the *server's* filesystem doesn't make sense as a remote chat action (where would the file go — into the chat as a document reply? that's a different, new feature, not a port of Console's file-write); a `Sessions.razor`-style admin page offering "download transcript" is the natural headless home | (b) Admin UI, new surface not yet in §5.1's page list |
| *(unknown command)* | Prints "Unknown command: {command}" | Same fallback text reply from the bridge | (a) Chat command error path |

**Key architectural point**: none of this requires new `Litos.Agent`/`Litos.Host` logic — every
command above already resolves to the same shared classes the research identified
(`Transcript`, `ITranscriptStore`, `Compactor.ForceCompactAsync`, `SkillDiscovery`, `SkillTool`,
`IChatProviderFactory`, `IChatProvider.ListModelsAsync`). What changes per command is purely
**which face gets to trigger it and how the argument is supplied** — a third, headless-specific
dispatch layer (chat-text parsing in `TelegramSessionDriver` for the (a) commands, new admin-page
controls for the (b) commands) sitting alongside Gui's switch and Console's switch, not a
replacement for either.

**Split rationale, stated as a rule rather than case-by-case**: a command is chat-safe **(a)** if
triggering it can't change cost/billing/security posture and its blast radius is scoped to the
caller's own session; it's admin-only **(b)** if it changes config shared across every caller
(provider/model) or touches the server's own filesystem outside the chat transport (export); it's
implicit **(c)** only where the original command's entire purpose was working around a keyboard
UI's lack of an automatic trigger (attach) — there's exactly one such case today.

## 9. What's explicitly out of scope for v1

Named here so the boundary is a decision, not an oversight:

- **Multi-workspace/multi-session isolation between several callers sharing one container** (§3,
  §5.6, §10.4 of the design doc) — v1 is one workspace, one implicit caller (the admin token
  holder). The `WorkspaceRoot`-per-owner sandboxing §10.4 describes is real, necessary work, but
  only once a second mutually-untrusted caller needs to share one running container — not needed
  for "one person's personal agent, reachable from their phone/laptop via a token." **Not to be
  confused with confining that one workspace to the host, which §5.6 treats as in-scope for v1** —
  this bullet is specifically about isolating multiple callers from *each other*, not about
  isolating the container from the host.
- **Process-level container hardening** (non-root user, read-only root filesystem, seccomp/AppArmor
  profiles) — §5.6 confines the container's *filesystem view* via mount policy, which is the
  primary and most load-bearing control for this use case, but doesn't additionally harden the
  container process itself. Standard, well-documented Docker practice; just not designed here.
- **Full identity/multi-tenant auth** (§5.5) — one shared admin token, not per-user accounts.
- **A complete Dockerfile/compose setup** (§3) — this document establishes the shape (§5.1, §6.3),
  not a finished, tested deployment artifact.
- **Encryption at rest for `ADMIN_TOKEN`/bot token/config volume** — inherits the same plaintext
  posture `ReadMe_TelegramIntegrationTool.md` §6 already flagged as a named open question there,
  not resolved here either; if anything, a container makes this marginally easier to reason about
  (a named Docker volume has clearer ownership than a loose file on a desktop OS), but doesn't
  change the fundamental plaintext-secret tradeoff.
- **Pending-approval persistence across restarts** (§5.3) — named as a known limitation, not fixed
  in v1.
- **Non-OpenAI transcription fallback** (§7.2) — voice-message support is unavailable without an
  OpenAI API key configured; no alternative transcription provider is designed here.

## 10. Build sequence (if this moves to implementation)

Sized similarly to how `ReadMe_TelegramIntegrationTool.md` §9 scopes its own build sequence —
small-to-medium, almost entirely additive, reusing `Litos.Host` unchanged throughout:

1. **`Litos.Api` skeleton** — `WebApplicationBuilder` + `AddLitosAgent(...)` wiring, a bare `GET
   /status` endpoint, no approval gate or Telegram yet. Proves the composition root works
   identically in an ASP.NET Core host as it does in Avalonia/Terminal.Gui.
2. **`AgentWorker` + `POST /sessions/{id}/turns`** — the turn-driving loop and SSE streaming (§5.2,
   §4), with `IToolApprovalGate` bound to a trivial auto-approve stub initially (mirrors
   `GuiApprovalGate`'s own bootstrapping history) so the core loop is provable before the approval
   UI exists.
3. **`HttpApprovalGate` + `Approvals.razor`** — the `TaskCompletionSource` bridge (§5.3) and the
   live SignalR-pushed approval list, replacing the stub from step 2. This is the milestone that makes
   tool-call approval actually work end-to-end headless.
4. **`AdminTokenFilter`** (§5.5) — locks down every page/endpoint added so far before any of this
   is exposed beyond localhost.
5. **`TelegramSetup.razor`** (§5.4, §6) — builds `TelegramBridge`/`TelegramPairing`
   (`ReadMe_TelegramIntegrationTool.md` §6.1) inside `Litos.Api`, wires the elevation-gate flow
   into the same `Approvals.razor` from step 3.
6. **Dockerfile + deployment docs** (§5.6, §6.3) — multi-stage build, the two-mount policy
   (workspace + state dir, §5.6) as the documented default rather than left to the deployer to
   figure out, and the loopback-vs-LAN exposure footgun called out explicitly in whatever setup
   instructions ship with this.
7. **Attachment/photo/voice parity** (§7) — wire the shared `IAttachmentConverter` into
   `TelegramSessionDriver` (and any later WhatsApp driver) for photos/documents (§7.1), add the
   `IAudioTranscriber` seam backed by the OpenAI SDK's `AudioClient` (§7.2), and route voice-message
   transcripts into the turn the same way document attachments already are. Sequenced last since it
   depends on the Telegram bridge (step 5) already working for plain text.
8. **Chat-text command parsing in the bridge, plus admin-page equivalents** (§8) — add the
   `/new`/`/resume`/`/branch`/`/skills`/`/skill`/`/compact` text-command parsing to
   `TelegramSessionDriver` (reusing the exact `Litos.Agent`/`Litos.Tools` calls Gui/Console already
   use), and add provider/model selection plus transcript export to the admin UI. Sequenced after
   the Telegram bridge and attachments so both the transport and the shared session-lookup plumbing
   already exist.

## 11. Open questions

- **Should `Litos.Api` eventually subsume `Litos.Console`'s `--stdio` mode** (§10.2 of the design
  doc, itself still unbuilt), or do they stay separate, purpose-built entry points (one for
  language-agnostic subprocess callers, one for a persistent networked service)? Not urgent — both
  are still hypothetical until either has an actual caller, per §10's own "which to build, and
  when" guidance.
- **Reverse-proxy/HTTPS guidance**: §5.5 recommends loopback-only `-p` binding as the safe default,
  but a genuinely remote-reachable deployment (checking on your agent from outside your home
  network) needs either a VPN/Tailscale-style overlay network or a properly TLS-terminated reverse
  proxy in front of it — this document doesn't pick one, since it depends heavily on the individual
  user's existing home-network setup, but the eventual docs should say *something* rather than
  leave "how do I safely reach this from outside my house" unanswered.
- **When does §10.4's full workspace isolation actually become necessary?** This document defers
  it (§3, §9) on the basis of "one implicit caller," but if the admin-token model is ever extended
  to multiple named users sharing one container (not currently proposed), that's the trigger point
  — worth flagging now so a future revision doesn't have to rediscover why it was deferred.
- **Pending-approval persistence across restarts** (§5.3) — is this worth fixing in the same
  milestone as the rest of the approval bridge, or a deliberate, documented v1 limitation to
  revisit only if it proves painful in practice?
- **Whisper credential naming** (§7.2): reuse `config.GetApiKey("openai")` as-is (simplest, but ties
  voice transcription to having an OpenAI key specifically, even for users on a different chat
  provider), or introduce a distinct `WHISPER_API_KEY`/`OPENAI_API_KEY` fallback chain mirroring how
  `LitosConfig` already separates chat-provider keys from capability keys like Tavily's? Not
  resolved here — worth deciding once this section moves toward implementation (§10 step 7).
- **`/branch`'s degraded UX over chat text** (§8): is a message-index/short-id argument acceptable,
  or does branch-forking need an admin-UI page (a session-detail view listing messages with a
  "branch here" button) to be usable in practice? Flagged in §8's table as not fully resolved.
