# Software Factory M4 — implementation architecture

M4 is "lessons, operations and V1 release" in [ReadMe_LitosSoftwareFactory_V1.md](../../ReadMe_LitosSoftwareFactory_V1.md) §18. It builds on M3 ([m3-architecture.md](m3-architecture.md)), whose check closed on 2026-10-14, and on the follow-ups built on 2026-10-16: the kernel shipped beside the worker (§12 there), skills chosen per task in the system prompt (§13), and work submitted with no change sent back (prompt revision m2.4). This document turns the M4 list into changes, schema, tests and a build order. Where it cites existing code, the names were checked against `litos-software-factory-v1` at `f8daed3`.

## 1. Starting point

| M4 item | Already there | Missing |
| --- | --- | --- |
| Lessons (§21, §22) | A "Project lessons" section in the run brief, always empty (`BriefComposer`, `RunContext.Lessons`); the worker's `FactorySystemPromptProvider`, which already puts task skills in every session's system prompt | Lesson records and revisions, approval, the Lessons page, proposal cards on the thread, retrieval, run snapshots, and showing which lessons a run used |
| Reflection jobs (§23) | Acceptance (`ApplyUserActionAsync` with `LifecycleTrigger.Accept`); reading workers for chat and spec (`ReadingWorker`); the gateway's per-call reservation | A job record created with acceptance, a reflection turn, its allowance, deferral when the budget is short, Retry and Skip |
| Flow metrics (§12.1) | The data: `outbox_events` keeps every thread's state and stage changes with their time, and is never pruned; `usage_entries` has every call's phase, model and charge; `task_runs` has `QueuedAt`, `StartedAt`, `EndedAt` and kind; `decisions` has open and answered times. A per-task review-yield view (`GET /api/review-yield`) | Computing the metrics, an API for them, and a Metrics tab |
| Operational counters (§24) | Per-user quota use (`BudgetLedger`); run heartbeats (`task_runs.HeartbeatAt`); reconciliation at start-up (§16) | Showing the counters together, for Admins |
| Backup and restore (§24) | Migrations through `--migrate`; Interrupted runs and lock and usage reconciliation at start-up | Pausing dispatch for a backup, the backup and restore scripts, a manifest with hashes, checks on a restored copy, a refusal to run on a schema the build does not match, and a tested restore |
| §19 acceptance scenarios | Automated tests for most scenarios, spread across the Host, Core and Infrastructure tests | A map from each scenario to its proof, tests for the lesson scenarios (16–19), and scenario 20 (a lost database stops dispatch) |
| Regression check | The M1 task set and the M3 check's results (§9.1 there) | — |

## 2. Decisions

Made with the user on 2026-10-16, before any M4 code:

- **Shape.** This document first, then one commit per build step (§10), each with comprehensive tests, as in M1 to M3.
- **Only Admins approve lessons.** This narrows §21, which lets a project Member approve. Members write and edit suggestions; Admins approve, dismiss, disable, re-enable and supersede (§3.2).
- **Reflection runs after every accepted task**, automatically, with a fixed allowance: a factory setting, 30,000 tokens by default, charged to the accepted task's budget (§5).
- **A run's lessons go in the system prompt, not the brief**, as task skills do (m3-architecture.md §13): compaction never summarises them, the provider caches them, and every session of the run sees them, review included. They stay bounded (five lessons, 1,500 tokens) and fixed for the run. This changes §22's context order, which put them in the brief after the execution contract (§4.3).
- **Flow metrics get a new Metrics tab**, seen by everyone for the projects they belong to. The operational counters on it are shown to Admins only (§6, §7).
- **The regression check re-runs three tasks:** F2, R3 and F8, the three the M3 check accepted, on the provider and model the M3 check used for them, `openrouter` / `deepseek/deepseek-v4.1-flash`, with the same budgets, so the results compare (§9).
- **Local-folder projects are not taken up** (decided 2026-10-14). V1 is GitHub-only.

Chosen without asking, and open to change: Members may write lessons but not approve them (§3.2); each lesson rule is at most 600 characters (§3.1); a person can mark whether an included lesson helped, on the task's handoff (§4.5); the backup excludes working copies and virtual environments (§8.2); and the regression check's cost bound is 20% over the M3 check (§9).

## 3. Lessons (build step 2)

### 3.1 Records

```text
lessons            Id, ProjectId, Status (Suggested | Approved | Dismissed), Enabled,
                   CurrentRevision, SupersededById?, ProposedByJobId?, CreatedBy?, CreatedAt, UpdatedAt
lesson_revisions   LessonId, Revision, Rule, TopicsJson, PathsJson, SourceKind, SourceRef?,
                   Reason, NormalizedHash, AuthorId?, CreatedAt, ApprovedBy?, ApprovedAt?
```

- **A revision is immutable.** Editing makes a new revision; approval activates exactly the reviewed revision (`expectedRevision` on every command), so a lesson edited after someone read it cannot be approved by mistake. Past run snapshots (§4.4) keep the text they copied, so editing a lesson never changes what an earlier run was told (scenario 17).
- **Rule** is one instruction, at most 600 characters. **Topics** are lower-case words or short phrases, one to eight. **Paths** are optional globs the lesson applies to (`src/Orders/**`).
- **Source** is one of `HumanCorrection` (a change request), `Decision` (an answered decision), `ReviewFinding` (a finding a person marked Real), `Evidence` (a verification or handoff), or `Manual`. `SourceRef` names the message, decision, finding or handoff, which must belong to a thread of the same project.
- **Duplicates.** `NormalizedHash` is the rule lower-cased, with punctuation removed and whitespace collapsed. A proposal or a new lesson whose hash matches a live (not dismissed) lesson of the project is refused as a duplicate. Lessons that are only similar are shown together on the Lessons page for a person to resolve; nothing is merged.
- **What a lesson may not be.** The host refuses a rule that looks like a secret (the patterns `SecretScrubber` already uses for logs), a stack trace, or more than 600 characters. A lesson cannot change policy: whatever it says, the run's tools, budget and verification profile come from host policy (§22's "authority").

### 3.2 Who may do what

| Action | Member of the project | Admin |
| --- | --- | --- |
| See the project's lessons and proposals | yes | yes |
| Write a lesson (it starts Suggested) | yes | yes, or **Save and approve** |
| Edit a suggestion (a new revision) | yes | yes |
| Edit an approved lesson | — | yes; the new revision is approved with it or left Suggested, as the Admin chooses |
| Approve, dismiss, disable, re-enable, supersede | — | yes |

Every command is audited (`lesson.create`, `lesson.revise`, `lesson.approve`, `lesson.dismiss`, `lesson.disable`, `lesson.enable`, `lesson.supersede`) with the revision it acted on.

### 3.3 API

As §23, under `RequireProjectAccess`:

- `GET /api/projects/{projectId}/lessons?status=` — the project's lessons with their current revision.
- `POST /api/projects/{projectId}/lessons` — a human lesson; `approve: true` for an Admin's Save and approve.
- `POST /api/lessons/{id}/revisions` — `{ expectedRevision, rule, topics, paths, approve? }`.
- `POST /api/lessons/{id}/approve | /dismiss | /disable | /enable` — `{ expectedRevision, idempotencyKey }`; a repeat with the same key returns the first result (scenario 19).
- `POST /api/lessons/{id}/supersede` — `{ expectedRevision, replacementId }`; the old one is disabled and linked.

### 3.4 App

- **Lessons page**, per project: tabs for Suggested, Approved, Disabled and Dismissed; each lesson shows its rule, topics, paths, source (a link to the thread message, decision or finding), reason, author and approver; Edit, and the Admin's commands. "New lesson" for anyone on the project.
- **Proposal cards on the thread**, after a reflection (§5): rule, scope, topics, source and reason, with **Remember for project** (Admin), **Edit** and **Dismiss** (Admin), as §21.3. A Member sees the card without Remember and Dismiss, with a line saying an Admin approves.

## 4. Retrieval and run snapshots (build step 3)

### 4.1 Which lessons a run gets

Deterministic, with no model call (§22):

1. **Candidates:** approved, enabled lessons of the task's project. Suggested, dismissed and disabled lessons are never candidates (scenario 16), and no lesson of another project is.
2. **Score:** each lesson topic found in the task's words (title, request, type label and any approved specification, lower-cased, split on non-letters, stop words dropped) scores 2; a lesson path matching a file the decision scan named (`PlanSubmission.Files`) scores 1. A lesson scoring 0 is not retrieved.
3. **Order:** score, then most recently approved, then id.
4. **Limits:** at most five lessons and 1,500 tokens (four characters a token), and never more than 2% of the task's remaining budget. A lesson that does not fit is left out and named in the run's snapshot as `NotIncluded: limit`.
5. **Manual selection:** on the thread, "Include a lesson" picks any approved lesson of the project for the next run. A manual pick always ranks first; one that cannot fit is reported, not silently dropped.

### 4.2 When

Lessons are retrieved once per run, after the decision scan (so its named files can count) and before the first implement turn. A resumed run keeps its snapshot. A rework takes a fresh one, and the thread says which lessons changed. Chat and spec runs take the current set without a snapshot, as they do for skills.

### 4.3 In the system prompt

The worker's `FactorySystemPromptProvider` gains a "Project lessons" section after the task skills: each lesson as `[L-12 r3] rule`, followed by its source in one line. The host passes them in the run's context file beside the skills (`RunSkills` gains a `Lessons` list, or a sibling `lessons.json`; the choice is made in the step). The always-empty lessons section leaves the run brief. Prompt revision m2.5.

The section says what lessons are: guidance from earlier accepted work, which does not override the request, the decisions or the factory's rules, and which the agent raises as a decision when it conflicts materially with what it finds (§22).

### 4.4 Snapshots

```text
run_lesson_snapshots   RunId, LessonId, Revision, Text, Score, Reason (Retrieved | Manual | NotIncluded),
                       IncludedAt, HelpedVerdict?, VerdictBy?, VerdictAt?
```

The text is copied, so a lesson's later revisions never change the record (scenario 17). `GET /api/runs/{runId}/lessons` returns it, and the run's details list the lessons included, each linked to its lesson and source.

### 4.5 Inclusion is not evidence that a lesson helped

§12.1 asks for the two to be recorded separately. Inclusion is the snapshot. Whether it helped is a person's verdict, given on the handoff card next to each included lesson: Helped, Did not matter, Misled. The Metrics tab reports both (§6).

## 5. Reflection jobs (build step 4)

### 5.1 Record and states

```text
reflection_jobs   Id, ThreadId, HandoffId, Status (Pending | Running | DeferredBudget | Completed | Failed | Skipped),
                  Attempts, LastError?, ProposalsJson?, CreatedAt, StartedAt?, EndedAt?
```

- **Created in the Accept transaction**, unique per (thread, accepted handoff), so a repeated Accept or a retry never makes a second job (scenario 19). Accept commits first: nothing about reflection can undo it (scenario 18).
- **Run by the coordinator** when a slot is free, on the project's reading copy, without taking the repository lock, as chat does. It never delays another task's dispatch beyond taking a slot.
- **Allowance:** `reflectionAllowanceTokens` in the Budgets settings, 30,000 by default, 0 to turn reflection off. If the accepted task has less than that left under its cap, the job becomes `DeferredBudget` and the thread says §23's words: "Task accepted. Lesson reflection is deferred; increase the allowance, add a lesson manually, or skip." Raising the task's cap and choosing Retry runs it.
- **Usage:** each call goes through the gateway against the task's budget; a call with unknown usage keeps its reservation, as any other.
- **Failure:** the job is `Failed` with its error; the thread offers Retry and Skip; the failure counts on the Admin counters (§7).

### 5.2 The reflection turn

A read-only worker session (no file or shell tools; the kernel off) with one completion tool, `submit_lessons`, taking zero to three proposals `{ rule, topics, paths, sourceKind, sourceRef, reason }`. Its brief carries:

- the task request, decisions and answers, change requests and their outcomes;
- review findings with the verdicts people gave;
- the final handoff summary and verification;
- the project's approved lessons, so it does not propose what is already known;
- §21's guidance on what makes a good lesson and what to avoid, and that zero is a good answer.

The host validates each proposal (§3.1: source in this thread, length, no secret, not a duplicate), stores the valid ones as Suggested lessons with `ProposedByJobId`, and completes the job in the same transaction, so a retry cannot duplicate proposals. Refused proposals are named in the job record. The proposals appear as cards on the thread (§3.4).

### 5.3 Thread actions

`POST /api/threads/{id}/reflection/retry` and `/skip`, for a `DeferredBudget` or `Failed` job; audited.

## 6. Flow metrics and the Metrics tab (build step 5)

Computed on read from existing records; no new tables. `GET /api/metrics?projectId=&days=` (7, 30 or 90; all the caller's projects when no project is given):

| §12.1 metric | Computed from |
| --- | --- |
| Time in each stage and state | Consecutive `state` outbox events per thread: the time between one and the next, summed by state and by stage; the median and the 90th percentile across tasks |
| Tokens per task, by stage and model | `usage_entries.Charged` grouped by thread, then by `Phase` and `Model`; median and total |
| Rework rounds per task | Count of `Rework` runs per accepted task; the distribution |
| First-handoff acceptance rate | Accepted tasks with no `Rework` run, over all accepted tasks |
| Open decisions, and the oldest waiting | `decisions` with no answer; the oldest's age and thread |
| Queue wait for locks and slots | `StartedAt − QueuedAt` per run, split by the waiting reason recorded on the thread while it was queued (`StateReason`, as the Board's waiting labels) |
| Lessons | Lessons included per run, and the helped verdicts (§4.5) |

The **Metrics tab** shows a project picker (or all), the period, tiles for the rates and counts, and small charts for time in state and tokens by phase, drawn with the existing app's styles. Indexes are checked against the queries in the step; `outbox_events (ThreadId, Sequence)` and `usage_entries (ThreadId)` are expected to suffice.

## 7. Operational counters (build step 6)

An Admin-only section of the Metrics tab, from `GET /api/admin/counters`:

- queued, running and paused tasks;
- the oldest pending decision;
- unreconciled reservations: usage entries still `Reserved` or `Unknown` after their run ended;
- workers: running runs with their last heartbeat, flagged when older than the liveness limit (§16);
- event backlog: outbox events newer than the oldest connected stream's cursor;
- push failures: runs stopped with a push failure in the period;
- reflection failures and deferrals;
- per-user usage against the daily and monthly quotas;
- dispatch paused or running (§8.1).

## 8. Backup and restore (build step 7)

### 8.1 Pausing dispatch

`POST /api/admin/dispatch { paused, reason }`, audited, and a banner in the app while paused. The coordinator claims nothing new; runs already working carry on to their next stop. `GET /api/admin/dispatch` reports whether any run is still active.

### 8.2 Backup

`deploy/factory-backup.ps1 -Destination <folder>`:

1. Sign in as an Admin, pause dispatch, and wait until no run is active (a time limit, listing the runs still working, then stop without a backup).
2. `pg_dump --format=custom` of `litos_factory`.
3. Copy from the data directory: `keys/` (the Data Protection key ring, without which no stored secret can be read again, m3-architecture.md §2), `skills/`, `runs/` (logs, reports, transcripts) and `baselines`. Working copies and `venvs/` are left out: GitHub holds handed-off code, and unpushed work is not covered (§24).
4. Write `manifest.json`: the dump's and every copied file's size and SHA-256, the host's version and the last migration id.
5. Resume dispatch, whatever happened.

### 8.3 Restore

`deploy/factory-restore.ps1 -Source <folder> -Database <name> -DataDirectory <path>`: checks the manifest's hashes, restores the dump into a new database with `pg_restore`, copies the files, and prints the `.env` lines to point a host at the copy. It never restores over the live database.

A host started on restored data runs its normal start-up recovery: unfinished runs become Interrupted and wait for a person's Resume (§16), locks and unknown usage are reconciled. It also checks every artifact the database references against the manifest and lists any that are missing or changed.

### 8.4 Schema version

The host refuses to start dispatch when the database has migrations the build does not know, or lacks ones it needs, and says which; `--migrate` remains the only way to apply them. (Today `FactoryHostApp.PrepareAsync` stops on pending migrations; the step checks the other direction too.)

### 8.5 Tested

The step's tests cover the manifest, the hash check and the restored host's checks (Infrastructure, PostgreSQL). The M4 check (§9) runs a real backup of the live factory and restores it into a separate database and folder, then starts a second host on the copy and opens a thread.

## 9. The M4 check (build step 9) — V1

1. **The §19 scenarios.** Each of the 20 is mapped to its automated test or, where only a live run can show it, a manual check (step 8 writes the map into this document). All must pass.
2. **Lessons end to end:** a task accepted, a reflection proposing lessons, one approved by an Admin, a later task on the same project retrieving it (its run details show it), a Member unable to approve, and a lesson from another project never retrieved.
3. **Backup and restore** as §8.5.
4. **Regression:** F2, R3 and F8 re-run on `openrouter` / `deepseek/deepseek-v4.1-flash` with M1's budgets and request texts, from the same base commits as the M3 check. Each must be accepted with at most one rework, with no evidence mismatch and no lock violation, at no more than 20% over its M3 check result (F2 430,086; R3 686,741; F8 533,605 tokens). Reflection cost is reported separately and does not count against this bound.

## 10. Build order

1. **Carried fixes.** Check why the J3 finding verdicts did not save, and fix it if it is a bug. A tool denied by the MCP settings reaches the agent as a message naming the policy, not as a C# compile error, and the kernel helpers' descriptions show a call (m3-architecture.md §12, the smaller items).
2. **Lesson store, API and Lessons page** (§3).
3. **Retrieval, snapshots and the system prompt** (§4), with the run details and the helped verdicts.
4. **Reflection jobs** (§5).
5. **Flow metrics and the Metrics tab** (§6).
6. **Operational counters** (§7).
7. **Backup and restore** (§8), including pausing dispatch and the schema check.
8. **§19 coverage:** the scenario map, and tests for whatever has none, including scenario 20.
9. **The M4 check** (§9). Passing it is V1.

Each step lands with its tests and an update to this document, as in M3.

### 10.1 Built

**Step 1, carried fixes (2026-10-16).**

- **The J3 verdicts were not lost; their findings were hidden.** Verdicts save (the audit trail shows them working since 2026-10-09), but thread details carried only the latest run's findings, so a change request made the rework the latest run and the first review's findings left the panel before anyone judged them. `ThreadDetails.EarlierFindings` now carries the earlier runs' findings, oldest run first, and the panel lists them under "From earlier rounds", with their verdict buttons. Review yield already counted every run's findings.
- **A denied MCP tool, called from kernel code, refuses by name.** The worker offers kernel code a stand-in for each tool the run's MCP settings deny (`DeniedMcpTool`, from `WorkerMcp.DeniedTools`, in `FactoryToolSetPolicy.CreateForBridge` for work turns only). The call compiles, is never sent to the server, and returns "… is denied for this run by the factory's MCP settings, so it was not called." The model's own tool list still leaves denied tools out.
- **Helper signatures show an object argument's fields**, one level deep, as the anonymous object to pass: `run_select_query("request", new { sql = <string>[, limit = <int>] })`, where `<object>` was shown before. Byte-stable, as the rest of the description must be for the prompt cache.

## 11. Open, not blocking

- **Over-asking.** The decision scan asked all three planned questions on J3 although the person's second answer said to use the agent's recommendation for the rest; the re-check does not treat that as settling anything (m3-architecture.md §13). Not in M4's list; a candidate for step 1 if the user wants it.
- **The skills A/B run** (m3-architecture.md §13, "Not done") can run alongside any step.
- **M2's cost regression** (m2-architecture.md §10.1) stays parked; the M4 regression check will show whether it persists.
