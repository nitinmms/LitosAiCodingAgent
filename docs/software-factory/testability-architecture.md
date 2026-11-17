# Testability: making web apps testable, then testing them in a browser before handoff

**Status:** Feasibility and draft design, revision 2 (re-evaluated for multiple tech stacks, §1.1). No factory code yet.
**Date:** 2026-11-17
**Input:** [ReadMe_Testability.md.md](ReadMe_Testability.md.md) (the idea), checked against the factory on branch `litos-software-factory-v1` (commit `3e23838`)
**Built by:** [Litos.Probe.Web](../../README_Litos.Probe.Web.md), Probe's deterministic first part (no model calls)
**Builds on:** [Litos.Probe](../../README_Litos.Probe.md) and its [feasibility report and decisions](../../ReadMe_LitosProbe_Feasibility.md) §8

**The idea in one line:** for a web app, the factory first makes the app *testable*, then drives it in a real browser and attaches screenshots, traces and server logs to the handoff, so the person who tests it starts from evidence instead of from nothing.

---

## 1. Verdict

**Feasible, and it is the missing half of Litos.Probe.**

- Probe answers *"does this change work in a browser?"*, but it assumes an app that can be started, reset, signed into and navigated. The Probe feasibility report called that "the largest single piece" and left it open.
- The Testability doc answers *"how does an app get into that state, and how do we prove it is?"* That is a contract, a gate that checks the contract, and a job that retrofits it.
- Together they give the user's goal: **testable app → UI test with evidence → handoff.**

The doc needs four changes to fit the factory. All four were decided on 2026-11-17 (§2):

1. The contract lives in the factory DB, not in a repo file.
2. Probe is the only engine. There is no generated TypeScript suite in V1.
3. A cheap check shows the gaps, and a person starts the retrofit.
4. V1 supports only `none` and `form` login.

**Effort:** about **3.5 weeks** on top of Probe Phase A (2–2.5 weeks). That is **5.5–6 weeks** in total to "testable app, UI-tested with screenshots before handoff" on two live-tested stacks, with four more shipped as data. Unit tests are included at every step (§11). Revision 1 said 2 weeks for one stack. The multi-stack review below is why it grew.

### 1.1 Revision 2: what the multi-stack review changed

Revision 1 was designed around one app shape: a single process, one port, a `/health` route, and a database somebody else keeps running. That shape fits a Blazor Server app and little else. Real web apps on the stacks the factory supports break it in eight ways:

| # | What breaks across stacks | Evidence | Change |
|---|---|---|---|
| 1 | **Many apps are more than one process.** An SPA plus an API, or a frontend dev server proxying to a backend. | `insta-story-generator`: `npm run dev` runs Vite and `tsx watch server` together, with Vite proxying `/api` to `:8787`. In production, `npm start` is one Express server serving `dist/`. | The contract describes **processes**, each with its own port, environment and readiness, and the order to start them in (§5). |
| 2 | **Dev servers make poor test targets.** They compile on first request (first-navigation timeouts), hold HMR websockets open, show dev-only error overlays, and restart on file changes. | `vite`, `next dev`, `tsx watch`, `dotnet watch`, `runserver` with autoreload | Stack profiles default to a **production-like build-and-serve** command, e.g. `vite build` then `npm start`, `next build` then `next start`, or `dotnet run -c Release --no-build`. The contract gets its own **`build`** step, because Verify's build isn't the app's build: the node preset's "build" is only `npm run typecheck`, and nothing runs `vite build`. |
| 3 | **Every framework takes its port differently.** "The app binds `PORT`" is true for Express and almost nothing else. | ASP.NET: `ASPNETCORE_URLS`, which launchSettings overrides unless you pass `--no-launch-profile`. Vite preview: `--port --strictPort`. Next: `-p`. Django: `runserver host:port`. Spring: `SERVER_PORT`. Go: there is no convention. | `{port:<process>}` placeholders in arguments and environment, with a **port-injection template per stack profile** (§6.6). The gate detects an app listening on its own default port instead of the assigned one, and says so. |
| 4 | **The factory would strip the app's own settings.** | `ToolchainEnvironment.SecretMarkers` removes any variable whose name contains `PASSWORD`, `CONNECTIONSTRING`, `TOKEN` and so on, so `ConnectionStrings__Default` and `DATABASE_PASSWORD` would vanish without an error. | Variables declared in the contract reach the app processes through an explicit path that skips the marker strip, and only for those processes (§8). |
| 5 | **"Where is the test database?" has a different answer per stack**, and a shared test DB is both a source of flakiness and of "environment down" failures. | Postgres, MySQL, SQL Server, SQLite files, Mongo | A **`services`** block: throwaway Docker containers per UI test run, limited to an allowlist of images (Postgres, MySQL, SQL Server, Mongo, Redis, Mailpit). Docker is already a factory prerequisite, because the factory's own Postgres runs in it. Also **`sqlite-snapshot`** reset: copy a seeded file before each run (§6.7). |
| 6 | **Readiness and error signals are stack-specific, and revision 1 required app changes for both.** | Blazor needs a marker. SSR stacks are ready on page load. Error pages differ: Django `Traceback`, Spring "Whitelabel Error Page", the ASP.NET developer exception page, the Next.js overlay. | **Universal signals first**, which need no app change: an uncaught `pageerror`, HTTP ≥ 500 on documents and fetch/XHR, failed same-origin requests, and error lines in stderr. Stack signals come second, as data. A readiness marker is required only by profiles flagged `needsMarker` (websocket-driven UIs: Blazor Server, LiveView, Hotwire with ActionCable). `/health` becomes optional; the default is "`GET /` answers below 500". |
| 7 | **Static "is the contract there?" heuristics would be N stacks × 9 items of code.** | — | The check becomes **detect, then dry run**. Stack markers come from data. Then, with the profile's default commands, the factory actually tries to build, start, reach and sign into the app (no LLM, about 1–2 min, under `VerificationGate`) and reports the *real* gaps. Running the app is the only check that works the same on every stack (§6.1). |
| 8 | **Retrofit know-how differs per framework** (how to seed Identity users versus Django users, how to read `PORT` in Go, how to register `/__test` only in Testing). | — | Stack recipes ship as **built-in skills** (`testability-dotnet`, `-node`, `-python`, `-jvm`, `-go`), attached to Make-testable tasks by detected stack. They are editable by Admins, with no code per stack (§6.3). |

Two simplifications come with these changes:
- **The required contract shrinks to three things:**
  1. how to build and start the app;
  2. how to reset its data;
  3. one user per role, if it has a login.

  Everything else is a stack default or optional. Fewer required app changes means less retrofit per stack.
- **`jwt-localstorage` isn't a separate login type.** Playwright's storage state saves `localStorage` as well as cookies, so `form` login covers most SPAs that keep a JWT there. Only tokens held in `sessionStorage` need `reSignInPerScenario: true`. V1's `none` + `form` therefore covers more apps than revision 1 claimed.

The full shape of what "works on any stack" means, and what it doesn't, is the support-tier table in §6.8.

## 2. Decisions (2026-11-17)

| # | Question | Decision |
|---|---|---|
| 1 | Where the test contract lives | **In verification profile v3** (DB, revisioned, Admin-approved). The *Make testable* task drafts `litos.test.yaml` in its PR. After merge, an Admin **adopts** it into the profile. Only the DB copy is ever used. A later edit to the repo file is flagged at handoff, never obeyed. |
| 2 | Which engine runs UI tests | **Litos.Probe**: in-host .NET Playwright, scenarios approved in the DB (Probe §8.1). The generated `tests/e2e` TypeScript suite becomes a later *Export to repo* feature. |
| 3 | When an app is made testable | A **Testability check** (no LLM, seconds) runs at registration and on demand, and lists the gaps. A person starts a **Make testable** task, which ends in a PR. Until a contract is adopted, the UI test step is skipped and the handoff says so. |
| 4 | Login types in V1 | **`none` and `form`.** Role credentials are per-project secrets, given only to the app and the browser, never to the agent. `test-handler` (Entra ID or Google), `jwt-localstorage` and TOTP come later. |

Probe decisions that carry over unchanged:
- no auto-accept;
- scenarios come from both the spec turn and people, each revision approved;
- runs with or without a spec (smoke scenarios);
- data assertions as named read-only queries;
- Probe failures share the 2 repair cycles;
- budget goes through `ModelGateway` under phase `Probe`;
- in-host library.

## 3. The idea doc against the factory as built

| The doc says | The factory today | What we do |
|---|---|---|
| `litos.test.yaml` at the repo root is read by the engine | Repo config is deliberately untrusted. The profile is `Project.VerificationProfileJson` (v2), edited only by Admins via `PATCH /projects/{id}` (`FactoryApi.cs:249`). `run.md:12`: "Do not change the verification configuration." | The yaml is a **draft** in the retrofit PR. It is adopted into **profile v3** (§5). |
| One Testing Engine with 11 tools (`start_app`, `login_as`, `run_tests` …), also given to the agent | `run.md:11`: "Do not start the application…". Nothing in the factory starts a long-lived process. `ProcessRunner` is run-to-completion only. There is no port allocation and no readiness poll. | The engine is **host-side**: the `TestEnvironment` service (start, ready, reset, sign in, stop) plus Probe. The agent gets **one** host tool, `check_testability`, and only in Make-testable runs (§6.3). The agent still never starts the app itself. |
| Generated Playwright TypeScript tests in `tests/e2e` | Probe's approved scenarios in the DB (Probe §8.1) | Probe only (decision 2). Triage categories map to Probe's classifier (§7.4). |
| Stack Profiles (Blazor, React, Rails, Go, Django) | Verification **presets** (`dotnet`, `node-react`, `python`, `java-maven`, `go`) are data in `Presets/*.json`. There is no Ruby or PHP toolchain or preset. | **Stack profiles** are a new data file per *framework*, which may share a toolchain preset: detection markers, default build and serve commands, port injection, readiness, error signals, log patterns, the Production environment switch and the retrofit skill (§6.6). Rails, Laravel and Phoenix are out until their toolchain preset exists (§6.8). |
| `database.provision: testcontainers:postgres:16` | The factory provisions nothing for target apps, but Docker is already installed for the factory's own Postgres | **`services`**: allowlisted images started per UI test run on free ports (§6.7), plus `external` (the project's own test DB) and `sqlite-snapshot`. |
| One `start` command and one `url` | — | **`processes[]`** with `{port:<name>}` placeholders, a `build` step, and `baseUrl` naming the process the browser opens (§5) |
| Test users from env vars `TEST_ADMIN_PASS` … | Secrets are allowlisted (`SecretNames.cs`: `github`, `provider:*`, `websearch:*`, `mcp:*:*`). `ToolchainEnvironment` strips `*PASSWORD*`/`*TOKEN*` variables from child processes. | New namespace **`project:<id>:<NAME>`**. Values go only into the app process and the Probe browser fill, through an explicit path that bypasses the strip only for those two. |
| Gate step 7: `/__test/*` returns 404 in a Production build | — | Kept, but only where it is cheap enough: in the Make-testable task and when a contract is adopted, not on every task (it needs a second app start). |
| Gate step 8: selector lint | — | **Runtime** lint, which is stack-agnostic: on every route the gate visits, Playwright's accessibility snapshot lists interactive elements with no accessible name and no `data-testid`. It needs no source parser per stack. |
| Routes auto-discovered from source | — | V1: routes are **declared** in the contract. The retrofit agent drafts them from source and cites file:line, and a person reviews them. A source parser per stack is later work. |
| `AGENTS.md` testability rules | Briefs (`Briefs/Templates/*.md`) own the process. Repo `AGENTS.md` is read but not enforced. | A **`{{testability}}` brief slot**, filled only when the project has an adopted contract: keep labels, update `data-testid` users, add new routes and roles to the contract draft. |
| Triage: app bug / test bug / flaky / environment | `RunOrchestrator.OnVerified` (repair loop, cap 2, "repair made no difference") | Probe's classifier (§7.4). Only *app bug* starts a repair turn. |

What lines up well:
- the per-project lease (one run per repo, so one app instance at a time);
- `VerificationGate` (exists *because* fixed ports collide);
- `.factory/` (git-excluded scratch);
- `HandoffEvidence` (already carries criteria and manual steps);
- the "Try it yourself" card, whose *Run it* step today reads "Suggested by the agent · not run by the factory". Once the contract is adopted, the factory has actually run those commands and can say so.

## 4. How it fits: the flow

```
Register project ──► Testability check (no LLM, seconds)
                         │
          not a web app ─┤─ test-ready (contract adopted) ─────────────────────┐
                         │                                                     │
                     gaps found                                                │
                         ▼                                                     │
      person starts "Make testable" task (type: testability)                   │
        Scan → Implement (+ check_testability tool) → Verify → Gate            │
        → Full review → Handoff: PR with code + litos.test.yaml draft          │
                         ▼                                                     │
      person merges PR on GitHub → project page: "Contract found on main"      │
                         ▼                                                     │
      Admin adopts → profile v3 revision N → Gate baseline on main ────────────┤
                                                                               ▼
                  every later task on this project:
   Discuss → Spec → Implement → Verify → UI test (start · reset · sign in ·
   smoke routes · criteria scenarios · screenshots) → Review → Handoff
   (evidence gallery; "Run it" marked "run by the factory")
```

## 5. Profile v3: the contract in the DB

Profile v3 adds two optional blocks. A v2 profile is read as v3 with neither block, so it behaves exactly as today. `application` (Probe's planned block, generalised to several processes) says how to bring the app up. `testability` says how to use it.

**Required:** `application.processes` (at least one), `application.baseUrl`, and a reset (`application.reset`, a `services` container, or `sqlite-snapshot`). `testability.auth` is required if the app has a login. **Everything else** is a stack-profile default that may be overridden.

Two examples follow, because the shape must fit both.

**A. `insta-story-generator`** (Node: an Express API serving a Vite build, no login):

```jsonc
"application": {
  "stack": "node-express-spa",
  "build": [ { "command": ["npm", "run", "build"], "timeoutSeconds": 600 } ],          // typecheck + vite build
  "processes": [
    { "name": "web",
      "command": ["npm", "start"],                                                         // tsx server/index.ts, serves dist/
      "environment": { "PORT": "{port:web}", "HOST": "127.0.0.1", "STORY_PROVIDER": "fake" } }
  ],
  "baseUrl": "web",
  "reset": { "strategy": "sqlite-snapshot", "file": "data/stories.db", "seed": ["npm", "run", "seed:test"] }
},
"testability": { "auth": { "type": "none" } }       // readiness, error signals, log patterns: node-express-spa defaults
```

**B. `clinic-portal`** (ASP.NET Core Blazor Server, form login, Postgres):

```jsonc
"application": {
  "stack": "aspnet-blazor-server",                       // selects stack-profile defaults (§6.6)
  "build": [ { "command": ["dotnet", "build", "src/Web", "-c", "Release"], "timeoutSeconds": 600 } ],
  "services": [ { "name": "db", "image": "postgres:16", "environment": { "POSTGRES_PASSWORD": "{generated}" } } ],
  "processes": [
    { "name": "web",
      "command": ["dotnet", "run", "--project", "src/Web", "-c", "Release", "--no-build", "--no-launch-profile"],
      "environment": {
        "ASPNETCORE_URLS": "http://localhost:{port:web}",          // the stack profile's port template
        "ASPNETCORE_ENVIRONMENT": "Testing",
        "ConnectionStrings__Default": "Host=127.0.0.1;Port={service:db:port};Username=postgres;Password={service:db:POSTGRES_PASSWORD}"
      },
      "ready": { "http": "/health" } }                             // optional; default is GET / < 500
  ],
  "baseUrl": "web",
  "reset": { "strategy": "command", "command": ["dotnet", "run", "--project", "tools/Seed", "-c", "Release", "--", "--reset"] }
},
"testability": {
    "ready": { "selector": "[data-testid=app-interactive]" },   // required by this stack profile (needsMarker)
    "auth": {
      "type": "form",                          // none | form   (V1)
      "loginUrl": "/Account/Login",
      "fields": { "user": "Email", "password": "Password" },    // accessible labels
      "submit": "Log in",
      "successWhen": { "urlNotContains": "/Account/Login" },
      "roles": {
        "admin":   { "user": "{secret:TEST_ADMIN_USER}",   "password": "{secret:TEST_ADMIN_PASS}" },
        "staff":   { "user": "{secret:TEST_STAFF_USER}",   "password": "{secret:TEST_STAFF_PASS}" },
        "patient": { "user": "{secret:TEST_PATIENT_USER}", "password": "{secret:TEST_PATIENT_PASS}" }
      }
    },
    "testEndpoints": { "clock": "POST /__test/clock" },        // optional
    "failOn": [ { "selectorVisible": "#components-reconnect-modal" } ],   // added to the profile's own signals
    "routes": {
      "public":    ["/", "/Account/Login"],
      "protected": { "admin": ["/admin/users"], "staff": ["/appointments"], "patient": ["/my/appointments"] },
      "denied":    { "patient": ["/admin/users", "/appointments"] }
    },
    "adoptedFrom": { "commit": "9f3c2e1", "path": "litos.test.yaml" }
}
```

**Placeholders** (resolved by the host only):

| Placeholder | Resolves to |
|---|---|
| `{port:<process>}` | a free loopback port allocated for this run |
| `{service:<name>:port}`, `{service:<name>:<VAR>}` | a service container's mapped port or its environment value |
| `{generated}` | a random value made per run, for throwaway service passwords |
| `{secret:NAME}` | the project secret `project:<projectId>:NAME` |

**Start order:**
1. services, each with the image's readiness probe;
2. processes in the order declared, each waiting for its `ready` (default: TCP accept on its port, then `GET /` below 500);
3. reset;
4. sign in.

Stop runs in reverse, and kills each process **tree**. `dotnet run` and `npm start` both leave a child process that holds the port if only the parent is killed. On Windows the tree goes in a Job Object. The port is checked free afterwards.

Rules:
- **`{secret:NAME}`** resolves to `project:<projectId>:NAME`. It is resolved only when the factory builds the app process environment and the Probe sign-in fill. It is never put into briefs, the worker environment, run logs, traces or the UI. `ProfileVerifier` refuses `{secret:}` in `steps`, so unit tests can't read role passwords.
- **Validation** (`VerificationProfile.Validate`): `start` and `health` are required whenever `application` is present. `reset` must be present. `auth.type=form` requires `loginUrl`, `fields`, `submit`, `successWhen` and at least one role. Every `{secret:}` must name a secret that has been set before the profile can be saved (the UI lists any that are missing).
- **Adoption** parses `litos.test.yaml` from the **default branch** (never from a task branch), maps it 1:1 onto these blocks, shows a diff against the current profile, and saves a new profile revision with audit. The yaml keeps the idea doc's shape for readability. Its parser lives in Infrastructure, with one schema.
- **Drift:** if a task's diff touches `litos.test.yaml`, the handoff says *"The contract file changed on this branch. The factory still used profile revision N. Adopt the new one after merging if you agree."* The same goes for scenario files (Probe §2.4).

## 6. The parts

### 6.1 Testability check: detect, then dry run (no LLM)

It runs on the reading copy at the default-branch head: at registration, after a profile change, and from a **Check again** button. It spends no tokens.

1. **Detect** (seconds). Every stack profile's `detect` markers are matched against the repo. For example:
   - `package.json` depends on `next` → `node-next`; on `express` with `vite` → `node-express-spa`;
   - a `.csproj` with `Microsoft.NET.Sdk.Web` plus `.razor` with `@rendermode` → `aspnet-blazor-server`;
   - `manage.py` → `python-django`;
   - `spring-boot-starter-web` → `jvm-spring-boot`.

   Several matches (a monorepo with `web/` and `api/`) are listed for the Admin to pick, and V1 supports one web app per project. No match at all → **Not a web app**. A web marker on a stack with no toolchain preset (`Gemfile` with `rails`, `composer.json` with `laravel`) → **Web app, stack not supported yet**.
2. **Dry run** (about 1–2 min, under `VerificationGate`). Using the profile's *default* commands (or the adopted contract, if there is one), the factory tries Gate steps 1–4 for real: build, start on an assigned port, reach `baseUrl`, look for a login form, and run the error signals on `/`. Each finding is concrete:
   - "Started, but listened on 8787, not the assigned 51873: the app ignores `PORT`."
   - "Build failed: `npm run build` missing."
   - "`/` returned 500: `ConnectionStrings:Default` is not set."
   - "Login form at `/Account/Login`, no test users."
   - "No reset declared."
3. **Static scan** only for what can't be seen at runtime: an existing `litos.test.yaml`, a `Testing` / `test` settings file, and a seed script.

**Output:** a `TestabilityReport` on the project, holding the detected stack, the dry-run findings, and a status: **Not checked / Not a web app / Stack not supported yet / Gaps (n) / Contract found, not adopted / Test-ready (rev N) / Broken on main**. The dry run is still advice; only the Gate (§6.2) makes a project Test-ready. It runs the project's code, like the existing baseline measurement already does.

### 6.2 Testability Gate

It is a host service that runs under `VerificationGate`, using the idea doc's 8 steps. Step 1 is generalised to "start the services and processes, and wait for each one's readiness", where readiness defaults to "`GET /` below 500", so `/health` is optional. Step 7 uses the stack profile's `productionEnvironment` switch: `ASPNETCORE_ENVIRONMENT`, `NODE_ENV`, `DJANGO_SETTINGS_MODULE` or `SPRING_PROFILES_ACTIVE`. It runs in three places, with different scope:

| Where | Steps | Why |
|---|---|---|
| Make-testable task (`check_testability` tool and the run's Gate step) | 1–8 | Prove the retrofit |
| Adoption / profile change (baseline on main) | 1–8 | Mark the project **Test-ready**, or **Broken on main** |
| Every task's UI test step (smoke) | 1–6 | Cheap regression of start, reset, sign-in, routes and access control. Steps 7–8 (Production build, lint) are skipped. Lint findings on *changed* pages are reported, not blocking. |

**Evidence for each step:** pass or fail, duration, and for route visits a **screenshot of each route as each role**, plus console errors and the server-log excerpt. Steps 5 and 6 (logged-out redirect, lower role denied) give the factory access-control checks without any scenario being written.

**Baseline rule** (Probe §2.1 lesson, inverted): smoke failures are compared with the **Gate baseline on main**.
- A step that also fails on main is **pre-existing**. It is disclosed and the task is not blamed.
- A step that newly fails is an **ApplicationDefect** of the task.
- Scenarios for *new* criteria are never baseline-compared (Probe's rule).

### 6.3 Make testable task

- **Task type `testability`**: added to the default `TaskTypes` (`TurnLabels.cs:49`). It is offered on the project page as **Start a Make testable task**, with the gap list prefilled as the request.
- **Stack recipes as skills:** the built-in skill named by the detected stack profile's `retrofitSkill` is attached automatically. Examples: `testability-dotnet` (seeding Identity users from environment variables, `--no-launch-profile`, registering `/__test` only in Testing), `testability-node`, `testability-python` (Django `createsuperuser` versus a fixture, waitress on Windows), `testability-jvm`, `testability-go` (read `PORT`). These are ordinary built-in skills: Admins can edit and reset them, they open with "the brief comes first", and each stays at about 2–3k characters to fit the 12,000-character skills cap.
- **Run:** `RunKind.Implement` with a new brief, `make-testable.md`, that differs from `run.md` in three places:
  1. It lists the gaps and the contract items in the order to fix them.
  2. It allows exactly one way to run the app: the **`check_testability`** host tool. The agent calls it, and the host starts the working copy's app under the draft contract on a free port, runs the Gate and returns step results plus screenshot ids. The agent never gets a shell that starts the app (`run.md:11` still holds).
  3. "Changes to auth or environment config: smallest possible, each one listed in `setupChanges`."
- **Draft secrets:** `check_testability` needs role passwords if the app has a login. The scan turn asks with `request_decision` for the role names. The person sets the values on the project's Testability panel (they're never typed into chat). Until they are set, the tool runs steps 1, 2, 4 and 7 only.
- **Steps:** Scan → Implement → Verify (unit) → **Gate (1–8)** → **full review always**. Auth and environment changes are risky, so `ReviewPlanner` forces Full for this type. → Handoff with a PR. The factory never merges, as today.
- **Repairs:** a Gate failure after Implement is an ApplicationDefect of the retrofit, and feeds the normal repair loop (2 cycles, shared).

### 6.4 UI test step in every task

- **Where:** in `RunOrchestrator`, `AfterPassingVerification` (`RunOrchestrator.cs:494`) enters a new `UiTestStep` when the project has an adopted contract and `UiTestedCommit != HEAD`. After a review-repair re-verify, it runs again before handoff, but only the smoke plus the scenarios that failed or that touch the changed criteria.
- **Order:**
  1. run `application.build` (cached per commit);
  2. start the services, allocate one port per process, and start the processes in order with the resolved environment;
  3. wait for each process's readiness, then the page-readiness signal;
  4. reset, using the declared strategy;
  5. sign in once per role, keeping an encrypted storage state in the run temp folder, deleted at stop (tracing is off during sign-in);
  6. smoke Gate steps 1–6;
  7. Probe scenarios for the task's criteria;
  8. stop and kill the process tree.
- **Budget:** smoke uses no model calls. Probe AI steps are charged under phase `Probe` (Probe §8.2). A budget refusal → `PausedBudget` with the existing panel.
- **No contract adopted:** the step is skipped. The Verification box reads *Application testing: Not set up*, with a link to the project's Testability panel, and the handoff keeps today's line "Application testing is yours."

### 6.5 Evidence and handoff

`HandoffEvidence` gains `UiTesting`:
- the status: Passed, Failed, Inconclusive, Skipped or NotSetUp;
- the contract revision and commit;
- one item per check: role, route or scenario, result, screenshot ids, trace id, console errors and a log excerpt;
- the pre-existing failures;
- a lint summary.

Stored files use Probe's `run_artifacts` table plus a download endpoint that copies `AttachmentsApi`'s `nosniff` and sandbox CSP headers. Both are already in Probe Phase A.

On the handoff card:
- **What the factory checked** gains the rows *App started*, *Signed in as*, *Routes visited*, *Access control*, *Criteria verified in a browser*, each with a pill.
- **Screenshots:** a gallery grouped by role. Clicking a shot opens it full size, with its trace and the log lines around it.
- **Try it yourself → Run it:** when the factory used the same commands, the tag changes from *Suggested by the agent · not run by the factory* to **Run by the factory on commit abc123**, and the commands come from the contract, not from the agent's guess.
- **Please check** lists only what the browser did not cover.

### 6.6 Stack profiles

A stack profile is one JSON data file per framework, shipped like the presets and editable by Admins. It names its toolchain preset, so `aspnet-blazor-server` and `aspnet-razor-pages` both build with `dotnet`. The engine knows nothing about any stack. Adding one means adding a file plus a retrofit skill.

| Field | Purpose | Example (`aspnet-blazor-server`) |
|---|---|---|
| `toolchain` | The verification preset it builds on | `dotnet` |
| `detect` | Markers: file globs and content patterns | `*.csproj` ~ `Microsoft.NET.Sdk.Web`; `*.razor` ~ `@rendermode Interactive` |
| `defaults.build` / `defaults.processes` | Production-like build and serve | `dotnet build -c Release`; `dotnet run -c Release --no-build --no-launch-profile` |
| `portTemplate` | How this framework is told its port | `env ASPNETCORE_URLS=http://localhost:{port}` |
| `testEnvironment` / `productionEnvironment` | The switch for Testing, and the one Gate step 7 uses | `ASPNETCORE_ENVIRONMENT=Testing` / `=Production` |
| `readiness` | `load`, or `needsMarker` with a default selector | `needsMarker`, `[data-testid=app-interactive]` |
| `errorSignals` | Page-side markers on top of the universal ones | `#blazor-error-ui`, `#components-reconnect-modal`, title "Error", "An unhandled exception occurred" |
| `logErrorPattern` | Regex for error lines in stdout and stderr | `^\s*(fail|crit):` |
| `quirks` | Facts the engine must respect | "never wait for network idle (SignalR)"; "one browser context per circuit" |
| `retrofitSkill` | The built-in skill attached to Make-testable tasks | `testability-dotnet` |

Initial profiles and the facts that differ between them:

| Profile | Serve command (production-like) | Port | Readiness | Error signals beyond universal | Windows note |
|---|---|---|---|---|---|
| `node-express-spa` | `npm run build`, then `npm start` | `PORT` env | `load` (or `js` probe if declared) | React error-boundary element, if declared | `npm` → `npm.cmd`; kill the tree, because node children hold the port |
| `node-vite-spa` (static SPA + separate API) | `vite build`, then `vite preview --port {port} --strictPort` | arg | `load` | — | API is a second process; its URL goes in via `VITE_*` **at build time**, so the API port is allocated before the build |
| `node-next` | `next build`, then `next start -p {port}` | arg | `load` | `nextjs-portal` (dev only, so its presence means the wrong mode) | — |
| `aspnet-blazor-server` | as above | `ASPNETCORE_URLS` | `needsMarker` | `#blazor-error-ui`, reconnect modal | `--no-launch-profile` or launchSettings wins over the port |
| `aspnet-mvc-razor` | `dotnet run -c Release --no-build --no-launch-profile` | `ASPNETCORE_URLS` | `load` | developer exception page text | — |
| `python-django` | `manage.py migrate`, then `waitress-serve --port={port}` (or `runserver 127.0.0.1:{port} --noreload`) | arg | `load` | "Traceback", the debug page title | **gunicorn does not run on Windows** |
| `python-flask` / `python-fastapi` | `waitress-serve` / `uvicorn --port {port}` | arg | `load` | Werkzeug debugger page | as above |
| `jvm-spring-boot` | `mvn -q package -DskipTests`, then `java -jar target/*.jar` | `SERVER_PORT` env | `load` | "Whitelabel Error Page" | JAR glob resolved by the host |
| `go-http` | `go build -o .factory/app`, then `.factory/app` | `PORT` env, **by convention only** | `load` | HTTP 5xx only | Retrofit adds `PORT` reading if the app hard-codes `:8080` |

**Universal signals**, on for every stack:
- an uncaught page error (`pageerror`);
- HTTP ≥ 500 on any document, fetch or XHR response from an app origin;
- failed requests to an app origin;
- `console.error` (configurable, because some libraries are noisy);
- stderr or `logErrorPattern` lines during a step.

**Gate step 1 diagnostics:**
- **Wrong port:** if nothing answers on the assigned port within the timeout, the host checks the profile's usual default ports (5000/5173/3000/8000/8080/8787) and reports "listened on X instead".
- **HTTPS redirect:** a redirect to `https://` is reported as "Testing still forces HTTPS: turn off HTTPS redirection and Secure-only cookies in Testing". This trap hits ASP.NET, Rails and Django the same way, and sign-in over plain HTTP silently fails when it isn't fixed.
- **Base host:** the base URL is `http://localhost:{port}`, not `127.0.0.1`, because browsers treat `localhost` as a secure context.

### 6.7 Test data across stacks

| `reset.strategy` | When | How |
|---|---|---|
| `services` + `command` | The app uses a server database | A fresh container per UI test run, from the allowlist `postgres`, `mysql`, `mariadb`, `mcr.microsoft.com/mssql/server`, `mongo`, `redis` (Admins can extend it). Mapped to a free host port, removed at stop and on pause. Then the project's migrate/seed command runs. There is no shared test DB, so there is no "test DB down" and no state left over between runs. |
| `sqlite-snapshot` | The app uses a SQLite file | The seed command runs once per commit into `.factory/snapshot.db`. Before each scenario the snapshot is copied over the app's file, with the app stopped or holding no handle; that is a stack-profile flag. |
| `command` | The project owns its test DB (`external`) | The project's reset command, as in revision 1 |
| `endpoint` | The app has `POST /__test/reset` | Called after start. Fastest between scenarios. |

**Email** across stacks: a **Mailpit** service (SMTP in, HTTP API out) replaces app-specific fake senders and a `/__test/emails` endpoint. Every framework can send SMTP to `{service:mail:smtp_port}`, and Probe checks messages through Mailpit's API. The fake-sender approach stays possible for apps that call an HTTP mail API.

**Data assertions** (Probe's named read-only queries) need a driver per database engine in the host: Npgsql, MySqlConnector, Microsoft.Data.SqlClient, Microsoft.Data.Sqlite, MongoDB.Driver. V1 ships Postgres and SQLite. Other engines fall back to HTTP assertions until their driver is added.

Docker already backs the factory's own Postgres, so every factory install has it. A `services` block on a machine where Docker isn't running is an **EnvironmentFailure** with that exact message, not a task failure.

### 6.8 What "works on any stack" means: support tiers

| Tier | Stacks | What you get |
|---|---|---|
| **1: built and live-tested in V1** | `node-express-spa` (insta-story-generator), `aspnet-blazor-server` (sample app with form login and roles) | Detection, dry run, retrofit skill, gate, UI test, all proved on a real repo |
| **2: shipped as data, not live-tested** | `node-vite-spa`, `node-next`, `aspnet-mvc-razor`, `python-django`, `python-fastapi`, `jvm-spring-boot`, `go-http` | The same features. The first real project on each is the test, and its findings go into the profile and skill. |
| **3: generic** | Any web app whose toolchain the factory has (dotnet, node, python, java, go) | A `generic-http` profile: universal signals only, commands typed by an Admin, no retrofit skill |
| **Not yet** | Rails, Laravel, Phoenix and anything else with no toolchain preset; apps that can only start with `docker compose up`; SSO-only login; mobile and desktop apps | The check says so plainly. Adding a toolchain preset plus a stack profile brings the stack in. |

## 7. Behaviour in detail

### 7.1 Lifecycle and run-model changes
- **No new lifecycle state.** The UI test step runs inside `Running` and ends either in a repair, `Blocked`, `PausedBudget` or Handoff, exactly like Verify.
- **`RunModel`** gains `UiTestStep` / `UiTested(outcome)`, `RunPhase.UiTest`, `RunState.UiTestedCommit` and `RunState.UiTest` status. Checkpoint JSON is round-trip tested.
- **The stage rail:** UI test is a sub-step of the **Verify** stage. The `Stage` enum is unchanged, and the rail shows "Verify · build · tests · browser". That avoids touching the board's columns.

### 7.2 Environment failures
- The app doesn't start, `health` times out, or `reset` exits non-zero → **EnvironmentFailure** → `Blocked`, with the start command, the readiness URL and the app-log tail. No repair turn.
- **Exception:** if the Gate baseline on main passes and the task changed startup code (`Program.cs`, `package.json` scripts, etc.), the failure is an **ApplicationDefect** instead, because the task broke startup.

### 7.3 Flakiness
- **Rerun before blame:** a failing smoke check or scenario is rerun once in a fresh browser context. If it then passes, it is marked **Flaky**: reported, not repaired, and counted on the project.
- After 3 flaky results in 10 runs, a scenario is **quarantined**: it still runs, but it can't fail a task. It appears on the Scenarios page for a person.

### 7.4 Triage mapping (idea doc → Probe classifier)

| Idea doc | Probe class | Factory action |
|---|---|---|
| App bug | `ApplicationDefect` | Repair turn (shared 2 cycles). The brief carries the failing check, the screenshot description, the log excerpt and "change the application, not the test". |
| Test bug | `TestDefect` | No code change. The scenario is flagged *needs fixing* for a person, and its criterion moves to **Please check**. |
| Flaky | `Flaky` (new) | Rerun-before-blame, then quarantine (§7.3) |
| Environment | `EnvironmentFailure` | `Blocked` with diagnostics (§7.2) |

## 8. Security

| Rule | How |
|---|---|
| The app gets its declared settings, and nothing else does | `ToolchainEnvironment` strips any name containing `PASSWORD`, `CONNECTIONSTRING`, `TOKEN` and so on. Variables declared in `application.processes[].environment` are added *after* the strip, for those processes only. A test pins that `ConnectionStrings__Default` reaches the app and never reaches a verification command or the worker. |
| Service containers are throwaway | They bind loopback only, use `{generated}` passwords, carry the label `litos.factory.run=<runId>`, and are removed at stop, on pause and on host start (orphan sweep by label) |
| Role passwords are never in the repo, the brief, worker env, logs or traces | `{secret:}` is resolved only for the app env and the Probe fill. Tracing is off during sign-in. Fill values are redacted in observations. A test asserts that no secret value appears in any artifact. |
| Storage state never leaves the run | It is encrypted with Data Protection under the run temp folder and deleted at stop and on pause. `.litos/auth/` is never used. |
| `/__test/*` exists only in `Testing` | Gate step 7 at retrofit and at adoption. The review brief for `testability` tasks names it. |
| Contract changes need a person | Adoption is Admin-only and audited. A PR is never auto-merged. Drift is flagged (§5). |
| The app under test runs on the factory host | It runs under the trusted-user rule (V1 §17), the same as verification commands. Probe's origin allowlist and route blocking are on by default, and the app gets the scrubbed toolchain environment plus its declared variables only. |
| Screenshots may show data | Only test data, by construction: reset runs before every UI test. Screenshots are served with project-access checks, like attachments. |

## 9. Change inventory (on top of Probe Phase A)

- **Core**
  - profile v3 `testability` block plus validation and `{secret:}` rules;
  - `TestabilityReport` / `TestabilityStatus`;
  - `UiTestStep` in `RunModel` and `RunOrchestrator`;
  - `HandoffEvidence.UiTesting`;
  - `make-testable.md` and the `{{testability}}` slot in `run.md`, `repair.md` and `review.md`;
  - task type `testability`, with Full review forced in `ReviewPlanner`.
- **Infrastructure**
  - `TestabilityChecker` (static heuristics per preset);
  - `LitosTestYaml` parser and mapper;
  - `TestEnvironment` (start, ready, reset, sign in, stop; builds on Probe's `ManagedProcess`);
  - `TestabilityGate` (steps 1–8, runtime accessibility lint);
  - a migration (project testability columns; secret-name pattern; no new run tables beyond Probe's).
- **Host**
  - the `check_testability` host tool (gateway-style callback from the worker, like `submit_work`);
  - the Gate baseline on adopt or profile change, under `VerificationGate`;
  - APIs: `GET /projects/{id}/testability`, `POST …/testability/check`, `POST …/testability/adopt`, and `PUT …/secrets/{name}` (set or replace, never read).
- **`SecretNames`**: `project:<guid>:<NAME>`.
- **Worker**: registers `check_testability` only when the run's brief kind is MakeTestable.
- **Web**
  - a *UI testing* column on the Projects page;
  - a Testability panel: gaps, contract diff, Adopt, role secrets;
  - an *Application testing* row in the Verification box;
  - handoff rows and screenshot gallery;
  - a "Run by the factory" tag on Try it yourself.
- **Presets**: `testability` defaults for `node-react` and `dotnet` first. The others follow as data.
- **Tests** (per the standing rule):
  - v2→v3 profile reading;
  - yaml mapping round-trip;
  - secret never leaks (briefs, env, artifacts);
  - checker heuristics on the sample repos;
  - Gate steps against `tests/Litos.Probe.TestWebApp` (gaining a form login with 2 roles and a `/__test` group);
  - orchestrator: UI test placement, rerun after review repair, skip when not set up, environment versus defect classification, baseline pre-existing;
  - API role checks;
  - web vitest for the new panels.

## 10. Rejected alternatives

- **Repo `litos.test.yaml` as the source of truth.** The coding agent could weaken its own checks. That is the same reason the profile ignores repo config.
- **Generated TypeScript suite run as a profile command with JUnit.** That is Probe §2.1's baseline trap: a new feature's tests fail on main too, so they would read as "pre-existing" and pass. It also gives no structured evidence.
- **Giving the agent `start_app` / `run_tests` tools on every run.** That breaks `run.md:11`, leaves processes running, and makes the model's own runs look like evidence. Only Make-testable runs get `check_testability`, and the host runs it.
- **A Testing-specific lifecycle state or board column.** It isn't needed: the step lives inside Verify, like Probe.

## 11. Build order and effort

Prerequisite: Probe Phase A steps for `ManagedProcess`, profile v3 `application`, `run_artifacts` and the classifier.

| Step | Scope | Effort |
|---|---|---|
| T1 | Profile v3 (`processes`, `build`, `services`, `reset` strategies, placeholders, `testability`), `{secret:}`, the project secrets namespace and API, the yaml parser and mapper | 3 days |
| T2 | `TestEnvironment`: multi-process start and stop with Job Object tree-kill and a port-free check, declared-env path past the strip, Docker services with orphan sweep, the four reset strategies, sign-in per role | 4 days |
| T3 | Stack profiles (schema, loader, 9 data files), universal and stack error signals, port and HTTPS diagnostics, `TestabilityGate` steps 1–8 against TestWebApp (two processes, form login) | 3 days |
| T4 | Check: detection, dry run, static scan; the Projects page column and Testability panel (gaps, secrets, Adopt, baseline) | 2 days |
| T5 | `UiTestStep` in the orchestrator; evidence; handoff rows and gallery; "Run by the factory" | 2 days |
| T6 | Make-testable task: type, brief, `check_testability` tool, forced Full review, 5 retrofit skills | 2 days |
| T7 | Live check on both tier-1 stacks: insta-story-generator (`node-express-spa`) and the Blazor sample (`aspnet-blazor-server`). Each goes register → check → Make testable → merge → adopt → feature task with UI test. | 2 days plus model cost |

That is about 18 days, or 3.5 weeks. If that is too much for one step, split it:
- **V1:** T1–T7 without `services` (external, command and sqlite-snapshot only) and with the two tier-1 profiles plus `generic-http`. That is about 2.5 weeks.
- **V1.1:** Docker services, Mailpit, the tier-2 profiles and the Mongo, MySQL and SQL Server assertion drivers. That is about 1 week.

## 12. Open questions

1. **Gate step 7 (Production build) cost.** It needs a second build and start. Is running it only at retrofit and adoption enough, or should a task that touches `Program.cs` or routing also run it?
2. **Live target with a login.** `insta-story-generator` has no login, and it needs a fake AI provider in `Testing` (already noted for Probe). Do we add a small sample web app with form login and roles (for example Blazor Server, matching the doc's examples) as the second target, or add a login to insta?
3. **Who may adopt a contract:** Admins only (like profile edits), or project Members too?
4. **Smoke on every task, or only when the diff touches UI or server code?** Smoke costs about 30–90 s of wall time and no tokens. The recommendation is every task, for the access-control checks.
5. ~~Docker services in V1 or V1.1?~~ **Decided 2026-11-17: in V1** (Probe.Web step W2), with a new Blazor Server sample on Postgres as the second live target. See README_Litos.Probe.Web.md §13.1.
6. **Tier-2 profiles: ship them untested, or hold each back until a real project uses it?** The recommendation is to ship them, labelled "not yet proved" in the check's output.
7. **Dry run at registration:** it runs the project's code for 1–2 minutes. Run it automatically like the baseline, or only on **Check again**?
8. **Later:** `test-handler` (SSO), TOTP, `sessionStorage` tokens beyond re-sign-in, `docker compose` apps, Ruby/PHP/Elixir toolchains, several web apps per project, the TypeScript suite export, and an MCP server exposing the engine to Claude Code and VS Code.
