# Litos.Console → Litos.Gui Parity Plan

Implementation plan for closing the feature gap between `Litos.Console` (Terminal.Gui TUI face,
currently "work in progress" per the top-level README) and `Litos.Gui` (Avalonia desktop face,
currently the primary/default face). Written as a reference document before implementation begins —
no code changes have been made yet.

**Hard constraint: zero changes to `Litos.Gui`.** Every item below is additive to `Litos.Console`
only. Where Gui has logic Console needs (self-update, MCP prompt binding, Win32 job-object cleanup),
that logic is **copied and adapted into Console**, not extracted into a shared project that Gui
would need to reference. This matches the existing convention in the codebase — `AttachHandler.cs`,
`MentionParser.cs`, and `ImageMedia.cs` already exist as independent, deliberately-duplicated copies
in both `src/Litos.Console/` and `src/Litos.Gui/`.

## Why these gaps exist

Both faces sit on the same `Litos.Host`/`Litos.Agent` composition root and are meant to be additive/
independent per the brain/environment/face architecture (`ReadMe_AgentDesign.md` §2, §9). `Litos.Gui`
has been the actively-developed primary face; `Litos.Console` was feature-complete relative to Gui
only up through an earlier milestone (§7.7) and has been stable since while Gui gained MCP support,
skills-by-name, `/reflect`, `/keys`, and self-update. This plan brings Console back to parity.

## Confirmed scope

**In scope** (this document): MCP server support (`/mcp`), `/skill <name>`, `/keys`, `/reflect`,
`/update` (self-update), `/context`, real markdown rendering, removing Console's tool-approval gating
to match Gui's auto-approve model, plus two bug fixes found during investigation (stale `@mention`
file-index cache, a debug-diagnostic window-title leak).

**Out of scope** (tracked separately, not addressed here): `Litos.Api`'s `share_file` tool; session
delete (not implemented in the shared `ITranscriptStore` for any face); `Litos.Api`'s per-user
Postgres/JWT auth (not applicable to single-user desktop/TUI faces).

**Revised direction on tool approval (supersedes this plan's original stance):** the original version
of this plan treated `Litos.Gui`'s unconditionally-auto-approving `GuiApprovalGate` as a Gui-side gap
to fix separately, while keeping Console's real interactive `ApprovalDialog` for built-in tools and
only exempting MCP calls from it. Per explicit direction, this is now reversed: **Console's built-in
tools should have no approval gating either, matching Gui exactly.** Auto-approve becomes the model
for every tool call in Console — built-in and MCP alike, interactive and non-interactive — not just
MCP. See Slice 0.4 for the concrete change; the `ApprovalDialog`/`DiffView`/`NonInteractiveApprovalGate`
code itself is left in place (dead, unused) rather than deleted, since it remains directly relevant
should approval gating be reintroduced later as an opt-in.

## Foundational constraints (verified against actual code/assemblies, not assumed)

These four facts drive every design decision below.

1. **Every new modal dialog must use the `AttachDialog` async pattern — never `PickerDialog.Pick`.**
   `Terminal/AttachDialog.cs` documents three approaches to showing a dialog from inside active key
   dispatch, empirically tested: `app.Run(dialog, null)` nests a second run loop and silently breaks
   `Invoke`/`TimedEvents` afterward (console output and slash commands stop working); `app.Begin` +
   blocking `.GetAwaiter().GetResult()` hard-deadlocks. The only working shape is `app.Begin(dialog)`
   + an `app.Iteration` handler polling `dialog.StopRequested` + a non-blocking `TaskCompletionSource`
   the caller `await`s. `PickerDialog<T>.Pick`'s own doc comment admits its on-thread branch still
   carries this bug, unfixed. Every new dialog in this plan (`McpServersDialog`, `ReflectDialog`,
   `ContextBreakdownDialog`, `ApiKeysDialog`'s `/keys` path) is reached from active key dispatch, so
   all of them use the `AttachDialog` shape.

2. **`Terminal.Gui.Views.Markdown` genuinely exists and is Markdig-backed** (confirmed by reading the
   pinned `Terminal.Gui 2.0.0-rc.64` assembly XML docs directly, not assumed from the package name).
   It supports headings, emphasis, strong, inline code, code blocks, tables, and links via a real
   `MarkdownPipeline`. But it is a read-only styled `View`, not a `TextView` — it has no `Lines`,
   `MoveEnd()`, or `SetContentSize()`, which `TranscriptView`'s hard-won scroll-fix logic depends on.
   Re-assigning `.Text` "triggers reparsing, re-layout, and a redraw" per the type's own docs, so
   using it for the live streaming path (one reparse per token) is not viable.

3. **Terminal.Gui's own clipboard abstraction cannot deliver a raw bitmap — but the OS clipboard can
   be read directly, bypassing it.** `IClipboard`'s entire surface is string-typed: `GetClipboardData
   ()`, `SetClipboardData(string)`, `TryGetClipboardData(out string)` — no image/bitmap/DIB member
   exists anywhere in the assembly, and no terminal emulator (Windows Terminal, PowerShell, cmd, iTerm2,
   Terminal.app) delivers a raw bitmap as a Ctrl+V input *event* — paste is always intercepted by the
   terminal and only text is ever forwarded to the running process. This is universal, not a Terminal.
   Gui-specific limitation — confirmed by researching how Claude Code (a directly comparable terminal
   agent) handles the same problem: it still has native-Windows-terminal raw-bitmap paste as an open,
   unsolved issue as of mid-2026, and on Linux it shells out to `xclip`/`wl-paste` rather than using
   any terminal-level clipboard API.
   However, going *around* Terminal.Gui straight to the OS clipboard is a real, working option:
   - **Windows**: direct Win32 P/Invoke — `OpenClipboard(IntPtr.Zero)` + `GetClipboardData(CF_DIBV5)`
     + `CloseClipboard()`. Reading (unlike writing) does not require an `HWND` or an STA thread, so
     this needs no WinForms/System.Drawing dependency and no shell-out to a helper process.
   - **macOS**: shell out to `osascript -e 'the clipboard as «class PNGf»'` (or bundle a small native
     helper) to extract PNG data from the pasteboard.
   - **Linux**: shell out to `xclip -selection clipboard -t image/png -o` (X11) or `wl-paste -t
     image/png` (Wayland) — this is exactly Claude Code's own approach.
   **Scope decision**: build direct bitmap paste for **Windows, macOS, and Linux** — all three of
   Console's targeted platforms. See Slice 3.3 for the concrete design.

4. **MCP requires Console's `ToolRegistry` to move from a one-time startup snapshot to a per-turn
   rebuild.** `Program.cs` currently calls `ToolRegistryFactory.Create()` once at startup with an
   explicit comment that dynamic tool discovery is out of scope. MCP tools are added/removed live via
   `/mcp`, so the registry must be rebuilt every turn (as Gui already does) for new/removed servers to
   take effect without an app restart. This is a prerequisite for all of MCP and is captured as its
   own early slice.

## Delivery plan

Organized as weekly, independently-shippable slices, ordered by risk and dependency rather than by
the numbered feature list — the goal is to prove the two riskiest unknowns (dialog threading pattern,
`Markdown` view behavior under this app's `AppModel.Inline` renderer) on low-stakes work before
attempting the largest feature (MCP).

### Phase 0 — Week 1: cleanup + quick wins (no dependencies)

**Slice 0.1 — Bug fixes**
- Delete `TranscriptView.DebugScrollState()` (`Terminal/TranscriptView.cs`) and its call site at
  `Program.cs` (`litosApp.Title = litosApp.Transcript.DebugScrollState();`, fired on every
  `MessageCompleted`). This is a labeled "temporary diagnostic — remove once root-caused" leak that
  currently overwrites the window title with raw viewport/line-count debug text after every reply.
  Optionally replace with a real title: `$"Litos — {activeProviderName}/{model}"`.
- Wire `MentionAutocomplete.InvalidateCache()`, which is never called today — the `@mention` file
  index is built once per process and never refreshed, so files created mid-session never appear in
  completions, and after `/resume`/`/branch` into a different working directory the index still
  serves the *old* directory's files. Expose an `InvalidateMentionCache()` passthrough from
  `Composer`/`LitosApp` and call it at the three sites that reassign `transcript`: `/new`, `/resume`,
  `/branch`.

**Slice 0.2 — `/skill <name>`**
- Console has `/skills` (list) but no by-name loader. Port `Litos.Gui`'s `LoadSkillAsync` pattern:
  construct `new SkillTool(new SkillDiscovery(transcript.WorkingDirectory))` — scoped to the *live
  session* working directory, not the DI singleton, matching why Gui does the same — invoke it with
  `{ name }`, and on success push the result into Console's existing `pendingAttachments` list so
  `BuildTurnContent` folds it into the next message. No new files; one new `case` in
  `TryHandleSlashCommandAsync`.

**Slice 0.3 — Per-turn `ToolRegistry` rebuild** (the MCP prerequisite)
- Replace the startup-captured `toolRegistry`/`loop` in `Program.cs` with a per-turn rebuild:
  `toolRegistryFactory.Create()` then `agentLoopFactory.Create(chatProvider, toolRegistry)`,
  called immediately before each turn (interactive and non-interactive paths). Simplify `/provider`,
  which currently rebuilds the loop from a stale registry — it no longer needs to.
- With zero `IToolSource` registered yet (MCP lands later), behavior is provably unchanged — this
  slice is safe to ship and verify in complete isolation before any MCP code exists.
- Update the now-false comment in `Program.cs` claiming dynamic tool discovery is out of scope.

**Slice 0.4 — Remove tool-approval gating (match `Litos.Gui`'s auto-approve model)**
- Replace the `IToolApprovalGate` registration in `Program.cs` — currently `ApprovalDialog` when
  interactive, `NonInteractiveApprovalGate` otherwise — with a single auto-approving gate used in
  both modes: a small `AutoApprovalGate : IToolApprovalGate` (Console-local copy of the same one-line
  shape as Gui's `GuiApprovalGate`, per the no-Gui-changes rule) whose `RequestAsync` unconditionally
  returns `ApprovalDecision.Approve`. This applies to every built-in tool (`shell`, `write_file`,
  `edit_file`) exactly as it already does for MCP tools in Gui — no dialog, no console `y/N/a` prompt,
  no distinction between interactive and non-interactive runs.
- `ApprovalDialog.cs`, `DiffView.cs`, and `NonInteractiveApprovalGate` are **left in the codebase,
  unused** rather than deleted — they're real, working implementations (`DiffView` in particular is
  reused as-is by `/reflect`, Slice 1.3) and this keeps the door open to reintroducing gating later as
  an opt-in without rebuilding it from scratch. `PickerDialog<T>`-pattern dialogs built later in this
  plan do not depend on `ApprovalDialog` for anything other than `DiffView`'s reuse.
- Sequenced here — immediately after the per-turn registry rebuild and before any of the new dialogs
  — because it's a single, isolated, low-risk change to `Program.cs`'s DI wiring with no dependents,
  and because Slice 2.1 (MCP wiring) previously had to special-case an MCP-only auto-approve gate
  wrapper; with this slice landing first, MCP simply reuses the same single gate registered here,
  removing that special case entirely (see Slice 2.1, revised below).
- **User-visible behavior change worth calling out plainly**: after this slice, Console will run shell
  commands and file writes without confirmation, same as Gui does today. This is an explicit, accepted
  product decision, not an oversight — recorded so it isn't mistaken for a regression later.

### Phase 1 — Week 1-2: prove the dialog pattern on low-risk dialogs

**Slice 1.1 — `/keys` (+ `SetupWizardDialog` fixes)**
- Generalize `SetupWizardDialog` into a dual-mode `ApiKeysDialog`: `RunFirstRun(app)` (unchanged
  on-thread `app.Run`, since no outer loop is active yet at first-run) and a new `ShowAsync(app)` for
  `/keys` (must use the `AttachDialog` pattern — an outer loop *is* active mid-session).
- Fix two real gaps while touching this file: the provider list omits `local` and `tavily` entirely
  (a user whose only provider is a local OpenAI-compatible server has no first-run path to configure
  it), and — found during investigation — the wizard's `LitosConfig` construction drops
  `LocalBaseUrl`/`ShellCommandTimeoutSeconds` from any pre-existing config, silently erasing a
  previously-configured local server if the wizard is ever re-run. Fix by loading current config and
  applying a `with` expression instead of constructing a fresh `LitosConfig`.
- Persistence policy (Windows → user-scope env var; else `config.json`; blank field = keep existing)
  is ported from Gui's `ApiKeysWindow.TrySave`, copied not shared, per the no-Gui-changes rule.
- This is deliberately the *first* new dialog built: it's a static form with no async work inside it,
  making it the cheapest place to get the `Begin`/`Iteration`/`TaskCompletionSource` shape right
  before attempting it on something more complex.

**Slice 1.2 — `/context`**
- `ContextBreakdown.Compute(transcript, systemPrompt, toolSchemas)` already exists in `Litos.Agent`
  and needs no changes. New `Terminal/ContextBreakdownDialog.cs`: a pure `RenderLines(...)` function
  (unit-testable without Terminal.Gui) producing a text table — category, token count, percentage,
  and a block-character bar (`█`/`░`) standing in for Gui's segmented `Border`-width bar — displayed
  in a read-only `TextView` inside a `Dialog`.
- Needs a `contextLength` Console doesn't currently track; resolve via `ModelContextWindows.Resolve
  (model)` (the same fallback Gui uses) rather than threading a new field through every
  `/model`/`/provider` switch.
- Chosen second because it's fully read-only — no mutation, no async work inside the dialog itself.

**Slice 1.3 — `/reflect`**
- `Reflector.ReflectAsync(...)` already exists in `Litos.Agent` and needs no changes — only the
  review-before-write UI is new. New `Terminal/ReflectDialog.cs`: an editable `TextView` (Terminal.
  Gui's `TextView` is a full multi-line editor, so this is closer to Gui's editable box than expected)
  pre-filled with the proposed `AGENTS.md` content, plus **Console's own `DiffView`** reused as-is for
  a live, re-rendered-on-keystroke diff against the existing file — Gui had to hand-reimplement
  `DiffView`'s color rules in Avalonia; here the real thing is available directly. Requires a small
  `DiffView.SetDiff(string)` addition so the view can be updated in place instead of recreated per
  keystroke.
- Never writes without explicit confirmation; non-interactive mode prints the proposed diff and
  refuses to write unattended.

### Phase 2 — Week 3-4: MCP (the large feature, split into three sub-slices)

**Slice 2.1 — MCP wiring + per-turn tool availability**
- Add a `Litos.Tools.Mcp` project reference to `Litos.Console.csproj` (the sanctioned per-face
  exception — MCP is deliberately not folded into `Litos.Host`).
- Construct `McpConfigStore`/`McpToolProvider` by hand before `BuildServiceProvider()` (the provider
  needs the approval-gate instance first), register an `IToolSource` wrapping `McpToolProvider` so
  Slice 0.3's per-turn rebuild picks up MCP tools automatically, and fire-and-forget the startup
  connection handshake (30s per-server timeout) so the TUI never blocks on it.
- **Approval gate: reuse the single `AutoApprovalGate` from Slice 0.4 — no MCP-specific handling
  needed.** With Slice 0.4 landed, Console already has exactly one `IToolApprovalGate` registered
  (auto-approving everything), so `McpToolProvider`'s constructor is simply handed that same instance
  — MCP tool calls get the identical no-prompt behavior as built-in tools, with no special-casing
  required. This matches Gui's own wiring shape (`GuiApprovalGate` is the one gate used for both
  built-in and MCP tools there too) and is the reason Slice 0.4 is sequenced before this one.
  **Do not** use `McpAwareApprovalGate` — that remains an `Litos.Api`-only concept
  (`PendingApprovalStore`, deny-by-default `DefaultPermission` semantics) that doesn't fit either
  face's model here. Set `DefaultPermission: Full` in the add-server form for the same reason Gui's
  form does: the field exists in `McpServerDefinition` but nothing ever consults it once the gate
  auto-approves, so the UI doesn't surface Deny/Ask/Full controls at all — matching
  `McpServersWindow`'s own documented reasoning for hiding them.
- Port `Win32JobObject.cs` verbatim into `Litos.Console` (self-contained, no Avalonia dependency) and
  call it at the top of `Main`, exactly as Gui does — without it, a crashed/killed Console orphans
  `npx`-spawned MCP stdio child processes on Windows.
- Verifiable in isolation before any UI exists: hand-edit `~/.litos/mcp.json`, confirm a configured
  server's tools reach the model on the next turn.

**Slice 2.2 — `/mcp` server management dialog**
- New `Terminal/McpServersDialog.cs`: a `Dialog<bool>` with an embedded add-server form (name,
  stdio/HTTP transport toggle, command+args or URL, enabled checkbox) plus a server list with status,
  enable/disable, remove, and refresh — flattened into `ListView` rows via a pure, testable
  `BuildRows(...)` function (mirroring the pure/UI-free split already used by `ComposerState` and
  Gui's `McpServersWindow.BuildDefinition`) since Terminal.Gui has no native expandable-list widget.
  Tool/prompt browsing per server is click-to-expand via `ListView.OpenSelectedItem`.
- The riskiest single slice in this plan: it's the first dialog with *async work inside it*
  (`RefreshAsync` after add/remove/toggle) — strictly fire-and-forget, with the re-render marshaled
  back via `app.Invoke`, never blocking, to avoid the deadlock shape `AttachDialog`'s doc warns about.
  Deliberately sequenced after Slices 1.1–1.3 so the simpler async-dialog pattern is already proven.

**Slice 2.3 — MCP prompts as dynamic slash commands**
- Copy `McpPromptArguments.cs` and `McpPromptContentConverter.cs` from `Litos.Gui` into
  `Litos.Console` verbatim (both are pure, UI-free, dependency-free logic — matching the existing
  `AttachHandler`/`MentionParser` duplication convention, and never touching the Gui originals).
- Extend the slash-command fallback: an unrecognized `/foo` is checked against
  `McpToolProvider.Prompts` (`/{server}__{prompt}`) before reporting "Unknown command," running the
  fetched prompt text through the exact same turn-execution path as a typed message.
- Since Console has no command-menu popup (unlike Gui's `CommandMenuPopup`) for discovering available
  prompt names, list them in the `/mcp` dialog's prompt-browser section (already built in Slice 2.2)
  as the discovery mechanism.

### Phase 3 — Trailing: markdown rendering and self-update

**Slice 3.1 — Real markdown rendering**
- `MarkdownRenderer.ToDisplayText` exists today but is dead code — never called anywhere; replies
  render as raw unstyled text despite the class's doc comment implying it's wired up.
- **Step A (small, low-risk, ships first):** route the live streaming path through
  `MarkdownRenderer.ToDisplayText` for its LaTeX-to-Unicode rewriting alone (`\alpha` → `α`) — a
  one-line call-site change with immediate visible value and zero regression risk, independent of the
  `Markdown` view question entirely.
  Then add a way to view a completed reply through a real `Markdown` view inside a `Dialog` (a `/md`-
  style command or keybinding showing the last reply styled) — small, isolated, and the cheapest way
  to confirm `Terminal.Gui.Views.Markdown` actually renders correctly under this app's `AppModel.
  Inline` driver before committing to anything larger. This codebase has a documented history of
  `AppModel.Inline` surprises (`TranscriptView`'s own scroll-fix saga), so this is treated as a real
  unknown, not a formality.
- **Step B (larger, conditional on Step A succeeding):** restructure `TranscriptView` so committed
  assistant replies render through `Markdown` while the in-flight stream still uses a cheap plain
  `TextView` — swapped once on turn completion, mirroring Gui's own two-tier approach exactly
  (`ReadMe_AgentDesign.md` §7.7: stream into a plain `TextBlock`, swap to a real `MarkdownViewer`
  once, on completion). Only attempted if Step A's isolated dialog proves the view behaves well;
  otherwise Console ships with LaTeX rewriting and the on-demand `/md` view as the final state, no
  regression to today's behavior either way.

**Slice 3.2 — `/update` self-update**
- Copy the shared, platform-agnostic half of Gui's `SelfUpdater.cs` (GitHub release check, semver
  compare, download+extract) into `Litos.Console`, adapted for Console's own release-asset naming
  (needs verification against whatever release workflow exists for Console before this literal can be
  written — flagged as an open prerequisite, see Risks).
- **Deliberately does not port Gui's relaunch mechanism.** Gui's Windows path generates a PowerShell
  helper and uses `CREATE_BREAKAWAY_FROM_JOB` P/Invoke solely because it runs inside a Win32 Job
  Object (for MCP child-process cleanup). Console's relaunch is simplified per the confirmed scope:
  rename the running exe aside (`target.exe` → `target.exe.old` — legal on Windows even while
  running, since renaming doesn't require the content lock that overwriting does), move the newly
  downloaded exe into place, and tell the user to restart manually. No helper process, no relaunch,
  no `Environment.Exit`. The stale `.old` file is best-effort deleted on the *next* startup rather
  than at update time.
- Sequenced last because it depends on an external prerequisite (a Console release pipeline/asset
  naming convention) that doesn't yet exist and is outside this plan's code changes.

**Slice 3.3 — Clipboard image paste (Windows, macOS, Linux)**
- Bypasses Terminal.Gui's `IClipboard` entirely (it's string-only, see Foundational Constraint 3) and
  reads the OS clipboard directly, matching the real precedent set by Claude Code's own platform-
  specific clipboard handling — including its Linux approach, adopted here as-is.
- **Windows**: a small P/Invoke wrapper — `OpenClipboard(IntPtr.Zero)` → `GetClipboardData
  (CF_DIBV5)` → copy the handle's bytes → `CloseClipboard()`. No `HWND`, no STA thread, and no
  WinForms/System.Drawing dependency required for *reading* (that requirement applies to *writing*
  the clipboard, not reading it). Decode the returned DIBV5 buffer to a PNG/bytes suitable for
  `AttachHandler`'s existing image-attachment path (native `ImageBlock` vision content, same as a
  file-based image attach).
- **macOS**: shell out to `osascript -e 'the clipboard as «class PNGf»'`, capturing stdout as raw PNG
  bytes; treat a non-zero exit / empty output as "no image on clipboard" rather than an error.
- **Linux**: shell out to the session's clipboard tool, detected rather than assumed — try
  `wl-paste -l` first (Wayland; also gate on `$WAYLAND_DISPLAY` being set) to list MIME types and
  extract via `wl-paste -t image/png`, falling back to `xclip -selection clipboard -t TARGETS -o`
  (X11) filtered for an `image/(png|jpeg|jpg|gif|webp|bmp)` target and then `xclip -selection
  clipboard -t image/png -o`. If neither binary is present, treat it as "no image on clipboard" and
  fall through to text paste — per the Claude Code issue found during research (#29204), silently
  falling through rather than erroring is itself a known rough edge worth avoiding, so surface a
  one-line hint ("install xclip or wl-clipboard for image paste") the first time a paste attempt on
  Linux finds neither tool, rather than staying silent about the missing dependency.
- **Trigger**: bind Ctrl+V in `Composer`'s key handler. Since Terminal.Gui's own clipboard read only
  ever returns text, first attempt the OS-level image read; if it yields no image, fall back to
  Terminal.Gui's normal string paste (unchanged today's behavior) so text paste is never regressed.
- Platform-gated (`OperatingSystem.IsWindows()`/`IsMacOS()`/`IsLinux()`) so each path is isolated and
  carries no risk to the existing paste behavior on another platform.
- The file-path-paste fallback (copying an image file's path via a file manager, which resolves
  through the existing `AttachHandler.AttachPathAsync`) continues to work everywhere regardless, as a
  second line of defense if the OS-level image read fails for any reason (missing tool, permission,
  unsupported clipboard format).

## Summary table

| Slice | Item | Size | Risk | Depends on |
|---|---|---|---|---|
| 0.1 | Debug-title leak + stale mention cache | S | Low | — |
| 0.2 | `/skill <name>` | S | Low | — |
| 0.3 | Per-turn `ToolRegistry` rebuild | S | Low-Med | — |
| 0.4 | Remove tool-approval gating (auto-approve) | S | Low | — |
| 1.1 | `/keys` + SetupWizard fixes | S | Low | — |
| 1.2 | `/context` | S | Low | 0.3 |
| 1.3 | `/reflect` | S-M | Low | — |
| 2.1 | MCP wiring | M | Med | 0.3, 0.4 |
| 2.2 | `/mcp` server management dialog | L | Med-High | 2.1 |
| 2.3 | MCP prompts as commands | M | Low-Med | 2.1, benefits from 2.2 |
| 3.1 | Markdown rendering | S then M | Med (Step B only) | — |
| 3.2 | `/update` | M | Med (external prereq) | — |
| 3.3 | Clipboard image paste (Windows, macOS, Linux) | S-M | Low-Med (3 platform paths) | — |

Clipboard image paste now covers all three of Console's target platforms — Windows (direct Win32
P/Invoke), macOS (`osascript`), and Linux (`xclip`/`wl-paste`, matching Claude Code's own approach).
The file-path-paste behavior (pasting a copied image file's path, which resolves through the existing
`AttachHandler`) remains as a fallback everywhere regardless of which OS-level path is available.

## Risks / open items to resolve before or during implementation

1. **`Markdown` view behavior under `AppModel.Inline` is unverified.** The type's existence and API
   are confirmed from the assembly directly; its actual rendering/scroll behavior in this specific
   app is not. Slice 3.1 Step A exists specifically to answer this cheaply before Step B is attempted.
2. **Console's release-asset naming for `/update` is unknown.** No Console release workflow was found
   during investigation (only a Gui one). This needs to be confirmed — or a release pipeline added —
   before Slice 3.2's `IsCurrentPlatformAsset` literal can be written correctly.
3. **`ISystemPromptProvider`'s exact call signature** (needed by `/context` to build the system-prompt
   argument for `ContextBreakdown.Compute`) should be double-checked against `Litos.Agent` at
   implementation time rather than assumed from Gui's call site.
4. ~~MCP approval-dialog volume~~ — **superseded twice over.** The concern was originally about a
   run of `ApprovalDialog` prompts during an MCP-heavy turn; it no longer applies for two independent
   reasons now layered on top of each other. First, MCP tool calls were exempted from approval gating
   specifically (an earlier revision of this plan). Second, and superseding that, **all** tool-approval
   gating in Console — built-in tools included — is removed in Slice 0.4, so there is no gate left to
   produce prompt volume for *any* tool, MCP or otherwise. Console's built-in tools (`shell`,
   `write_file`, `edit_file`) and MCP tool calls now behave identically: no confirmation, no dialog,
   full parity with `Litos.Gui`'s `GuiApprovalGate`. This is an explicit, accepted product decision —
   recorded here so the removal of `ApprovalDialog` from the active path isn't later mistaken for a
   regression rather than a deliberate parity change.
