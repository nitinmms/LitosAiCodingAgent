# LitosAiAgent — WhatsApp Integration Feasibility (via OpenWA)

Evaluates a second chat-platform bridge hosted inside `Litos.Api` (`ReadMe_HeadlessServiceTool.md`),
using [OpenWA](https://github.com/rmyndharis/OpenWA) — a self-hosted, MIT-licensed WhatsApp API
gateway — as the WhatsApp-facing component, so a user can chat with their agent over WhatsApp the
same way `ReadMe_TelegramIntegrationTool.md` already designed for Telegram. Written before any
implementation; no code has changed as a result of this document. Builds on
`ReadMe_AgentDesign.md` (architecture), `ReadMe_HeadlessServiceTool.md` (the `Litos.Api` host this
lives inside), and `ReadMe_TelegramIntegrationTool.md` (§0's "`Litos.Api` is the sole home for
messaging integrations" decision, and §5's `IChannelBridge` abstraction, sketched specifically so a
second platform would slot in without a rework). This document exercises that abstraction for the
first time rather than re-deriving it, and calls out by name every place WhatsApp genuinely differs
from Telegram rather than silently assuming parity.

## 1. What this is, in one sentence

A second `IChannelBridge` implementation inside `Litos.Api`, `WhatsAppBridge`, that talks to a
companion OpenWA instance (a separate self-hosted process, not a library referenced in-process) over
plain REST + webhooks, feeding each inbound WhatsApp message into the same `AgentLoop` every other
face already drives, under its own `SessionOwner.WhatsApp`-scoped session and the same
`HttpApprovalGate`/elevation-gate trust model Telegram uses — set up and monitored from a new
`WhatsAppSetup.razor` page on `Litos.Api`'s existing Blazor Server admin UI.

## 2. What OpenWA is, confirmed by inspection of its repo and README

Fetched directly from `github.com/rmyndharis/OpenWA`, not recalled from general knowledge about
WhatsApp integrations generally:

- **Purpose**: a self-hosted, free WhatsApp API gateway — REST API + webhooks for programmatic
  WhatsApp automation, positioned as infrastructure you run yourself rather than a hosted SaaS.
- **Connects to WhatsApp via reverse-engineered client libraries, not Meta's official Cloud API.**
  Two selectable engines: `whatsapp-web.js` (headless Chromium mimicking WhatsApp Web, ~300–500 MB
  per session, lower ban-risk) or `@whiskeysockets/baileys` (direct multi-device WebSocket protocol
  implementation, ~30–80 MB per session, higher account-restriction risk). This is a load-bearing
  fact for §9's risk discussion below — neither engine is Meta's sanctioned integration path.
- **Auth/pairing**: QR-code scan from the WhatsApp mobile app, the same "link a device" gesture
  WhatsApp Web itself uses — structurally the closest thing to Telegram's `/start <code>` deep link
  (§6.4 of the Telegram doc) that WhatsApp has, though the underlying mechanics differ (native
  WhatsApp multi-device linking vs. a bot receiving a slash command).
- **Stack**: TypeScript on Node.js 22 LTS, NestJS 11.x. SQLite or PostgreSQL, optional Redis,
  optional S3/MinIO for media storage. Ships a Docker Compose setup and a bundled React dashboard
  (port 2886 in dev; dashboard and API share port 2785 in the packaged config).
- **REST surface** (confirmed endpoint shapes):
  ```
  POST /api/sessions                              # create a session
  GET  /api/sessions/{sessionId}/qr                # fetch pairing QR
  POST /api/sessions/{sessionId}/messages/send-text
       { "chatId": "628123456789@c.us", "text": "..." }
  ```
  plus (named in the README but not fully inspected here — §9) media messaging, reactions, edits,
  group operations, and contact/profile management.
- **Webhooks with HMAC signing** deliver inbound events (message receipt, status changes, session
  lifecycle) to a URL you configure, with optional smart filtering on fields like `sender`,
  `recipient`, `body`, `type`, `mentions`, `fromMe`, `hasMedia`, `isGroup`. **The exact payload JSON
  schema and the HMAC header name/algorithm were not resolved from the README excerpt fetched for
  this document** — named explicitly in §9 as something to pin down against OpenWA's source (or a
  fuller docs page) before implementation, not assumed here.
- **Multi-session**: supports several concurrent WhatsApp sessions in one OpenWA instance.
  Configurable rate limiting; docs recommend "a few messages per minute" as a sustainable send rate
  — relevant to §7's batching design.
- **License**: MIT — "free for personal and commercial use."
- **Standalone (non-Docker) run path exists**: `npm install && npm run dev` after cloning, confirming
  OpenWA doesn't strictly require Docker Compose, though Compose is its documented production path.

## 3. The one structural difference from Telegram: push, not pull

`ReadMe_TelegramIntegrationTool.md` §4 chose long-polling specifically so `Litos.Api` would never
need to accept an inbound connection for the bridge itself — every call is outbound to
`api.telegram.org`. **OpenWA inverts this**: it delivers inbound WhatsApp messages to `Litos.Api` via
webhook, which means `Litos.Api` must expose a new inbound HTTP endpoint
(`POST /channels/whatsapp/webhook` or similar) that OpenWA calls. This is the first genuinely new
exposed route in `Litos.Api` beyond the admin UI itself — `ReadMe_TelegramIntegrationTool.md` §2 and
`ReadMe_HeadlessServiceTool.md` §2 both note, as of their writing, zero inbound-networking surface
existed anywhere in the repo except the admin UI's own Kestrel listener; this document's design adds
exactly one more inbound route to that previously-single-purpose listener.

Concretely, this means:

- The webhook endpoint needs its own auth check — **not** the `AdminTokenFilter`'s bearer/cookie
  scheme (`ReadMe_HeadlessServiceTool.md` §5.5), since OpenWA is a machine caller, not a browser
  session. HMAC signature verification (OpenWA's own mechanism, §2) is the natural fit: reject any
  webhook POST whose signature doesn't match a shared secret, using the same
  `CryptographicOperations.FixedTimeEquals` constant-time-comparison discipline
  `ReadMe_HeadlessServiceTool.md` §5.5 already applies to `ADMIN_TOKEN`.
- §5.5's "exposure footgun" warning applies with *more* force here, not less: Telegram's bridge adds
  zero exposed surface (§4 of that doc), but WhatsApp's does add one — the webhook route. If
  `Litos.Api` is published as recommended with `-p 127.0.0.1:8080:8080` (loopback-only), OpenWA
  (running as a sibling container on the same Docker network, §5 below) can still reach it via the
  Docker-internal network without the port ever touching the LAN — worth stating explicitly as the
  reason the two containers should share a Docker network rather than talk over the host's LAN
  interface.
- Unlike Telegram's `TelegramBridge.StartAsync` (which starts an outbound polling loop),
  `WhatsAppBridge.StartAsync` is closer to "register the webhook URL with OpenWA and start accepting
  calls" — the always-running half of the work happens in OpenWA's own process, not inside
  `Litos.Api`.

## 4. Confirmed/proposed design decisions

Following `ReadMe_TelegramIntegrationTool.md` §3's format — decisions treated as settled for the
rest of this document, distinguishing what's a direct carry-over from Telegram (confirmed by that
document's own reasoning) from what's newly decided here for WhatsApp specifically:

| Decision | Choice | Rationale |
|---|---|---|
| Host process | **`Litos.Api` exclusively**, per Telegram doc §0 — no exception for WhatsApp | Same reasoning as Telegram: one place messaging integrations are configured, run, and administered, not split across faces |
| WhatsApp connectivity | **Via a companion OpenWA instance, not a direct protocol implementation in .NET** | No .NET library implements WhatsApp's multi-device protocol; OpenWA already absorbs that complexity (and its risk, §9) behind a plain REST/webhook API — reimplementing `whatsapp-web.js`/Baileys-equivalent logic in C# is out of scope and not sensible when a working MIT-licensed gateway already exists |
| Transport | **Inbound webhook from OpenWA, HMAC-verified** (§3) — a new exposed route, unlike Telegram's zero-new-surface long-polling | OpenWA's own design is webhook-first; fighting that by polling OpenWA's REST API for new messages instead would work but throws away the push model for no benefit, and adds latency |
| Session model | **Independent `SessionOwner.WhatsApp` session**, one per linked WhatsApp chat, mirroring Telegram's per-chat session model exactly (`ReadMe_TelegramIntegrationTool.md` §6.5) | Same reasoning: keeps "what's on my phone via WhatsApp" separate from any other face's session, no structural change to `SessionOwner` needed beyond one more static value |
| Trust model | **Same elevation-gate design as Telegram** (`ReadMe_TelegramIntegrationTool.md` §7) — read-only until a human approves elevation via `Approvals.razor` | The gate's reasoning (linking proves possession, not standing trust) is platform-agnostic; no WhatsApp-specific weakening or strengthening is warranted |
| Linking mechanism | **Proxy OpenWA's own QR**, don't generate a second one | OpenWA already renders a pairing QR via `GET /sessions/{id}/qr` for WhatsApp's native multi-device linking; `Litos.Api` fetches and displays that image rather than inventing a parallel pairing scheme the way Telegram's `/start <code>` deep link needed to (WhatsApp has no bot-command pairing primitive to reuse) |
| Companion process lifecycle | **OpenWA runs as a sibling Docker container**, not embedded in `Litos.Api`'s process | OpenWA is a full Node.js/NestJS application (Chromium-per-session for the default engine) — fundamentally a different runtime from `Litos.Api`'s .NET process, not something to host in-process; `docker-compose.yml` gains a second service, matching how `ReadMe_HeadlessServiceTool.md` §3 already scoped Docker deployment as this project's packaging story |
| Multi-platform shape | **Implement `IChannelBridge` as already sketched** (`ReadMe_TelegramIntegrationTool.md` §5.2) | This document is the abstraction's first real second caller — confirms or corrects the sketch's assumptions (§8 below) rather than replacing it |
| Scope of this document | **Design/feasibility only**, matching both prior documents' pattern — no code written here | — |

## 5. Architecture

### 5.1 Two processes, one Docker network

```
┌─────────────────────────────┐         ┌──────────────────────────────┐
│  Litos.Api container        │         │  OpenWA container              │
│  (.NET / ASP.NET Core)      │         │  (Node.js / NestJS)             │
│                              │  REST   │                                │
│  WhatsAppBridge  ───────────┼────────►│  POST /api/sessions             │
│  (IChannelBridge)            │         │  GET  /api/sessions/{id}/qr     │
│                              │         │  POST .../messages/send-text    │
│                              │◄────────┼──  webhook (HMAC-signed)        │
│  POST /channels/whatsapp/    │         │      on inbound WhatsApp msg    │
│       webhook                │         │                                │
└─────────────────────────────┘         └──────────────────────────────┘
        both on one Docker Compose network, e.g. `litos-net` — Litos.Api's
        webhook route need never be reachable from the LAN, only from OpenWA
```

Unlike Telegram, which needs zero new infrastructure beyond a NuGet package (`Telegram.Bot`), this
integration's first cost is operational: standing up and keeping OpenWA itself running, healthy, and
upgraded — a second piece of software with its own release cadence, dependencies, and failure modes,
not something `Litos.Api` fully controls the way it controls its own in-process `TelegramBridge`.

### 5.2 Project shape: `Litos.Api/Channels/WhatsApp/`

Mirrors `ReadMe_TelegramIntegrationTool.md` §6.1's Telegram layout, sub-namespaced inside `Litos.Api`
rather than a separate project, for the identical reasoning given there (§0: `Litos.Api` is the only
caller, so a project boundary buys nothing):

```
src/Litos.Api/Channels/WhatsApp/
├── WhatsAppBridge.cs           # IChannelBridge — owns the OpenWA REST client + webhook registration
├── OpenWaClient.cs             # thin typed HttpClient wrapper: create session, get QR, send-text
├── WhatsAppWebhookEndpoint.cs  # POST /channels/whatsapp/webhook — HMAC verification, dispatch
├── WhatsAppSessionDriver.cs    # per-linked-chat: drives AgentLoop.RunTurnAsync (mirrors §6.3 of the
│                                #   Telegram doc almost verbatim — see §6 below for the deltas)
└── WhatsAppConfig.cs           # OpenWA base URL, API key, webhook secret; {state-dir}/whatsapp.json
```

New dependency: none beyond `System.Net.Http.Json` (already implicit in ASP.NET Core) — no NuGet
package is needed for OpenWA specifically, since its surface is plain REST/JSON and a hand-written
typed client (`OpenWaClient`) is small enough not to warrant pulling in a generated client, mirroring
how this codebase already prefers a thin wrapper over `HttpClient` for `WebSearchTool`'s Tavily calls
rather than a generated SDK.

### 5.3 `IChannelBridge` implementation, checked against the existing sketch

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

holds up for `WhatsAppBridge` with one adjustment worth naming rather than silently absorbing:

- `ChannelName => "whatsapp"` — used for `SessionOwner.WhatsApp` exactly as sketched.
- `StartAsync` — for Telegram this starts a polling loop; for WhatsApp it instead **registers**
  `Litos.Api`'s webhook URL with OpenWA (or confirms it's already registered) and ensures an OpenWA
  session exists/is connected. The actual message-receiving work happens passively, in
  `WhatsAppWebhookEndpoint`, not in a loop `StartAsync` owns — a real behavioral difference from
  Telegram's `StartAsync`, even though both satisfy the same interface signature. This confirms
  `ReadMe_TelegramIntegrationTool.md` §5.3's open question about extraction timing was right to defer:
  the interface itself needed no change, but a shared `ChannelSessionDriver` helper (if extracted)
  would need to accommodate "started via push registration" as well as "started via pull loop."
- `StopAsync` — deregisters the webhook (or simply stops honoring calls to it) and optionally tells
  OpenWA to disconnect the session.
- `BeginPairingAsync` — calls OpenWA's `GET /sessions/{id}/qr`, wraps the returned QR image bytes in
  the same `PairingHandle` shape `TelegramSetup.razor` consumes, so `WhatsAppSetup.razor` can render
  it with the same `<img src="data:image/png;base64,...">` pattern
  (`ReadMe_TelegramIntegrationTool.md` §6.2) — no new rendering code, just a different QR source
  (OpenWA-generated instead of `QRCoder`-generated).

## 6. Turn-driving: `WhatsAppSessionDriver`, deltas from `TelegramSessionDriver`

The turn-driving loop itself — resolve/create a `Transcript` under `SessionOwner.WhatsApp`, start or
steer a turn via `AgentLoop.RunTurnAsync`, translate `AgentEvent`s to outgoing messages — is the same
shape `ReadMe_TelegramIntegrationTool.md` §6.3 already designed and this document does not repeat it
in full. Deltas specific to WhatsApp/OpenWA:

1. **Trigger is a webhook POST, not a polled update.** `WhatsAppWebhookEndpoint` verifies the HMAC
   signature, extracts the sender's `chatId` (WhatsApp's `628123456789@c.us`-shaped identifier — the
   sender-identity analog to Telegram's numeric `chatId`, per §2) and message body, and hands off to
   `WhatsAppSessionDriver` the same way `TelegramBridge`'s receiver callback does for Telegram.
2. **Sending replies is a plain REST POST** (`send-text`), not an SDK call — `OpenWaClient.SendTextAsync(chatId, text)`
   wraps `POST /api/sessions/{sessionId}/messages/send-text`, called from the same
   `TextDelta`-buffering/coalescing logic §6.7 of the Telegram doc describes (WhatsApp has its own
   message-length and rate-limit ceiling — OpenWA's docs suggest "a few messages per minute" as
   sustainable, §2 — so the same buffer-until-message-boundary approach applies, tuned to a likely
   *lower* throughput ceiling than Telegram's ~1/second/chat).
3. **No native inline-keyboard equivalent confirmed.** Telegram's `/resume` picker (§6.6 of that doc)
   relies on Telegram's inline-keyboard `callback_query` mechanism. Whether OpenWA/WhatsApp expose an
   equivalent (WhatsApp Business does have interactive list/button messages in some API tiers) was
   **not confirmed by this document's research** — named explicitly in §9 as unresolved. Until
   confirmed, the safe fallback is the same one `ReadMe_HeadlessServiceTool.md` §8 uses for `/branch`
   (a plain numbered text list, reply with the number/id) rather than assuming picker parity with
   Telegram.
4. **No native typing indicator confirmed either** — §6.7's Telegram `sendChatAction("typing")` has
   no verified OpenWA equivalent in the README excerpt fetched for this document; also §9.
5. **Elevation gate and boundary-marking are unchanged.** Every message arriving via
   `WhatsAppSessionDriver` routes through `Litos.Api`'s `HttpApprovalGate` exactly like Telegram
   (§7 of that doc), and any attachment/URL-sourced content gets the same
   `<<<EXTERNAL_UNTRUSTED_CONTENT source="whatsapp_attachment:{id}">>>` boundary-marking
   `ReadMe_TelegramIntegrationTool.md` §10.3 designed — the `source` tag's prefix is the only thing
   that changes per channel.

## 7. Attachments and voice messages

`ReadMe_HeadlessServiceTool.md` §7 already designed attachment/voice parity generically across
"Telegram/WhatsApp" — its §7.1 (photos/documents via the existing `IAttachmentConverter`/
`AttachmentSource` pipeline) and §7.2 (voice messages transcribed via `IAudioTranscriber`/Whisper
before ever reaching the turn) apply to `WhatsAppSessionDriver` without modification. The one
OpenWA-specific detail: media referenced in an inbound webhook payload needs to be resolved to
actual bytes — the fetched README states media "is not automatically persisted... only explicitly
saved media uses backend storage," meaning `WhatsAppSessionDriver` likely needs an explicit
media-fetch call against OpenWA's API (exact endpoint not confirmed here, §9) rather than receiving
inline bytes in the webhook payload itself — treated as a `StreamSource` once downloaded, same as
Telegram's `file_id` download step.

## 8. Configuration & secrets

Following `LitosConfig`'s established pattern (`ReadMe_TelegramIntegrationTool.md` §8), a new
`whatsapp.json` under `Litos.Api`'s `/data` mount:

```json
{
  "openWaBaseUrl": "http://openwa:2785",
  "openWaApiKey": "...",
  "webhookSecret": "...",
  "linkedChats": [
    { "chatId": "628123456789@c.us", "sessionId": "a1b2c3...", "linkedAt": "2026-07-28T10:00:00Z", "elevated": false }
  ],
  "enabled": false
}
```

Two credentials, not one — OpenWA's own API key (authenticates `Litos.Api` → OpenWA REST calls) and
a webhook secret (authenticates OpenWA → `Litos.Api` webhook calls, verified via HMAC per §3) — a
structurally richer secret surface than Telegram's single bot token, inheriting the same plaintext-
JSON posture `ReadMe_TelegramIntegrationTool.md` §8/§9 already flagged as an open question, now with
one more secret sharing that same unresolved posture.

## 9. Open questions and unconfirmed details

Named explicitly because this document's OpenWA research came from its README and repo metadata
only, not its full source or a hands-on install — narrower verification than
`ReadMe_TelegramIntegrationTool.md` had available for `Telegram.Bot` (an official, thoroughly
documented SDK):

- **Exact webhook payload JSON schema** — the README names filterable fields (`sender`, `recipient`,
  `body`, `type`, `mentions`, `fromMe`, `hasMedia`, `isGroup`) but the fetched excerpt didn't show a
  complete example payload. Needed before `WhatsAppWebhookEndpoint`'s DTO can be written.
- **HMAC header name and signing algorithm** — README confirms HMAC signing exists, not the header
  name (`X-OpenWA-Signature`? something else?) or hash algorithm (SHA-256 presumed but unconfirmed).
- **Media retrieval endpoint** for downloading an attachment referenced in a webhook payload (§7) —
  not located in the fetched material.
- **Interactive list/button message support** for a `/resume`-style picker (§6 point 3) and
  **typing-indicator equivalent** (§6 point 4) — unconfirmed either way; treat as unavailable until
  verified against OpenWA's actual API reference rather than assumed present.
- **Account-ban risk is materially different from Telegram's bot-token model and should be surfaced
  to the user, not just engineering.** Both OpenWA engines (§2) are unofficial/reverse-engineered
  integrations against a personal or business WhatsApp number — WhatsApp's terms of service do not
  sanction this the way Telegram's official Bot API sanctions bots. This is a real-world risk
  (account restriction/ban) with no code-level mitigation; worth a plainly-worded warning in
  `WhatsAppSetup.razor` itself before a user links their personal number, not just a line in this
  document.
- **OpenWA version/stability** — not independently assessed here (e.g. release maturity, maintenance
  activity, issue backlog); worth a quick health check before depending on it, the same diligence
  any new third-party dependency should get regardless of how clean its README reads.
- **`IChannelBridge`/`ChannelSessionDriver` extraction timing** (`ReadMe_TelegramIntegrationTool.md`
  §5.3, §9) — this document is the "second platform" event that section said would force the
  question; per §5.3 above, the interface itself didn't need to change, but whether to now extract
  the shared turn-driving helper is ripe for revisiting, not resolved here.
- **Should OpenWA's own React dashboard be exposed at all**, or fully hidden behind `Litos.Api`
  (reachable only container-to-container, never published)? Given `Litos.Api`'s own admin UI is meant
  to be the single administration surface (§0 of the Telegram doc), the natural default is "OpenWA's
  dashboard is never published to the host," relying on `WhatsAppSetup.razor` for everything a user
  needs — stated as the likely answer, not fully designed here.

## 10. What this does *not* change

Consistent with both prior documents' additive posture: no changes to `AgentLoop`, `ITool`,
`ToolRegistry`, `Litos.Host`, `Litos.Tools`, `Litos.Persistence`, or `Litos.Gui`. `SessionOwner`
gains one more static value (`SessionOwner.WhatsApp`) the same trivial way `SessionOwner.Telegram`
did. Everything else is additive inside `Litos.Api`, plus one new sibling container in the Compose
deployment.
