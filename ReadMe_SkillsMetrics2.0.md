# Skills metrics 2.0

Status: design, 2026-11-19. Nothing built. Replaces the skills half of `docs/software-factory/m4-architecture.md` §12.5 ("1.0" below) once built.

Decisions taken (user, 2026-11-19):

- Behaviour signals are captured **live in the worker**, not parsed from transcripts afterwards. No backfill: runs from before the change show "no trace".
- **Opt-in holdout**: an Admin may hold a chosen skill back from a share of tasks, to get a comparison group. Off by default.
- A skill's **`appliesTo`** turn kinds are used **for metrics only**. Prompts are unchanged: every chosen skill stays in every session's system prompt.
- This step is the design only.

## 1. Why

The Skills tab says every built-in skill is at **Too few tasks**, that **nothing was ever loaded**, and that `safe-refactor` was **Never loaded**. Reading the runs' transcripts (§2) tells a different story:

- The skills are followed, and visibly: in 22 of 35 work and rework turns that had `tdd`, the agent wrote tests and ran them red **before** writing the code. That happened in 1 of the 4 turns before skills existed.
- No skill is judged on what it asks for. 1.0 counts only whether the text was in the prompt ("Chosen") and whether it was loaded.
- The one comparison 1.0 offers (first-time acceptance with against without) can never produce a result for a skill that is chosen for every task, because there is no "without".
- Several of the counters are wrong (§3).

2.0 judges each skill on three things, in this order:

1. **Exposure**: which turns it reached, and what that cost, split into turns it applies to and turns it doesn't.
2. **Adherence**: in the turns it applies to, how often the agent did what it asks, step by step.
3. **Effect**: whether the outcome it exists to improve got better (verification first time, blocking findings, rework, people overriding a recommendation), and first-time acceptance against a held-out group when there is one.

## 2. What the transcripts showed (2026-11-18)

Method: a one-off script read every session transcript under `%LOCALAPPDATA%\LitosSoftwareFactory-Installed\runs\*\sessions\local\*\transcript.jsonl` (84 runs: 70 with chosen skills, 14 without). It worked out each session's turn type from its brief's heading, then the order of test runs, test-file edits, production edits and the final `submit_*` call. Tools are called from inside `run_kernel_code`, so it had to parse the C# the agent wrote. That is the main reason 2.0 captures signals in the worker instead (§5).

Work, repair and rework turns with edits, finishing with `submit_work`:

| Skill | What it asks | With skills (35 turns) | Without (4 turns, garmin, 2026-11-06) |
| --- | --- | --- | --- |
| `tdd` | write the slice's tests, see them fail, then the code | **22/35** had a red test run (assertions failing, build fine) right after writing tests | 1/4 |
| `tdd` | run the tests that guard the code before the first edit | **6/35** | 1/4 |
| `tdd` | work in slices | 1/35 say so | 0/4 |
| `verify-before-submit` | build or test after the last edit, before submitting | 29/32; the last test run was green in 23/32 | 4/4 |
| `verify-before-submit` | each criterion names its tests | **30/32** | 1/4 |
| `verify-before-submit` | `knownLimitations` filled | 31/32 | 4/4 |
| `root-cause-debugging` | reproduce first, state the root cause | 2/35 mention a root cause; too few bug tasks to judge | none |

Other turns:

| Skill | Turns | What they did |
| --- | --- | --- |
| `grill-the-request` | 25 scans | 66 choices, every one with a recommendation, 34 with `settledBy` filled; 2 scans asked nothing |
| `review-rubric` | 28 reviews | 17 blocking and 92 minor findings; **39 findings** say "unconfirmed / could not verify / not read". Without skills (4 reviews): 6 blocking, 13 minor, 2 unconfirmed |
| `address-feedback` | 10 reworks | not measured: the script's check failed |

What this says:

- `tdd` and `verify-before-submit` change behaviour. Their main asks are done in about two thirds and nine tenths of turns.
- Some steps are mostly skipped: running the guarding tests before editing, and working in slices. Either they cost too many calls under the brief's few-calls rule, or the skill is too long to keep every step in view. 1.0 cannot see this at all.
- `review-rubric` may be adding noise. Its "say what you would need to check" produces unconfirmed minor findings, each of which a person has to read and none of which is ever judged (§3, item 7).

Limits of the analysis: 35 turns against 4, on different projects and dates. It shows the skills are followed, not that they cause better outcomes. 25 of 737 edits could not be classified as test or production code. Test results were read with regexes.

## 3. What is wrong with 1.0

| # | Problem | Where | Effect on the tab |
| --- | --- | --- | --- |
| 1 | "Offered" counts a chosen skill as offered, but the worker hides chosen skills from the loadable list | `EfFactoryStore.Metrics.cs` `SkillRunOf` (`LibrarySkills`) against `FactorySkillDiscovery.DiscoverAsync` | `address-feedback` shows 35 offered, really 0; `root-cause-debugging` shows 35, really ~31 |
| 2 | Spec and chat runs get the task's chosen skills (`ReadingWorker.StartAsync`) but record no capabilities | `ReadingWorker.cs`, `SkillRunOf` | `grill-the-request` is invisible on spec turns; the skills' cost in 166 chat calls and 101 spec calls is uncounted |
| 3 | The unit is the run, but a run holds scan, work, repair and review turns, and every chosen skill is in all of them | `SkillMetrics.Score`, `CostOf(chars, calls)` | "Tokens a run" mixes turns the skill applies to with turns it can't help (`tdd` in a review) |
| 4 | "Chosen" is only presence in the prompt | `SkillMetrics` | "Chosen 35" reads as "used 35 times" |
| 5 | With-or-without needs tasks without the skill; a skill chosen by default for a task type never has them | `SkillMetrics.Score` (`without`) | permanent "Too few tasks" for the five feature skills |
| 6 | "Never loaded" fires on skills whose task type has never come up | `SkillMetrics.Standing` | `safe-refactor` flagged for rewriting although no refactor task exists |
| 7 | Review findings are never given a verdict: 127 minor findings are `Open` with no `Verdict`, and the 27 blocking ones are `Fixed` | `review_findings` | false findings can't be counted, by 1.0 or by 2.0, until testers judge them or §6.4's proxy is used |

What 1.0 gets right, and 2.0 keeps: loads are recorded (`run_skill_loads` has one row: `root-cause-debugging` in a rework on 2026-11-18); costing text at the cached weight after the first call; the repository-skill approval check; reflection runs left out.

## 4. Terms

- **Turn kind**: `Scan`, `Spec`, `Implement`, `Repair`, `Rework`, `Review`, `Chat` (worker `TurnKind`; `Nudge` counts as the turn it continues; `Reflection` has no skills).
- **Applies to**: a library skill's turn kinds (§6.1). A skill is judged only on **applicable turns**.
- **Exposed turn**: a turn whose system prompt carried the skill, because it was chosen or loaded.
- **Trace**: what the worker records about one turn (§5).
- **Signal**: one yes/no fact computed from a trace or a stored outcome, such as `RedBeforeImplementation`.

## 5. Capturing a turn's trace in the worker

### 5.1 Where

Every tool call already passes through the wrappers that `FactoryToolSetPolicy` builds, whether the agent calls the tool directly or from kernel code (`CreateForBridge` hands kernel code the same registry):

- `GuardedShellTool` wraps `shell`, and already knows a test run (`TestOutputTrimmer.IsTestRun`) and holds the whole output;
- `ConfinedFileTool` wraps `write_file`, `edit_file`, `read_file`, `list_directory` and `search_code`, and knows the resolved path;
- `SubmitWorkTool`, `SubmitReviewTool`, `SubmitSpecTool`, `SubmitPlanTool` and `RequestDecisionTool` end a turn;
- `ReportedSkillTool` sees loads.

A new **`TurnTraceRecorder`** (Worker, one per session) is given to those wrappers. Each wrapper appends one event after its inner tool returns. The recorder never changes a tool's result, and a failure to record never fails the tool, the same rule as `ReportedSkillTool`.

### 5.2 What an event holds

No file content and no command text beyond what is needed to classify it:

| Event | Fields |
| --- | --- |
| `Edit` | path relative to the working copy; `Test` / `Production` / `Other` (§5.3); `write` or `edit` |
| `Read` | path class only (counted, not listed) |
| `TestRun` | runner (`dotnet`, `vitest`, `jest`, `pytest`, `npm`, other); exit code; outcome `Green` / `Red` / `CompileError` / `Unknown`; failed and passed counts when the summary has them; whether a filter was used (`--filter`, `-t`, `-k`, a path) |
| `Build` | exit code; outcome |
| `GitDiff` | that `git diff` or `git status` ran |
| `SkillLoad` | name |
| `Submit` | tool name; for `submit_work`, the counts in §5.4 |

### 5.3 Classifying, in one place

`TraceClassifier` (Worker, static, pure):

- **Test path**: a folder named `test`, `tests`, `__tests__` or `spec`, or a file ending `Tests.cs`, `Test.cs`, `.test.*`, `.spec.*`, `_test.py`, or starting `test_`. `Other` is documentation and config (`.md`, `.json`, `.yml`, `.csproj`, `.props`, `.config`, `.txt`), which counts neither way.
- **Test run**: `TestOutputTrimmer.IsTestRun`, the same rule the trimmer already uses.
- **Outcome**: exit code 0 with a summary that names no failures is `Green`. A compiler or import error (`error CS`, `error TS`, `Build FAILED`, `SyntaxError`, `ModuleNotFoundError`, `Cannot find module`) is `CompileError`. A summary with failures greater than 0 (`Failed: n`, `n failed`, `[FAIL]`) is `Red`. Anything else is `Unknown`. The rules move out of the 2026-11-18 script into tested code, and `TestOutputTrimmer`'s own failure and summary patterns are reused where they already exist.

### 5.4 Submission counts

`SubmitWorkTool` already has the payload. It adds to the trace: criteria, criteria naming at least one test, manual-only criteria, `manualTestSteps` entries, `knownLimitations` entries, `setupChanges` entries, and whether the summary names a root cause. `SubmitReviewTool` adds: blocking, minor, and findings worded as unconfirmed (`unconfirmed`, `could not verify`, `not read`). `SubmitPlanTool` and `SubmitSpecTool` add: choices, choices with a recommendation, choices with `settledBy`, and acceptance criteria.

### 5.5 Sending it

A turn's trace is sent to the host once, on the first of:

- a submit tool or `request_decision` returning success;
- `FactoryToolSetPolicy.Create` starting the session's next non-nudge turn (the last turn ended without submitting);
- the worker stopping.

It goes to `POST /internal/runs/{id}/turn-traces` with the run's secret, as `TurnTraceReport(SessionId, TurnKind, Sequence, StartedAt, EndedAt, Ended, Events, SubmitCounts, TraceVersion)`. `Ended` is `Submitted`, `Decision`, `NoSubmit` or `WorkerStopped`. An unreachable host is logged and ignored. Events are capped at 2,000 per turn, the last ones kept, with a `Truncated` flag.

### 5.6 Storing it

Table `run_turn_traces` (migration `SkillsMetrics2Traces`): `Id`, `RunId` (FK, cascade), `SessionId`, `TurnKind`, `Sequence`, `StartedAt`, `EndedAt`, `Ended`, `TraceVersion`, `EventsJson` (jsonb), `SubmitJson` (jsonb). Unique on (`RunId`, `SessionId`, `Sequence`), so a resent trace replaces the earlier one. Signals are **computed on read** from the events, as the rest of the metrics are. Changing a signal's definition therefore needs no migration, and `TraceVersion` says which classifier made the events.

### 5.7 Spec and chat runs

`ReadingWorker` records a capabilities snapshot for spec and chat runs too, with only `TaskSkills` (names and text length) and `LibrarySkills` filled. That ends §3's item 2. The snapshot is for the report: nothing reads it back to resume a light run.

## 6. The model

### 6.1 `appliesTo`

`LibrarySkill` gains `AppliesTo: IReadOnlyList<TurnKind>`. Empty means all turns, which is also how a skill from before 2.0 and a repository skill are treated. The built-ins ship with it set from what each already says it applies to:

| Skill | Applies to |
| --- | --- |
| `grill-the-request` | Scan, Spec |
| `tdd` | Implement, Rework |
| `root-cause-debugging` | Implement and Rework of a bug task, Repair |
| `verify-before-submit` | Implement, Repair, Rework |
| `review-rubric` | Review (full and light) |
| `address-feedback` | Rework, Repair |
| `safe-refactor` | Implement and Rework of a refactor task |

A task-type condition ("of a bug task") is `AppliesToTypes`, a second list that is empty for all types. Settings > Skills shows both as chips, editable for every skill. `BuiltInSkillSync` carries them like `DefaultFor`. They change no prompt (decision above).

### 6.2 Exposure, from existing data plus §5.7

Per skill, for the period:

- **Applicable turns**: turns of its kinds, and types, in tasks it was chosen for or in sessions that loaded it.
- **Exposed turns not applicable**: every other turn whose prompt carried it.
- **Tokens on applicable turns** and **tokens elsewhere**: its text size at 1.0's cost rule (first call in full, each later call at the cached weight), applied **per turn kind** using `usage_entries.Phase` instead of the run's total call count. `Nudge` calls count toward the kind before them; `LightReview` counts as Review. This replaces "Tokens a run".
- **Offered**: runs where it was in the loadable list, which excludes the runs that had it chosen (§3, item 1). **Loaded** stays as it is.

"Tokens elsewhere" is the number a later decision on prompt scoping would need. It is reported, not acted on.

### 6.3 Adherence signals

Each signal is computed per applicable turn from its trace. A skill's adherence is the share of its applicable, traced turns in which each signal held. A signal is **only counted where it can apply**: `RedBeforeImplementation` needs a turn that wrote both tests and production code, and is "not applicable" in a turn that changed only docs.

| Skill | Signal | Definition (over the turn's ordered events) |
| --- | --- | --- |
| `tdd` | `GuardingTestsFirst` | a `TestRun` before the first `Production` edit |
| `tdd` | `RedBeforeImplementation` | a `TestRun` with outcome `Red` whose last earlier edit was a `Test` edit, followed later by a `Production` edit |
| `tdd` | `GreenAtEnd` | the last `TestRun` before submitting is `Green` |
| `tdd` | `NoTestRemoved` | no test file deleted or emptied (an `Edit` to `Test` whose result shrank it to under 10% of its size) |
| `verify-before-submit` | `VerifiedAfterLastEdit` | a `TestRun` or `Build` after the last `Edit` and before `Submit` |
| `verify-before-submit` | `DiffReadBeforeSubmit` | a `GitDiff` after the last `Edit` |
| `verify-before-submit` | `CriteriaNameTests` | every non-manual criterion names a test |
| `verify-before-submit` | `LimitationsStated` | `knownLimitations` has at least one entry, or the factory's verification passed with no failures |
| `root-cause-debugging` | `ReproducedFirst` | a `Red` `TestRun` before the first `Production` edit |
| `root-cause-debugging` | `RootCauseStated` | the submission summary names a root cause |
| `root-cause-debugging` | `FixesWithinThree` | at most three `Red` → `Production` edit → `Red` cycles on the same test filter |
| `address-feedback` | `ItemByItem` | the summary has one line or numbered item per feedback item or blocking finding listed in the brief (the host knows the count) |
| `address-feedback` | `TestedEachItem` | `Test` edits in the turn ≥ 1 when any item was a code change |
| `grill-the-request` | `EveryChoiceRecommended` | every choice has a recommendation |
| `grill-the-request` | `FactsSettled` | choices with `settledBy` ÷ choices (reported as a share, not yes/no) |
| `review-rubric` | `NoUnconfirmedFindings` | no finding worded as unconfirmed |
| `review-rubric` | `BlockingShare` | blocking ÷ findings (a share, shown beside the trend) |
| `safe-refactor` | `PinnedFirst` | a `Green` `TestRun` before the first `Production` edit |
| `safe-refactor` | `GreenAfterEachStep` | every `Production` edit batch is followed by a `TestRun` before the next |

Signals live in Core as `SkillSignals` (pure functions over a parsed trace), keyed by built-in skill name. A library skill that is not built-in, or a repository skill, has no signals. It shows exposure, loads and effect only, marked "no adherence checks for this skill".

### 6.4 Effect

From tables that already exist, per skill, over the tasks with an applicable turn:

| Outcome | Source | For |
| --- | --- | --- |
| Factory verification passed first time | first `verifications` row of kind `Run` per run | `tdd`, `verify-before-submit`, `root-cause-debugging` |
| Repair cycles per task | `Repair` turns per run | `verify-before-submit`, `tdd` |
| Blocking findings later fixed | `review_findings` `Severity = Blocking`, `Status = Fixed` | `review-rubric` (signal that a finding was real) |
| Minor findings never acted on | `review_findings` `Severity = Minor`, still `Open` when the task is accepted | `review-rubric` (a proxy for noise until findings get verdicts) |
| Reworks per task | `Rework` runs | `address-feedback` (a rework followed by another rework) |
| Recommendation taken | `decisions`: `Answer` equals `Recommendation` | `grill-the-request` |
| Accepted at first handoff | as 1.0 | all |

Each is shown **with** the skill, and **without** it only where a comparison group exists: tasks of the same project and type that were held out (§6.5) or that never had it. The 1.0 minimum stays: 8 tasks on each side before a difference is shown.

### 6.5 Holdout

`LibrarySkill` gains `HoldoutPercent` (0–50, default 0), set by an Admin in Settings > Skills.

- It applies only where a task gets the skill **by default** for its type (`DefaultFor`), never to a skill a person ticked on the task.
- The roll is made once, when the task's skills are first resolved (`TaskSkillSelection`), and is deterministic from the task id and skill name, so a restarted or resumed run gets the same answer.
- A held-out skill is recorded on the thread (`SkillsJson` gains `heldOut`) and noted on it: "`tdd` was held back from this task to compare outcomes (Settings > Skills)". A person can still tick it back on, and the task then counts as neither side.
- Held-out tasks are the "without" side in §6.4. Until a skill has a holdout, its with-or-without cell says **No comparison group**, with a link to turn one on, not "Too few tasks".

### 6.6 Standings

1.0's single standing becomes two, each with its own column:

**Followed** (from adherence, needs at least 5 traced applicable turns):

- *Followed*: every signal of the skill holds in 70% or more of the turns where it applies.
- *Partly followed*: the skill's main signal holds in 70% or more, but another signal holds in under 40%. The report names the weak step.
- *Ignored*: the skill's main signal holds in under 30%.
- *Not judged*: fewer than 5 traced applicable turns, or the skill has no signals.

Main signals: `RedBeforeImplementation`, `VerifiedAfterLastEdit`, `ReproducedFirst`, `ItemByItem`, `EveryChoiceRecommended`, `NoUnconfirmedFindings`, `PinnedFirst`.

**Effect** (1.0's rules, now fed by §6.4 and §6.5): *Earns its place*, *No clear effect*, *Costly, no clear gain* (on tokens on applicable turns), *Worse with it*, *No comparison group*, *Too few tasks*.

**Not applicable yet** replaces *Never loaded* when the skill had no applicable turn in the period, as with `safe-refactor` while no refactor task exists. *Never loaded* stays for a skill that had applicable turns, was offered in them and was never loaded.

### 6.7 Improvements

| Severity | When | Suggested change |
| --- | --- | --- |
| Fix now | Effect: worse with it | review what it tells the agent |
| Improve | Followed: ignored | the agent doesn't do what it asks: shorten it, or move its main step to the top |
| Improve | Followed: partly, naming the step | that step is skipped in N of M turns: make it cheaper (one call), or drop it |
| Improve | `review-rubric`: unconfirmed findings in 25% or more of reviews | the review is reporting what it couldn't check: tell light reviews to leave those out |
| Improve | Effect: costly, no clear gain | let it load on demand instead |
| Improve | tokens elsewhere at least twice tokens on applicable turns | most of its cost is in turns it doesn't apply to (shown for the prompt-scoping decision) |
| Improve | Effect: no comparison group, chosen by default | turn on a holdout to find out whether it helps |
| Improve | never loaded (as redefined) | rewrite its description, or remove it |
| Improve | changed since its approval | as 1.0 |

## 7. The tab

Skills section of **Skills & lessons**:

- **Summary row**: traced turns in the period, against turns without a trace (older runs); skills followed / partly / ignored; tokens on applicable turns against tokens elsewhere.
- **Table** columns: Skill · How runs get it · Applicable turns · Followed (standing and percent of the main signal) · Weakest step · Tokens on its turns · Tokens elsewhere · Effect · Standing pills. Retired: Chosen, Offered and "Tokens a run" as headline columns. Offered and Loaded stay, in the row's detail.
- **Row detail** (expand): each signal with turns held ÷ applicable; each outcome with and without; the 10 most recent applicable turns, each linking to its thread with the signals it met and missed.
- **Chart**: x = tokens on applicable turns, y = main-signal adherence, shape = how runs get it as in 1.0; the with-or-without change moves to the detail.

`GET /api/metrics/skills` keeps its scope and periods. The response gains the fields above. The old fields stay for one release, marked obsolete, so an older web bundle against a newer host still renders.

## 8. Build plan

One commit per step, every step with comprehensive tests (worker, Core, store on both databases, host, web), as for every factory change.

| Step | What | Tests |
| --- | --- | --- |
| 1 | **Fix 1.0's counters**: Offered excludes chosen; "Not applicable yet" needs `appliesTo`, so for now *Never loaded* waits until the skill's task type has occurred (`DefaultFor`/type seen in the period); spec and chat capability snapshots (§5.7) | Core `SkillMetricsTests` (offered, never-loaded rule); store snapshot of light runs; Host spec and chat runs record capabilities |
| 2 | **`appliesTo`, `AppliesToTypes`, `HoldoutPercent`** on `LibrarySkill`, built-ins set, `BuiltInSkillSync`, settings validation and UI chips | Core settings validation and sync; web Settings > Skills |
| 3 | **Exposure per turn kind** from `usage_entries.Phase` (§6.2) | Core cost per kind, nudge attribution, light review |
| 4 | **Worker trace**: `TurnTraceRecorder`, `TraceClassifier`, hooks in the shell, file, submit and skill wrappers, flush rules, the callback, `run_turn_traces` | Worker: each event from a direct call and from kernel code, classification table-driven over real outputs from the 2026-11-18 runs (dotnet, vitest, pytest, compile errors), flush on submit / next turn / stop, unreachable host, cap; Host callback 401 / 400 / replace; store both databases |
| 5 | **Signals and Followed standing** (§6.3, §6.6) in Core | `SkillSignalsTests`: every signal true, false and not applicable, from hand-built traces; standing thresholds; skills without signals |
| 6 | **Effect from outcomes** (§6.4) and **holdout** (§6.5): the roll, `heldOut` on the thread, the note, ticking back on | Core outcomes per skill, comparison groups, minimums; Host roll determinism, resume, person override |
| 7 | **API and tab** (§7), improvements (§6.7) | Host `MetricsApiTests` new and obsolete fields; web table, detail, chart, summary, empty and "no trace" states |
| 8 | **Live check** on the installed factory: one feature task and one bug task; compare the tab against a manual read of both transcripts | recorded in this doc |

Steps 1–3 need no worker change and correct the tab on their own. Step 4 is the first that needs a new worker exe, so it needs the MSI to carry it.

## 9. Not in scope, and open

- **Prompt scoping** (putting a skill only in the turns it applies to) is not part of this. §6.2's "tokens elsewhere" is the evidence for deciding it later.
- **Finding verdicts**: no review finding has ever been judged, so false findings can't be measured. §6.4 uses "minor and never acted on" as a proxy. Asking testers to mark findings at handoff would make it exact. That's a separate change.
- **Backfill**: not done (decision above). The transcripts stay on disk under each run's folder, so the 2026-11-18 script could be turned into a backfill later if wanted.
- **Signals for custom skills**: only built-ins have signals. A way for an Admin to declare a signal for their own skill (say, "a `TestRun` before the first edit") would be a later step.
- **Thresholds** (70% / 40% / 30%, 5 turns, 25% unconfirmed) are first guesses, chosen without asking, and open to change after step 8.
