---
name: tdd
version: 1
description: Use in work turns (ending with submit_work) of a feature or bug task, before writing production code: find and run the tests that already cover the code you will touch, then build the change one failing test at a time.
defaultFor: bug, feature
---

Applies to turns that end with `submit_work`; ignore it in other turns. The brief comes first: where they differ, its rules and allowance win.

## 1. Find the tests that guard this code, before any edit
- For each file you expect to change, find the tests that exercise it and its callers.
- Run just those tests now and note the result; print only the summary and failures. A test already failing is not yours to fix silently; name it in `knownLimitations`.
- They are the contract: never delete, skip or weaken one. If the request needs behaviour an existing test pins to change, that is a change to public behaviour: go ahead only if the approved spec or a decision in the brief settles it, otherwise call `request_decision`.

## 2. Red, then green, in batches
Work in slices of a few related criteria (for a bug: the reported symptom). The brief asks for few, large calls, so do each slice in one or two scripts, not one call per step:
1. Write the slice's tests, one behaviour each, through the public interface a caller uses, named for the behaviour. Give the code under test only empty stubs, so it compiles.
2. Run just those tests and see each one fail because the behaviour is missing, not from a compile or import error. A test that passes at once proves nothing: fix the test. Print only the failing names.
3. Write the least production code that makes them pass. No extras.
4. Rerun them with the tests from step 1. All green before the next slice.
Never write all the code first and test it at the end. Scaffolding and config need no failing test.

## 3. Good tests
- Assert what a caller can observe, not private methods or calls between internals.
- Take expected values from the spec or a literal worked out by hand, never by recomputing them the way the code does.
- Mock only what a test can't run: network, clock, outside services.
- Follow the project's test framework, style and folder layout.

## 4. Tidy only on green
Clean up names and duplication in what you just wrote, rerun, stay green. Leave code you didn't need to touch.

Start the slice again if you wrote production code before its test, or never saw a test fail.
