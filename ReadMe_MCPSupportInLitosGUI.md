# MCP Support in Litos.Gui — Findings & Design

Extends MCP (Model Context Protocol) server support — already shipped in `Litos.Api` (see
`ReadMe_LitosApi_Mcp.md`) — to `Litos.Gui`, the Avalonia desktop face. Written after a full
codebase-exploration and clarifying-questions pass; no code has changed as a result of this
document. Where this document's decisions differ from `Litos.Api`'s, it's because the two faces
have different requirements (chiefly: **no approval gating in Litos.Gui**) and different hosting
models (no `IHost`/`BackgroundService` infrastructure in `Litos.Gui` today).

**Confirmed decisions** (resolved during scoping, before design):

- **Transport**: both stdio and HTTP/SSE, same as `Litos.Api` — the underlying library already
  supports both, so this is free.
- **Gating**: MCP tool calls in `Litos.Gui` are **not gated at all** — they flow through the
  existing `GuiApprovalGate`, which already auto-approves everything unconditionally. No
  `McpAwareApprovalGate`-style decorator is needed or wanted here.
- **Permission UI**: the popup does **not** show Deny/Ask/Full controls. Since nothing in
  `Litos.Gui` ever consults `DefaultPermission`/`ToolOverrides`, showing those controls would
  imply gating that doesn't exist. `DefaultPermission` is stored internally as a fixed value
  (`Full`) and never surfaced.
- **Reconciliation model**: live — add/edit/enable/disable/remove via the popup takes effect
  without restarting `Litos.Gui`, mirroring `Litos.Api`'s behavior. But **no background poller** —
  `Litos.Gui` has no `BackgroundService`/`IHost` infrastructure today (see §2.4), and the user
  explicitly asked for manual control instead: a per-server **Refresh** button and a **Refresh
  all** button in the popup, plus an async, non-blocking auto-connect attempt at app startup.
- **Entry point**: a new `/mcp` slash command only — no permanent status-bar button.
- **Popup shape**: one window, server list plus inline add-server form, mirroring
  `McpServers.razor`'s single-page layout (list at top, add form below) rather than splitting into
  a list window and a separate edit dialog.

## 1. What already exists and is reused as-is

MCP support in `Litos.Api` was deliberately split into a face-agnostic library,
`Litos.Tools.Mcp`, plus `Litos.Api`-specific wiring and a Blazor admin page
(`ReadMe_LitosApi_Mcp.md` §9.2 decision 6 explicitly calls this out: kept in `Litos.Tools.Mcp`
"for future cross-face (`Litos.Gui`/`Litos.Console`) reuse even though only `Litos.Api` wires it up
today"). That investment pays off directly here — **none of the following needs to change**:

| Type | File | Role |
|---|---|---|
| `McpTransportKind`, `McpServerDefinition`, `McpConfig` | `src/Litos.Tools.Mcp/McpConfig.cs` | Config data model + JSON `Load`/`Save`. |
| `McpConfigStore` | `src/Litos.Tools.Mcp/McpConfigStore.cs` | Thread-safe singleton; `Current`/`Update(mutate)`; validates names (no `"__"`, no duplicates); persists to `{LITOS_STATE_DIR or ~/.litos}\mcp.json`. |
| `McpServerConnection` | `src/Litos.Tools.Mcp/McpServerConnection.cs` | One server's connect/handshake/status (`Connecting`/`Connected`/`Unreachable`), both transports, exponential backoff (15s→5min) for `NextRetryAt`. |
| `McpToolProxy` | `src/Litos.Tools.Mcp/McpToolProxy.cs` | `ITool` wrapper; name `mcp__{server}__{tool}`; calls `IToolApprovalGate.RequestAsync` before invoking. |
| `McpToolProvider` | `src/Litos.Tools.Mcp/McpToolProvider.cs` | Orchestrates all connections; `InitializeAsync`/`RefreshAsync` reconcile config↔live state; exposes `Tools`/`Connections`. |
| `McpToolSource` | `src/Litos.Tools.Mcp/McpToolSource.cs` | Thin `IToolSource` adapter over `McpToolProvider.Tools`. |
| `IToolSource`, `ToolRegistryFactory` | `src/Litos.Agent/Tools/IToolSource.cs`, `ToolRegistryFactory.cs` | Face-agnostic "build a fresh `ToolRegistry` from static tools + every `IToolSource`" seam — already lives in `Litos.Agent`, zero MCP knowledge. |

**Not reused**: `McpAwareApprovalGate` (gating — out of scope here by design) and
`McpToolRefreshService` (the `Litos.Api`-specific `BackgroundService` poller — replaced by manual
refresh, §3.3).

**One new project reference is required**: `Litos.Gui.csproj` needs a direct
`<ProjectReference>` to `Litos.Tools.Mcp.csproj`, the same way `Litos.Api.csproj` already has one.
`Litos.Host` (the shared composition root both faces build on) deliberately does **not** carry
this reference (`ReadMe_LitosApi_Mcp.md` §9.2 decision 2/6) — each face wires MCP in itself.

## 2. Litos.Gui architecture relevant to this feature

### 2.1 No MVVM, no dialog service

`Litos.Gui` is flat, code-behind Avalonia — no `CommunityToolkit.Mvvm`/`ReactiveUI`, no
`Views`/`ViewModels` folders, no `INotifyPropertyChanged`. `ToolCallRow.cs`'s own doc comment
states this explicitly: *"Not a ViewModel — no INotifyPropertyChanged/binding; MainWindow still
pushes into TextBlock.Text directly."* The only existing dialog precedent is
`ListPickerWindow` (`src/Litos.Gui/ListPickerWindow.axaml`/`.cs`): a plain `Window`, opened via a
static async factory (`PickAsync<T>(owner, title, items, labelSelector)`) returning Avalonia's
native `ShowDialog<T?>(owner)` — no hand-rolled `TaskCompletionSource`, no `IDialogService`
abstraction anywhere in the codebase.

### 2.2 Composition root and session state

`Program.cs` builds a plain `ServiceCollection` (via `Litos.Host.AddLitosAgent`), registers
`GuiApprovalGate`, calls `BuildServiceProvider()` — no Generic Host (`IHost`), no
`BackgroundService` support today. It then resolves a `ToolRegistry` **once**
(`provider.GetRequiredService<ToolRegistryFactory>().Create()`, `Program.cs:62`) and builds one
`AgentLoop` bound to it, both handed into `MainWindowSession` — a plain mutable-property bag, not
a ViewModel (`MainWindowSession.cs`). Its own doc comment currently states dynamic MCP discovery
is "out of scope for this face," which this document changes (§3).

### 2.3 The send path binds `ToolRegistry` into `AgentLoop`, not per-call

Critically, `AgentLoop.RunTurnAsync` does **not** take a `ToolRegistry` parameter —
`AgentLoopFactory.Create(chatProvider, toolRegistry)` bakes the registry into the `AgentLoop`
instance at construction time. `MainWindow.axaml.cs:290` calls `_session.Loop.RunTurnAsync(...)`
against whichever `AgentLoop` is currently in `_session.Loop`. There is already a precedent for
rebuilding it: `MainWindow.axaml.cs:939`, a provider switch, does
`_session.Loop = _session.LoopFactory.Create(newChatProvider, _session.ToolRegistry)`. **This
means "rebuild the tool registry before every send" (§3.2) really means rebuilding `_session.Loop`
itself right before `SubmitAsync`'s `RunTurnAsync` call**, reusing the current `ChatProvider`, not
just swapping out an internal field — the same shape as the existing provider-switch call site,
just triggered on every send instead of only on `/provider`.

### 2.4 No background service host

`Litos.Api`'s `McpToolRefreshService : BackgroundService` is started/stopped by the ASP.NET Core
Generic Host (`builder.Services.AddHostedService(...)`). `Litos.Gui` has no such host — it's a
bare `ServiceProvider` plus an Avalonia application lifetime. Running the same poller here would
mean either adding a minimal Generic Host just for this one service, or manually driving a
`BackgroundService`'s `StartAsync`/`StopAsync`. The user's decision (§0) avoids this entirely: no
poller, manual refresh buttons instead.

### 2.5 Avalonia pitfall (per `CLAUDE.md`)

If the server list needs to scroll, put `Margin` on the `ScrollViewer`'s direct child, never
`Padding` on the `ScrollViewer` itself — this Avalonia version's `ScrollViewer.Padding` doesn't
fold correctly into the scrollable extent (see `CLAUDE.md`, `ReadMe_AgentDesign.md` §7.7).

## 3. Design

### 3.1 New project reference

`src/Litos.Gui/Litos.Gui.csproj` gains a `<ProjectReference>` to
`src/Litos.Tools.Mcp/Litos.Tools.Mcp.csproj`.

### 3.2 `Program.cs` wiring

Mirrors `Litos.Api/Program.cs`'s pattern (`ReadMe_LitosApi_Mcp.md` §9.4) but adapted for no
gating and no blocking startup:

1. Construct `McpConfigStore` directly (`new McpConfigStore()`) — same "plain construction, not
   DI" approach `Litos.Api` uses, since it's needed before/alongside other startup work.
2. Construct `McpToolProvider` directly, passing the **existing** `GuiApprovalGate` instance as
   its `IToolApprovalGate` — no wrapping, no `McpAwareApprovalGate`. Every `McpToolProxy`'s
   `InvokeAsync` still calls `approvalGate.RequestAsync(...)` (that call is baked into
   `McpToolProxy` itself and can't be skipped), but `GuiApprovalGate.RequestAsync` unconditionally
   returns `Approve` — so the call is a no-op in practice, exactly satisfying "not gated" with zero
   new gating code.
3. **Do not** `await InitializeAsync(...)` before the window is ready. Instead, kick off
   `_ = mcpToolProvider.InitializeAsync(timeout, CancellationToken.None)` as a fire-and-forget
   `Task` (logged, never thrown) so the main window appears immediately; connections populate
   `McpToolProvider.Tools` as they complete in the background. This is the user-confirmed "async
   with timeout, non-blocking" startup behavior (§0) — a deliberate departure from `Litos.Api`'s
   blocking `await` before `app.Build()`, justified because `Litos.Gui` rebuilds its tool registry
   fresh before every send anyway (§3.3), so there's no "first turn must have everything" pressure
   the way a synchronous DI-container snapshot creates in `Litos.Api`.
4. Register `McpToolSource(mcpToolProvider)` as an `IToolSource` and pass it into
   `ToolRegistryFactory` alongside the existing static tools — same construction shape
   `Litos.Api` uses, just assembled by hand here since there's no DI container doing it
   automatically pre-`Build()`.
5. Hand `mcpConfigStore` and `mcpToolProvider` into `MainWindowSession` (new properties) so the
   `/mcp` command and the popup can reach them.
6. On app shutdown (`App.axaml.cs`'s exit path, or `MainWindow`'s `Closing` handler), call
   `mcpToolProvider.ShutdownAsync()` bounded by a short timeout — mirrors `Litos.Api`'s
   `ApplicationStopping` hook, adapted to Avalonia's own lifetime event.

### 3.3 Manual reconciliation instead of a background poller

No `BackgroundService`, no `McpToolRefreshService`. Instead:

- **`McpToolProvider.RefreshAsync(perServerTimeout, ct)` is called directly** (it already exists
  and needs no changes — it's the same method `Litos.Api`'s poller calls on a timer) from three
  places:
  1. Once, fire-and-forget, at startup (§3.2 point 3).
  2. Whenever the popup's **"Refresh all"** button is clicked.
  3. Whenever the popup's per-server **"Refresh"** button is clicked — for a single-server refresh,
     either call the same `RefreshAsync` (cheap: it only reconnects what's stale/due, so refreshing
     "all" to refresh "one" is not wasteful) or add a small single-server overload if scoping to
     exactly one server's connection attempt turns out to matter during implementation. Given
     `RefreshAsync`'s existing diff logic already only touches servers that are new/changed/due for
     retry (`McpToolProvider.cs`'s `DefinitionsMatch`/backoff checks), calling the same method for
     both buttons is likely sufficient — worth confirming with one implementation-time check rather
     than deciding definitively here.
- **No backoff-driven automatic retry.** `McpServerConnection.NextRetryAt`/exponential backoff
  fields still exist and are still respected by `RefreshAsync` itself (a manual refresh before the
  backoff window elapses is still a no-op retry-wise, same logic as today) — the only thing removed
  is the *timer* that called `RefreshAsync` automatically every 5s. The user drives every
  reconciliation via the two buttons in §3.4.
- **Popup left open while a connection is in flight**: the popup should reflect live status
  (`Connecting…`/`Connected`/`Unreachable`) while open, the same way `McpServers.razor` does today
  by reading `McpToolProvider.Connections` — but since there's no polling loop, the popup itself
  needs to re-read connection status after each button click's `Task` completes (no separate
  "live updates while idle" mechanism is needed, since nothing changes without a button click).

### 3.4 The popup: `McpServersWindow`

New files: `src/Litos.Gui/McpServersWindow.axaml` + `.axaml.cs`, following `ListPickerWindow`'s
conventions (plain `Window`, no ViewModel, dark background, `CenterOwner`) but larger and with a
richer layout, ported directly from `McpServers.razor`'s field set minus the permission UI:

- **Server list** (scrollable — remember §2.5's `Margin`-not-`Padding` rule): for each configured
  server, show name, transport + command/URL summary, a live status badge
  (`Connecting…`/`Connected`/`Unreachable: {error}`/"Not started"), Enable/Disable toggle, a
  per-server **Refresh** button, and a **Remove** button. No permission controls (§0).
- **"Refresh all" button** at the top of the list, calling `RefreshAsync` for every enabled
  server.
- **Add-server form** below the list, same fields as `McpServers.razor`: name, transport
  (stdio/HTTP radio or combo), then either command+args (stdio) or URL (HTTP), plus an
  Enabled checkbox. `DefaultPermission` is set to a fixed `ToolPermission.Full` in code when
  constructing the `McpServerDefinition` — never a form field.
- **Validation feedback**: `McpConfigStore.Update` throws `McpConfigValidationException` for bad
  names (contains `"__"`, duplicate) — caught and shown as inline text in the popup, mirroring
  `McpServers.razor`'s `_message` pattern (and consistent with `Litos.Gui`'s existing
  no-modal-error-dialog convention of showing errors as plain text, e.g. `MainWindow`'s
  `AddToolLine`).
- **Opened via**: a static async factory following `ListPickerWindow`'s shape, e.g.
  `McpServersWindow.ShowAsync(owner, mcpConfigStore, mcpToolProvider)`, called from the new `/mcp`
  slash command handler in `MainWindow.axaml.cs` (added to the same dispatch table as
  `/resume`/`/provider`/`/model`/`/skills`/`/branch`, and to `CommandMenuPopup`'s command list so it
  appears in the `/`-menu).
- Every mutation (`Update` on the store) should trigger a `RefreshAsync` afterward — add, edit,
  enable, disable, and remove all change what `McpToolProvider` should be connected to, so each
  action's handler both updates `McpConfigStore` and immediately calls `RefreshAsync` (not waiting
  for the user to separately click "Refresh all"), matching `Litos.Api`'s live-reconcile spirit
  even without a poller. A raw "Remove" doesn't need a reconnect attempt, only a disconnect, but
  routing it through `RefreshAsync` too is simplest and correct (`RefreshAsync` already disposes
  connections for servers no longer in the desired set).

### 3.5 Rebuilding the tool registry (and `AgentLoop`) before every send

Per §2.3, this isn't just "call `ToolRegistryFactory.Create()` again" — it's rebuilding
`_session.Loop` itself. In `MainWindow.axaml.cs`'s `SubmitAsync`, immediately before the
`RunTurnAsync` call (line ~290), insert:

```csharp
_session.ToolRegistry = _session.ToolRegistryFactory.Create();
_session.Loop = _session.LoopFactory.Create(_session.ChatProvider, _session.ToolRegistry);
```

(`MainWindowSession.ToolRegistry` changes from a constructor-only `{ get; }` to a mutable
`{ get; set; }`, and a `ToolRegistryFactory` property is added alongside the existing
`LoopFactory` one.) This exactly mirrors the existing provider-switch call site
(`MainWindow.axaml.cs:939`) — same two lines, just also run on every send rather than only on
`/provider`/`/model`. A turn already in flight keeps whatever `AgentLoop`/`ToolRegistry` it
captured at the moment it started (Avalonia's single-turn-at-a-time send path, §2.3's
`RejectSlashCommandMidTurn`/`Steer` handling, already guarantees only one turn runs at a time), so
"next-turn-only visibility" (`ReadMe_LitosApi_Mcp.md` §10.3 requirement 2) holds here for free,
with no `AgentWorker`-style per-turn snapshot machinery needed.

### 3.6 What's explicitly out of scope here (matching `Litos.Api`'s own precedent)

- **MCP resources and prompts** — unchanged from `ReadMe_LitosApi_Mcp.md` §7/§10.5, still tools
  only.
- **Per-session/per-user MCP server scope** — still global/deployment-wide (one `mcp.json`),
  consistent with `Litos.Api`.
- **Any gating** — confirmed explicitly out of scope for `Litos.Gui` (§0); `McpAwareApprovalGate`
  is never instantiated in this face.
- **Background auto-retry poller** — confirmed explicitly out of scope (§0); manual refresh only.
- **`Litos.Console`** — not addressed by this document; `Litos.Tools.Mcp`'s face-agnostic design
  means it could adopt the same approach later without rework, same as `ReadMe_LitosApi_Mcp.md`
  §10.5 already notes for `Litos.Gui` itself.

## 4. Suggested build sequence

1. `Litos.Gui.csproj` → add `<ProjectReference>` to `Litos.Tools.Mcp.csproj`. Confirm the project
   builds with no other changes yet.
2. `MainWindowSession` → add `McpConfigStore`, `McpToolProvider`, `ToolRegistryFactory` properties;
   change `ToolRegistry`/`Loop` to mutable if not already (`Loop` is already mutable per
   `MainWindow.axaml.cs:939`; `ToolRegistry` needs the same treatment, §3.5).
3. `Program.cs` → construct `McpConfigStore`/`McpToolProvider` (passing the existing
   `GuiApprovalGate`), fire-and-forget `InitializeAsync`, wire `McpToolSource` into
   `ToolRegistryFactory`'s sources, thread the new objects into `MainWindowSession`.
4. `SubmitAsync` → insert the two-line rebuild from §3.5 immediately before `RunTurnAsync`.
5. `McpServersWindow.axaml`/`.axaml.cs` → the popup itself (§3.4), including per-action
   `RefreshAsync` calls.
6. `/mcp` slash command → dispatch table entry in `MainWindow.axaml.cs` + `CommandMenuPopup`'s
   command list, opening `McpServersWindow.ShowAsync(...)`.
7. Shutdown hook → call `McpToolProvider.ShutdownAsync()` (bounded timeout) from the app's exit
   path.
8. Manual verification: configure a real stdio server (e.g.
   `npx @modelcontextprotocol/server-everything`, the same one used to verify `Litos.Api`'s
   implementation) and a real HTTP/SSE server if one is available; confirm tools appear in a chat
   turn after Refresh, confirm Remove/Disable stop them from appearing on the next send, confirm no
   approval prompt ever appears for an MCP tool call.

## 5. Key files (for implementation)

**Reused, unmodified**: `src/Litos.Tools.Mcp/McpConfig.cs`, `McpConfigStore.cs`,
`McpServerConnection.cs`, `McpToolProxy.cs`, `McpToolProvider.cs`, `McpToolSource.cs`;
`src/Litos.Agent/Tools/IToolSource.cs`, `ToolRegistryFactory.cs`.

**New**: `src/Litos.Gui/McpServersWindow.axaml`, `McpServersWindow.axaml.cs`.

**Modified**: `src/Litos.Gui/Litos.Gui.csproj` (project reference); `src/Litos.Gui/Program.cs`
(construction/wiring, §3.2); `src/Litos.Gui/MainWindowSession.cs` (new properties, mutable
`ToolRegistry`); `src/Litos.Gui/MainWindow.axaml.cs` (`SubmitAsync` rebuild, §3.5; `/mcp` dispatch;
shutdown hook); `src/Litos.Gui/CommandMenuPopup.cs`/`.axaml` (add `/mcp` to the command list).

**Reference for porting UX**: `src/Litos.Api/Components/Pages/McpServers.razor` (field layout and
behavior to port, minus permission controls) and `src/Litos.Gui/ListPickerWindow.axaml`/`.cs`
(the dialog-construction convention to follow).

**Background reading**: `ReadMe_LitosApi_Mcp.md` (full MCP design/implementation history for
`Litos.Api`; §10 is current truth for that face) and `CLAUDE.md`/`ReadMe_AgentDesign.md` §7.7
(the `ScrollViewer.Padding` pitfall).
