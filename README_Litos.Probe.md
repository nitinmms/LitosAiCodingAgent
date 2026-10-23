# Litos.Probe

**AI-assisted, replay-first web application verification for Litos Software Factory**  
**Status:** Proposed architecture / one-week proof of concept  
**Target:** .NET 10, C#, Playwright for .NET  
**Initial scope:** Chromium-based browser verification of web applications (not mobile-native testing)  
**License goal:** MIT for Litos.Probe's own code; audit all dependencies and their licenses before distribution.

## 1. Executive summary

Litos.Probe is a lightweight, native C# verification component for [Litos Software Factory](https://litosai.dev/). It replaces the factory's **mandatory human functional-verification stop** with a **policy-controlled automated gate**. Litos.Probe opens a running web application, performs predefined or AI-discovered user interactions, evaluates explicit acceptance criteria, records verified action sequences, and replays them deterministically on later runs. When tests fail, it returns compact, actionable evidence to the existing Litos coding agent for bounded repair-and-retest cycles.

**Design principle:** AI discovers unfamiliar workflows; Playwright executes and verifies them; verified actions are replayed without spending model tokens. A successful click sequence alone is **not** evidence that a requirement is satisfied.

This is **not** a C# port or fork of TesterArmy e2e. It takes inspiration from the general pattern of hybrid agentic actions, deterministic assertions, and verified replay, while implementing a smaller factory-specific solution. Do not copy third-party source code or assume that its APIs are compatible.

### Goals

- Verify features through a real browser with minimal or no human interaction.
- Support web applications irrespective of backend: ASP.NET WebForms, Blazor, MVC, Angular, React, Vue, etc.
- Reuse existing Litos model orchestration and factory task infrastructure.
- Make routine regression runs deterministic, low-token, and reproducible.
- Return machine-readable reports, screenshots, traces, and concise failure evidence.
- Learn reusable per-project workflows while keeping each task's test data and browser state isolated.
- Preserve an explicit human fallback when behavior cannot be verified confidently or policy requires approval.

### Non-goals for V1

- Rebuilding Playwright or creating a general replacement for Playwright Test.
- Full autonomous site crawling or exhaustive test discovery.
- Native Android/iOS testing, distributed browser farms, or broad cross-browser coverage.
- Complex visual diffing, pixel-based clicking, autonomous exploratory chaos testing, or advanced self-healing.
- Automatically treating an AI model's subjective PASS response as proof of correctness.
- Automatic merge, production deployment, or removal of release-approval controls.

## 2. What changes in the software factory

**Old:** `Plan -> Implement -> Build/Unit Tests -> Human Verification Stop -> Approve/Reject`.

**New:**

```text
Task / acceptance criteria
          |
          v
Plan and implement
          |
          v
Build + unit tests
          |
          v
Start isolated test application + seed test data
          |
          v
Litos.Probe automated verification gate
          |
          +----> PASS + required coverage -> Verified -> Task Completed*
          |
          +----> Confirmed app defect -> Coding Agent fixes -> Build -> Retest
          |
          +----> Inconclusive / environment issue / retries exhausted
          |                                         |
          |                                         v
          +----------------------------> Existing Human Verification UI

* Completion is not merge or release approval; those policies stay separate.
```

The existing verification stage and human-review screen remain. Only the **gate's decision logic** changes. Avoid replacing the entire factory workflow engine.

### Suggested gate states

```csharp
public enum VerificationMode
{
    HumanOnly,
    Automated,
    AutomatedWithHumanFallback
}

public enum VerificationOutcome
{
    Passed,
    FailedApplication,
    FailedInfrastructure,
    Inconclusive,
    HumanRequired
}
```

**Passed** requires all mandatory scenarios and acceptance criteria to be covered and verified. A runner with zero tests, skipped requirements, or incomplete evidence must not return `Passed`.

| Condition | Gate behavior |
|---|---|
| All required assertions pass and required coverage is complete | Auto-verify; complete factory task if policy permits |
| Reproducible application defect | Return to coding agent for bounded repair |
| Fix passes affected test and mandated regression set | Auto-verify |
| Login/test-data/environment unavailable | Diagnose; escalate as infrastructure or inconclusive, not app bug |
| Missing test coverage or ambiguous expected behavior | Human fallback (or generate a missing scenario and rerun) |
| Subjective visual/UX approval or explicitly protected task | Human review |
| Automated repairs exhausted | Human review with evidence |
| Separately configured release approval | Remain in release-approval state, even after verification passes |

## 3. Architecture

```text
Litos.SoftwareFactory (.NET 10)
  |-- Task orchestrator / task workspace
  |-- Existing coding agent / model infrastructure
  |-- Existing lessons / artifacts / PostgreSQL factory state
  |-- Verification stage
        |
        +-- IVerificationProvider
              |
              +-- LitosProbeVerificationProvider
                    |
                    +-- Litos.Probe.Core
                    |     Scenarios, results, policies, execution limits
                    +-- Litos.Probe.Browser
                    |     Microsoft.Playwright, observation, actions, traces
                    +-- Litos.Probe.Agent
                    |     Adapter to existing Litos model/tool calls
                    +-- Litos.Probe.Replay
                    |     Validated recordings and cache lifecycle
                    +-- Litos.Probe.Fixtures
                    |     Auth setup, data provisioning, DB/API checks
                    +-- Litos.Probe.Cli (optional)
                          `probe run`, standalone diagnostics
```

**Dependency direction:** `Core` has no browser or LLM dependency. `Browser`, `Agent`, `Replay`, and `Fixtures` depend on `Core` interfaces. Factory integration depends on Probe's public interface, not its internal implementation.

Use Playwright's **official .NET bindings**; do not implement CDP, browser drivers, a DOM parser, or a custom browser engine. Native C# does **not** mean there is no Playwright browser-runtime installation requirement.

### Proposed solution layout

```text
src/
  Litos.Probe.Core/
  Litos.Probe.Browser/
  Litos.Probe.Agent/
  Litos.Probe.Replay/
  Litos.Probe.Fixtures/
  Litos.Probe.Cli/
  Litos.Factory.Integrations.Probe/     # or an existing factory project

tests/
  Litos.Probe.UnitTests/
  Litos.Probe.IntegrationTests/
  TestWebApp/                           # intentionally simple fixture application
  DemoScenarios/

docs/
  decisions/
  integration-example.md
```

The layout is suggested, not a claim about the current Litos repository. Reuse existing namespaces/projects if appropriate; don't create a parallel orchestration system.

## 4. Public contracts (proposed, not existing APIs)

```csharp
public record ProbeScenario(
    string Id,
    string Description,
    IReadOnlyList<ProbeStep> Steps,
    IReadOnlyList<ProbeAssertion> Assertions,
    IReadOnlyList<string> RequirementIds);

public record ProbeRunRequest(
    string ProjectId,
    string TaskId,
    Uri BaseUrl,
    IReadOnlyList<ProbeScenario> Scenarios,
    ProbeRunOptions Options);

public record ProbeRunResult(
    VerificationOutcome Outcome,
    IReadOnlyList<ProbeScenarioResult> Scenarios,
    IReadOnlyList<string> UnverifiedRequirementIds,
    string ArtifactDirectory,
    ProbeUsage Usage);

public interface IProbeRunner
{
    Task<ProbeRunResult> RunAsync(
        ProbeRunRequest request, CancellationToken cancellationToken);
}

public interface IBrowserDecisionModel
{
    Task<BrowserDecision> DecideAsync(
        BrowserObservation observation,
        string objective,
        IReadOnlyList<AllowedBrowserAction> allowedActions,
        CancellationToken cancellationToken);
}
```

Other types (`ProbeStep`, `ProbeAssertion`, `ProbeRunOptions`, etc.) are to be defined by implementation. **No unbounded free-form code execution** should be exposed as an AI browser action.

### Allowed V1 actions

- `Navigate(url)` — only to allowlisted application origins.
- `Click(locator)`; `Fill(locator, parameter)`; `Select(locator, value)`.
- `Check/Uncheck(locator)`; `WaitFor(locator/state)`; `ReadText(locator)`.
- `Screenshot()` and `Observe()`.
- Assertions: visible, hidden, text, URL, element state, expected error, and (separately) API/DB state.

Prefer Playwright roles, accessible names, labels, and test IDs. Treat CSS selectors as fallback; coordinate clicks are **out of scope for the one-week MVP**. The agent may select an action and locator, but the C# runtime validates arguments and executes a restricted tool.

## 5. Test specification and acceptance criteria

The factory should create an **acceptance-criteria manifest before or alongside implementation**. Tests must be traceable to user requirements; they must not be derived solely from the newly implemented UI's observed behavior.

Example (`probe.scenarios.json` — proposed Litos.Probe format):

```json
{
  "projectId": "sample-erp",
  "feature": "customer-registration",
  "requirements": [
    { "id": "REQ-1", "text": "Customer name is mandatory" },
    { "id": "REQ-2", "text": "Invalid email is rejected" },
    { "id": "REQ-3", "text": "Valid customer is persisted" }
  ],
  "scenarios": [
    {
      "id": "create-valid-customer",
      "requirementIds": ["REQ-3"],
      "steps": [
        { "type": "navigate", "path": "/customers/new" },
        { "type": "act", "goal": "Fill valid unique customer details and save" }
      ],
      "assertions": [
        { "type": "visibleText", "value": "Customer saved" },
        { "type": "dbRecordExists", "fixtureQueryId": "customer-by-test-key" }
      ]
    }
  ]
}
```

This deliberately includes **only one** of three requirements: the coverage gate must flag `REQ-1` and `REQ-2` as unverified and refuse automatic success until corresponding tests are added. Treat examples as schemas to implement, not as currently available APIs.

Use generated, unique, **parameterized** test data (e.g. `CustomerName = "Probe-{RunId}"`) so replays don't hardcode IDs or stale email addresses. Provide reset/cleanup of test data.

## 6. Execution engine

1. Factory prepares isolated application workspace and seeded database.
2. Factory starts application using project-defined startup commands; Probe polls a configured health/readiness endpoint or route.
3. Probe creates a fresh Playwright browser context per scenario or designated isolation group.
4. Probe applies saved test authentication state, if configured and valid.
5. Probe runs deterministic steps directly.
6. For an unfamiliar `act` step: collect a **compact** DOM/accessibility-oriented observation, ask the existing Litos model for one allowed action, validate action, execute via Playwright; repeat until bounded goal complete.
7. Run **explicit assertions** of user-visible results and backend/API/database state where applicable.
8. Persist evidence and result with requirement coverage.
9. Save replay actions only when the required postconditions pass; do not cache an unverified flow.
10. Return a compact result to the factory; on confirmed app defect, the existing coding agent repairs it and reruns the affected test plus required regressions.

Do not feed entire screenshots, raw HTML, full browser logs, or previous model transcripts to the model by default. Prefer a bounded list of relevant controls, page title, URL, and recent errors. A stronger vision-enabled model is a later fallback when DOM-based navigation fails.

### Playwright .NET illustrative code

```csharp
using Microsoft.Playwright;

using var playwright = await Playwright.CreateAsync();
await using var browser = await playwright.Chromium.LaunchAsync(
    new() { Headless = true });
await using var context = await browser.NewContextAsync();
var page = await context.NewPageAsync();

await context.Tracing.StartAsync(new()
{
    Screenshots = true,
    Snapshots = true,
    Sources = true
});

await page.GotoAsync("http://127.0.0.1:5176/customers/new");
await page.GetByRole(AriaRole.Textbox, new() { Name = "Customer Name" })
          .FillAsync("Probe-Test-Customer");
await page.GetByRole(AriaRole.Button, new() { Name = "Save" })
          .ClickAsync();

// Implement a retrying assertion / use Playwright .NET testing assertions
// and explicitly check persistence where acceptance criteria require it.

await context.Tracing.StopAsync(new()
{
    Path = "artifacts/trace.zip"
});
```

This is an illustrative fragment. Production code must use unique test data, exception-safe trace capture, configurable URLs, cancellation, time limits, and secure paths. Playwright context tracing records browser operations and network activity; it does **not automatically capture all custom assertions**, so Litos.Probe must store assertion outcomes in its own report.

## 7. Verified action recording and replay

Replay is the central token-saving mechanism, not a reason to trust old results.

### Recording format (proposed)

```json
{
  "schemaVersion": 1,
  "projectId": "sample-erp",
  "scenarioId": "create-valid-customer",
  "stepId": "fill-and-save",
  "instructionHash": "sha256:...",
  "environmentFingerprint": "app-route+viewport+auth-role+browser-family",
  "startingRoute": "/customers/new",
  "actions": [
    {
      "kind": "fill",
      "locator": { "role": "textbox", "name": "Customer Name" },
      "parameter": "CustomerName"
    },
    {
      "kind": "click",
      "locator": { "role": "button", "name": "Save" }
    }
  ],
  "expectedEndRoute": "/customers/new",
  "validatedRequirementIds": ["REQ-3"]
}
```

### Replay rules

1. Match project, scenario, instruction, app role, viewport, and relevant environment fingerprint.
2. Confirm a suitable **starting state**; never blindly click through the wrong page.
3. Resolve each saved locator using Playwright. If missing or ambiguous, stop replay safely.
4. Execute actions with runtime parameters; **never persist passwords, OTPs, auth cookies, or access tokens in action recordings**.
5. Rerun explicit postcondition assertions after replay; merely reaching the same page is insufficient for business correctness.
6. If replay cannot proceed, switch to live AI navigation only if within configured budgets. Log cache miss/handoff reason.
7. Promote a newly discovered recording **only after verified success**; stale or failed recordings are quarantined or invalidated.
8. If an action had an uncertain commit result (timeout after submit), inspect DB/UI before retrying to avoid duplicate transactions.

**Security:** Treat replay files as executable instructions, not trusted arbitrary JSON. Validate schema, action allowlist, URLs, locator constraints, and file provenance. Project-specific cache is separate from task-local writable cache. Promote verified cache entries after task success; prevent concurrent tasks from mutating the same cache index.

Suggested layout:

```text
<project>/.litos/probe/scenarios/      # reviewable test definitions
<project>/.litos/probe/fixtures/       # test data and reusable workflows
<factory>/probe-cache/<project>/      # verified cache snapshots
<task>/probe-cache/                   # per-run writable overlay
<task>/artifacts/probe/               # logs, traces, screenshots, reports
```

Persist reviewable tests in the application repository when appropriate. Do not commit secrets, real customer information, or auth-state files.

## 8. Authentication, WebForms, Blazor, and DB validation

### Authentication

Support `Anonymous`, `FormLogin`, and `PreAuthenticatedState` in V1. The factory injects credentials through its secret store/environment; never embed passwords in test scripts or model prompts. For OTP, MFA, SSO, or external providers, supply a restricted test-environment fixture/account or pre-provisioned session. **Do not weaken production authentication.** Auth state must be encrypted/protected where persisted, scoped to the test environment, and rotated/cleared when a task ends.

### WebForms and DevExpress

Treat WebForms as a browser application: postbacks, ViewState, dynamic IDs, asynchronous callbacks, and DevExpress grids are browser behaviors that Playwright can interact with. Prefer label/role/test IDs; add **project-specific helper actions** for ASPxGridView editing, dropdowns, and popups as needed. Don't attempt to generalize every DevExpress control during the first week.

### Blazor

Use retrying locators/assertions and explicit readiness markers for interactive state. Avoid fixed sleeps for SignalR updates and async rendering.

### Database and API assertions

UI success messages are insufficient for transactions with persistence requirements. Provide a controlled assertion registry such as `dbRecordExists`, `dbFieldEquals`, and `httpResponseMatches`. Use per-task databases/test schemas, parameterized queries, read-only verification roles where possible, and fixtures for data setup/cleanup. No arbitrary agent-generated write SQL against production or shared databases.

## 9. Failure taxonomy and repair loop

A failed UI interaction does **not** automatically imply an application defect.

| Category | Typical evidence | Action |
|---|---|---|
| `ApplicationDefect` | Incorrect validation, HTTP 500, wrong persisted state | Coding agent repair loop |
| `TestDefect` | Wrong locator, incorrect assertion or scenario assumption | Fix test only after preserving acceptance criteria |
| `EnvironmentFailure` | App not started, missing DB, unreachable URL | Retry/startup diagnosis; escalate |
| `AuthenticationFailure` | Invalid test credentials, expired test session | Fixture correction/escalation |
| `ReplayStale` | Locator moved or start/end state mismatched | Live discovery + verify; refresh cache |
| `ModelFailure` | Timeout, malformed decision, unavailable provider | Fallback or inconclusive |
| `InsufficientCoverage` | Requirement without validating assertion | Generate missing test or human fallback |
| `AmbiguousOutcome` | Action may have committed before timeout | Read-only state check before retry |

Suggested bounded loop:

```text
Probe.Run -> Failure report -> Classification
  -> confirmed ApplicationDefect? -> Litos code repair
  -> build/unit tests -> affected Probe scenarios -> core smoke regression
  -> pass? finalize : continue (max 2 repairs)
  -> limit exceeded / inconclusive? existing human stop
```

**Anti-gaming invariant:** The coding agent cannot silently delete failing scenarios, weaken assertions, replace independent expected results with observed values, or mark unverified requirements as passed. Changes to acceptance criteria need an explicit review or trusted workflow approval.

## 10. Factory integration: minimal adapter

Use the existing verification-stage extension point, or introduce a small interface if no extension point exists:

```csharp
public interface IVerificationProvider
{
    Task<VerificationResult> VerifyAsync(
        FactoryTaskContext context,
        CancellationToken cancellationToken);
}
```

`LitosProbeVerificationProvider` should:

1. Read task acceptance criteria and project test configuration.
2. Ensure test application/data/auth fixtures are prepared.
3. Launch `IProbeRunner` with task workspace paths and resource budgets.
4. Persist structured results and artifact pointers to existing factory PostgreSQL/artifact storage.
5. Translate Probe outcome to current factory states.
6. Return **compact failure evidence** to the existing coding agent, not entire traces in chat context.
7. Preserve the current human verification UI for escalation, visual/subjective review, or policy-driven approval.

Proposed factory config (`litos.factory.json`, names provisional):

```json
{
  "verification": {
    "mode": "AutomatedWithHumanFallback",
    "provider": "Litos.Probe",
    "maxRepairAttempts": 2,
    "requireVerifiedCoverage": true,
    "autoCompleteOnPass": true,
    "requireHumanApprovalForRelease": true,
    "probe": {
      "browser": "chromium",
      "headless": true,
      "navigationTimeoutSeconds": 30,
      "scenarioTimeoutSeconds": 180,
      "maxAiDecisionsPerScenario": 20,
      "maxAiTokensPerScenario": 12000,
      "cacheMode": "ReadWriteTaskLocal"
    }
  }
}
```

Values are **initial tuning defaults**, not recommended universal limits. Map onto existing factory configuration conventions rather than duplicating them.

### Result envelope (proposed)

```json
{
  "taskId": "task-101",
  "outcome": "FailedApplication",
  "coverage": { "required": 3, "verified": 2, "missing": ["REQ-3"] },
  "scenarios": { "passed": 2, "failed": 1, "skipped": 0 },
  "failure": {
    "scenarioId": "create-valid-customer",
    "assertion": "dbRecordExists",
    "expected": "Customer persisted",
    "actual": "No matching customer row"
  },
  "usage": {
    "aiCalls": 2,
    "aiInputTokens": 1900,
    "aiOutputTokens": 300,
    "replayHits": 4,
    "replayFallbacks": 1
  },
  "artifacts": {
    "report": "artifacts/probe/report.json",
    "trace": "artifacts/probe/trace.zip"
  }
}
```

Litos should preserve this in its task history so the human-review screen can show **what was tested, what wasn't tested, and why the gate decided as it did**.

## 11. Model strategy and Jev

V1: **reuse the existing Litos LLM provider abstraction** for browser-navigation decisions. Expose an adapter to `IBrowserDecisionModel`; do not build another general agent runtime inside Probe.

Model responsibilities:

- Pick one valid action from the allowed set based on compact page state.
- Recover from an unexpected UI state when deterministic replay fails.
- Optionally suggest scenario steps; success criteria must remain grounded in the task's manifest.

Jev or another low-cost decision model is an **optional experiment**, not a hard dependency. Its actual compatibility with browser observations/tool schemas must be tested. Early candidate tasks: classify ambiguous failure reports, prioritize tests, or select from a small action list. A vision model is needed separately if visual interpretation is required. Do not let model confidence override a failed deterministic assertion.

Track **total cost per correctly verified feature**, rather than only cost per model call.

## 12. Evidence, security and observability

On failure collect: current URL, concise DOM observation, last successful action, failed action/locator, assertion expected-vs-actual, recent console exceptions, relevant failing network requests, screenshot, Playwright trace, API/DB assertion results, and model/replay usage. Redact cookies, tokens, passwords, sensitive form values, and real production data.

Key counters per task and project:

- Mandatory acceptance criteria verified / total; inconclusive criteria.
- Scenario pass/fail/skip and retry counts.
- AI calls, input/output tokens and latency.
- Replay hit/miss/handoff rates; stale recordings.
- False positives/negatives found in manual audits.
- Median time to verified task; repair attempts and root causes.

Run browser actions on **isolated test environments only**. Configure origin allowlists, timeout/step budgets, filesystem isolation, least-privilege credentials, and restricted tool sets. Disable arbitrary URL browsing, external purchases, outbound emails, or destructive production actions by default. Treat application web content as untrusted and never let instructions appearing in a page override Probe's test goal or tool permissions.

## 13. One-week MVP implementation plan

This is an **aggressive proof-of-concept plan**, assuming existing Litos agent/tool orchestration is reusable. It is not a promise of parity with TesterArmy e2e or production-hardening in seven days.

| Day | Deliverable | Exit check |
|---|---|---|
| **1** | Core contracts + Playwright Chromium lifecycle, locators, tracing | Deterministic happy-path UI test passes |
| **2** | Compact browser observation + Litos model adapter + allowlisted action execution | AI discovers and fills an unfamiliar form |
| **3** | Requirement-mapped scenario specs + deterministic UI/API/DB assertions | Missing coverage blocks auto-pass |
| **4** | Verified action recording and parameterized replay | Second run completes known flow with zero navigation model calls |
| **5** | Replay failure handoff, classification, timeouts, reports | Changed locator safely falls back; incorrect postcondition still fails |
| **6** | Factory verification-provider integration, artifacts, repair loop | Confirmed app failure returns to coder; retest passes |
| **7** | End-to-end demo and reliability hardening | Run representative Blazor, WebForms/DevExpress, and authenticated flows |

If time is constrained, prioritize **correct assertions, a reliable browser runner, and factory integration** over sophisticated caching. Cache can be added without changing the public verification contract.

### V1 acceptance criteria / definition of done

- [ ] A real Chromium session starts, navigates, and interacts with the demo app.
- [ ] At least one new form workflow can be discovered using the existing Litos model and then executed.
- [ ] A successful, verified AI action sequence is saved and replayed without additional navigation model calls.
- [ ] Changing a locator or postcondition invalidates replay or triggers safe fallback; it never silently passes.
- [ ] Explicit invalid-input tests reject bad data; a valid-input test checks persistence, not just a toast.
- [ ] A test suite with zero scenarios or uncovered mandatory requirements cannot return `Passed`.
- [ ] Existing factory verification state consumes Probe results without breaking HumanOnly mode.
- [ ] A confirmed app defect invokes at most two bounded repair loops and reruns required regression tests.
- [ ] An infrastructure issue or ambiguous result does **not** trigger uninformed code modification.
- [ ] Authentication and seeded test data work in an isolated environment.
- [ ] Artifacts include structured report, screenshot on failure, trace, and token/replay metrics.
- [ ] Secrets are never stored in replay files, prompts, logs, or committed fixtures.
- [ ] Basic automated unit/integration tests pass on Windows 11; document Linux runner requirements.

## 14. Comparison experiment against TesterArmy e2e

Building Probe is strategically reasonable because Litos already has an LLM/tool-calling and factory layer. Still, benchmark rather than assume parity. Run **identical acceptance criteria and fixtures** through Probe and TesterArmy e2e on: (1) basic Blazor CRUD, (2) legacy WebForms + DevExpress grid, and (3) authenticated ERP transaction.

Compare autonomous completion rate, assertion accuracy, false-pass rate, first-run AI tokens, replay AI tokens, execution latency, stale-cache recovery, and maintenance burden. Keep the decision reversible: Probe implements `IVerificationProvider`, so a TesterArmy-backed provider could be added later if the native implementation underperforms.

## 15. Delivery priorities beyond MVP

**Phase 2:** richer replay fingerprinting and strict cache mode; resilient auth fixtures; test prioritization; reusable WebForms/DevExpress helper library; better regression selection; model-cost routing (including Jev experiment); artifact retention policy.

**Phase 3:** visual assertions, multimodal navigation fallback, broader exploratory testing, alternative browsers, richer project-specific workflow memory, and optional standalone Probe CLI.

Avoid expanding into a parallel factory planner, coding agent, or workflow engine.

## 16. Directions to the implementing Litos coding agent

1. Inspect the current Litos Software Factory repository first. Identify the existing verification stop, task-state machine, model/tool interfaces, task workspace isolation, artifact persistence, and repair loop.
2. Write a short integration map before making code changes. Reuse existing abstractions; only add new types where genuinely needed.
3. Implement a **thin** `Litos.Probe` core with Playwright .NET and deterministic tests first; then add AI navigation and recording.
4. Never use AI as the only oracle for clear functional requirements. Coverage and assertions govern auto-pass.
5. Keep all scenarios and recorded actions reviewable, parameterized, and safe to execute in isolated environments.
6. Provide at least one runnable sample app, example scenario spec, sample output report, and instructions for browser/runtime installation.
7. Add unit tests for cache validation, coverage gating, failure classification, and verification-state transitions.
8. Make the factory's original HumanOnly flow work unchanged after the integration.
9. Report unresolved limitations explicitly; do not silently mark an unimplemented capability as complete.

## 17. References

- Playwright for .NET: https://playwright.dev/dotnet/
- Playwright .NET locators: https://playwright.dev/dotnet/docs/locators
- Playwright .NET tracing: https://playwright.dev/dotnet/docs/api/class-tracing
- Playwright .NET authentication: https://playwright.dev/dotnet/docs/auth
- TesterArmy e2e (conceptual reference, not a dependency): https://github.com/tester-army/e2e
- TesterArmy verified replay design: https://github.com/tester-army/e2e/blob/main/docs/cache.mdx
- TesterArmy introduction (October 1, 2026): https://tester.army/blog/introducing-e2e
- Litos: https://litosai.dev/

---

**Bottom line:** Keep Litos Software Factory's existing verification gate, replace its mandatory human dependency with `Litos.Probe` automated browser verification, and use **AI discovery + deterministic assertions + verified replay + bounded repair** as the entire V1 design center.
