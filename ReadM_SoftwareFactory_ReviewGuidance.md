# Litos Software Factory — Review Guidance

**Document:** `ReadM_SoftwareFactory_ReviewGuidance.md`  
**Target:** Litos Software Factory V1  
**Status:** Final recommendation  
**Date:** 2026-10-06

---

## 1. Executive Recommendation

Litos Software Factory should **not have a mandatory heavyweight LLM code-review stage for every task**.

The factory should keep **verification as mandatory**, but make **LLM review conditional and risk-based**.

The recommended principle is:

> **Tests by default. Review by exception. Evidence always.**

The default pipeline should be:

```text
Task
  ↓
Acceptance Contract
  ↓
Implementation
  ↓
Build
  ↓
Unit / Regression Tests
  ↓
Deterministic Checks
  ↓
Risk Evaluation
  ↓
┌───────────────────────────────┐
│ Low risk     → Handoff        │
│ Medium risk  → Quick Verifier │
│ High risk    → Deep Review    │
└───────────────────────────────┘
  ↓
Human application acceptance where required
```

A review stage that routinely consumes as many or more tokens than implementation is a warning sign. Verification should generally be **narrower, cheaper, and evidence-driven** than implementation.

---

## 2. Why Litos Should Not Review Every Change

An implementation agent has the difficult job of understanding the task, inspecting the repository, finding the correct change location, writing code, adapting to the existing architecture, fixing build errors, writing or updating tests, and repairing failures.

A traditional review agent often repeats much of that work by rereading many of the same files, reconstructing architecture, reinterpreting the task, exploring alternatives, and commenting on style or speculative edge cases.

This can cause review to cost more than implementation while producing little additional confidence.

That is not an efficient software-factory architecture.

Review is only valuable when it catches defects that cheaper verification mechanisms are unlikely to catch.

---

## 3. Review Is Only One Form of Verification

Litos should distinguish **verification** from **review**.

Verification includes:

```text
Build succeeds
Unit tests pass
Regression tests pass
Static analysis passes
Lint rules pass
Formatting rules pass
Acceptance scenarios pass
API contracts remain compatible
Database constraints are valid
No unexpected files changed
No secrets were added
No forbidden dependencies were added
```

An LLM reviewer is only one additional verifier.

Therefore Litos should select the **cheapest verification mechanism capable of providing sufficient confidence** for the task.

---

## 4. Recommended Litos V1 Pipeline

```text
┌──────────────────────┐
│ User / Factory Task  │
└──────────┬───────────┘
           ↓
┌──────────────────────┐
│ Acceptance Contract  │
└──────────┬───────────┘
           ↓
┌──────────────────────┐
│ Implementation Agent │
└──────────┬───────────┘
           ↓
┌──────────────────────┐
│ Build                │
└──────────┬───────────┘
           ↓
┌──────────────────────┐
│ Unit/Regression Test │
└──────────┬───────────┘
           ↓
┌──────────────────────┐
│ Deterministic Checks │
└──────────┬───────────┘
           ↓
┌──────────────────────┐
│ Risk Evaluation      │
└──────────┬───────────┘
           ↓
   ┌───────┼───────────┐
   ↓       ↓           ↓
 LOW     MEDIUM       HIGH
   ↓       ↓           ↓
Handoff  Quick      Deep
         Verify     Review
           ↓           ↓
           └─────┬─────┘
                 ↓
          Bounded Repair
                 ↓
              Handoff
```

A successful build and test run must **not automatically trigger a full repository review**.

Full review should happen only when the risk policy or verification evidence says it is justified.

---

## 5. Acceptance Contract Before Implementation

Unit tests written by the same agent that implemented the feature can share the same misunderstanding.

Example:

```text
Requirement:
Discount should be applied after tax.

Agent misunderstanding:
Discount is applied before tax.
```

The same agent may write a wrong implementation plus tests validating that wrong implementation.

Litos should therefore create a small **Acceptance Contract** before implementation.

Example:

```yaml
task: Apply preferred dealer discount

acceptance:
  - id: AC1
    given: subtotal=100, tax=10, discount=10%
    expected_total: 99

  - id: AC2
    condition: discount is zero
    expected: existing calculation remains unchanged

  - id: AC3
    condition: discount is negative
    expected: request is rejected

  - id: AC4
    expected: existing dealer calculation tests remain green
```

The contract should be frozen before implementation where practical.

This gives verification an independent target.

---

## 6. Verification Levels

Add an explicit verification level to the factory.

```csharp
public enum VerificationLevel
{
    DeterministicOnly,
    QuickVerify,
    DeepReview
}
```

### DeterministicOnly

Use when the change is small and local, existing tests cover the behavior, no sensitive area is touched, the diff is small, and no unusual repair activity occurred.

Checks might include:

```text
Build
Unit tests
Regression tests
Lint
Static analysis
Acceptance contract
Diff sanity checks
```

No extra LLM reviewer is required.

### QuickVerify

Use for ordinary feature work and moderate-risk changes.

The verifier should receive only targeted evidence:

```text
Task
Acceptance Contract
Changed files
Git diff
Build result
Test result
Implementation summary
Any warnings or repairs
```

It should **not receive the entire repository by default**.

The verifier's job is:

> Find a concrete defect in the change.

Not:

> Perform an architectural critique of the repository.

### DeepReview

Use only for high-risk tasks.

Examples include authentication, authorization, security, payments, accounting calculations, database schema migrations, destructive data operations, concurrency, locking, transactions, public API contract changes, infrastructure, deployment, dependency changes, cryptography, sensitive configuration, large cross-cutting changes, poor test coverage, or repeated failed repair attempts.

Deep review may read additional files and spend a larger token budget.

---

## 7. Deterministic Risk Scoring

Do not use one LLM to decide whether another LLM is needed.

Start with a deterministic scoring policy.

| Condition | Score |
|---|---:|
| Authentication / authorization | +4 |
| Security-sensitive code | +4 |
| Payment / financial calculation | +3 |
| Database schema / migration | +3 |
| Concurrency / locking / threading | +3 |
| Destructive data operation | +3 |
| Public API contract changed | +2 |
| Infrastructure / deployment | +2 |
| New external dependency | +2 |
| Existing tests removed or weakened | +2 |
| More than 10 files changed | +2 |
| Large diff | +2 |
| Build required repair | +1 |
| Tests required repair | +1 |
| Agent reported uncertainty | +1 |
| Acceptance criteria partly inferred | +1 |

Initial policy:

```text
Score 0–2   → DeterministicOnly
Score 3–5   → QuickVerify
Score 6+    → DeepReview
```

These thresholds should later be tuned from Litos telemetry.

---

## 8. Quick Verifier Design

The Quick Verifier is the most important replacement for a heavyweight mandatory review.

### Input

```text
TASK
----
Add duplicate customer-email validation.

ACCEPTANCE CRITERIA
-------------------
1. Duplicate active email must be rejected.
2. Existing customer update must still work.
3. Email comparison is case-insensitive.

CHANGED FILES
-------------
CustomerService.cs
CustomerServiceTests.cs

DIFF
----
<git diff>

BUILD
-----
PASS

TESTS
-----
142 passed
3 new tests
0 failed

IMPLEMENTER NOTES
-----------------
Added normalized email lookup.
```

### Prompt

```text
You are a targeted software-change verifier.

Your job is to find a concrete defect introduced by this change.

Check only for:
- requirement violations;
- correctness defects;
- regressions;
- security problems;
- data integrity problems;
- clearly missing edge cases implied by the acceptance contract.

Do NOT:
- suggest stylistic improvements;
- suggest optional refactoring;
- redesign working code;
- compare alternative architectures;
- inspect unrelated files without concrete reason;
- produce general best-practice advice.

Use the supplied task, acceptance contract, diff, build result,
and test result as primary evidence.

Return exactly one of:

PASS

or

FAIL
Severity: <critical|high|medium>
Finding: <specific defect>
Evidence: <why the diff violates the requirement or creates a defect>
Suggested check: <smallest verification needed>
```

The Quick Verifier should be **adversarial but bounded**. It should try to falsify the patch, not redesign it.

---

## 9. Review Token Budgets

Litos should explicitly budget verification tokens.

A good initial policy:

```text
Implementation budget      = task allocation

Quick verification budget  = 10–20% of implementation tokens

Deep review budget          = 25–40% of implementation tokens
                              and only when triggered
```

Suggested starting formula:

```text
QuickVerifyBudget =
    max(MinQuickVerifyTokens,
        ImplementationTokens * 0.15)
```

Example:

```text
Implementation: 20,000 tokens

Quick verifier:
max(2,000, 3,000)

Budget = 3,000 tokens
```

A Quick Verifier that wants to consume 15,000–30,000 tokens should stop rather than expand into an uncontrolled review.

---

## 10. Hard Stop Rules

Review should stop when:

```text
Token budget exhausted
No concrete defect found
Finding is only stylistic
Finding is only optional refactoring
Finding requires unrelated repository exploration
Finding cannot be tied to acceptance criteria or regression risk
Reviewer starts proposing an alternative implementation without proving a defect
```

The factory should record:

```text
PASS_WITHIN_BUDGET
FAIL_WITH_FINDING
ESCALATE_INSUFFICIENT_EVIDENCE
```

Avoid unlimited "keep reviewing until confident" behavior.

---

## 11. Bounded Repair Should Stay

Litos should keep bounded repair loops because they react to objective evidence.

Example:

```text
dotnet build
   ↓
FAIL
   ↓
Compact error evidence
   ↓
Implementation/repair agent
   ↓
dotnet build
```

Likewise:

```text
dotnet test
   ↓
FAIL
   ↓
Relevant test failure
   ↓
Repair
   ↓
dotnet test
```

The same model should apply to verifier findings:

```text
Quick Verifier
   ↓
Concrete FAIL
   ↓
Repair
   ↓
Re-run affected deterministic checks
   ↓
Re-run verifier once if required
```

Do not allow endless:

```text
review → repair → review → repair → review ...
```

A reasonable V1 limit is:

```text
Maximum verifier-triggered repair loops: 1–2
```

After that, escalate to human review.

---

## 12. Evidence Packet

Every completed task should create a compact evidence packet.

```json
{
  "taskId": "TASK-184",
  "verificationLevel": "QuickVerify",
  "riskScore": 4,
  "changedFiles": 3,
  "build": "passed",
  "tests": {
    "passed": 142,
    "failed": 0,
    "new": 3
  },
  "acceptanceCriteria": {
    "passed": 4,
    "failed": 0
  },
  "review": {
    "result": "PASS",
    "tokens": 2870
  }
}
```

This is more useful than a long review transcript.

The human handoff can say:

```text
Implementation completed.

Build: PASS
Tests: 142 PASS
New tests: 3
Acceptance checks: 4/4
Risk: Medium (4)
Quick verifier: PASS
Files changed: 3
Human application test required: Yes
```

---

## 13. Metrics Litos Should Track

Do not measure `review completed` as success.

Track actual value.

### Token metrics

```text
planning_tokens
implementation_tokens
test_generation_tokens
repair_tokens
quick_verification_tokens
deep_review_tokens
total_task_tokens
```

### Verification outcomes

```text
build_failures_found
unit_test_failures_found
regression_failures_found
acceptance_failures_found
quick_verifier_defects_found
deep_review_defects_found
human_defects_found
production_defects_found
```

### Finding quality

Classify findings as:

```text
Critical defect
Real defect
Questionable finding
Style/refactoring suggestion
False positive
Duplicate finding
```

---

## 14. Review Yield

Add a metric such as:

```text
Review Yield =
    confirmed actionable defects
    ----------------------------
    review tokens
```

A more operational version:

```text
DefectsPer100KReviewTokens =
    confirmed defects * 100000
    --------------------------
    review tokens
```

Also track:

```text
CriticalDefectsPer100KTokens
FalsePositiveRate
ReviewerEscalationRate
ReviewerRepairSuccessRate
HumanDefectsAfterQuickVerify
HumanDefectsAfterDeepReview
```

This will tell Litos whether review is actually worth paying for.

---

## 15. Self-Improving Verification Policy

This is where Litos can become genuinely self-improving.

Suppose telemetry shows:

```text
Task category: CRUD UI

Last 100 tasks:
- Builds passed
- Unit tests passed
- Quick verifier found 1 minor defect
- Deep review found 0 additional defects
- Human acceptance found 2 UI issues
```

Litos can learn:

```text
Deep review adds almost no value for this task class.
Use DeterministicOnly or QuickVerify.
Allocate more effort to UI acceptance instead.
```

Another category may show:

```text
Task category: DB migration

Last 30 tasks:
- Quick verifier found 3 serious defects
- Deep review found 4 additional defects
```

Then Litos should keep DeepReview for migrations.

Eventually verification policy can be based on empirical risk rather than intuition.

---

## 16. Suggested Verification Decision Model

```csharp
public sealed record VerificationDecision(
    VerificationLevel Level,
    int RiskScore,
    IReadOnlyList<string> Reasons,
    int TokenBudget);

public interface IVerificationPolicy
{
    VerificationDecision Decide(
        FactoryTask task,
        ChangeSummary change,
        VerificationEvidence evidence);
}
```

Example:

```csharp
var decision = verificationPolicy.Decide(
    task,
    changeSummary,
    verificationEvidence);

switch (decision.Level)
{
    case VerificationLevel.DeterministicOnly:
        break;

    case VerificationLevel.QuickVerify:
        await quickVerifier.VerifyAsync(...);
        break;

    case VerificationLevel.DeepReview:
        await deepReviewer.ReviewAsync(...);
        break;
}
```

The policy itself should initially be deterministic and easy to audit.

---

## 17. Do Not Let Review Rediscover the Repository

One of the biggest sources of wasted tokens is allowing the reviewer to repeat implementation discovery.

Avoid:

```text
Reviewer starts
↓
Read solution
↓
Read README
↓
Read project files
↓
Read architecture
↓
Search repository
↓
Read 20 files
↓
Understand task
↓
Review patch
```

Prefer:

```text
Task
+
Acceptance Contract
+
Diff
+
Changed-file excerpts
+
Test evidence
+
Build evidence
+
Implementation notes
↓
Quick Verifier
```

Only DeepReview should be allowed to expand context, and even then it should do so because of a specific suspected failure mode.

---

## 18. Use Diff-First Verification

The verifier should begin with:

```bash
git diff <base>...<task-branch>
```

Then inspect only:

1. changed files;
2. direct callers/callees needed to understand the change;
3. relevant tests;
4. contracts/interfaces touched by the change.

This gives verification a much smaller context footprint.

---

## 19. Deterministic Checks Before LLM Review

Run cheap checks first.

Examples for .NET projects:

```bash
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet format --verify-no-changes
```

Depending on the repository:

```text
Roslyn analyzers
StyleCop
Sonar analyzers
Security scanners
Dependency vulnerability checks
SQL migration validation
API schema comparison
Lint rules
JEV/Laya targeted checks
```

There is little reason to spend LLM tokens finding something a compiler or analyzer can detect reliably.

---

## 20. Relation to JEV / Lint / PTC

JEV or similar lightweight models can fit below full review.

```text
Implementation
   ↓
Build
   ↓
Tests
   ↓
Deterministic lint
   ↓
JEV / targeted code checks
   ↓
Risk gate
   ↓
Optional LLM QuickVerifier
```

PTC should respond to **specific evidence**, not trigger broad re-analysis.

Example:

```text
Verifier finding:
Null branch introduced in CustomerService.cs:184

PTC task:
Correct this specific issue while preserving all passing tests.
```

This is preferable to sending the entire task back through implementation.

---

## 21. Human Review Boundary in V1

The Litos Factory V1 philosophy includes human application-level acceptance.

That makes mandatory heavyweight AI review even less attractive.

A sensible V1 confidence stack is:

```text
Implementation
    ↓
Compiler/build
    ↓
Unit/regression tests
    ↓
Acceptance contract
    ↓
Optional QuickVerifier
    ↓
Optional DeepReview
    ↓
Human application acceptance
```

Litos does not need perfect certainty before human handoff.

It needs **high-confidence, low-cost evidence** that the change is ready for acceptance.

---

## 22. Tasks That Probably Need No LLM Review

Examples:

```text
Small localized bug fix
Well-covered CRUD change
Simple validation addition
Text/label change
Configuration value change with tests
Straightforward mapping change
Adding a covered API field
Minor refactor with unchanged behavior
Generated code update validated by tests
```

If build, tests, acceptance criteria, and low-risk scoring all pass, hand off.

---

## 23. Tasks That Should Usually Trigger QuickVerify

Examples:

```text
Normal feature implementation
Moderate business logic changes
Multiple related files changed
New validation logic
New API endpoint
Changes to data access code
Moderately complex calculations
New unit tests written by the implementation agent
```

---

## 24. Tasks That Should Usually Trigger DeepReview

Examples:

```text
Authentication
Authorization
Security boundaries
Money
Accounting
Inventory valuation
Database migrations
Data deletion
Concurrency
Transactions
Public API breaking changes
Infrastructure
Deployment
Secrets
Dependency upgrades with broad impact
Large architectural changes
Cross-cutting changes
Insufficient test coverage
Repeated implementation failures
```

---

## 25. Recommended Litos V1 Defaults

```yaml
verification:
  default_level: deterministic_only

  quick_verify:
    enabled: true
    implementation_token_ratio: 0.15
    minimum_tokens: 2000
    max_repair_loops: 1

  deep_review:
    enabled: true
    implementation_token_ratio: 0.35
    minimum_risk_score: 6
    max_repair_loops: 2

  risk:
    quick_verify_threshold: 3
    deep_review_threshold: 6
```

Treat these numbers as starting values, not permanent constants. Tune them from factory telemetry.

---

## 26. Recommended Migration From a Mandatory Review Pipeline

### Phase 1 — Measure

Keep the current reviewer temporarily and record:

```text
review tokens
review duration
findings
accepted findings
rejected findings
human defects
```

### Phase 2 — Introduce QuickVerifier

Run:

```text
Build
Tests
QuickVerifier
Existing Review
```

Compare what the QuickVerifier catches versus the existing reviewer.

### Phase 3 — Risk Gate Deep Review

Change to:

```text
Build
Tests
Risk Gate

Low     → no review
Medium  → QuickVerifier
High    → DeepReview
```

### Phase 4 — Learn From Outcomes

Use telemetry to adjust:

```text
risk weights
thresholds
token caps
task-category policies
```

---

## 27. What Not to Optimize For

Do not optimize for:

```text
Maximum number of reviewer comments
Maximum code coverage alone
Maximum reviewer context
Maximum reasoning length
Maximum number of agents
Maximum review passes
```

Optimize for:

```text
Defects prevented
Regression rate
Human acceptance rate
Production failure rate
Tokens per successful task
Time per successful task
Confirmed defects per verification token
```

---

## 28. Final Design Principle

The factory should not ask:

> "Has another agent reviewed this code?"

It should ask:

> "What evidence do we have that this change satisfies the task, preserves existing behavior, and is safe enough for its risk level?"

That evidence may come from:

```text
compiler
tests
acceptance scenarios
static analysis
lint rules
runtime checks
quick verifier
deep reviewer
human validation
```

The LLM reviewer is therefore **one selectable verification tool**, not a mandatory ceremony.

---

# Final Recommendation

For Litos Software Factory V1:

1. **Remove mandatory heavyweight review from the happy path.**
2. **Keep build, tests, deterministic checks, and bounded repair mandatory.**
3. **Create/freeze an Acceptance Contract before implementation where practical.**
4. **Introduce a very small diff-first QuickVerifier.**
5. **Use deterministic risk scoring to decide whether QuickVerify or DeepReview is required.**
6. **Put hard token caps on all reviewer activity.**
7. **Record verification evidence rather than long review transcripts.**
8. **Measure confirmed review yield.**
9. **Retain human application acceptance in V1.**
10. **Let telemetry gradually teach Litos which task categories need deeper verification.**

The resulting philosophy should be:

> **Tests by default. Review by exception. Evidence always.**

And the long-term objective should be:

> **Use the cheapest independent verifier that provides enough confidence for the risk of the change.**

That gives Litos a path toward a software factory that becomes **more reliable while also becoming cheaper**, rather than increasing confidence simply by adding more agent passes.
