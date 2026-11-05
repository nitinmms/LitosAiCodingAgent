# Artifacts (VS Code face, v1)

Lets the Litos agent publish a self-contained HTML page — a report, a design proposal, a
diagram, a small interactive tool — that the user opens in their browser from a link in the
chat. Modelled on Claude Code's Artifacts feature, scoped down to "test the waters" in the VS Code
extension only. Litos.Gui and the Software Factory are deliberately out of scope for v1; §9
records how each would follow.

## 1. Decisions (agreed 2026-11-05)

| Question | Decision |
|---|---|
| Target face | VS Code extension (Litos.VsCode + Litos.VsCodeHost) only |
| Viewer | The user's default browser, via the link the tool returns. No webview panel. |
| Auto-open | Never — the tool result is a clickable link; the user opens it when they want |
| Content | Static HTML; inline scripts/styles plus scripts and stylesheets from an allowlisted set of CDNs. No network calls from the page, no runtime capabilities (shared DB, comments) |
| Serving | A new route on the existing VsCodeHost loopback port (Approach A), isolated by response headers alone |
| Code home | New shared project `src/Litos.Artifacts`, wired only into VsCodeHost for now |
| "Artifact published" signal | None beyond the tool result itself — the existing `toolCallResult` text and the webview's existing linkifier. No new `AgentEvent` type, so no C#/TS mirror changes |
| Design guidance | A built-in `artifact-design` skill, shipped inside the host binary |
| Compaction | Skills loaded before a compaction cut are re-attached verbatim, so the artifact guidance survives (§6) |

## 2. User-visible behaviour

1. The user asks for something page-shaped ("make me a dashboard of these benchmark numbers").
2. The model loads the `artifact-design` skill (the tool description tells it to), writes an HTML
   file with `write_file`, then calls `artifact` with `action: "publish"` and that file path.
3. The tool result in the chat reads:

   ```
   Published artifact "Benchmark Dashboard" (id 3f9c…, version 1, new).
   Open: http://127.0.0.1:53817/artifacts/3f9c0a1b2c3d4e5f
   ```

   The URL is clickable (webviewContent.ts `linkify` → `openLink` → `vscode.env.openExternal`).
4. The user asks for a change. The model edits the same file and publishes the same path again →
   version 2 at the **same URL**; the user refreshes the browser tab.
5. After a VS Code reload the host gets a new port; the old link in the transcript still works
   because the extension rewrites `/artifacts/{id}` links to the current host on click, exactly
   as it already does for `share_file`'s `/files/{token}` links.

## 3. The `artifact` tool

One tool, an `action` discriminator (same shape as Claude Code's tool):

| action | arguments | result |
|---|---|---|
| `publish` | `file_path` (required), `title`, `id` | Creates or updates an artifact; returns the line above |
| `read` | `id` (required), `version` | The stored HTML of that version (latest when omitted), truncated past 100,000 characters |
| `list` | — | One line per artifact, most recently updated first: id, version, title, updated time, URL |
| `delete` | `id` (required) | Removes the stored artifact (every version). The source file on disk is untouched |

**Publish rules**
- Target resolution: an explicit `id` updates that artifact (an unknown id is an error, never a
  silent create); otherwise a `file_path` that an existing artifact was published from updates
  that one (path compared fully-qualified, case-insensitively on Windows); otherwise a new
  artifact is created.
- Only `.html` / `.htm` files, at most 10 MB, must exist.
- Title: the `title` argument, else the page's `<title>`, else the file name without extension.
  A republish without an explicit title re-derives it, so renaming the page's `<title>` renames
  the artifact.
- Not approval-gated, same as `share_file`: publishing copies a file the model could already read
  into a store served only on loopback. Delete removes only Litos's own copy.

**Why a file path rather than inline HTML?** The page stays an ordinary workspace file the model
can `edit_file` for small changes and republish, and the user can open and diff it — the same
workflow Claude Code uses. The skill tells the model where to write it (§5).

## 4. Storage and serving

**Store** (`ArtifactStore`): `~/.litos/artifacts/{id}/`
- `meta.json` — id, title, source path, current version, oldest retained version, created/updated
  timestamps. Replaced via temp file + move, retried briefly because on Windows the replace fails
  while a concurrent reader (a browser refresh, another window's host) has the file open.
- `v{n}.html` — one file per version. The newest 50 versions are kept; older version files are
  pruned on publish.
- `id` is `RandomNumberGenerator.GetHexString(16, lowercase: true)` — unguessable, and the sole
  credential for the unauthenticated GET route, same model as `share_file`'s token.
- No expiry (unlike shares' 24h). `meta.json` is written via temp-file + move so a concurrent
  reader in another VS Code window's host never sees a half-written file.
- All ids and version numbers from requests are validated before touching the filesystem.

**Routes** (`ArtifactEndpoints.MapArtifactEndpoints`), on VsCodeHost's existing loopback port:
- `GET /artifacts/{id}` — the current version.
- `GET /artifacts/{id}/v/{version}` — a specific retained version.
- 404 for anything unknown or malformed.

**Response headers** (`ArtifactContentPolicy`) — this is the security boundary, see §7:

```
Content-Type: text/html; charset=utf-8          (inline, never Content-Disposition: attachment)
Content-Security-Policy:
  sandbox allow-scripts allow-popups allow-modals allow-downloads;
  default-src 'none';
  script-src 'unsafe-inline' <script CDNs>;
  style-src  'unsafe-inline' <script CDNs> https://fonts.googleapis.com;
  font-src   https://fonts.gstatic.com <script CDNs> data:;
  img-src    data: blob: <script CDNs>;
  media-src  data: blob:;
  connect-src 'none'; frame-src 'none'; object-src 'none';
  base-uri 'none'; form-action 'none'; frame-ancestors 'none'
X-Content-Type-Options: nosniff
Referrer-Policy: no-referrer
Cache-Control: no-store                          (a refresh always shows the latest version)
```

Allowlisted CDNs: `https://cdnjs.cloudflare.com`, `https://cdn.jsdelivr.net`, `https://unpkg.com`,
`https://cdn.tailwindcss.com`, `https://code.jquery.com`.

## 5. The `artifact-design` skill

- Source: `src/Litos.Artifacts/Skills/artifact-design/SKILL.md`, compiled in as an embedded
  resource (VsCodeHost is a compressed single-file exe, so loose content files are not an option).
- `ArtifactSkills.EnsureInstalled()` writes it to `~/.litos/builtin-skills/artifact-design/SKILL.md`
  (only when the content differs), because `SkillTool` reads skill bodies from disk.
- `ArtifactSkills.UserRoots()` = the normal user roots (`~/.litos/skills`, `~/.claude/skills`) +
  `~/.litos/builtin-skills` **last**, i.e. lowest priority: a user's or project's own
  `artifact-design` skill overrides the built-in one.
- VsCodeHost uses those roots for both its DI-registered `ISkillDiscovery` (system-prompt catalog
  + the model's `skill` tool) and `SkillsEndpoints` (the `/skills` UI), so the skill appears
  everywhere in this face and nowhere else — Litos.Gui/Console don't have the `artifact` tool, so
  they must not advertise a skill for it. The built-in root is therefore not added to
  `SkillDiscovery.DefaultUserRoots()`.
- Content: page contract (`<title>`, colour tokens with light/dark, explicit body background,
  16px gutter, no horizontal scroll at phone width), what the sandbox blocks (no `fetch`/XHR/
  WebSocket, no form posts, storage throws in an opaque origin → wrap in try/catch, no `eval`),
  CDN allowlist with exact pinned versions, where to write the file (a path the user names,
  otherwise `<system temp>/litos-artifacts/<slug>.html`), and the publish → edit → republish loop.

## 6. Surviving compaction

A skill's instructions reach the model only as the `skill` tool's result in the transcript.
Compaction (`Compactor`) replaces everything before the cut with an LLM-written summary, so a
skill loaded early in a long session would silently vanish — and with it every artifact rule.

**Fix (generic, all skills, all faces):** `Compactor` re-attaches skills verbatim.
- Before summarizing, `RetainedSkills.Collect` scans the messages being cut for successful
  `skill` tool calls (a `ToolUseBlock` named `skill` whose matching `ToolResultBlock` is not an
  error) and for skills already re-attached in an earlier compaction summary.
- The latest body per skill name wins; at most 5 skills are kept, most recently loaded first,
  within a character budget of **half the kept tail** (`KeepRecentTokens × 2` chars at ~4
  chars/token, capped at 60,000): 8K chars on a 16K window, 16K on 32K, 40K on 200K, 60K on 1M.
  A fixed budget would leave a small local model's context at the compaction trigger right after
  compacting (compacting again every round) and add a fixed cost to every compaction elsewhere
  (the factory worker uses Compactor too). Skills that don't fit are named in the summary so the
  model can reload them.
- They are appended to the new `CompactionSummaryBlock.Summary` inside a delimited
  `<litos-retained-skills>` section. Being inside the summary block means:
  - every provider already renders it (as text) — no provider changes;
  - it persists with the checkpoint in the JSONL and survives `Transcript.LoadAsync` replay;
  - the next compaction finds it again (it parses the section back out of the earlier summary) —
    so a skill survives any number of compactions;
  - the old retained section is stripped from the text sent to the summarizer, so the LLM never
    paraphrases or duplicates it.
- Skills loaded *after* the cut are still in the kept tail and need nothing.
- Limitation: a skill loaded from inside a PTC kernel script (`run_kernel_code`) is not visible
  as a `skill` tool call and is not retained.

Belt and braces: the `artifact` tool's description also says "if the artifact-design guidance is
not in your context, load it with the skill tool before writing or rewriting a page."

## 7. Security model

Agent-authored HTML is treated as **hostile**: the model reads untrusted repositories and web
pages and can be prompt-injected into writing script.

The risk: VsCodeHost serves every endpoint — `/config/keys`, `/sessions/{id}/turns` (which runs
an agent with a shell tool), `/mcp/servers` — unauthenticated on `127.0.0.1:{port}`. A page served
*same-origin* with script able to call those would be remote code execution.

Mitigation (all from the response headers in §4):
- `sandbox` without `allow-same-origin` makes the document an **opaque origin** — even served from
  `127.0.0.1:{port}`, its script is not same-origin with the host API, and cannot read responses.
- `connect-src 'none'` blocks `fetch`/XHR/WebSocket/EventSource/beacons to *anywhere*, including
  the host's own endpoints (so not even blind no-cors POSTs).
- `form-action 'none'` and no `allow-forms` block form posts; `frame-src 'none'` blocks embedding
  the host's pages; `frame-ancestors 'none'` stops other pages framing the artifact.
- Popups are allowed (links opening a new tab) but **not** `allow-popups-to-escape-sandbox`: a
  popup inherits the sandbox, so the page can't pass its own URL — the host's port — to an
  unsandboxed page that could then try DNS rebinding against the host.
- Host mutation endpoints are all POST with JSON bodies, so top-level navigation (which a sandboxed
  page can still do) can at most issue GETs.

Accepted residual risks (documented, not fixed in v1):
- Script from an allowlisted CDN can be any published npm package (jsdelivr/unpkg), and a page can
  always navigate itself elsewhere — so an artifact can leak *its own content*. It cannot reach the
  host, keys or other artifacts.
- The host's pre-existing lack of auth (DNS-rebinding exposure) is unchanged by this feature. A
  Host-header allowlist on VsCodeHost (`127.0.0.1`/`localhost` only) would close it for every
  endpoint and is the recommended follow-up, independent of artifacts.
- Re-attached skills (§6) sit in the user-role summary message rather than a tool result. A
  project-level skill from an untrusted repository therefore carries over with the same weight
  it had when it was first loaded; it gains no new reach.

Isolation rests on the browser honouring CSP `sandbox` on a top-level document (supported by all
current Chromium, Firefox and Safari). A dedicated artifact-only origin (Approach B) would add a
second layer and remains the upgrade path.

## 8. Files

New — `src/Litos.Artifacts/`
- `Litos.Artifacts.csproj` — class library, `FrameworkReference Microsoft.AspNetCore.App`,
  references Litos.Agent + Litos.Tools; embeds `Skills/artifact-design/SKILL.md` with an explicit
  `LogicalName` (a second built-in skill needs its own `EmbeddedResource` entry).
- `ArtifactStore.cs`, `ArtifactMeta.cs` — §4 store.
- `ArtifactTool.cs` — §3 tool.
- `ArtifactEndpoints.cs`, `ArtifactContentPolicy.cs` — §4 routes and headers.
- `ArtifactSkills.cs`, `Skills/artifact-design/SKILL.md` — §5.
- `ArtifactsServiceCollectionExtensions.cs` — `AddLitosArtifacts(baseUrl)`.

New — `src/Litos.Agent/Session/RetainedSkills.cs` — §6.

Changed
- `src/Litos.Agent/Session/Compactor.cs` — re-attach retained skills (§6).
- `src/Litos.Tools/Skills/SkillTool.cs` — uses the shared skill-tool name constant.
- `src/Litos.VsCodeHost/Program.cs`, `Litos.VsCodeHost.csproj`, `Skills/SkillsEndpoints.cs`.
- `src/Litos.VsCode/src/extension.ts` → link rewrite moved to a new testable `shareLinks.ts`,
  extended to `/artifacts/{id}[/v/{n}]`, and now limited to loopback hosts (the old inline
  version would also have re-pointed a model-written `https://example.com/files/<hex>` link).
- `src/Litos.Gui/MainWindow.axaml.cs` — the history view strips the retained-skills section from
  a compaction summary before showing it (§6 applies to every face).
- `LitosAiAgent.slnx`.

## 9. Tests

- `tests/Litos.Artifacts.Tests` (new):
  - `ArtifactStoreTests` — create; republish by path → same id, version 2; republish by id;
    unknown id; path case-insensitivity on Windows; version pruning past 50; read specific/latest/
    pruned version; list ordering; delete; malformed ids rejected; meta survives a fresh store
    instance.
  - `ArtifactToolTests` — every action's success and error paths; missing/oversized/non-HTML file;
    title precedence; URL format and that it ends the line (so `linkify` doesn't swallow
    punctuation); read truncation.
  - `ArtifactEndpointsTests` — real Kestrel on loopback port 0: 200 with every §4 header; specific
    version; 404 for unknown/malformed/pruned; no `Content-Disposition`.
  - `ArtifactContentPolicyTests` — sandbox without `allow-same-origin`, `connect-src 'none'`,
    `form-action 'none'`, every CDN present, no `unsafe-eval`.
  - `ArtifactSkillsTests` — installs, idempotent, rewrites stale content, built-in root is last,
    the embedded skill has valid frontmatter named `artifact-design`, user skill overrides it.
- `tests/Litos.Agent.Tests/Session/RetainedSkillsTests.cs` — `RetainedSkillsTests` (collects
  successful skill loads only; latest wins; round-trips verbatim; count and window-scaled budget
  caps) and `CompactorSkillRetentionTests` (survives one and then a second compaction; the old
  section is stripped from the summarizer input; a skill reloaded after the cut isn't carried
  twice; a small window drops an oversized skill; no skills → summary unchanged).
- `src/Litos.VsCode/src/__tests__/shareLinks.test.ts` — rewrites stale `/files/` and
  `/artifacts/{id}[/v/{n}]` links, leaves other URLs alone.

## 10. Later (not v1)

- **Litos.Gui:** reuse `Litos.Artifacts` unchanged; Gui needs a small loopback Kestrel to serve
  the route (it has none) or `Avalonia.Controls.WebView` 12.1.x (licence to verify).
- **Software Factory:** side chats are built (§11). Work runs (Implement/Repair/Review…), an
  in-app viewer, deleting, an Admin switch and the `@factory` path are not.
- Approach B (dedicated artifact origin), runtime capabilities, Markdown artifacts.

## 11. Software Factory side chats (v1)

A person's plain message on a task thread — the side chat, never `@factory` (m2-architecture.md
§5) — can now be answered with a page as well as prose.

### 11.1 Decisions (agreed 2026-11-05)

| Question | Decision |
|---|---|
| Which runs | Chat runs only (`TurnKind.Chat`). Work, review, spec, scan and reflection turns don't get the tool |
| Viewer | A new browser tab, from a card under the answer. No in-app viewer |
| Design guidance | On demand: the tool's `guide` action returns the design rules. Not in the system prompt |
| Delete | None in v1: artifacts stay with the thread (and go with it) |
| Setting | None: always on, no Admin switch |
| Storage | Postgres (mine, not asked): pages are small text, so they ride the existing database backups with no files, sweeper or backup change |

### 11.2 What a person sees

1. They ask in the thread: "Draw me a diagram of how the export pipeline fits together."
2. The chat answer is posted as usual, and under it a card per page it published:
   **"Export Pipeline" · version 1 · Open ↗**. Open goes to a new tab.
3. A follow-up ("make the boxes for failed steps red") is a new chat run. Its brief lists the
   thread's pages, so the agent reads the current version and publishes version 2 of the same
   artifact. The new answer's card shows version 2; the earlier answer's card still opens
   version 1 and says a newer one exists.
4. Anyone who can see the thread can open its pages; no one else can (404, as for every
   project-scoped thing).

### 11.3 The tool (worker, chat turns only)

`artifact`, actions:

| action | arguments | result |
|---|---|---|
| `guide` | — | The design rules (§5's skill without its file workflow). The description says to call it once before writing a page |
| `publish` | `html` (required), `title`, `id` | Stores a version. With `id`, a new version of that artifact on this thread; without, a new artifact |
| `read` | `id` (required), `version` | The stored HTML, truncated past 100,000 characters |
| `list` | — | The thread's artifacts: id, title, latest version |

Unlike VS Code, the page is passed inline: chat turns have no `write_file`. Title precedence is
the same (argument, then `<title>`, then "Untitled page"). The model is told the page appears as a
card under its answer, so it needn't paste a link.

### 11.4 Worker → host

`POST /internal/runs/{runId}/artifacts` (publish), `GET …/artifacts` (list) and
`GET …/artifacts/{id}?version=` (read), authenticated by the run secret like every callback, and
refused unless the run is a **chat run** — a non-terminal call, outside the submission path. The
host scopes everything to the run's own thread: an `id` from another thread is "not found".
Limits: 1 MB of UTF-8 per version, 20 MB of versions per thread.

### 11.5 Store

- `thread_artifacts`: Id, ThreadId, ProjectId, Title, CurrentVersion, CreatedBy (the asker),
  CreatedAt, UpdatedAt. Cascade-deleted with the thread.
- `thread_artifact_versions`: (ArtifactId, Version) key, Html, SizeBytes, RunId, CreatedAt.
- `ProjectScoped.Artifact` → the artifact's project, for the access filter.
- `FinishChatRunAsync` records the versions that run published on the answer it posts (or on the
  failure note, if the answer failed after publishing): payload `{"artifacts":[{"id","version"}]}`.

### 11.6 Browser API

- `GET /api/artifacts/{id}` and `GET /api/artifacts/{id}/v/{version}` serve the page with the
  shared `ArtifactContentPolicy` headers, behind `RequireProjectAccess(ProjectScoped.Artifact)`.
- The thread view's messages carry `artifacts: [{ id, title, version, latestVersion, url, latestUrl }]`.

**Security.** The factory UI and API share one origin with the `factory.session` cookie
(SameSite=Strict, HttpOnly). The CSP `sandbox` (no `allow-same-origin`) gives the page an opaque
origin, so a request from it is cross-site: the cookie isn't sent, and `connect-src 'none'` blocks
it anyway. State-changing API calls also need the `X-Factory-Request` header, which an opaque
origin can't send without a preflight the host never grants. Tray access tokens can't read
`/api/artifacts` (they are limited to an allowlist of read routes).

### 11.7 Brief

The chat brief (revision m2.11) gains "Pages published on this thread": each artifact's id,
title and latest version, and one line on updating one by id rather than creating another.

**Kernel sessions (found in the first live test, 2026-11-05).** A thread with PTC on gives the
model `run_kernel_code` alone; every other tool, `artifact` included, is called from inside a
script. On claude-haiku-5-5 the model read "call it with action `guide`" in the brief, called
`artifact` directly, got "Unknown tool 'artifact'" and answered in prose instead. Two fixes:
- the brief and the tool's `guide` show the in-script form, as the completion tools' briefs
  already do: `await artifact("action", "publish", "html", page, "title", "…")`;
- `ToolRegistry` (Litos.Agent, every face): an unknown tool in a kernel-only registry now says
  "this session's only tool is run_kernel_code … call it from inside a script, with its
  arguments as name and value pairs", instead of a bare "Unknown tool".
Re-asked on the same thread, the answer called `guide` then `publish` from kernel code and
published "Version Constant Planner" (5.4 KB: four-step checklist, live semver preview, dark
theme, no network or storage).

### 11.8 Tests

- Core: the chat brief lists the thread's artifacts, and omits the section when there are none.
- Infrastructure (SQLite + Postgres store contract): publish new / new version / unknown or
  other-thread id refused; size caps; read latest/specific/missing version; list; the chat
  answer's payload names that run's versions; `FindProjectIdAsync(ProjectScoped.Artifact)`.
- Host: callbacks refuse a non-chat run, a wrong secret and an oversized page; the browser route
  serves with the policy headers, 404s for a non-member and an unknown version, and refuses a
  tray token; the thread view carries the artifact links.
- Worker: the tool's actions against a fake host, and that only chat turns get it.
- Web (vitest): the cards render under an answer, open in a new tab, and point to the newest
  version when a newer one exists.
