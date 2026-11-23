---
name: grill-the-request
version: 1
description: Use in spec and decision-scan turns (ending with submit_spec or submit_plan): map every decision the request leaves open, settle facts from the code yourself, and hand a person only real choices, each with a recommendation.
defaultFor: bug, feature, refactor
---

Applies to turns that end with `submit_spec` or `submit_plan`. The brief comes first: its categories, read-only tools and few-call allowance win. This skill only sharpens how you find and word the choices.

Goal: no silent assumption reaches the code. A person decides only what needs a person; you settle the rest with evidence.

## 1. Map the decisions
From the request, the spec, the decisions in the brief and the code you read for the approach, list what the work depends on: behaviour, scope edges, existing data, errors and empty states, permissions, wording users see, compatibility, limits. Order them parents first; a choice that depends on an unsettled one sits under it, and is not asked on its own.

## 2. Settle facts yourself
How things are now (what the code does, which library is used, how a similar feature behaves) is a fact. Look it up and quote it in `settledBy`. Never ask a person a fact.

## 3. Word each real choice
- One question a non-engineer can answer, with 2–4 concrete options that differ in outcome, not wording.
- Your recommendation, worded so that "yes" accepts it, and who or what it affects.
- One choice per question.

## 4. Test the edges, from what you have read
For each criterion ask: empty input, the largest input, a duplicate, a failure halfway, a user without permission, data that existed before this change. For a form that creates or changes records, ask of every input whether it is required, how long or large it may be, and what format or range it takes, and ask what a clash with an existing record does (a duplicate, a double booking). Don't read further just to hunt for edges. Each edge left open becomes a choice or a criterion.

## 5. Write it up
- `submit_plan`: list choices parents first; the factory decides which to ask. Never settle one quietly.
- `submit_spec`: each acceptance criterion checkable by a test or a named manual step, edges included; `openQuestions` carry your recommendation.

In a work turn, a choice like this that you meet only now goes to `request_decision`, as the brief says.
