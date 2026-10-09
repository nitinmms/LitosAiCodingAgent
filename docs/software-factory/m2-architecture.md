# Software Factory M2 — implementation architecture

M2 is "collaboration and concurrency" in [ReadMe_LitosSoftwareFactory_V1.md](../../ReadMe_LitosSoftwareFactory_V1.md) §18. It builds on the M1 code described in [m1-architecture.md](m1-architecture.md), whose gate was met on 2026-10-06 ([evaluation/m1-results.md](evaluation/m1-results.md)). This document turns the M2 list into changes, schema, tests and a build order. Where it cites existing code, the names were checked against `litos-software-factory-v1` at `e85b6da`.

## 1. Starting point

The six M2 items are not equally far along. M1 already built part of several:

| M2 item | Already in M1 | Missing |
| --- | --- | --- |
| Concurrency across repositories, queueing, waiting reasons | The claim transaction (advisory lock, slot count, project lease), a "held by" waiting reason, `FactoryOptions.SlotCap` | A way to set the cap; correctness with more than one slot (§3.1); a slot waiting reason; isolation between concurrent runs |
| Recovery (§16) | Interrupted at host start, liveness by PID and start time, usage reconciliation | Liveness while the host runs; a check of the working copy on resume; a guard against a second host; stops during verify and handoff |
| Identity, roles, membership, audit | Identity with cookie sign-in, the Admin and Member roles, one seeded Admin, owner and actor columns | Invitations, user administration, `ProjectMember` and membership checks, `AuditEvent`, the missing actors |
| Chat before `@factory`, spec stage | The worker's read-only `Chat` tool set, `submit_spec`, the `Specification` table, `Stage.Spec`, the brief's approved-spec section | Everything on the host and in the app: no chat or spec turn is ever started, and a submitted spec is ignored |
| Board, whose-turn labels, task types | Task type chosen at creation; a non-personal turn pill in the app | The board, a server-side turn label, owner, filters, editing the type |
| SSE replay; pause and cancel in the UI | Durable outbox sequence, `Last-Event-ID` replay, snapshot cursor; Pause, Cancel and Resume buttons | Client reconnect and sign-out handling; a project-level stream for the board; an interim "stopping" state |

## 2. Decisions

Made with the user on 2026-10-06, before any M2 code:

- **Shape.** This document first, then one commit per build step (§9), as in M1.
- **First step:** concurrency and recovery.
- **Slot cap:** default 2, set with `FACTORY_SLOT_CAP`.
- **Isolation between concurrent runs:**
  - each run gets its own temporary directory, `<data>/runs/<runId>/tmp`, as `TEMP` and `TMP` for its worker and its verification commands;
  - build and test commands across all runs are limited by `FACTORY_VERIFY_CONCURRENCY` (default 1), so a second run's verify step waits instead of competing for CPU;
  - tests that bind fixed ports can collide across repositories. That is documented as a V1 limit; no ports are allocated.
- **Liveness while the host runs:** a sweep marks a Running run Interrupted when no executor in this host owns it. This is the same rule as at startup: the slot is freed, the lease is kept, and nothing is re-run without the user. A dead worker under a live executor is left to that executor, which already fails the turn or starts a new worker, so each run has one writer.
- **Resume check:** a checkpoint records `HEAD` and the hashes of the changed files. On resume the host compares them with the working copy and lists any difference in a run message and in the resume brief. The run continues.
- **One host per database:** the host holds a PostgreSQL session advisory lock for its lifetime, and a second host refuses to start.
- **Queue order:** first in, first out by when a run was last queued, not when it was created, so a resumed old run does not jump ahead of newer work.
- **Chat and spec turns** are light `TaskRun` kinds (§5). They reuse the worker, gateway and registry, take a slot, and take no repository lease.
- **Invitations** are one-time links the Admin copies and sends by any channel (§4).
- **Admins see every project** without a membership row (decided 2026-10-07).
- **Outside a project, its resources are "not found":** a signed-in user who is not a member gets 404 for its threads, decisions, findings and events, never 403, so its ids are not confirmed to exist. Factory-wide actions a Member may not take still answer 403 (decided 2026-10-07).
- **Carried open item from M1:** the decision scan still asks existing-data questions that the request settles (blueprint §18, M1 result). It is fixed alongside M2, not before it.

## 3. Concurrency and recovery (build step 1)

### 3.1 Defects that appear with more than one slot

- **The resume race.** A resumed run keeps its id. The old executor commits its stop inside its loop, then disposes the worker and calls `registry.Remove(run.Id)` in its `finally` (`RunExecutor.cs:99-102`). With a cap of 1, the coordinator's `registry.Count < SlotCap` check keeps the run from being claimed again before that removal. With a cap of 2 or more, a fast resume can be claimed while the old executor is still tearing down. The old `Remove` then deletes the new entry, so the new worker's callbacks get 401 and Pause returns 409. `RunCoordinator._running` and the old executor's `ReconcileUsageAsync(run.Id)` have the same overlap.
- **A leaked slot.** If the database fails while a run is stopping, `TryBlockAsync` cannot record it, and the thread stays Running with no executor. It holds a slot and its lease until the host restarts.
- **No waiting reason at the cap.** `ClaimNextRunAsync` returns before scanning when all slots are busy, so queued threads show no reason, or a stale "held by" one.
- **Stops between turns are late.** `ActiveRun.RequestStop` cancels only the current turn. A Pause or Cancel during preflight, verify or handoff is acted on after the next turn, and is lost if no turn follows.
- **Shared toolchain state.** Every worker and verification command shares the host's `TEMP`, the package caches and the build servers.

### 3.2 Design: ownership in one place

Two designs were compared on 2026-10-06. The minimal one fixed the race by committing the stop only after the worker was torn down. The chosen one makes run ownership explicit, so a run stops in the database as soon as it stops, and its slot is released last:

| Piece | Role |
| --- | --- |
| `RunRegistry` | The **slot ledger** and the only count of busy slots. `TryAdd` refuses a duplicate; `Remove(ActiveRun)` removes only that instance; `RunIds` is a snapshot of the held runs. |
| `RunSupervisor` (new, `IRunControl`) | Owns each executor's lifetime: registers the `ActiveRun` synchronously when a run is claimed, runs the executor, and in its `finally` removes the registry entry **last**, after the executor has committed its stop, disposed the worker, deleted the run's temporary directory and reconciled usage. It also carries `RequestStop` and `SteerAsync`, moved from the coordinator. |
| `RunLiveness` (new) | The one rule: a run that is Running in the database with no owner in this host is Interrupted. It runs at startup (no owners yet) and as a sweep every 30 seconds. An orphan's surviving worker is killed first (PID and start time), its usage is reconciled, and its lease is kept. The sweep also checks the host lock (§3.5). |
| `RunCoordinator` | Shrinks to the loop: sweep when due, claim, hand the claim to the supervisor, wait. The sweep, the claim and the supervisor's registration all run on this loop, so a sweep never sees a run that is claimed but not yet registered. |

**Claim.** `ClaimNextRunAsync(slotCap, now, ct, heldRuns)` keeps its advisory-lock transaction and changes in four ways:
- **The cap no longer stops the scan.** The early return at the cap is removed, and the coordinator's own `registry.Count` check goes with it.
- **The held runs count as busy.** `busy` is the number of held runs, and a held run is skipped without a reason: it is still tearing down. With no `heldRuns` (store tests), the store falls back to its Running rows.
- **First in, first out** by `QueuedAt`, then `CreatedAt`. A new `Enqueue` helper sets `QueuedAt` wherever a run becomes Queued: dispatch, rework, a decision answer, resume, recover, and a budget raise.
- **Reasons, most specific first.** A queued thread whose repository is held by another thread keeps the "held by" reason. Otherwise, at the cap, it gets `Waiting for a free slot (n of N busy).` A reason is written only when its text changes, and a claim clears it.

Each run has one writer, so no claim token is stored: within one host, the held-run exclusion already prevents a stale executor from writing over a newer claim, and §3.5 enforces one host.

### 3.3 Stops at any step

- `ActiveRun` gains a run-level `StopToken`.
  - `RequestStop` cancels it as well as the current turn.
  - A cancel is never downgraded by a later pause.
  - `BeginTurn` links the turn to it, so a stop that arrives before a turn starts cancels that turn at once.
- `RunExecutor.GuardedStepAsync` runs preflight, the baseline, verify, handoff and the work before a turn (compaction, the brief) under that token. A user stop becomes a new outcome, `StepStopped(cancel)`.
- After each checkpoint, the loop checks for a stop that arrived between steps.
- The orchestrator maps `StepStopped` to Pause or Cancel, with the step as the resume point (preflight, turn, verify or handoff).
- A cancelled verification saves no evidence (§16: an interrupted test run is never evidence), and its process tree is killed.
- The first clone is not cancelled by a user stop, because a half-finished clone would later look complete. The stop takes effect at the next preflight step.
- Bookkeeping after a push uses no cancellation, so a pushed branch always gets its handoff record.

### 3.4 Isolation and the resume check

- **Temporary directory.** `<data>/runs/<runId>/tmp` is set as `TEMP`, `TMP` and `TMPDIR` for the worker (`WorkerLaunch.TempDirectory`) and for verification commands (`VerificationRequest.TempDirectory`). It is applied last, so neither the host's environment nor a profile can redirect it, and it is deleted when the run's executor ends.
- **Verification gate.** `VerificationGate` holds a semaphore sized by `FACTORY_VERIFY_CONCURRENCY`. It is taken only around the verifier call. A run that has to wait posts one note, and a stop cancels the wait.
- **Fixed ports.** Tests that bind fixed ports may collide across repositories; this is a documented V1 limit. The gate of 1 mostly hides it, but a worker's own shell commands are not gated.
- **Workspace snapshot.** `IWorkspace.SnapshotAsync` returns the branch, `HEAD`, and SHA-256 hashes of up to 1,000 changed files, refusing paths that resolve outside the working copy. The executor takes one after each step and stores it on the run (`TaskRun.WorkspaceSnapshotJson`) with the checkpoint or the stop.
- **Resume check.** When an existing branch is resumed, preflight compares the stored snapshot with a new one. `WorkspaceDrift.Describe` lists the differences in a status note and in the resume brief, and the run continues. Snapshots are taken at step boundaries, so a run interrupted mid-turn also lists that turn's own edits. The wording says "since the last checkpoint" and does not blame anyone. The brief revision moves to `m2.1`.

### 3.5 One host per database

- **The lock.** `IHostInstanceLock` is acquired in `PrepareAsync`, after the pending-migrations check. `PostgresHostInstanceLock` holds `pg_try_advisory_lock` on a dedicated, unpooled connection with TCP keep-alive. It uses a different key from the claim lock, because session and transaction advisory locks share one key space.
- **A second host** gets "Another factory host is already running against this database" and does not start.
- **A lost lock.** The sweep runs `SELECT 1` on the lock's connection. If it fails, the host stops itself, because another host may now own the database.
- **Exemptions.** SQLite and the test hosts use `NoHostInstanceLock`, and `--migrate` takes no lock.
- **Connection pooling.** The state database must be reached directly, or through a session-mode pooler: a transaction-mode pooler cannot hold a session lock.

### 3.6 Settings and schema

| Setting | Default | |
| --- | --- | --- |
| `FACTORY_SLOT_CAP` | 2 | Concurrent runs across all repositories; at least 1 |
| `FACTORY_VERIFY_CONCURRENCY` | 1 | Concurrent verification commands; at least 1 |

Migration `M2Concurrency` adds `task_runs.QueuedAt` (backfilled from `CreatedAt`) and `task_runs.WorkspaceSnapshotJson` (jsonb), and replaces the `(Status, CreatedAt)` index with `(Status, QueuedAt)`.

### 3.7 Commits

Step 1 was completed on 2026-10-07 in these commits:

| # | Commit | What |
| --- | --- | --- |
| 1 | `6f4074f` | Options: the two settings, the liveness interval and the run temporary directory |
| 2 | `44793dd` | Core: `StepStopped`, `WorkspaceSnapshot` and `WorkspaceDrift`, and the resume brief (revision `m2.1`) |
| 3 | `cb44f01` | Store: `QueuedAt`, the snapshot column and migration, the claim rewrite and `Enqueue`, and reconciling one run's in-flight usage |
| 4 | `46ce15d` | Ownership: registry, `RunSupervisor`, coordinator and executor rewiring, and the resume-race regression test |
| 5 | `f6981b4` | Liveness and the host lock |
| 6 | `7b1e5f6` | Stops at any step |
| 7 | `75ef2ce` | Isolation: temporary directories and the verification gate |
| 8 | `0378701` | Resume check |

Two differences from the plan above:

- **The slot cap's default became 2 in commit 4, not commit 1.** Before the registry held a run's slot until its executor had finished, a second slot exposed the resume race. `TestHost` keeps a cap of 1, so the M1 tests keep their order; the concurrency tests set 2.
- **An orphaned run does not hold a slot.** The registry is the only slot count, so a run marked Running that no executor owns takes no slot, even before the sweep interrupts it. It still holds its repository's lease.

## 4. Identity, membership and audit (build step 2)

**Records** (new tables, snake_case as in M1):

| Table | Fields |
| --- | --- |
| `invitations` | Id, UserName, optional Email, Role (Admin/Member), project ids to join (jsonb), TokenHash, ExpiresAt (7 days), CreatedBy, CreatedAt, AcceptedAt, AcceptedUserId, RevokedAt |
| `project_members` | ProjectId, UserId, CreatedBy, CreatedAt; unique `(ProjectId, UserId)` |
| `audit_events` | Id, ActorId, Action, TargetType, TargetId, ProjectId (nullable), DetailsJson, CreatedAt; index `(ProjectId, CreatedAt)` |

**Invitations.** An Admin creates an invitation and the app shows a one-time link, `#/invite/<token>`, once. Only the token's SHA-256 is stored. The invitee opens the link, chooses a password (the existing 12-character policy) and is signed in. Accepting is an anonymous `POST /api/invitations/accept` with its own rate limit, separate from sign-in. Admins can list and revoke pending invitations.

**User administration (Admin):** list users, disable and re-enable, change role, add and remove project membership. The last enabled Admin cannot be disabled or demoted. Disabling must end live sessions: the cookie's principal is re-validated against the `Disabled` flag and the security stamp at most once a minute.

**Membership checks.** Every project-scoped endpoint resolves the project first, from the thread, decision or finding id, and returns **404** to a signed-in user who is not a member, so other projects' ids are not disclosed. Admins see every project. The thread and project lists, the event stream and the review-yield views are filtered the same way. One helper, `ProjectAccess`, does the resolution and the check, so no endpoint carries its own.

**Attribution.** Each user action writes an `AuditEvent` in the same transaction as the change it records. The gaps M1 left are closed: a budget change records who changed it, and a stop of a running task records who asked. Identity changes made through `UserManager` (invite, accept, disable, role) write their audit row right after.

**Migration.** The existing owner and actor columns stay bare UUIDs, without foreign keys to `AspNetUsers`, so a removed user never cascades into task history. Users are disabled, never deleted. The migration adds a `project_members` row for each existing project's creator.

**Step 2 was completed on 2026-10-07** in these commits:

| # | Commit | What |
| --- | --- | --- |
| 1 | `548a0a7` | Tables and store: `invitations`, `project_members`, `audit_events` (migration `M2Identity`) |
| 2 | `5a7b866` | Membership checks: `ProjectAccess` as an endpoint filter on every thread, decision and finding route; filtered lists |
| 3 | `8574846` | An audit row for every user action, including budget changes and stopping a running task |
| 4 | `4828640` | Invitations API: create, list and revoke (Admin); look up and accept (anonymous, rate limited) |
| 5 | `4ab5c25` | User administration, and sessions checked against their account every minute |
| 6 | `104f18d` | Web: the people screen and the invitation page |

Differences from the plan above:

- **The invitation token never appears in a URL the host sees.** The link is `#/invite/<token>`, and the app sends the token to `POST /api/invitations/lookup` and `/accept` in the body.
- **An Admin cannot disable their own account,** in addition to the last-Admin guard.
- **A role change signs nobody out.** Disabling rotates the account's security stamp, which ends its sessions at the next check; a role change only refreshes the session's role at that check.
- **Changing a task's budget is limited to the project's members** (by the membership check) and recorded with the old and new cap.

## 5. Chat before `@factory` and the spec stage (build steps 4 and 5)

**Execution.** Two new `RunKind`s, `Chat` and `Spec`, go through the same claim, registry, worker and gateway path as Implement and Rework, so the per-run secret, metering and recovery all apply unchanged. They differ in three ways:

- **No repository lease.** They read a separate **reading copy** of the project, `<data>/reading/<projectId>`, fetched before each turn and checked out detached at the default branch, or at the task branch once it has been pushed. A reading-copy lock in the host serialises fetch and checkout per project.
- **Read-only tools.** The worker's existing `Chat` tool set, and the `Spec` set (read-only plus `submit_spec`).
- **They take a slot** like any run, so the cap still bounds the machine's load.

**Chat.**
- A message without a leading `@factory` is a chat message. It is stored with its dispatch key and queues a `Chat` run with one turn on the thread's session.
- The reply is a Factory text message, which the app already renders.
- Chat is allowed in Draft, AwaitingHumanTesting and Accepted. In other states the composer either steers the running task (Queued, Running) or says why it cannot chat.
- **Metering:** the usage rows of a chat turn have phase `Chat` and the author as user. They are shown separately and not charged to the task budget (blueprint §5). Each chat turn is bounded by `FACTORY_CHAT_TURN_CAP`. Per-user quotas arrive with the M3 limits; until then chat usage is recorded per user but not refused.

**Step 4 (chat) was completed on 2026-10-08** in these commits:

| # | Commit | What |
| --- | --- | --- |
| 1 | `8a5f222` | Store: `RunKind.Chat`, queued by a plain message, with its own budget on the run; the `M2Chat` migration |
| 2 | `241a5a6` | Worker client: a turn's last message text is kept as its reply |
| 3 | `3638750` | Host: the reading copy, `ChatExecutor`, chat on the messages API, liveness for chat runs, `FACTORY_CHAT_TURN_CAP`, the chat brief (revision `m2.2`) |
| 4 | `f060dd4` | Web: asking a question, the pending answer, chat after acceptance; a plain message is marked as such |
| 5 | `a759e1a` | An answer in progress is shown: a `chat` event (activity, lookups, model calls, start time) at most once a second, and a running clock in the page |

Differences from the plan above:

- **Each answer has a session of its own** (`chat-<runId>`), not a turn on the thread's session, which is the task agent's and would carry every question into the work. The brief carries the thread so far instead: the newest 20 messages, each cut to 2,000 characters.
- **The chat budget lives on the run** (`ChatBudgetCap`, `ChatTokensUsed`, `ChatTokensReserved`). The gateway reserves against it exactly as it reserves against a task's, so a chat over its cap is refused before the model is called. Its usage rows have phase `Chat`.
- **The reading-copy lock is held for the whole answer,** not only the fetch and checkout, because each answer checks out the branch it reads. Two answers on one project take turns.
- **A task branch gone from the remote** (deleted after its merge) falls back to the default branch, and the brief says so.
- **One answer at a time per thread:** a second plain message while one is being answered is refused with 409. Pause, cancel and steering reach the task's run only, never a chat run.
- **An answer is cut off after 30 tool calls or 10 minutes.** Neither is a setting yet.
- **An orphaned chat run is finished, not interrupted:** the liveness rule posts a note that there is no answer, and the task's state is untouched.
- **An answer in progress reports what it is doing,** added after the first real question sat for a minute with nothing to show. The `chat` events stay in the outbox like any other event; the board's stream never carries them.
- **The store marks a plain message** with the payload `{"plain":true}`, because an `@factory` message is stored without its mention. The app shows the mention only on delegations. `@factory` with nothing after it is refused with 400.

**Spec.**
- `@factory spec <text>` queues a `Spec` run from Draft.
- Its `submit_spec` payload is stored as a new `Specification` revision. The table gains `AffectedAreasJson`, `TestPlan` and `OpenQuestionsJson`.
- A `Spec` message kind and card show the revision with Approve. Changes are asked for in a message, and Litos revises the spec in a new revision.
- After the spec run, the thread returns to Draft with stage `Spec` and an unapproved revision. Its turn label is "Awaiting you" (§6). `@factory` is refused until the revision is approved: `POST /api/threads/{id}/spec/{revision}/approve`, with an optimistic revision check (§14 rule 4).
- The first Implement run snapshots the approved revision into its `RunContext`. That turns on the brief's approved-spec section, which M1 already renders but never fills, and the handoff's criteria checklist. The decision scan is told which questions the spec's open questions and criteria already settle.
- A spec run is charged to the task budget, as task work.

**Step 5 (spec) was completed on 2026-10-08** in these commits:

| # | Commit | What |
| --- | --- | --- |
| 1 | `04d58a0` | Core: `RunKind.Spec`, the `ProposeSpec` transition (Running to Draft), the turn label of a draft at the Spec stage, the spec brief (revision `m2.3`) |
| 2 | `6439698` | Store: request, propose and approve; `@factory` refused while a revision waits; each run records the revision it builds; migration `M2Spec`. Also fixes the task's request being taken from the first message |
| 3 | `0cd1bbd` | Host: `SpecExecutor`, `@factory spec`, the approve endpoint, the approved spec in the run's brief and the handoff's criteria check; chat and spec share `ReadingCopies` and `ReadingWorker` |
| 4 | `ad7f059` | Web: the spec card with Approve, Ask for changes and Build it; the composer's spec hints; the handoff's revision line |

Differences from the plan above, and choices the plan left open:

- **A spec is optional.** `@factory <work>` from a draft with no specification starts the work as before.
- **A spec run stops like any run.** Paused, Blocked, PausedBudget and Interrupted all apply, at the Spec stage, and resuming writes the specification again from the start (it is one turn). A turn that proposes nothing blocks the task rather than retrying on its own.
- **A revision is asked for with `@factory spec <changes>`**, which may follow an approved revision too; the new revision must be approved again. A plain message is a question about it, answered as chat. "@factory specify ..." is a request for the work: "spec" must be a word of its own.
- **Only the newest revision can be approved,** and only while the task is a draft at the Spec stage; the endpoint takes the revision the person read, so a newer one makes it a 409.
- **The run's revision is fixed when it is queued** (`task_runs.SpecificationRevision`), and a change request carries the same revision. A task's specification cannot change once delegated, since `@factory spec` is refused outside a draft.
- **The handoff's criteria check** lists each approved criterion the work did not report on under known limitations ("Approved criterion not reported on: ..."), matching words without regard to case, spacing or a closing full stop; the run's brief asks it to report on each in exactly the approved words. The handoff also records the revision it built.
- **The decision scan needed no change:** its brief already shows the approved specification and criteria, now that the run's context carries them.
- **The task's request** given to a change request's review is now its first implement run's request. It had been the thread's first message, which since chat could be a question.

## 6. Board, whose-turn labels and task types (build step 3)

- **Turn label in Core.** `TurnLabels.For(state, specPending)` implements blueprint §7.1. Draft is "Not started", or "Awaiting you" when a spec awaits approval. The API sends the label with each thread, so the app no longer keeps its own copy.
- **"Awaiting you" is personal:** it counts threads in an awaiting-you state that the current user owns. Any member can still act on any thread in their projects.
- **Thread view additions:** `ownerId`, owner display name, project name and the waiting reason.
- **Board view** (`#/board`, the default after sign-in): columns by stage, cards as in the prototype (title, turn label, type, project, owner, budget bar, waiting reason, PR). Filters for project, type, owner and label are applied in the app over `GET /api/threads`.
- **Task types:** the list moves from `FactoryApi` into Core, and `PATCH /api/threads/{id}` lets the owner or an Admin change the type and title.
- **Live board:** `GET /api/events?after=` streams `state` events of every project the user belongs to, from the same outbox, and replaces the 15-second polling.
- **Flow metrics (§12.1) stay in M4,** as the roadmap says.

**Step 3 was completed on 2026-10-07** in these commits:

| # | Commit | What |
| --- | --- | --- |
| 1 | `22dcaba` | `TurnLabels` and `TaskTypes` in Core; `ownerId` and `turn` on every thread; `PATCH /api/threads/{id}`; `GET /api/directory` |
| 2 | `94020e7` | `GET /api/events`, the board's stream; `X-Event-Cursor` on the thread list; a new thread is announced |
| 3 | `3e9b425` | Web: the board, the host's turn label everywhere, a personal "Awaiting you" |
| 4 | `26fad8c` | Web: the board kept live from its stream, polling removed; renaming and re-filing a thread |

Differences from the plan above:

- **Owners' names come from `GET /api/directory`,** which names only the owners of threads the caller can see, rather than being added to every thread.
- **A board event is the thread's whole current view,** not a change to apply, so a client keeps the newer copy by revision and never re-fetches. State events on a thread's own stream now also carry the turn label, title and type.
- **Creating a thread writes a state event,** so other people's boards see it appear. A new thread's stream therefore starts with that event.
- **Known limit:** a thread in a project the user leaves stays on their board until they reload; the stream stops sending its changes at once.

## 7. Event stream and controls (build step 6)

- **Client resilience:**
  - handle `open` and `error`, and show a "reconnecting" indicator;
  - remember the last sequence received and ignore repeats;
  - when the stream closes for good (a 401 after the session expires), return to sign-in, then re-snapshot and resubscribe.
- **Ordering invariant:** every outbox writer locks the thread row first, so per-thread commit order matches sequence order. A Postgres-gated test with concurrent writers pins it, and new writers (chat, spec, audit) must follow it.
- **Retention:** the outbox is kept in full in V1, so there is no expired-cursor case. Pruning arrives with operations work in M4.
- **Controls:**
  - after a 202 from Pause or Cancel, the app shows "Stopping…" until the state event arrives;
  - a Draft thread can be cancelled, which needs a `Draft → Cancelled` transition;
  - Resume is offered for Blocked and Interrupted beside the composer.

**Step 6 was completed on 2026-10-08** in these commits:

| # | Commit | What |
| --- | --- | --- |
| 1 | `d5a17f9` | Store: outbox events commit in sequence order across threads (the outbox lock), with Postgres-gated tests of concurrent writers |
| 2 | `c0c7d05` | Core, store and host: `Draft → Cancelled` |
| 3 | `2e89681` | Web: streams report reconnecting, drop repeats, and are replaced from a fresh snapshot when the host closes them; "Pausing…"/"Cancelling…" after a 202; a draft can be cancelled; Resume beside the composer |

Differences from the plan above:

- **The ordering invariant did not hold across threads.** The thread row lock orders one thread's events, but a sequence is handed out at insert, not at commit, so two threads' writers could commit out of order. The board's stream, which reads across threads, then moved its cursor past an event not yet committed and never sent it; so could the thread list's `X-Event-Cursor`. A Postgres test with eight concurrent writers showed it (sequence 55 committed after 56). Every transaction that writes outbox events now takes a transaction-level advisory lock just before its insert, after its row locks, and holds it to commit. That makes sequence order the commit order everywhere. `FactoryDbContext` refuses an outbox write on PostgreSQL that skipped the lock, so a new writer cannot forget it. Creating a thread, the one writer outside a write scope, now uses one.
- **A stream the host closes for good is replaced, not just reported.** After a refusal (a 401, a 404, a restart that answers with an error), the page waits 1, 2, 5, 10, then every 30 seconds and takes a fresh snapshot. A 401 on that snapshot already sends the client to sign-in. After signing in, the page re-snapshots and resubscribes as on first load, on the same thread. A 403 or 404 ends the attempts and shows the reason. Anything else keeps trying.
- **The board's fresh snapshot replaces the list,** so a thread in a project the user has left now disappears once its stream is reopened. Before, it stayed until a reload. The step 3 known limit is narrowed, not gone: a stream that stays open still keeps the thread.
- **Repeats are dropped by sequence** in the client, per stream, from the cursor it was opened at.
- **"Stopping…" is shown as "Pausing…" or "Cancelling…"** and only while the task is still Running. A state event that arrives before the 202 has been read therefore never leaves it showing.
- **Resume beside the composer** covers PausedUser, Blocked and Interrupted ("Recover and resume"). A budget-paused task resumes from its panel, where the cap can be raised.
- **A cancelled draft** has the note "Cancelled before it was delegated." A question being answered is left to finish, as for any cancel.

## 8. Schema changes

| Step | Migration | Change |
| --- | --- | --- |
| 1 | `M2Concurrency` | `task_runs`: `QueuedAt`, `WorkspaceSnapshotJson`; index `(Status, QueuedAt)` (§3.6) |
| 2 | `M2Identity` | `invitations`, `project_members`, `audit_events`; backfill members; index `task_threads(OwnerId)` |
| 4 | `M2Chat` | Run kind values only (enums are stored as strings), so possibly no migration |
| 5 | `M2Spec` | `specifications`: `AffectedAreasJson`, `TestPlan`, `OpenQuestionsJson`, `SubmittedByRunId` |

Each migration is applied with the host's `--migrate` command, which a normal start refuses to skip.

## 9. Build order

Each step ends green (all test projects and the web tests) and is committed separately.

1. **Concurrency and recovery** (§3).
2. **Identity, membership and audit** (§4). Needed before the board's personal "Awaiting you" and the owner filter.
3. **Board, turn labels and task types** (§6), including the project-level stream.
4. **Chat before `@factory`** (§5), including the reading copy.
5. **Spec stage** (§5).
6. **Event-stream resilience and control polish** (§7).
7. **M2 check** (§10).

The scan fix carried from M1 (§2) can land between any two steps; it changes the scan brief and its tests only.

## 10. Testing and the M2 check

**Tests per step,** in the M1 projects:
- Core.Tests for pure rules (turn labels, queue order, new lifecycle transitions);
- the `FactoryStoreContract` for every new store method, on SQLite and on PostgreSQL when `FACTORY_TEST_DB` is set;
- Host.Tests through `TestHost` for endpoints, membership (404 for non-members), the coordinator with two slots, and recovery;
- Vitest for the app, with `FakeHost` updated alongside each new route.

**SQLite cannot show claim races:** its fallback write lock serialises all writes. Concurrency tests that matter run in the PostgreSQL suite.

**M2 check** (manual, with a real provider and the evaluation repositories):
- two tasks on `filedb-sharp` and `insta-story-generator` run at the same time with `FACTORY_SLOT_CAP=2`, with no lock violation and correct waiting reasons for a third;
- stopping the host mid-run, then starting it, shows Interrupted, and Recover continues;
- a Member invited by link sees only their projects;
- a thread goes chat → spec → approve → implement → handoff;
- one M1 task re-runs within its earlier cost, as a regression check.

### 10.1 M2 check results

| # | Check | Result |
| --- | --- | --- |
| 1 | Two tasks at once, a third waits | **Passed 2026-10-08.** GetOrDefault (`filedb-sharp`, 55,583 tokens, PR #13) and hex colours (`insta-story-generator`, 33,941, PR #5) ran at once. A third task, IsEmpty (`filedb-sharp`), showed "held by" and started when the first handed off; it asked one genuine decision and handed off at 78,550 (PR #14). |
| 2 | Host stopped mid-run, Interrupted, Recover continues | **Failed, fixed, passed 2026-10-09.** See below. "Keys with a prefix" (`filedb-sharp`) recovered mid-Implement, posted the resume check, and handed off at 176,757 tokens including the cut-off attempt (PR #15). |
| 3 | A Member invited by link sees only their projects | **Passed 2026-10-09.** `nitin`, invited by link to `insta-story-generator` only, saw none of `filedb-sharp`'s 15 threads, no admin screens, and a `filedb-sharp` thread's address said "Thread not found". Three display bugs found and fixed; see below. |
| 4 | Chat → spec → approve → implement → handoff | **Passed 2026-10-09.** "Clear a collection" (`filedb-sharp`): a plain question answered from the code, `@factory spec` proposed 14 criteria with no open questions, approved, built with one steer (a batch of Delete operations, no new log operation kind), which the scan recorded and the code kept; handed off and accepted (PR #16, 5 files, 15 new tests, three Minor review findings on test coverage). The budget was raised twice; see below. |
| 5 | One M1 task re-runs within its earlier cost | **Failed on cost 2026-10-09; quality matched.** F3 "Automatic compaction" from the same base as M1's re-run (`18422cc`, on a temporary branch): accepted after one rework at **302,061** against M1's **231,807** (+30%), all five criteria met with tests (M1 left criterion 5 weak). See below. |

**Check 2 first failed.** On Ctrl+C the web server, and with it the model gateway, stopped before the coordinator's stopping token was cancelled. The turn under way failed with "the model gateway could not be reached", and the run was recorded Blocked (TurnFaulted), as if the task had failed. The tests had not shown it because they cancel the token directly, never through a real shutdown in which the gateway goes first. Fixed in `fa6054f`:

- runs are given a token linked to `ApplicationStopping`, which is signalled before anything stops;
- a turn that fails after it (a faulted result, an unreachable worker, a broken stream) leaves the run Running, for the next start to mark Interrupted; the same holds for a spec turn, and a chat says the host was stopping;
- the coordinator claims nothing new once the host is stopping;
- `HostShutdownTests` covers each, and that the same failure with the host running still blocks.

Also from check 2, `6c2e74c`: a status note keeps its line breaks, so the resume check's list no longer runs together on one line.

**Check 3 found three display bugs, none of them a leak.** The host's 404 held throughout; what the app did with it, and with several people on one thread, did not:

- every person's message was labelled with the viewer's own name, so nitin saw admin's request as his. Messages now carry `authorName` from the directory (`2f7d2c6`);
- the address of a thread the user cannot see showed the first thread of their own list under it. It now says "Thread not found", in the same words whether the thread does not exist or is in another project (`39a40e6`);
- a task cancelled mid-way, which stays in its stage's column, showed its turn label "Done" as a green pill. It now says Cancelled, in grey (`70b8e5e`).

**Check 4's cost.** The task was charged 411,328 tokens against a 300,000 default:

| Stage | Model calls | Charged |
| --- | --- | --- |
| Chat (its own budget) | 5 | 20,430 |
| Spec | 14 | 111,501 |
| Decision scan | 7 | 31,102 |
| Implement | 33 | 169,935 |
| Review (full, risk score 8) | 12 | 87,582 |

The spec is charged to the task and cost more than a third of the default budget before anything was built. With the review, the stages around the work cost as much as the work. Both are carried to M3 below.

**Also from check 4:** the decision scan's note joined its assumptions into one sentence (`e44cfe0` puts each on a line), and messages showed Markdown as written, hashes and all (`97d76ae` lays out headings, lists, paragraphs and bold, as elements, never HTML).

**Check 5 by stage,** against M1's F3 re-run on `m1.9`:

| Stage | M1 | M2 |
| --- | --- | --- |
| Decision scan and three questions | — | 23,953 |
| Implement | 99,633 (26 calls) | 143,728 (36 calls) |
| Review | 51,578 (full) | 30,089 (light) |
| Repair of the review's blocking finding | — | 27,873 |
| **First handoff** | **151,211** | **225,643** |
| Rework | 80,596 | 76,418 |
| **Accepted** | **231,807** | **302,061** |

- **The implementation cost 44% more** for a diff of the same size: the regression the check exists to catch, and not yet explained.
- **The scan paid for itself in quality:** one of its three questions was the failure-observability gap behind criterion 4, which M1 missed twice; answered, it was met from the first handoff. It did not pay for itself in tokens.
- **The repair fixed a real bug:** one failed compaction switched automatic compaction off for the life of the instance.
- **About 29,000 of the rework was lost to a follow-up** (below). Without it the total is about 273,000: still over.
- The scan assumed a fixed minimum file size; criterion 2 wants it configurable. The agent sees the request, not the task set's criteria, so this was the rework's to fix, as M1's was for criteria 4 and 5.

**Found on check 5: a question sent while a task works blocks it.** A plain message to a running task is a follow-up, steered into the agent's turn as the person's words. "what are you working on now?" was answered in text, which ends a turn; the turn had changed no files, so the orchestrator blocked it at once as NoProgress (that rule skips the nudge), and the answer was never shown. Resume continued it. To fix: frame a follow-up as an aside to take into account without ending the turn; continue, not block, a turn that ended after one; and post the agent's reply to the thread.

**Carried to M3: reviews that never reply.** On check 2 the light review of a 5-file change spent about 39,000 tokens reasoning and reached the output limit without replying, so the run handed off with the review `DidNotFinish` and said so, as designed (`RunOrchestrator.OnReviewCutOff`). It is the third such review with `deepseek/deepseek-v4.1-flash` (36,006 tokens on F7, 64,717 on the R3 retry). The handling is right; the cost is not, since each attempt buys nothing. Two remedies, for M3's settings and model catalog work: a separate model for review turns, chosen for direct answers over long reasoning; or a cap on reasoning without a reply, which saves tokens but still yields no review.

**Carried to M3: the spec's cost.** A spec is charged to the task, and check 4's took 14 model calls and 111,501 tokens for a one-method feature: it reads the code as widely as an implementation does. A spec allowance, as the scan and the review have, would bound it.

**Carried to M3: implementation cost.** Check 5's implementation took 36 calls and 143,728 tokens against M1's 26 and 99,633 for a diff of the same size. Find out why before M3 adds more to the brief.
