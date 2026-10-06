# Software Factory M1 evaluation results

Results of running the [M1 task set](m1-task-set.md). One row per task, recorded as that file describes. Provider and model for every run: OpenRouter, `deepseek/deepseek-v4.1-flash`. Prompt revision: `m1.1` for F1 to F3, `m1.2` for F4 and F5, `m1.3` from R1.

## Summary

| ID | Accepted | Rework rounds | Repair cycles | Decisions | Tokens used | Cap | Within cap | Wall clock | Evidence mismatches |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| F1 | Yes, first handoff | 0 | 0 | 0 | 993,607 (old rule; about 203,000 under the current one) | 150,000 | No | about 15 min of run time | 0 |
| F2 | Yes, after one rework | 1 | 0 | 0 | 575,884 | 300,000 | No | 12 min (8 + 3 for the rework) | 0 |
| F3 | **No: budget-paused during its rework.** All criteria met in the end | 1 | 2 (both for review findings) | 0 | 971,057 | 600,000 | No | 31 min (19 + 12 for the rework) | 0 |
| F3, re-run with the read limits | **Yes, after one rework** | 1 | 0 | 0 | 231,807 | 600,000 (900,000 with the rework's top-up) | **Yes**, inside the original cap | 9 min of call time (5 + 4 for the rework) | 0 |
| F4 | Yes, after one rework | 1 | 0 | 0 | 401,521 | 600,000 | **Yes** | 11 min (9 + 2 for the rework) | 0 |
| F5 | **No: criteria unmet, and the run ended Blocked once** | 0 | 1 (review finding) | 0 | 554,007 | 600,000 | Yes | 28 min of run time | 0 |
| F5, re-run with the read limits | **No: the rework ended Blocked** (a provider stream that produced nothing), after a first handoff that wrapped the synchronous methods in `Task.Run` | 1 | 0 | 0 | 803,108 | 600,000 (900,000 with the rework's top-up) | Yes | about 45 min of call time, 10 of it in test runs that hung | 0 |
| R1 | **No: budget-paused twice, and one criterion unmet** | 0 | 0 | 0 | 437,338 | 300,000 | No | 11 min | 0 |
| R1, re-run on `m1.8` | **No: budget-paused during its implementation**, before any submission | 0 | 0 | 0 | 288,465 | 300,000 | No | 8 min | 0 |
| R1, re-run on `m1.9` | **No: budget-paused during its implementation**, before any submission | 0 | 0 | 0 | 297,999 | 300,000 | No | about 10 min | 0 |
| R1, re-run with the kernel output cap | **No: budget-paused during its implementation**, before any submission | 0 | 0 | 0 | 293,451 | 300,000 | No | about 15 min | 0 |
| F6 | **No: budget-paused during its rework, and the expected decision was never asked** | 1 | 1 (review finding) | 0 of 1 expected | 1,188,184 | 1,200,000 | No | 32 min (17 + 15 for the rework) | 0 |
| F6, re-run on `m1.7` | **Yes, after one rework.** The expected decision was still never asked | 1 | 0 | 0 of 1 expected | 799,093 | 1,200,000 (1,800,000 with the rework's top-up) | **Yes**, inside the original cap | 50 min of call time (13 + 37 for the rework, of which about 20 stalled on a kernel defect and paused for its fix) | 0 |
| F7, with the decision scan (`m1.11`) | **Yes, after one rework** | 1 | 0 | **2 asked**, including the 1 expected, answered as scripted | 942,772 | 1,200,000 (1,800,000 with the rework's top-up) | **Yes**, inside the original cap | about 95 min of run time (55 + 40 for the rework), not counting time spent Blocked | 0 |
| R4, with the decision scan (`m1.11`) | **No: budget-paused three times during its implementation**, before any submission | 0 | 0 | **3 asked**, including the 1 expected, which was answered against the script | 1,315,680 | 1,200,000 (raised to 1,400,000) | No | about 35 min of run time | 0 |
| R3, with the decision scan (`m1.12`) | **No: budget-paused twice**, once during its implementation and once at its repair. Criteria met, except that the browser-side late-answer race has no test | 0 | 1 (review finding) | **3 asked, none expected**: 2 fair, 1 unnecessary | 909,924 | 600,000 (raised to 850,000, then 1,050,000) | No | about 35 min of run time | 0 |
| R2, with the decision scan (`m1.11`; rework on `m1.12`) | **No: budget-paused four times during its implementation.** All five criteria met after one rework | 1 | 0 | **3 asked, none expected**: 1 fair, 2 unnecessary | 1,279,785 | 600,000 (raised to 1,000,000; 1,300,000 with the rework's top-up) | No | about 45 min of run time (38 + 7 for the rework) | 0 |

**Against the M1 gate so far (7 of 12 run):**

- Accepted with at most one rework: 3 of 7. **The gate needs 7 of 12, so it can be met only if all five remaining tasks pass.**
  - F3 produced code that meets every criterion after one rework, but it paused on budget during that rework, which the task set counts as a failure.
  - F5 is the first task whose handoff does not do what was asked: see its section.
  - R1 paused on budget twice before its handoff, and changed something a criterion said must not change.
  - F6 paused on budget during its rework, after a first handoff that made every existing database unreadable instead of asking how to treat them.
- **Decisions asked: 0 in 7 tasks.** One was expected (F6) and one was warranted (R1).
- Evidence mismatches: 0.
- **Budget overrun: F1, F2, F3, R1 and F6.** F4 and F5 finished inside their caps. See "Budgets" below.
- Estimator 95th-percentile under-estimate: 2.3% on F2, 4.0% on F3, 2.3% on F4 (margin 10%). On F3 one call of 155 was charged more than it had reserved; the task's cap was not passed by it.
- Lock violations: 0.

**After the fixes (re-runs on prompt revision `m1.7`):** F6 was re-run and accepted after one rework, inside its original cap, at 799,093 tokens against 1,188,184 the first time. It still did not ask the decision it was expected to; see its section. R1 was re-run three times and paused on budget during its implementation every time, before submitting anything; see its section. On this model a change the task set calls small costs this repository about 300,000 to 440,000 tokens. F3 was re-run and accepted after one rework at 231,807 tokens, against 971,057 and a budget pause the first time; see its section. F5 was re-run and failed again: its first handoff wrapped the synchronous methods in `Task.Run`, and its rework, which did move to asynchronous file I/O, ended Blocked when a provider stream produced nothing, after spending most of its calls on a test hang it blamed on the environment. With the re-runs, 5 of the 7 tasks run are accepted.

**With the decision scan (`m1.11`):** F7 and R4 each asked the decision the task set expects, first: the first decisions the factory asked in the evaluation. F7 was accepted after one rework at 942,772 tokens, inside its original cap. R4 asked its three questions for 36,012 tokens, then paused on budget three times during its implementation, in 125 calls on a context of up to 122,000 tokens, and was cancelled at 1,315,680 before submitting anything. **6 of the 9 tasks run are accepted** (F1, F2, F3, F4, F6, F7; F3 and F6 on re-runs). The gate needs 7 of 12, so one of the three unrun tasks (R2, R3, F8) must pass.

**R2 (2026-10-05 and 06):** paused on budget four times during its implementation, so it is recorded as failed, although its code met all five criteria after one rework. Its scan asked three questions on a task that expects none. **6 of the 10 tasks run are accepted**; the gate needs 7 of 12, so one of R3 and F8 must pass.

**R3 (2026-10-06):** paused on budget during its implementation and again at its repair, so it is recorded as failed; its handoff meets the criteria except a missing client-side test of the late-answer race. **6 of the 11 tasks run are accepted, and the gate needs F8 to pass.**

## Budgets

The task set's caps (150,000 / 300,000 / 600,000) were written before any run and are marked "to be calibrated after the first full run". They are too low:

- F1 (small) ran under the original rule that counted cached input in full, and used 993,607. Under the current rule (cached input at 10%) the same calls come to about 203,000.
- F2 (medium) ran under the current rule and used 422,135 to its first handoff and 575,884 with its rework.

On F2, 242,000 of the first handoff's 422,135 was cached input even at 10%: the agent made 72 model calls with 25,000 to 50,000 tokens of context each. The rework round, which added one test and six lines of README, cost 153,749, almost all of it re-reading context and reviewing.

Both tasks are recorded as over budget against the caps they ran under.

**Recalibrated 2026-10-02:** from F3 onward the caps are 300,000 / 600,000 / 1,200,000 (small / medium / large), and the "Cap" column shows the cap each task ran under. Under the new caps F1 (about 203,000 as a small task) and F2 (575,884 as a medium task) would both have fitted.

### Where the tokens go

F3 is the clearest case. The first implementation was the cheapest part of the task:

| F3 phase | Tokens |
| --- | --- |
| First implementation | about 95,000 |
| First review (including a benchmark the reviewer wrote and ran) | about 265,000 |
| Repair of the review's blocking finding | about 195,000 |
| Rework: implementation | about 188,000 |
| Rework: review, a second repair and verification | about 228,000 |

Review is the largest single cost on every task so far, and each rework repeats it in full on a larger context: by the end of F3 every model call carried about 45,000 tokens of input. The reviews did earn their cost in findings (see F3), so the question is how to make them cheaper, not whether to keep them.

Measured on F3's four sessions:

| Session | Model calls | Input per call | Output (of which reasoning) |
| --- | --- | --- | --- |
| First run, implement and repair | 62 | 31,000 | 57,600 (36,700) |
| First run, review | 37 | 25,600 | 46,000 (37,100) |
| Rework, implement and repair | 34 | 42,300 | 33,800 (19,400) |
| Rework, review | 22 | 34,300 | 71,500 (66,100) |

What this shows:

- **The number of calls is the multiplier.** The agent took one small step per call, 22 to 62 times a turn, and each call re-reads the whole conversation.
- **Compaction is not the lever.** These contexts are far below the size at which compaction pays for itself (see the blueprint, §8.6), so the factory leaves them alone.
- **Reasoning is a large share of output**, up to 92% in the rework's review. The engine has no setting to limit it yet.

From F4 onward the briefs (revision `m1.2`) ask for fewer, larger steps, and a rework's review is given only the rework.

### The effect, measured on F4

| | F3 (`m1.1`) | F4 (`m1.2`) |
| --- | --- | --- |
| First run, implement: model calls | 62 (with one repair) | 32 (no repair) |
| First run, review: model calls | 37 | 10 |
| Tokens to the first handoff | 556,150 | 333,285 |
| Rework: implement, model calls | 34 (with one repair) | 16 |
| Rework: review, model calls | 22 | 8 |
| Rework: review, output tokens | 71,500 | 5,000 |
| Tokens for the rework round | 414,900 | 68,236 |

The two tasks are not the same work: F3's rework was a real fix with a second review finding and repair, and F4's was a one-line change of an exception's base type. So the rework figures show the direction, not the size, of the improvement. The first-run figures are the fairer comparison, and there the calls fell by about half in implementation and by nearly three quarters in review.

What remains is reasoning. F4's first review made 10 calls and produced 60,700 output tokens, 58,100 of them reasoning, so it still cost about 164,000 tokens: half of the first handoff.

## Changes made to the factory during the evaluation

The first two tasks were also the first real end-to-end runs, and found defects that were fixed before continuing. Runs after a fix are not comparable token-for-token with runs before it.

| When | Found by | Fix | Commit |
| --- | --- | --- | --- |
| F1 | Resume after a budget pause failed | Checkpoints are read back after PostgreSQL reorders their keys | `efe4234` |
| F1 | Review turn returned empty replies | Output allowance raised from 8,192 to 32,768; a reply cut off at the limit reports itself | `351a6a3` |
| F1 | Small task used almost 1M tokens | Cached input counts at 10% | `a429cdb` |
| F1 | An interrupted run could not be recovered | A run cut off mid-step redoes that step | `0f92bf4` |
| F1 | Six gaps around the handoff | Withdraw a change request; reconcile unreported usage; reserve closer to cost; PR label from GitHub; and others | `c039d2a` |
| F2 | F2 could not start | Uncommitted edits an earlier task left in the working copy are set aside | `d46aff6` |
| F2, F3 | Every update of an existing pull request failed in transit | Idle connections to GitHub are dropped before GitHub closes them; a request that fails in transit is retried; a handoff keeps naming a pull request it could not update | `c5e10d8` |
| F5 | The agent ran `taskkill /F /IM dotnet.exe /T` and stopped every dotnet process on the machine, its own worker included; the host reported its own failure | The agent's shell refuses to stop processes by name or shut the machine down; a worker that dies mid-turn is the turn's failure and the run resumes with a new worker; the briefs carry the rule (`m1.3`) | `c0408bb` |
| F1 to F6, R1 | Review was about a third of all tokens, and a one-line change got the same open-ended review as a file-format change | Review depth chosen by the host (light or full); a review allowance with a steer to submit and a hard tool-call limit; the agent's account given to the reviewer as claims to check; every call records its phase; prompt revision `m1.4` | `6019f19` |
| F1 to F6, R1 | Every review's first `submit_review` (11 of 11) and every work session's first `submit_work` were rejected, each costing a model call: from PTC kernel code the model sees only a tool's top-level signature, never the fields inside a finding or a criterion, and the briefs named the wrong field (`description` for `text`) or none | The tools take the names and shapes the model guessed (`description`, `finding` or `message` for `text`; `path` for `file`; a line as text; a single string as a one-item list; `name` or `text` for `criterion`); the briefs name every field exactly; a criterion given as a plain string is still refused, with the shape to use, because it would lose its tests; the light review's tool-call limit is 6, not 4; prompt revision `m1.5` | `a254363` |
| Check (PR #7) | A light review's submission was recorded, but from kernel code the agent never sees a tool's reply unless it prints it; it went on checking and submitted again, so two of the review's four paid calls came after the result was in | Once a turn has recorded its result, the gateway answers the turn's next model call itself, with a reply that ends it, and charges nothing | `39a8be2` |
| Check (wrong project) | Named C# arguments to `request_decision` fail to compile (CS1739); the agent probed the tool with test calls, and one ("Q test2", "w", "a", "b") reached the user as a decision while the real question was never asked | The briefs show each completion tool's call in the kernel's name/value form, run in tests through a real kernel; never call a tool to test it; `request_decision` refuses text too short to be a question or reason, and options that are not choices; prompt revision `m1.6` | `fe222da` |
| Check (wrong project) | On a task created against the wrong project, the agent listed its working copy's parent, found another project's working copy and read it | The file tools refuse paths outside the working copy, and the shell refuses commands that name another working copy beside it; the briefs say to work only in the working copy. A guard against a mistake, not a sandbox: kernel code can still reach the disk (§17) | `4e29bb5` |
| F6, R1 | No task has called `request_decision`. On F6 the agent saw that persisting expiry changed the file format, decided alone that version-1 files would be rejected, wrote that into the README and listed it under known limitations in all five submissions; the rework it caused ran the task out of budget | The run brief names the choices that always need a decision (existing data or files that would stop loading, public behaviour removed, data deleted or migrated, a new dependency, two reasonable behaviours a user would notice); the host sends back once a submission whose known limitation says something existing stops working, telling the agent to keep it working or ask, and records it if resubmitted; not after the task has had a decision; prompt revision `m1.7` | `d40708f` |
| F3, F6 | Both reached their first handoff inside the cap and failed in the rework the tester asked for: one budget covered the task's whole life | Each change request adds half the task's original cap (`FACTORY_REWORK_TOP_UP`), and the thread says so; a cap raised by hand does not raise later top-ups | `2e81dad` |
| F6 re-run | The new check missed F6's second unasked choice, worded "not readable by earlier library versions ... truncates from there" | The check also recognises "readable", "loadable", "compatible" and truncating, corrupting or silently dropping, still only when said of something existing | `eb8537e` |
| F6 re-run | The rework's review sat silent for over ten minutes with no call in flight: two `run_kernel_code` calls in one reply, and the kernel ignores an eval that overlaps another | A kernel session runs its evals one at a time and starts its kernel once; it counts as ready only after init (an engine fix, so every face that uses kernels gets it) | `65d6b24` |
| F6 re-run | Clicking into the token budget closed the New thread dialog | The dialog closes only when a press begins and ends on its backdrop, and its form turns browser autofill off | `66f5787` |
| R1 re-runs | A rework brief never showed `submit_work`'s criteria shape, so F6's rework sent plain strings once | The contract restated in rework and repair briefs gives the shape (`m1.8`) | `feabebd` |
| R1 re-runs | Ten whole files printed while exploring filled a 67,000-token context that every later call re-read; the briefs said to read every file in one call | The briefs say to find with `search_code` and read only the lines needed, and that everything printed is paid for again (`m1.9`). It did not change the behaviour | `79b25c3` |
| R1 re-runs | The same, after `m1.9` | A factory worker's kernel returns at most 8,000 characters per script; the rest goes to a scratch file and the agent is told where it is and how to print less | `49c9e91` |
| R1 re-runs | The 8,000-character cap kept only the start of a script's output: it cut five of seven files from one script and hid the summary a test run prints last, and the agent read the lost files again in extra calls | `read_file` returns at most 400 lines or 20KB unless asked for more, with a note on how to continue; the cap keeps the start and the end of a script's output and is raised to 24,000 characters, as a safety net | `9db8329` |
| F5 re-run | A provider stream ended after 100 seconds without producing anything; nothing was charged, but the task was Blocked | The gateway sends such a call again, up to twice; failures are classified for any provider (the whole exception chain, and the HTTP status from any SDK exception), and a 429 from any provider is waited out | `972e4e1` |
| F5 re-run | A test hung after a locking change; the run had no timeout, the kernel killed the script twice, and the agent spent about 35 calls working around a test runner it believed the environment blocked | A `dotnet test` with no hang timeout is run with `--blame-hang-timeout 2m --blame-hang-dump-type none`, so a hanging test stops and names itself; the briefs say a hanging test run is a hanging test (`m1.10`) | `810aacd` |
| All | The factory asked 0 decisions in every run: on F6 it saw the file-format question twice and decided alone, on F5's rework it redesigned locking alone; brief wording and the check on submitted limitations did not change that | A decision scan before implementation: a read-only turn in a fresh session lists the choices a request leaves open, and the host asks by rule those in an always-ask category that nothing settles, one at a time, and states the rest as assumptions in the implement brief and the handoff; `FACTORY_SCAN_ONLY` checks it on requests without implementing (`m1.11`) | `fd8a6f0` |
| F7, R4 | The decision scan treated an existing-data choice as settled by request text that did not settle it | Existing-data choices are always asked | `6933279` |
| F7 | A review failed with "the process cannot access the file ... transcript.jsonl": another handle held the transcript for a moment | Transcript files are opened sharing read, write and delete, and an append retries a briefly locked file (an engine change) | `283e7c5` |
| F7 | OpenRouter ended a stream 3,479 bytes into a tool call's arguments; the reply could not be read and the turn failed | The gateway holds a call's events until it completes, with heartbeats, and sends a call that broke part-way again, up to twice, each attempt charged on its own | `219bee8` |
| F7 | A light review of one test file reasoned for 36,006 tokens without a reply, twice, and the task sat blocked at its last step | A review whose model reaches its output limit without replying runs once more, then the run hands off with the review not run and says so; a scan cut off this way implements without it | `efda4b5` |
| R4 | Thread messages showed numbers in the host machine's culture ("1,22,616"), and the scan's allowance note called it a review | Thread messages format numbers invariantly; the note names the decision scan | `2fee56c` |
| R2 | Four calls missed the provider cache completely and cost about 281,000 of 860,000 tokens; each miss also made the next reservation assume no cache (about 104,000 for a call that cost 8,000), so every resume paused again at once | A session's cache, once proven, is still counted on after one miss (two in a row are reserved in full); every call records who served it (`ServedBy`: a router's upstream when the provider reports one, otherwise the provider), for any provider | `d368d7b` |
| All | Review took 10% to 44% of a task's tokens, a quarter on average, and a tiny check task spent 44% on a light review that found nothing (ReadM_SoftwareFactory_ReviewGuidance.md) | Review depth is scored: weighted risk signals pick no review, light or full; deep-review categories (auth, schema, storage, locking, process or markup injection) are full on their own; no review only for at most 40 lines in 4 files with no signal, a clean verification and every criterion tested; `FACTORY_REVIEW_NONE=off` keeps at least a light one (`m1.12`) | `1480171` |
| All | Nothing recorded whether a review finding was real, so review's value could not be measured | A person can judge each finding (real defect, not worth fixing, wrong); review yield per task and in total: review tokens, share, confirmed defects per 100,000 review tokens, false-positive rate | `76ad5b3` |
| F3 | A rework's review cost more than the first implementation | The review of a rework covers only the rework; briefs say that calls are what cost; prompt revision `m1.2` | `c5e10d8` |

## F1 · Enforce size limits on keys, names and documents

- **Outcome:** accepted on the first handoff. The reviewer merged pull request #1 on GitHub; all four repository checks passed.
- **Evidence at handoff:** build passed; 55 tests passed (15 new); changed-line coverage 100%; agent review left 3 minor findings open.
- **Decisions:** none asked, none expected.
- **Notes:**
  - The thread reads Cancelled, not Accepted. After the handoff a question sent with `@factory` started a rework run, and at that time there was no way to withdraw it and reach Accept. The change was judged and merged from the first handoff.
  - The run was resumed several times while the defects above were fixed, so its wall-clock time and token total are not representative.

## F2 · JSON Lines export and import

- **Outcome:** accepted after one rework round. Pull request #2 is open as a draft; it has not been merged.
- **Baseline deviation:** the task set starts every task from `07fb8b8`. F2 started from `main` after F1 was merged (`1565c10`), because the factory branches from the current default branch.
- **First handoff** (commit `37afe6e`): build passed; 82 tests passed (27 new); changed-line coverage 99.1%. Criteria 1, 2, 3, 4 and 6 met. Not met: criterion 5 (round trip not tested with nested documents) and criterion 7 (README had no example line).
- **Rework** (commit `049f4b4`): one message listing criteria 5 and 7. It added a nested-document round-trip test and an example line to the README. 83 tests passed (28 new). All seven criteria met.
- **Decisions:** none asked, none expected.
- **Agent review:** 3 minor findings left open at the second handoff, none blocking.
- **Evidence:** no mismatch. The second handoff disclosed that the draft pull request could not be updated ("An error occurred while sending the request"); the branch was pushed and pull request #2 shows the new commit.
- **Notes:**
  - Criterion 2 (atomic import) is met by the implementation, which commits every line as one batch, but no test reopens the database to show it.
  - The run paused on budget at 300,000 and again at 400,000; the cap was raised each time.

## F3 · Automatic compaction

- **Outcome:** recorded as **not accepted**. The rework round paused on budget during its review, and the task set counts a run that ends budget-paused as failed. The cap was raised so the work could be finished, and the final handoff meets all five criteria. Pull request #3 is open as a draft.
- **Baseline deviation:** as F2. F3 started from `main` with F1 merged and without F2.
- **First handoff** (commit `c7ff34d`, 556,150 tokens, inside the 600,000 cap): build passed; 64 tests passed (9 new); changed-line coverage 100%. Criteria 1, 2 and 3 met. Not met: criterion 4 (a failed automatic compaction propagated out of the commit, and nothing made the failure observable) and part of criterion 5 (no test compared file size with the option off).
- **Rework** (commit `8e5f348`): one message listing criteria 4 and 5. It wrapped the automatic compaction so a failure cannot fail the commit, added a public `LastCompactionError` property and documented it, and added four tests. 68 tests passed (13 new). All five criteria met.
- **Budget:** paused at 897,115 of a cap already raised to 900,000, then finished at 971,057 after a second raise.
- **Decisions:** none asked, none expected.
- **What the agent review found** (both blocking, both fixed by a repair cycle before the handoff that followed):
  - *First review:* automatic compaction counted per-record framing as reclaimable space, so a workload of many small unique documents compacted after every commit. The reviewer measured it by writing and running a benchmark: 8,000 inserts took 2.6 s with the option off and 128 s with it on. The build and all tests had passed.
  - *Rework review:* the hook added to let a test force a compaction failure was static and mutable, and would have been shared by tests that run in parallel. It was made per-instance.
- **Evidence:** no mismatch. As on F2, the rework handoff disclosed that the pull request could not be updated ("An error occurred while sending the request"); the branch was pushed. Twice in two rework handoffs suggests a defect in the factory's update of an existing pull request, not chance.
- **Notes:**
  - The review turn is meant to be read-only, but with programmatic tool calling the reviewer ran its own C# to benchmark the change. The result was useful; the restriction is weaker than designed.
  - While that benchmark ran for four and a half minutes, nothing on the task's page changed, and nothing in the conversation says that a review found problems and a repair started.

### F3 re-run (2026-10-05)

Run after the per-file read limit and the output safety cap (`9db8329`), prompt revision `m1.9`, on a `main` that by then included F6's format change.

- **Outcome:** **accepted after one rework**, inside the original 600,000 cap. Pull request #9, merged.
- **First handoff** (commit `7c30d72`, 151,211 tokens: 99,633 implementing in 26 calls, 51,578 reviewing in 13): build passed; 101 tests passed (11 new); changed-line coverage 88.6%. The first attempt reached its first handoff at 556,150.
  - Met: criteria 1 (`FileDatabaseOptions.AutoCompaction`, null by default), 2 (`MinimumFileBytes`, 1 MiB by default) and 3 (compacts once after a commit; tests for the threshold, small files, transactions and reopening).
  - **Not met: criterion 4**, as in the first attempt: the committed data stayed, but the compaction's exception still left the commit, and nothing recorded it.
  - Weak: criterion 5. No test compared file size with the option off against a baseline.
  - The full review stopped at its allowance (12 calls) and was asked to submit; it found three minor issues, one a real bug (compacting a version-1 file left its upgrade flag set).
- **Rework:** one message naming criteria 4 and 5. The thread recorded the top-up (cap 900,000). 51,958 tokens in 14 calls, then a light review of 28,638 in one call: 80,596 in all, against 414,907 for the first attempt's rework.
  - Delivered: a failed automatic compaction no longer fails the commit; the error is kept in `LastAutoCompactionError`, and a test forces a failure and checks both. A baseline test compares file sizes with the option enabled but below its minimum; the review noted that its name says "off". Build passed; 103 tests passed (13 new); changed-line coverage 100%.
- **What it shows:** the first task re-run that we had not tuned against cost a quarter of its first attempt (231,807 against 971,057), with the rework a fifth of what it was. The criterion the first attempt missed was missed again, so the brief does not make the agent consider failure paths a request does not mention.

## F4 · Read-only open mode

- **Outcome:** accepted after one rework round, inside the 600,000 cap. Pull request #4 is open as a draft.
- **Baseline deviation:** as F2. F4 started from `main` with F1 merged and without F2 or F3.
- **First handoff** (commit `6325e50`, 333,285 tokens): build passed; 64 tests passed (9 new); changed-line coverage 86.6%. Criteria 1, 2, 3, 5, 6 and 7 met. Not met: criterion 4. Every write on a read-only instance threw, with a clear message, but as a new `ReadOnlyDatabaseException` that did not derive from `InvalidOperationException`.
- **Rework** (commit `9e56779`, 68,236 tokens, 2 minutes): one message naming criterion 4. The exception now derives from `InvalidOperationException`, and the tests assert it. 65 tests passed (10 new). All seven criteria met.
- **Decisions:** none asked, none expected.
- **Agent review:** no blocking finding. One minor finding open at the final handoff; three at the first, including that any I/O error while opening for writing is now reported as "database locked".
- **Evidence:** no mismatch. The rework handoff updated pull request #4 and named it: the pull request update that failed on F2 and F3 worked.
- **Notes:** no budget pause, no repair cycle, no call charged above its reservation.

## F5 · Async API with cancellation

- **Outcome:** recorded as **not accepted**, on two counts: the handoff does not meet the criteria, and the run ended Blocked once, which the task set counts as failed. No rework round was sent. Pull request #5 is open as a draft and was not accepted.
- **Baseline deviation:** as F2. F5 started from `main` with F1 merged and without F2, F3 or F4.
- **Handoff** (commit `9e356f2`, 554,007 tokens, inside the 600,000 cap): build passed; 78 tests passed (23 new); changed-line coverage 89.2%.
  - Met: criterion 1 (async counterparts with a cancellation token), 4 (same semantics and exceptions, tested) and 6 (synchronous API and existing tests unchanged).
  - **Not met: criterion 2,** the centre of the task. `GetAsync` and `TryGetAsync` check the token, do an ordinary blocking read and return a completed task. Writes call `WriteAsync` on a file that is not opened for asynchronous I/O, so they block a pool thread, and the flush to disk is a plain blocking call. The result is an async-shaped API that still blocks.
  - **Not met: criterion 5.** Synchronous and asynchronous calls are mixed only one after the other; the concurrent tests use asynchronous callers only.
  - Partly met: criterion 3. A cancelled token throws and nothing is written, but the tests check that the document is absent, not that the file is unchanged.
- **How criterion 2 was missed.** The first implementation had a real asynchronous read. The review found that it awaited while holding a thread-affine lock, a genuine defect. The repair removed the asynchronous read instead of correcting it. The build and every test still passed.
- **Evidence:** no mismatch, and this is the case that shows why it matters. The handoff's known limitations say the read "cannot be interrupted once started", and the review's open findings say that an async commit still blocks on the flush and that the README overstates what is asynchronous. A reviewer reading the handoff is told the truth; a reviewer reading only "78 tests passed" is not.
- **Decisions:** none asked, none expected.
- **What stopped the run.** While checking its repair the agent put the defect back on purpose to prove a test would catch it; that test run deadlocked and was killed after five minutes. To clear the hung test host it ran `taskkill /F /IM dotnet.exe /T`, which stopped every dotnet process on the machine, its own worker among them. The run was Blocked with the defect still in the working copy, was resumed, and handed off with it removed. The factory now refuses that command (see the table of changes).
- **Notes:** this is the first task the factory could not do well, and the first where a clean build and passing tests concealed a miss on the requirement.

### F5 re-run (2026-10-05)

- **Outcome:** **not accepted.** The rework ended Blocked, which the task set counts as failed. Pull request #10 (the first handoff) closed unmerged.
- **First handoff** (commit `19d44fd`, 241,256 tokens: 162,865 implementing in 23 calls, 78,391 reviewing in 10; the first attempt used 554,007): build passed; 120 tests passed (17 new); changed-line coverage 100%.
  - Met: criteria 1 (async counterparts with a `CancellationToken`, and `CommitAsync`), 3 (an already-cancelled token writes nothing) and 6 (no existing test changed); mostly 4.
  - **Not met: criterion 2.** Every async method ran the existing blocking method through `Task.Run`. The request says only "async versions ... for use in ASP.NET Core apps"; the criterion is stricter. Its own review raised it as minor.
  - Not met: criterion 5 (no concurrency test mixing synchronous and asynchronous writers).
- **Rework:** one message naming criteria 2 and 5. Real asynchronous I/O meant replacing locking a synchronous `ReaderWriterLockSlim` cannot hold across an `await`, a material design choice; the factory again did not ask. It rewrote the write and commit paths onto awaited file writes and removed the `Task.Run` helper, reading with `search_code` and line ranges throughout.
  - **Then its test run hung.** The test host used 259 seconds of CPU in about three minutes, so a test was spinning, most likely on the new locking. The agent gave the command no timeout; the kernel killed the script after five minutes, twice.
  - **It blamed the environment:** "the environment can't run vstest (its control channel needs a TCP socket, which the sandbox blocks)". The factory's own verification runs the same tests on every task. It spent about 35 calls working around a problem that did not exist: loading `System.Diagnostics.Process` by reflection, detached processes and log polling, and a standalone test harness.
  - At 803,108 of 900,000 a model call's stream ended after 100 seconds without producing anything; nothing was charged, but the turn failed and the task was Blocked.
- **What it shows:** reading costs are under control (the context stayed near 20,000 to 72,000 and reads were ranged), but a hanging test sent the agent down a long wrong path, and a provider stream that produced nothing blocked a task that could have retried. Three fixes followed (see the changes table).

### The decision scan, first check on F7 and R4 (2026-10-05)

Two requests that expect a decision, run with the scan (prompt revision `m1.11`). Both are unrun tasks, so each continues as its real evaluation run once its questions are answered.

| Task | Expected decision | Scan's first question | Questions asked | Choices assumed | Scan cost |
| --- | --- | --- | --- | --- | --- |
| F7 | How existing files store versions | "Existing version-1 and version-2 files hold no per-document version. What version should their documents read back as?" Its recommendation was the task set's scripted answer | 2 (the second, restart after delete, matches a criterion) | 7 | 26,771 tokens, 7 calls |
| R4 | Generate for every slide, or only on request | "When are images generated: eagerly for all no-photo slides, or lazily per slide?" | 3 (the limit; the others: how an image is stored, whether old drafts load) | 9 | 36,012 tokens, 7 calls |

- **Both expected decisions were asked**, first: the first decisions the factory asked in the evaluation.
- **A flaw:** F7's scan marked "how the format changes, and when an existing file is upgraded" as settled by "Versions must survive reopening and compaction", which does not settle it. That is F6's failure mode, so `existing-data` choices should be asked whatever the scan says settles them.
- **Questions on a request that should ask none:** measured on R2 (see its section). It asked three: one fair existing-data question, one that repeated it, and one the request's own words settled.
- **How the two runs ended:** F7 accepted, R4 failed on budget. See their sections.

## R1 · Per-slide text alignment and size

The first task on `insta-story-generator`, and the first real run of the Node/React verification profile. **The factory itself needed no fix:** it cloned the repository, ran `npm ci`, the typecheck and the Vitest suite on the base commit, read the JUnit and Cobertura reports, measured changed-line coverage, pushed the branch and opened the pull request.

- **Outcome:** recorded as **not accepted**. It paused on budget at 300,000 and again at 400,000 before handing off at 437,338, and the task set counts a budget pause as failed. No rework round was sent. Pull request #1 on that repository is open as a draft.
- **Handoff** (commit `b1e436e`): typecheck passed; 138 tests passed (15 new); changed-line coverage 100%.
  - Met: criterion 1 (alignment and size on the slide model, with defaults that also apply to drafts saved before the change), 2 (labelled controls in the slide editor) and 3 (the renderer uses `textAlign`, the x-position and scaled sizes, tested with the recording context).
  - Partly met: criterion 4. Text stays inside the safe area because the layout clamps it, and a test checks that for large text on the default slide. No test covers every size and layout with the longest allowed text.
  - **Not met: criterion 5,** which says AI-generated slides get the defaults and the JSON schema sent to providers does not change. The change adds `align` and `textSize` to that schema as required fields and tells the model to choose them.
- **Evidence:** no mismatch. The handoff's known limitations state that "AI-generated stories now also request align/textSize".
- **Agent review:** 2 minor findings open, one of them real: regenerating a slide does not tell the model the slide's current alignment and size, so a user's choice can be silently reset.
- **Decisions:** none asked. One was arguably warranted: whether the AI should choose alignment and size is a product choice the request did not make, and the agent made it without asking.
- **Budget.** This was sized as a small task. Its implementation turn made 56 model calls on an average of 39,800 tokens of context, larger than any filedb-sharp task: 265,000 of the 437,338 tokens were cached input even at 10%. The estimator's 95th-percentile under-estimate was 7.5%, the highest so far and still inside the 10% margin.

### R1 re-runs on `m1.8` and `m1.9` (2026-10-03)

Both paused on budget during the implementation turn, before any submission, so neither reached verification or review.

- **On `m1.8`:** 37 calls, 288,465 tokens. The context grew to 67,000 tokens per call. 53% of the charge was the agent's own context re-read from the provider's cache (1.53 million cached tokens at 10%), 24% new input and 23% output (45,693 of it reasoning).
  - The context was mostly tool output: about 137,000 characters, 125,000 of them in ten results over 4,000 characters, seven of those whole files printed with `read_file`. It made 10 `read_file` calls and none used a line range; F6's re-run made 23 and one did.
- **Trimming old tool results was considered and rejected.** Removing a result breaks the provider's cache from that point, so the next call re-sends everything after it at full price. On this run it would have saved about 10%.
- **On `m1.9`** (briefs: find with `search_code`, read only the lines needed, everything printed is paid for again): 48 calls, 297,999 tokens. It still began by printing 21 whole files in four scripts of 9,000 to 24,000 characters each, used `search_code` once after that, and read no line ranges. The brief changed nothing.
- **What it shows:** for this model, advice in the brief does not change how it explores; the 300,000 cap is spent re-reading files it printed early. Enforcement followed: a factory worker's kernel now returns at most 8,000 characters per script (`49c9e91`).
- **With the output cap:** 54 calls, 293,451 tokens. The cap did what it was built for: nine explorations were cut short, almost every later result was under 3,500 characters, and a call cost about 5,400 tokens against about 7,800 before. But the agent made more calls: 19 edits, most of them one per call; 12 test runs, six of them spent measuring coverage itself, which the factory's verification does anyway; 11 reads; 5 searches; and 15 steps that ended in an error. The context still reached 56,000 tokens, now mostly its own edit scripts.
- **Conclusion:** three runs and three fixes each moved the cost rather than removing it. Tuning against R1 was stopped there. On this model, a small React change in this repository costs about 300,000 to 440,000 tokens; the task set's small cap of 300,000 does not fit it. Two further wastes are known and not fixed: edits made one per call, and coverage measured by the agent.

## F6 · Document expiry (TTL)

The large task designed to make the factory stop and ask.

- **Outcome:** recorded as **not accepted**. The rework paused on budget at 1,188,184 of 1,200,000 during a repair, and its work is neither committed nor pushed. Pull request #6 shows the first handoff.
- **The missed decision.** Storing expiry times changes the on-disk format, and the request does not say what should happen to existing files. The factory did not ask. It moved the format to version 2 and made version-1 files unreadable, which would break every existing database. The handoff disclosed this ("version 1 files are rejected"), so there is no evidence mismatch, but the choice was not the agent's to make.
- **First handoff** (commit `4128887`, 751,957 tokens, inside the cap): build passed; 70 tests passed (15 new); changed-line coverage 100%.
  - Met: criterion 1 (write overloads taking a time-to-live; a non-positive one throws), 2 (an expired document is invisible to every read, and `Insert` of its key succeeds) and 4 (expiry survives reopening; `Compact` drops expired documents).
  - **Not met: criterion 3** (time injectable; tests use a fake clock): it read `DateTime.UtcNow` directly, and the tests used `Thread.Sleep`.
  - **Not met: criterion 5** (version-1 files still open and read): they were rejected.
  - Partly met: criterion 6. The README documented version 2, with the rule that version-1 files are not read.
- **Rework:** one message giving the task set's scripted answer for the format and naming criteria 3, 5 and 6. In the working copy, uncommitted: version-1 files are read and stay version 1 until compacted; a `TimeProvider` option with a fake clock in the tests and no real sleeps; the README states the rule. The host verified it (77 tests passed, coverage 100%). Its review then found a blocking problem, and the budget ran out in the repair.
- **Agent review:** in the first run it found, by running the built code, that compaction dropped a live document's expiry; that was repaired before the first handoff.
- **The shell rule held.** During the first repair the agent met a build output file locked by a process it had not started. It recorded that it did not stop that process, and moved the file aside instead.
- **Budget:** about 752,000 for the first run and about 436,000 for the rework (268,000 implementing, 140,000 reviewing, 28,000 into the repair).

### F6 re-run on `m1.7` (2026-10-03)

The same request, with the fixes made after the first seven tasks: light or full review, completion tools that take the model's field names, a turn that ends free once its result is recorded, the briefs' list of choices that always need a decision, and a change request's budget top-up.

- **Outcome:** **accepted after one rework**, inside the original 1,200,000 cap. Pull request #8, merged.
- **First handoff** (commit `f7383fe`, 351,946 tokens: 272,542 implementing in 42 calls, 79,404 reviewing in 7): build passed; 81 tests passed (19 new); changed-line coverage 97.6%.
  - It kept version-1 files readable this time, by keeping the format at version 1 and adding a new record kind. But it reported that "files written by this version are not readable by earlier library versions (an older reader treats the first expiry frame as a corrupted tail and truncates from there)": an older library would silently lose data. Again it was not asked, and the host's new check did not recognise "not readable" or "truncates" (fixed in `eb8537e`).
  - Not met: criterion 3. The clock was not injectable and the tests waited on wall-clock time; the request does not mention this, so the factory could not have known.
- **Rework:** one message giving the task set's scripted answer, a rule that an older library must never silently truncate a file, and an injectable clock. The thread recorded the top-up: 600,000 added, cap 1,800,000.
  - Delivered: `FileDatabaseOptions.TimeProvider` used everywhere expiry is computed, tests on a fake clock with no sleeps; new and compacted files are version 2; an older library rejects a version-2 file instead of truncating it; the README states the rule.
  - **One interpretation made alone:** a version-1 file is upgraded to version 2 in place the first time a document with an expiry is written to it, where the scripted answer says a version-1 file stays version 1 until compacted. The answer does not say what writing an expiring document to a version-1 file should do, so this was a real choice; the tester accepted it.
  - Cost: 320,858 in 44 calls, more than the first implementation, most of it in three calls of 6,000 to 20,000 output tokens. One `submit_work` was rejected for criteria given as plain strings: the rework brief does not repeat their shape.
- **Rework review:** full (9 files, data format), 126,289 tokens in 9 calls; three minor findings (`GetStats` counts an expired document's bytes as live until compaction; a test does not check what its name says; the README grammar marks an expiring record's value as optional).
- **A kernel defect stalled the review.** Its first reply held two `run_kernel_code` calls. The kernel runs one eval at a time and ignored the second, which the session sent anyway, so the turn waited with no call in flight. The task was paused, the defect fixed (`65d6b24`), and the review resumed.
- **What it shows:** the budget fixes worked (799,093 against 1,188,184, with the top-up unused), and an unasked decision now costs one rework instead of the task. The factory still does not ask: twice it made the format choice itself, the second time more carefully, and both times disclosed it honestly.

## F7 · Optimistic concurrency with document versions

Run on 2026-10-05 with the decision scan (prompt revision `m1.11`) from `main` after F3's merge.

- **Outcome:** **accepted after one rework**, inside the original 1,200,000 cap. Pull request #11, merged.
- **Decision scan** (26,771 tokens, 7 calls): 9 open choices, 2 asked, 7 assumed and stated in the brief and the handoff.
  - "Existing version-1 and version-2 files hold no per-document version. What version should their documents read back as?" Answered "Assign version 1 to every document", the task set's scripted answer.
  - "When a document is deleted and its key is later re-inserted, does the version continue from before or restart?" Answered "Restart from the initial version", as criterion 2 requires.
- **First handoff** (commit `78727c7`, 800,577 tokens with the scan: 529,994 implementing in 53 calls, 243,812 reviewing in 15): build passed; 129 tests passed (33 new); changed-line coverage 94.9%. Full review (387 lines, 10 files, a data format).
  - Criteria 1, 2, 3, 4 and 6 met. Not met: criterion 5. The concurrency test did not make two writers race.
- **Rework:** one message asking for a real race: two writers on separate threads, released together, with the same expected version. The thread recorded the top-up: 600,000 added, cap 1,800,000.
  - Delivered (commit `9a3f6e5`): a test that holds two dedicated threads at a `Barrier`, releases them together, and checks over 100 rounds that exactly one update wins, the other throws `VersionMismatchException`, and the version rises by one. 130 tests passed (34 new).
  - Cost: 142,195 tokens. 37,582 reworking in 17 calls; the rest in the light review.
- **Agent review:** clean at the final handoff.
- **Evidence:** no mismatch.
- **Three failures, each Blocking the task until resumed, and each fixed afterwards:**
  - OpenRouter ended a stream 3,479 bytes into a tool call's arguments (fixed in `219bee8`).
  - The first review could not append to its transcript while another handle held it (fixed in `283e7c5`).
  - The rework's light review, of one test file, reached the 32,768-token output limit twice while reasoning, without replying. The third attempt replied (the rule in `efda4b5` now hands off after the second).
- **Notes:**
  - The exception is named `VersionMismatchException`, not `ConcurrencyConflictException`. The request did not name it, so this is not counted against criterion 3.
  - The scan marked the format change as settled by the request when it was not. The format question was still covered by the first question asked, and the format was correct; existing-data choices are now always asked (`6933279`).
- **What it shows:** the scan asked the expected decision, and the answer went into the work: existing files read back at version 1, with no rework spent on the format. The one rework was for a test, which the scan could not have prevented.

## R4 · AI image generation for slides without a photo

Run on 2026-10-05 with the decision scan (prompt revision `m1.11`), at the same time as F7, from `main` on insta-story-generator.

- **Outcome:** **not accepted.** It paused on budget during its implementation three times, before submitting anything, and was cancelled at 1,315,680 tokens. The branch and its uncommitted edits are kept; no pull request was opened.
- **Decision scan** (36,012 tokens, 7 calls): 12 open choices, 3 asked (the limit), 9 assumed.
  - "When are images generated: eagerly for all no-photo slides as part of the story request, or lazily per slide?" This is the expected decision. It was answered "Eagerly with the story plan", **not the scripted answer** ("only on request, never automatically"). Criterion 3 could therefore not have been met.
  - "How does the generated image reach and persist for the client?" Answered "Base64 on the slide".
  - "Can existing saved drafts still load?" Answered "Extend Slide with an optional image field, keep old drafts loading".
- **Implementation:** 125 calls, 1,279,668 tokens.
  - The first pause came 17 minutes after the last answer, at about 1,194,000 of 1,200,000.
  - The cap was raised to 1,400,000. The run paused again twice, when single calls needed about 123,000 and 140,000 tokens.
  - The context reached 121,759 tokens. Most of the cost was that context re-read from the cache: 910,106 of the 1,279,668, at 10%, over 125 calls. The final pauses were for reservations of about 123,000 and 140,000, which assume no cache hit.
- **What it shows:** the scan works on a client-and-server request too. All three questions were real and answered in about seven minutes. But the change is too large for this model at this cap: it spent the whole budget implementing, in 125 calls that each re-read a growing context. Even so, a handoff would have failed criterion 3, because the timing answer was not the scripted one.

## R2 · Focal point for photo cropping

Run on 2026-10-05 with the decision scan (prompt revision `m1.11`); its rework ran on 2026-10-06 on `m1.12`. The thread was titled "Old drafts load with the focal point at the centre." by mistake (a suggested answer went into the title field), so its branch is `factory/143e-old-drafts-load-…`; the title reaches no brief, and pull request #2 was retitled "Focal point for photo cropping".

- **Outcome:** **not accepted.** Its implementation paused on budget four times before submitting, at 541,481 of 600,000 and again after each raise (to 900,000, then 1,000,000). The final handoff meets all five criteria, and pull request #2 was merged.
- **Decision scan** (23,701 tokens, 7 calls): 7 open choices, 3 asked, 4 assumed. The task set expects no decision.
  - "How is the focal point represented and added to the Slide schema?" A fair existing-data question, but its options offered `focusX`/`focusY` where criterion 1 asks for `{x, y}`. Answered with "Other": an optional `focalPoint {x, y}` defaulting to the centre. The answer also corrected one of the scan's stated assumptions, "click/tap only for now" for keyboard users, which contradicted criterion 2. **That requirement reached the factory through the answer, not the request.**
  - "What happens to focal points already-saved drafts do not have?" **Unnecessary:** the first answer settled it. Questions are chosen all at once during the scan, and a later one is not checked against an earlier answer.
  - "Should the AI planner also choose focal points?" **Unnecessary:** the request says "let users set a focal point".
- **First handoff** (commit `c992d93`, 983,387 tokens with the scan: 857,545 implementing in 69 calls, 102,141 reviewing in 13): build passed; 142 tests passed (19 new); changed-line coverage 97.6%. Full review (15 files, a changed public declaration).
  - Criteria 1, 2, 3 and 5 met. Not met: criterion 4. No focal-point marker; the handoff disclosed it.
  - Criterion 2 is met with two weaknesses: the accessible name is the preview's existing label, and the canvas is a button that Enter and Space do not activate.
  - **Four calls missed the cache completely** and cost about 281,000 tokens, a third of the task. Two came 40 seconds apart with the same prefix, so the first wrote no cache the second could use. Each miss made the next reservation assume no cache, about 104,000 tokens, and every resume paused again at once. Fixed in `d368d7b`.
  - The review left 7 minor findings, one a real defect beyond the criteria: rewriting a slide reset its crop to the centre. No verdicts were recorded on them.
- **Rework** (commit `748b920`, prompt revision `m1.12`): one message asking for the marker, a rewritten slide keeping its crop, and the hint only on slides with a photo. The thread recorded the top-up: 300,000 added, cap 1,300,000.
  - Delivered all three: a marker drawn as an element over the canvas, never on it, shown while the preview is focused or hovered, at the right place in the cropped frame; the slide-replace step keeps the user's focal point, with a test; the hint is shown only with a photo. 145 tests passed (22 new); changed-line coverage 98.1%.
  - Cost: 296,398 tokens, 273,447 reworking in 41 calls and 22,951 for a **light review** (risk score 5: 54 lines, 7 files, a changed public declaration) in one call. **No call missed the cache**; every rework call was served by the same upstream (Together), and the largest reservation was 42,556.
  - One minor finding open: hover and focus share one flag, so the marker hides when the pointer leaves a focused preview.
- **Evidence:** no mismatch.
- **What it shows:** the factory built a correct medium UI change, but on this repository it costs far more than the cap: 857,545 tokens to the first handoff, against 600,000. The scan's false-alarm rate on a request that needs no decision is high: two of three questions were unnecessary. The rework is the first run on the cache and review fixes, and both behaved as intended.

## R3 · Cancel a running generation

Run on 2026-10-06 with the decision scan, prompt revision `m1.12`. The thread title carries a stray note ("(only this goes in the title field)") from the instructions it was copied from; pull request #3 was retitled "Cancel a running generation".

- **Outcome:** **not accepted.** It paused on budget at 584,011 of 600,000 during its implementation, and at 842,035 of 850,000 when its review's blocking finding sent it to repair. The final handoff was accepted and pull request #3 merged.
- **Decision scan** (18,013 tokens, 7 calls): 9 open choices, 3 asked, 6 assumed. The task set expects no decision.
  - "What does the user see when the work is running versus cancelled?" **Fair:** the request does not say. Answered with the Cancel button beside the progress message, the status reading "Cancelled", and nothing partial shown.
  - "Is a partial response kept?" **Unnecessary:** the first answer settled it. The same flaw as R2: questions are chosen at once, and a later one is not checked against an earlier answer.
  - "Should the server also abort when the client disconnects for another reason?" **Fair:** a real cost choice the request leaves open. Answered: abort on any disconnect.
- **Implementation** (700,517 tokens in 64 calls): build passed; 164 tests passed; changed-line coverage 100%. The context grew from 6,000 to 108,000 tokens, and late calls cost 11,000 to 13,000 each with the cache working: 61% of the implementation's cost was cached context re-read, 20% new input, 19% output (87,211 of it reasoning). No call missed the cache before the pause.
  - **After the resume, OpenRouter sent the session to a different upstream** (AtlasCloud before, Together after), and the first call found no cache: the whole 110,000-token context at full price. The new `ServedBy` record is what showed it.
- **Review:** full (risk score 10: 172 lines, 15 files, asynchronous code, a changed public declaration), 123,505 tokens in 8 calls. 1 blocking finding, **real**: the in-progress flag cleared in a `finally` before the cancellation was recorded, so the Cancel button could vanish and the raw abort error surface. The repair (67,889 tokens, 5 calls) records "Cancelled" before the flag clears. 165 tests passed (20 new); changed-line coverage 96.5%. 2 minor findings open.
- **Criteria:** 1, 2, 3 and 5 met. Criterion 4 is built on both sides, but only the server's late-answer race is tested; the browser discards a late answer too, but its test fakes reject on abort, so that path has no test. Criterion 5 holds in the code, without an explicit test.
- **Scan assumption not followed:** the scan assumed both a cancel endpoint and disconnect detection; the factory built disconnect detection only, which is what criterion 3 asks for, and said so in the handoff.
- **What it shows:** the same cost pattern as R2 and R4 without any provider fault: a correct change whose first implementation alone costs more than the medium cap, because every call re-reads a context that keeps growing. The review earned its cost here, with a real blocking defect repaired.

## What the seven tasks show

The gate cannot realistically be met: it needs all five remaining tasks to pass, and three of them are large or expect a decision. The blueprint's rule for a missed gate is to iterate on the prompts, orchestration and tools and re-run the task set before M2.

Two causes account for most of the failures:

1. **Rework rounds exhaust the budget.** F3 and F6 both reached their first handoff inside the cap and failed in the rework. A rework re-implements on a context of 40,000 tokens or more, then pays for a full review and often a repair.
2. **The factory never asks.** No task has called `request_decision`, including R1 and F6, where stopping to ask was the right move. Both needed a rework that a question would have avoided, and F6's unasked choice would have made existing databases unreadable.

F5 adds a third, smaller one: a repair that satisfied a review finding by removing the feature it found a defect in, with the build and every test still passing.

**After F7 and R4 (2026-10-05):**

- **The second cause is addressed.** With the decision scan, both tasks asked their expected decision before implementing.
- **The first cause is addressed for reworks.** F3, F6 and F7 finished their reworks well inside the top-up.
- **What remains is the cost of a large first implementation.** R4 spent 1.28 million tokens implementing without submitting, in 125 calls on a context of up to 122,000 tokens. R1 failed the same way at a smaller scale.
- **Tally:** 6 of 9 tasks are accepted. One of R2, R3 and F8 must pass for the gate.

**After R2 (2026-10-06):**

- **Implementation cost is now the cause of every failure since F6.** R1, R4 and R2 all paused on budget before their first submission; the code R2 eventually produced met every criterion.
- **A third of R2's cost was calls that missed the cache**, which the factory did not cause and could not see; it now records who served each call, and one miss no longer inflates the next reservation.
- **The scan over-asks on a request that needs no decision:** two of R2's three questions were unnecessary.
- **Tally:** 6 of 10 tasks are accepted. The gate needs 7 of 12, so one of R3 and F8 must pass.

**After R3 (2026-10-06):** R3 failed the same way, with a clean cache until a resume moved it to another upstream. **6 of 11 tasks are accepted; the gate needs F8 to pass.**
