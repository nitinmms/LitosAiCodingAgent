# Software Factory M1 evaluation results

Results of running the [M1 task set](m1-task-set.md). One row per task, recorded as that file describes. Provider and model for every run: OpenRouter, `deepseek/deepseek-v4.1-flash`. Prompt revision: `m1.1` for F1 to F3, `m1.2` for F4 and F5, `m1.3` from R1.

## Summary

| ID | Accepted | Rework rounds | Repair cycles | Decisions | Tokens used | Cap | Within cap | Wall clock | Evidence mismatches |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| F1 | Yes, first handoff | 0 | 0 | 0 | 993,607 (old rule; about 203,000 under the current one) | 150,000 | No | about 15 min of run time | 0 |
| F2 | Yes, after one rework | 1 | 0 | 0 | 575,884 | 300,000 | No | 12 min (8 + 3 for the rework) | 0 |
| F3 | **No: budget-paused during its rework.** All criteria met in the end | 1 | 2 (both for review findings) | 0 | 971,057 | 600,000 | No | 31 min (19 + 12 for the rework) | 0 |
| F4 | Yes, after one rework | 1 | 0 | 0 | 401,521 | 600,000 | **Yes** | 11 min (9 + 2 for the rework) | 0 |
| F5 | **No: criteria unmet, and the run ended Blocked once** | 0 | 1 (review finding) | 0 | 554,007 | 600,000 | Yes | 28 min of run time | 0 |

**Against the M1 gate so far (5 of 12 run):**

- Accepted with at most one rework: 3 of 5.
  - F3 produced code that meets every criterion after one rework, but it paused on budget during that rework, which the task set counts as a failure.
  - F5 is the first task whose handoff does not do what was asked: see its section.
- Evidence mismatches: 0.
- **Budget overrun: F1, F2 and F3.** F4 and F5 finished inside their caps. See "Budgets" below.
- Estimator 95th-percentile under-estimate: 2.3% on F2, 4.0% on F3, 2.3% on F4 (margin 10%). On F3 one call of 155 was charged more than it had reserved; the task's cap was not passed by it.
- Lock violations: 0.

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
- **Compaction is not the lever.** These contexts are far below the size at which compaction pays for itself (see the blueprint, Â§8.6), so the factory leaves them alone.
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
| F3 | A rework's review cost more than the first implementation | The review of a rework covers only the rework; briefs say that calls are what cost; prompt revision `m1.2` | `c5e10d8` |

## F1 Â· Enforce size limits on keys, names and documents

- **Outcome:** accepted on the first handoff. The reviewer merged pull request #1 on GitHub; all four repository checks passed.
- **Evidence at handoff:** build passed; 55 tests passed (15 new); changed-line coverage 100%; agent review left 3 minor findings open.
- **Decisions:** none asked, none expected.
- **Notes:**
  - The thread reads Cancelled, not Accepted. After the handoff a question sent with `@factory` started a rework run, and at that time there was no way to withdraw it and reach Accept. The change was judged and merged from the first handoff.
  - The run was resumed several times while the defects above were fixed, so its wall-clock time and token total are not representative.

## F2 Â· JSON Lines export and import

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

## F3 Â· Automatic compaction

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
