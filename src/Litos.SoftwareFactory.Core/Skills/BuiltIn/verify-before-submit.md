---
name: verify-before-submit
version: 1
description: Use before every submit_work: pre-run the verification the brief lists, keep the diff to the request, map every criterion to its tests, and write the handoff fields so a tester can follow them without asking.
defaultFor: bug, feature, refactor, chore
---

Applies before every `submit_work`. The brief comes first: only the factory's own verification run is evidence. This check is there so that run passes first time, and the review and handoff after it go smoothly. Never present your own results as proof.

## 1. Pre-check
1. After your last edit, run the verification the brief lists under "Verification the factory will run", once. Print only the summary and failures. Fix every failure now; one already in the brief's baseline is not yours, but don't hide it. Make every listed command work for what you added: a script, report or config it expects in a new folder is part of the work.
2. If that verification doesn't run the tests for code you changed (another language or folder), run those tests too, and say in `knownLimitations` that the factory's verification doesn't cover them.
3. Read your diff: every file belongs to the request; no debug output, commented-out code, stray files, secrets or unrelated reformatting. A smaller diff gets a lighter, cheaper review.

## 2. Make the review cheap
- Fill `criteria` from what the tests actually cover: each criterion names its tests. A criterion with no test makes the factory review the change more deeply.
- A criterion about code you didn't change (existing behaviour that must keep working) names the existing tests that already check it, not your new ones. Never list a test that doesn't exercise the criterion.
- Use `manualOnly` only when no unit test could check it, such as a layout or a real device.

## 3. Make the handoff stand on its own
The tester sees only what you write here. Each thing they can't follow costs a round of "request changes".
- One `manualTestSteps` entry per manual-only criterion: what to do, and what they should see.
- Put anything assumed, untested or unfinished in `knownLimitations`. A surprise found in testing costs more than one stated.
- `setupChanges` names every new setting, variable or unapplied script.

## Not part of this check
Starting the application, deploying, running migrations or changing the verification configuration: the brief forbids them.

If something still fails and you can't fix it, say so in `knownLimitations`, or call `request_decision` if the fix needs a choice that isn't yours.
