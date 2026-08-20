# Litos in VS Code — Design & Status

A fourth face: a VS Code extension (`src/Litos.VsCode`, TypeScript) backed by a new minimal local
.NET host (`src/Litos.VsCodeHost`), giving a chat UI inside a VS Code panel without asking the user
to stand up Docker, Postgres, or any other infrastructure `Litos.Api` requires.

**Status: working end-to-end, Windows only.** Chat, API-key first-run, MCP-aware approval gating,
file sharing with clickable links, multiple concurrent independent sessions sharing one host
process, and full slash-command parity with `Litos.Gui` (`/new`, `/resume`, `/provider`, `/model`,
`/skills`, `/skill`, `/attach` + clipboard paste-to-attach, `/branch`, `/compact`, `/reflect`,
`/mcp`) are all built and verified — backend endpoints against the real self-contained `win-x64`
binary via live integration smoke tests, `AgentWorker`'s new provider/model-switching surface and
every new `LitosClient` method's request construction via unit tests (31 `.NET` + 47 TS, all
passing), and the webview itself confirmed working end-to-end inside a real VS Code Extension
Development Host window (F5) — this manual pass caught and fixed one real bug not visible at the
unit-test layer (see §7.6). See §7 for the command-by-command trace this was scoped from and the
design decisions made along the way.

## 1. Why not just run `Litos.Api`

`Litos.Api` was evaluated first and rejected as the backend for this face: `Program.cs` hard-requires
`POSTGRES_CONNECTION_STRING` at startup (throws if unset) and gates every endpoint behind JWT/cookie
auth built around a per-user `UserStore` — real infrastructure for what should be a single-command,
single-user install. Retrofitting a "local mode" into `Litos.Api` would mean fighting assumptions
that project is correctly built around (multi-tenant accounts, Telegram, admin auth), not removing
a small optional layer.

## 2. Why not just launch `Litos.Console` in a VS Code terminal

This was the first design considered and is still the simplest option if the goal is purely
"see the existing Terminal.Gui UI without leaving VS Code" — `Litos.Console` already runs in any
real terminal, including VS Code's integrated one, via `vscode.window.createTerminal`, with zero new
code. It was set aside in favor of a real extension because a terminal-embed can't see VS Code
workspace state (open file, selection, diagnostics) and can't share a running session with another
face the way an HTTP-backed extension can. That tradeoff — real UI-rendering work in exchange for
VS Code-native context — is the one this document's approach commits to.

## 3. Architecture

```
VS Code (Node/TypeScript, Extension Host)          Local .NET process (loopback only)
┌─────────────────────────────┐                    ┌──────────────────────────────┐
│ src/Litos.VsCode              │                    │ src/Litos.VsCodeHost           │
│  extension.ts    — activation, │  spawn + stdout    │  Program.cs — AddLitosAgent,   │
│                    child-proc  │◄──── port ─────────│    port-0 bind, stdout         │
│                    lifecycle   │      handshake      │    handshake                  │
│  hostProcess.ts  — spawn/find  │                    │  AgentWorker.cs — trimmed copy │
│                    binary, │                        │    of Litos.Api's own          │
│                    parse handshake                   │    (no attachment queueing)   │
│  agentEvents.ts  — SSE client,│  HTTP/SSE loopback  │  Turns/TurnsEndpoints.cs —     │
│                    ported from │◄──────────────────►│    trimmed copy, no auth,      │
│                    AngularChat │                     │    SessionOwner.Local fixed    │
│                    example     │                     │  AutoApprovalGate.cs — copy    │
│  webviewContent.ts — styled   │                     │    of Litos.Api's, zero-dep    │
│                    HTML/CSS    │                     │                                │
│                    transcript  │                     │  ProjectReference: Litos.Host  │
└─────────────────────────────┘                     │    only (no Postgres/JWT/       │
                                                       │    Telegram/EF Core)           │
                                                       └──────────────────────────────┘
```

The .NET side reuses `Litos.Host.AddLitosAgent` — the exact same composition root
`Litos.Console`/`Litos.Gui`/`Litos.Api` all build on — so `Litos.Agent`, `Litos.Tools`, and every
provider are completely unmodified. The only new .NET code is: a stripped `Program.cs` (no
Postgres/JWT/CORS/Telegram/Blazor-admin — see `Litos.Api`'s `Program.cs` for everything dropped), a
trimmed `AgentWorker`/`TurnsEndpoints` (no per-user auth, no attachment queueing, no MCP-approval
gating decorator), and a reused `AutoApprovalGate` (zero dependencies, copied verbatim).

**This is a structurally different integration shape than the other three faces.** Console/Gui/Api
all link `Litos.Host` *in-process*. This face can't — a Node extension host cannot reference .NET
assemblies — so it spawns `Litos.VsCodeHost` as a **child process** and talks to it over **loopback
HTTP/SSE**, architecturally closer to the external-client examples (`src/Litos.Api/examples/
AngularChat`, `BlazorChat`) than to an in-process face. This was a deliberate choice, not an
oversight; see §4 for the transport contract this implies.

## 4. Transport contract

### 4.1 Startup handshake

`Litos.VsCodeHost` binds Kestrel to `http://127.0.0.1:0` (OS-assigned free port), then — only after
`app.StartAsync()` has actually opened the listener — writes one line of JSON to stdout:

```json
{"port":54321}
```

**The extension must scan stdout for the first line that parses as this shape, not assume it's
line 1** — Kestrel's own `Microsoft.Hosting.Lifetime` startup logs ("Now listening on...",
"Application started...") also go to stdout ahead of the handshake line. `hostProcess.ts` does this
scan already; this is the one real gotcha found during integration testing.

### 4.2 Endpoints (today)

Same shape as `Litos.Api`'s turns endpoints, minus auth and attachments:

| Endpoint | Behavior |
|---|---|
| `GET /sessions` | Lists sessions for `SessionOwner.Local` (shared on-disk storage with Console/Gui — see §5). |
| `GET /sessions/{id}/history` | Replays a session's prior messages. |
| `POST /sessions/{id}/turns` | JSON body `{"input": "..."}` only (no multipart/attachments yet). Returns `202 Accepted` (steered) or an SSE stream (`event: agent-event`, `data: <json>`) of `AgentEvent`s. |

### 4.3 Wire format has no type discriminator

Each `AgentEvent` record (`src/Litos.Agent/Streaming/AgentEvent.cs`) serializes by its own
PascalCase properties with no `"$type"` field. `TextDelta` and `ReasoningDelta` are genuinely
byte-for-byte identical on the wire (`{"Text":"..."}` for both) — this is an existing wart in
`Litos.Api`'s own wire format, not something introduced here, and not fixed here (would require
touching `TurnsEndpoints.cs`/`AgentEvent.cs`, out of scope for an additive face).
`src/Litos.VsCode/src/agentEvents.ts`'s `parseAgentEvent` classifies events by which properties are
present, in the same priority order `src/Litos.Api/examples/AngularChat/app.js`'s
`parseAgentEvent` already established: `Result` → `Arguments` → (`Reason`+`CallId`) →
(`ToolName`+`CallId`) → (`Message`+`Usage`) → `Exception` → `TokensBefore` → else `Text`.

### 4.4 Shutdown

**v1 has no graceful-shutdown handshake.** `extension.ts` calls `hostProcess.stop()` (a direct
`child_process.kill()`) on panel disposal and extension deactivation. No `/shutdown` endpoint, no
stdin-close signal. Acceptable for now; revisit if orphaned host processes become a real problem in
practice (deliberately deferred, not an oversight — see conversation history).

## 5. Session storage and concurrency model

`SessionOwner.Local` is hardcoded everywhere in `Litos.VsCodeHost` (no `ClaimsPrincipal`, no
per-caller identity) — the same fixed bucket `Litos.Console`/`Litos.Gui` already use. Because
`JsonlTranscriptStore` persists to `%USERPROFILE%\.litos\sessions\local\...` regardless of which
face wrote it, **sessions are already shared across Console, Gui, and this extension** — confirmed
during integration testing (`GET /sessions` against a freshly-spawned host returned 190+ real prior
sessions created by other faces on this machine).

**One `Litos.VsCodeHost` process is shared by every chat panel opened within one extension
activation** (= one VS Code window, since activation is per-window) — not one process per panel.
`extension.ts`'s module-level `sharedHost` is lazily spawned on the first `Litos: Open Chat` and
reused by every subsequent panel; each panel is an independent *session* (own `sessionId`, own
transcript) riding the same process, matching `AgentWorker`'s existing per-`(SessionOwner,
sessionId)` concurrency (needed zero backend changes — confirmed via a live smoke test running two
concurrent sessions against one host with no cross-talk). This mirrors the shared-server/
many-sessions shape confirmed in real prior art in this space (OpenCode's server/session
architecture, per its own docs — one HTTP server, many concurrent SSE-streamed sessions, no
built-in file-locking between them either) rather than spawning a redundant ~75MB process per
open panel, which an earlier version of `extension.ts` did.

**Fixed bug**: that earlier version reassigned a single `hostProcess` module variable on every
`openChatPanel` call, so any panel's `onDidDispose` would `stop()` whichever process happened to be
"current" — closing one panel could silently kill a *different* panel's still-open session. Now
`sharedHost` is only torn down in `deactivate()` (the whole extension/window shutting down);
closing one panel just removes it from the tracked `openPanels` set.

**`cwd` is captured once**, at whichever moment the shared host first gets spawned (the first panel
opened in that window) — `vscode.workspace.workspaceFolders[0]`, so a later panel reuses the
already-running host's working directory even if the open workspace changes afterward. Picking up a
workspace-folder change without restarting the whole host is out of scope (§8 non-goals).

**Same working directory, concurrent sessions — no file-level coordination.** Two sessions can
target the same workspace folder at once (this is the common case, not an edge case — most users
will only ever have one folder open). `Litos.Tools`' `write_file`/`edit_file`/`shell` have no
cross-session locking (confirmed by reading their source — plain `File.ReadAllText`/
`WriteAllText`), so two sessions concurrently touching the *same file* behave like two independent
editors: `write_file` can silently lose one session's change to the other's overwrite (last write
wins), while `edit_file`'s anchor-based find/replace at least fails loudly (anchor text no longer
matches) rather than corrupting anything. This is an inherited property of `Litos.Tools`, not
introduced by this face — the same risk already exists if a user ran two `Litos.Gui`/`Litos.Console`
instances against one folder — and matches what OpenCode's own docs say too (file-locking between
concurrent sessions is explicitly unspecified/absent there as well).

## 6. Packaging

`Litos.VsCodeHost.csproj` carries the same self-contained/single-file publish block as
`Litos.Console.csproj` (`SelfContained`, `PublishSingleFile`, `InvariantGlobalization`, etc.),
conditioned on `RuntimeIdentifier` being set — so `dotnet publish -c Release -r <rid> --self-contained
true -p:PublishSingleFile=true -o src/Litos.VsCode/bin/<rid>` produces one ~75MB single-file exe per
platform with no separate .NET install required by the end user. `src/Litos.VsCode/src/
hostProcess.ts`'s `resolveRid()` maps `process.platform`/`os.arch()` to the matching folder
(`win-x64`, `osx-x64`, `osx-arm64`, `linux-x64`, `linux-arm64`) at activation time.

**Only `win-x64` has been published and tested so far.** Per-platform publish scripts now exist for
all three OSes — `deploy/publish-vscodehost-windows.ps1`, `deploy/publish-vscodehost-linux.sh`,
`deploy/publish-vscodehost-macos.sh` — mirroring the now-shelved `Litos.Console`'s own scripts
(`deploy/publish-console-*`), since `Litos.VsCodeHost` is the same shape (a plain headless binary,
not a windowed `.app` bundle like `Litos.Gui`'s `deploy/publish-macos.sh`) — that reference remains
correct even though `Litos.Console` itself is shelved as a product. None have been run for
osx-arm64/osx-x64/linux-x64/linux-arm64 yet; only Windows has an actual bundled+tested binary.
A `release-vscode.yml` CI workflow (mirroring `release-console.yml`'s multi-RID publish-and-
checksum pattern) still needs writing to produce and bundle all five RIDs into every release.

**macOS code signing is required, not optional, for this specific binary.** Per
`publish-vscodehost-macos.sh`'s own header comment: Gatekeeper quarantines any downloaded, unsigned
executable and blocks first-run — but since the extension spawns this binary *silently* as a child
process (never a user double-click or terminal launch the way `Litos.Console`/`Litos.Gui` are),
there is no natural moment for the user to see Gatekeeper's "Open anyway" dialog and clear it
themselves the way they could for a directly-launched app. An unsigned build fails opaquely on the
very first `Litos: Open Chat` activation. The script supports the same
`APPLE_SIGN_IDENTITY`/`APPLE_ID`/`APPLE_TEAM_ID`/`APPLE_APP_PASSWORD` codesign+notarize+staple
pipeline `Litos.Console`/`Litos.Gui`'s own macOS scripts already use — confirmed reusable, same
Apple Developer credentials. Not yet run for a real signed build; `APPLE_SIGN_IDENTITY` presence is
what gates signing (absent = the script publishes an explicitly-labeled unsigned build and warns
loudly not to ship it).

**Executable-bit preservation across platforms**: `hostProcess.ts`'s `start()` defensively
`chmod`s the resolved binary to `0o755` on every launch on non-Windows platforms, rather than
trusting the `.vsix` packaging step to preserve the executable bit end-to-end — packaging into a
zip (which is what a `.vsix` is) and re-extracting on install is a known way to lose that bit, not
something the RID-bundling step alone guarantees.

The extension itself has never been packaged as a `.vsix` — it has only been run via VS Code's
Extension Development Host (`F5` with `src/Litos.VsCode` open as the workspace root, using the
`.vscode/launch.json`/`tasks.json` checked in there). See §11 for what a real Marketplace release
(Windows + macOS + Linux) requires from here.

## 7. Slash-command parity with `Litos.Gui` — implemented

Command-by-command trace this was scoped from (`src/Litos.Gui/SlashCommand.cs`,
`MainWindow.axaml.cs`'s `TryHandleSlashCommandAsync`) against what `Litos.Api` and
`Litos.VsCodeHost` exposed over HTTP at the time — the "Backend gap" column below is what was
missing *before* this pass; every row is now built, endpoint and webview UI both, and verified via
a live integration smoke test against the real binary (§7.6 confirms the exact endpoints exercised):

| Command | Backend gap (before this pass) | Status |
|---|---|---|
| `/new` | None — purely client-side (mint a new session id) | **Done** |
| `/resume` | None — `GET /sessions`/`GET /sessions/{id}/history` already existed | **Done** — in-webview picker |
| `/provider`, `/model` | `AgentWorker` had no provider/model mutability; no JSON endpoint anywhere | **Done** — `GET /settings`, `GET /settings/models`, `POST /settings/{provider,model}` |
| `/skills`, `/skill` | No listing/load endpoint on any face | **Done** — `GET /skills`, `GET /skills/{name}`, workspace-scoped (`new SkillDiscovery(cwd)`, matching `Litos.Gui`'s own reasoning) |
| `/attach` + clipboard paste-to-attach | No standalone "convert this path/blob to a content block" endpoint anywhere | **Done** — `POST /attachments/from-path` (file picker), `POST /attachments/from-bytes` (paste) |
| `/branch` | `ITranscriptStore.BranchAsync` was never called by any HTTP endpoint | **Done** — `GET /sessions/{id}/branch-points`, `POST /sessions/{id}/branch` |
| `/compact` | `Compactor` was never invoked by any HTTP endpoint | **Done** — `POST /sessions/{id}/compact` |
| `/reflect` | No endpoint anywhere | **Done** — `POST /sessions/{id}/reflect` (proposes text only; the extension writes AGENTS.md via VS Code's own filesystem API and native `vscode.diff`) |
| `/mcp` (+ MCP prompts) | **Was the largest gap**: no `McpConfigStore`/`McpToolProvider`/`McpToolSource` wiring, no JSON CRUD anywhere | **Done** — full `McpToolProvider`/`McpToolRefreshService` wiring (Litos.Api's pattern, not Litos.Gui's manual-refresh one — see §7.4) + `GET/POST/DELETE /mcp/servers`, `POST /mcp/refresh`, own dedicated webview panel. MCP *prompts*-as-commands not yet added (see §7.8 remaining gaps) |
| `/keys` | No endpoint | **Done** (§8, built in an earlier pass) |
| `/update` | N/A | **Dropped from parity** — see confirmed decision below |
| Abort mid-turn | Not a slash command in Gui either | **Not yet built** — see §7.8 |

**Confirmed decisions for this pass** (locked in before architecture design):

- **`/mcp` is in full scope**, not deferred or stubbed — `McpConfigStore`/`McpToolProvider`/
  `McpToolSource` get wired into `Litos.VsCodeHost` exactly as `Litos.Gui`/`Litos.Api` already do,
  plus new JSON endpoints (list servers+status, add, remove, enable/disable, refresh — no existing
  JSON precedent to copy, since the only prior art is the Blazor page) and a management UI.
- **`/update` is dropped, not built.** A VS Code extension updates itself via the Marketplace's own
  auto-update mechanism; porting `Litos.Gui`'s custom GitHub-release `SelfUpdater` (with its Win32
  Job Object relaunch dance built for an interactive GUI window) doesn't map onto a background host
  process the extension manages. The bundled `Litos.VsCodeHost` binary is versioned implicitly —
  a new `.vsix` release ships a new binary — with no separate update check.
- **`/attach` and clipboard paste-to-attach are built together**, not paste-only, since both need
  the identical missing capability: a way for `Litos.VsCodeHost` to turn bytes (a picked file or a
  pasted clipboard blob) into an `ImageBlock`/document content block. The natural endpoint shape
  here is base64-in-JSON on `TurnsEndpoints.cs` (simpler than multipart for a local single-user
  host), not a port of `Litos.Api`'s `AttachmentContentBuilder`'s `IFormFile` path.
- **Paste-to-attach's browser side is simple; the real work was the backend.** A webview is a
  Chromium context, so image paste is a standard `paste` event on the composer reading
  `event.clipboardData.items` for an `image/*` blob — no native P/Invoke, no per-OS branching, unlike
  `Litos.Console`'s Win32 `CF_DIBV5` reader (`ClipboardImageReader.cs`/`Win32Clipboard.cs`) or even
  `Litos.Gui`'s Avalonia `IClipboard.TryGetBitmapAsync()`. Confirmed in practice: the listener
  itself (`webviewContent.ts`'s `paste` handler → `FileReader` → base64 → `pasteAttach` message)
  was a small, mechanical addition; `POST /attachments/from-bytes` was the actual work.
- **UI shell for pickers/commands/MCP management**: all in-webview custom UI, **not** VS Code Quick
  Pick — chosen on its own architectural merits (a persistent, revisitable panel of chat-adjacent
  state benefits from staying visually part of the conversation surface, unlike Quick Pick's
  one-shot command-palette shape). Note: an earlier pass through this design cited "this is how the
  Claude Code VS Code extension does it" as supporting precedent — that claim was made without
  actually verifying Claude Code's internals and shouldn't be treated as confirmed; it's left out
  here deliberately. MCP management specifically gets its **own dedicated second webview panel**
  (mirroring `McpServersWindow`'s scope as its own surface) rather than being crammed into the chat
  panel or reduced to native input boxes.
- **`/reflect`'s diff preview uses VS Code's native diff editor** (`vscode.diff`), not a custom
  webview diff view — the one place this pass intentionally deviates from "everything in-webview,"
  because VS Code already has a strictly better tool for showing a diff than any webview
  reimplementation would.
- **`/context` has no Gui slash-command equivalent** — Gui shows context usage passively in its
  status bar (`RefreshContextUsage()`), not via a command. If a VS Code equivalent is wanted, it
  should be a status-bar-style indicator, not a ported `/context` command; `ReadMe_ConsoleParityPlan.md`'s
  `/context` slice was written for `Litos.Console`, not `Litos.Gui`.

### 7.4 `/mcp` wiring: Litos.Api's pattern, not Litos.Gui's

`Litos.VsCodeHost` has real `IHost`/`BackgroundService` infrastructure (unlike `Litos.Gui`, which
has none — the reason `ReadMe_MCPSupportInLitosGUI.md` chose manual-refresh-only for that face
instead of a poller). Since that infrastructure constraint doesn't apply here, `/mcp` follows
`Litos.Api`'s own pattern instead: `McpToolProvider` constructed and `InitializeAsync`'d before
`builder.Build()` (so the first turn already has real per-tool schemas, bounded by a 30s
per-server handshake timeout), plus `McpToolRefreshService` — a `BackgroundService` polling every
5s to reconcile config changes and retry `Unreachable` servers with backoff, live, with no
process restart needed. Same face-agnostic `Litos.Tools.Mcp` types `Litos.Api`/`Litos.Gui` already
use, same `McpAwareApprovalGate` instance shared between the DI-registered `IToolApprovalGate` and
the one passed to `McpToolProvider` (an early draft of this wiring accidentally constructed two
separate gate instances — fixed before it shipped, since both need to observe the same
`PendingApprovalStore`/`McpConfigStore` state).

### 7.5 `/reflect`'s write step lives in the extension, not the host

Unlike `Litos.Gui`'s `ReflectWindow` (which both proposes the text and writes `AGENTS.md` itself),
`POST /sessions/{id}/reflect` only ever returns the *proposed* content — it never touches disk.
`extension.ts`'s `runReflect` does the write-adjacent work: reads the real `AGENTS.md` (if any)
via `vscode.workspace.fs`, calls the endpoint, then opens VS Code's native diff editor
(`vscode.diff`) against an in-memory `untitled:` document holding the proposal — the user copies
what they want into the real file and saves it manually; nothing is written automatically. If no
`AGENTS.md` exists yet, the proposal opens directly in a text editor instead of a diff (there's
nothing to diff against).

### 7.6 Verification

Every new backend endpoint was exercised in one live integration smoke test against the real
published `win-x64` binary (not mocked): `GET /settings`, `GET /settings/models`, `GET /skills`,
`POST /attachments/from-path`, `GET /mcp/servers` (against this machine's real, already-configured
MCP servers — confirmed live `Connected`/`Unreachable` status and tool counts came back correctly),
an attachment-bearing turn (the model correctly read an attached `README.md` and identified the
project from it), `GET /sessions/{id}/branch-points`, `POST /sessions/{id}/branch`, `POST
/sessions/{id}/compact`, and `POST /sessions/{id}/reflect` (produced a correctly-formatted
`AGENTS.md` proposal). Unit tests cover `AgentWorker`'s new provider/model-switching surface (7
new `.NET` tests) and every new `LitosClient` method's request construction — URL, HTTP method,
JSON body shape/casing — against a mocked `fetch` (19 new TS tests).

The webview UI was then manually exercised inside a real VS Code window (F5 Extension Development
Host) and this immediately caught a real bug the HTTP/client-library-layer testing above could
never have found: `getWebviewHtml()`/`getMcpPanelHtml()` build the entire webview `<script>` body
as text embedded inside an *outer* JS template literal in `webviewContent.ts`/`mcpPanelContent.ts`.
A backslash escaping a character that has no special meaning inside a template literal (`\/`, `\s`
— as opposed to `\\`, `` \` ``, `\$`, `\n`, etc.) is a no-op *identity escape* and is silently
dropped by the JS engine when the outer literal is evaluated, not preserved verbatim the way it
would be in a plain string. Two regex literals in the inner webview script relied on a single
backslash surviving into the emitted text (`/^\/([a-zA-Z]*)$/` for the command-menu trigger,
`/(https?:\/\/[^\s)]+)/g` for `linkify`'s URL matcher); both got corrupted into invalid regex
syntax at generation time (e.g. `/^/([a-zA-Z]*)$/`), which broke the entire inline `<script>`
block's parsing in the browser — the whole webview rendered as a blank panel with no composer, no
error visible anywhere in the panel itself, only in the webview's own DevTools console (`Developer:
Open Webview Developer Tools`) as `Uncaught SyntaxError ... Unexpected token ')'`. Fixed by
doubling the backslashes (`\\/`, `\\s`) so they survive the outer template literal intact; verified
by extracting the actual runtime-generated `<script>` text and syntax-checking it directly, not
just re-reading the `.ts` source (which looked correct on its own — the bug only exists in the
*interaction* between the outer and inner literal, invisible from either layer alone). This is now
confirmed working end-to-end via F5 in a real Extension Development Host window.

A second real bug turned up in the same manual pass, once the panel was rendering: `share_file`
download links worked inconsistently — clicking one link produced a real OS Save-As dialog, an
adjacent one (the model's own Markdown-formatted `[label](url)` prose link) instead opened a blank
pane next to the chat with no download, and right-click "Copy Link" did nothing on either. Root
cause: `linkify()` emitted plain `<a href="..." target="_blank">` elements, and VS Code webviews do
not hand off `<a>` click navigation to the OS consistently — sometimes it reaches the OS default
handler (hence the working Save-As), sometimes it's swallowed into an internal preview pane
instead, and neither path is real, addressable web content a right-click "Copy Link" can act on.
Fixed by no longer emitting anchors at all: `linkify()` now renders both bare URLs and Markdown
`[label](url)` links (previously unhandled — brackets/parens leaked through as literal text
alongside the raw URL) as `<span class="share-link" data-share-link="URL">`, and a delegated click
listener on `#transcript` posts `{type:'openLink', url}` to the extension, which calls
`vscode.env.openExternal` — the one API that reliably reaches the OS the same way on every click,
Save-As dialog included. Copy-link is no longer needed as a separate affordance since the label
itself is now the click target and the raw URL is never left dangling as literal text to select.

### 7.7 Markdown rendering

Assistant replies were plain text (via `linkify`'s narrow bare-URL/markdown-link handling only) —
headers, bold/italic, and lists all rendered as literal `##`/`**`/`-` characters, a real gap versus
`Litos.Gui`'s `MarkdownViewer`-based bubbles. Fixed by vendoring
[`marked`](https://github.com/markedjs/marked) (MIT licensed, v12.0.2, single-file minified UMD
build, zero dependencies) at `media/marked.min.js` — not npm-installed into the packaged extension
and not loaded from a CDN (the CSP's `default-src 'none'` forbids any external `script-src`), but
read via `fs.readFileSync` at HTML-generation time and inlined as its own `<script>` block ahead of
the webview's own, so `marked` is a ready global (`marked.parse`, `marked.Renderer`) by the time the
extension's script runs — the same "everything self-contained, no `<script src>`" shape the rest of
this webview already follows.

Two overrides were required, not just a default `marked.parse` call:
- **`renderer.link`** — marked's default emits a real `<a href>`, exactly the element §7.6's
  earlier fix moved away from for reliability reasons. Overridden to emit the same
  `<span class="share-link" data-share-link="URL">` the rest of the webview uses, so a `share_file`
  link the model writes as ordinary Markdown (as opposed to a bare URL) routes through the same
  delegated click listener and `openExternal` call, not a second, less reliable code path.
- **`renderer.html`** — marked v12 dropped the old `sanitize` option and passes raw HTML found in
  the Markdown source through unescaped by default. Assistant text is untrusted model output, not
  authored HTML (same posture `linkify`'s own escape-first approach already takes), so this is
  overridden to render any raw HTML in the source as inert escaped text instead — verified with a
  literal `<img src=x onerror="alert(1)">` in the test suite, confirmed neutralized.

Streaming still uses `linkify` (cheap, re-run on every token) while a message is still growing;
`marked.parse` is invoked exactly once, when the message completes — mirrors `Litos.Gui`'s own
`AppendAssistantText`/`FinalizeAssistantText` split (plain text while streaming, one real render on
completion) and avoids re-parsing full Markdown into HTML on every delta. `renderHistory` (used by
`/resume` and `/branch`) also renders through `marked.parse`, for consistency with a freshly
completed message. Covered by 5 new tests in `webviewMarkdown.test.ts`, which extract the real
generated `<script>` text (not a hand-copied re-implementation) and exercise header/bold/list
rendering, both link forms becoming `data-share-link` spans, and the raw-HTML-escaping override —
bringing the TS suite to 47 tests.

A stray backtick inside a code comment (written while adding the `renderer.link` override,
originally read `` `renderer.link` `` for Markdown emphasis) briefly broke `tsc` compilation
outright: the whole webview body is one giant outer TS template literal, and a matched backtick
pair anywhere inside it — even inside a `//` comment — closes and reopens that literal, corrupting
everything after it. Unlike §7.6's silent-corruption bug (a single backslash silently dropped at
runtime with no compiler error), this one is loud — `npx tsc` fails immediately — so it was caught
before ever reaching a running webview.

### 7.8 Remaining gaps

- **Abort mid-turn** has no UI yet — the backend already supports it "for free" (both `Litos.Api`
  and `Litos.VsCodeHost` stop a turn when its SSE request disconnects), but the webview composer
  has no stop button that would actually abort its `fetch`.
- **MCP prompts as commands** (`Litos.Gui`'s dynamic `/{server}__{prompt}` commands, sourced from
  `McpToolProvider.Prompts`) are not surfaced in the command menu — `/mcp` itself (server
  management) is fully built, but running a connected server's *prompt* as if it were typed text
  is not yet wired into `SLASH_COMMANDS`/`runSlashCommand`.

### 7.10 Composer usability pass — implemented

Three small composer/attachment UX gaps, closed in one pass:

- **Taller, auto-growing input box.** `#composerInput` started at a fixed `rows="2")` — enough for
  one short line before the box started internally scrolling, which made multi-line prompts (a
  common case: pasted stack traces, multi-part instructions) awkward to review before sending.
  Now starts at 4 rows and auto-grows with each `input` event (`el.style.height = 'auto'` then
  `el.style.height = el.scrollHeight + 'px'`, the standard textarea-autogrow trick — Avalonia/WPF
  have real auto-sizing layout primitives for this, the DOM does not) up to a capped max height
  (`max-height: 14 lines` via CSS, in `ch`-independent `em`/`px` terms so it tracks the editor font
  size VS Code themes provide); beyond the cap the textarea scrolls internally exactly as before.
  Resets back to its 4-row minimum after every send (`resetComposerHeight()`, called alongside the
  existing `inputEl.value = ''` in `send()`/`selectCommand()`), so a long draft doesn't leave the
  box permanently tall for the next message.
- **Slash-command button.** A small `/` icon button sits in the composer row, left of Send. Click
  focuses the composer, sets its value to `/`, and opens the exact same in-webview command-menu
  dropdown `updateCommandMenu()` already renders for typed `/` — no second menu implementation,
  no native `showQuickPick` (would break the "everything chat-adjacent stays in-webview" posture
  §7's design decisions already committed to for `/resume`/`/provider`/etc.). Purely a discoverability
  affordance for users who don't know the `/`-to-trigger convention exists.
- **Removable attachment chips.** Each pending-attachment chip (populated from `/attach`'s file
  picker, clipboard paste-to-attach, or a loaded `/skill`) now renders a small `×` at its trailing
  edge. Clicking it removes just that one attachment before send: the webview posts
  `{type: 'removeAttachment', index}` (index into the chip list as currently rendered) to the
  extension; `extension.ts`'s `handlePanelMessage` splices that same index out of
  `state.pendingAttachments` (the array `send`/`pasteAttach`/`/attach`/`/skill` all already push
  onto) and the chip removes itself from the DOM. No backend change — this only ever touches an
  attachment queued client-side before the turn is sent; nothing about `POST /sessions/{id}/turns`
  or the attachment-conversion endpoints changed. Previously the only way to drop a mistakenly
  attached file was to send the turn anyway or reload the whole panel.
- **Attachment chip previews.** A chip is no longer just a text label — it now shows a small 20px
  visual so a pasted/attached file is recognizable (and, for images, visually verifiable) before
  send. Image attachments (`kind: "image"`) render the real image as a thumbnail: `extension.ts`'s
  `attachmentThumbnail()` builds a `data:{mimeType};base64,{base64Data}` URI straight from the
  `AttachedContent` it already has in hand (no extra fetch) and sends it alongside
  `attachmentAdded`; the webview drops it straight into an `<img>`. Document attachments (`/attach`
  on a non-image file, or a loaded `/skill`) instead get one of a small set of inline-SVG file-type
  glyphs — `attachmentIconKind()` classifies by file extension (`.pdf`, `.doc(x)`, `.xls(x)`/`.csv`,
  `.ppt(x)`, `.txt`/`.md`/`.log` → pdf/word/excel/powerpoint/text) and falls back to a plain generic
  glyph for anything unmapped, so an unrecognized extension still renders something rather than a
  blank chip. All icons are inline SVG defined in `webviewContent.ts` itself (`ATTACHMENT_ICON_SVG`)
  — no icon font, no external asset, consistent with this webview's existing "everything
  self-contained" posture (§7.7's vendored `marked`, §6's bundled binary). The thumbnail/icon swatch
  itself is given an explicit white background rather than inheriting the chip's own badge-colored
  background — both a transparent-cornered pasted image and the file-type glyphs (drawn assuming a
  light backing, matching common OS file-icon conventions) read poorly sitting directly on that
  color.

  **Real bug found live**: the page's CSP was `default-src 'none'` with no `img-src` directive at
  all, which silently blocks `data:` URIs too — the pasted-image thumbnail's `<img src="data:...">`
  never rendered, with no visible error anywhere (CSP violations don't throw, and this webview has
  no console output surfaced to the user by default). The file-type SVG icons worked fine alongside
  it purely because inline `<svg>` injected via `innerHTML` isn't a CSP `img-src` resource load at
  all — which is exactly what made the image-specific breakage easy to miss from source alone.
  Fixed by adding `img-src data:` to the CSP — deliberately scoped to `data:` only, not `*` or
  `https:`, so this doesn't open the webview up to loading arbitrary remote images.

### 7.11 Real bug found post-ship: a bad pasted-image MIME type could silently poison a session forever

Found live, not in testing: pasting a screenshot from certain OS clipboard sources produced a
`DataTransferItem` whose `.type` was an empty string rather than a real `image/*` value.
`webviewContent.ts`'s paste handler forwarded that empty string verbatim as `mimeType` to
`pasteAttach`, `extension.ts` forwarded it verbatim to `POST /attachments/from-bytes`, and
`AttachEndpoints.cs` had no validation on it at all — it built an `ImageBlock` with
`MimeType: ""` and returned 200. That `ImageBlock` can't be sent to any provider, so the *next*
turn 500'd — but by then it was already too late in a second, worse way: `extension.ts`'s "send"
handler only cleared `state.pendingAttachments` *after* `await sendTurn(...)` returned
successfully, so a 500 left the same broken attachment sitting in the queue, and it silently
re-attached itself to every subsequent message typed in that panel — including plain text-only
messages with no visible attachment chip at all, since the webview's own chip UI had already
(correctly) cleared itself optimistically on send. Each failed retry pushed a duplicate copy in
alongside it (`pasteAttach` only ever pushes), so a session hit by this bug got a new, larger,
still-broken image block appended to `state.pendingAttachments` on every failed send — confirmed
on a real affected session's on-disk transcript, whose per-line size (JSONL is append-only) grew
from ~425KB to ~850KB across three consecutive failed turns as duplicate copies of the same
corrupted image piled up in each new user message.

Fixed in three places, from the ground up:
- **`AttachEndpoints.cs`**'s `/attachments/from-bytes` now rejects a missing/non-`image/*`
  `MimeType` with a `400`, so a bad attachment can never enter a transcript in the first place —
  this is the real fix; the other two are defense in depth.
- **`extension.ts`**'s `pasteAttach` handler now defaults an empty/non-`image/*` clipboard MIME
  type to `image/png` before it ever reaches the endpoint above, since `image/png` is what
  paste-to-attach's own filename (`pasted-image.png`) already assumes.
- **`extension.ts`**'s `send` handler now clears `state.pendingAttachments` *before* calling
  `sendTurn`, not after — matching the webview's own already-optimistic chip-clearing — so a
  failed turn (whatever the cause) can never leave a stale attachment silently reattached to
  future messages on that session again.

No provider/agent-layer code was touched — `Litos.Api` and `Litos.Gui` don't share
`Litos.VsCodeHost`'s `AttachEndpoints.cs` (a face-local copy, same convention as `Files/*.cs` in
§7.9) and don't call it, so neither face is affected by either the bug or the fix.

### 7.12 Attachments were invisible in the transcript once sent

Reported by a user: after sending a message with an attached PDF, the model answered correctly, but
nothing in the transcript indicated a file had been attached at all — the user bubble showed only
the typed text. Two separate gaps, fixed together:

- **Live send showed nothing.** `send()` called `addEntry('user', text)` with no reference to
  whatever was in `pendingAttachmentChips` at the time, even though the webview already had the
  real filenames in hand (populated by `attachmentAdded` for every `/attach`/paste/`/skill`).
  Fixed by a new `addUserEntry(text, attachmentNames)` — mirrors `Litos.Gui`'s own
  `AddUserBubble`/`BuildBubbleLabel` split (`MainWindow.axaml.cs`) for the identical reason: once
  the composer's chips clear on send, a small `📎 filename, filename` line above the message is the
  *only* remaining record of what was attached to that turn. `pendingAttachmentLabels` (a new array
  kept parallel to `pendingAttachmentChips`) is snapshotted via `.slice()` *before*
  `clearAttachmentChips()` empties it, so the label list handed to `addUserEntry` isn't the same
  array being cleared out from under it.
- **History replay leaked a document attachment's converted text into the displayed message.**
  A real, independent bug found while scoping the above: `/sessions/{id}/history` built each
  message's displayed `text` by concatenating *every* `TextBlock` in the stored `ChatMessage`, but
  `/sessions/{id}/turns` always constructs a turn's content as `[TextBlock(typed input),
  ...attachments]` — so a document attachment (`/attach` on a non-image file, or a loaded `/skill`,
  both converted to a `TextBlock` by `AttachEndpoints.ToContentBlock`) got its entire
  `UntrustedContent`-wrapped Markdown silently glued onto the user's own typed text on replay,
  rendered as if the user had typed the whole document. Fixed by taking only the *first* `TextBlock`
  as the displayed text and folding any `TextBlock`s past it into the same `attachments` count
  `ImageBlock` already contributes to — verified live: attached a real text file, sent a turn, and
  confirmed `/history` now returns the clean typed message (`"What does this file say?"`) with
  `attachments: 1`, not the leaked document body.

**Scope note, matching a question raised while designing this**: this only touches what's already
in hand client-side at send time (live path) and how an *existing* persisted `TextBlock` array is
summarized for display (history path) — no new field was added to `TranscriptEntry` or any shared
`Litos.Agent` type, and nothing about what's sent to the model changed. `Litos.Gui`'s own
`AddUserBubble` comment states outright that it has the same live-only limitation (attachment names
have "no other trace in the transcript" once its staging strip clears) — matching that scope here
rather than adding new persistence was a deliberate choice, not an oversight: recovering real
filenames on history replay after a session reload would need a genuinely new persisted field
(`ImageBlock`/`TextBlock` carry no filename at all), which is a larger, separate change than this
pass's actual reported problem called for.

## 7.9 File sharing with clickable download links — implemented

`Files/ShareFileTool.cs`, `Files/SharedFileStore.cs`, `Files/SharedFileMeta.cs`, `Files/
FilesEndpoints.cs` are local copies of `Litos.Api`'s own (unchanged logic — same disk layout under
`~/.litos/shared-files/{owner}/{token}/`, same 24h token lifetime, same reject-don't-sanitize path
safety, same known v1 debt of no expired-share cleanup job). One real simplification versus
`Litos.Api`'s version: there is no `PUBLIC_BASE_URL` concept here. `Litos.Api`'s `ShareFileTool`
needs an operator to configure an externally-reachable base URL and degrades to a bare token when
that's unset; `Litos.VsCodeHost` is always loopback-only, so its own base URL
(`http://127.0.0.1:{port}`) is always known — `Program.cs`'s `LoopbackBaseUrl` holder is populated
right after `app.StartAsync()` resolves the real port, and `ShareFileTool` reads `.Value` lazily
inside `InvokeAsync` rather than at construction (DI registration happens before the port is known,
but no tool call can run before it, so the ordering is safe — see that class's own remarks).

**Webview rendering**: `webviewContent.ts`'s `linkify()` auto-links bare `http(s)://` URLs
(escape-first, then pattern-match — same safety principle as `AngularChat/app.js`'s own `linkify`
filter, `target="_blank" rel="noopener noreferrer"` since the URL is model-influenced), applied to
both tool-result detail text (where `share_file`'s own response — `"Shared x.txt: http://127.0.0.1:
PORT/files/{token} (expires ...)"` — renders) and streamed assistant text, re-rendered on every
`textDelta` rather than incrementally patched. This differs from `AngularChat`'s own filter, which
only matches markdown `[label](url)` syntax — `share_file`'s actual output is a bare URL, not
markdown-link syntax, so a straight URL-pattern auto-linker is the correct match here, not a port of
that filter's regex.

Verified end-to-end via a live integration smoke test: asked a real turn to `share_file` this
repo's `README.md`, extracted the returned URL, fetched it, and confirmed the downloaded bytes
matched the source file exactly.

**Cross-session link staleness — fixed**: a share link embeds the port `Litos.VsCodeHost.exe`
happened to bind (`Program.cs`'s port-0 OS-assigned bind) at the moment `share_file` ran, and that
string is what ends up persisted into the transcript (as the assistant's own reply text quoting the
tool result). The file/token themselves are fine for a full 24h regardless of process lifetime
(`SharedFileStore.TryGetAsync` re-reads `meta.json` fresh off disk on every request), but the
*process* is not: closing or reloading the VS Code window kills the shared host (`deactivate()`),
and the next panel-open spawns a new one on a new random port. A link from an earlier session that
gets clicked after such a restart was pointing at a port nothing listens on anymore, even though the
underlying file was still perfectly valid. Fixed entirely on the extension-host side, no protocol or
webview change needed: `extension.ts` now keeps `sharedHost.baseUrl` (the live host's own
`http://127.0.0.1:{port}`) alongside `client`, and its `openLink` handler rewrites any URL whose
path matches `/files/{token}` to the *current* `sharedHost.baseUrl` before calling
`vscode.env.openExternal` — the token in the path is the only part that actually identifies the
file, so the stored host:port is simply discarded and replaced at click-time. A stale link now
resolves correctly as long as the token is still within its 24h window, independent of how many
times the host process has restarted since the link was generated.

## 8. API-key first-run UX — implemented

`ConfigEndpoints.cs` (`GET /config/status`, `POST /config/keys`) exposes the same persistence
`Litos.Gui`'s `ApiKeysWindow` already uses — Windows: user-scope env var via
`Environment.SetEnvironmentVariable(..., EnvironmentVariableTarget.User)`; else: `~/.litos/
config.json` — over JSON instead of a modal window, so the same key works across every face. Not
VS Code `SecretStorage` — a deliberate choice (see §11) so a key entered once in VS Code is
immediately usable by `Litos.Console`/`Litos.Gui` too, not siloed to this extension.

**No live reload**: `LitosHostBuilder.AddLitosAgent` conditionally registers each keyed
`IChatProvider` once, at DI-container-build time — there is no seam to swap a provider registration
into an already-built `IServiceProvider`. `Program.cs` therefore stays alive with no `AgentWorker`/
turns endpoints registered at all when unconfigured (only `/config/*`), and `extension.ts`'s
`saveKeys` handler kills and respawns the whole host process after a successful save, then re-checks
`/config/status` before showing the chat UI — the automated equivalent of `ApiKeysWindow`'s own
first-run message, "Litos will then close so you can restart it."

**Caution for anyone testing this locally**: the Windows env-var write path cannot be sandboxed by
overriding `USERPROFILE`/`HOME` in a test process's environment the way the file-based `config.json`
path can — `EnvironmentVariableTarget.User` always targets the real Windows user account regardless
of what the calling process's own environment looks like. A local smoke test of `POST /config/keys`
with a real provider name (not just `LocalBaseUrl`) on Windows **will** write to the real machine's
user environment variables; this was hit and had to be manually reverted once already during
development. Test this path with `LocalBaseUrl` only (config-file-only, safely sandboxable) or
accept the real-env-var side effect deliberately, never accidentally.

## 9. Key files

| File | Role |
|---|---|
| `src/Litos.VsCodeHost/Program.cs` | Composition root: `AddLitosAgent`, port-0 bind, stdout handshake. |
| `src/Litos.VsCodeHost/AgentWorker.cs` | Trimmed copy of `Litos.Api`'s `AgentWorker` (no attachment queueing). |
| `src/Litos.VsCodeHost/AutoApprovalGate.cs` | Copy of `Litos.Api`'s, zero dependencies. |
| `src/Litos.VsCodeHost/Turns/TurnsEndpoints.cs` | Trimmed copy of `Litos.Api`'s, no auth, `SessionOwner.Local` fixed, JSON-only; merges `PendingApprovalRelay` onto the SSE stream. |
| `src/Litos.VsCodeHost/Config/ConfigEndpoints.cs` | `GET /config/status`, `POST /config/keys` — first-run key setup (§8). |
| `src/Litos.VsCodeHost/ChannelContext.cs` | Local copy of `Litos.Api`'s, trimmed to `Owner`/`SessionId` only — tags each turn so approvals route back to the right SSE stream. |
| `src/Litos.VsCodeHost/Approvals/PendingApprovalRelay.cs` | Bridges `PendingApprovalStore`'s process-wide events to the one turn/session that triggered each approval. |
| `src/Litos.VsCodeHost/Approvals/PendingApprovalWireEvents.cs` | `PendingApprovalRequested`/`Resolved` wire shapes merged onto the SSE stream — deliberately not `AgentEvent` subtypes. |
| `src/Litos.VsCodeHost/Files/ShareFileTool.cs` | `share_file` tool, local copy of `Litos.Api`'s, using `LoopbackBaseUrl` instead of `PUBLIC_BASE_URL` (§7.9). |
| `src/Litos.VsCodeHost/Files/{SharedFileStore,SharedFileMeta}.cs` | Local copies of `Litos.Api`'s, unchanged. |
| `src/Litos.VsCodeHost/Files/FilesEndpoints.cs` | `GET /files/{token}` — unauthenticated, token-as-credential, local copy of `Litos.Api`'s. |
| `src/Litos.VsCodeHost/Turns/AgentSettingsEndpoints.cs` | `/settings`, `/settings/models`, `/settings/provider`, `/settings/model` — `/provider`/`/model` parity. |
| `src/Litos.VsCodeHost/Turns/AgentSettingsEndpoints.cs` (`SessionActionsEndpoints`, `ReflectEndpoints`) | `/sessions/{id}/branch-points`, `/sessions/{id}/branch`, `/sessions/{id}/compact`, `/sessions/{id}/reflect`. |
| `src/Litos.VsCodeHost/Turns/AttachEndpoints.cs` | `POST /attachments/from-path` (file picker), `POST /attachments/from-bytes` (clipboard paste) — `/attach` + paste-to-attach parity. |
| `src/Litos.VsCodeHost/Turns/{ImageMedia,UntrustedContent}.cs` | Local copies (face-local convention), needed by `AttachEndpoints.cs`. |
| `src/Litos.VsCodeHost/Skills/SkillsEndpoints.cs` | `GET /skills`, `GET /skills/{name}` — workspace-scoped, reuses `SkillTool.InvokeAsync` itself rather than the `internal` frontmatter parser directly. |
| `src/Litos.VsCodeHost/Mcp/McpEndpoints.cs` | `GET/POST/DELETE /mcp/servers`, `POST /mcp/refresh` — `/mcp` parity. |
| `tests/Litos.VsCodeHost.Tests/` | `AgentWorkerTests.cs` (incl. provider/model-switching, 7 new), `AutoApprovalGateTests.cs`, `Approvals/PendingApprovalRelayTests.cs`, `Approvals/McpAwareApprovalGateWiringTests.cs`, `Files/ShareFileToolTests.cs`, local `Fakes/` — 31 passing. |
| `src/Litos.VsCode/src/hostProcess.ts` | Spawns the bundled binary, scans stdout for the port handshake, defensively re-chmods on non-Windows. |
| `src/Litos.VsCode/src/agentEvents.ts` | SSE client + event classifier, ported from `AngularChat/app.js`; `LitosClient` methods for every endpoint above. |
| `src/Litos.VsCode/src/extension.ts` | Activation, shared-host lifecycle across multiple panels (§5), first-run gate, `runSlashCommand`/`handlePickerSelection` dispatch, native `vscode.diff` for `/reflect` (§7.5), MCP panel lifecycle. |
| `src/Litos.VsCode/src/webviewContent.ts` | Styled HTML/CSS transcript; command-menu (`/`-trigger dropdown), generic picker modal (reused by `/resume`/`/provider`/`/model`/`/branch`/`/skills`), pending-attachment chips, clipboard-paste listener, `linkify()` for clickable links (§7.9). |
| `src/Litos.VsCode/src/mcpPanelContent.ts` | `/mcp`'s own dedicated webview panel — server list, live status, add/enable-disable/remove form. |
| `src/Litos.VsCode/src/__tests__/` | `agentEvents.test.ts`, `litosClientCommands.test.ts`, `hostProcess.test.ts`, `webviewMarkdown.test.ts` (vitest, 47 passing) — the `vscode`-free, unit-testable modules, plus the generated-`<script>`-text extraction tests for Markdown rendering. |
| `src/Litos.VsCode/media/marked.min.js` | Vendored `marked` v12.0.2 (MIT), inlined verbatim into the webview's `<script>` — see §7.7. |
| `deploy/publish-vscodehost-{windows,linux,macos}.{ps1,sh}` | Per-platform publish scripts, mirroring the now-shelved `Litos.Console`'s own (§11) — only Windows has been run/tested so far. |

## 10. Publishing to the VS Code Marketplace — not yet done

Scoped but not yet executed. Nothing below has been run for real; this is the plan, not a status
report. Split into four buckets, in dependency order.

### 10.1 One-time account setup (portal work, outside CI) — done

1. **Done.** Publisher created at https://marketplace.visualstudio.com/manage as **`litosai`**
   (`litos` was unavailable). `src/Litos.VsCode/package.json`'s `"publisher"` field was updated to
   match (`litos`→`litosai`) — per this section's own original guidance, the ID drives
   `package.json`, not the other way around. No other file references the publisher ID by name
   (`name`/`contributes.*.id` are extension-namespaced, unaffected by publisher).
2. **Done.** PAT generated at `https://dev.azure.com/{org}` → User settings → Personal access
   tokens, **Organization: All accessible organizations**, **Scope: Marketplace (Manage)**. Max
   expiry is 1 year — needs periodic rotation; an expired token fails CI publishing silently (no
   advance warning), so whoever rotates it should also update the GitHub secret at the same time.
3. **Done.** Token stored as the GitHub Actions repository secret `VSCE_PAT`.
4. **Not yet done.** Verify locally before trusting CI to publish: `npx vsce login litosai`, paste
   the PAT — confirms the publisher ID and token are both valid. Recommended before the first real
   `vsce publish`.

### 10.2 Extension packaging plumbing (`src/Litos.VsCode/`) — not yet added

- `@vscode/vsce` as a devDependency — not present in `package.json` today; `npx vsce` currently
  fails locally with no package installed.
- A `.vscodeignore` — doesn't exist yet. Needs to exclude `node_modules/*` (check whether any
  runtime `dependencies` actually exist first — `dist/extension.js` may already be self-contained
  via `tsc`'s plain `commonjs` output, in which case this is `node_modules/**`, full stop),
  `src/**`, `**/__tests__/**`, `.vscode/**`, `*.map`, `package-lock.json` — and, per platform-target
  build, the **other four RIDs'** `bin/<rid>/` folders. `vsce`'s `--target` flag does not do this
  exclusion automatically; each targeted `vsce package --target <target>` needs the unrelated
  `bin/*` directories moved aside (or a filtered copy of the tree staged) before packaging, or
  every platform's `.vsix` silently balloons to include all five binaries.
- Missing `package.json` fields the Marketplace expects: `repository`, `license` (repo root already
  has `LICENSE.txt`, Apache 2.0 — the `license` field should reference that, e.g. `"license": "SEE
  LICENSE IN LICENSE.txt"` since it isn't a bare SPDX identifier), optionally `bugs`/`homepage`.
- Investigate and strip the stray `.pdb` files currently sitting in `bin/win-x64/` alongside the
  real `.exe` (`libSkiaSharp.pdb` alone is ~84MB, plus nine smaller ones) — these must never enter a
  packaged `.vsix`. Worth checking *why* `dotnet publish -p:PublishSingleFile=true` is emitting
  `.pdb` output into that folder at all before just deleting them in a build step — possible stale
  `-o` directory reused across a non-single-file and a single-file publish.

### 10.3 Producing and verifying all 5 host binaries

- **Windows** (`win-x64`): already built (§6), but never smoke-tested from an actually-*packaged*
  extension — only from F5, which never exercises the `.vscodeignore`/packaging path at all.
- **macOS** (`osx-arm64`, `osx-x64`): `deploy/publish-vscodehost-macos.sh` exists and is complete
  (codesign + notarize + best-effort staple, reusing the exact `APPLE_SIGN_IDENTITY`/`APPLE_ID`/
  `APPLE_TEAM_ID`/`APPLE_APP_PASSWORD` secrets `release-console.yml`/`release-macos.yml` already use
  for `Litos.Console`/`Litos.Gui`) but has **never actually been run**. Note this is a *different*
  signing shape than `release-macos.yml`'s — that workflow signs `Litos.Gui`'s `.app` bundle with
  entitlements; this signs a plain headless binary, no bundle, no entitlements, `codesign --sign`
  only. Same certificate/credentials, different artifact — no new Apple Developer enrollment needed,
  just a first real run of the existing script.
- **Linux** (`linux-x64`, `linux-arm64`): `deploy/publish-vscodehost-linux.sh` exists, no signing
  required (no Gatekeeper-equivalent gate), also never actually run.
- Each binary should be smoke-tested end to end — spawn it, confirm the stdout port handshake
  (§4.1), confirm a real chat turn works — ideally from a locally `vsce package`'d `.vsix` actually
  installed into a real VS Code, not just the raw published binary, since installing from a `.vsix`
  is the one step (zip-and-re-extract) that can silently drop the executable bit (§6) or otherwise
  behave differently than a binary sitting directly in a dev checkout.

### 10.4 `release-vscode.yml` CI workflow — not yet written

Structurally: `release-console.yml`'s tag-triggered, per-RID matrix (already flagged in §6 as the
correct template — same shape of plain headless binary, not a windowed bundle) plus a packaging
stage Console's own workflow doesn't need (Console ships raw zipped exes; this needs `vsce package`/
`vsce publish` on top of the binaries):

```
tag push (vscode-v*.*.*)
 ├─ publish-windows     → dotnet publish win-x64 → upload-artifact
 ├─ publish-macos (matrix: osx-arm64, osx-x64)
 │    → import cert (same steps as release-macos.yml/release-console.yml)
 │    → deploy/publish-vscodehost-macos.sh (sign + notarize)
 │    → upload-artifact
 ├─ publish-linux (matrix: linux-x64, linux-arm64)
 │    → deploy/publish-vscodehost-linux.sh → upload-artifact
 └─ package (needs: all above)
      → download all 5 binaries into their bin/<rid>/ slots
      → npm ci && npm run compile (src/Litos.VsCode)
      → per target: vsce package --target <target>
      → attach .vsix's to a GitHub Release (softprops/action-gh-release, same as the other
        release-*.yml workflows) and/or `vsce publish` straight to the Marketplace using VSCE_PAT
```

**Open decision, not yet made**: publish straight to the Marketplace from CI on tag push, vs. build
and attach `.vsix`s to a GitHub Release only, with a manual `vsce publish <file>.vsix` run by hand
for the first release or two. The latter is safer for a first release specifically — it allows
inspecting the actual packaged `.vsix` contents and size before anything goes out publicly — and
should be the default until the packaging step (§10.2) is proven trustworthy; switch to full
CI auto-publish only after that.

**Recommended sequencing**: do §10.3 (produce one real signed macOS binary and one Linux binary,
package a `.vsix` locally, install it in a real VS Code, confirm chat works end to end) *before*
writing §10.4's CI workflow. The unknowns here — the stray `.pdb`s, whether `vsce package --target`
actually produces a clean per-platform bundle, whether a notarized-but-not-stapled macOS binary
passes Gatekeeper when spawned silently as a child process rather than user-launched — are all far
cheaper to debug locally than inside CI's slower feedback loop.

## 11. Design decisions confirmed during scoping

- **Backend**: new minimal local face (`Litos.VsCodeHost`), not a `Litos.Api` "local mode" —
  avoids fighting that project's multi-tenant assumptions.
- **Binary delivery**: bundle self-contained per-RID binaries in the `.vsix` rather than requiring
  a separate .NET install or downloading one on first run — no first-run internet dependency, no
  setup step beyond installing the extension.
- **Webview UI**: styled HTML/CSS transcript, not `xterm.js`. Both a true PTY-passthrough of
  `Litos.Console` and an ANSI-stream-rendering approach from the new host were evaluated and
  rejected — the former just re-derives "launch Console in a terminal" with an added `node-pty`
  dependency for no benefit over VS Code's free `createTerminal`; the latter relocates the exact
  rendering-engine cost `Litos.Console` already paid once (Spectre.Console → Terminal.Gui, per
  `ReadMe_AgentDesign.md` §7.3) into a second, harder-to-get-right target (hand-rolled ANSI
  layout) instead of avoiding it.
- **Shutdown**: no graceful-shutdown handshake in v1 (§4.4) — explicit "don't worry about it yet"
  from the user; direct process kill is enough for a first working version.
- **API-key storage**: shared `~/.litos/config.json`/Windows user env vars (§8), not VS Code
  `SecretStorage` — a key entered once works across every face, matching how session storage is
  already shared (§5), rather than siloing credentials per-face.
- **Extension icon**: `Litos.Gui`'s existing `src/Litos.Gui/Assets/AppIcon.png` (256×256), copied to
  `src/Litos.VsCode/media/icon.png` and wired via `package.json`'s `icon` field — real branding
  reused as-is rather than commissioning new artwork.
- **macOS signing**: required, not optional, for `Litos.VsCodeHost`'s macOS builds specifically
  (see §6) — reuses the same Apple Developer credentials `Litos.Console`/`Litos.Gui`'s own macOS
  publish scripts already use.
- **`Litos.Console` is shelved as a product** (per explicit direction), but its now-unused
  `deploy/publish-console-*` scripts remain the correct *structural reference* for
  `Litos.VsCodeHost`'s own publish scripts — both are plain headless/CLI binaries, not windowed
  `.app` bundles like `Litos.Gui`'s publish script produces. Shelving Console doesn't change which
  pattern is correct for `Litos.VsCodeHost` to follow.
