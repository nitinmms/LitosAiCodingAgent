# Litos.Probe.Web

**Deterministic browser verification of web apps for the Litos Software Factory. Probe's first part.**

**Status:** W1 (engine, CLI, test apps) built 2026-11-17; W2–W4 not started. See §11.1.
**Date:** 2026-11-17
**Target:** .NET 10, C#, Playwright for .NET, Chromium
**Design source:** [docs/software-factory/testability-architecture.md](docs/software-factory/testability-architecture.md) (revision 2)
**Part of:** [Litos.Probe](README_Litos.Probe.md). Its decisions in [ReadMe_LitosProbe_Feasibility.md](ReadMe_LitosProbe_Feasibility.md) §8 still apply.

---

## 1. What it is

Litos.Probe.Web makes a web app **testable** by the factory, then **checks it in a real browser before handoff**, with screenshots, traces and server logs as evidence.

It is the part of Litos.Probe that needs no AI:
- start the app, whatever its stack;
- reset its data;
- sign in as each role;
- visit every route as the right role;
- check access control;
- watch for errors;
- run approved, fixed-step scenarios;
- report with evidence.

It is built first, on its own, because:
- **It delivers the visible value early.** A person opens the handoff and sees the app already started, signed into and checked, with a screenshot of every page as every role.
- **It spends no model tokens.** There are no gateway changes and no budget-pause paths, so no Probe §8.2 work is needed yet.
- **Full Probe needs it anyway.** Probe's AI navigation, replay and coverage gate all assume an app that can be started, reset and signed into. This is that foundation.
- **It can be proved stack by stack** with a command-line tool, before any factory code changes.

### What it does not do (it stays in full Probe)

- AI-driven steps (`act`), `IBrowserDecisionModel`, model calls of any kind
- Recording and replay of discovered action sequences
- Database assertions through named read-only queries
- The requirement-coverage gate over structured acceptance criteria (`AC-1` IDs)
- Auto-accept (Probe decided against it for V1 anyway)

## 2. Where it sits

```
Litos.SoftwareFactory (host)                          Litos.Probe.Web (library, in-host)
 ├─ Testability check      ── calls ──►  StackDetector, DryRun
 ├─ Make-testable task     ── calls ──►  Gate (via check_testability host tool)
 ├─ Adopt contract         ── calls ──►  ContractYaml → WebAppContract, Gate on main
 ├─ Run: Verify ─► UiTestStep ─calls─►  AppEnvironment + Smoke + Scenarios
 ├─ Handoff evidence       ◄── report ── ProbeWebReport + artifacts
 └─ Project secrets        ── supplies ─► ISecretResolver
                                         ISecretResolver, IArtifactSink, IProcessLauncher:
                                         interfaces the host implements
Litos.Probe.Web.Cli  ── the same library, with env-var secrets and a folder sink,
                        for trying a repo outside the factory
```

**Placement:** a library inside the factory host, as Probe decided (§8.1 Q7). The CLI is a development and diagnostics tool only. The factory never shells out to it.

**Containment of the in-host browser:**
- Playwright's Node driver is a child process.
- Every Probe.Web operation has a hard timeout and its own cancellation token.
- A crash or hang kills that run's browser, app processes and containers. The host itself keeps running.

## 3. Projects

```
src/
  Litos.Probe.Web.Core/        contract model + validation, stack-profile model, check/report/result types,
                               classifier, baseline comparison. No browser, no processes, no I/O beyond files.
  Litos.Probe.Web/             runtime: StackDetector, AppEnvironment (build, services, processes, ports,
                               reset, tree-kill), Browser (sign-in, visits, signals, screenshots, traces,
                               accessibility lint), Gate, Smoke, Scenarios, ContractYaml, embedded stack profiles.
  Litos.Probe.Web.Cli/         `litos-probe-web` (detect · dry-run · gate · smoke).
tests/
  Litos.Probe.Web.Tests/              unit tests, no browser (contract, profiles, placeholders, classifier, yaml)
  Litos.Probe.Web.IntegrationTests/   real Chromium and real processes ([SkippableFact] when Chromium is missing)
  Litos.Probe.Web.TestApps/
    AspNetBlazor/              Blazor Server, form login, roles admin/staff/patient, /__test in Testing only
    NodeExpressSpa/            Express serving a built SPA, PORT, SQLite file, no login
    BrokenApps/                one per failure: wrong port, HTTPS redirect, 500 on /, no labels, …
```

**Dependencies:**
- `Microsoft.Playwright` (Apache-2.0);
- `YamlDotNet` (MIT), for `litos.test.yaml`;
- the Docker CLI, through processes, for `services`. There is no Docker SDK dependency.

`Litos.SoftwareFactory.Core` references `Litos.Probe.Web.Core`, so profile v3 can embed `WebAppContract`. Nothing in Probe.Web references the factory.

## 4. The contract

The contract says how to build, start, reset, sign into and walk the app. Its full shape and rules are in testability-architecture.md §5. In short:

- **Required:**
  - `processes` (one or more) and `baseUrl`;
  - a reset (`command`, `endpoint`, `sqlite-snapshot`, a `services` container plus a command, or `none`, declared for an app that keeps no data);
  - one user per role, if the app has a login.
- **Defaulted by the stack profile:** build and serve commands, port injection, readiness, error signals and log error patterns.
- **Optional:** `/health`, a readiness marker (required only for websocket-driven UIs such as Blazor Server), `/__test/*` endpoints, the clock, and `routes.denied`.
- **Placeholders**, resolved only by Probe.Web at start:

  | Placeholder | Resolves to |
  |---|---|
  | `{port:<process>}` | a free port allocated for this run |
  | `{service:<name>:port}`, `{service:<name>:<VAR>}` | a service container's mapped port or one of its values |
  | `{generated}` | a value made per run |
  | `{secret:NAME}` | a value from `ISecretResolver` |

Two forms carry the same model:
- **`litos.test.yaml`** is human-friendly. It is read by the CLI, and by the factory only when an Admin adopts it from the default branch.
- **`WebAppContract` JSON** is embedded in the factory's verification profile v3. During a task, the factory never reads the yaml file.

The CLI trusts whatever file you give it. Trust is the factory's job: Admin approval, a DB copy, and drift flags.

## 5. Stack profiles

There is one JSON file per framework, embedded in the assembly and overridable by the host (the factory lets Admins edit them). The engine has no stack-specific code. A profile holds:
- `detect` markers;
- production-like `build` and `processes` defaults;
- `portTemplate`;
- the `testEnvironment` and `productionEnvironment` switches;
- `readiness` (`load` or `needsMarker`);
- `errorSignals` and `logErrorPattern`;
- `quirks`;
- `retrofitSkill`.

| Tier | Profiles |
|---|---|
| 1: built with a test app, live-checked | `node-express-spa`, `aspnet-blazor-server` |
| 2: shipped as data, not yet proved | `node-vite-spa`, `node-next`, `aspnet-mvc-razor`, `python-django`, `python-fastapi`, `jvm-spring-boot`, `go-http` |
| 3: generic | `generic-http`: universal signals only, commands given by hand |
| Not yet | Rails, Laravel, Phoenix (no factory toolchain), `docker compose`-only apps, SSO-only login |

The per-profile facts (serve commands, port flags, Windows traps such as gunicorn and `--no-launch-profile`) are in testability-architecture.md §6.6.

## 6. What it checks

### 6.1 Gate: "is this app testable?" (8 steps)

| # | Step | Fails if |
|---|---|---|
| 1 | Build, start services and processes, wait for each one's readiness | Timeout, wrong port (it reports which port it bound instead), HTTPS redirect, `GET /` ≥ 500 |
| 2 | Reset data | Non-zero exit, endpoint not 2xx, snapshot copy failed |
| 3 | Sign in as each role and save the storage state | `successWhen` not reached, or a login field has no accessible label |
| 4 | Visit every declared route as its role, wait for page readiness | Marker missing, an error signal, a 5xx, or a server error line |
| 5 | A protected route while logged out | Not redirected to login |
| 6 | Each `denied` route as its role | Not 401/403 and not redirected away |
| 7 | `/__test/*` with the stack's Production switch | It succeeds, or answers differently from a path that cannot exist (Blazor answers any unknown POST with 400, so "anything but 404" would be wrong) |
| 8 | Accessibility lint on every visited page | An interactive element with no accessible name and no `data-testid` |

Where each part runs:
- **The factory** runs all 8 steps in Make-testable tasks and when a contract is adopted. Per task, it runs steps 1–6 as **smoke**.
- **The CLI** runs whichever steps you ask for.

### 6.2 Scenarios: "does this change work?" (fixed steps, no AI)

These are approved scenarios using the fixed half of Probe's action set:
- **actions:** `Navigate` (allowlisted origins only), `Click`, `Fill` (by parameter, or by `{secret:}`), `Select`, `Check`/`Uncheck`, `WaitFor`, `Screenshot`;
- **assertions:** visible, hidden, text, URL, element state, expected error, and HTTP response;
- **locators:** roles, labels and `data-testid` first; CSS selectors as a fallback; never coordinates.

Each scenario has a `role`, a `criterion` (the text of the spec's acceptance criterion, matched the way `submit_work` matches today; structured IDs come with full Probe) and a revision. The factory stores scenarios in its DB and runs **only approved revisions**. The spec turn can draft them, people can write them, and each revision is approved before use (Probe §8.1).

A step whose locator finds nothing is a **TestDefect**. Probe.Web never guesses another element, so the scenario goes to a person.

### 6.3 Signals watched on every page and every step

- **Universal:**
  - an uncaught `pageerror`;
  - HTTP ≥ 500 on an app-origin document, fetch or XHR;
  - failed app-origin requests;
  - `console.error` (configurable);
  - stderr or `logErrorPattern` lines from any process during the step.
- **Stack:** the profile's `errorSignals` (`#blazor-error-ui`, Django `Traceback`, "Whitelabel Error Page", …) plus the contract's `failOn`.
- **Readiness:** never "network idle", because SignalR, LiveView and ActionCable hold sockets open forever. It is either `load`, or the profile's marker or `js` probe.

## 7. Results and evidence

```jsonc
{
  "kind": "smoke",                       // gate | smoke | scenarios | dry-run
  "outcome": "Failed",                   // Passed | Failed | EnvironmentFailure | Inconclusive
  "commit": "4e1b9c2",
  "contract": { "source": "profile", "revision": 4, "stack": "aspnet-blazor-server" },
  "app": { "baseUrl": "http://localhost:51873", "startSeconds": 3.8, "processes": [{ "name": "web", "port": 51873 }] },
  "checks": [
    { "id": "visit:patient:/my/appointments", "kind": "RouteVisit", "role": "patient", "route": "/my/appointments",
      "status": "Failed", "classification": "ApplicationDefect",
      "message": "console.error: Unhandled exception rendering component",
      "evidence": { "screenshots": ["a1f0…"], "trace": "9c2e…", "consoleErrors": 1,
                    "log": ["fail: Microsoft.AspNetCore.Components… NullReferenceException"] },
      "durationMs": 640 },
    { "id": "denied:patient:/admin/users", "kind": "Denied", "status": "Passed", "evidence": { "screenshots": ["77b3…"] } }
  ],
  "lint": { "pages": 5, "interactive": 41, "unnamed": 0 },
  "usage": { "modelCalls": 0, "wallSeconds": 58 }
}
```

- **Check kinds:** `Build`, `Start`, `Reset`, `SignIn`, `RouteVisit`, `LoggedOutRedirect`, `Denied`, `ProductionTestEndpoints`, `SelectorLint`, `Scenario`.
- **Classification:** a subset of Probe §9 plus one new class:
  - **`ApplicationDefect`:** the factory starts a repair turn (shared 2 cycles).
  - **`TestDefect`:** a scenario or contract entry is wrong; it goes to a person, with no code change.
  - **`EnvironmentFailure`:** Blocked, with diagnostics and no repair.
  - **`AuthenticationFailure`:** the test credentials are wrong or a secret is missing; it goes to a person.
  - **`Flaky`:** passed on its one automatic rerun in a fresh context; reported, not repaired.
- **Baseline comparison:** `SmokeComparison.Compare(current, baselineOnMain)` is a pure function in Core. A check that also fails on main is **pre-existing**: disclosed, not blamed. Scenarios for new behaviour are never baseline-compared (the trap in Probe feasibility §2.1).
- **Artifacts** go to `IArtifactSink`. The factory sink writes `run_artifacts` and serves files with project-access checks. The CLI sink writes `./.probe-web/<timestamp>/`.
  - **Screenshots:** PNG, one per visit or step, full page.
  - **Trace:** one Playwright trace per role or scenario, with tracing **off during sign-in**.
  - **Logs:** the stdout and stderr tail of each process.

## 8. Public API (sketch)

```csharp
// Litos.Probe.Web.Core
public sealed record WebAppContract(AppDefinition Application, TestabilityDefinition Testability);
public static class ContractValidator { public static IReadOnlyList<string> Validate(WebAppContract c, StackProfile p); }
public sealed record StackProfile(string Id, string Toolchain, DetectRules Detect, AppDefaults Defaults, ...);
public sealed record ProbeWebReport(ReportKind Kind, ProbeWebOutcome Outcome, IReadOnlyList<CheckResult> Checks, ...);
public static class SmokeComparison { public static ComparedReport Compare(ProbeWebReport now, ProbeWebReport? baseline); }

// Litos.Probe.Web: the host supplies these
public interface ISecretResolver { string? Resolve(string name); }
public interface IArtifactSink    { Task<string> SaveAsync(ArtifactKind kind, string fileName, Stream content, CancellationToken ct); }

// Litos.Probe.Web: entry points
public interface IStackDetector { DetectionResult Detect(string repoRoot, IEnumerable<StackProfile> profiles); }
public interface IProbeWeb
{
    Task<ProbeWebReport> DryRunAsync(string repoRoot, StackProfile profile, ProbeWebOptions o, CancellationToken ct);
    Task<ProbeWebReport> GateAsync(string workingCopy, WebAppContract c, GateScope scope, ProbeWebOptions o, CancellationToken ct);
    Task<ProbeWebReport> SmokeAsync(string workingCopy, WebAppContract c, ProbeWebOptions o, CancellationToken ct);
    Task<ProbeWebReport> ScenariosAsync(string workingCopy, WebAppContract c, IReadOnlyList<WebScenario> s, ProbeWebOptions o, CancellationToken ct);   // W3: not in W1's interface
}
public sealed record ProbeWebOptions(
    ISecretResolver Secrets, IArtifactSink Artifacts,
    IReadOnlyDictionary<string, string> BaseEnvironment,   // the host's scrubbed toolchain env
    TimeSpan Timeout, bool Headless = true, int FlakyReruns = 1);
```

**Environment:** `BaseEnvironment` is the host's scrubbed toolchain environment. Probe.Web adds the contract's declared variables to it, for the app processes only, **after** the scrub. That path keeps `ConnectionStrings__Default` and friends alive (testability-architecture.md §8).

**One app at a time per working copy:** the factory's per-project lease already serializes runs on a repository. Probe.Web also refuses a second start in the same folder.

## 9. CLI

```
litos-probe-web detect   <repo>                               # which stack profile, candidates in a monorepo
litos-probe-web dry-run  <repo> [--profile node-express-spa]  # try the profile defaults: real gaps
litos-probe-web gate     <repo> [--contract litos.test.yaml] [--steps 1-8]
litos-probe-web smoke    <repo> [--contract …] [--headed]
litos-probe-web scenarios <repo> --scenarios scenarios/*.json                         # W3: not in W1's CLI
```

- **Secrets** come from environment variables with the same names, e.g. `TEST_STAFF_PASS`.
- **Output:** `report.json` plus artifacts under `./.probe-web/`, and a one-screen summary.
- **Exit codes:** 0 passed, 1 failed, 2 environment failure, 3 bad contract.

This is how a new stack profile gets proved: run `dry-run`, then `gate`, on a real repo, before any factory task depends on it.

## 10. Security

| Rule | How |
|---|---|
| Secrets never in artifacts or logs | `{secret:}` is resolved into process env and sign-in fills only. Tracing is off during sign-in. Fill values are redacted in step logs. A test scans every artifact of a run for the secret values. |
| Storage state never leaves the run | It is held in memory or encrypted in the run temp folder, and deleted at stop |
| Browser stays on the app | Navigation and requests are limited to the app's own origins (all `baseUrl` and process ports). Everything else is blocked by Playwright routing, unless the contract allowlists it. |
| Nothing left running | Process trees are killed (a Job Object on Windows), and the port is checked free. Containers carry the label `litos.probe.run=<id>` and are swept at stop and on host start. |
| `/__test/*` absent in Production | Gate step 7 |
| Same trust level as verification | The app runs on the factory host like today's build and test commands (V1 trusted-user rule). A sandbox is required before untrusted users, as for the rest of the factory. |

## 11. Build plan

Each step ends with its tests green (the factory's standing rule: comprehensive unit tests for every new feature and every change).

| Step | Delivers | Exit check | Effort |
|---|---|---|---|
| **W1 Engine** | Core (contract, validator, stack-profile model, placeholders, report, classifier, comparison); `ContractYaml`; `AppEnvironment` (build, processes, ports, reset `command` / `endpoint` / `sqlite-snapshot`, tree-kill, declared env); Browser (sign-in `none` / `form`, visits, signals, screenshots, traces, lint); Gate 1–8; Smoke; CLI; the 2 test apps + broken apps; profiles `node-express-spa`, `aspnet-blazor-server`, `generic-http` | `litos-probe-web gate` passes on both test apps and on **insta-story-generator** with a hand-written contract; each broken app fails with the right message | ~1.5 weeks |
| **W2 Factory: testable + smoke before handoff** | Profile v3 embeds `WebAppContract`; project secrets `project:<id>:NAME`; **`services`** (Docker allowlist: postgres, mysql, mariadb, mssql, mongo, redis, mailpit; free host ports; `{generated}` passwords; orphan sweep by label; "Docker not running" → EnvironmentFailure); **Mailpit** email checks; detect + dry run at registration; Testability panel; Adopt + gate baseline on main; `UiTestStep` (smoke) after Verify; repair on `ApplicationDefect`; `HandoffEvidence.UiTesting`; screenshot gallery; "Run by the factory" on *Try it yourself*; Make-testable task type + `make-testable.md` + `check_testability` host tool + retrofit skills `testability-node`, `testability-dotnet` | Live: register insta-story-generator → check → Make-testable task → PR merged → adopt → a feature task hands off with smoke screenshots. Same on the **new Blazor sample** (3 roles, Postgres from `services`, email through Mailpit). | ~2.5 weeks |
| **W3 Scenarios** | `WebScenario` model and runner (fixed steps); scenario store with revisions and approval; spec turn drafts scenarios from criteria; scenario results in the handoff criteria table | A task's criteria show "Verified in browser" with screenshots; a renamed button gives TestDefect, not a code change | ~1 week |
| **W4 Breadth** (can follow later) | Tier-2 profiles + retrofit skills `testability-python`, `-jvm`, `-go` | `dry-run` and `gate` on one real or sample repo per tier-2 profile; findings folded into the profiles | ~0.5–1 week |

**Total:** about **5 weeks** for W1–W3 (web apps testable, smoke-tested and scenario-checked before handoff on two stacks, with throwaway databases), plus up to 1 week for W4.

### 11.2 W2 progress

- **W2.1** (factory): verification profile v3 embeds the contract; project secrets `project:<id>:NAME` (Admins and Members, set-only); `run_artifacts` and its download endpoint.
- **W2.2** (engine): Docker `services` and Mailpit, below.
- **W2.3** (factory): the testability check (detect, the default branch's `litos.test.yaml`, then a dry run of the stack's defaults under `VerificationGate` when there is no contract) at registration, after a profile change and on *Check again*; stored in `testability_checks`; a *UI testing* column and a Testability panel (gaps, dry-run checks with screenshots, the contract on main, set-only role secrets) open to the project's Members; the factory's leftover service containers swept at host start.
- **W2.4** (factory): *Adopt* (Admins and Members, audited): the default branch's `litos.test.yaml` shown as a line diff against the adopted contract, refused if main moved since the preview, if it does not validate or if its secrets are not set; saved as a new profile revision with `adoptedFrom`. The check then runs the whole Gate (1–8) on the default branch: *Test-ready* or *Broken on main*, kept as the baseline a task's smoke is compared with. A newer contract on main than the adopted one shows as a gap. The handoff's drift note comes with W2.5.
- **W2.5** (factory): `UiTestStep` in the run: Verify → browser smoke (Gate 1–6, compared with the Gate baseline on main) → Review → Handoff, again after a review repair. New application defects start a repair turn in the shared cycles, with the failing checks and server log lines in the brief and "change the application, not the test"; the same failures after a repair block as "repair made no difference". A start failure is the task's when main starts fine, the environment's otherwise (Blocked, `UiTestUnavailable`, resumes at the browser step). Test and credential defects, checks already failing on main and flaky checks are disclosed, not repaired. `HandoffEvidence.UiTesting` carries every check with its screenshots and trace, the pre-existing failures, lint, emails and the commands the factory ran; the card shows a *Browser checks* row, screenshots by role, *Run by the factory on commit …* on *Try it yourself*, and the drift note when the branch changes `litos.test.yaml`.
- **W2.6** (factory): the *Make-testable* task. Task type `testability`, started from the Testability panel with the check's gaps as its request; its own brief (`make-testable.md`: the gaps, the detected stack, the contract's shape, "start the app only through `check_testability`"); the `check_testability` worker tool, given only to that task's work turns, which has the host run the Gate on the draft `litos.test.yaml` in the working copy; the run's own Gate step (all 8 steps on the draft, no baseline: a missing or broken draft, and a start-up that fails, are the task's to repair); a full review always; built-in skills `testability-dotnet` and `testability-node`, default for the type (both attach: they are short, and say which stack they are for).
- **W2.7** (live check, 2026-11-19): §11.3.

### 11.3 W2.7 live check (2026-11-19)

**Set-up.** Dev host from source (Debug, `:5180`, dev database on 5433). The installed factory (`:5181`) was not touched. The target was the private repository `nitinmms/clinic-portal`: the Blazor sample without the contract (§13.1 item 4), SQLite-backed, form login, roles admin, staff and patient. The tasks ran on the dev factory's default model, `deepseek/deepseek-v4.1-flash` via OpenRouter.

**What happened**

| Step | Result | Tokens |
|---|---|---|
| Register, then the check at registration | **Gaps**: `aspnet-blazor-server` detected. The dry run built and started the app; the marker was missing, with no reset and no auth. | none |
| Make-testable task, first run | Out of budget (PausedBudget) at 299k with **no file changed** (finding 1). Resumed with more budget: draft PR #1, but the Gate stopped at *start* because the role secrets were not set (4, 7). | 828k |
| Request changes 1 (marker render mode, `console.error`, junk, run the Gate) | Gate 18 of 19: sign-in failed for every role because of the seeding and the password policy. Out of budget again, then resumed: **Gate 19 of 19**. | +450k |
| Request changes 2 (remove `artifacts/`) | Clean branch, 13 files. Merged by the user (main `2aad33c`). | +156k |
| Check again, Adopt | **Contract found**, then adopted (profile rev 2). Gate baseline **Test-ready**, 19 of 19 in 31 s. | none |
| Feature task "patients can cancel an upcoming appointment" | 3 decisions answered. Out of budget at 292k in Implement (12), then resumed: verification 30 tests and 98.6% coverage, **browser smoke 17 of 17 with 11 screenshots, no repair**, full review, draft PR #2. | 538k |

**Exit check (§11 W2 row):**
- **Blazor sample, the core path:** met (check → Make-testable → merge → adopt → Test-ready → feature task handed off with smoke screenshots).
- **Postgres from `services` and email through Mailpit:** not covered, because clinic-portal uses SQLite and sends no email in Testing.
- **insta-story-generator:** not run yet.
- **Cost:** far above what the design expected, about 1.43M tokens for the retrofit and 0.54M for a small feature.

**Fixed during the check (pushed):**
- `d62987c`: the .NET preset writes `dotnet test` results to `artifacts/factory-test-results/` in the working copy, and only `/.factory/` was excluded, so every .NET task committed about 15k lines of `.trx` and coverage output. Both folders are now in `.git/info/exclude`.
- `4005bfc`: the adopted contract is stored in a `jsonb` column, which does not keep key order. A file adopted a moment earlier therefore showed as "differs from the adopted contract". `ContractYaml.Canonical` writes data maps in key order, and adoption compares that form.

**Findings, not yet fixed.** They are numbered as in the session. 12, 1 and 2 cost the most.

| # | Finding | Where it showed | Suggested direction |
|---|---|---|---|
| 1 | The Make-testable brief says "write unit tests", and the .NET preset requires 80% changed-line coverage, but the repo had **no test project**. The model spent 246k tokens on scratch experiments about where a test project could live, and wrote nothing. | Make-testable, first turn | Tell the brief what to do in a repo without tests (create `Tests/` next to the app, the layout the skill names). Or relax coverage for a `testability` task's own hooks. |
| 2 | No guard against a work turn that makes no progress: 29 calls with zero files changed, until the budget ran out. | Make-testable | A no-change limit (N calls without a write, then *Blocked: no progress*), like the scan's allowance. |
| 3 | The model. Reasoning bursts of 9–23k output tokens, all reasoning and no reply, spent the last of the budget several times, and cost notes were acknowledged and then ignored. | All three tasks | Record per model which ones suit work turns. Retry the same tasks on a stronger model before changing the briefs. |
| 4 | A Make-testable task **hands off when the Gate on its draft failed**. | First handoff | Hand off only when the Gate passes. Otherwise the result is a repair turn or *Blocked*. |
| 6 | The review called about 53k lines of committed junk *Minor* and left it open; nothing stopped the handoff. | First handoff | Committed generated output (coverage, `.trx`, logs) is at least *Major*. |
| 7 | The Gate failing at *start* because **the contract's secrets were not set** is the person's job, not the agent's. | First handoff | *Blocked: set TEST_… on the Testability panel*, then resume at the Gate. |
| 8 | `uiTesting.commit` holds the previous commit (main or the earlier draft), not the one the Gate ran on. | Every handoff | Record the working copy's head when the browser step runs. |
| 9 | The review can't tell real Gate rules from guesses: it reported the `hidden` marker as a risk, but the Gate waits for *attached*. It also didn't notice the test being gamed (`console.error` changed to `console.warn`). | First review | Give the review the contract's rules. Treat a change that hides a Gate signal as a defect. |
| 10 | A failed sign-in carries no server-log lines (the seeding error), so the agent built its own in-process login probe, costing about 70k. | Request changes 1 | Attach the app's log lines from the sign-in window to a `signin:*` failure. |
| 11 | When told the budget is low, the agent keeps exploring instead of committing what is already fixed. | Request changes 1 | A budget-low note that says "commit, then run `check_testability`", or a forced checkpoint commit. |
| 12 | **Coverage of `.razor` markup sent the feature task into bUnit:** version probing, reading assemblies through reflection, package conflicts. That was about 25 of 43 calls, more than the feature itself. | Feature task | When a contract is adopted, browser smoke checks the pages, so `.razor` markup lines could leave the coverage denominator. The brief would say "test the logic in C#". This is a design decision. |
| 13 | The reset seeds users only, so the lists are empty in the smoke screenshots and the feature (the Cancel button, the Cancelled label) is never visible in the evidence. | Feature handoff | The Make-testable brief and skills ask for representative domain data in the reset. W3 scenarios need it anyway. |
| 14 | The handoff published a "known limitation" (four tests failing on a clean checkout) that the factory's own verification contradicted: all 30 passed. | Feature handoff | Drop or flag agent-written limitations that name tests the verification ran green. |

Finding 5 is the `d62987c` fix above. The registration, the dry run, Check again, Adopt, the Gate baseline and the smoke comparison all behaved as designed, apart from 8 and the `jsonb` fix.

### 11.1 W1 as built (2026-11-17)

**Exit check met:**
- `litos-probe-web gate` passes all 8 steps on `NodeExpressSpa`, on `AspNetBlazor` (3 roles) and on **insta-story-generator** with the hand-written contract in `tests/Litos.Probe.Web.TestApps/Contracts/`.
- Each of 18 broken apps fails at the right check, with the right class and message. A flaky one passes as `Flaky`, and a `healthy` control passes (`tests/Litos.Probe.Web.IntegrationTests`).

**Differences from the plan above:**
- **Reset `none`** is a fourth reset strategy, for apps with no data (insta-story-generator). It must be declared.
- **`litos.test.yaml`** is the v3 JSON model in snake_case, mapped 1:1 (`application:` / `testability:`), not the idea doc's `app:` / `database:` sections. One schema, and misspelt keys are errors.
- **`InvalidContract`** is a fifth report outcome: nothing was started (CLI exit code 3).
- **Gate step 7** compares each test endpoint with a path that cannot exist, instead of requiring 404.
- **Step 8 lint** runs in the page as JavaScript. Playwright for .NET no longer has an accessibility snapshot API.
- **Steps 1 and 2 always run**, whatever `--steps` says, because no other step can run without them.
- **A `sqlite-snapshot` reset runs before start**, because a running app holds the file open.
- **`services`** (W2.2): the allowlist is data (`Probe.Web.Core/Profiles/services.json`), each image with its ports and a readiness command run inside the container, because Docker's port proxy accepts connections before the server is up. Containers bind loopback on run-allocated ports, carry `litos.probe` / `litos.probe.run` / `litos.probe.owner` labels, and are removed with their anonymous volumes at stop; `DockerServices.SweepAsync` removes an owner's leftovers.
- **Mailpit** (W2.2): emails it catches are saved as `.eml` artifacts and listed in the report's `emails`; checks on them come with W3 scenarios.
- The **Blazor test app** runs on Postgres from a `services` container and sends a "new sign-in" email to Mailpit (W2.2); SQLite stays its local-dev default. Moving it found a real Postgres trap: Npgsql refuses a local `DateTime` for `timestamptz`.
- **Build and reset commands may declare their own `environment`**, for values a build bakes in (a Vite SPA's API URL, §6.6 of the architecture doc). Secrets are refused there; only app processes and sign-in get them.
- **`DryRunAsync` takes an optional app folder**, for a monorepo candidate from `detect`. `ScenariosAsync` and the `scenarios` command wait for W3.
- **`JobObject` and the Windows executable resolver are copies** of the factory's, because Probe.Web never references the factory.

**Learned on the real apps, now in the `aspnet-blazor-server` profile's quirks:**
- Outside `Development`, a build (not a publish) serves no static web assets. Testing needs `builder.WebHost.UseStaticWebAssets()`, or `blazor.web.js` is missing and no circuit starts.
- The Identity template's passkey script logs `console.error` in any browser without a passkey.

`testability-dotnet` should carry both.

**After Probe.Web, full Probe adds:**
- AI `act` steps through `ModelGateway` (§8.2 budget rules);
- replay of verified recordings;
- data assertions;
- structured criteria with IDs and the coverage gate.

Probe.Web's environment, browser, signals, evidence and classifier are reused unchanged.

## 12. How it maps onto Probe's phases

| Probe feasibility Phase A item | Where it lands |
|---|---|
| App lifecycle, profile v3 `application` block | Probe.Web W1 + W2 |
| Deterministic steps and assertions (visible, text, URL, state, HTTP) | Probe.Web W3 |
| Scenarios approved in the DB | Probe.Web W3 |
| `ProbeStep` + repair on defects | Probe.Web W2 (`UiTestStep`) |
| Artifacts + Details UI, classifier | Probe.Web W1 + W2 |
| Structured criteria with IDs, coverage gate | Full Probe |
| Phase B (AI navigation, replay), Phase C (policy) | Full Probe, unchanged |

## 13. Decisions

### 13.1 Decided (2026-11-17)

1. **Location:** `Litos.Probe.Web.*` under `src/`, with this README at the repo root next to `README_Litos.Probe.md`.
2. **Scenarios:** in Probe.Web, as step W3. Fixed-step scenarios need no AI, and they give the criteria table real browser evidence.
3. **Docker `services`:** in **W2**, together with Mailpit. The Blazor sample uses Postgres from a throwaway container, so the factory never depends on a shared test database.
4. **Second tier-1 target:** a **new Blazor Server sample**, written for this purpose:
   - **Features:** form login (ASP.NET Identity), roles admin, staff and patient, Postgres, and email over SMTP. The domain is clinic appointments, as in the [UX mockup](https://claude.ai/artifact/2JEetresfyE1MaryHEMF1m).
   - **Two copies:**
     - **Under `tests/Litos.Probe.Web.TestApps/AspNetBlazor`**, it already meets the contract and backs the W1 integration tests.
     - **As its own GitHub repository**, it **starts without the contract**, so W2's live check goes through detect → dry run → Make-testable task → adopt for real. The two copies differ only by the retrofit, and the testapp copy is what a good retrofit should look like.

### 13.2 Still open

Carried from testability-architecture.md §12:
- the scope of Gate step 7;
- who may adopt a contract (Admins only, or Members too);
- smoke on every task, or only when the diff touches UI or server code;
- the dry run automatic at registration, or on demand.
