---
name: review-rubric
version: 1
description: Use in review turns (ending with submit_review), inside the depth and list the review brief sets: grade findings blocking or minor consistently, spot tests that prove nothing, and word each finding so it can be fixed without asking.
defaultFor: bug, feature, refactor, chore
---

Applies to turns that end with `submit_review`. The review brief sets the process: how deep the review goes, what to look for, what you may read or run, and when to stop. This skill never widens it. In a light review you still read and run nothing; in a full review you still check only the brief's list, within its allowance. This skill only helps you judge what you find.

## Blocking or minor
- blocking: an acceptance criterion not met, or no test that shows it; a bug a person testing would hit; an existing test deleted, skipped or weakened; a change outside the request that could break something.
- These are blocking however small the fix: wrong text on screen (escape sequences, placeholders, a garbled message), and a criterion that holds only in some of the cases it names. Judge by what the tester would see, not by the size of the fix.
- minor: safe for a person to test but worth fixing, such as an untested small edge, unclear naming, or leftover debug or commented-out code.
- Before you submit, reread each minor finding and ask: would a tester hit this, or does it break a criterion? If yes, it is blocking.
- Leave out style a formatter fixes, taste, ideas for more work, and anything you haven't checked.

## Tests that prove nothing
Signs visible in the diff: expected values computed the same way the code computes them; asserting that a mock was called instead of what the caller gets; a test named for a criterion that doesn't exercise it; a test that would still pass if the change were reverted.

## Each finding
One sentence: what is wrong, where, and what a person would see, concrete enough to fix without asking. A false finding costs a rework turn. When you're unsure and the brief lets you check, check; when it doesn't, say what you would need to check, as the brief asks. An empty list is right for a clean change.
