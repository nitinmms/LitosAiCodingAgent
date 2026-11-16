---
name: address-feedback
version: 1
description: Use in rework turns after a tester asked for changes, and in repair turns that list blocking review findings: turn each item into a check, confirm findings before acting, change only what each needs, and report item by item.
defaultFor: bug, feature, refactor, chore
---

Applies to rework turns (the tester asked for changes) and to repair turns that list blocking review findings. The brief comes first: where they differ, its rules win.

## 1. Turn feedback into checks
Split the feedback or findings into separate items. For each, write what will be true once it is addressed, as something a test or the tester can check. If an item is ambiguous about a material choice, call `request_decision` as the brief says; don't guess what the tester meant.

## 2. Confirm before acting
Check each review finding against the code first. If one is wrong, don't change correct code to silence it. Say why in `summary`, citing the file, line or test that shows it.

## 3. Test first, change only that
For each item, write a test that fails because of the problem where a unit test can show it, then make the smallest change that passes. Don't redo or restyle work already handed off; it has been reviewed. Rerun the tests that guard what you touched.

## 4. Close the loop
In `summary`, one line per item: what changed and which test shows it. Update `criteria`, `manualTestSteps` and `runSteps` wherever the change affects them, so the next handoff stands on its own.
