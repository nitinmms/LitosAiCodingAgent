# Software Factory M1 evaluation results

Results of running the [M1 task set](m1-task-set.md). One row per task, recorded as that file describes. Provider and model for every run: OpenRouter, `deepseek/deepseek-v4.1-flash`. Prompt revision: `m1.1`.

## Summary

| ID | Accepted | Rework rounds | Repair cycles | Decisions | Tokens used | Cap | Within cap | Wall clock | Evidence mismatches |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| F1 | Yes, first handoff | 0 | 0 | 0 | 993,607 (old rule; about 203,000 under the current one) | 150,000 | No | about 15 min of run time | 0 |
| F2 | Yes, after one rework | 1 | 0 | 0 | 575,884 | 300,000 | No | 12 min (8 + 3 for the rework) | 0 |
| F3 | **No: budget-paused during its rework.** All criteria met in the end | 1 | 2 (both for review findings) | 0 | 971,057 | 600,000 | No | 31 min (19 + 12 for the rework) | 0 |

**Against the M1 gate so far (3 of 12 run):**

- Accepted with at most one rework: 2 of 3. F3 produced code that meets every criterion after one rework, but it paused on budget during that rework, which the task set counts as a failure.
- Evidence mismatches: 0.
- **Budget overrun: all three tasks.** See "Budgets" below.
- Estimator 95th-percentile under-estimate: 2.3% on F2, 4.0% on F3 (margin 10%). On F3 one call of 155 was charged more than it had reserved; the task's cap was not passed by it.
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
- **Compaction is not the lever.** These contexts are far below the size at which compaction pays for itself (see the blueprint, §8.6), so the factory leaves them alone.
- **Reasoning is a large share of output**, up to 92% in the rework's review. The engine has no setting to limit it yet.

From F4 onward the briefs (revision `m1.2`) ask for fewer, larger steps, and a rework's review is given only the rework. The effect is to be measured on F4, not assumed.

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
| F2, F3 | Every update of an existing pull request failed in transit | Idle connections to GitHub are dropped before GitHub closes them; a request that fails in transit is retried; a handoff keeps naming a pull request it could not update | see the commit after F3 |
| F3 | A rework's review cost more than the first implementation | The review of a rework covers only the rework; briefs say that calls are what cost; prompt revision `m1.2` | see the commit after F3 |

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
