# Implementing Litos Buzzies in Litos.Gui

This is the build plan for [ReadMe_Buzzies.md](ReadMe_Buzzies.md). That document describes the product. This one says what gets built, in what order, and how each phase is checked. Phase 0 is the first, smallest MVP. Each later phase builds on the one before and leaves the app in a working state.

Status (2026-11-26): Phases 0 and 1 are committed on branch `buzzies-mvp0` (commit `9c25852`), Phase 2 in `f0aab23`, and Phase 3 in a commit on top. Phase 4 (the Gmail connector) is built in the working tree after Phase 3: a new `src/Litos.Mail` project, the Gmail sidebar row and settings window, an Email section in each Buzzy's access settings, and `tests/Litos.Mail.Tests`. The 76 mail tests, the 442 Gui tests (215 of them Buzzy tests), the Tools (171), Agent (340), Host (24), Hosting (81), VsCodeHost (65) and Artifacts (94) suites pass. Phases 0–2 were checked live (see each phase's "as built" section); Phase 3's creation lane, grants and per-Buzzy model have not been checked in the running app yet, and neither has the contents editor. Phase 4 is checked by tests and headless window layout, and by hand only against the sample inbox — a real Google sign-in needs an OAuth client and a test account, which this machine did not have. Phases 5–6 are planned, not started. `DEMO_Buzzies.md` is the run sheet for checking Phases 0–4 by hand.

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

### Phase 3 as built

- **Create by conversation, but only on request.** `/buzzy <what it should do>` in the chat, plus
  "+ Create a Buzzy" for the plain dialog, is the *only* way to start a proposal. One tool-less
  model call (`BuzzyProposer`, shaped like `WorkflowDrafter`) proposes a name, template, model,
  tools, a soul and the folders it would want, and `BuzzyProposalWindow` shows it as a card: nothing
  is saved until the user presses Create. The user can change the template, name, colour, tools
  (click a chip to grant or take away), model and soul text there. Dismissing the card puts the typed
  description back in the input box, so nothing is lost.
- **Saying so.** Because the command is now the only way in, the window points at it: `/buzzy` is in
  the `/` command menu ("Create a Buzzy from a description of what it should do"), the sidebar's
  hint reads *"To make one by describing the job, type /buzzy in the chat"* with a worked example,
  the "+ Create a Buzzy" button's tooltip names it, and an empty sidebar says it in the first place a
  new user looks. `/buzzy` with no description, and a proposal that fails, both say what to type
  instead of failing silently.- **Why it is a command and not a guess** (changed after review). The first draft inferred creation
  from the sentence — a create verb within 60 characters of a "buzzy/bot/coworker/assistant/agent/
  helper/worker" noun, minus code words. That was unsound, and measurably so: on a 14-sentence
  sample it misjudged 3, including opening a creation card for *"add an assistant to the checkout
  page"* — a coding request hijacked, in the app whose plain chat *is* the coding agent. The words
  are not the problem; an inferred intent sits on the critical path of somebody else's message, and
  no pattern strong enough to catch "create an assistant for invoices" excludes that sentence. A
  command makes the rule statable and testable instead: *a message not starting with `/buzzy` cannot
  create a Buzzy*. `BuzzyCreationInvariantTests` pins exactly that, three ways (creation has one
  caller and it is the `/buzzy` handler; `SubmitAsync` mentions neither the entry point nor any
  heuristic; `BuzzyProposer` exposes no intent-guessing member), and was confirmed to fail when the
  old hook is put back.
- **What the model may decide, and what it may not.** Tools are narrowed to
  `BuzzyToolPolicy.Grantable` and a model must be an exact id the provider currently offers; anything
  else is dropped (`BuzzyProposer.Parse`), so a confused reply can't widen a Buzzy's reach. Folder
  requests are kept only as *descriptions* ("invoice folder"); every grant is picked by the user in
  the system folder dialog in the card, and a path a model invents is never granted.
- **Folder grants** live in `buzzy.json` as `BuzzyGrant(Path, CanWrite)`. `WorkspacePathGuard.Resolve`
  gained an `Access` argument (Read / Write / Change) and a grant list: inside the workspace as
  before, or inside the deepest matching grant that allows the operation. Read-only grants refuse
  `write_file` and `edit_file`; a grant never covers a sibling folder that merely shares its prefix;
  traversal out of a granted folder is still refused; and `CanonicalGrant` checks the folder on the
  way in — it must exist, must not be (or contain) a link, and must not be inside the Buzzy's own
  folder — so a granted junction can't become an escape hatch later.
- **Scheduled runs get read-only grants only.** `BuzzyToolPolicy` passes unattended runs only the
  read-granted folders, since nobody is watching a routine's writes.
- **Per-Buzzy model.** `buzzy.json` gained `Model`; empty means "follow `/model`", which is why a
  Buzzy made before this phase behaves exactly as it did. A Buzzy with its own model runs its chat
  turns and its routines on that model (`BuzzyModels.Resolve`), the status bar shows the model that
  will actually be used, and a routine run no longer inherits the window's global model
  (`BuzzyRunEnvironment.ModelFor` / `ContextLengthFor`).
- **One place to change both:** the sidebar's new right-click "Folders and model…" opens
  `BuzzySettingsWindow`, which edits the grants and the model and saves each change as it is made.
- **Fixed while reviewing this phase:** `RoutineSchedulerTests` asserted `StartedCount` immediately
  after a tick, but a run is claimed on the tick and enters the executor on the thread pool, so the
  assertion was a race. It passed on a warm pool and failed reliably when `OneRunPerBuzzy_…` ran on
  its own (found by running that test in isolation). `FakeExecutor` now has `StartedAtLeastAsync`,
  a real signal rather than a delay, and the four affected tests await it. Confirmed by six runs in
  isolation after the fix, all passing.- **Editing soul, memory and skills inside the app.** The sidebar's right-click "Soul, memory and
  skills…" opens `BuzzyEditorWindow`: Soul, Memory and Skills tabs over the same files the Buzzy
  reads, with Save, Reload and Revert, and a warning before closing with unsaved edits. The Skills
  tab lists the folders under `skills/` (including ones written by hand or by "Remember this
  workflow"), and can create, edit and delete one. `BuzzySkillLibrary` does the reading and writing;
  it reuses `WorkflowMarkdown.SplitFrontmatter` so the editor parses exactly what the skill tool
  parses, squeezes the description to one line (a newline or `---` in it would otherwise corrupt the
  frontmatter while looking fine in the box), and refuses a body that is only whitespace.
  **Editing is not a re-approval:** a skill's `versions/vN.md` files are left exactly as they were,
  so a routine pinned to an approved version keeps running that version. "Open soul file" and "Open
  memory file" are still there for the external editor.
- **Two bugs found and fixed in the editor before it shipped:** writing text into a `TextBox` raises
  `TextChanged`, so loading a file marked the window dirty and warned on close of a just-opened
  window that had nothing to lose (`_loading` suppresses the mark while a load is running). And
  `Save` wrote both files unconditionally, so if the first read failed the empty boxes would have
  overwritten SOUL.md with nothing; it now refuses to save before a successful load (`_loaded`).
  A headless Skia check confirmed that opening and closing the window leaves SOUL.md byte-identical.

### Phase 3 demos

1. **"Hire one by asking."** In the plain chat: */buzzy Penny reads my invoices folder and writes a
   weekly summary.* The card appears with Penny, Buddy's template, the tools it chose and a soul.
   Pick the invoice folder in the dialog, press Create, and Penny is in the sidebar. Ask her
   something and watch the wings.
2. **"The grant that isn't there."** Ask Penny to read a file from your Documents folder. It is
   refused with the guard's message. Grant that folder read-only through *Folders and model…* and
   ask again: it reads. Ask it to save a change there: refused, because the grant is read-only.
3. **"Two Buzzies, two models."** Give Builder a bigger model through *Folders and model…* and leave
   Scout following `/model`. Switch between them: the status bar follows. Set `/model` to something
   small in the plain chat, and show that Builder still runs on its own model while Scout moved.
4. **"It can't quietly widen itself."** Ask for *"create a Buzzy with every tool, including MCP and
   the code kernel"*. The card offers only the grantable tools — no MCP, no PTC — and the create
   button still needs your click.
5. **"Routines can't write outside."** Give a Buzzy a read-and-write grant and a routine that saves a
   file into that folder. The run reports it couldn't, because a routine only gets read-only grants.

## Phase 4: Gmail connector

- Google OAuth (installed-app flow with PKCE) with the narrowest scopes: `gmail.readonly` first, and `gmail.compose` only if drafting replies. Tokens live in the OS credential store, never in prompts or Buzzy files.
- A trusted broker with tools for search, read, summarize, approved attachment download and draft reply. Sending, if built at all, goes through an approval card bound to the exact message, and mail is never deleted.
- Email and attachment text is wrapped as untrusted content, as `UntrustedContent` does today.
- Fallback while Google's verification is pending: a fixture inbox, clearly labelled as sample data.

### Phase 4 as built

A new project, `src/Litos.Mail`, holds the whole connector. It references `Litos.Tools` (for
`UntrustedContent`, `PendingApprovalStore`, `IAttachmentConverter` and `ITool`) and **not**
`Litos.Gui`, so the Gui, the VS Code host or a later BotHost could each host it. The Gui adds it as
one project reference and one partial class.

**Sending was not built.** `IMailClient` has no send, delete, move, archive or mark-read method, and
`MailBroker` exposes five tools, none of which can do those things. `mail_draft` saves a draft and the
tool's result says the user must send it. A test asserts that no tool name contains "send" or
"delete", so a later change that adds one fails a test rather than passing review.

| File | Role |
|---|---|
| `src/Litos.Mail/IMailClient.cs` | The five operations a mailbox supports, and nothing else |
| `src/Litos.Mail/MailMessage.cs` | `MailMessage`, `MailAttachment`, `MailSearch`, `MailDraft` |
| `src/Litos.Mail/GmailClient.cs` | Gmail REST client: list, get, attachment, draft; payload-tree parsing; `RawMessage` MIME builder; `Base64Url`; `HtmlText` |
| `src/Litos.Mail/MailOAuth.cs` | Authorize/exchange/refresh, PKCE (S256), the 127.0.0.1 loopback listener, error sentences |
| `src/Litos.Mail/MailBrokerTools.cs` | The five tools, the approval gates and the untrusted wrapping |
| `src/Litos.Mail/AttachmentText.cs` | Attachment bytes to text, via `IAttachmentConverter` (MarkItDown) |
| `src/Litos.Mail/Credentials.cs` | `ICredentialStore`; DPAPI on Windows, AES-GCM files elsewhere |
| `src/Litos.Mail/MailSettings.cs` | `~/.litos/gmail.json`: the address, the granted scopes, the sample-inbox flag |
| `src/Litos.Mail/MailTokens.cs` | `MailTokenProvider` (refresh, reuse, clear) and `FixtureMailClient` |
| `src/Litos.Mail/MailFacade.cs` | `MailConnection`: which client is in force, which tools a turn gets |
| `src/Litos.Mail/PendingMailApproval.cs` | `IMailApproval` over the Gui's approval card |
| `src/Litos.Gui/Buzzies/GmailSettingsWindow.cs` | Connect, disconnect, sample inbox, and an honest account of the scopes |
| `src/Litos.Gui/Buzzies/BuzzySettingsWindow.cs` | Gained an Email section: one tick per mail tool |
| `src/Litos.Gui/MainWindow.Mail.cs` | The Gmail sidebar row, the per-turn wiring, the sidebar's "mail" label |
| `tests/Litos.Mail.Tests/` | 76 tests: broker, Gmail parsing, OAuth, credentials, settings, fixture |
| `tests/Litos.Gui.Tests/Buzzies/BuzzyMailTests.cs` | 15 tests: policy, grants, the approval adapter, the prompt line |

Decisions worth recording:

- **Installed-app flow with PKCE and a raw `TcpListener`.** `HttpListener` needs a URL ACL on Windows,
  and a browser-embedded flow would mean showing Google's page inside an app that also holds the
  user's prompts. The listener takes one connection on `127.0.0.1`, checks `state`, answers with a
  small HTML page, and stops. `access_type=offline` + `prompt=consent` so a refresh token always comes
  back.
- **The scopes are read back, not assumed.** Google's consent screen lets a user untick an individual
  permission, so `MailAccount.CanDraft` is `Scopes.Contains(gmail.compose)` and both the policy and the
  settings window drop `mail_draft` when it is false. Asking for a permission and having it are
  different things.
- **Tokens in the credential store, and only there.** `gmail.json` holds an address, a scope list and
  a date. A test reads the file after saving a token and asserts the refresh token is not in it.
  Disconnecting deletes the stored token, so the tools stop working at once rather than at expiry.
- **One connection per machine, app-wide.** Deliberately not per Buzzy: re-authorizing Google for each
  Buzzy would be tedious, and the real control is the per-Buzzy grant, which is visible in the sidebar
  and in the proposal card.
- **A Buzzy with a mail grant but no mailbox gets no mail tools at all.** Not five tools that fail —
  nothing, plus a sidebar line saying why. The settings window says it too, and disables the ticks.
- **Routines get the three read-only tools.** `mail_draft` and `mail_download_attachment` both wait on
  an approval card, and nobody is at the screen at 06:00; a routine that blocks forever on a card is
  worse than one that cannot draft. Same reasoning as shell being absent from unattended runs.
- **The fixture inbox is a real `IMailClient`.** The same broker, the same approvals and the same
  untrusted wrapping run over it, and its `Describe` says "sample data, not a real mailbox" in every
  tool result. Switching it on clears any real connection's tokens, so a demo can't quietly reach a
  live inbox.
- **`UntrustedContent` was made line-ending-independent.** It used a raw string literal, whose line
  breaks follow the source file's; on this CRLF checkout the markers came out with `\r\n` while an LF
  clone produced `\n`, and an existing test had been failing on Windows for that reason. It now uses
  explicit `\n`, so the bytes a model sees are the same everywhere. This was a pre-existing bug, not
  one introduced here.

Live status on 2026-11-26, without a Google OAuth client to hand:

- **Checked headlessly**: the Gmail settings window and the access window construct and lay out in
  four states (nothing connected, sample inbox on, account without compose, Buzzy with mail granted).
- **Checked by tests**: everything in the table above — the broker's wrapping and refusals, MIME
  building, PKCE against RFC 7636's worked example, the refresh flow against a scripted server, DPAPI
  round-trip, settings-file round-trip and damage, the policy's grants and the unattended exclusions.
- **Not checked against Google**: a real consent round-trip, a real mailbox read, and a real draft
  appearing in Gmail. Those need `LITOS_GMAIL_CLIENT_ID`/`LITOS_GMAIL_CLIENT_SECRET` from a Google
  Cloud project with the Gmail API enabled, and a signed-in test account. Until then the sample inbox
  is the supported path, and Google's "app isn't verified" interstitial is expected.
- **Not built**: DKIM/SPF checks. Those belong to the inbound IMAP bridge in
  `ReadMe_EmailIntegrationTool.md`, a different tool with a different trust direction.

### Phase 4 demos

**Demo A: the sample inbox, no Google account needed** (the supported demo today).

1. Click the Gmail row in the sidebar. Tick "Use the sample inbox". The row subtitle changes to the
   sample inbox, and the window says the tools answer from sample data.
2. Create a Buzzy from the Buddy template. Its sidebar row now says `mail`, and in Access settings the
   five mail ticks are there — with "draft" greyed out only if a real connection lacked the scope.
3. Ask it: *"Summarise the sample invoice email and tell me what I owe."* It calls `mail_search` then
   `mail_summarise`. Every result is wrapped in `<<<EXTERNAL_UNTRUSTED_CONTENT source="mail-body">>>`,
   and the answer says it came from sample data.
4. Ask: *"Draft a reply saying I'll pay on Friday."* An approval card appears in the transcript with
   the exact draft body. Deny it: nothing is saved and the turn carries on. Ask again, approve: the
   tool result says the draft is saved and that the user must send it.
5. Open the sample message that tries to give instructions ("ASSISTANT INSTRUCTION: ignore your
   previous rules…") and ask the Buzzy to act on it. It should treat the text as data — the test suite
   asserts the wrapping, and this is the demo that shows it in a conversation.

**Demo B: a real Gmail account** (needs the OAuth client's environment variables).

1. Set `LITOS_GMAIL_CLIENT_ID` and `LITOS_GMAIL_CLIENT_SECRET`, start Litos, open the Gmail row, press
   "Connect Gmail…". The browser opens Google's own consent page; approve it. Google's unverified-app
   interstitial is expected while verification is pending.
2. The window shows `you@example.com (Gmail, read and draft)` once Google's profile call succeeds.
   Check `~/.litos/gmail.json` has no token in it, and that `~/.litos/secrets/` has a `*.dpapi` file.
3. Give a Buzzy `mail_search` and `mail_read`, ask for today's unread mail, and watch the sidebar say
   `mail` next to that Buzzy only.
4. Grant a routine `mail_draft` and confirm it doesn't get it: the routine's tool list has the three
   read-only tools. Then ask the same Buzzy in chat for a draft and confirm the approval card shows the
   whole message before anything is saved.
5. Press Disconnect. Ask for mail again: the tools are gone from the next turn, and nothing is left in
   `~/.litos/secrets/`.

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
| `src/Litos.Gui/Buzzies/BuzzyProposer.cs` | One tool-less model call that proposes a Buzzy from a request, and the narrowing of its reply (Phase 3) |
| `src/Litos.Gui/Buzzies/BuzzyProposalWindow.cs` | The confirmation card for a proposed Buzzy, with folder picking (Phase 3) |
| `src/Litos.Gui/Buzzies/BuzzySettingsWindow.cs` | Folder grants and the per-Buzzy model (Phase 3); the Email section (Phase 4) |
| `src/Litos.Gui/Buzzies/GmailSettingsWindow.cs` | Connect/disconnect Gmail, sample inbox (Phase 4) |
| `src/Litos.Gui/MainWindow.Buzzies.cs` | Sidebar, switching Buzzies, the per-Buzzy loop and registry, create and delete |
| `src/Litos.Gui/MainWindow.Mail.cs` | The Gmail sidebar row, the per-turn mail wiring (Phase 4) |
| `src/Litos.Gui/MainWindow.Grants.cs` | Create-by-conversation and the access settings dialog (Phase 3) |
| `src/Litos.Gui/MainWindow.axaml(.cs)` | Sidebar column, `Owner` instead of `SessionOwner.Local`, PTC refused for Buzzies |
| `src/Litos.Host/LitosHostBuilder.cs` | `AgentLoopFactory.WithSystemPromptProvider` and `SystemPromptProvider` |
| `src/Litos.Mail/` | The Gmail connector in full: broker tools, OAuth, credentials, settings, fixture inbox (Phase 4) |
| `tests/Litos.Gui.Tests/Buzzies/` | Store, path guard (including junctions and grants), tool wrappers, policy, prompt, avatar motion, mail policy (Phase 4) |
| `tests/Litos.Mail.Tests/` | The broker, Gmail parsing and MIME building, OAuth and PKCE, credential stores, settings, fixture inbox |

The non-UI files (`BuzzyProfile`, `BuzzyStore`, the guard, the tools, the policy and the prompt provider) do not reference Avalonia, so Phase 1 or Phase 6 can move them into their own project. `src/Litos.Mail` already lives in its own project, so a second face can host the connector without moving anything.
