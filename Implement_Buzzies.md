# Implementing Litos Buzzies in Litos.Gui

This is the build plan for [ReadMe_Buzzies.md](ReadMe_Buzzies.md). That document describes the product. This one says what gets built, in what order, and how each phase is checked. Phase 0 is the first, smallest MVP. Each later phase builds on the one before and leaves the app in a working state.

Status (2026-11-26): Phases 0 and 1 are committed on branch `buzzies-mvp0` (commit `9c25852`). Phase 2 is built and documented but not yet committed. The 144 Buzzy tests pass, along with the rest of Litos.Gui.Tests (371 in total), and the Host, Hosting and VsCodeHost tests. All three phases were checked live (see each phase's "as built" section); Phases 0 and 1 in the running app, Phase 2 partly in code and partly in the window. Phases 3–6 are planned, not started.

## What the existing code gives us

A feasibility check of the code (2026-11-26) found that most of the foundation already exists:

| Needed | Existing piece |
|---|---|
| Separate session state per Buzzy | `AgentLoop.RunTurnAsync(owner, sessionId, transcript, ...)` is already per-session. `JsonlTranscriptStore` keeps one folder per `SessionOwner`, so owner `buzzy-<id>` gives each Buzzy its own session history. |
| Identity and memory in the prompt | `ISystemPromptProvider` can be wrapped, as `FactorySystemPromptProvider` does for the factory. |
| Per-Buzzy tool set | `ToolRegistry` is a plain list of `ITool`. A Buzzy's registry is the global one, filtered and wrapped. |
| Cancellation, usage and tool events | Already streamed by `AgentLoop` and rendered by `MainWindow`. |
| Approval cards | `ApprovalCard` and `PendingApprovalStore` (used today for MCP "Ask"). |
| Artifacts and skills | `Litos.Artifacts`, the loopback server and `SkillDiscovery`. |
| Tray | Avalonia's built-in `TrayIcon`. |

Gaps the phases have to close:

1. **The working directory is shared by the whole process.** `Environment.CurrentDirectory` decides where relative file paths and shell commands land, and `ITool.InvokeAsync` gets no per-call context. Phase 0 avoids the problem by chatting with one Buzzy at a time and rewriting every path argument to an absolute path inside the Buzzy's workspace. Phase 1 must finish this, giving the shell its own working directory, before a routine can run in the background while you chat with another Buzzy.
2. **No path restrictions or tool allowlist.** `GuiApprovalGate` approves every built-in tool. Phase 0 adds per-Buzzy allowlists and a workspace path guard.
3. **MainWindow handles one session at a time** (about 2,600 lines, `SessionOwner.Local` in about 10 places). Phase 0 makes the owner a field and switches it when you pick a Buzzy, the same way `/resume` switches sessions.
4. **No SQLite and no scheduler.** Phase 1.
5. **Gmail needs Google OAuth verification**, which is outside our control. Phase 4, with fixture data as the fallback.

## Phase 0: two Buzzies that remember (basic MVP)

**Goal:** prove that each Buzzy has its own identity, memory, workspace and history, and that all of them survive a restart. No scheduling, Gmail, tray or natural-language creation yet.

### Scope

1. **Storage:** one folder per Buzzy at `~/.litos/buzzies/<id>/`:
   ```text
   buzzy.json     name, template, colour, allowed tools, last session id, created time
   SOUL.md        who the Buzzy is and how it works (user-editable)
   MEMORY.md      what it has been asked to remember (user-editable; written by the remember tool)
   workspace/     its private working folder: the only place its file tools can reach
   ```
   Sessions stay in the existing store at `~/.litos/sessions/buzzy-<id>/`. This differs from the product doc's `<buzzy>/sessions/` layout, but it reuses `JsonlTranscriptStore` unchanged. `buzzy.json` stands in for SQLite until Phase 1, where scheduling's occurrence keys make a database worth having. Each Buzzy has exactly one `buzzy.json`, so there is never a second writable copy of its settings.
2. **Three templates:** Buddy (personal coworker), Scout (researcher) and Builder (developer), each with a starting SOUL.md, colour and tool allowlist:

   | Template | Tools |
   |---|---|
   | Buddy | read_file, write_file, edit_file, list_directory, search_code, web_search, skill, artifact, share_file, remember |
   | Scout | read_file, write_file, list_directory, search_code, web_search, skill, artifact, remember |
   | Builder | read_file, write_file, edit_file, list_directory, search_code, web_search, skill, artifact, share_file, remember, shell (asks every time) |

   **Artifacts are a default capability of every Buzzy**, as they are for the plain Litos chat. `artifact` and `skill` are always granted, whatever `buzzy.json` says. The Buzzy keeps the system prompt's skill catalog, so the built-in artifact-design skill is offered exactly as in the plain chat. The artifact tool is the same instance the plain chat uses, unwrapped, and its links open through the same loopback server. In Phase 0, published artifacts go to the shared artifact store (`~/.litos/artifacts`); a per-Buzzy `artifacts/` index comes with SQLite in Phase 1.

3. **Create a Buzzy:** a dialog where you pick a template, then a name and colour. It shows the tools and workspace folder for you to confirm before anything is saved.
4. **Sidebar:** a left-hand column with the plain Litos chat, then "MY BUZZIES" listing each Buzzy, then "+ Create a Buzzy". Selecting a Buzzy switches the chat to its last session, or starts one. `/new` and `/resume` work within the selected Buzzy. Its right-click menu has Edit soul, Edit memory, Open workspace and Delete.
5. **Buzzy avatars:** each Buzzy is drawn as a glossy, candy-coloured, Skittle-like bean with a honey-bee character: two dark stripes, translucent wings, eyes and antennae. It bobs slowly when idle, and its wings beat fast while it is working. It is pure vector drawing with no image files.
6. **Per-Buzzy prompt:** `BuzzySystemPromptProvider` adds a short identity line, SOUL.md and MEMORY.md to the normal system prompt. They count as instruction files in "View Context".
7. **Workspace guard:** every path-taking tool (`read_file`, `write_file`, `edit_file`, `list_directory`, `search_code`, `share_file`) is wrapped. The wrapper resolves the path against the workspace and rejects anything outside it, including `..` traversal, absolute paths elsewhere and symlink or junction escapes. It then passes the absolute path through, so the tool never depends on the process directory.
8. **Tool restrictions:** a Buzzy gets only its template's allowlist. MCP tools and PTC (the code kernel) are off for Buzzies, because both can reach outside the workspace. Builder's shell asks for approval on every command, using the existing approval card. In Phase 0 the shell starts in the workspace but is not confined to it: the approval card is the control, and the card says so.
9. **`remember` tool:** appends a dated line to MEMORY.md, so the Buzzy can keep facts you ask it to remember.

### Not in Phase 0

Routines, tray, notifications, natural-language creation, folder grants, per-Buzzy model choice (all Buzzies use the global `/model`), Gmail, Learn This Workflow, and running two Buzzies at the same time.

### Done when

- Two Buzzies (for example Scout and Builder) keep separate memory and sessions, and both are still there after restarting the app.
- A Buzzy's file tools refuse `..\..\x`, `C:\Windows\x` and a junction pointing outside the workspace. Automated tests cover this.
- MCP and PTC are absent for a Buzzy, Builder's shell shows an approval card, and Scout has no shell.
- The plain Litos chat behaves exactly as before.

### Phase 0 as built

- **Live check (2026-11-26):** the starter Buddy, Scout and Builder Buzzies were created through `BuzzyStore` and the real Litos.Gui was launched. Clicking Scout switched the window: the title read "LitosAiAgent — Scout", the session was fresh, and the PTC button showed "PTC: off for Buzzies". One real turn on OpenRouter (deepseek-v4.1-flash) asked Scout to remember a preference, write `hello.md` and read `C:\Windows\win.ini`. The result: `MEMORY.md` gained the dated line, `hello.md` landed in Scout's workspace, the read was refused with the guard's message, and the session was saved under `~/.litos/sessions/buzzy-scout/` with its id in `buzzy.json`.
- **Not checked live:** the create dialog, the right-click menu, Builder's shell approval card (covered by unit tests through `PendingApprovalStore`), and deleting a Buzzy.
- **Found and fixed during the build:**
  - A junction inside a workspace made the ordinary recursive folder delete fail with "access denied". `BuzzyStore.Delete` now removes links without following them, so it also can't delete anything outside the Buzzy's folder.
  - The status bar's context `ProgressBar` ignored `Width="48"` because of the Fluent theme's minimum width. Once the sidebar took its share of the window, that squeezed the working-folder label to nothing. It now sets `MinWidth="0"`.
  - Buzzy files are written as UTF-8 without a byte-order mark.
- **Known, not changed:** a long assistant reply sometimes renders as two bubbles. That is the existing streaming behaviour, not Buzzy code.

### Phase 0 demos

Each demo uses only what Phase 0 has. Two of them end in a published artifact, which opens in the browser from the link in the chat.

1. **"Scout writes a live research page" (artifact).** Ask Scout: *"Research the open-weight models released this month that run on a 24 GB Mac. Publish the result as an artifact: a comparison table with links to each source and a short recommendation."* Scout runs web_search, loads artifact-design and publishes. Open the link.
2. **"Two Buzzies, two memories."** Tell Buddy *"remember I like everything as short bullet lists"* and tell Scout *"remember I care most about licence terms"*. Restart Litos. Ask each one for a summary of something and show that each follows its own memory and doesn't know the other's. Open Edit memory to show the plain Markdown.
3. **"Builder ships a mini app" (artifact plus approval).** Ask Builder: *"Make a small to-do web app as an artifact, and also save its source in your workspace."* Then ask it to *"run dir to show the files"*. The approval card appears for the shell command; approve it on camera.
4. **"The workspace wall."** Ask Scout to read a file from your Documents folder. It is refused with the guard's message and explains why. Then drop a file into Scout's workspace (Open workspace folder) and ask again: it reads it.
5. **"Make your own Buzzy."** Create a Buzzy, for example "Penny" from the Buddy template in Bubblegum pink. Edit its soul to *"You are a cheerful finance-news explainer who always ends with one takeaway"*, then ask it about this week's news. The new bee wobbles while it works.

## Phase 1: routines, tray and notifications

**Goal:** a Buzzy repeats work on a schedule while the app sits in the tray.

- SQLite (`Microsoft.Data.Sqlite`, WAL mode) at `~/.litos/buzzies/buzzies.db` becomes the source of truth for Buzzy configuration (migrated from `buzzy.json`), routines, routine runs, task status and artifact metadata.
- Routine model: Buzzy, prompt, trigger (manual, daily at a time, weekly on a day and time), IANA time zone, enabled flag, token and time budget, and missed-run policy (skip and record by default, or one catch-up).
- An in-process scheduler with a per-user lock. A unique (routine id, occurrence key) row prevents duplicate runs. Missed occurrences are recorded on start and wake. Run now, pause and cancel. The next run time is shown.
- **Close the working-directory gap:** pass an explicit working directory to the shell (a per-Buzzy `ShellTool` instance or a decorator that sets it) and remove the reliance on `Environment.CurrentDirectory`. One active task per Buzzy, a global limit of three, and one concurrent local-model request.
- Scheduled runs use only allowlisted, non-interactive tools: no shell, no MCP. Anything that needs approval fails the run with a clear "needs you" status rather than waiting forever.
- Tray: closing the window minimizes to the tray, and Quit lives in the tray menu, with the difference stated in the UI. A real desktop notification fires when a routine finishes or fails.
- Task status in the sidebar: the Buzzy's wings beat only while it really has a task running.

Done when: a test routine with a two-minute interval runs while the window is closed to the tray, writes a new report, and shows a notification. After a restart the routine is still there and a missed run is recorded, not executed twice.

### Phase 1 as built

Decisions made during the build, where they differ from the plan above:

- **Buzzy settings stay in `buzzy.json`.** SQLite holds what needs a database: routines and run history, including each run's status, tokens, summary and outputs (files written and artifact links published). Each fact is still written in exactly one place, and moving settings into SQLite can happen whenever a feature needs to query them.
- **Time zones** are stored as the machine's own zone id (`TimeZoneInfo.Local.Id`, a Windows id on Windows). The published single-file Gui runs with invariant globalization, which can't convert IANA ids on Windows. An id that isn't known on the current machine falls back to its local zone.
- **An "every N minutes (for testing)" trigger** is included alongside manual, daily and weekly, because the done-when check above needs a short interval.
- **Notifications are Litos-drawn corner pop-ups** (`ToastWindow`): topmost, never stealing focus, stacking upwards and closing themselves. That gives one implementation for Windows and macOS with no extra packages. Native notification-centre toasts can come later.
- **Closing goes to the tray only while at least one routine is switched on.** Otherwise closing quits, as before, and the plain chat is unaffected. Quit lives in the tray menu, labelled "Quit Litos (stops routines)".
- **A queued run waits rather than being skipped** when its Buzzy is busy (a chat turn or another run). It is skipped only after 60 minutes. A run left mid-way by a crash is marked Interrupted on the next start and is never retried, since it may already have written files.
- **Concurrency:** one run per Buzzy, three at once across all Buzzies, and one when the provider is the local one. A chat turn with a Buzzy holds its routines back. A chat turn with the plain Litos agent does not count towards the limit.
- **Only the window holding the scheduler lock hides to the tray.** A second Litos window can view and edit routines but runs nothing, so closing it quits it.
- **Known gap:** the "is this Buzzy busy?" check before a chat turn and the turn claiming the Buzzy are two steps. A routine that becomes due in the moment between them can run alongside that chat turn. This is rare and harmless to the schedule, but it should become one atomic claim in Phase 5.
- **Unattended runs** get the Buzzy's tools without shell. MCP and PTC are already excluded for Buzzies. Every run is a new session under the Buzzy, so Open conversation shows exactly what it did.
- **No reliance on the process directory:** each Buzzy's shell is its own `ShellTool` started in its workspace (the shell tool gained an optional `workingDirectory`). Skills are discovered from the Buzzy's own `skills/` folder, the user's skill folders and the built-in ones, and searched from its workspace, never from wherever Litos was launched. File tools were already given absolute paths.

**Live check (2026-11-26), which met the done-when above.** I gave Scout an "every 2 minutes" routine ("Tick test": write `reports/tick-<time>.md` with the time and a line of encouragement), running on OpenRouter (deepseek-v4.1-flash).
- Scout's sidebar row read "Next: today 14:13", and the Routines window listed the routine.
- Closing the window hid Litos to the tray and showed the "still running in the tray" notification. The process stayed alive.
- The 14:13 and 14:15 runs happened with the window hidden. Each wrote a new report (`tick-2026-11-26-1413.md`, `…1415.md`) and was recorded as Succeeded, using about 6.5k and 9.7k tokens. The 14:15 run showed the "Scout finished "Tick test"" notification with its summary.
- I then stopped Litos and relaunched it at 14:21:25. The 14:17 and 14:19 occurrences were recorded as Missed and not run. 14:21 was within the grace window, so it ran once. No occurrence key appeared twice.
- In the Routines window, run history showed Done and Missed with times, tokens, summaries and saved files. Pause flipped the routine to Paused in the database. Open conversation switched the window to Scout's run session. With the routine paused, closing the window quit Litos.

Fixed after the live check: saved files were shown as full absolute paths (now relative to the workspace), and a pause or edit made in the Routines window didn't refresh the sidebar's "Next:" until the next run (it now notifies straight away).

Not checked live: the routine editor dialog (the test routine was created in code through `BuzzyDb`), the tray icon's menu (Quit and Routines…), Cancel on a running run (covered by scheduler tests), and the second-window case.

| File | Role |
|---|---|
| `src/Litos.Gui/Buzzies/Routines/Routine.cs` | Routine and run records, triggers and statuses |
| `src/Litos.Gui/Buzzies/Routines/RoutineSchedule.cs` | Next occurrence, due and missed planning, DST handling, occurrence keys |
| `src/Litos.Gui/Buzzies/Routines/BuzzyDb.cs` | SQLite (WAL): routines and runs, unique occurrence claim, interrupted recovery |
| `src/Litos.Gui/Buzzies/Routines/BuzzyRunExecutor.cs` | Runs one routine as an unattended Buzzy turn with time and token limits |
| `src/Litos.Gui/Buzzies/Routines/RoutineScheduler.cs` | Tick loop, per-user lock, dispatch, concurrency, cancel, events |
| `src/Litos.Gui/Buzzies/RoutinesWindow.cs`, `RoutineEditorWindow.cs` | Routines list and history; create and edit form |
| `src/Litos.Gui/Buzzies/ToastWindow.cs` | Corner notifications |
| `src/Litos.Gui/MainWindow.Routines.cs` | Scheduler lifetime, tray, close-to-tray, notifications, Open conversation |
| `src/Litos.Tools/Shell/ShellTool.cs` | Optional `workingDirectory` |

### Phase 1 demos

1. **"Research while you're away" (artifact plus tray).** Give Scout an "every 2 minutes" routine: *"Publish an artifact page of the newest open-weight model releases, with sources."* Close the window: Litos goes to the tray and the bee notification appears. When the run finishes, its notification pops up; click it to open the run's conversation and the published page.
2. **"Your Friday report writes itself."** Give Buddy a weekly routine, Friday at 17:00: *"Turn this week's notes in your workspace into a dated summary report."* Show "Next: Fri 17:00" in the sidebar, then press Run now on camera.
3. **"It never does the same job twice."** Quit Litos past a scheduled time and reopen it. The run shows as Missed in the history and does not run late. Switch on "run once when it opens again", repeat, and exactly one catch-up run happens.
4. **"Hands off while I'm talking to it."** Start chatting with Scout just as its routine is due. The run waits as Queued and starts the moment your chat turn ends, with the wings beating in the sidebar.
5. **"Guard-rails."** Set a routine's limit to 10 thousand tokens and give it a big job. It stops itself with "Stopped: the run used more than its budget". Show that Builder's routines run without the shell.

## Phase 2: Learn This Workflow

**Goal:** teach a task once and have it repeat without retyping the prompt.

- After a successful turn, an action row: Open Artifact, Remember This Workflow, Repeat Every… (schedule picker).
- Remember This Workflow drafts a skill (inputs, steps, tool grants, output, validation criteria) into `<buzzy>/skills/<name>/SKILL.md`, using the existing skill format, versioned as `v1`, `v2`, and so on. The user reviews and edits it before saving.
- A routine pins a skill version, and upgrading needs approval. A repeat run writes a new, timestamped artifact instead of overwriting.
- Memory hygiene: proposed memories show where they came from and need confirmation, and the text of web pages, emails and tool output never becomes trusted instructions.

Done when: the 60-second demo's steps 1, 3 and 4 work end to end on a real provider.

### Phase 2 as built

- **Action row:** after a Buzzy turn with no error and no cancel, the window offers Open artifact (when the turn published one; the first link opens), Remember this workflow, and Repeat every…. The actions apply only to the conversation they belong to.
- **Remember this workflow:** one tool-less model call (`WorkflowDrafter`, shaped like the existing Reflector) drafts a SKILL.md with Inputs, Steps, Tools, Output, Checks and Limitations. The drafter sees what the user asked, the tool calls (names and shortened arguments) and the final answer. It never sees tool results, and attached documents (the untrusted-content blocks) are replaced by "[attached document]". A web page or file the Buzzy read therefore can't write itself into a skill that later runs unattended. The draft opens in `WorkflowReviewWindow` for editing, with Save workflow or Save and repeat…. If the model call fails, an outline built from the conversation is offered instead.
- **Versioned skills** (`WorkflowStore`): `skills/<name>/SKILL.md` is always the newest version, so the skill tool and the prompt's skill catalog find it with no special casing. `skills/<name>/versions/vN.md` keeps every version. Names are lower-case and hyphenated, and the description is kept to one line so it can't break the frontmatter.
- **Pinned versions:** routines gained `SkillName` and `SkillVersion` (buzzies.db schema v2; v1 databases are migrated in place). A pinned run gets that exact version's text in its brief and is told not to load it again with the skill tool. A missing version fails the run with a message saying so. The Routines window shows "Follows workflow X vN" and, when a newer version exists, a Use vN button. Moving to it is the user's click.
- **Repeat every…** opens the routine editor prefilled with the same request, every Friday at 17:00, pinned to the workflow saved in that conversation if there is one. You can untick the pin.
- **No overwriting:** in unattended runs, `write_file` is wrapped in `NoOverwriteTool`. It can create files and rewrite ones it created in the same run, but it refuses to replace a file an earlier run left, and says to use a new dated name. `edit_file` still works for deliberate edits.
- **Memory hygiene:** every MEMORY.md line now records its source ("- 2026-11-26 (chat): …"). In unattended runs, `remember` only proposes (`MemoryProposals`, `memory-proposals.json` per Buzzy, at most 50 waiting). The sidebar row shows "N memories to review", and the right-click menu opens `MemoryReviewWindow` with Keep and Discard. A kept memory is written with "(from routine "…", kept by you)".
- **Wrap-up before the hard stop** (added after the live check below): at 75% of the token budget or the time limit, an unattended run gets a steering message to stop researching, save what it has as the dated file and finish. The hard stop stays at 100%. The unattended brief and the drafter now both ask for proportionate work: roughly 5 to 15 tool calls for a run.

**Live check (2026-11-26)** on OpenRouter (deepseek-v4.1-flash):
- **Scout, run in code through the same classes the window uses, because the screen was locked:** a research chat turn ran several web searches and wrote `reports/2026-11-26-three-newest-24gb-mac-open-weight-models.md`. The drafter produced `newest-local-open-weight-models` v1: Inputs as placeholders, 11 concrete steps, checks and limitations, and nothing taken from search results. Two pinned unattended runs of it were stopped by the token budget at 207k and 253k of 200k, and a third at 618k of 600k. That run made 25 web searches; about 307k characters of results built up and were re-sent every round. No file was written. This is what led to the wrap-up rule and the "proportionate" wording above.
- **Buddy, in the window:** "write a haiku, save it as poems/bees.md and publish it as an artifact". The action row appeared with all three buttons. Remember this workflow drafted `haiku-artifact-publisher`, and the review window showed name, version, description, steps and tools used. Save and repeat… opened the editor prefilled (Friday 17:00, pin ticked), and Create showed the routine in Routines as "Follows workflow … v1, Next: tomorrow 17:00". Run now succeeded in about 23 seconds with 37.6k tokens. It wrote new `poems/bees-2026-11-26.md` and `.html` files, left the original `bees.md` untouched, and published a new artifact.
- **Found and fixed during the check:**
  - Clicking a Buzzy in the first seconds after start-up let the plain chat's background session restore land on top of it, so the Buzzy showed (and recorded as its own) the plain chat's session. The restore now leaves a selected Buzzy alone and is remembered for when you switch back. `ResumeSessionAsync` drops a load whose owner changed meanwhile.
  - The reply's last line could render below the action row.
  - The routine editor showed the time zone in double brackets and cut off "thousand tokens at most".
- **Also seen:** the built-in artifact-design skill tells the model to write its HTML in a temp folder. The workspace guard refused, and Buddy saved and published it from its workspace instead. That's the intended outcome, but the skill's wording should say "workspace" when it's used by a Buzzy.
- **Machine sleep:** the locked machine entered Modern Standby during one run. Timers count awake time only, so that run picked up after wake and was ended by its time limit. Phase 5 should mark a run caught by sleep as interrupted on wake.

## Phase 3: conversational creation and folder grants

- Create a Buzzy by conversation: the default chat (or Buddy) proposes a configuration as a card (name, role, template, tools, workspace, model), and nothing is saved until you confirm.
- Per-Buzzy model choice, defaulting to the global model.
- Explicit folder grants (read, or read and write) for external folders, enforced by the same path guard. Builder can be granted a repository and still asks before every shell command.
- Editing soul, memory and skills inside the app, not only in an external editor.

## Phase 4: Gmail connector

- Google OAuth (installed-app flow with PKCE) with the narrowest scopes: `gmail.readonly` first, and `gmail.compose` only if drafting replies. Tokens live in the OS credential store, never in prompts or Buzzy files.
- A trusted broker with tools for search, read, summarize, approved attachment download and draft reply. Sending, if built at all, goes through an approval card bound to the exact message, and mail is never deleted.
- Email and attachment text is wrapped as untrusted content, as `UntrustedContent` does today.
- Fallback while Google's verification is pending: a fixture inbox, clearly labelled as sample data.

## Phase 5: beta hardening

- Security tests: workspace escapes, unauthorized tool calls, prompt injection through web, mail and documents, and duplicate external writes after a crash.
- Budgets: per-task token and time limits enforced outside the model. Interrupted tasks are surfaced rather than blindly retried.
- Export of a Buzzy's memory, skills and artifacts. Install and tray behaviour tested on Windows and macOS.
- Optional "Litos Work" branding of the window and installer.

This completes the product doc's beta acceptance criteria (§9).

## Phase 6 and later: after the beta

Taken from ReadMe_Buzzies.md §4–§10, and only after the beta shows real repeat use:

- Extract BuzzyManager and the scheduler into a per-user BotHost (REST and SSE on loopback), so the GUI and VS Code share Buzzies.
- Delegation between Buzzies (`assign_task`, durable child tasks, depth and budget limits).
- The Analyst template with structured document tools (ClosedXML, Open XML SDK, PdfPig, CsvHelper).
- Google Drive and Calendar connectors.
- Licensing and payments (Dodo first), entitlement leases and the Free/Pro limits.
- Buzzy Store, OS sandboxing for unattended code, and other connectors.

## Code map (Phase 0)

| File | Role |
|---|---|
| `src/Litos.Gui/Buzzies/BuzzyProfile.cs` | The `buzzy.json` record and templates |
| `src/Litos.Gui/Buzzies/BuzzyStore.cs` | Create, list, save and delete Buzzy folders |
| `src/Litos.Gui/Buzzies/WorkspacePathGuard.cs` | Path resolution and escape checks |
| `src/Litos.Gui/Buzzies/WorkspaceScopedTool.cs` | Decorator that rewrites and validates `path` arguments |
| `src/Litos.Gui/Buzzies/ApprovalRequiredTool.cs` | Decorator that asks before every call (Builder's shell) |
| `src/Litos.Gui/Buzzies/RememberTool.cs` | Appends to MEMORY.md |
| `src/Litos.Gui/Buzzies/BuzzyToolPolicy.cs` | Builds a Buzzy's registry from the global one |
| `src/Litos.Gui/Buzzies/BuzzySystemPromptProvider.cs` | Adds identity, SOUL.md and MEMORY.md |
| `src/Litos.Gui/Buzzies/BuzzyAvatar.cs` | The animated honey-bee candy avatar |
| `src/Litos.Gui/Buzzies/CreateBuzzyWindow.cs` | The create-and-confirm dialog |
| `src/Litos.Gui/MainWindow.Buzzies.cs` | Sidebar, switching Buzzies, the per-Buzzy loop and registry, create and delete |
| `src/Litos.Gui/MainWindow.axaml(.cs)` | Sidebar column, `Owner` instead of `SessionOwner.Local`, PTC refused for Buzzies |
| `src/Litos.Host/LitosHostBuilder.cs` | `AgentLoopFactory.WithSystemPromptProvider` and `SystemPromptProvider` |
| `tests/Litos.Gui.Tests/Buzzies/` | Store, path guard (including junctions), tool wrappers, policy, prompt, avatar motion |

The non-UI files (`BuzzyProfile`, `BuzzyStore`, the guard, the tools, the policy and the prompt provider) do not reference Avalonia, so Phase 1 or Phase 6 can move them into their own project.
