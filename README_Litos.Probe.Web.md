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
    Task<ProbeWebReport> ScenariosAsync(string workingCopy, WebAppContract c, IReadOnlyList<WebScenario> s, ProbeWebOptions o, CancellationToken ct);
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
litos-probe-web scenarios <repo> --scenarios scenarios/*.json
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

### 11.1 W1 as built (2026-11-17)

**Exit check met:**
- `litos-probe-web gate` passes all 8 steps on `NodeExpressSpa`, on `AspNetBlazor` (3 roles) and on **insta-story-generator** with the hand-written contract in `tests/Litos.Probe.Web.TestApps/Contracts/`.
- Each of 17 broken apps fails at the right check, with the right class and message. A flaky one passes as `Flaky`, and a `healthy` control passes (`tests/Litos.Probe.Web.IntegrationTests`).

**Differences from the plan above:**
- **Reset `none`** is a fourth reset strategy, for apps with no data (insta-story-generator). It must be declared.
- **`litos.test.yaml`** is the v3 JSON model in snake_case, mapped 1:1 (`application:` / `testability:`), not the idea doc's `app:` / `database:` sections. One schema, and misspelt keys are errors.
- **`InvalidContract`** is a fifth report outcome: nothing was started (CLI exit code 3).
- **Gate step 7** compares each test endpoint with a path that cannot exist, instead of requiring 404.
- **Step 8 lint** runs in the page as JavaScript. Playwright for .NET no longer has an accessibility snapshot API.
- **Steps 1 and 2 always run**, whatever `--steps` says, because no other step can run without them.
- **A `sqlite-snapshot` reset runs before start**, because a running app holds the file open.
- **`services`** are in the model but refused by the validator until W2.
- The **blazor test app uses SQLite**. Postgres from a `services` container comes in W2.

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
