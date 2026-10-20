# ReadMe_CodeVerifyOptimisations.md

## Goal

Improve Litos Software Factory verification so that it is **reliable, framework-agnostic, fast, and token-efficient**.

The key principle is:

> **Automation should execute verification. The agent should decide what to verify and interpret the evidence.**

The verification agent should not re-discover the repository, re-implement the solution, or replay the entire application workflow unless the change genuinely requires it.

---

## 1. Verification Should Be Progressive

Do not start every task with a large general-purpose verifier.

Use progressively more expensive verification:

### Level 0 — Deterministic checks
Run automatically where applicable:

- build / compile
- lint / static analysis
- type checks
- unit tests
- targeted integration tests
- database migration checks
- formatter / schema validation

If these fail, stop and return the evidence.

### Level 1 — Targeted functional verification
Verify only the feature or behaviour affected by the change.

Examples:

- API endpoint → targeted HTTP test
- CLI → invoke command and inspect output
- web UI → targeted browser interaction
- desktop app → targeted UI automation
- database change → execute migration + relevant queries
- library → focused integration/contract tests

### Level 2 — Agent review
Give the verifier only:

- task / acceptance criteria
- changed files
- git diff
- implementation summary
- deterministic test results
- targeted runtime evidence

Ask it to find:

1. unmet acceptance criteria
2. obvious correctness/regression risks
3. documented repository-rule violations

### Level 3 — Deep verification
Escalate only when:

- tests fail
- behaviour is ambiguous
- security/authentication/permissions changed
- database schema/migrations changed
- cross-cutting architecture changed
- the verifier finds contradictory evidence
- the change is high-risk

---

## 2. Pass an Evidence Package, Not the Whole Repository

The implementation worker should produce a compact `ImplementationEvidence` object.

Example:

```json
{
  "task": "Add duplicate-email validation",
  "acceptanceCriteria": [
    "Duplicate email is rejected",
    "Existing users remain unaffected"
  ],
  "changedFiles": [
    "CustomerService.cs",
    "CustomerServiceTests.cs"
  ],
  "summary": "Added duplicate lookup before insert.",
  "testsRun": [
    "CustomerServiceTests"
  ],
  "testResult": "42 passed, 0 failed",
  "knownRisks": [
    "Database-level uniqueness is still recommended"
  ]
}
```

The verifier should begin from this evidence and the diff.

It should read additional files only when needed.

---

## 3. Add Repo-Specific Verification Knowledge

Each repository should be able to contain a small verification definition, for example:

```text
.litos/
  verification/
    manifest.json
    README.md
    scenarios/
```

This should describe reusable facts such as:

- how to build the project
- how to start it
- health checks
- test commands
- available verification personas
- authentication bootstrap
- common test-data setup
- important feature entry points
- cleanup commands
- known platform-specific behaviour

The factory should learn this once instead of making every verifier rediscover it.

---

## 4. Separate Preconditions From Behaviour Under Test

This is especially important for UI-heavy applications.

If a screen requires ten previous transactions before it can be tested, those transactions do not necessarily need to be performed through the UI.

Use a repository-provided verification harness, CLI, API, fixtures, or test helper to create **preconditions**.

Example:

```text
prepare invoice-edit
    ↓
creates customer
creates product
creates order
creates invoice
returns target identifier
```

Then test the actual changed behaviour through the real application surface.

Rule:

> **Never bypass the behaviour that is part of the acceptance criteria.**

Setup may be synthetic. The behaviour under test should be real.

---

## 5. Use the Cheapest Appropriate Verification Surface

Verification should match the change.

Examples:

| Change | Preferred verification |
|---|---|
| Pure function | unit test |
| Library/API contract | integration/contract test |
| REST endpoint | targeted HTTP request |
| CLI | command invocation |
| Database migration | disposable database + assertions |
| Web screen | targeted browser automation |
| Desktop/mobile UI | targeted UI automation |
| Auth/permissions | real authentication/authorization flow |
| Configuration | startup + health check |

Do not use a browser to verify something a unit test can prove.

Do not use a unit test to claim that a real UI workflow works.

---

## 6. Authentication Should Usually Be Infrastructure

For ordinary feature verification:

- create/seed a verification user
- authenticate once
- reuse the authenticated session for the task
- verify only the affected feature

Perform the full login/authentication journey only when authentication or authorization itself changed.

Secrets must come from secure environment/configuration, never from verification files.

---

## 7. Keep Agent Context Small

The verifier should prefer:

1. task
2. acceptance criteria
3. diff
4. changed files
5. implementation evidence
6. test/runtime evidence
7. targeted source reads only when necessary

Avoid:

- full repository scans
- rereading unchanged files
- replaying implementation reasoning
- broad architectural exploration for local changes
- dumping large logs into the context

Large logs should be summarized, with raw logs stored outside the main model context.

---

## 8. Cache Repository Knowledge

Reuse knowledge that remains valid:

- project structure
- build commands
- test commands
- file summaries
- dependency graph
- verification scenarios
- unchanged file hashes
- environment/bootstrap knowledge

Invalidate cached information when relevant files change.

This prevents implementation and verification agents from repeatedly paying to understand the same repository.

---

## 9. Make Verification Risk-Based

Verification depth should be driven by risk, not simply task size.

Possible risk signals:

- authentication/authorization
- payments
- destructive operations
- database schema
- concurrency
- public API contracts
- security-sensitive code
- shared infrastructure
- many downstream dependents
- large or cross-cutting diff

Small/local changes should remain cheap.

---

## 10. Give the Verifier a Hard Scope Contract

Recommended instruction:

> Verify the submitted implementation against the acceptance criteria using the smallest amount of repository context necessary. Do not independently reimplement the task or reconstruct the full solution. Prefer deterministic evidence and targeted checks. Escalate only when the evidence is insufficient or contradictory.

This is important because a verifier that independently solves the task can easily consume more tokens than the implementation worker.

---

## 11. Put a Token Budget on Verification

Treat verifier cost as an engineering metric.

Track per task:

- implementation tokens
- verification tokens
- verification/implementation ratio
- files read by verifier
- number of verification steps
- tests executed
- escalation level
- defects found
- false-positive findings

A useful target for normal tasks is for verification to cost **substantially less than implementation**.

Expensive verification should be the exception for high-risk or failing changes.

---

## 12. Recommended Factory Architecture

```text
Task
  ↓
Implementation Worker
  ↓
Implementation Evidence
  ↓
Verification Planner
  ↓
Deterministic Checks
  ↓
Targeted Runtime Verification
  ↓
Compact Evidence
  ↓
Focused Agent Review
  ↓
PASS / FAIL / ESCALATE
```

The **Verification Planner** should select the cheapest checks that can establish confidence for the specific change.

The **agent reviewer** should be a reasoning layer over evidence, not the primary execution engine.

---

## 13. Self-Improving Verification

When a verifier repeatedly discovers a useful procedure, convert it into reusable repository knowledge.

Examples:

- new scenario setup command
- reliable health check
- better test-data fixture
- UI selector
- authentication bootstrap
- regression test
- known failure pattern

The long-term goal is:

> **Every successful verification should make the next similar verification cheaper and more deterministic.**

---

## Recommended V1 Priorities

For Litos Software Factory V1, implement these first:

1. `ImplementationEvidence` passed from implementation to verification.
2. Deterministic build/test checks before any verifier agent call.
3. A small repo-specific `.litos/verification/` definition.
4. Targeted verification rather than full-application verification.
5. A strict verifier scope prompt.
6. Verification token/risk metrics.
7. Escalation to deep verification only when required.

These changes should provide most of the benefit without requiring a large verification framework upfront.
