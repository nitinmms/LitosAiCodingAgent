# LitosAiAgent — Email Integration Feasibility (via IMAP IDLE / MailKit)

Evaluates a third chat-platform bridge hosted inside `Litos.Api` (`ReadMe_HeadlessServiceTool.md`):
a mailbox listener that treats an email inbox as a chat channel — the user emails their agent, the
agent replies in the same thread — the same way `ReadMe_TelegramIntegrationTool.md` and
`ReadMe_WhatsAppIntegrationTool.md` already designed for Telegram and WhatsApp. Written before any
implementation; no code has changed as a result of this document. Builds on `ReadMe_AgentDesign.md`
(architecture), `ReadMe_HeadlessServiceTool.md` (the `Litos.Api` host this lives inside), and
`ReadMe_TelegramIntegrationTool.md` (§0's "`Litos.Api` is the sole home for messaging integrations"
decision and §5's `IChannelBridge` abstraction, sketched specifically so additional platforms would
slot in without a rework). This is the abstraction's **third** implementation — after Telegram and
WhatsApp — and, like the WhatsApp document did, this one calls out by name every place email
genuinely differs rather than assuming parity with either prior design.

## 1. What this is, in one sentence

A third `IChannelBridge` implementation inside `Litos.Api`, `EmailBridge`, that watches a dedicated
mailbox via IMAP IDLE (using the [MailKit](https://github.com/jstedfan/MailKit) .NET library),
feeds each inbound message into the same `AgentLoop` every other face already drives, and sends the
agent's reply back as a threaded email reply via SMTP — under its own `SessionOwner.Email`-scoped
session and the same `HttpApprovalGate`/elevation-gate trust model Telegram and WhatsApp use — set
up and monitored from a new `EmailSetup.razor` page on `Litos.Api`'s existing Blazor Server admin
UI.

## 2. What a .NET mailbox listener actually is, confirmed by inspection of MailKit

Fetched directly from MailKit's own documentation and repo (`github.com/jstefan/MailKit`), not
recalled from general knowledge about email protocols generally:

- **MailKit is the de facto standard .NET library for IMAP/POP3/SMTP** — MIT-licensed, actively
  maintained, built on the same author's `MimeKit` for message parsing/construction. There is no
  serious competing option for this in the .NET ecosystem the way `Telegram.Bot` is the obvious
  choice for Telegram; this is a one-library decision, not a menu of trade-offs like OpenWA's
  engine choice was for WhatsApp.
- **IMAP IDLE is a real push mechanism, not polling dressed up as one.** `ImapClient.Idle(CancellationToken)`
  (or `IdleAsync`) issues the IMAP `IDLE` command (RFC 2177) and blocks until the server pushes an
  unsolicited response (new message arrived, flags changed, etc.) or the idle period times out —
  the client then re-issues `IDLE` in a loop. This is a genuine long-lived connection held open to
  the mail server, structurally closer to Telegram's long-polling receiver than to WhatsApp's
  webhook-push model, but the connection is initiated *outbound* from `Litos.Api` in both cases —
  no inbound port is ever opened for email, unlike WhatsApp's webhook (§3 below).
- **Not every IMAP server supports IDLE.** `ImapClient.Capabilities.HasFlag(ImapCapabilities.Idle)`
  is how MailKit surfaces support after connecting; providers that don't support it (rare among
  major providers — Gmail, Outlook/Microsoft 365, and most self-hosted Dovecot/Cyrus setups all do)
  require falling back to periodic polling (`ImapClient.Inbox.Status(...)` on a timer, e.g. every
  60 seconds) instead. This fallback needs to exist in the design (§4.4) rather than being an
  unhandled edge case, since "which mail provider does the user actually use" is not something this
  document can assume.
- **Auth is provider-dependent, and modern providers increasingly require OAuth2, not a plain
  password.** Gmail and Microsoft 365 have both deprecated plain username/password ("basic auth")
  IMAP login for regular accounts — Gmail requires either an **app password** (if 2FA is enabled
  and "less secure app access" equivalents are unavailable, which is now the common case) or a full
  OAuth2 flow (`SaslMechanismOAuth2`, which MailKit supports natively); Microsoft 365 similarly
  requires OAuth2 (`XOAUTH2`) for most tenants since Basic Auth deprecation. A self-hosted mail
  server (Dovecot, generic IMAP) typically still accepts a plain app-specific password. This is a
  materially bigger credential-acquisition burden on the user than Telegram's single bot-token copy
  or WhatsApp's QR scan (§9).
- **Sending is a separate protocol and a separate connection** — `SmtpClient` (also MailKit), not
  part of IMAP at all. A reply needs its own SMTP credentials/connection, generally to the same
  provider (Gmail's SMTP, Microsoft 365's SMTP) but configured independently. Some providers unify
  this under one OAuth2 token usable for both IMAP and SMTP scopes; others don't.
- **Threading is a first-class MIME concept, not something Litos needs to invent.** RFC 5322 defines
  `Message-ID`, `In-Reply-To`, and `References` headers specifically so mail clients thread
  conversations. A reply that sets `In-Reply-To` to the inbound message's `Message-ID` (and appends
  it to `References`) threads correctly in Gmail, Outlook, Apple Mail, and every other mainstream
  client without any Litos-side thread-tracking logic — `MimeKit.MimeMessage` exposes all three
  headers directly as settable properties.
- **No sandboxed/local-only test option** the way Telegram has a free-to-create bot — testing this
  bridge requires either a real mailbox (a dedicated address, not the user's primary inbox, per
  §4.1) or a local test SMTP/IMAP server (e.g. a Dockerized Dovecot/GreenMail instance) for
  development, worth naming as a practical difference in how this gets built and verified.

## 3. The structural comparison: pull, like Telegram — but a held-open connection, unlike either

`ReadMe_TelegramIntegrationTool.md` §4 chose long-polling so `Litos.Api` never accepts an inbound
connection for the bridge itself. `ReadMe_WhatsAppIntegrationTool.md` §3 inverted that: OpenWA
pushes to `Litos.Api` via webhook, requiring a new inbound route. **Email lands in a third
position**: like Telegram, it never requires `Litos.Api` to expose anything new — `EmailBridge`
only ever makes outbound TLS connections (IMAP on port 993, SMTP on port 465/587) to the mail
provider's servers, so §5.5's admin-UI-only inbound-surface property from the Telegram document
holds here too, unlike WhatsApp's webhook route. But unlike Telegram's request/response-shaped
long-polling (`getUpdates` returns promptly, loop again), IMAP IDLE holds one TCP connection open
for extended periods (typically capped by the server at ~29 minutes per RFC 2177 recommendation,
requiring the client to re-issue `IDLE` before then) — closer to a persistent WebSocket in
connection lifecycle, even though the wire protocol is much older. Concretely, this means:

- `EmailBridge.StartAsync` establishes one `ImapClient` connection and enters an idle-loop task that
  runs for the lifetime of the bridge being enabled, re-issuing `IDLE` roughly every 20–25 minutes
  (leaving margin under the server's own timeout) and reconnecting on any dropped connection —
  structurally a `BackgroundService`-hosted loop much like `TelegramBridge`'s receiver, just with a
  held-open socket instead of a request/response cycle repeating.
- No HMAC verification, no webhook auth model (§3 of the WhatsApp document) — the trust boundary is
  entirely "does this IMAP/SMTP credential work," resolved once at connection time, not per-message.
- Network resilience matters more here than for either prior bridge: a dropped IDLE connection
  (transient network blip, server-side connection recycling) needs a reconnect-with-backoff loop,
  not a fatal error — MailKit's own samples document exactly this pattern (catch, wait, reconnect,
  re-`IDLE`) as expected steady-state operation, not an exceptional path.

## 4. Confirmed/proposed design decisions

Following the format `ReadMe_TelegramIntegrationTool.md` §3 and `ReadMe_WhatsAppIntegrationTool.md`
§4 established — decisions treated as settled for the rest of this document, distinguishing direct
carry-overs from what's newly decided here for email specifically:

| Decision | Choice | Rationale |
|---|---|---|
| Host process | **`Litos.Api` exclusively**, per Telegram doc §0 — no exception for email | Same reasoning as Telegram/WhatsApp: one place messaging integrations are configured, run, and administered |
| Mailbox connectivity | **Direct IMAP/SMTP via MailKit, in-process** — not a companion service | Unlike WhatsApp (no .NET protocol implementation exists, forcing a companion process, §2 of that doc), MailKit is a mature, first-party-quality .NET library — no reason to introduce OpenWA-style operational overhead when the protocol is directly implementable in-process |
| Transport | **IMAP IDLE**, polling fallback for servers without IDLE support (§2, §4.4) | IDLE is a genuine push mechanism and the modern standard; a periodic-poll fallback is needed for completeness but is not the primary design |
| Dedicated mailbox, not the user's primary inbox | **Confirmed: a separate address is required**, e.g. `agent@yourdomain.com` or a dedicated Gmail account, never the user's everyday personal/work inbox | An agent with IMAP access to a mailbox can read every email in it, and (per §7) any sender who can reach that address can attempt to prompt-inject the agent — sharing a primary inbox multiplies both the blast radius of a credential leak and the untrusted-sender surface for no benefit; this mirrors WhatsApp's implicit "don't link a number you can't afford to have banned" caution, made explicit here because email has no platform-level guardrail (no bot-vs-personal-account distinction the way Telegram has) forcing the separation itself |
| Session model | **Independent `SessionOwner.Email` session, one per correspondent (sender address)**, mirroring Telegram/WhatsApp's per-chat model | Same reasoning: keeps "what's in the agent's inbox" separate from any other face's session; a `SessionOwner` partition per distinct sender rather than one shared mailbox-wide session, so two different people emailing the agent don't see each other's conversation |
| Trust model | **Same elevation-gate design as Telegram/WhatsApp** (`ReadMe_TelegramIntegrationTool.md` §7) — read-only until a human approves elevation via `Approvals.razor` | Platform-agnostic reasoning carries over unchanged; if anything, email's sender-identity spoofability (§7 below) argues for *keeping*, not loosening, this gate |
| Linking/pairing mechanism | **Allowlist-based, not a scan/pair gesture** — an explicit list of approved sender addresses configured in `EmailSetup.razor`, since email has no native "link a device" primitive the way Telegram's `/start <code>` or WhatsApp's QR-linked-device flow do | Email addresses are trivially spoofable in the `From` header (§7) — a scan-to-link gesture would falsely imply a proof-of-possession guarantee email cannot provide. An explicit allowlist is honest about what's actually being trusted: "I configured this address as one I'll accept agent requests from," not "this address proved who it is" |
| Reply threading | **Set `In-Reply-To`/`References` on every reply**, using MimeKit's native header support (§2) | Free correctness win — the alternative (not threading) would make every agent reply look like a new, disconconnected email in the recipient's client, a materially worse experience for zero implementation savings |
| Attachments | **Reuse the existing `IAttachmentConverter` pipeline**, per `ReadMe_HeadlessServiceTool.md` §7 | Same reasoning as Telegram/WhatsApp — email attachments (PDFs, images, documents) map directly onto `StreamSource`, no new conversion logic |
| Multi-platform shape | **Implement `IChannelBridge` as already sketched** (`ReadMe_TelegramIntegrationTool.md` §5.2) | This is the abstraction's third caller — confirms the sketch generalizes across a request/response bot API (Telegram), a webhook-push gateway (WhatsApp), and a stateful protocol with a held-open connection (email) without needing to change (§5 below) |
| Scope of this document | **Design/feasibility only**, matching all three prior documents' pattern — no code written here | — |

## 5. `IChannelBridge` implementation, checked against the existing sketch

`ReadMe_TelegramIntegrationTool.md` §5.2's interface, already exercised once by WhatsApp
(`ReadMe_WhatsAppIntegrationTool.md` §5.3):

```csharp
public interface IChannelBridge
{
    string ChannelName { get; }
    Task StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
    Task<PairingHandle> BeginPairingAsync(CancellationToken ct);
}
```

holds up for `EmailBridge` with adjustments worth naming, same spirit as the WhatsApp document's
own §5.3 callouts:

- `ChannelName => "email"` — used for `SessionOwner.Email` exactly as sketched.
- `StartAsync` — connects the `ImapClient`, authenticates (§6.2), and starts the IDLE loop
  (§3) as a long-running background task tracked by a `CancellationTokenSource` the same way
  `TelegramBridge.StartAsync` starts its polling loop. Structurally the closest of the three bridges
  to Telegram's shape (a loop `StartAsync` itself owns, not a passive webhook endpoint like
  WhatsApp's).
- `StopAsync` — signals the idle loop's `CancellationToken`, closes the `ImapClient`/`SmtpClient`
  connections cleanly (`DisconnectAsync(quit: true)`).
- `BeginPairingAsync` — email has no QR/deep-link pairing gesture to proxy or generate (§4's
  allowlist decision). This method's contract is awkward for email specifically: rather than
  returning a scannable artifact, `EmailSetup.razor` doesn't call `BeginPairingAsync` for its normal
  flow at all — "pairing" is just adding an address to a config list and clicking Save. Named
  explicitly as a place the shared interface's assumption (every channel has a "begin pairing, get a
  visual artifact" step) doesn't fully fit — `EmailBridge` can implement it as a no-op returning an
  empty/informational `PairingHandle`, or the interface can grow an `IsPairingSupported` flag so
  `EmailSetup.razor` knows to render a plain form instead of a QR slot. This is the first concrete
  crack in the interface sketch across three implementations — flagged in §9 rather than resolved,
  consistent with `ReadMe_TelegramIntegrationTool.md` §5.3's original "sketch now, don't over-build"
  framing.

## 6. Architecture

### 6.1 Project shape: `Litos.Api/Channels/Email/`

Mirrors the Telegram/WhatsApp layout, sub-namespaced inside `Litos.Api` for the identical reasoning
given in both prior documents (§0: `Litos.Api` is the only caller, so a project boundary buys
nothing):

```
src/Litos.Api/Channels/Email/
├── EmailBridge.cs             # IChannelBridge — owns the ImapClient IDLE loop + SmtpClient sender
├── MailboxListener.cs         # the IDLE-loop task: connect, authenticate, idle, dispatch on new mail
├── EmailSessionDriver.cs      # per-correspondent: drives AgentLoop.RunTurnAsync (mirrors §6.3 of the
│                               #   Telegram doc — see §7 below for the deltas)
├── EmailReplyComposer.cs      # builds a MimeMessage reply: In-Reply-To/References, quoted original,
│                               #   plain-text + optional HTML body
└── EmailConfig.cs             # IMAP/SMTP host+creds, sender allowlist; {state-dir}/email.json (§8)
```

New dependency: `MailKit` (which pulls in `MimeKit` transitively) — one NuGet package, comparable in
weight to `Telegram.Bot`, added to `Litos.Api.csproj` only. No second process, unlike WhatsApp's
OpenWA sibling container.

### 6.2 Authentication: three tiers, not one

Unlike Telegram's single bot-token model, email auth genuinely branches by provider (§2), and this
needs to be designed rather than assumed uniform:

1. **App password (simplest, self-hosted or Gmail-with-app-password)**: plain IMAP/SMTP
   username+password, stored via `LitosConfig`'s existing pattern (§8). MailKit's
   `ImapClient.AuthenticateAsync(username, password)` — no special handling needed.
2. **OAuth2 (Gmail, Microsoft 365)**: MailKit's `SaslMechanismOAuth2` needs an access token acquired
   via an OAuth2 flow *outside* MailKit itself — MailKit authenticates with a token, it does not
   obtain one. This means `EmailSetup.razor` needs an OAuth2 authorization-code flow (Google/Microsoft
   identity platform, refresh-token storage, silent refresh before expiry) that has no equivalent
   complexity anywhere in the Telegram or WhatsApp designs — closer in shape to a "Sign in with
   Google" web flow than to pasting a bot token into a text box. This is the single largest new
   piece of engineering this document introduces relative to its two predecessors.
3. **Fallback guidance**: for users who don't want to build/click through an OAuth consent screen,
   documenting "create a dedicated Gmail account, enable 2FA, generate an app password" as the
   supported low-effort path (mirroring how Gmail itself still issues app passwords for exactly this
   IMAP-client use case) — the same posture as recommending a throwaway bot account, but requiring
   one extra manual step (app-password generation) Telegram's bot creation doesn't need.

`EmailSetup.razor` therefore needs two distinct credential-entry forms (plain password vs. OAuth2
"Connect Google/Microsoft account" button), not one — a genuine UI complexity increase over
`TelegramSetup.razor`'s single token field or `WhatsAppSetup.razor`'s QR display.

### 6.3 Lifecycle: started/stopped from `Litos.Api`'s Status page

Per §4's toggle-parity decision with Telegram/WhatsApp, `EmailBridge` is not started automatically
at container boot:

- `Status.razor` gains an "Enable Email" / "Disable Email" toggle, matching the existing Telegram
  toggle pattern (`ReadMe_TelegramIntegrationTool.md` §6.2).
- Turning it on resolves `EmailBridge` via DI and calls `StartAsync` (connect, authenticate, begin
  IDLE loop); turning it off calls `StopAsync` (graceful IMAP/SMTP disconnect).
- Toggle state persists in `email.json` (§8) under `Litos.Api`'s `/data` mount, off by default even
  once credentials are configured — identical reasoning to Telegram's "no surprise always-on remote
  channel" decision.
- `EmailSetup.razor` shows connection health (last successful IDLE check-in, last error) — more
  useful here than for Telegram/WhatsApp, since a silently-dead IDLE connection (network drop that
  didn't trigger a clean disconnect event) is a real failure mode worth surfacing in the admin UI
  rather than only in logs.

### 6.4 The allowlist and "pairing" flow, end to end

1. Authenticated admin opens `EmailSetup.razor`, enters mailbox credentials (§6.2), and separately
   maintains a list of **approved sender addresses** — the allowlist decision from §4. This can be a
   plain textbox of one-address-per-line, no QR/deep-link ceremony needed.
2. `EmailBridge`'s IDLE loop (once "Enable Email" is on) receives a new-message notification, fetches
   the message via IMAP, and checks the `From` header's address against the allowlist.
3. **Allowlisted sender**: routed to `EmailSessionDriver` (§7). **Not allowlisted**: the message is
   left unread/ignored — no auto-reply is sent to an unrecognized sender, deliberately, since
   auto-replying to spam/unknown senders is how mailboxes end up backscattering to forged addresses
   and attracting more junk mail; silently ignoring is the safer default (an admin can always widen
   the allowlist later if a legitimate sender was missed, visible via §6.3's admin UI showing "N
   messages seen from non-allowlisted senders" as a lightweight diagnostic, not a per-message alert).
4. There is no separate "confirmation reply" step the way Telegram's linking flow sends "Linked to
   Litos ✅" (§6.4 of that document) — adding an address to the allowlist takes effect on the next
   IDLE-detected message from that address, with no round-trip needed to confirm linking succeeded.

### 6.5 Session identity: `SessionOwner.Email`

```csharp
// src/Litos.Agent/Session/SessionOwner.cs — one new static value, no structural change
public static SessionOwner Email { get; } = new("email");
```

Sessions created by `EmailSessionDriver` use `SessionOwner.Email`, landing under
`{sessions-root}/email/{sessionId}.jsonl` via the existing `JsonlTranscriptStore` path convention.
**Unlike Telegram/WhatsApp's `chatId`-per-device model, email's natural session key is the
correspondent's address** — `sender@example.com → sessionId`, one entry per distinct sender in
`email.json`'s persisted state (§8), mirroring the `chatId → sessionId` map both prior documents use
but keyed on an email address instead of a platform-specific chat identifier.

A second, email-specific question the other two channels don't have: **does a new email thread
(different `Subject`, no `In-Reply-To` header) start a new session, or continue the sender's current
one?** Proposed default: a message with no `In-Reply-To`/`References` header pointing at a prior
agent reply starts a **fresh session** for that sender (closer to email's own mental model — a new
subject line is a new conversation) — while a reply within an existing thread continues that
thread's session. This is a genuine design choice unique to email (§9), since Telegram/WhatsApp have
no equivalent "did the user start a new conversation" signal beyond an explicit `/new` command.

### 6.6 Turn-driving: `EmailSessionDriver`, deltas from `TelegramSessionDriver`

The turn-driving loop itself — resolve/create a `Transcript` under a channel-specific
`SessionOwner`, drive `AgentLoop.RunTurnAsync`, translate `AgentEvent`s to an outgoing reply — is the
same shape `ReadMe_TelegramIntegrationTool.md` §6.3 already designed and repeated for WhatsApp
(`ReadMe_WhatsAppIntegrationTool.md` §6); this document does not repeat it in full. Deltas specific
to email:

1. **No mid-turn steering in the same sense.** Telegram/WhatsApp's steering-channel reuse (writing a
   new inbound message into an in-flight turn's `ChannelWriter<SteeringMessage>`) assumes a chat
   medium where a second message can plausibly arrive while the first is still "being read." Email
   has no equivalent expectation — a sender writes one email, waits for a reply, then writes another;
   there is no realistic scenario of a second email arriving mid-turn that should interrupt the
   first, the way a chat app's rapid back-and-forth naturally does. **Proposed: queue, don't steer**
   — if a second email from the same sender arrives while a turn is running for that sender's
   session, it's queued and processed as the *next* turn once the current one completes, rather than
   injected as a steering message into the live turn. This is a genuine behavioral divergence from
   both prior bridges, motivated by the medium's own conventions rather than a technical limitation.
2. **No streaming/typing-indicator equivalent at all.** Telegram has `sendChatAction("typing")`;
   WhatsApp's equivalent was unconfirmed (`ReadMe_WhatsAppIntegrationTool.md` §9); email has no
   concept of a live status signal whatsoever — the entire `TextDelta` stream is buffered until the
   turn completes, and exactly one email is sent as the reply (or a small number, if response length
   requires splitting, though email has no character cap remotely close to Telegram's 4096, so
   splitting is essentially never needed in practice).
3. **Reply composition, not "send-text".** `EmailReplyComposer` builds a full `MimeMessage`: `Subject`
   is the inbound subject prefixed with `Re: ` (if not already present), `In-Reply-To`/`References`
   set per §4's threading decision, body is the agent's full response as plain text (with an optional
   HTML multipart alternative for basic formatting — code blocks, lists — mirroring how the agent's
   Markdown output already renders in `Litos.Gui`/`Litos.Console`). `ToolCallCompleted` events are
   **not** rendered inline the way Telegram's `🔧 read_file src/Foo.cs` status lines are (§6.3 of that
   doc) — email is not a live-updating medium, so intermediate status has no useful place to appear;
   only the final response text is sent.
4. **Elevation gate and boundary-marking are unchanged in mechanism, but the untrusted-content
   surface is larger by default.** Every message arriving via `EmailSessionDriver` routes through
   `Litos.Api`'s `HttpApprovalGate` exactly like Telegram/WhatsApp (§7 of the Telegram document), and
   any attachment gets the same boundary-marking convention
   (`<<<EXTERNAL_UNTRUSTED_CONTENT source="email_attachment:{filename}">>>`, §10.3 of that document)
   — but see §7 below for why the *body* of the email itself, not just its attachments, deserves the
   same treatment here in a way it didn't need separate emphasis for Telegram/WhatsApp.

## 7. Trust & security model — sender-identity spoofing is the headline new risk

**This is the section that differs most from both prior documents, and is worth being direct
about**: email's `From` header is trivially forgeable. Unlike Telegram (a `chatId` is bound to a
real, authenticated Telegram account by Telegram's own backend) or WhatsApp (a `chatId` is bound to
a phone number that went through WhatsApp's own device-linking flow), **nothing in the base SMTP/IMAP
protocol cryptographically proves who sent an email**. This directly undermines §4's allowlist
design unless addressed:

- **SPF/DKIM/DMARC exist precisely to address this, and should gate allowlist matching, not just the
  raw `From` address.** A receiving mail server (Gmail, Microsoft 365, etc.) typically already
  performs SPF/DKIM/DMARC validation and exposes the result via `Authentication-Results` headers on
  the delivered message — `EmailSessionDriver` should check that header (or, more robustly, verify
  DKIM signatures directly via a library capable of it) and treat a message whose sender **passes**
  DMARC alignment differently from one that merely claims to be from an allowlisted address but
  fails or lacks authentication. **Concretely: an allowlist match on `From` alone, without checking
  the mailbox provider's own spam/authentication verdict, is not a meaningfully enforceable trust
  boundary** — this is the one place this document's initial allowlist design (§4) needs a caveat
  spelled out explicitly rather than glossed over the way a first draft might.
- **Practical mitigation for v1, stated plainly as a v1-acceptable compromise, not a full fix**:
  most mail providers already reject or flag obviously-spoofed mail before it reaches the inbox at
  all (Gmail marks failed-DMARC mail, often routing it to spam rather than inbox) — relying on the
  provider's own filtering as a first line of defense, combined with keeping the elevation gate (§4)
  firmly in place so even a successfully-spoofed allowlisted sender can't get standing tool access
  without a human approving it, is a reasonable v1 posture. A more rigorous v1+ would have
  `EmailSessionDriver` itself parse and require a passing DKIM/SPF/DMARC verdict before treating a
  message as coming from an allowlisted address at all, rather than trusting the provider's default
  inbox placement as an implicit signal.
- **The email body itself is now squarely "untrusted external content"** in a way that's more acute
  than for Telegram/WhatsApp: a chat message is typed by a real-time human participant in a
  conversation; an email can be composed by anyone, forwarded, auto-generated by another system, or
  (per the point above) sent from a spoofed address. `ReadMe_TelegramIntegrationTool.md` §10.3's
  boundary-marking convention was designed for *tool results and attachments*, not the primary user
  message itself, on the reasoning that a live chat sender is presumed to be a real, present human.
  Email's weaker sender-identity guarantee argues for wrapping the **body of every inbound email**
  in the same `<<<EXTERNAL_UNTRUSTED_CONTENT source="email_body:{message-id}">>>` marker, not just
  its attachments — a genuinely new recommendation this document makes beyond what either prior
  bridge needed, and worth flagging as a deliberate strengthening rather than an oversight carried
  over unchanged.
- **The elevation gate (`ReadMe_TelegramIntegrationTool.md` §7) applies unchanged and matters more
  here, not less.** A newly-allowlisted email address lands read-only, exactly like a newly-linked
  Telegram chat, until a human explicitly elevates it via `Approvals.razor`. Given the weaker
  identity guarantee above, this document does **not** propose any relaxation of that gate for
  email — if anything, an admin approving elevation for an email correspondent should see a clearer
  warning than Telegram's card does, noting that email sender identity is not independently verified
  by this bridge in v1 (per the caveat above).
- **Unlinking/revocation**: removing an address from the allowlist (`EmailSetup.razor`) immediately
  stops new messages from that sender reaching `AgentLoop` — the same "remove linked device" pattern
  §7 of the Telegram document specifies, applied to a list entry instead of a `chatId` record.

### 7.1 Hardening for v1: six layers, not one control

**Confirmed decision, resolving what §9 of this document's first draft left as an open scope
question ("trust the provider's filtering, or verify independently?").** No single control below is
sufficient alone — that's the point of stating this as layers rather than picking one silver bullet.
Ordered roughly by how much protection each buys:

1. **Verify sender identity cryptographically — don't trust the `From` header, and don't trust the
   receiving server's `Authentication-Results` header blindly either.** The original v1 sketch (§7
   above) proposed reading `Authentication-Results`, but that header is written by *whichever* server
   last handled the message before it reached the mailbox `EmailBridge` polls — a message delivered
   by a path that skips or forges that header is indistinguishable from a genuinely-authenticated one
   if `EmailSessionDriver` only reads it at face value. **Resolved for v1: `EmailSessionDriver`
   independently verifies the DKIM signature on every inbound message** (a small, focused DKIM
   verification step — parsing the `DKIM-Signature` header, fetching the signer's public key from
   DNS via a TXT lookup, and checking the signature — is a well-bounded piece of code, not a
   dependency on trusting an intermediate server's say-so) and additionally checks SPF/DMARC
   alignment where available. A message that **fails or lacks** DKIM/SPF/DMARC verification is
   treated identically to a message from a non-allowlisted sender (§6.4's silent-ignore path) —
   **allowlist match alone is never sufficient to reach `AgentLoop`.**
2. **Keep the allowlist closed, narrow, and operationally treated as a credential.** Default-deny is
   already the design (§4, §6.4) — the discipline layered on top is operational, not technical: add
   addresses deliberately, review the list periodically from `EmailSetup.razor`, and never widen it
   reactively the moment a legitimate email gets silently dropped without first checking *why* it
   failed verification (layer 1) rather than assuming it's a false positive.
3. **Never let email traffic acquire standing tool access implicitly.** The elevation gate
   (`ReadMe_TelegramIntegrationTool.md` §7) is the load-bearing control for email specifically,
   more so than for Telegram/WhatsApp, precisely because identity is structurally weaker even after
   layer 1. Every newly-allowlisted address starts read-only; the `Approvals.razor` card for an email
   elevation request states plainly that identity was DKIM/SPF/DMARC-verified (or wasn't, if the
   admin is choosing to elevate a sender despite a failed/missing verdict) — a human approves an
   address with a stated confidence level, not an anonymous claim.
4. **Boundary-mark the email body itself, not just attachments** — already the §7 recommendation
   above, restated here as part of the layered picture: wrap the inbound body in
   `<<<EXTERNAL_UNTRUSTED_CONTENT source="email_body:{message-id}">>>` regardless of DKIM outcome,
   since even a cryptographically-verified sender's own account could be compromised or could
   forward/paste adversarial content from elsewhere. Verification (layer 1) establishes *who sent
   it*; boundary-marking protects against *what's inside it* regardless of who sent it — different
   threats, both real, neither substitutes for the other.
5. **Keep the pre-elevation tool deny-list in place.** Same coarse `ShellTool`/`WriteFileTool`/
   `EditFileTool` block Telegram uses pre-elevation (`ReadMe_TelegramIntegrationTool.md` §7) applies
   to `SessionOwner.Email` turns identically — a read-only default means even a message that somehow
   clears layers 1–2 can query and search but cannot act, until layer 3's human approval is given.
6. **Rate-limit per sender and alert on verification-status changes, not just volume.** A cap on
   turns-per-sender-per-hour bounds the cost/abuse blast radius of any single compromised or
   misconfigured allowlist entry. More valuable than a raw volume cap: **alert when a previously
   DKIM-passing allowlisted sender suddenly starts failing verification** — a passing-to-failing
   transition on an already-trusted address is a stronger spoofing-attempt signal than either a
   static "N rejected messages today" counter (§6.4, still worth keeping as a coarse diagnostic) or
   volume alone, and is the kind of anomaly worth surfacing prominently on `EmailSetup.razor` rather
   than only in logs.

**What this does and doesn't achieve, stated with the same plainness §7 above uses for the
elevation gate generally**: layers 1–2 raise the cost of impersonating an allowlisted sender from
"trivial" (forge a `From` header) to "requires compromising or spoofing DKIM for that domain," which
is a materially higher bar but not an absolute one (a sender's own mailbox or DNS could still be
compromised). Layers 3–5 assume layer 1 can still fail and bound the damage when it does. Layer 6
shortens the detection window when it does fail. None of the six is a claim that email reaches
Telegram/WhatsApp's identity-guarantee strength — it's a claim that the *gap* is deliberately
compensated for by depth of control rather than left as a single, unqualified allowlist check.

## 8. Configuration & secrets

Following `LitosConfig`'s established pattern (`ReadMe_TelegramIntegrationTool.md` §8,
`ReadMe_WhatsAppIntegrationTool.md` §8), a new `email.json` under `Litos.Api`'s `/data` mount:

```json
{
  "imapHost": "imap.gmail.com",
  "imapPort": 993,
  "smtpHost": "smtp.gmail.com",
  "smtpPort": 587,
  "authMode": "oauth2",
  "username": "agent@yourdomain.com",
  "oauthRefreshToken": "...",
  "allowedSenders": [
    {
      "address": "you@example.com",
      "sessionId": "a1b2c3...",
      "elevated": false,
      "linkedAt": "2026-07-28T10:00:00Z",
      "lastVerification": "pass",
      "lastVerifiedAt": "2026-07-28T10:00:00Z"
    }
  ],
  "enabled": false
}
```

`lastVerification` (`pass` / `fail` / `none`) and `lastVerifiedAt` are new per-sender fields
supporting §7.1 layer 6 — persisting the last known DKIM/SPF/DMARC verdict per allowlisted address is
what makes a pass-to-fail transition detectable across restarts, not just within one running
process's memory.

For the plain-app-password auth mode (§6.2), `username`/`password` follow `LitosConfig`'s
env-var-first-then-file resolution exactly like Telegram's bot token
(`config.GetApiKey("email")`-equivalent); for OAuth2 mode, the refresh token is the long-lived
secret (access tokens are short-lived and refreshed at connect time, not stored) — a materially
different secret shape than either prior bridge, and one more reason (alongside §6.2's UI
complexity) that OAuth2 support is the single largest new engineering surface this document
introduces. Same plaintext-JSON-at-rest posture already flagged as an open, unresolved question in
both prior documents (`ReadMe_TelegramIntegrationTool.md` §8/§9) — inherited here unchanged, now
covering an IMAP/SMTP credential and (in OAuth2 mode) a refresh token capable of ongoing mailbox
access, arguably a higher-value target than a bot token since a compromised mailbox credential often
grants broader account recovery/reset capability across other services tied to that email address.

## 9. Open questions and unconfirmed details

- **`IChannelBridge.BeginPairingAsync`'s fit for allowlist-based channels** (§5) — the first concrete
  case across three implementations where the shared interface's "pairing produces a visual
  artifact" assumption doesn't hold. Worth revisiting once a fourth channel is considered, per
  `ReadMe_TelegramIntegrationTool.md` §5.3/§9's already-deferred extraction-timing question.
- ~~**DKIM/SPF/DMARC verification depth**~~ — **resolved**, not still open: §7.1 settles this as
  independent DKIM verification inside `EmailSessionDriver` (layer 1), not reliance on the receiving
  provider's `Authentication-Results` header alone. What remains genuinely open is the *implementation*
  choice of DKIM-verification library/approach in .NET — not located or evaluated in this document —
  and whether SPF/DMARC alignment checks are implemented as a full RFC 7489 evaluation or a lighter
  heuristic for v1.
- **New-thread-vs-continuing-session heuristic** (§6.5) — whether "no `In-Reply-To` header" is a
  reliable enough signal for "start a new session," or whether some mail clients omit/mangle these
  headers often enough to need a fallback (e.g. matching on `Subject` similarity) — untested
  assumption, flagged rather than assumed solid.
- **OAuth2 token-refresh failure handling** (§6.2, §8) — what `EmailBridge` does when a refresh token
  is revoked or expires (Google/Microsoft both allow this) — presumably surfaces as a connection
  error on `EmailSetup.razor`'s health display (§6.3), requiring the admin to re-authorize, but the
  exact UX isn't designed here.
- **Queueing semantics for concurrent messages from the same sender** (§6.6 point 1) — "queue, don't
  steer" is proposed but not stress-tested against, e.g., a sender who replies twice in quick
  succession before the first reply is sent; whether the second queued message should be merged into
  one turn or processed as two sequential turns is unresolved.
- **HTML email parsing on the inbound side** — this document assumes plain-text extraction from
  inbound messages (`MimeMessage.TextBody`), but a real-world sender's email client may send
  HTML-only bodies with rich formatting/quoted-reply chains; whether `EmailSessionDriver` needs to
  strip quoted history (so the agent doesn't re-process the entire thread's prior text on every
  reply) is a real parsing question, not addressed in detail here — MimeKit's `TextBody`/`HtmlBody`
  properties provide the raw material, but quote-stripping heuristics are a separate, unsolved
  problem in general (every mail client quotes slightly differently).
- **Whether email even ships before WhatsApp**, given §6.2's OAuth2 complexity is a larger new-build
  surface than anything WhatsApp's document identified — a build-sequencing/prioritization call, not
  an architecture question, and explicitly not decided in this document.
- **`IChannelBridge`/`ChannelSessionDriver` extraction timing**
  (`ReadMe_TelegramIntegrationTool.md` §5.3, §9; revisited in `ReadMe_WhatsAppIntegrationTool.md` §9)
  — now with three implementations in hand (Telegram, WhatsApp, email), this is arguably the natural
  point to actually extract the shared turn-driving helper rather than defer again — noted, not done
  here.

## 10. What this does *not* change

Consistent with all three documents' additive posture: no changes to `AgentLoop`, `ITool`,
`ToolRegistry`, `Litos.Host`, `Litos.Tools`, `Litos.Persistence`, or `Litos.Gui`. `SessionOwner`
gains one more static value (`SessionOwner.Email`) the same trivial way `SessionOwner.Telegram` and
`SessionOwner.WhatsApp` did. Everything else is additive inside `Litos.Api` — one new NuGet package
(`MailKit`), no new container, no new externally-hosted companion service.
