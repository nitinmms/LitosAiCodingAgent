# LitosAiAgent — Rocket.Chat Integration Feasibility

Evaluates a third chat-platform bridge hosted inside `Litos.Api` (`ReadMe_HeadlessServiceTool.md`),
this time against [Rocket.Chat](https://github.com/RocketChat/Rocket.Chat) — a self-hosted, open-source
team chat server — so a user can DM their agent through their own Rocket.Chat instance the same way
`ReadMe_TelegramIntegrationTool.md` and `ReadMe_WhatsAppIntegrationTool.md` already designed for Telegram
and WhatsApp. Written before any implementation; no code has changed as a result of this document. Builds
on `ReadMe_AgentDesign.md` (architecture), `ReadMe_HeadlessServiceTool.md` (the `Litos.Api` host this
lives inside), and `ReadMe_TelegramIntegrationTool.md` (§0's "`Litos.Api` is the sole home for messaging
integrations" decision, and §5's `IChannelBridge` abstraction — this is that abstraction's *third*
implementation, after Telegram and WhatsApp). Facts about Rocket.Chat below are drawn from its own
developer docs (`developer.rocket.chat`, `docs.rocket.chat`), fetched directly for this document, not
recalled from general knowledge — anything not confirmed against a real source is flagged explicitly
rather than assumed.

## 1. What this is, in one sentence

A third `IChannelBridge` implementation inside `Litos.Api`, `RocketChatBridge`, that authenticates to a
user-owned, self-hosted Rocket.Chat instance as a bot **user account**, subscribes to that account's
direct-message stream over Rocket.Chat's Realtime API (DDP over WebSocket), feeds each inbound DM into
the same `AgentLoop` every other face already drives, and replies via Rocket.Chat's REST API — under its
own `SessionOwner.RocketChat`-scoped session and the same `HttpApprovalGate`/elevation-gate trust model
Telegram and WhatsApp use, set up and monitored from a new `RocketChatSetup.razor` page on `Litos.Api`'s
existing Blazor Server admin UI.

## 2. What Rocket.Chat is, and its three integration paths — confirmed by inspection of its docs

Rocket.Chat is a full self-hosted team chat **server** (Node.js/Meteor, MongoDB-backed) — structurally
unlike either prior platform: Telegram is a hosted bot API requiring no self-hosting at all, and OpenWA
(the WhatsApp doc's companion process) is a thin gateway in front of someone else's network. Rocket.Chat
is the whole chat server itself, self-hosted by the user, and it exposes **three genuinely different**
ways to build a bot against it — this section states all three plainly, since picking one is this
document's first real design decision (§4), not a foregone conclusion the way "use the Bot API" was for
Telegram.

- **REST API + Realtime API** (**the path this document designs against**, §4): a normal Rocket.Chat user
  account, acting as the bot identity, authenticates with a **Personal Access Token** (User ID + token
  pair, generated from that account's own Profile page — confirmed: Rocket.Chat has no separate "bot
  user" primitive in core; the documented pattern is an ordinary user account issued a token) and sends
  messages via `POST /api/v1/chat.sendMessage` (body: `{"message": {"rid": "<roomId>", "msg": "...",
  "tmid": "<threadId>"}}` — confirmed field shapes). Inbound messages are received by subscribing over
  the **Realtime API**, a DDP protocol over WebSocket (`ws://<host>/websocket`), to the
  `stream-room-messages` stream for a given room id — Rocket.Chat's own docs note DDP *method calls* are
  deprecated in favor of REST, but the *subscription* mechanism for live message streaming remains the
  standard, current way to receive messages in real time. Source:
  [developer.rocket.chat/apidocs/realtimeapi](https://developer.rocket.chat/apidocs/realtimeapi),
  [developer.rocket.chat REST send-message](https://developer.rocket.chat/reference/api/rest-api/endpoints/messaging/chat-endpoints/send-message).
- **Built-in Incoming/Outgoing Webhooks**: a no-code feature under Administration > Integrations.
  An **Outgoing Webhook** POSTs a JSON payload (`token`, `channel_id`, `channel_name`, `timestamp`,
  `user_id`, `user_name`, `text`, `trigger_word` — confirmed fields, though a `message_id`-equivalent
  field was **not** confirmed from a primary source) to an external URL whenever a trigger condition
  (e.g. a keyword, or "any message in this room") fires. An **Incoming Webhook** is a URL Rocket.Chat
  generates that, when POSTed to with `{"text": "...", "attachments": [...]}`, creates a message in a
  configured room. Source: [docs.rocket.chat/docs/integrations](https://docs.rocket.chat/docs/integrations).
- **Apps-Engine**: Rocket.Chat's own in-process extension system — a TypeScript app (an `app.json`
  manifest plus a class extending Apps-Engine's `App`) running inside Rocket.Chat's own sandboxed
  execution environment, capable of registering slash commands, message hooks (before/after
  sent/updated/deleted), room hooks, and its own HTTP endpoints. Structurally the closest Rocket.Chat
  analog to this codebase's own `ReadMe_Extensibility.md` — a code-plugin system with lifecycle hooks,
  not just a webhook. Source:
  [developer.rocket.chat/docs/rocketchat-apps-engine](https://developer.rocket.chat/docs/rocketchat-apps-engine).

**Confirmed by user decision, not left open**: this document designs against the **REST API + Realtime
API** path (§4). The other two are named here for completeness and revisited briefly in §9, not
designed in this document.

## 3. Why REST + Realtime over the other two — the actual trade-off

Stating the reasoning explicitly, the way `ReadMe_WhatsAppIntegrationTool.md` §3 stated its
push-vs-pull trade-off, rather than treating the chosen path as obviously correct:

- **Vs. Incoming/Outgoing Webhooks**: webhooks are less code (no WebSocket client, no DDP subscription
  management) but structurally coarser — an Outgoing Webhook fires per *trigger condition* on a
  *room*, not per message to a *bot identity*, and Rocket.Chat's own webhook config is oriented around
  "notify an external system when X happens in this channel," not "this bot account is a first-class
  chat participant." Modeling a DM-based, session-per-user bridge (§6, mirroring Telegram's one-chat-
  one-session model) is a more natural fit for "the bot has its own account and its own inbox" than for
  "an external URL gets pinged on trigger." It also does not obviously avoid new inbound surface either
  — `Litos.Api` still needs a webhook receiver route for the Outgoing Webhook, the same
  `ReadMe_WhatsAppIntegrationTool.md` §3 trade-off the REST+Realtime path sidesteps by making
  `Litos.Api` the one holding an outbound WebSocket connection instead.
- **Vs. Apps-Engine**: an Apps-Engine app is the most "native" integration — slash commands and message
  hooks running inside Rocket.Chat itself — but it inverts this codebase's established hosting model.
  Every prior chat-platform bridge (`ReadMe_TelegramIntegrationTool.md` §0, `ReadMe_WhatsAppIntegrationTool.md`
  §4) lives entirely inside `Litos.Api`, in C#, as an `IChannelBridge` implementation; an Apps-Engine
  app would instead be a second codebase, in TypeScript, running inside a third-party server, that
  calls *out* to `Litos.Api`. That's a legitimate design (§9 revisits it), but it breaks the "one
  channel = one `IChannelBridge` implementation inside `Litos.Api`" pattern the `IChannelBridge`
  abstraction (`ReadMe_TelegramIntegrationTool.md` §5) exists to give every future platform.
- **REST + Realtime keeps the pattern intact**: `Litos.Api` holds the connection (a WebSocket to the
  user's own Rocket.Chat instance, entirely outbound from `Litos.Api`'s perspective — no new inbound
  route, unlike the WhatsApp/OpenWA design), authenticates as a bot user via a token
  (structurally the same shape as Telegram's bot token, §8), and both sends and receives through APIs
  designed for exactly this purpose. It costs a WebSocket/DDP client (new work, §5.1) that Telegram's
  long-polling and WhatsApp's plain REST+webhook didn't need, but it's the option that best preserves
  "one consistent shape across every chat-platform bridge," which is the entire reason `IChannelBridge`
  was sketched ahead of a second platform in the first place.

## 4. Confirmed/proposed design decisions

Following the format `ReadMe_WhatsAppIntegrationTool.md` §4 used — decisions treated as settled for the
rest of this document, distinguishing direct carry-overs from Telegram/WhatsApp from what's newly
decided here for Rocket.Chat specifically:

| Decision | Choice | Rationale |
|---|---|---|
| Host process | **`Litos.Api` exclusively**, per Telegram doc §0 — no exception for Rocket.Chat | Same reasoning as every prior bridge: one place messaging integrations are configured, run, and administered |
| Integration path | **Bot user account via REST API + Realtime API** (§2, §3), not built-in webhooks or Apps-Engine | Closest structural fit to the existing `IChannelBridge` pattern (§3) — `Litos.Api` holds the connection, no new codebase or language, no coarser trigger-based webhook model |
| Room scope (v1) | **Direct messages only** — one linked Rocket.Chat user DMs the bot account, one session per DM, mirroring Telegram's per-chat session model exactly (`ReadMe_TelegramIntegrationTool.md` §6.5) | Rocket.Chat's channel/private-group/thread semantics are richer than Telegram's, and multi-user rooms raise an unresolved policy question (whose session is it? trigger on every message or only @mentions?) that a v1 doesn't need to answer. DM-only sidesteps it entirely, the same scoping discipline `ReadMe_HeadlessServiceTool.md` §3 applied to single-workspace v1 |
| Rocket.Chat's own hosting | **Prerequisite, out of scope** — this document assumes a Rocket.Chat instance already exists (self-hosted via Docker Compose, confirmed officially supported, §2) and designs only the bridge, not Rocket.Chat's own deployment | Matches how `ReadMe_WhatsAppIntegrationTool.md` treats OpenWA as a given companion service rather than designing its deployment in detail |
| Session model | **Independent `SessionOwner.RocketChat` session**, one per linked DM, mirroring Telegram/WhatsApp exactly | Same reasoning as both prior bridges: keeps "what's on my phone via Rocket.Chat" separate from any other face's session; no structural change to `SessionOwner` beyond one more static value |
| Trust model | **Same elevation-gate design as Telegram/WhatsApp** (`ReadMe_TelegramIntegrationTool.md` §7) — read-only until a human approves elevation via `Approvals.razor` | The gate's reasoning (linking proves possession, not standing trust) is platform-agnostic; no Rocket.Chat-specific weakening or strengthening is warranted |
| Linking mechanism | **DM the bot account a one-time pairing code**, not a QR scan (§6.4) | Rocket.Chat has no QR-based device-linking primitive the way Telegram's `/start` deep link or WhatsApp's multi-device linking do — a self-hosted chat server's natural equivalent is "know the bot's username and message it," so pairing is a typed code instead of a scanned image |
| Multi-platform shape | **Implement `IChannelBridge` as already sketched** (`ReadMe_TelegramIntegrationTool.md` §5.2), confirmed a third time | This is the abstraction's third real caller (after Telegram, then WhatsApp) — by this point the interface should need zero changes if the sketch was sound; §5.3 checks this claim explicitly |
| Scope of this document | **Design/feasibility only**, matching all three prior documents' pattern — no code written here | — |

## 5. Architecture

### 5.1 Project shape: `Litos.Api/Channels/RocketChat/`

Mirrors `ReadMe_TelegramIntegrationTool.md` §6.1's and `ReadMe_WhatsAppIntegrationTool.md` §5.2's
layout, sub-namespaced inside `Litos.Api` for the identical reasoning given in both: `Litos.Api` is the
only caller, so a separate project buys nothing.

```
src/Litos.Api/Channels/RocketChat/
├── RocketChatBridge.cs          # IChannelBridge — owns the DDP/WebSocket connection lifecycle
├── RocketChatRealtimeClient.cs  # thin DDP-over-WebSocket client: connect, login, subscribe, method calls
├── RocketChatRestClient.cs      # thin typed HttpClient wrapper: chat.sendMessage, auth.login (§8)
├── RocketChatPairing.cs         # pairing-code generation/validation, linked-user persistence (§6.4)
├── RocketChatSessionDriver.cs   # per-linked-DM: drives AgentLoop.RunTurnAsync (mirrors Telegram §6.3
│                                 #   almost verbatim — see §6 below for the deltas)
└── RocketChatConfig.cs          # instance URL, bot credentials, linked-user state, {state-dir}/rocketchat.json
```

New dependency: a DDP/WebSocket client. No official first-party .NET SDK for Rocket.Chat's Realtime API
was located during this document's research — `@rocket.chat/sdk` (the closest thing to an "official"
client, source at `github.com/RocketChat/Rocket.Chat.js.SDK`) is Node.js-only, and its current
maintenance activity was **not confirmed** during research (flagged, not assumed current). Two realistic
options for `RocketChatRealtimeClient`, neither yet decided:

- Hand-roll a minimal DDP client atop .NET's built-in `System.Net.WebSockets.ClientWebSocket` — DDP's
  message shape (`{"msg": "connect", ...}`, `{"msg": "method", ...}`, `{"msg": "sub", ...}`) is a plain
  JSON-over-WebSocket protocol, not a binary or otherwise exotic format, so this is plausible without a
  third-party package, mirroring this codebase's existing preference for a thin hand-written wrapper over
  a generated SDK (`ReadMe_WhatsAppIntegrationTool.md` §5.2's `OpenWaClient` precedent).
- Search for a community .NET DDP or Rocket.Chat client package before committing to hand-rolling one —
  not done as part of this document's research (§9); worth a deliberate check before implementation,
  since "no official SDK" doesn't mean "no community package exists."

### 5.2 `IChannelBridge` implementation, checked against the existing sketch — third confirmation

`ReadMe_TelegramIntegrationTool.md` §5.2's interface:

```csharp
public interface IChannelBridge
{
    string ChannelName { get; }
    Task StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
    Task<PairingHandle> BeginPairingAsync(CancellationToken ct);
}
```

holds up for `RocketChatBridge` with no structural change needed — the third platform in a row to
confirm this, after Telegram (§5.2 of that doc) and WhatsApp (`ReadMe_WhatsAppIntegrationTool.md` §5.3):

- `ChannelName => "rocketchat"` — used for `SessionOwner.RocketChat` exactly as sketched.
- `StartAsync` — opens the DDP/WebSocket connection, authenticates as the bot user (§8), and issues the
  `stream-room-messages` (or equivalent "subscribe to my DMs") subscription. Structurally closer to
  Telegram's `StartAsync` (an always-running connection `Litos.Api` itself holds and owns) than to
  WhatsApp's (a passive webhook registration) — this is push-based like WhatsApp in that messages arrive
  asynchronously rather than being polled, but the connection itself is outbound-initiated and
  long-lived like Telegram's polling loop, not an inbound HTTP route Rocket.Chat calls into. A genuine
  third shape, not a clone of either prior `StartAsync`.
- `StopAsync` — closes the WebSocket connection cleanly.
- `BeginPairingAsync` — generates a pairing code and returns it as text (via `PairingHandle`, the same
  shape `TelegramSetup.razor`/`WhatsAppSetup.razor` consume) for `RocketChatSetup.razor` to display —
  not a QR image this time (§4's linking-mechanism decision), just a code to DM the bot.

### 5.3 Turn-driving: `RocketChatSessionDriver`, deltas from `TelegramSessionDriver`

The turn-driving loop itself — resolve/create a `Transcript` under `SessionOwner.RocketChat`, start or
steer a turn via `AgentLoop.RunTurnAsync`, translate `AgentEvent`s to outgoing messages — is the same
shape `ReadMe_TelegramIntegrationTool.md` §6.3 already designed, and this document does not repeat it in
full. Deltas specific to Rocket.Chat:

1. **Trigger is a DDP subscription event, not a polled update or a webhook POST.** `RocketChatRealtimeClient`
   raises an event when a `stream-room-messages` notification arrives for a room the bot account is a
   member of; `RocketChatBridge` filters to DM rooms only (§4) and hands off to `RocketChatSessionDriver`
   the same way `TelegramBridge`'s receiver callback does for Telegram.
2. **Sending replies is a plain REST POST** (`chat.sendMessage`), not an SDK call —
   `RocketChatRestClient.SendMessageAsync(roomId, text)` wraps `POST /api/v1/chat.sendMessage`, called
   from the same `TextDelta`-buffering/coalescing logic `ReadMe_TelegramIntegrationTool.md` §6.7
   describes. Rocket.Chat's own per-message character limit is configurable server-side (Administration
   > Settings > Message > "Maximum Allowed Characters Per Message") — **no universal default value was
   confirmed** during this document's research (a commonly-cited ~5000 figure could not be pinned to a
   version-specific primary source, §9), so `RocketChatSessionDriver`'s chunking threshold should be a
   configured value (§8) rather than a hardcoded constant the way Telegram's 4096-character cap is.
3. **Threading is a first-class message field, unlike either prior platform.** Rocket.Chat's
   `chat.sendMessage` body supports `tmid` (thread message id) — replies can be sent as a threaded reply
   to the DM's original message rather than a flat chronological stream. Not designed in detail here
   (§9); a natural fast-follow once the flat-DM version works, since neither Telegram nor WhatsApp's
   designs needed to consider threading at all.
4. **No inline-keyboard equivalent confirmed.** Telegram's `/resume` picker
   (`ReadMe_TelegramIntegrationTool.md` §6.6) relies on Telegram's inline-keyboard `callback_query`
   mechanism. Rocket.Chat's REST API does support message `attachments`/`blocks`-style rich content, but
   whether it offers a genuine tappable-button-with-callback primitive comparable to Telegram's was
   **not confirmed** during this document's research — named explicitly in §9. Until confirmed, the same
   safe fallback `ReadMe_WhatsAppIntegrationTool.md` §6 point 3 adopted applies here too: a plain
   numbered text list, reply with the number.
5. **No typing-indicator equivalent confirmed either** — not located in the research performed for this
   document; also §9.
6. **Elevation gate and boundary-marking are unchanged.** Every message arriving via
   `RocketChatSessionDriver` routes through `Litos.Api`'s `HttpApprovalGate` exactly like Telegram and
   WhatsApp (`ReadMe_TelegramIntegrationTool.md` §7), and any attachment content gets the same
   `<<<EXTERNAL_UNTRUSTED_CONTENT source="rocketchat_attachment:{id}">>>` boundary-marking
   (`ReadMe_TelegramIntegrationTool.md` §10.3) — only the `source` tag's prefix changes per channel.

### 5.4 Pairing flow, end to end

1. User creates a Rocket.Chat user account for the bot (or reuses an admin-designated one) on their own
   instance, generates a **Personal Access Token** for it from that account's Profile > Personal Access
   Tokens page (`docs.rocket.chat/docs/manage-personal-access-tokens` — confirmed: yields a User ID +
   token pair, shown once, non-expiring), and pastes the instance URL, bot User ID, and token into
   `RocketChatSetup.razor` (or sets them via env vars, §8).
2. Authenticated admin clicks "Link a Rocket.Chat account" on `RocketChatSetup.razor`.
   `RocketChatPairing` generates a random, single-use pairing code and displays it as plain text (no QR
   — §4) alongside instructions: "DM `@<bot-username>` on your Rocket.Chat instance with this code."
3. User opens Rocket.Chat (any device — it doesn't need to be the one viewing the admin page), starts a
   DM with the bot account, and sends the pairing code as a message.
4. `RocketChatBridge`'s DDP subscription (already running once "Enable Rocket.Chat" is on, mirroring
   Telegram's on/off toggle, `ReadMe_TelegramIntegrationTool.md` §3/§6.2) receives the DM,
   `RocketChatPairing` validates the code (matches, not expired, not already consumed), and on success
   records `(roomId, userId, linkedAt)` in `rocketchat.json`'s persisted state (§8). The code is
   invalidated immediately after use.
5. The bot replies with a confirmation message ("Linked to Litos ✅") and `RocketChatSetup.razor`
   updates live (same Blazor Server SignalR-circuit mechanism `Approvals.razor` already uses,
   `ReadMe_HeadlessServiceTool.md` §5.3) once linking is detected.
6. From then on, any message in that `roomId` is routed to `RocketChatSessionDriver` (§5.3); messages
   from any other room (including channels/groups the bot might technically be able to see, per §4's
   DM-only scope) are ignored outright, not just deprioritized.

### 5.5 Slash commands, attachments, elevation — unchanged in shape

Following the exact precedent both prior bridges established:

- `/new`/`/resume` work identically to `ReadMe_TelegramIntegrationTool.md` §6.6, scoped to that DM's own
  `SessionOwner.RocketChat` sessions — `/resume`'s picker degrades to the same numbered-text-list
  fallback WhatsApp's design already adopted (§5.3 point 4 above), pending confirmation of any richer
  Rocket.Chat interactive-message primitive.
- Photos/file attachments reuse the existing `IAttachmentConverter`/`AttachmentSource` pipeline exactly
  as `ReadMe_HeadlessServiceTool.md` §7.1 designed for Telegram/WhatsApp — Rocket.Chat messages carry
  file attachments as downloadable URLs (via its own file-upload REST endpoints), fitting the
  `UrlSource`/`StreamSource` shape already in place. Not re-derived in full here since §7.1 of that
  document already generalizes past "Telegram/WhatsApp" to any chat-platform bridge.
- Voice messages, if Rocket.Chat's client supports recording them, follow `ReadMe_HeadlessServiceTool.md`
  §7.2's transcription-before-turn design unchanged — whether Rocket.Chat's mobile/desktop clients
  support voice-message recording at all was **not confirmed** during this document's research (§9).
- The elevation gate (`ReadMe_TelegramIntegrationTool.md` §7) applies unchanged: a newly-linked
  Rocket.Chat DM lands read-only until a human approves elevation via `Approvals.razor`, the same
  surface every other bridge and every other `Litos.Api` action already uses.

## 6. Configuration & secrets

Following `LitosConfig`'s established pattern (`ReadMe_TelegramIntegrationTool.md` §8), a new
`rocketchat.json` under `Litos.Api`'s `/data` mount:

```json
{
  "instanceUrl": "https://chat.example.com",
  "botUserId": "aBcD1234...",
  "botAuthToken": "...",
  "linkedDms": [
    { "roomId": "xYz789...", "userId": "aBcD5678...", "sessionId": "a1b2c3...", "linkedAt": "2026-07-28T10:00:00Z", "elevated": false }
  ],
  "enabled": false
}
```

Two credentials plus an instance URL — the bot account's User ID and Personal Access Token (analogous to
Telegram's single bot token, but a pair rather than one string, since Rocket.Chat's REST/Realtime auth
scheme requires both headers, §2) and the self-hosted instance's own base URL, since — unlike Telegram's
fixed `api.telegram.org` — every Rocket.Chat deployment lives at a different, user-controlled address.
Resolved via `config.GetApiKey`-style env-var-first lookup (`ROCKETCHAT_URL`/`ROCKETCHAT_USER_ID`/
`ROCKETCHAT_AUTH_TOKEN`), same precedence order every other key already uses, not added to
`ChatProviderNames` (mirrors the Tavily/Telegram precedent, `ReadMe_TelegramIntegrationTool.md` §8).
Inherits the same plaintext-JSON-at-rest posture already flagged as an open question by both prior
documents — not re-litigated here (§9).

## 7. What this does *not* change

Consistent with every prior bridge's additive posture: no changes to `AgentLoop`, `ITool`,
`ToolRegistry`, `Litos.Host`, `Litos.Tools`, `Litos.Persistence`, or `Litos.Gui`. `SessionOwner` gains one
more static value (`SessionOwner.RocketChat`) the same trivial way `SessionOwner.Telegram` and
`SessionOwner.WhatsApp` did. Everything else is additive inside `Litos.Api`.

## 8. Open questions and unconfirmed details

Named explicitly because this document's Rocket.Chat research came from its developer docs and general
docs site, not its full source or a hands-on install — similar in depth to
`ReadMe_WhatsAppIntegrationTool.md`'s OpenWA research (README/docs-level), narrower than
`ReadMe_TelegramIntegrationTool.md` had available for `Telegram.Bot` (an official, thoroughly documented
SDK):

- **No .NET DDP/Realtime client was confirmed to exist.** §5.1 names hand-rolling one atop
  `ClientWebSocket` as the fallback, but a community package search was not performed as part of this
  document — worth doing before committing to a hand-rolled client.
- **`@rocket.chat/sdk`'s current maintenance status** was not confirmed — it's Node.js-only regardless
  (irrelevant to a hand-rolled .NET client decision), but its existence as a reference implementation of
  the DDP handshake/subscription flow could still be useful to consult during implementation.
- **Exact DDP message schemas** for login (`method: "login"`) and the `stream-room-messages` subscription
  payload shape were confirmed at a high level (§2) but not exhaustively — the precise field-by-field
  message contract needs to be pinned down against Rocket.Chat's Realtime API docs or a working reference
  client before `RocketChatRealtimeClient` can be implemented.
- **Maximum message character limit** — confirmed to be a configurable workspace setting, but no
  universal default value was confirmed from a version-pinned source (§5.3 point 2). `RocketChatSessionDriver`'s
  chunking threshold should be configured (§6), not hardcoded, precisely because of this uncertainty.
- **Rate limiting** — Rocket.Chat's REST API has per-endpoint rate limiting enabled by default,
  configurable as request-count/interval pairs, exposed via `x-ratelimit-*` response headers — but no
  universal default numeric value was confirmed (docs show an illustrative example, not a stated
  current default). `RocketChatRestClient` should read and respect these headers defensively rather than
  assume a specific ceiling.
- **Interactive message/button support** and **typing-indicator equivalent** (§5.3 points 4–5) —
  unconfirmed either way; treat as unavailable until verified against Rocket.Chat's actual message-format
  docs (`attachments`/`blocks` fields) rather than assumed present.
- **Threading (`tmid`) as a first-class reply mechanism** (§5.3 point 3) — confirmed to exist as a field,
  not designed in detail here; whether `RocketChatSessionDriver` should thread every reply or stay flat
  is a UX call, not an architecture one.
- **Voice-message support in Rocket.Chat's own clients** — not confirmed either way (§5.5); if absent,
  `ReadMe_HeadlessServiceTool.md` §7.2's transcription design simply has no trigger for this channel,
  which is a non-issue, not a gap to fix.
- **Apps-Engine as an alternative worth a second look** (§3) — this document set it aside because it
  inverts the hosting model established by every prior bridge, but it remains the most "native" Rocket.Chat
  integration (slash commands, message hooks, running inside Rocket.Chat itself) — if a future need arises
  for behavior only Apps-Engine can provide (e.g. reacting to messages in channels the bot wasn't
  directly DMed in, without polling), it's worth revisiting as its own document rather than retrofitted
  into this one.
- **Multi-instance / multi-DM support**: this document assumes one Rocket.Chat instance, one bot account,
  linked to (eventually) possibly several DMs, each with its own session and independent elevation status
  — structurally supported already (§6's `linkedDms` array), but whether `RocketChatSetup.razor` needs to
  support viewing/managing multiple linked DMs from the start is a v1-scoping call, same open question
  `ReadMe_TelegramIntegrationTool.md` §9 left open for Telegram.
- **`IChannelBridge`/`ChannelSessionDriver` extraction timing**
  (`ReadMe_TelegramIntegrationTool.md` §5.3, revisited by `ReadMe_WhatsAppIntegrationTool.md` §9) — this
  document is the *third* platform confirming the interface itself needs no change (§5.2); the shared
  turn-driving helper extraction question is now ripe for a decision rather than a third deferral, but
  is not resolved here.
