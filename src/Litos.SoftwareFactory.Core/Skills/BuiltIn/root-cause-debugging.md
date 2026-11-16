---
name: root-cause-debugging
version: 1
description: Use in work turns of a bug task, and in any repair turn after the factory's build or tests failed: reproduce the failure with a test first, find the root cause, then fix it once with a regression test.
defaultFor: bug
---

Applies to work turns of a bug task, and to repair turns. The brief comes first: where they differ, its rules and allowance win. No fix before a root cause.

## 1. A command that fails on this exact problem
Before any theory, get a command that goes red on it: a failing test, a test filter or a small script. Not starting the application, deploying or touching a database; the brief forbids those. Check it shows the reported symptom, not a nearby failure. In a repair turn, start from the failing tests and output the brief lists, and read the whole error. If it's flaky, raise the failure rate by looping the test with a timeout. If no such command can be built, call `request_decision` saying exactly what you need.

## 2. Narrow it
Trim the reproduction until every part matters. Check recent changes to the code involved (git log, git diff). Find similar code that works and list the differences.

## 3. Hypotheses
Write 3–5 ranked hypotheses, each with what you'd see if it were true. Test the first with the smallest probe, changing one thing at a time. Tag temporary logging with one unique prefix, such as `[DBG-7f3a]`, so you can remove all of it.

## 4. Fix once
Keep a failing regression test at a public seam. Fix the cause where the bad value starts, not where it shows. Rerun the reproduction and the related tests. One fix, no unrelated changes.

## 5. The three-fix rule
Count failed fixes. After the third in a turn, stop: an assumption or the design is probably wrong. Do the same when the repair brief says this is its last cycle and the cause is still unproven. Call `request_decision` with what you tried, what each attempt showed and the options you see, rather than guess.

## 6. Clean up
Remove every tagged log and throwaway script. State the root cause in one sentence in your `summary`.
