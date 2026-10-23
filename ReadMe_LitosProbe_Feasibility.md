# Litos.Probe — Feasibility Report

**Subject:** [README_Litos.Probe.md](README_Litos.Probe.md) checked against the Software Factory as built on branch `litos-software-factory-v1` (commit 15a9b7b)
**Date:** 2026-10-22
**Verdict:** **Feasible. The design center is sound, but parts of the plan rest on wrong assumptions about the factory.**
- Playwright itself is not the hard part.
- Most of the effort goes into capabilities the factory deliberately left out of V1: starting the application under test, provisioning test data, per-project secrets, structured acceptance criteria, and a new run step with artifacts and UI.
- The one-week plan covers about the first quarter of an honest V1. A realistic estimate is **4–5 weeks**, delivered in three phases (§6). Phase A alone gives most of the value.

---

## 1. What the README assumes, and what the code actually has

| README assumption | Reality in the code | Impact |
|---|---|---|
| A "human functional-verification stop" exists as a stage with an extension point (`IVerificationProvider`) | The human stop is a **lifecycle state**, `AwaitingHumanTesting`. It is entered by `LifecycleTrigger.Handoff` *after* commit/push/PR ([TaskLifecycle.cs:115](src/Litos.SoftwareFactory.Core/Lifecycle/TaskLifecycle.cs#L115)). There is no provider interface. `IVerifier` exists ([Ports.cs:169](src/Litos.SoftwareFactory.Core/Ports/Ports.cs#L169)), but it is a single singleton typed to build/unit-test/coverage. The run steps are a hard-coded `switch` ([RunExecutor.cs:103-110](src/Litos.SoftwareFactory.Host/Runs/RunExecutor.cs#L103-L110)). | A new `ProbeStep` in the run step machine is required. Adding a stage is cheap by design (stage and lifecycle are separate fields), but it touches `RunModel`, `RunOrchestrator`, `RunExecutor`, the store and the Web UI. |
| "Only the gate's decision logic changes" | `Accept` is reachable **only** by a user action from `AwaitingHumanTesting` ([TaskLifecycle.cs:126](src/Litos.SoftwareFactory.Core/Lifecycle/TaskLifecycle.cs#L126), `ApplyUserActionAsync`). Accepting also queues lesson reflection and writes an audit event. | Automatic completion needs a new system trigger, a policy, and an audit identity. This is the most sensitive change in the proposal (see Q1). |
| Factory "starts the isolated test application and seeds test data" | The V1 blueprint explicitly **defers** "Automated UI, browser… testing" and "Application startup" ([ReadMe_LitosSoftwareFactory_V1.md §2](ReadMe_LitosSoftwareFactory_V1.md)). `ProcessRunner` is run-to-completion only. There is no background process, no port allocation and no readiness poll. Nothing provisions a database. | This is entirely new work, and the largest single piece. |
| Acceptance criteria with IDs (`REQ-1`) drive a coverage gate | Specs are **optional**, and `Specification.AcceptanceCriteriaJson` is a JSON array of **plain strings** with no IDs ([Submissions.cs:59](src/Litos.SoftwareFactory.Contracts/Submissions.cs#L59)). `submit_work` maps criteria back by normalised text. | Requirement-coverage gating needs a schema change (string → object with ID) and a migration that tolerates old specs. Tasks without a spec have nothing to gate on. |
| Per-project test configuration (`litos.factory.json`) | Project config lives in the DB (`Project.VerificationProfileJson`, version 2). Config files in the repository are deliberately untrusted. There are no per-project settings beyond the profile. | Probe config belongs in the verification profile, as profile version 3, admin-approved like the rest of the profile. It should not be a repository file. |
| Credentials come from "the factory's secret store" | `factory_secrets` exists (Data Protection), but names are allowlisted to `github`, `provider:*`, `websearch:*` and `mcp:*:*` ([SecretNames.cs](src/Litos.SoftwareFactory.Core/Settings/SecretNames.cs)). Nothing is per project. `ToolchainEnvironment` strips any `*TOKEN*`/`*PASSWORD*` variable from child processes. | A new `project:<id>:<NAME>` secret namespace is needed. Secrets must reach the app and Probe, but **never** the worker or the agent shell. |
| Artifacts go to "existing artifact storage" | There is no run-artifact table and no endpoint that serves run logs. The only file-store precedent is `thread_attachments` (bytes on disk under `<data>/attachments`, metadata in the DB, served with `nosniff` and a sandbox CSP). | A new table, a migration, a serving endpoint, Details-panel UI and backup-manifest coverage are needed. |
| Reuse the Litos model abstraction for `DecideAsync` | Feasible. `IChatProvider.StreamAsync` with a one-shot `ChatRequest` follows the established pattern (`Compactor`, `Reflector`). But: **no `tool_choice` / structured output** exists, so the model can reply with text. Budget enforcement lives only in the host's `ModelGateway`, which is scoped to a run. | Probe calls must go through the gateway, or they bypass the task budget. See the gotchas in §4. |
| Jev as a cheap decision model | Not implemented in `src`. It exists only as a design ([ReadMe_PTC_JEV.md](ReadMe_PTC_JEV.md)), whose own doc says not to make it an `IChatProvider`. | Drop it from V1 scope, as the README already allows. |
| Proposed type names `VerificationOutcome`, `IVerificationProvider` | `Litos.SoftwareFactory.Core.Verification.VerificationOutcome` already exists, as does an unused `HumanTestingStatus`. | Rename Probe's types, e.g. `ProbeOutcome`. |

What *does* line up well:
- **The repair loop.** The repair loop (Verify fails → Repair turn with a bounded brief → Verify, capped by `MaxRepairCycles = 2`, blocks on "repair made no difference") is exactly the shape the README wants. Probe failures can feed the same `BriefComposer.Repair` path.
- **Handoff evidence.** `HandoffEvidence` already carries `Criteria` and `ManualTestSteps`. That is where "what was tested, what wasn't" naturally goes.
- **Isolation.** The per-project lease already serializes work on a repository. Probe can safely run inside the leased working copy, with no new isolation layer.
- **The scratch folder.** `.factory/` is already git-excluded inside each clone, so it can hold per-run scratch output.
- **Model-call recording.** Phase-labelled usage (`ReserveCommand.Phase`) means a `Probe` phase shows up in the budget panel for free.
- **Browser runtime.** Chromium for Playwright is already installed on this machine (`%LOCALAPPDATA%\ms-playwright\chromium-1234`). `LOCALAPPDATA` and `HOME` pass the toolchain environment allowlist, so the default browser path works without changes.

## 2. Hidden risks the README does not mention

1. **The baseline comparison would silently pass broken features.** The verifier counts a failure only if it *also does not fail on the baseline* (`VerificationOutcome.NewFailures`).
   - A scenario for a brand-new feature always fails on the baseline commit, because the feature isn't there yet.
   - If Probe results were fed through the existing unit-test path (the tempting zero-orchestrator-change route: Probe as a profile command emitting JUnit), a still-broken new feature would be classified *pre-existing* and pass.
   - Probe needs its own outcome type and must not reuse the baseline diff logic.
2. **Gateway "finished turn" short-circuit.** `ModelGateway.HandleAsync` answers any tool-bearing call on a session whose turn has already submitted with a free canned reply ([ModelGateway.cs:85-92](src/Litos.SoftwareFactory.Host/Gateway/ModelGateway.cs#L85-L92)). A Probe decision call on the run's session key would get "you're done" instead of an action. Probe must use its own session key per scenario.
3. **Output reservation.** Each gateway call reserves input plus output, up to `OutputAllowanceTokens` (32,768). Probe decisions must set a small `MaxOutputTokens` (≈ 512). Otherwise a tight budget refuses Probe calls and moves the whole run to `PausedBudget`.
4. **Anti-gaming is harder than the README implies.**
   - The coding agent has write access to the whole working copy, so it can edit `.litos/probe/scenarios/*.json` as easily as source code.
   - The factory already solves this for the verification profile (stored in the DB, admin-approved) and for repository skills (an approval queue).
   - Scenarios must follow one of those two models. Probe should read the **approved** revision, not the working-copy file, and flag any diff to scenario files in the handoff.
5. **Port collisions.** `FACTORY_VERIFY_CONCURRENCY` defaults to 1 *because* fixed-port tests collide. The app under test must bind a factory-assigned free port, and run under the same `VerificationGate`.
6. **Probe runs untrusted code with a browser, on the host.** This is acceptable under §17's trusted-user rule, but it widens the surface: the app under test now runs as a long-lived process on the factory machine, and it may have network access. The origin allowlist and Playwright route blocking must be on by default.
7. **The repair-cycle budget is shared.** Two cycles already cover test failures *and* blocking review findings. Adding Probe failures to the same pool makes "blocked: repair cycles exhausted" more common. Decide whether Probe gets its own allowance.
8. **Linux.** `playwright install --with-deps` needs root for system libraries. That conflicts with the per-user install model planned for mac/Linux. Document it for V1; it is not a blocker on Windows.
9. **Target-app reality.** Each stack has its own concrete hurdle:
   - DevExpress/WebForms grids need project-specific helpers. The README is right not to generalise them.
   - Blazor needs explicit readiness markers.
   - None of the factory's current evaluation targets is Blazor or WebForms. `insta-story-generator` (React/Express) is the only web target. `filedb-sharp` is a library with no UI.

## 3. Where Probe should run (recommendation)

**Out of process: a `Litos.Probe.Cli` executable launched by the host for a new `ProbeStep`, with model calls routed through the run's gateway the same way the worker routes them.**

- **Isolation.** Playwright .NET already spawns a Node driver process. Keeping it out of the factory host keeps a browser crash or hang from taking down the host, and keeps Playwright out of the host's dependencies. The host already kills process trees on timeout (`ProcessRunner`).
- **Budget and keys.** The CLI is given the gateway URL and the per-launch secret, exactly like the worker, and uses a `GatewayChatProvider`. It holds no keys, and every decision call is charged to the task under phase `Probe`. `GatewayChatProvider` would move from Worker into a shared assembly (or the CLI references Worker).
- **Standalone use.** The same CLI runs outside the factory (`probe run --base-url …`) with a direct `IChatProvider` for diagnostics, as the README wants.
- **Application lifecycle.** The host owns app startup and shutdown (new `IManagedProcess`: start, readiness poll, kill tree), so failures can be classified as `EnvironmentFailure` without a browser involved.

Rejected alternatives:
- **Probe as a profile command emitting JUnit.** Rejected because of the baseline trap (§2.1), because it gives no coverage gate, and because it produces no structured evidence.
- **A Playwright MCP server for the coding agent.** Useful later for the agent's *own* fast feedback, but it makes the model the oracle. That is the opposite of the README's core rule.

## 4. Change inventory

**New projects** (following the README's layout, trimmed):
- `Litos.Probe.Core`: scenario/run/result contracts, coverage gate, failure classifier, outcome mapping. No browser or LLM dependency.
- `Litos.Probe.Browser`: Playwright lifecycle, a compact observation built from the accessibility tree, an allowlisted action executor, tracing and screenshots.
- `Litos.Probe.Agent`: an `IBrowserDecisionModel` over `IChatProvider`, one call per action, with a text-only reply treated as `ModelFailure`.
- `Litos.Probe.Replay`: schema-validated recordings, promote-only-after-verified, quarantine. Phase B.
- `Litos.Probe.Cli`.
- Tests: `tests/Litos.Probe.Tests`, plus `tests/Litos.Probe.TestWebApp`, a tiny ASP.NET app started from `AppContext.BaseDirectory` the way `TestMcpServer` is. Chromium-dependent tests use `[SkippableFact]`.
- `Fixtures` is folded into Core/Cli for V1: seed and reset are profile-declared commands, not a framework.

**Factory changes:**
- **Core**
  - `ProbeStep` / `ProbeCompleted` in `RunModel`.
  - `RunOrchestrator`: Verify passes → Probe → (Repair on `ApplicationDefect`) → Review → Handoff.
  - `ProbeStatus` on `RunState`.
  - Repair brief section for Probe failures.
  - `HandoffEvidence.Probe`.
  - Handoff text no longer hard-codes "Application testing is yours."
- **Profile v3**: an optional `application` block (start command, working dir, port variable `{port}`, readiness path, timeouts, seed/reset commands) and a `probe` block (scenario revision, budgets, auth mode). Validator and presets updated.
- **Spec**: criteria become `{id, text, verification: probe|unit|manual}` with backward-compatible reading of string arrays. `submit_spec` / `submit_work` are updated to match.
- **Scenarios**: stored and approved like the profile (DB, revisioned, Admin/Member approval). Probe reads the approved revision only.
- **Infrastructure**:
  - `ManagedProcess` (background start, readiness, kill tree).
  - `ProbeRunner` adapter launching the CLI.
  - EF migration for `probe_runs`, `run_artifacts`, `probe_scenarios` and the spec criteria shape.
  - Backup manifest coverage.
- **Secrets**: a `project:<id>:<NAME>` namespace, injected only into the app and Probe environments.
- **Host**: an artifact download endpoint with project-access checks (copy `AttachmentsApi`'s headers), and a Probe phase in the gateway's usage labels.
- **Web**:
  - Details panel: criteria coverage table, scenario results, screenshot viewer, trace download.
  - Project settings: the application and Probe sections.
  - Scenario review UI.
- **Installer**: `doctor` check for Chromium; an optional `litos-factory probe install-browsers`.

**Unchanged:** `AgentLoop`, providers, the HumanOnly path (a profile without an `application` block behaves exactly as today).

## 5. Feasibility per README V1 acceptance criterion

| Criterion | Feasible? | Notes |
|---|---|---|
| Real Chromium session interacts with demo app | Yes, low risk | Playwright .NET, browsers already present |
| AI discovers a new form workflow via the Litos model | Yes, medium risk | No forced tool choice, so retry/abort on text replies; success depends on the model and the observation quality |
| Verified sequence replays with zero navigation calls | Yes | Straightforward once recordings are keyed and validated |
| Changed locator/postcondition never silently passes | Yes | Assertions always rerun after replay; this is a design rule, not a hard problem |
| Invalid-input tests reject; valid-input checks persistence | Partly | UI side yes. **DB assertions need a per-project read-only query registry and a test DB the factory does not provision today.** Phase A uses `httpResponseMatches` against the app's own API; DB assertions come later. |
| Zero scenarios / uncovered requirements cannot pass | Yes | Requires the structured-criteria schema change |
| HumanOnly mode unbroken | Yes | Probe is opt-in per profile |
| ≤ 2 bounded repair loops with regression rerun | Yes | Reuses the existing loop; decide whether the allowance is shared |
| Infra/ambiguous issues don't trigger code edits | Yes | Classifier maps them to `Blocked` (environment) or the human fallback, never to Repair |
| Auth + seeded data in isolated env | Partly | FormLogin + new secrets: yes. Seeding: only via project-supplied commands. Isolation is per-project (lease), not per-task DB. |
| Artifacts: report, screenshot, trace, metrics | Yes | New table, endpoint and UI |
| No secrets in replays, prompts, logs, fixtures | Yes, needs care | Parameter-by-name in recordings; redact fill values in observations and logs; tests to pin it |
| Tests pass on Windows 11; Linux documented | Yes | |

## 6. Recommended phasing and effort

Estimates are for one developer, including comprehensive unit tests at each step (per the factory's standing rule). They are rough.

| Phase | Scope | Effort | Outcome |
|---|---|---|---|
| **A — Deterministic gate, evidence only** | Probe Core/Browser/Cli; deterministic steps and assertions (visible, text, URL, state, HTTP); app lifecycle; profile v3; structured criteria; scenarios approved in the DB; `ProbeStep` + repair on defects; artifacts + Details UI; classifier. **The task still ends at `AwaitingHumanTesting`**, with the handoff showing exactly which criteria Probe verified. | ~2–2.5 weeks | The human tester checks only what Probe could not, so most of the value comes at the lowest risk |
| **B — AI navigation + replay** | `act` steps through the gateway; compact observation; action allowlist; recordings with promote-after-verify, quarantine and fallback; usage metrics | ~1–1.5 weeks | Unfamiliar flows without hand-written locators; low-token regressions |
| **C — Policy-driven auto-completion** | A system `AutoAccept` trigger (or an "auto-verified" state) gated on full mandatory coverage and the project policy; auditing; tray/alerts wording; DB assertion registry; WebForms/DevExpress helpers driven by a real target app | ~1 week + target-app work | Removes the human stop where policy allows |

Total: **~4–5 weeks** to the README's definition of done. The README's day 1–3 deliverables correspond roughly to the Probe-library part of Phase A, without the factory work.

## 7. Open questions that change the design

1. **Auto-accept.** Should a fully covered Probe pass move a task to `Accepted` automatically (Phase C), or should V1 stop at "verified evidence in the handoff, human still accepts"? Auto-accept also triggers lesson reflection and changes what "Accepted" means in metrics. *Recommendation:* evidence-only first.
2. **Who writes scenarios?**
   - Option a: the spec turn drafts them from the acceptance criteria and a person approves them.
   - Option b: humans hand-write them in the repository and an Admin approves each revision.
   - Option c: both.
   - *Recommendation:* (a), with approval, because it keeps tests traceable to requirements rather than to the implementation.
3. **Tasks without a spec.** Specs are optional today. Should Probe require an approved spec with criteria IDs (no spec → HumanOnly), or run project-level smoke scenarios only?
4. **Test data.** Is it acceptable that the factory only *runs* project-declared seed/reset commands (the project owns its test DB), rather than provisioning databases itself? Are DB assertions needed in V1, or are API/HTTP assertions enough?
5. **Repair allowance.** Should Probe failures share the existing 2 repair cycles, or get their own?
6. **Demo/target apps.** Which real Blazor, WebForms/DevExpress and authenticated apps should Phase A/C be proven on? None of the current evaluation targets covers them.
7. **Process placement.** Do you agree with the out-of-process `Litos.Probe.Cli` using the run gateway (§3), over an in-host library?
