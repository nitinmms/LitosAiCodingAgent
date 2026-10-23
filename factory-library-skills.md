# Litos Software Factory - library skills

Exported from the Skills tab on 2026-10-22. To add each skill on a new instance, open Settings > Skills, choose to add a skill, and copy the three fields below into its form. All three are enabled. "Default for" is left empty, so no task type starts with them ticked.

## Which task types each skill fits

| Skill | bug | feature | refactor | chore | Suggested "Default for" |
|---|---|---|---|---|---|
| tdd | Yes: a failing test reproduces the bug | Yes: one test per rule or acceptance criterion | Partly: existing tests are the safety net, since behaviour should not change | No, usually | bug, feature |
| fix-root-causes | Yes: this is what it is for | When a build or test breaks | When a build or test breaks | When something breaks, e.g. a dependency upgrade | bug, refactor, chore |
| prove-it-works | Yes | Yes | Yes | Yes | all four |

The defaults chosen for one task type must fit within 12,000 characters together. All three skills come to about 6,500, so any combination fits.

---

## 1. tdd

**Name**

```text
tdd
```

**Description** (one line)

```text
Use for new behaviour and for bug fixes with a clear, cheap local test path: write each test first, run it and read why it fails, then make the change. Skip it when the test path is unclear, expensive or integration-heavy.
```

**Instructions** (the SKILL.md body)

````markdown
# Test first

For new behaviour and for bug fixes alike, make the intended behaviour executable before writing the production code: a focused test that fails before the change and passes after it.

Do not force a test when it would be impractical. If the only available test would need broad harness setup, brittle mocks, slow end-to-end infrastructure, production-only state or large unrelated fixture churn, use the closest useful check instead, and say so.

## Workflow

1. **Pin the behaviour.** For a feature, take one rule or acceptance criterion at a time. For a bug, identify the intended behaviour, the current behaviour and the smallest reproduction.
2. **Write the test first.** Add the smallest test that states the intended behaviour, in the style of the project's existing tests. It should encode behaviour, not mirror an implementation.
3. **Run it and read the failure.** Run only that test (filter by name) and read its output. It must fail, and for the intended reason: an assertion about the missing behaviour, or for a feature, a compile error naming the type or member the change will add. A run that "exited with code 1" is not evidence until you have read why. If it passes, or fails for an unrelated reason, fix the test before touching production code.
4. **Make the smallest change** that gives the intended behaviour.
5. **Rerun the test,** then the tests nearest the change, and confirm they pass. Check the test count went up by the tests you added.
6. **Repeat** for the next rule or criterion. Do not write every test and all the code in one step: then nothing was seen failing.

When the requested test already passes against existing code (asked-for coverage of behaviour that already works), say so: it pins the behaviour, but it is not a failing-first test.

## Guardrails

- Do not change tests to match a wrong implementation, or weaken assertions without a clear reason.
- Keep each test focused; avoid broad fixture churn or unrelated coverage.
- If the behaviour is timing-dependent, make the test deterministic and say what it locks down.

## When you submit the work

Report the evidence, not just the outcome:

- each failing-first run and the failure it showed (the test name and the message);
- the passing run afterwards, with the test count;
- if a failing-first run was not possible, why, and the check used instead.

---
Adapted from pstack by Lauren Tan (poteto), MIT licence: https://github.com/backnotprop/pstack
````

---

## 2. fix-root-causes

**Name**

```text
fix-root-causes
```

**Description** (one line)

```text
Use when debugging, or when a repair turn hands you failing tests, a build error or a review finding: trace each symptom to its root cause and fix it there, rather than adding a guard that makes the failure go away.
```

**Instructions** (the SKILL.md body)

````markdown
# Fix root causes

When debugging, do not fix symptoms. Trace every problem to its root cause and fix it there.

**Why:** symptom fixes accumulate. Each workaround makes the system harder to reason about, and the real bug remains, to come back as another failing test or review finding. Root-cause fixes are slower up front but cost less in total.

**Pattern:**

- Reproduce first: run the failing test or command and read its actual output.
- Ask "why" until you reach the root cause.
- Do not add guards. A null check, try/catch or early return that silences a crash or a failing assertion is a symptom fix.
- Never make a test pass by weakening, skipping or deleting it, unless the expected behavior genuinely changed; say so when it did.
- If a workaround needs a paragraph-long comment to justify it, the code is wrong: fix the code, not the comment.
- Check for the pattern, not just the instance: search for the same mistake elsewhere and fix every instance.
- When stuck, instrument; don't guess. Add logging or a small script, read the actual error, then remove the instrumentation.

**Bugs that appear only on a second run: suspect state before code.** When something fails only after a restart or a rerun, suspect stale persistent state first: config files, caches, lock files, generated or serialized files. If clearing that state restores the behavior, fix how it is validated or written.

---
Adapted from pstack by Lauren Tan (poteto), MIT licence: https://github.com/backnotprop/pstack
````

---

## 3. prove-it-works

**Name**

```text
prove-it-works
```

**Description** (one line)

```text
Use after finishing a change and before submitting the work: check the real behavior directly (run it, read the actual value, inspect the diff) rather than trusting that it compiles, that tests exist, or your own account of what you did.
```

**Instructions** (the SKILL.md body)

````markdown
# Prove it works

Verify every change by checking the real thing directly. Do not infer from proxies, from your own account of what you did, or from "it compiles".

**Why:** unverified work has unknown correctness. Indirect checks feel cheaper than direct observation, but a wrong inference costs far more later: a failed verification, a review finding, or a change request after handoff.

**First, check your edits landed.** After writing or editing a file, read it back and find the change in it. A replace whose anchor matched nothing returns the text unchanged and reports no error: a `\n` anchor in a file with `\r\n` line endings is the usual cause. Never say a test or change is "in place" until you have seen it in the file.

**Check the real thing, not a proxy:**

- Run the tests by name and read the counts: after adding tests, the number run must go up by as many as you added. The same count as before means your test is not there.
- Run the code path you changed, and read its actual output or value.
- Make sure the tests you rely on exercise the change: they pass with it, and would fail without it.
- Read your own diff end to end before submitting: leftover debugging code, unrelated edits, missed call sites, and files you meant to change but did not.
- Check each acceptance criterion against what the code now does, one by one.
- When a check fails, suspect the way you observed it before suspecting the system.

**Show it working.** In your summary, include one concrete example of the new behaviour as the code now gives it: an input and the result, taken from a test you ran or its output, not described from memory.

**When you submit the work,** say how you verified it: what you ran and what it showed, and anything you could not verify and why.

---
Adapted from pstack by Lauren Tan (poteto), MIT licence: https://github.com/backnotprop/pstack
````
