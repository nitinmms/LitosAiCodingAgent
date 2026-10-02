# Software Factory M1 evaluation results

Results of running the [M1 task set](m1-task-set.md). One row per task, recorded as that file describes. Provider and model for every run: OpenRouter, `deepseek/deepseek-v4.1-flash`. Prompt revision: `m1.1`.

## Summary

| ID | Accepted | Rework rounds | Repair cycles | Decisions | Tokens used | Cap | Within cap | Wall clock | Evidence mismatches |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| F1 | Yes, first handoff | 0 | 0 | 0 | 993,607 (old rule; about 203,000 under the current one) | 150,000 | No | about 15 min of run time | 0 |
| F2 | Yes, after one rework | 1 | 0 | 0 | 575,884 | 300,000 | No | 12 min (8 + 3 for the rework) | 0 |

**Against the M1 gate so far (2 of 12 run):**

- Accepted with at most one rework: 2 of 2.
- Evidence mismatches: 0.
- **Budget overrun: both tasks.** Neither finished inside the task set's cap. See "Budgets" below.
- Estimator 95th-percentile under-estimate: 2.3% on F2 (margin 10%). No call was charged more than it had reserved.
- Lock violations: 0.

## Budgets

The task set's caps (150,000 / 300,000 / 600,000) were written before any run and are marked "to be calibrated after the first full run". They are too low:

- F1 (small) ran under the original rule that counted cached input in full, and used 993,607. Under the current rule (cached input at 10%) the same calls come to about 203,000.
- F2 (medium) ran under the current rule and used 422,135 to its first handoff and 575,884 with its rework.

On F2, 242,000 of the first handoff's 422,135 was cached input even at 10%: the agent made 72 model calls with 25,000 to 50,000 tokens of context each. The rework round, which added one test and six lines of README, cost 153,749, almost all of it re-reading context and reviewing.

Both tasks are recorded as over budget against the stated caps. Recalibrating the caps is a decision to take once more tasks have run.

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
