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
- **Software Factory:** a Postgres-backed store modelled on attachments (`ThreadArtifact` +
  `ArtifactVersion`, `ProjectScoped.Artifact`, backup coverage), a non-terminal worker callback
  `/internal/runs/{runId}/artifacts`, an Artifact card + sandboxed-iframe viewer route, reusing
  `ArtifactContentPolicy` and the skill text. Sharing = project membership; comments = thread chat.
- Approach B (dedicated artifact origin), runtime capabilities, Markdown artifacts.
