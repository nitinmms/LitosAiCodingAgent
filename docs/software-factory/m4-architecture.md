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
lessons            Id, ProjectId, Number, Status (Suggested | Approved | Dismissed), Enabled,
                   CurrentRevision, ApprovedRevision?, SupersededById?, ProposedByJobId?, CreatedBy?, CreatedAt, UpdatedAt
lesson_revisions   Id, LessonId, Revision, Rule, TopicsJson, PathsJson, SourceKind, SourceRef?, SourceThreadId?,
                   Reason, NormalizedHash, AuthorId?, CreatedAt, ApprovedBy?, ApprovedAt?
```

- **A revision is immutable.** Editing makes a new revision; approval activates exactly the reviewed revision (`expectedRevision` on every command), so a lesson edited after someone read it cannot be approved by mistake. The lesson keeps its newest revision and its approved revision apart: retrieval uses only `ApprovedRevision`, so an Admin's edit of an approved lesson leaves its guidance in place until the edit is approved.
- **Numbers** ("L-3") count per project, under a lock on the project row, which also makes the duplicate check safe against two lessons written at once. `SourceThreadId` records the thread the source belongs to, found when the revision is written, for the app to link to. Past run snapshots (§4.4) keep the text they copied, so editing a lesson never changes what an earlier run was told (scenario 17).
- **Rule** is one instruction, at most 600 characters. **Topics** are lower-case words or short phrases, one to eight. **Paths** are optional globs the lesson applies to (`src/Orders/**`).
- **Source** is one of `HumanCorrection` (a change request), `Decision` (an answered decision), `ReviewFinding` (a finding a person marked Real), `Evidence` (a verification or handoff), or `Manual`. `SourceRef` names the message, decision, finding or handoff, which must belong to a thread of the same project.
- **Duplicates.** `NormalizedHash` is the rule lower-cased, with punctuation removed and whitespace collapsed. A proposal or a new lesson whose hash matches a live (not dismissed) lesson of the project is refused as a duplicate. Lessons that are only similar are shown together on the Lessons page for a person to resolve; nothing is merged.
- **What a lesson may not be.** The host refuses a rule that looks like a secret (common key and token formats, a private key block, a long value given to a secret-sounding name: `LessonRules`), a stack trace, or more than 600 characters. A lesson cannot change policy: whatever it says, the run's tools, budget and verification profile come from host policy (§22's "authority").

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
- `POST /api/lessons/{id}/approve | /dismiss | /disable | /enable` — `{ expectedRevision, idempotencyKey? }`. A repeat is safe without the key: approving the approved revision, dismissing what is dismissed and so on change nothing and record nothing (scenario 19). The key is accepted for §23's contract.
- `GET /api/lessons/{id}` — one lesson.
- `POST /api/lessons/{id}/supersede` — `{ expectedRevision, replacementId }`; the old one is disabled and linked.

### 3.4 App

- **Lessons page**, per project: tabs for Suggested, Approved, Disabled and Dismissed; each lesson shows its rule, topics, paths, source (a link to the thread message, decision or finding), reason, author and approver; Edit, and the Admin's commands. "New lesson" for anyone on the project.
- **Lessons in the top bar**, between Projects and People, at `#/lessons/<project>`.
- **Proposal cards on the thread**, after a reflection (§5): rule, scope, topics, source and reason, with **Remember for project** (Admin), **Edit** and **Dismiss** (Admin), as §21.3. A Member sees the card without Remember and Dismiss, with a line saying an Admin approves.

## 4. Retrieval and run snapshots (build step 3)

### 4.1 Which lessons a run gets

Deterministic, with no model call (§22):

1. **Candidates:** approved, enabled lessons of the task's project. Suggested, dismissed and disabled lessons are never candidates (scenario 16), and no lesson of another project is.
2. **Score:** each lesson topic found, as whole words, in the task's words (title, type label, the request that delegated it, this run's request and any approved specification, lower-cased and split on everything but letters and digits) scores 2; a lesson path matching a path or file name the task's words name scores 1. A lesson scoring 0 is not retrieved.
3. **Order:** score, then most recently approved, then id.
4. **Limits:** at most five lessons and 1,500 tokens (four characters a token), and never more than 2% of the task's remaining budget. A lesson that does not fit is left out and named in the run's snapshot as `NotIncluded: limit`.
5. **Manual selection:** on the thread, "Include a lesson" picks any approved lesson of the project for the next run. A manual pick always ranks first; one that cannot fit is reported, not silently dropped.

### 4.2 When

Lessons are retrieved once per run, **when the run starts**, as task skills are (built 2026-10-16; the plan first said after the decision scan, so its named files could count, but a worker's system prompt is fixed when it starts, before the scan). A resumed run keeps its snapshot. A rework takes a fresh one, and the thread says which lessons it was given. Chat and spec runs take the current set without a snapshot, as they do for skills.

### 4.3 In the system prompt

The worker's `FactorySystemPromptProvider` gains a "Project lessons" section after the task skills: each lesson as `[L-12 r3] rule`, followed by its source in one line. The host passes them in the run's context file beside the skills (`RunSkills` gains a `Lessons` list, or a sibling `lessons.json`; the choice is made in the step). The always-empty lessons section leaves the run brief. Prompt revision m2.5.

The section says what lessons are: guidance from earlier accepted work, which does not override the request, the decisions or the factory's rules, and which the agent raises as a decision when it conflicts materially with what it finds (§22).

### 4.4 Snapshots

```text
run_lesson_snapshots   RunId, LessonId, Revision, Text, Score, Reason (Retrieved | Manual | NotIncluded),
                       IncludedAt, HelpedVerdict?, VerdictBy?, VerdictAt?
```

The text is copied, so a lesson's later revisions never change the record (scenario 17). `GET /api/runs/{runId}/lessons` returns it, and the run's details list the lessons given and those left out.

### 4.5 Inclusion is not evidence that a lesson helped

§12.1 asks for the two to be recorded separately. Inclusion is the snapshot. Whether it helped is a person's verdict, given in the run's details next to each included lesson (`POST /api/runs/{runId}/lessons/{lessonId}/verdict`): Helped, Didn't matter, Misled. A lesson left out cannot be judged. The Metrics tab reports both (§6).

## 5. Reflection jobs (build step 4)

### 5.1 Record and states

```text
reflection_jobs   Id, ThreadId, HandoffId, Status (Pending | Running | DeferredBudget | Completed | Failed | Skipped),
                  Attempts, LastError?, ProposalsJson?, CreatedAt, StartedAt?, EndedAt?
```

- **Created in the Accept transaction**, unique per (thread, accepted handoff), so a repeated Accept or a retry never makes a second job (scenario 19). Accept commits first: nothing about reflection can undo it (scenario 18).
- **Run by the coordinator** as a `Reflection` run when a slot is free, on the project's reading copy, without taking the repository lock, as chat does. It never delays another task's dispatch beyond taking a slot, and like a chat run it is never the task's own run (the thread's latest run stays the task's).
- **Allowance:** `reflectionAllowanceTokens` in the Budgets settings, 30,000 by default, 0 to turn reflection off. If the accepted task has less than that left under its cap, the job becomes `DeferredBudget` and the thread says §23's words: "Task accepted. Lesson reflection is deferred; increase the allowance, add a lesson manually, or skip." Raising the task's cap and choosing Retry runs it.
- **Usage:** each call goes through the gateway and is admitted against both the task's budget and the reflection's own cap (the allowance), whichever has less room, and charged to both; a call with unknown usage keeps its reservation, as any other.
- **Failure:** the job is `Failed` with its error; the thread offers Retry and Skip; the failure counts on the Admin counters (§7).

### 5.2 The reflection turn

A worker session with one tool only, `submit_lessons` (no file or shell tools), taking zero to three proposals `{ rule, topics, paths, sourceKind, sourceRef, reason }`. Its brief carries:

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
| Queue wait for locks and slots | Each stretch a task spent Queued, from its state events, split by the waiting reason those events recorded ("… held by …" for the repository, "Waiting for a free slot" for a slot); a task claimed at once counts for neither. Built this way, not as `StartedAt − QueuedAt`, because a resumed run is queued again while its `StartedAt` keeps the first start |
| Lessons | Lessons included per run, and the helped verdicts (§4.5) |

The **Metrics tab** shows a project picker (or all), the period, a summary of the rates and counts, and tables for time in state and stage, and tokens by phase and model (built as tables, not charts: the figures are few and read best as numbers). Indexes are checked against the queries in the step; `outbox_events (ThreadId, Sequence)` and `usage_entries (ThreadId)` are expected to suffice.

## 7. Operational counters (build step 6)

An Admin-only section of the Metrics tab, from `GET /api/admin/counters`:

- queued, running and paused tasks;
- the oldest pending decision;
- unreconciled reservations: usage entries still `Reserved` or `Unknown` on a run no executor is working (finished, or stopped until someone acts);
- workers: each run marked Running, whether this host's executor holds it and whether its worker process is alive (process id and start time), with its last checkpoint. A run's heartbeat is written only at checkpoints, so a long turn is silent for minutes and age alone would alarm wrongly; a run no executor holds is flagged;
- event backlog: streams read the outbox themselves, so nothing queues to be published. What is counted is the open streams and how long ago the slowest last read a page that was not full (`EventStreams`);
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
3. Copy from the data directory: `keys/` (the Data Protection key ring, without which no stored secret can be read again, m3-architecture.md §2), `skills/`, `runs/` (logs, reports, transcripts) and `projects/` (each project's baselines). Working copies and `venvs/` are left out: GitHub holds handed-off code, and unpushed work is not covered (§24).
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

1. **The §19 scenarios.** Each of the 20 is mapped to its automated test or, where only a live run can show it, a manual check (§9.1). All must pass.
2. **Lessons end to end:** a task accepted, a reflection proposing lessons, one approved by an Admin, a later task on the same project retrieving it (its run details show it), a Member unable to approve, and a lesson from another project never retrieved.
3. **Backup and restore** as §8.5.
4. **Regression:** F2, R3 and F8 re-run on `openrouter` / `deepseek/deepseek-v4.1-flash` with M1's budgets and request texts, from the same base commits as the M3 check. Each must be accepted with at most one rework, with no evidence mismatch and no lock violation, at no more than 20% over its M3 check result (F2 430,086; R3 686,741; F8 533,605 tokens). Reflection cost is reported separately and does not count against this bound.

### 9.1 The §19 scenario map

Each scenario's proof (step 8, 2026-10-17). Test names are `Class.Method`; `Store.` means `FactoryStoreContract`, run on SQLite and on PostgreSQL. A scenario with a manual check is checked live in the M4 check as well; the rest are proven by the tests alone. Every test named passes at step 8.

| # | Scenario | Automated proof | Manual check in §9 |
| --- | --- | --- | --- |
| 1 | A chat message never edits code; one assignment starts exactly one run | `ApiTests.Message_WithoutALeadingMention_IsChat_AndStartsNoWork`, `FactoryMentionTests.AnythingElse_DoesNot`, `ApiTests.SameMessageIdTwice_CreatesOneRun` | — |
| 2 | A small feature gives a pushed branch, draft PR, real test and coverage evidence, then waits for human testing | `RunTests.Delegated_RunsImplementVerifyReviewHandoff_ThenWaitsForHumanTesting` (fakes for git, GitHub and the verifier) | The regression runs (item 4): a real branch, draft PR and report-based evidence on GitHub |
| 3 | One repository, one run at a time; different repositories in parallel up to the slot cap | `RunTests.TwoThreadsOnOneRepository_RunOneAfterTheOther`, `Store.Claim_SameRepositoryHeldByAnotherThread_Waits_AndSaysWhy`, the slot-cap claims in `Store` and `ConcurrencyTests` | — |
| 4 | A task awaiting human testing does not block the repository; rework re-takes the lock and adds commits without force-pushing | `Store.Handoff_ReleasesTheLease_SoAnotherThreadCanUseTheRepository`, `RunTests.ReworkAfterHandoff_RunsOnTheSameBranchAndSession_AndAddsACommit`, `GitWorkspaceTests.PushAsync_RemoteBranchHasDiverged_IsRejected_NotForced` | — |
| 5 | A decision pauses the run; the answer resumes it without losing context | `RunTests.Decision_StopsTheRun_AndTheAnswerResumesTheSameThreadSession`, `Store.Decision_Opened_ThenAnswered_RequeuesTheSameRunWithTheAnswer` | — |
| 6 | An unaffordable call is refused before sending, and a handoff still appears | `GatewayTests.CallThatDoesNotFit_IsRefusedBeforeSending_AndTheRunIsToldWhy`, `RunTests.UnaffordableModelCall_PausesForBudget_AndRaisingTheCapResumes` | — |
| 7 | Raising a cap keeps prior usage and resumes the same task | `BudgetLedgerTests.WithTaskCap_ChangesTheMaximum_NotTheHistory`, `Store.RaisingTheCap_KeepsWhatWasUsed_AndLetsTheNextCallIn`, `RunTests.UnaffordableModelCall_PausesForBudget_AndRaisingTheCapResumes` | — |
| 8 | Unknown usage is never refunded; a 429 waits and retries keeping the reservation | `BudgetLedgerTests.KeepUnknown_KeepsTheReservationCharged`, `Store.UnknownUsage_KeepsTheReservationCharged_UntilItIsReconciled`, `GatewayTests.RateLimited_WaitsAndRetries_KeepingTheReservation_ThenSettlesOnce`, `GatewayTests.ProviderFailsMidResponse_UsageIsUnknown_SoTheReservationStaysCharged` | — |
| 9 | A failing test or blocking finding gets bounded repair; persistent failure is reported honestly | `RunTests.FailingTest_GetsARepairTurn_WithTheFailureInItsBrief_ThenVerifiesAgain`, `RunOrchestratorTests.PersistentFailure_BlocksAfterTwoRepairCycles`, `RunTests.PersistentFailure_IsBlocked_NotHandedOff_AndKeepsTheRepository`, `RunOrchestratorTests.CoveragePersistentlyBelow_IsDisclosedInTheHandoff_NotBlocked` | — |
| 10 | Counts and coverage come only from reports; a watch-mode runner cannot hang a run | `VerificationProfileTests.Validate_UnitTestsWithoutATestReport_IsInvalid_BecauseCountsComeOnlyFromReports`, `ProfileVerifierTests.EverythingPasses_ReportsCountsAndCoverageFromTheReports`, `ProfileVerifierTests.CoverageReportDeclaredButNotProduced_IsUnavailable_NeverMet`, `ProfileVerifierTests.RealProcess_HangingTestCommand_IsKilledAtItsTimeout` | — |
| 11 | No integration tests, app start-up, deployment, merge or database change under the profile | Structural: a profile has only prepare, restore, build and unit-test commands (`VerificationProfile`); the GitHub port has no merge, and `RunTests.Accept_AfterHandoff_ClosesTheTask_AndTouchesNoRepository`; the working copy never holds the token (`GitWorkspaceTests.Token_IsGivenOnlyToCommandsThatTalkToTheRemote`, `GitWorkspaceTests.RemoteRewrittenInTheWorkingCopysConfig_DoesNotRedirectThePush`); the worker gets no database connection string or GitHub token (row 14); the briefs forbid start-up, deployment and database commands (`BriefComposerTests.ReworkAndRepairBriefs_RestateTheExecutionContract`) | The regression runs' worker logs hold no start-up, deploy or migration command (see the note below) |
| 12 | A restart recovers state without repeating unsafe operations, and never takes a lock from a live worker | `RecoveryTests.RunLeftRunningByAPreviousHost_IsMarkedInterrupted_AndRecoverContinuesIt`, the `Recovered_Mid…` orchestrator tests, `LivenessTests.Sweep_LeavesARunWhoseExecutorIsAlive`, `LivenessTests.Sweep_KillsAWorkerTheRunLeftBehind`, `LivenessTests.Prepare_RefusesToStart_WhenAnotherHostHoldsTheDatabase`, `PostgresHostInstanceLockTests.ASecondHost_IsRefused_UntilTheFirstLetsGo`, `Store.Claim_ARunTheHostStillHolds_IsNotClaimedAgain_UntilItIsReleased` | — |
| 13 | A Member cannot see or act on projects they are not in; every action is attributed | `MembershipApiTests.EveryRouteNamingAProjectsThing_ChecksMembership`, `Store.Membership_IsPerProject`, `UsersApiTests.AMember_CannotManagePeopleOrMembership`, `ApiTests.Message_StartingWithFactory_QueuesARun_AttributedToTheUser`, the audit tests (`Store.ARefusedAction_LeavesNoAuditRow` and the `…IsAudited` tests) | — |
| 14 | Workers never get provider keys, the GitHub credential or the connection string | `WorkerIntegrationTests.Worker_HoldsNoProviderKeys` (a real worker process), `WorkerEnvironmentTests.Build_NeverPassesForbiddenOrUnknownVariables`, `WorkerEnvironmentTests.Build_NoSecretValueFromTheHostAppearsAnywhere`, `WorkerEnvironmentTests.Build_AllowedPrefixDoesNotLetACredentialThrough` | — |
| 15 | Accept records approval without merging or deploying | `RunTests.Accept_AfterHandoff_ClosesTheTask_AndTouchesNoRepository`, `Store.Accept_AfterHandoff_MarksTheThreadAcceptedAndDone` | — |
| 16 | A pending, dismissed or disabled lesson is never retrieved; none leaks across projects | `Store.Retrievable_AreOnlyApprovedEnabledLessons_AtTheirApprovedRevision` (now with a dismissed lesson too), `RunLessonsTests.ARun_IsGivenTheApprovedLessonsMatchingItsTask_NeverASuggestionOrAnotherProjects` | Item 2: a lesson from another project is never given |
| 17 | Editing a lesson keeps past run snapshots | `Store.ARunsSnapshot_IsRecordedOnce_InOrder_AndKeepsItsTextWhenTheLessonChanges`, `Store.EditingAnApprovedLesson_KeepsItsApprovedRevision_UntilTheEditIsApproved_AndOldRevisionsNeverChange` | — |
| 18 | Reflection cannot exceed the task budget or block acceptance; saving a lesson by hand needs no model call | `ReflectionTests` (class: acceptance never waits on reflection), `Store.AReflectionsCalls_AreChargedToTheTask_AndRefusedPastTheAllowance_EvenWithTheTaskUnderItsCap`, `Store.WithTooLittleLeft_OrReflectionOff_AcceptingDefersOrQueuesNone_AndStillAccepts`, `LessonsApiTests.AMember_WritesASuggestion_ThatEveryoneOnTheProjectSees` (now asserts no model call) | Item 2: a real reflection after acceptance |
| 19 | Duplicate messages, acceptances or reflection retries create nothing twice | `ApiTests.SameMessageIdTwice_CreatesOneRun`, `Store.Dispatch_SameMessageIdTwice_CreatesOneRunAndOneMessage`, `Store.Accept_AtTheSameMomentTwice_SucceedsExactlyOnce`, `Store.Accept_Twice_IsAConflict_AndTheSecondAccountJoinsNothing`, `Store.CompletingAReflection_StoresItsValidProposalsAsSuggestions_RefusesTheRest_AndIsSafeToRepeat` | — |
| 20 | Losing the database stops new dispatch | `DatabaseLossTests.WhileTheDatabaseIsLost_NothingStarts_AndOnceItIsBack_TheQueuedTaskRuns` (new: every store call fails; the coordinator keeps trying, starts nothing, and starts the waiting task once the database is back); at start-up, `FactoryHostApp.PrepareAsync` cannot read the settings or take the host lock, so the host does not start | — |

**Scenario 11, the part no test proves.** The agent's shell is guarded only against stopping processes by name and shutting the machine down. Nothing stops an agent from running `npm start`, an integration test or a migration command against a database it can reach itself; the brief forbids it, and the factory keeps every credential that would let it push, merge or reach the factory's own database out of the worker. The M4 check reads the regression runs' worker logs for such commands. A shell guard that refuses them would be a follow-up, not part of V1 unless the check finds one.

### 9.2 Results (2026-10-18): items 1 to 3 pass, item 4 fails on cost

Run on the live factory at `eed67c1` (Debug), with the user's choices made before any spend: run all three regression tasks; prove lessons on the regression itself (F2's reflection, a lesson approved by an Admin, F8 on the same project, R3 on the other); start from the M3 check's base commits again (`m3-check-base` branches on `07fb8b8` and `324ec0e`, each project's default branch pointed at them for the check, then set back to `main` and the branches deleted, which closed PRs #21, #22 and insta #7 unmerged).

**Item 1, the §19 scenarios: pass.** Every suite green, PostgreSQL included: Core 1,038, Worker 336, Host 677 (one skip, the opt-in `LiveGatewayTests`, which needs `FACTORY_LIVE_TEST=1`), Infrastructure 859, web 436.

**Item 2, lessons end to end: pass.**

- F2's acceptance queued its reflection, which completed on its first attempt for 9,023 tokens and proposed three lessons (L-1 from the blocking review finding, L-2 from the change request, L-3 from the upsert decision), each citing its source in the thread, none refused. R3's and F8's reflections did the same (three each, 9,548 and 12,787 tokens; F8's numbered 4 to 6 on filedb-sharp).
- A Member of filedb-sharp (an account made for the check, disabled afterwards) was refused approval with 403, "Only an Admin can do that to a lesson.", could list the project's lessons, and got 404 for another project's. The Admin approved L-1 at revision 1.
- F8's run was given L-1 at revision 1, shown in its run details and written to the run's skills file, from which the system prompt's "Project lessons" section is built. It came in as a **manual pick**: its retrieval score was 0 (below).
- R3's rework, a new run on insta-story-generator started after L-1 was approved, was given no lesson.

**Item 3, backup and restore: pass.** The backup paused dispatch, waited for nothing to be working, and wrote 1,474 files and the dump (about 59 MB, at `M4Reflection`), its manifest verified; dispatch resumed. Restored into `litos_factory_m4check` and a scratch folder, and a second host started on the copy at :5181 beside the live one: it came up paused ("A backup is being taken."), its restore check found all 1,474 files unchanged and every secret readable, and it served all 45 threads, F8's opened in full (messages, decisions, pull request, lesson snapshot, reflection). The copy was stopped and its database dropped.

**Scenario 11's manual check: pass.** The six work runs' transcripts hold about 115 shell calls, literal and composed. Every one is a build or test (`dotnet build`, `dotnet test`, `npx tsc`, `npx vitest`), a read-only `git` command or a directory listing: no start-up, deployment, migration, database, network or push command.

**Item 4, regression: fails on cost.** All three tasks were accepted after one rework each, meeting every acceptance criterion, with no evidence mismatch and no lock violation; none came within the 20% bound, and R3 was paused for budget during its implementation, which M1 and M3 record as a failure.

| Task | Result | Tokens (work) | M3 check | Over M3 | Reflection |
| --- | --- | --- | --- | --- | --- |
| F2 JSON Lines export and import | Accepted after one rework: the first handoff lacked the `Stream` overloads criteria 1 and 2 name, a test after reopening (2), and Unicode keys and nested documents in the round trip (5) | 760,952 (358,771 + rework 402,181) | 430,086 | **+77%** | 9,023 |
| R3 Cancel a running generation | **Paused for budget at 587,782 of 600,000 during implementation**; cap raised to 900,000 to see it out. Accepted after one rework: the late-response race (4) and a generation after cancelling (5) were untested, and a `POST /api/story/cancel` that cancelled nothing was removed | 995,362 (751,182 + rework 244,180) | 686,741 | **+45%** | 9,548 |
| F8 Secondary indexes | Accepted after one rework: no test that a lookup leaves non-matching documents unread (4), and none for `Update` or a transaction disposed without committing (3). The review found a real race (a transaction's lookup read a live index after its lock was released), reproduced and fixed | 852,210 (732,034 + rework 120,176) | 533,605 | **+60%** | 12,787 |

Where the extra cost came from, from the per-call usage. Lessons and task skills are not it: no task had a skill, and F8's single lesson is about 120 tokens.

- **Provider cache misses (R3).** Six of R3's implementation calls came back with no cached input, re-sending 33,000 to 43,000 tokens each at full weight: about 155,000 tokens more than the same calls with the usual 90% cache hit. One follows the quota pause (below) and is expected; M3's R3 had none, and neither did F2 or F8 here. OpenRouter routes a model to more than one upstream, and only the one that served the earlier calls holds the cache.
- **Longer runs on the same requests.** F2's first run took a repair cycle M3's did not (53,000, for a real blocking finding), and its rework made 66 calls against M3's 33. R3's review stopped at its allowance (12 calls, 123,983 tokens).
- **Reasoning bursts.** F2's light review spent one call's whole output allowance, 32,768 tokens, on reasoning with no reply.

One run of each is not enough to tell this from run-to-run variance on deepseek, or to say it is a factory regression: at the M3 check the same request texts came in under M1 (F2 −25%, R3 −2%, F8 −8%). **V1 is not met as specified** until item 4 passes; what to change before a re-run is in §11.

Found by the check, besides the cost:

- **A quota-shortened call is reported as an output-limit failure.** The admin's monthly quota (20,000,000; the M3 check and the forms tasks used most of it) ran out mid-check. R3 paused correctly ("your quota has 1,060 left"); F2's rework instead got a call admitted with 5,042 output tokens (`BudgetLedger.Admit` cuts the output limit to what the quota or cap has left), which deepseek spent on reasoning, and was Blocked with "The model reached the output limit … resuming tries the step again", which resuming could not fix. The user raised the quota to 25,000,000 and both resumed. The ledger was right; the stop is wrong: a call whose limit was cut by the quota or cap and that produced no reply should pause for budget, naming which.
- **Reflection's lessons rarely match another task.** Their topics are the finished task's own words (`json lines`, `export`, `stream`, `upsert`), so F8, a secondary-index task on the same project, scored 0 on all three of F2's and would have been given none without the manual pick.
- **Over-asking, unchanged.** Eight questions across the three tasks, every one settled by the acceptance criteria the scan does not see. Seven recommendations matched the criteria; F8's "optionally unique" indexes would have added scope no criterion asks for, and was answered "non-unique only".

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

**Step 2, lesson store, API and Lessons page (2026-10-16).** As §3, with the approved-revision model above. Migration `M4Lessons`. `LessonRules` cleans and checks a draft (length, topics, paths, a source when the kind needs one, no secret, no stack trace) and normalizes the rule for duplicates; the store re-checks it. `ProjectScoped.Project` and `ProjectScoped.Lesson` put the project's lesson routes and every lesson route behind membership, and the route guard test now covers them. A reflection's proposal (no author) is stored but not audited: its job is the record (step 4). Tests: Core `LessonRulesTests`; store (SQLite and PostgreSQL) numbering, approval, the pending edit, stale revisions, duplicates, dismissal, supersession, source checks, and that earlier revisions never change; Host `LessonsApiTests` for who may do what and 404 outside the project; web `Lessons.test.tsx`.

**Step 3, retrieval, snapshots and the system prompt (2026-10-16).** `LessonRetrieval` (Core) scores and limits as §4.1. `FactorySkills.LessonsForAsync` gathers the task's words, its manual picks and its remaining budget; `RunExecutor` chooses and records the snapshot when a new run starts (a rework included) and the thread is told which lessons were given and which left out; a resumed run keeps its `RunCapabilities.Lessons`. Lessons reach the worker in the run's skills file (`RunSkills.Lessons`) and `FactorySystemPromptProvider` adds a "Project lessons" section after the task's skills, saying they do not override the request, decisions or the factory's rules. The run brief's empty lessons section is gone (prompt revision m2.5). Migration `M4RunLessons`: `run_lesson_snapshots` and `task_threads.ManualLessonsJson`. API: `GET /api/runs/{id}/lessons`, the verdict, and `POST /api/threads/{id}/lessons` for picks (anyone on the project; audited; the thread is told). The run's details have a Lessons box with verdicts and "Pick a lesson for the next run". Tests: Core `LessonRetrievalTests`; store (both databases) retrievable lessons (scenario 16), a snapshot recorded once and unchanged by later edits (scenario 17), verdicts, picks; worker prompt; Host `RunLessonsTests`; web.

**Step 4, reflection jobs (2026-10-16).** As §5. `reflection_jobs` (migration `M4Reflection`), unique per accepted handoff. `ApplyUserActionAsync` takes the allowance (`BudgetSettings.ReflectionAllowanceTokens`, 30,000, 0 off, on the Budgets tab) and, in the Accept transaction, queues a `RunKind.Reflection` run whose own cap is the allowance, or defers the job with §23's words. The ledger charges a reflection run to the task and its cap. `ReflectionExecutor` runs one `TurnKind.Reflection` turn whose brief (`reflection.md`, prompt revision m2.6) lists people's messages, answered decisions, every run's review findings with their verdicts and the last handoff, each with the id to cite, plus the project's approved lessons. `CompleteReflectionAsync` checks each proposal (at most three) as a person's lesson is checked, and also that its source is in this task's own thread, stores the valid ones as Suggested with `ProposedByJobId`, records the refused ones with why, finishes the run and tells the thread, in one transaction; a repeat returns the first outcome. A failure, or the host stopping (liveness), fails the job with Retry and Skip; the task stays accepted (scenario 18). The thread shows a "Lessons from this task" panel with the proposals as cards. Tests: store (both databases) queueing, deferral, off, the ledger, completion and refusals, repeats, failure, retry and skip; brief; `submit_lessons`; Host `ReflectionTests` end to end; web.

**Step 5, flow metrics and the Metrics tab (2026-10-16).** As §6. `FlowMetrics.Compute` (Core) is a pure function over rows `GetFlowDataAsync` loads: the tasks created in the period in the caller's projects, their state events (the outbox payload's state, stage and reason), usage, runs and lesson snapshots, and every open decision on a visible task with its task's title. `GET /api/metrics?projectId=&days=` (7, 30 or 90): everyone, over their own projects; another project is not found. A Metrics tab between Lessons and People. No migration. Tests: Core `FlowMetricsTests` (time in state and stage, queue waits by reason, rework and first-handoff acceptance, tokens, decisions, lessons, percentiles); store (both databases); Host `MetricsApiTests`; web `Metrics.test.tsx`.

**Step 6, operational counters (2026-10-16).** As §7. `GetOperationsDataAsync` reads task counts by state, running runs, unsettled reservations, handoff failures (`StopReason.HandoffFailed`, where pushes and pull requests fail) in the last 7 days, reflection jobs by status, open decisions, and each person's charged and reserved use this UTC day and month. `GET /api/admin/counters` (Admins) adds whether the host executes each running run and whether its worker is alive, the event streams, and each person's share of the quotas. An Operations section on the Metrics tab, for Admins, marks what needs attention. Dispatch paused or running is added with step 7, which builds the pause. Tests: store (both databases), `CountersApiTests` (including `EventStreams`), web.

**Step 7, backup and restore (2026-10-17).** As §8, with these choices made with the user: dispatch pauses every run kind, chat and reflection included, and the pause is stored (settings section `dispatch`, `DispatchSettings`), so a restart or a backup that failed half way does not quietly resume it; the scripts reach PostgreSQL inside its container by default (`docker exec`, and `docker cp` for the dump, since PowerShell 5.1 cannot pipe binary output), or with local client tools given `-PgBin`.

- **Pause.** `ClaimNextRunAsync` takes the pause's waiting reason: it claims nothing, and each queued task says "Dispatch is paused by an Admin: …". `POST /api/admin/dispatch` answers `wasPaused`, so a backup resumes only a pause it made itself; asking for what is already so writes nothing. A pause or resume is audited as `dispatch.pause` or `dispatch.resume` with the reason. `GET /api/dispatch` is for everyone (the banner, polled every minute); `GET /api/admin/dispatch` adds the runs still working: those marked Running and any this host still holds an executor for. The Operations section has Pause and Resume, and the counters say who paused and when.
- **One implementation of the manifest.** The scripts pause, wait, dump and restore; the manifest is written and checked by the host (`FactoryBackup` in Infrastructure, behind `--backup-data <folder>` and `--verify-backup <folder>`), so the backup, the restore and the restored host's check cannot disagree on its format. The kept folders are `keys/`, `skills/`, `runs/` and `projects/` (baselines live under it, not in a `baselines/` folder); the manifest lists the dump and each file by path, size and SHA-256, with the host's version and the database's last migration.
- **Restore.** The script refuses an existing database and a non-empty data directory, checks the backup first, restores with `--exit-on-error` into the same PostgreSQL (the roles the dump names exist there), copies the files, and leaves the manifest in the data directory as `restored-from.json`. A copy starts with dispatch paused, because the dump was taken while paused.
- **The restored host's check** (`RestoreCheck`, at start-up when `restored-from.json` is present): every file the backup held is there unchanged (files added since are fine), and the copied key ring reads every stored secret. It logs and shows the result in Operations and does not stop the host. Narrower than "every artifact the database references": the skill library is written again from the database when a revision is missing, and run folders are covered by the file check.
- **Schema check.** `FactoryDatabase.CheckSchemaAsync` returns the pending migrations and the applied ones this build does not know; the host refuses either, naming them.
- **Carried fixes.** Cancelling a task, idle or running, closes its open decisions ("Not answered: the task was cancelled."); the counters and the flow metrics count only open decisions on unfinished tasks, which also drops the one left open on the cancelled "Show how much space compaction would reclaim".

Tests: Infrastructure `FactoryBackupTests` (what is kept and left out, sizes and hashes, a missing, changed, resized or edited-out-of-root file, refusals, manifest format); schema check on PostgreSQL (empty, migrated, migrated by a newer build); store on both databases (claims while paused, audit, decisions closed on both cancel paths and left open on pause, finished tasks' decisions not counted); Host `DispatchApiTests` (a delegated task waits and says why, then runs once resumed; audit; kept over a reload; Members see but cannot change), `RestoreCheckTests`, `BackupCommandsTests`; web (pause with a reason, banner for everyone, resume, the restore report).

Live check (2026-10-17), ahead of the full one in §9: a real backup of the live factory (1,356 files and a 1.4 MB dump, at `M4Reflection`; dispatch paused and resumed), restored into `litos_factory_restorecheck` and a scratch folder, and a second host started on the copy beside the live one. The copy came up paused, its check found every file unchanged and every secret readable, and it served all 42 threads; the copy was dropped afterwards. It found three faults, fixed with the step: a stray empty `docker` file in `system32` that PowerShell tried to open as a document (the scripts now resolve `docker.exe` itself); `Select-Object -First 1` stopping `docker compose ps` early and failing it; and `ASPNETCORE_URLS` in a settings file never reaching Kestrel, so the copy's host tried the live host's port (`FactoryOptions.Urls` now carries it; the environment variable still wins).

**Step 8, §19 coverage (2026-10-17).** The map is §9.1. Every scenario has automated proof; 2, 11, 16 and 18 also get a manual check in the M4 check. Added: scenario 20's test (`DatabaseLossTests`: a store that fails every call while the database is "lost"; the coordinator keeps trying, starts nothing, and starts the waiting task once it is back, with no restart), a dismissed lesson in scenario 16's store test (it named dismissed lessons but had none), and scenario 18's "no model call" on saving a lesson by hand. Found and recorded, not fixed: scenario 11 rests on the brief and on keeping credentials out of the worker, not on the shell refusing a start-up, deploy or migration command (§9.1, below the table).

**Step 9, the M4 check (2026-10-18).** §9.2: items 1 to 3 pass; item 4, the regression, fails on cost (F2 +77%, R3 +45% and paused for budget, F8 +60% over the M3 check), so V1 is not yet met.

## 11. Open, not blocking

- **Item 4 of the M4 check, before a re-run (§9.2).** Pin OpenRouter's upstream for a run so the provider cache holds (or record cache misses in the usage view, so they can be told from the factory's own cost); stop a call cut short by the quota or cap as a budget pause naming which; then re-run F2, R3 and F8, more than once if variance is to be separated from a regression.
  - **Done 2026-10-19: the two fixes.** A call whose output limit the task's budget or the user's quota cut below the allowance (`Admitted.LimitedBy`), and that came back with no reply, is now a budget refusal naming which ("The model spent the 5,042 output tokens your quota had left on reasoning without producing a reply. Raise the quota, or wait for it to reset, then resume."), so the run pauses for budget instead of being Blocked. Cache misses are recorded: each usage entry keeps the input the gateway expected the cache to serve and what the cache read (migration `UsageCacheMiss`; `ActualCachedInput` also counts cache writes, which would hide a miss on Anthropic). A miss is a settled call expected to hit that read under half of it (`UsageCacheMiss`); its extra cost is the missed input less its cached weight. The thread's Budget box totals them and tags each call with its upstream.
  - **Pinning was not done, on R3's own rows.** Of its unexpected implementation misses, all three were on the upstream that served the calls before and after them (InferenceNet, seconds apart): the upstream lost its cache, which no pin prevents. The only routing switch was in the scan (to Sail Research and back), likely because the preferred upstream was unavailable, where a hard pin would have failed the call instead. The gateway keeps asking for the last upstream with fallbacks allowed; revisit if the recorded misses show switches dominating.
  - **The re-run is held** (user, 2026-10-19): cost work carries on after the factory is live.
- **Lesson topics from reflection** are too narrow to reach a different task (§9.2). Candidates: ask the reflection for the project-level concepts a lesson is about as well as the task's own words, or let an Admin edit topics on approval (already possible, but not prompted).

- **Over-asking.** The decision scan asked all three planned questions on J3 although the person's second answer said to use the agent's recommendation for the rest; the re-check does not treat that as settling anything (m3-architecture.md §13). Not in M4's list; a candidate for step 1 if the user wants it.
- **A shell guard for scenario 11.** The agent's shell could refuse commands that start the application, deploy, or change a database, as it already refuses stopping processes by name (§9.1). Taken up only if the M4 check's worker logs show such a command.
- **The skills A/B run** (m3-architecture.md §13, "Not done") can run alongside any step.
- **M2's cost regression** (m2-architecture.md §10.1) stays parked; the M4 regression check will show whether it persists.
