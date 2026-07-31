# LitosAiAgent — Telegram Integration Feasibility

Evaluates a built-in Telegram bridge hosted inside `Litos.Api` (the headless service —
`ReadMe_HeadlessServiceTool.md`): the user scans a QR code once, from `Litos.Api`'s Blazor Server
admin UI, to link a Telegram chat to a running headless instance, then chats with the agent
remotely through Telegram — replies, tool calls, and results flow the same way they would if the
user were typing locally. Written before any implementation; no code has changed as a result of
this document. Builds on `ReadMe_AgentDesign.md` (architecture, §5 agent loop, §7.2 steering, §9
composition root, §10.3 `Litos.Api`) and `ReadMe_HeadlessServiceTool.md` (the `Litos.Api` project
this feature lives inside), and follows `ReadMe_Extensibility.md`'s citation-heavy,
verify-by-inspection style, since this is the same kind of pre-implementation scoping exercise.

**Revision note**: this document originally proposed hosting the Telegram bridge inside
`Litos.Gui` (the desktop app). That approach is superseded — confirmed decision, not an open
question — by hosting it inside `Litos.Api` instead (§0). The desktop-hosted version is kept out
of this document entirely rather than described as a discarded alternative, since carrying two
competing designs forward invites exactly the split-brained ambiguity that prompted the change.

## 0. Why `Litos.Api`, not `Litos.Gui` — and why messaging integrations live there exclusively

Confirmed design decision, settled directly (not left as a trade-off table entry, because it's
foundational to everything below): **`Litos.Gui` carries no messaging-bridge code of any kind.**
Telegram — and, per §5's shared abstraction, any future platform (WhatsApp, Slack) — is hosted
exclusively inside `Litos.Api`. `Litos.Gui` stays what it already is: a desktop app for sitting at
the keyboard, with no chat-platform awareness.

Two things made this the right call, not just a tidier one:

- **The original `Litos.Gui`-hosted design carried a structural flaw its own text admitted**:
  `ReadMe_TelegramIntegrationTool.md` §4 (as it read before this revision) stated the bridge
  "requires `Litos.Gui`'s desktop process to be running" — meaning "chat with your agent from your
  phone" only worked while a desktop window happened to be open on a PC. `Litos.Api` (per
  `ReadMe_HeadlessServiceTool.md` §1) exists specifically to run continuously, headless, on a
  server/NAS/always-on box — the natural home for anything that needs to be reachable "from
  anywhere, any time," which a Telegram bridge always was.
- **Splitting "where does this run" across two faces invites exactly the ambiguity a design
  document exists to prevent.** An earlier draft of this decision considered letting both
  `Litos.Gui` and `Litos.Api` host the bridge (with a caveat about not double-polling the same bot
  token from two processes at once) — workable, but it means every future reader has to hold two
  mental models of "where does Telegram actually live" instead of one. Consolidating into
  `Litos.Api` exclusively removes that ambiguity outright: there is exactly one place a chat
  platform integration is configured, run, and administered.

This reframes `Litos.Api`'s role from `ReadMe_HeadlessServiceTool.md` §3's original "alternative
deployment mode, opt-in for people who want a server instead of a desktop app" to something
slightly stronger: **`Litos.Api` is the one and only way to get remote/chat-platform access to
your agent.** `Litos.Gui` remains fully capable of running standalone with zero chat integrations,
exactly as it does today — this isn't "Gui becomes crippled," it's "messaging was never a Gui
feature to begin with, and now the docs say so plainly instead of hedging." Anyone who wants
Telegram (or, later, WhatsApp/Slack) stands up `Litos.Api`; anyone who only wants the desktop
experience never needs to.

## 1. What this is, in one sentence

A background service inside `Litos.Api` that polls the Telegram Bot API for messages sent to a
user-owned bot, feeds each one into the *same* `AgentLoop` every other face already drives, and
sends the agent's replies back to Telegram — with its own independent, disk-persisted session, set
up and monitored entirely through `Litos.Api`'s Blazor Server admin UI (`TelegramSetup.razor`,
`Approvals.razor` — `ReadMe_HeadlessServiceTool.md` §5.1, §5.4).

## 2. Where Litos stands today (confirmed by inspection)

- **No inbound networking of any kind**, independent of this feature. A repo-wide search for
  `HttpListener`, `WebApplication`, `Kestrel`, `WebSocket`, `TcpListener` returns zero matches in
  `Litos.Agent`/`Litos.Tools`/`Litos.Host`. Every existing `HttpClient` usage (`WebSearchTool`,
  `OpenRouterModelCatalog`, `OpenRouterChatProvider`) is outbound-only. The Telegram bridge itself
  stays outbound-only too (§4) — the inbound-*shaped* surface this feature actually needs
  (an admin UI reachable over HTTP) is `Litos.Api`'s own Kestrel listener, already scoped and
  designed in `ReadMe_HeadlessServiceTool.md`, not something this document needs to design again.
- **`Litos.Api` already provides the background-service and web-UI infrastructure this feature
  needs.** `ReadMe_HeadlessServiceTool.md` §5.2 establishes `AgentWorker : BackgroundService` and
  a Blazor Server admin UI running in the same process/container. This document's Telegram bridge
  is additional background work hosted in that same process (§6), not a reason to invent a second
  hosting mechanism.
- **No QR code library, no Telegram SDK.** Neither `QRCoder` nor `Telegram.Bot` appears in any
  `.csproj`/`packages.lock.json` in the solution. Both are new dependencies, added to `Litos.Api`
  (§5.1), not to `Litos.Gui`.
- **The agent loop is already UI-agnostic and streaming-first.** `AgentLoop.RunTurnAsync`
  (`src/Litos.Agent/AgentLoop.cs:55`) is a plain method on a zero-UI-dependency class returning
  `IAsyncEnumerable<AgentEvent>` — nothing about it assumes Avalonia, ASP.NET Core, a console, or
  any particular caller. Every face drives it the same way: create a `CancellationTokenSource`,
  optionally create a `Channel<SteeringMessage>`, `await foreach` the event stream, dispatch on
  event type. A Telegram bridge is structurally **one more driver** of this same method, hosted
  inside `Litos.Api`'s `AgentWorker` (§6) rather than a bespoke desktop integration.
- **Mid-turn steering already exists and is exactly the right shape for "a new message arrives
  while the agent is busy."** `RunTurnAsync`'s optional `ChannelReader<SteeringMessage>` parameter
  (`src/Litos.Agent/AgentLoop.cs:62`) is consulted between tool calls; a `SteeringMode.Steer`
  message interrupts the current plan (remaining pending tool calls in that round get synthetic
  "skipped" results), a `SteeringMode.FollowUp` message waits until the turn has no pending tool
  calls. `MainWindow.axaml.cs` (`SendSteeringMessage`, line 349) is the existing reference
  implementation for how a face writes into a live turn's steering channel — the Telegram driver
  reuses the same channel mechanics, just from `Litos.Api`'s `AgentWorker` instead of a GUI event
  handler. See §6.3.
- **`SessionOwner` exists precisely for this scenario and is currently unused for anything but a
  single hardcoded value.** `SessionOwner` (`src/Litos.Agent/Session/SessionOwner.cs:5`) is a
  string-wrapping partition key already threaded through every `ITranscriptStore` method
  (`AppendAsync`, `ReadAsync`, `ListSessionsAsync`, `BranchAsync` —
  `src/Litos.Agent/Session/ITranscriptStore.cs`) and `AgentLoop.RunTurnAsync` itself. Today
  exactly one value exists, `SessionOwner.Local`, used everywhere. `JsonlTranscriptStore`
  (`src/Litos.Persistence/JsonlTranscriptStore.cs`) already stores sessions at
  `{sessions-root}\{owner}\{sessionId}.jsonl` — the owner segment of that path is already live
  infrastructure, just never exercised with a second value. Adding `SessionOwner.Telegram` costs
  nothing structurally; it's the mechanism this field was built for. Inside `Litos.Api`, the
  sessions root is the `/data` mount `ReadMe_HeadlessServiceTool.md` §5.6 already establishes.
- **Config/secrets follow one established, plaintext-JSON pattern.** `LitosConfig`
  (`src/Litos.Host/LitosConfig.cs`) resolves every key (env var first, config file as fallback)
  via `GetApiKey(name)`. `WebSearchTool`'s Tavily key is the precedent for a tool-only key that is
  *not* added to `ChatProviderNames` (`LitosConfig.cs:26`) — a Telegram bot token follows the exact
  same shape (§8), and inside `Litos.Api` resolves against the same env-var-first pattern
  `ReadMe_HeadlessServiceTool.md` §5.5 already uses for `ADMIN_TOKEN`.
- **The trust model was built for "the person sitting at the keyboard" — and, per §0, `Litos.Api`
  is where that assumption gets revisited for every remote caller, not just Telegram.**
  `GuiApprovalGate` (`src/Litos.Gui/GuiApprovalGate.cs`) auto-approves every tool call
  unconditionally, but that's irrelevant here — `Litos.Api` has its own approval gate
  (`HttpApprovalGate`, `ReadMe_HeadlessServiceTool.md` §5.3), and per §3's confirmed decision below,
  Telegram-originated tool calls and device-elevation requests both route through it, the same
  approval surface every other `Litos.Api`-hosted action uses.

## 3. Confirmed design decisions

These were settled during scoping and are treated as fixed for the rest of this document, not as
open questions:

| Decision | Choice | Rationale |
|---|---|---|
| Host process | **`Litos.Api` exclusively** — no Telegram code in `Litos.Gui` (§0) | Removes the "must keep a desktop window open" limitation the original design carried, and avoids two competing hosting stories for the same feature |
| Session model | **Independent Telegram-only session** — its own `SessionOwner`, its own `Transcript`, decoupled from any `Litos.Gui` window that might separately be open | Keeps "what's running on my phone" and "what's on my screen right now" from stepping on each other; no risk of a remote message landing mid-edit in a session someone is actively looking at locally |
| Trust model (v1) | **Same trust as sitting at the keyboard** by default, but per §0.1 of `ReadMe_HeadlessServiceTool.md`'s approval-UI decision, routed through `HttpApprovalGate` — a linked Telegram chat's tool calls are visible and interceptable via `Litos.Api`'s Approvals page, not silently auto-approved the way `GuiApprovalGate` would | Linking via QR is the consent gesture (physical possession of both the admin UI screen and the phone); routing through the same approval surface every other `Litos.Api` action uses (rather than a Telegram-specific bypass) is what makes the elevation-gate design in §7 possible at all |
| Transport | **Long-polling** via the `Telegram.Bot` NuGet package's `getUpdates`-based receiver, not a webhook | All connections are outbound from `Litos.Api`'s container to Telegram's servers — no additional exposed port beyond the admin UI's own (`ReadMe_HeadlessServiceTool.md` §5.5), no TLS certificate for Telegram specifically, nothing new to expose on top of what `Litos.Api` already requires |
| Linking mechanism | **QR encodes a one-time deep link** (`https://t.me/<BotUsername>?start=<pairing-code>`), displayed on `TelegramSetup.razor` | Telegram bots have no native "scan to link" primitive; a `/start <code>` deep link is the standard pattern and requires nothing beyond what the Bot API already supports — rendering it as a `<img>` data URI on a Blazor page (§6.2) is a direct, no-extra-endpoint way to show it |
| Bot lifecycle | **Explicit on/off toggle** on `Litos.Api`'s Status page, off by default even when a bot token is configured | Avoids a surprise always-on remote-access channel the moment a token exists in config; matches this codebase's general preference for explicit, visible state over implicit magic |
| Mid-turn message handling | **Reuse the existing steering channel exactly as-is** — a Telegram message that arrives while a turn is running is written to the turn's `ChannelWriter<SteeringMessage>` as `SteeringMode.Steer`, identical to the steering mechanism every face uses | No new interruption model to design or explain; "typing while it's thinking" behaves the same from Telegram as from any other caller of `AgentLoop` |
| Multi-platform design | **Sketch a shared `IChannelBridge` abstraction now** (§5), even though only Telegram is built first | Avoids Telegram-specific naming/shape leaking into places WhatsApp/Slack would later need to change; `Litos.Api` being the sole home for messaging integrations (§0) makes a shared shape worth designing before a second platform forces a rework |
| Scope of this document | **Design/feasibility only** — matches `ReadMe_Extensibility.md`'s pattern of a pre-implementation study; no code is written as part of producing this doc | — |

## 4. Why long-polling, concretely

The `Telegram.Bot` package (the de facto standard .NET Telegram Bot API client) wraps
`getUpdates` behind a receiver API (`StartReceiving`) that internally loops, calling Telegram's
servers and invoking a callback per incoming message — `Litos.Api` never accepts an inbound
connection *for Telegram specifically*; it only ever makes outbound HTTPS calls to
`api.telegram.org`, the same shape every other `HttpClient` in this codebase already uses (§2).
This sidesteps every operational problem a Telegram-specific webhook would introduce on top of
what `Litos.Api` already exposes (`ReadMe_HeadlessServiceTool.md` §5.5): no second certificate, no
second port, no coupling the bridge's availability to whatever reverse-proxy/tunnel setup fronts
the admin UI. The cost is a small, constant polling overhead and slightly higher latency than a
push — both irrelevant at the scale of one user's personal bot.

## 5. A shared shape for future platforms: `IChannelBridge`

Per §3's confirmed decision, this is sketched now — before a second platform is built — precisely
*because* §0 makes `Litos.Api` the sole home for every messaging integration going forward
(Telegram now; WhatsApp, Slack, or others later, per the original request that prompted this
document's rework). The goal here is narrow: name the seam that would let a second platform be
added without Telegram-specific assumptions leaking into shared code, **not** to design WhatsApp
or Slack support itself — that remains a separate, later exploration (§9).

### 5.1 What's actually platform-specific vs. shared, based on what §6 below builds for Telegram

Looking at the concrete Telegram design in §6, three things are inherently platform-specific and
three are not:

**Platform-specific** (would need a new implementation per platform):
- The wire protocol itself (Telegram Bot API's `getUpdates`/`sendMessage` vs. WhatsApp Business
  API's webhook-and-reply model vs. Slack's Events API/Socket Mode) — these are different enough
  (some push, some pull; different message-formatting rules, different rate limits) that no single
  client library spans them.
- The linking/pairing mechanism's *specific mechanics* — Telegram's `/start <code>` deep link
  (§6.2) has no exact WhatsApp or Slack equivalent (WhatsApp Business has its own device-linking
  QR flow via the Business API; Slack uses OAuth app installation, not a per-chat pairing code).
- Message formatting/chunking rules (§6.7's 4096-character Telegram cap and paragraph-boundary
  chunking is a Telegram-specific constant; other platforms have different limits or none).

**Shared, and worth a common interface now**:
- **Turn-driving**: "take an incoming message, resolve/create a session under a
  platform-specific `SessionOwner`, drive `AgentLoop.RunTurnAsync` or write into an in-flight
  turn's steering channel, translate `AgentEvent`s back to outgoing messages" — this is exactly
  what `TelegramSessionDriver` (§6.3) does, and none of that logic is Telegram-specific; it's the
  same shape `MainWindow.SubmitAsync` and `Litos.Console`'s non-interactive loop already use.
- **The elevation/approval question**: "should this newly-linked device get tool access" is
  platform-agnostic — it's a `ToolInvocationPreview`-shaped decision routed through
  `HttpApprovalGate` (§7) regardless of which chat platform the device request came from.
- **Session/config persistence shape**: a linked-identity-to-session mapping, an enable/disable
  toggle, a bot-credential lookup via `LitosConfig` — the *shape* of `telegram.json` (§8) is
  reusable verbatim for a hypothetical `whatsapp.json`/`slack.json`, just with a different
  credential type.

### 5.2 The interface sketch

```csharp
// src/Litos.Api/Channels/IChannelBridge.cs — sketch, not final
public interface IChannelBridge
{
    string ChannelName { get; }              // "telegram", "whatsapp", "slack" — used for SessionOwner
    Task StartAsync(CancellationToken ct);    // begin listening/polling
    Task StopAsync(CancellationToken ct);
    Task<PairingHandle> BeginPairingAsync(CancellationToken ct);  // returns whatever TelegramSetup.razor
                                                                    //   (or a future WhatsappSetup.razor)
                                                                    //   needs to render — a QR, a code, a link
}

// The turn-driving loop itself (TelegramSessionDriver's generalized shape) is NOT part of this
// interface — it's a shared internal helper (ChannelSessionDriver, taking an IChannelBridge's
// inbound-message stream and outbound-send delegate) that every bridge implementation reuses,
// rather than something each bridge reimplements. See §6.3 for the concrete Telegram instance
// this helper is extracted FROM — the extraction itself is a §9 open question, not built here.
```

`SessionOwner` gains one value per channel (`SessionOwner.Telegram` today, §6.5; a hypothetical
`SessionOwner.WhatsApp`/`SessionOwner.Slack` later) — no structural change to `SessionOwner` itself
is needed, since it's already a plain string-wrapping key (§2).

### 5.3 What this sketch deliberately does not resolve

Consistent with §3's "sketch now, don't over-build" framing:

- Whether `ChannelSessionDriver` (the shared turn-driving helper hinted at above) is actually
  extracted from `TelegramSessionDriver` *before* or *only once* a second platform is built. Doing
  it now risks over-abstracting from a single example; doing it only once WhatsApp/Slack exists
  risks Telegram-specific assumptions being baked in that need unwinding. Left as an open question
  (§9) deliberately, not decided here.
- The elevation/approval UI's generic shape (`Approvals.razor` already handles "any
  `ToolInvocationPreview`-shaped decision" per `ReadMe_HeadlessServiceTool.md` §5.4 point 4, so
  this likely needs no change at all when a second platform arrives — noted, not re-designed).
- Per-platform message-formatting abstractions (chunking, native buttons/inline-keyboards,
  typing indicators) — each platform's formatting quirks are different enough that forcing a
  shared "send a message" interface too early risks the same lowest-common-denominator problem a
  premature abstraction usually creates. `IChannelBridge` intentionally stops at "start/stop/pair,"
  leaving message I/O to each bridge's own driver.

## 6. Architecture

### 6.1 Project shape: `Litos.Api/Channels/Telegram/`

Following `ReadMe_HeadlessServiceTool.md` §5.1's project layout, Telegram integration is a
sub-namespace of `Litos.Api` itself — not a separate project. This differs from the original
`Litos.Telegram`-as-its-own-project sketch: since `Litos.Api` is now the *only* host (§0), there's
no second face that would need to reference a standalone Telegram project independently, so the
extra project boundary no longer earns its keep.

```
src/Litos.Api/Channels/Telegram/
├── TelegramBridge.cs           # IChannelBridge (§5.2) — owns the Telegram.Bot receiver lifecycle
├── TelegramPairing.cs          # pairing-code generation, validation, linked-chat persistence
├── TelegramSessionDriver.cs    # per-linked-chat: drives AgentLoop.RunTurnAsync (§6.3)
├── QrCodeGenerator.cs          # wraps QRCoder to render the pairing deep-link as an image
└── TelegramConfig.cs           # bot token + linked-chat state, {state-dir}/telegram.json (§8)
```

Reference direction stays inward, matching `ReadMe_HeadlessServiceTool.md` §5.1:
`Litos.Api → Litos.Host → Litos.Tools/Providers/Persistence → Litos.Agent`. The Telegram
sub-namespace depends on `Litos.Host` (for `AgentLoopFactory`, `LitosConfig`,
`IChatProviderFactory`) exactly the way `Litos.Api`'s other code already does, plus the new
`Telegram.Bot` and `QRCoder` NuGet packages, both added to `Litos.Api.csproj` only. Nothing in
`Litos.Agent`, `Litos.Tools`, `Litos.Persistence`, or `Litos.Gui` changes.

### 6.2 Lifecycle: started/stopped from `Litos.Api`'s Status page

Per §3's toggle decision, `TelegramBridge` is not started automatically at container boot.
Instead:

- `Litos.Api`'s `Status.razor` page (`ReadMe_HeadlessServiceTool.md` §5.1) gains an
  "Enable Telegram" / "Disable Telegram" toggle and, when enabling for the first time, a
  "Link a Telegram account" action leading to `TelegramSetup.razor`.
- Turning it on resolves `TelegramBridge` via DI (same `IServiceCollection` `Program.cs` builds)
  and calls `StartAsync`; turning it off calls `StopAsync`. Both are plain `Task`-returning
  methods on the `IChannelBridge` interface (§5.2) — no separate hosted-service registration
  needed beyond what `AgentWorker` already establishes for the process.
- The toggle state itself (on/off, independent of whether a token exists) is persisted in
  `telegram.json` (§8) under `Litos.Api`'s `/data` mount (`ReadMe_HeadlessServiceTool.md` §5.6),
  so it *doesn't* silently resume across container restarts by accident — re-enabling after a
  restart is a conscious action, matching the "off by default" decision in §3.
- `TelegramSetup.razor` renders the pairing QR as a `QRCoder`-generated PNG, base64-encoded
  directly into an `<img src="data:image/png;base64,...">` — no separate image-hosting endpoint
  needed, consistent with `ReadMe_HeadlessServiceTool.md` §5.4 point 2's original sketch of this
  exact mechanic.

### 6.3 Turn-driving: `TelegramSessionDriver`, hosted by `Litos.Api`'s `AgentWorker`

`TelegramSessionDriver` drives `AgentLoop.RunTurnAsync` the same way every other caller does —
create a `CancellationTokenSource`, optionally create a `Channel<SteeringMessage>`, `await foreach`
the event stream, dispatch on event type — the same orchestration shape `MainWindow.SubmitAsync`
and `Litos.Console`'s non-interactive loop already establish, just running inside `Litos.Api`'s
`AgentWorker : BackgroundService` (`ReadMe_HeadlessServiceTool.md` §5.2) instead of a GUI event
handler or a console REPL:

1. On a message from a linked chat, resolve (or lazily create) that chat's `Transcript` +
   `sessionId`, scoped under `SessionOwner.Telegram` (§6.5) — one Litos session per linked
   Telegram chat, so a multi-chat setup never mixes conversations.
2. If no turn is currently running for that chat: start one, calling `AgentLoop.RunTurnAsync` with
   `SessionOwner.Telegram` and `Litos.Api`'s `HttpApprovalGate` (§7) as the approval gate — the
   same gate every other `Litos.Api`-hosted turn uses, not a Telegram-specific bypass.
3. If a turn *is* already running for that chat: don't start a second one. Per §3, write the new
   message to that turn's `ChannelWriter<SteeringMessage>` as `SteeringMode.Steer`. This reuses
   `AgentLoop`'s existing interrupt-and-skip-pending-tool-calls behavior verbatim; no new
   concurrency-control code is needed beyond tracking "is a turn live for this chat" (a
   `Dictionary<long, CancellationTokenSource>` keyed by Telegram chat id is sufficient).
4. `await foreach` the `AgentEvent` stream, translating events to Telegram messages:
   - `TextDelta` — buffered and coalesced (§6.7), not sent delta-by-delta.
   - `ToolCallCompleted`/`ToolCallResult` — rendered as a short status line (e.g. `🔧 read_file
     src/Foo.cs`), so a chatty tool result doesn't flood the chat.
   - `ErrorOccurred` — sent as a plain message.
   - `CompactionOccurred` — a short informational message (`⚏ context compacted`).
5. On completion (or cancellation/error), the turn's tracked `CancellationTokenSource` is cleared
   for that chat.

No changes to `AgentLoop`, `ITool`, `ToolRegistry`, `Litos.Host`, or `Litos.Gui` are required —
this is purely additive inside `Litos.Api`.

### 6.4 QR pairing flow, end to end

1. User creates a bot via Telegram's `@BotFather` (outside Litos — a one-time, user-driven step;
   Litos cannot create the bot on the user's behalf) and pastes the resulting bot token into
   `TelegramSetup.razor` (or sets it via `TELEGRAM_BOT_TOKEN` env var, §8).
2. Authenticated admin (via `Litos.Api`'s shared admin token, `ReadMe_HeadlessServiceTool.md` §5.5)
   clicks "Link a Telegram account" on `TelegramSetup.razor`. `TelegramPairing` generates a
   random, single-use pairing code and renders `https://t.me/<BotUsername>?start=<code>` as a QR
   code (§6.2), alongside the same URL as copyable text.
3. User scans with their phone's camera (or taps the link directly, from wherever they're viewing
   the admin page — it doesn't need to be the same device), which opens Telegram and pre-fills a
   `/start <code>` message to the bot; user taps Send.
4. `TelegramBridge`'s receiver loop (already running once "Enable Telegram" is on) receives the
   `/start <code>` update, `TelegramPairing` validates the code (matches, not expired, not
   already consumed), and on success records `(chatId, linkedAt)` in `telegram.json`'s persisted
   state (§8). The pairing code is invalidated immediately after use so a leaked/screenshotted QR
   image can't be reused later.
5. The bot replies with a confirmation message ("Linked to Litos ✅") and `TelegramSetup.razor`
   updates (via the same Blazor Server live-circuit mechanism `Approvals.razor` uses for pending
   approvals, `ReadMe_HeadlessServiceTool.md` §5.3) once linking is detected.
6. From then on, any message from that `chatId` is routed to `TelegramSessionDriver` (§6.3); any
   message from an *unlinked* `chatId` is ignored or gets a fixed "not linked" reply, never
   reaches `AgentLoop`.

### 6.5 Session identity: `SessionOwner.Telegram`

```csharp
// src/Litos.Agent/Session/SessionOwner.cs — one new static value, no structural change
public static SessionOwner Telegram { get; } = new("telegram");
```

Sessions created by `TelegramSessionDriver` use `SessionOwner.Telegram`, landing under
`{sessions-root}/telegram/{sessionId}.jsonl` via the existing `JsonlTranscriptStore` path
convention — no change to `JsonlTranscriptStore` itself. Since `Litos.Gui` never hosts this
feature (§0), there's no cross-face `/resume` visibility question to resolve here the way the
original design needed to — a Telegram session is only ever browsable from within `Litos.Api`
itself, a natural fast-follow (listing `SessionOwner.Telegram` sessions somewhere in the admin UI)
noted in §9 rather than designed in detail here.

One Telegram chat maps to one *current* `sessionId` at any given time, reused by default across
the chat's lifetime (not a fresh session per message, and not a fresh session per container
restart) — otherwise every restart of `Litos.Api` would silently orphan the remote conversation's
history. The mapping `chatId → sessionId` is itself part of `telegram.json`'s persisted state
(§8), and — per §6.6 — is exactly what `/new` and `/resume` repoint.

### 6.6 Starting and resuming sessions from Telegram itself

Confirmed decision: **`/new` and `/resume` work from Telegram, scoped to that chat's own
`SessionOwner.Telegram` sessions only.**

`TelegramSessionDriver` intercepts `/`-prefixed messages before they reach `AgentLoop` — Telegram's
Bot API additionally supports registering a native command menu (`setMyCommands`) so `/new` and
`/resume` show up as tap-to-insert suggestions in the Telegram client itself, not just typed text.

**This section covers `/new`/`/resume` specifically; the full command set is settled in
`ReadMe_HeadlessServiceTool.md` §8**, which was written after this section and resolves what used
to be an open question here (formerly §9's "slash-command parity beyond `/new`/`/resume`," now
removed — see that section's changelog note). In short: `/branch`, `/skills`, `/skill`, `/compact`
follow the identical intercept-before-`AgentLoop` mechanism described here, `/attach` becomes
implicit (any photo/document/voice message is attached automatically, §7 of that same document),
and `/provider`/`/model`/`/export` are deliberately **not** exposed as chat commands at all — moved
to `Litos.Api`'s admin UI, since they change shared config or touch the server's own filesystem
rather than acting within one caller's own session. `TelegramSessionDriver`'s `/`-prefix interception
described above is the single dispatch point all of those chat-exposed commands share; this section
and that one are not two separate designs.

**`/new`** — generates a fresh `sessionId`, creates an empty `Transcript`, repoints this chat's
entry in `telegram.json`'s `chatId → sessionId` map at it. The old session is untouched on disk
(JSONL is append-only) — nothing is deleted, just no longer the chat's *current* session. Bot
replies with a short confirmation (`Started a new session.`).

**`/resume`** — uses Telegram's **inline keyboard** feature: the bot's reply carries tappable
buttons attached to the message, one per recent `SessionOwner.Telegram` session for this chat,
sourced from `ITranscriptStore.ListSessionsAsync`, each button labeled with date plus a trimmed
preview of the first user message. Tapping a button fires a `callback_query` update, which
`TelegramSessionDriver` handles by loading that session's `Transcript` via `Transcript.LoadAsync`
and repointing the chat's `chatId → sessionId` mapping at it. If a turn happens to be running for
this chat when `/resume` or `/new` is sent, it's rejected with a short "a turn is already running"
reply rather than trying to interrupt a live turn with a session switch.

### 6.7 Reply batching (Telegram is not a token-streaming medium)

Sending a Telegram message per token would both hit Bot API rate limits (roughly 30 messages/second
per bot, 1/second per individual chat) and be a genuinely bad chat experience. `TelegramSessionDriver`
instead:

- Buffers `TextDelta` text until the assistant's message segment completes (either a
  `MessageCompleted` boundary, or accumulated text crosses a size threshold — Telegram caps a
  single message at 4096 characters, so a long response is chunked at paragraph boundaries, not
  mid-sentence) before calling `sendMessage`.
- Uses Telegram's `sendChatAction` (`typing`) on turn start and again on any gap longer than a few
  seconds, so the user sees the native "bot is typing…" indicator instead of silence while a long
  tool call runs.

## 7. Trust & security model — the elevation gate

**Confirmed decision (this document's central design point, revised from the original "flat trust,
no gate" v1):** a linked Telegram chat does *not* get full tool access the instant it links. It
lands **read-only** — able to chat, search, and read files — until a human explicitly elevates it
via `Litos.Api`'s shared approval surface. This closes a gap identified directly against
[OpenClaw](https://docs.openclaw.ai/)'s comparable design (§10): scanning a QR proving *who you
are* and a device getting *standing tool access* were originally the same action; they are now two
separate, separately-timed decisions.

- **What the QR-scan proves**: physical possession of both the admin UI screen and the Telegram
  account that scanned it, at that moment — nothing about who controls that account or device
  going forward. This is why elevation is a second, independent gate rather than something the QR
  scan itself grants.
- **The elevation flow, concretely**: immediately after linking (§6.4 step 5), the new chat is
  read-only. `TelegramBridge` raises a `ToolInvocationPreview`-shaped elevation request through
  the same `HttpApprovalGate` (`ReadMe_HeadlessServiceTool.md` §5.3, §7's confirmed
  general-purpose-approval decision) every ordinary tool call already uses. It appears as an entry
  on `Approvals.razor` — *"A new Telegram device linked — allow it to run tools (edit files,
  shell)?"* — indistinguishable in mechanism from approving a `ShellTool` invocation, just a
  different `ToolInvocationPreview.Summary`. An admin approves or denies from the same page they'd
  approve any other action from. Declining leaves the chat permanently read-only until
  re-requested (e.g. by unlinking and relinking, or a future explicit "request elevation" command
  from within the chat itself — left as an open question, §9).
- **What "read-only" concretely restricts, before elevation**: `ShellTool`, `WriteFileTool`,
  `EditFileTool` are unavailable to a not-yet-elevated `SessionOwner.Telegram` turn — enforced as
  a coarse deny-list checked before `ToolRegistry.Resolve` is even reached for those tool names, a
  simpler and cheaper control than per-call approval, and directly closes the "no tool-scoping
  concept exists" gap named in §10.2 below. `WebSearchTool`/`ReadFileTool`/`ListDirectoryTool`/
  `GrepTool` remain available immediately, so a newly-linked phone is useful right away (checking
  status, asking questions) without being immediately dangerous.
- **After elevation**: the chat reaches full parity with any other `Litos.Api`-hosted caller —
  still routed through `HttpApprovalGate` for every tool call exactly like everything else
  `Litos.Api` drives, not auto-approved. This is a meaningfully stronger default than the original
  document's "same trust as sitting at the keyboard, `GuiApprovalGate` auto-approves everything"
  framing, because `Litos.Api` never had that auto-approve shortcut to begin with — every action,
  from any caller, already goes through a real approval surface.
- **Unlinking/revocation**: `telegram.json` needs a "remove linked device" action (on
  `TelegramSetup.razor`, alongside "link a device") that deletes the `chatId` entry and its
  elevation status together — the equivalent of revoking an OAuth grant. Without this, there's no
  way to cut off a lost phone short of rotating the bot token entirely (unlinking every chat, not
  just one).

## 8. Configuration & secrets

Following `LitosConfig`'s exact precedent (§2), a new key:

```csharp
// LitosConfig.cs — EnvVarNames gains one entry, NOT added to ChatProviderNames
["telegram"] = "TELEGRAM_BOT_TOKEN"
```

resolved via the existing `config.GetApiKey("telegram")`, same env-var-first-then-file order
already used for every other key, and the same pattern `ReadMe_HeadlessServiceTool.md` §5.5 uses
for `ADMIN_TOKEN` inside `Litos.Api`. This mirrors the Tavily precedent exactly (§4.3.2 of the
design doc): a session runs fine with zero Telegram token configured, so it is deliberately
excluded from `ChatProviderNames`.

Pairing/link state (linked chat IDs, chat→session mapping, elevation status, on/off toggle) lands
in its own file, under `Litos.Api`'s `/data` mount (`ReadMe_HeadlessServiceTool.md` §5.6) rather
than the host's `~/.litos`-equivalent directory, since — per §0 — it's `Litos.Api`'s state, not
something a desktop install needs to know about at all:

```
{state-dir}/telegram.json
{
  "linkedChats": [
    { "chatId": 123456789, "sessionId": "a1b2c3...", "linkedAt": "2026-07-28T10:00:00Z", "elevated": true }
  ],
  "enabled": false
}
```

Same shape as `LitosConfig` itself — plain JSON, `Load()`/`Save()`, no encryption at rest. This is
consistent with existing practice but worth flagging explicitly rather than silently inheriting: a
Telegram bot token is functionally a credential that grants **read access, and — once a chat is
elevated (§7) — remote tool execution** — a materially higher-stakes secret than an LLM API key.
Whether that gap is closed (e.g. encrypting the volume, or a secret-manager indirection like the
one flagged in the OpenClaw comparison, §10.2) is called out as an open question in §9, inheriting
the same posture `ReadMe_HeadlessServiceTool.md` §7 already accepted for `ADMIN_TOKEN`/the
container's state volume generally — not resolved here, not resolved there either.

## 9. Open questions

- **Secret-at-rest hardening for `telegram.json`/the bot token** (§8): plaintext-on-disk matches
  existing `config.json` practice but is a bigger blast radius here (remote control once elevated,
  not just API spend) — worth a deliberate call before shipping, not an inherited default. Shared
  with `ReadMe_HeadlessServiceTool.md`'s own open question about `ADMIN_TOKEN`/state-volume
  encryption — likely worth resolving both at once rather than separately.
- **Requesting elevation from within the chat itself**: §7 leaves "how does a declined or
  not-yet-elevated chat ask again" unspecified beyond "unlink and relink." A `/request-access`
  command that re-raises the same `Approvals.razor` entry without a full unlink/relink cycle would
  be a small, natural addition — not designed here.
- **Multi-chat support**: this document assumes one bot linked to (eventually) possibly several
  chats, each with its own session and independent elevation status (§6.5's `chatId → sessionId`
  mapping and §7's per-chat elevation flag already support this structurally) — but is a
  single-linked-chat v1 sufficient, or should `TelegramSetup.razor` support viewing/managing
  multiple linked devices from the start?
- **Browsability of `SessionOwner.Telegram` sessions from within `Litos.Api`'s own admin UI**
  (§6.5): a small, clearly-additive fast-follow (a "Telegram sessions" list somewhere in the admin
  UI, mirroring what `/resume`'s picker already does for `Litos.Console`/would have done for
  `Litos.Gui`) — not required for the core loop to work.
- **"Off by default" semantics** (§6.2): does the enable toggle reset to off on every container
  restart, or persist as "on" once explicitly enabled until explicitly turned off again? Both are
  one boolean; this is a UX call, not an architecture one.
- ~~**Slash-command parity beyond `/new`/`/resume`**~~ — **resolved**, not still open: settled in
  `ReadMe_HeadlessServiceTool.md` §8, which maps every `Litos.Gui`/`Litos.Console` slash command to
  one of three headless routes (a literal chat command via `TelegramSessionDriver`'s existing
  `/`-prefix interception, §6.6; an admin-UI-only action for config/filesystem-sensitive commands;
  or an implicit trigger for `/attach`). See §6.6 above for the pointer. `ReadMe_Extensibility.md`
  §4.1's per-face `CommandRegistry` refactor gap is unrelated to this — that's about deduplicating
  *how* each face parses its own commands, not *which* commands a remote chat caller should be
  allowed to trigger, which is what was actually undecided here.
- **Rate limits / abuse**: the Bot API's own rate limits (§6.7) provide a basic ceiling, but there
  is no Litos-side throttling if a linked chat sends many messages in quick succession — likely a
  non-issue for a single-user personal bot, but worth explicit acknowledgment rather than silent
  assumption.
- **`IChannelBridge`/`ChannelSessionDriver` extraction timing** (§5.3): whether the shared
  turn-driving helper is factored out of `TelegramSessionDriver` proactively or only once a second
  platform is actually being built — genuinely undecided, flagged deliberately rather than picked.
- **A WhatsApp or Slack bridge itself** — out of scope for this document entirely (§5's sketch is
  the extent of multi-platform thinking here); either would be its own future feasibility document,
  following this one's structure, once actually prioritized.
- **§10.3's boundary-marking fix is design-complete but unimplemented**: unlike the rest of this
  document's Telegram-specific work, it touches `Litos.Tools`/`Litos.Gui`/`Litos.Console` directly
  and benefits every face, not just this one — worth sequencing independently of
  `ReadMe_HeadlessServiceTool.md` §9's Telegram-hosting build steps rather than bundling it in,
  since it has no dependency on `Litos.Api` existing at all and could ship before or after the
  Telegram bridge itself.

## 10. Comparison with OpenClaw's Telegram integration — security gap analysis

[OpenClaw](https://docs.openclaw.ai/) is a self-hosted gateway that bridges multiple messaging
platforms (Telegram among them) to AI agents — the closest existing prior art for exactly this
feature, and worth checking this design against directly rather than inventing a security model
from first principles. **Scope of this comparison is deliberately narrow: OpenClaw's Telegram
channel and the security controls that apply to it, not its full multi-channel/multi-agent
gateway architecture**, most of which (Discord, WhatsApp, Signal, node pairing, multi-agent
routing) is out of scope for what this document proposes. Everything attributed to OpenClaw below
is quoted or closely paraphrased from its official docs (`docs.openclaw.ai`), fetched directly for
this comparison, not recalled from general knowledge. **Note**: §10.1's "tool-call trust" and
"tool scoping" rows below predate this document's §7 rework (the elevation gate) and are kept as
the historical comparison that motivated that rework — see §10.4 for how §7 changes the picture.

### 10.1 How the two designs compare, feature by feature

| | This design (§3–§8) | OpenClaw (Telegram channel) |
|---|---|---|
| Linking mechanism | QR-encoded one-time deep link (`/start <code>`), self-service, then a **separate elevation approval step** (§7) | Bot token in config/env, then a **separate operator approval step**: unknown senders get a pairing code, but an administrator must run `openclaw pairing approve telegram <CODE>` |
| Sender identity | `chatId` recorded at link time (§6.4, §8) | Numeric Telegram user ID, explicitly *not* username or phone number |
| Default DM policy | Effectively "allowlist-of-one, read-only until elevated" (§7) | Four explicit named policies (`pairing` default, `allowlist`, `open`, `disabled`) — OpenClaw's docs explicitly warn `open` is "last-resort" |
| Tool-call trust | **Tiered, per §7's rework**: read-only until elevated, then routed through the same `HttpApprovalGate` every `Litos.Api` action uses | **Tiered**: `tools.exec.security` has three modes (`full` / `ask` / `deny`) specifically for shell/process execution, independent of DM pairing status |
| Tool scoping | **Coarse deny-list before elevation** (§7) — `ShellTool`/`WriteFileTool`/`EditFileTool` unavailable pre-elevation | `tools.deny`/`tools.profile` can strip whole tool categories per agent, independent of channel — finer-grained than this design's binary pre/post-elevation split |
| Sandboxing | `Litos.Api`'s container filesystem confinement (`ReadMe_HeadlessServiceTool.md` §5.6) — real, but not Telegram-specific | Optional Docker-based sandboxing per agent, with `workspaceAccess` further limited to `none`/`ro`/`rw` |
| Secret storage | Plaintext JSON under `Litos.Api`'s state volume (§8) | Also plaintext by default, but ships a `SecretRef` indirection layer plus `openclaw secrets audit --check` |
| Revocation | "Remove linked device" action noted as required (§7) — including elevation status, not just link status | No revocation flow described in the fetched docs either |
| Untrusted content the agent reads | Not addressed — still an open gap, unchanged from the original comparison | External content wrapped in explicit boundary markers, chat-template tokens sanitized |
| Self-audit tooling | None | `openclaw security audit --fix` — one command, multiple checks |

### 10.2 What remains a genuine gap after §7's rework

Two of the three original gaps are meaningfully addressed by §7; one is not:

1. **Approval and identity are no longer conflated** — §7's elevation gate is a direct, explicit
   response to this gap. Resolved, not just narrowed.
2. **Tool scoping is now coarse but present** — the pre-elevation deny-list (§7) is a real answer,
   though blunter than OpenClaw's per-category `tools.deny`. A finer-grained version (e.g.
   allowing `WriteFileTool` but not `ShellTool` even post-elevation) remains unbuilt — worth a
   sentence in a future revision, not urgent enough to block this one.
3. **Untrusted tool-result content previously got no special handling — now designed, in §10.3.**
   Still the easiest gap to overlook, since it doesn't require a hostile second sender: a single
   elevated, fully-trusted chat's own `web_search`/`web_fetch` results can carry adversarial
   instructions embedded in ordinary page text. §10.3 settles a concrete fix (boundary-marking at
   `WebSearchTool` and `UrlSource`-derived attachments) — design-complete, not yet implemented
   (§9).

### 10.3 Closing the last gap: boundary-marking untrusted content

**Confirmed design, addressing this document's one remaining open item.** Two call sites inject
externally-sourced text into the transcript today with no marking distinguishing it from the
user's own words, confirmed by inspection:

- `WebSearchTool.InvokeAsync` (`src/Litos.Tools/Web/WebSearchTool.cs:69-70`) formats Tavily search
  results as plain `"{Title} — {Url}\n{Content}"` text, joined and returned as an ordinary
  `ToolResult.Ok(...)`.
- The `### Attachment: {Title}` formatting that turns a `DocumentMarkdown` into turn content —
  duplicated identically in `MainWindow.axaml.cs:456` (`Litos.Gui`) and `Program.cs:423`
  (`Litos.Console`) — treats every attachment the same regardless of where its content came from.

**Scope: exactly the content that actually crossed a trust boundary, not everything uniformly.**
`WebSearchTool` output is marked unconditionally — it's always third-party search-engine content,
never anything else. Attachments are mixed and need a source-aware decision: a local file the user
explicitly picked (`FilePathSource`, `StreamSource`) isn't attacker-reachable and marking it would
be noise with no security benefit; a `UrlSource`-derived attachment (`ReadMe_AgentDesign.md` §4.3's
`IAttachmentConverter`) is fetched from an address the *user* supplied but whose *content* is
exactly as untrusted as a search result — the same class of risk, arriving through a different
tool. The fix therefore applies at two call sites, not one:

1. `WebSearchTool.InvokeAsync` — wrap the joined results before returning them.
2. Wherever `DocumentMarkdown` is formatted into turn content (`BuildTurnContent` in
   `MainWindow.axaml.cs`, its `Litos.Console` counterpart) — wrap only when the attachment's
   `AttachResult`/`DocumentMarkdown` originated from a `UrlSource`, which means threading a
   `bool IsFromUrl` (or equivalent) through `AttachResult`/`DocumentMarkdown` from
   `AttachHandler.AttachArgumentAsync`'s existing URL-vs-path branch
   (`src/Litos.Gui/AttachHandler.cs:53`) down to the formatting call — the branch already exists
   to route a URL differently, it just doesn't yet propagate that fact forward to this decision.

**Marker shape: adopted verbatim from OpenClaw**, not a Litos-specific design, and that choice is
deliberate enough to state explicitly rather than silently deviate from local convention. This
codebase already has one precedent for framing injected text with a bracketed instructional
prefix — `AgentLoop.RunTurnAsync`'s steering-message injection
(`src/Litos.Agent/AgentLoop.cs:174`: `"[The user interrupted your in-progress work with this
message — acknowledge briefly, then address it before continuing]: {steer.Text}"`) — which was a
candidate shape to reuse here. It was set aside in favor of OpenClaw's format for two concrete
reasons: OpenClaw's docs describe the marker as specifically effective against chat-template-token
role-forging attacks (§8's own summary: "sanitizes common LLM chat-template tokens... to prevent
tokenizer-level role-forging attacks"), a threat model the steering-message framing was never
designed against; and reusing a syntax already validated by a real, shipping system is a better bet
for a first version than inventing a new one this document would be the only evidence for. Adopted
shape:

```
<<<EXTERNAL_UNTRUSTED_CONTENT source="web_search:{query}">>>
{content}
<<<END_EXTERNAL_UNTRUSTED_CONTENT>>>
```

with `source` set to `web_search:{query}` for search results and `attachment_url:{url}` for a
`UrlSource`-derived attachment — enough for the model to say *what* it's looking at without
implying the content itself is trustworthy. `WebSearchTool`'s system-prompt-visible `Description`
(§4.3.2 of the design doc) should gain one sentence explaining the marker's meaning, mirroring how
`ReadMe_AgentDesign.md` already documents every other tool's contract in its own `Description`
string rather than only in this design doc.

**What this does and doesn't fix, stated plainly, matching this document's own convention for
every other control (§7's elevation gate, §5.6 of `ReadMe_HeadlessServiceTool.md`'s filesystem
confinement)**: this signals to the model that content is data, not instructions, and gives a
capable model a fighting chance at not blindly executing embedded directives — it is not a
guarantee, the same caveat OpenClaw's own docs carry for their identical mechanism. It does not
sandbox, filter, or block anything; a sufficiently adversarial payload can still attempt injection,
just now with an explicit "this part is untrusted" signal surrounding it rather than none at all.
Pairs with, but doesn't replace, `IToolApprovalGate`/the elevation gate (§7) — that layer limits
*what the model can do* if it's fooled; this layer reduces the odds it gets fooled in the first
place.

**Why here, in the Telegram document, rather than a standalone doc**: the fix is general — it
benefits `Litos.Console`/`Litos.Gui` local usage identically, not just Telegram-originated turns —
but this document is what surfaced and motivated it (§10.2), so the design is recorded where its
reasoning lives rather than split across a third file for two call sites of formatting logic.

### 10.4 What this design already does at least as well

- **No inbound exposure beyond what `Litos.Api` already requires**, by construction (§4): the
  Telegram bridge itself adds zero additional exposed surface — it's outbound-only, riding on
  whatever port/auth discipline `ReadMe_HeadlessServiceTool.md` §5.5 already establishes for the
  admin UI, rather than a second thing to secure.
- **Narrower blast radius by scope, not by policy**: this is a single-feature bridge for one
  personal agent on one machine, not a multi-channel, multi-agent, potentially multi-tenant
  gateway — OpenClaw's own docs stress it is "not a hostile multi-tenant security boundary."
- **Elevation is enforced through the same approval mechanism every other action uses** (§7) —
  not a Telegram-specific side door, which is arguably a *more* unified model than OpenClaw's
  separate `tools.exec.security`/pairing-approval axes, at the cost of being coarser-grained.
