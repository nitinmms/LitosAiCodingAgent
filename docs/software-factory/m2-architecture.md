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

1. Options: the two settings, the liveness interval and the run temporary directory. `TestHost` keeps a cap of 1 so the M1 tests keep their order.
2. Core: `StepStopped`, `WorkspaceSnapshot` and `WorkspaceDrift`, and the resume brief.
3. Store: `QueuedAt`, the snapshot column and migration, the claim rewrite and `Enqueue`, and reconciling one run's in-flight usage.
4. Ownership: registry, `RunSupervisor`, coordinator and executor rewiring, and the resume-race regression test.
5. Liveness and the host lock.
6. Stops at any step.
7. Isolation: temporary directories and the verification gate.
8. Resume check.

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

**Spec.**
- `@factory spec <text>` queues a `Spec` run from Draft.
- Its `submit_spec` payload is stored as a new `Specification` revision. The table gains `AffectedAreasJson`, `TestPlan` and `OpenQuestionsJson`.
- A `Spec` message kind and card show the revision with Approve. Changes are asked for in a message, and Litos revises the spec in a new revision.
- After the spec run, the thread returns to Draft with stage `Spec` and an unapproved revision. Its turn label is "Awaiting you" (§6). `@factory` is refused until the revision is approved: `POST /api/threads/{id}/spec/{revision}/approve`, with an optimistic revision check (§14 rule 4).
- The first Implement run snapshots the approved revision into its `RunContext`. That turns on the brief's approved-spec section, which M1 already renders but never fills, and the handoff's criteria checklist. The decision scan is told which questions the spec's open questions and criteria already settle.
- A spec run is charged to the task budget, as task work.

## 6. Board, whose-turn labels and task types (build step 3)

- **Turn label in Core.** `TurnLabels.For(state, specPending)` implements blueprint §7.1. Draft is "Not started", or "Awaiting you" when a spec awaits approval. The API sends the label with each thread, so the app no longer keeps its own copy.
- **"Awaiting you" is personal:** it counts threads in an awaiting-you state that the current user owns. Any member can still act on any thread in their projects.
- **Thread view additions:** `ownerId`, owner display name, project name and the waiting reason.
- **Board view** (`#/board`, the default after sign-in): columns by stage, cards as in the prototype (title, turn label, type, project, owner, budget bar, waiting reason, PR). Filters for project, type, owner and label are applied in the app over `GET /api/threads`.
- **Task types:** the list moves from `FactoryApi` into Core, and `PATCH /api/threads/{id}` lets the owner or an Admin change the type and title.
- **Live board:** `GET /api/events?after=` streams `state` events of every project the user belongs to, from the same outbox, and replaces the 15-second polling.
- **Flow metrics (§12.1) stay in M4,** as the roadmap says.

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
