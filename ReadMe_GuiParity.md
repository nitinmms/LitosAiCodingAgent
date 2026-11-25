# Litos.Gui ↔ VS Code extension parity

Brings Litos.Gui (the Avalonia desktop app) up to what the VS Code extension offers, by sharing
code rather than copying it. Built 2026-11-25 on branch `gui-parity`.

## 1. What was compared

Gui was already ahead in places: separate thinking display, `/update`, MCP prompts as slash
commands, folder @-mentions, the reflect diff window, tool rows on resume, Alt+Enter follow-up,
`/kernel-reset`. Editor-only features (several chat panels, VS Code theming, Reload Window) don't
apply. The gaps that did apply, and what closed them:

| # | Gap | Now in Gui |
|---|---|---|
| G1 | MCP permission per server (Deny / Ask / Full) | `McpAwareApprovalGate` wraps `GuiApprovalGate`; a Deny/Ask/Full control per server and on the add form (default **Full**, as Gui always behaved); Ask shows an `ApprovalCard` in the transcript |
| G2 | Artifacts | `artifact` tool, built-in `artifact-design` skill, served by `GuiLoopbackServer` |
| G3 | `share_file` download links | Same server, `/files/{token}` |
| G4 | Live context meter during a turn | Meter refreshes on every tool result |
| G5 | Background MCP reconnect | `McpToolRefreshService` started in `Program.cs` |
| G6 | Untrusted-content markers and 20MB cap on attachments | `AttachHandler` (files, URLs, pasted images) |
| G7 | Startup with no usable model; restart after saving keys | Window opens with a "pick a model" notice instead of a silent crash; `/keys` and first run relaunch Litos |
| G8 | Slow tool-call notice | Rows appear at `ToolCallStarted`; after 20s they say which wait it is |
| — | PTC (Programmatic Tool Calling) | Gui's kernel mode, presented as PTC: `/ptc` dialog, `PTC: On/Off/unavailable` status, availability probe, kernel shipped by the installers |

## 2. Decisions

- **Share, don't copy.** Gui now references `Litos.Hosting` and `Litos.Artifacts`. Gui keeps its
  own in-process turn loop. Adopting `AgentWorker` would have meant rewriting `MainWindow`, for no
  user-visible gain.
- **ASP.NET in Gui.** Needed for the loopback server. `Litos.Gui.exe` (win-x64, self-contained
  single-file) grew from 70.8MB to 83.2MB.
- **Built-in tools stay auto-approved.** This is the cross-face convention from
  `ReadMe_ConsoleParityPlan.md`, and VS Code does the same. Only MCP tools are gated.
- **Cancel denies pending approvals.** A gated MCP call waits on `PendingApprovalStore`, which
  ignores the turn's cancellation token. Without this, Escape would leave the turn stuck until the
  10-minute auto-deny.
- **PTC consent lives in the `/ptc` dialog.** The warning sits next to the switch, as in VS Code's
  popup, so turning it on is the informed-consent moment. This replaces the once-per-process
  confirmation dialog. `/kernel-reset` stays, and `/ptc-reset` is added as an alias for it.
- **Live meter uses the real C# bands.** `ContextUsage.Compute` gained `additionalEstimatedTokens`,
  because `AgentLoop` yields `ToolCallResult` *before* appending the result to the transcript.
  There is no third copy of the compaction constants. The CLAUDE.md TypeScript mirror rule applies
  only to `liveContextUsage.ts`.

## 3. Moved into shared projects

| From | To | Now used by |
|---|---|---|
| `VsCodeHost/Turns/UntrustedContent.cs` (+ Api's identical copy) | `Litos.Tools/Attachments/UntrustedContent.cs` | Api, VsCodeHost, Gui |
| `VsCodeHost/Files/{ShareFileTool,SharedFileStore,SharedFileMeta}.cs` + `/files` route | `Litos.Hosting/Files/` (`AddLitosSharedFiles`, `MapSharedFileEndpoints`) | VsCodeHost, Gui |
| `shareLinks.ts` `rewriteStaleHostLink` | `Litos.Hosting/Files/HostLinks.cs` (C# port, same test cases) | Gui (TS copy stays for the extension) |
| VsCodeHost `McpEndpoints` / Gui `McpServersWindow` mutation lambdas | `Litos.Tools.Mcp/McpServerAdmin.cs` | VsCodeHost, Gui |

`ShareFileTool` now takes `Func<string> baseUrl`, the same shape as `ArtifactTool`, so the
`LoopbackBaseUrl` holder class is gone. A bug that predated this work was fixed along the way:
VsCodeHost's add-server endpoint caught `ArgumentException`, but validation throws
`McpConfigValidationException`. A duplicate server name therefore returned a 500 instead of a 400.

## 4. Installs (Windows and macOS)

The requirement was that existing installs keep working exactly as before.

- **macOS:** `publish-macos.sh` publishes `Litos.Kernel.Host` into `Contents/MacOS` and signs it
  explicitly, with hardened runtime and `entitlements.plist`, before the bundle is signed.
  `install.sh` and the in-app updater swap the whole `.app`, so they carry the kernel with no
  change of their own.
- **Windows:** the kernel ships as a **separate release asset**, `Litos.Kernel.Host-{tag}-win-x64.zip`
  (+ `.sha256`). The Gui zip still holds only `Litos.Gui.exe`.
  - The reason: Gui builds already installed take "the first `.exe`" from the
    `Litos.Gui-*-win-x64.zip` they match. A second exe in that zip could have replaced
    `Litos.Gui.exe` with the kernel.
  - The kernel asset's name matches neither that rule nor `install.ps1`'s glob.
  - `install.ps1` and the updater (`SelfUpdater.IsWindowsKernelAsset`) fetch the kernel when the
    release has it.
  - The updater now picks `Litos.Gui.exe` by name, and its helper script moves the kernel in
    before relaunching.
- **Installs already out there:** they keep updating as before. They get the kernel on the first
  update made by a build containing this change, or on a re-run of `install.ps1`. Until then,
  `/ptc` reports "unavailable" rather than failing.

## 5. Verified, and not

- **Verified:**
  - The full solution builds.
  - These test suites pass: Agent 318, Tools 170, Tools.Mcp 43, Hosting 77, VsCodeHost 65,
    Gui 205, Artifacts 94, Host 24, Api 141, Console 119.
  - The VS Code extension type-checks (`tsc --noEmit`) and its Vitest suite passes: 174 passed,
    1 skipped. The skipped test is non-Windows-only. This branch changes no extension TypeScript.
  - A three-way code review (correctness, simplicity, conventions) was run, and its findings were
    fixed:
    - restored history rows and steering-skipped rows no longer show a false "still running"
      notice
    - `/attach` no longer claims a skipped file was attached
    - a first run with an offline model list no longer crashes
    - `/compact` and `/reflect` refuse when no model is set, and an empty model is never saved
    - the kernel exe is picked by name only
    - PTC toggle failures are reported
    - one shared 20MB limit (`AttachmentLimits`)
    - one macOS relaunch helper
    - Console's MCP dialog uses `McpServerAdmin`
  - The timer fixes sit in Avalonia controls, which this test project has no way to exercise.
  - `deploy/publish-windows.ps1` produced both exes.
  - The published `Litos.Gui.exe` started and its loopback server listened.
  - `GuiLoopbackServerTests` served a real artifact and a real shared file over a socket.
- **Not verified here:**
  - macOS publishing and signing. Needs a Mac: run `deploy/publish-macos.sh` and check
    `codesign --verify --deep --strict` on the bundle.
  - A real tagged release through the workflows.
  - A Windows update from an older installed build.
  - Clicking through the new UI by hand: the approval card, `/ptc`, slow-tool notice, Restart.

## 6. Not done

- No approval prompt for built-in tools (see §2).
- No in-app artifact viewer; links open in the browser, as in VS Code.
- The MCP servers window doesn't redraw by itself while open. The background reconnect still
  happens; click Refresh to see it.
