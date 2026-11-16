---
name: safe-refactor
version: 1
description: Use in work turns of a refactor task: pin current behaviour with tests first, then change structure in small steps that each stay green, with no change in behaviour.
defaultFor: refactor
---

Applies to the work turns of a refactor task. The brief comes first: where they differ, its rules and allowance win. A refactor changes how code is arranged, never what it does.

## 1. Pin current behaviour first
- Find and run the tests covering the code you will reshape.
- Where coverage is thin, first add characterization tests: tests that record what the code does now, through its public interface, odd behaviour included. Run them green on the unchanged code. Don't fix odd behaviour while pinning it; note it in the submission.

## 2. Small steps
Make one named move at a time: rename, extract, move, inline, split. Build and run the pinned tests after each. If they go red, undo that step. For a rename, find every use, including strings, reflection, config, docs and other projects.

## 3. Behaviour stays the same
- Public APIs, wire formats, stored data, file paths and messages others parse keep working. If one must change, that's a behaviour change: call `request_decision`.
- Existing tests change only to follow a rename or move of what they call.
- No features, bug fixes or dependency upgrades mixed in; list the ones you spot.

## 4. Done
The same tests pass as before, plus the new ones, with no new build warnings. The submission lists the moves made.
