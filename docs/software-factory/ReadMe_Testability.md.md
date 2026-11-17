# Litos Software Factory — Testability Features

> **Status:** Design / in progress. This document describes how the Litos Software Factory makes web applications ready for automated browser testing and how the testing agent uses them.

The Litos Software Factory doesn't just build web apps. It builds apps that **can test themselves**. Every app the factory creates, or brings in from an existing repo, follows the same **Testability Contract**. A single, stack-agnostic **Testing Engine** can then start the app, log in as any role, drive it in a real browser, and report bugs with evidence.

This works the same way for Blazor Server, React, Rails, Go, Django/Flask and any other web stack the factory supports.

---

## Table of Contents

1. [How It Works](#how-it-works)
2. [Architecture](#architecture)
3. [The Testability Contract](#the-testability-contract)
4. [The `litos.test.yaml` File](#the-litostestyaml-file)
5. [Stack Profiles](#stack-profiles)
6. [Making an App Testable](#making-an-app-testable)
7. [The Testability Gate (Verification)](#the-testability-gate-verification)
8. [Authentication & Roles](#authentication--roles)
9. [Testing Engine Tools](#testing-engine-tools)
10. [Test Generation & Triage](#test-generation--triage)
11. [Conventions for the Coding Agent](#conventions-for-the-coding-agent)
12. [Security Rules](#security-rules)
13. [Stack Examples](#stack-examples)
14. [FAQ](#faq)
15. [Roadmap](#roadmap)

---

## How It Works

```
 ┌────────┐   ┌───────────────┐   ┌──────────┐   ┌──────────┐   ┌─────┐   ┌────────┐
 │ Build  │ → │ Make Testable │ → │  Verify  │ → │ Generate │ → │ Run │ → │ Triage │
 │  app   │   │  (contract)   │   │  (gate)  │   │  tests   │   │     │   │& report│
 └────────┘   └───────────────┘   └──────────┘   └──────────┘   └─────┘   └────────┘
```

1. **Build:** the factory's coding agent builds or changes the app.
2. **Make Testable:** the factory makes sure the app meets the Testability Contract.
3. **Verify:** an automatic smoke test proves the contract actually works.
4. **Generate:** the agent writes Playwright tests from user stories and the app's source code.
5. **Run:** tests run headless and deterministic, recording traces, screenshots and server logs.
6. **Triage:** each failure is sorted into *app bug*, *test bug* or *flaky*. Test bugs are fixed automatically and app bugs are reported with evidence.

**Design principle:** the LLM *writes and repairs* tests, and Playwright *runs* them. Regression runs are fast, cheap in tokens, and reproducible. An LLM driving the browser step by step is kept for the optional exploratory mode.

---

## Architecture

| Layer | Count | Responsibility |
|---|---|---|
| **Testing Engine** | 1 for the whole factory | Starts apps, resets data, logs in, runs tests, collects traces and logs. Built into Litos and optionally exposed as one MCP server. |
| **Stack Profiles** | 1 per framework | Default readiness checks, login quirks and failure signals for each stack. |
| **`litos.test.yaml`** | 1 per repo | Tells the engine how this particular app starts, gets ready, resets and authenticates. |
| **Test-only endpoints** | Optional, per repo | App-specific data setup (e.g. "create an order in state X"). Live inside the app, active only in `Testing`. |

There is **no MCP server per repo**. Differences between apps are configuration, not tools.

All browser tests are written in **Playwright (TypeScript)**, whatever the app's stack. Tests treat the app as a black box, so one test language means one runner, one report format, and fixes that carry over between projects.

---

## The Testability Contract

Every testable app provides the following:

| # | Item | Purpose |
|---|---|---|
| 1 | **`/health` endpoint** | Shows that the server is up and has finished starting. |
| 2 | **Readiness marker** | Shows that the *page* can be used (e.g. the Blazor circuit is connected, the React app has finished its first render). |
| 3 | **`Testing` environment** | Separate config: test database, emails captured instead of sent, external APIs mocked, animations disabled. |
| 4 | **Seed / reset command** | Restores the database to a known state before each run. |
| 5 | **Test users per role** | One seeded user per role. Credentials come from environment variables only. |
| 6 | **Test-only endpoints** | `/__test/*` endpoints for data setup and clock control. Registered in `Testing` only. |
| 7 | **Stable selectors** | Interactive elements have accessible labels/roles, or `data-testid` where labels aren't enough. |
| 8 | **Error surfacing** | A known element or signal that shows an unhandled UI error (e.g. `#blazor-error-ui`). |
| 9 | **`litos.test.yaml`** | Describes items 1–8 to the Testing Engine. |

---

## The `litos.test.yaml` File

Placed at the repo root. Written by the factory and reviewed by a human.

```yaml
# litos.test.yaml
version: 1
stack: blazor-server            # blazor-server | react-spa | rails | go-ssr | django | flask | ...

app:
  start: dotnet run --project src/Web --environment Testing
  url: http://localhost:5080
  env:
    ASPNETCORE_ENVIRONMENT: Testing
    ConnectionStrings__Default: ${TEST_DB_URL}
  startup_timeout_seconds: 90

ready:
  http: /health                           # server readiness
  selector: "[data-testid=app-interactive]"  # page readiness (checked after every navigation)

database:
  provision: testcontainers:postgres:16   # or: external (uses TEST_DB_URL)
  reset: dotnet run --project tools/Seed -- --reset

auth:
  type: form                              # form | jwt-localstorage | test-handler | none
  login_url: /Account/Login
  fields:
    user: "Email"                         # accessible label of the field
    password: "Password"
  submit: "Log in"                        # accessible name of the submit button
  success_when: url_not_contains("/Account/Login")
  roles:
    admin:    { user_env: TEST_ADMIN_USER,    pass_env: TEST_ADMIN_PASS }
    user:     { user_env: TEST_USER_USER,     pass_env: TEST_USER_PASS }
    readonly: { user_env: TEST_READONLY_USER, pass_env: TEST_READONLY_PASS }
  mfa:
    mode: disabled-in-testing             # disabled-in-testing | totp
    # totp_secret_env: TEST_ADMIN_TOTP

test_endpoints:
  reset: POST /__test/reset
  clock: POST /__test/clock               # body: { "now": "2026-12-01T09:00:00Z" }
  seeds:
    order: POST /__test/seed/order?state={state}

fail_on:
  - selector_visible: "#blazor-error-ui"
  - selector_visible: "#components-reconnect-modal"
  - server_log_level: Error

routes:
  public:  [/, /Account/Login, /about]
  protected:
    admin: [/admin, /admin/users]
    user:  [/dashboard, /orders]
```

| Section | Required | Notes |
|---|---|---|
| `stack` | ✅ | Selects the stack profile defaults. |
| `app` | ✅ | How to start the app and where it listens. |
| `ready` | ✅ | `http` is required. `selector` is strongly recommended for SPA/interactive stacks. |
| `database` | ✅ | `reset` must be idempotent. |
| `auth` | If the app has a login | See [Authentication & Roles](#authentication--roles). |
| `test_endpoints` | Optional | App-specific setup helpers. |
| `fail_on` | Optional | Overrides/extends the profile's failure signals. |
| `routes` | Optional | Auto-discovered from source if omitted. Explicit entries override. |

---

## Stack Profiles

Profiles provide defaults so that `litos.test.yaml` stays short. Any value can be overridden.

| Stack | Page readiness default | Auth default | Failure signals | Notes |
|---|---|---|---|---|
| **Blazor Server** | `[data-testid=app-interactive]` (rendered when `RendererInfo.IsInteractive`) | Identity cookie via form, antiforgery handled by the browser | `#blazor-error-ui`, reconnect modal | Don't wait for "network idle": the SignalR WebSocket stays open. One browser context per test = one circuit. |
| **React / Vite SPA** | `window.__APP_READY__ === true` set after first render and initial queries | JWT in `localStorage`, or cookie | Error boundary element, console errors | Save `localStorage` in the storage state, not just cookies. |
| **Rails (Hotwire)** | Page load complete and no busy Turbo frames | Devise session cookie, CSRF | Rails error page markers | Wait for `turbo:load` after navigations. |
| **Go (server-rendered)** | Normal page load | Session cookie, CSRF | HTTP 5xx | Usually the simplest profile. |
| **Django / Flask** | Normal page load | Session cookie, CSRF token | Debug/500 page markers | Use `TESTING` settings module. |

Adding a new stack = writing **one profile**. Nothing changes in the engine.

---

## Making an App Testable

### New apps — testable from day one

Each stack's starter template includes the full contract:

- `/health` endpoint
- Readiness marker in the root layout
- `Testing` environment config
- Seed project / script with role users
- `/__test/*` endpoints, registered only in `Testing`
- Accessible labels / `data-testid` on all template components
- A generated `litos.test.yaml`

### Existing repos — the "Make Testable" job

For repos the factory didn't create:

1. **Analyze:** detect the stack, the start command, the auth mechanism, roles and routes.
2. **Gap check:** compare against the Testability Contract.
3. **Retrofit:** add the missing items with the smallest changes possible.
4. **Describe:** write `litos.test.yaml`.
5. **Verify:** run the [Testability Gate](#the-testability-gate-verification).
6. **Pull request:** open a PR for human review. Changes to auth and environment config **always** need human approval.

---

## The Testability Gate (Verification)

An app is only marked **test-ready** after this automatic smoke test passes:

| Step | Check | Fails if |
|---|---|---|
| 1 | Start app in `Testing` and poll `/health` | Timeout or non-200 |
| 2 | Run database reset | Non-zero exit code |
| 3 | Log in as each role and save storage state | Login doesn't reach `success_when` |
| 4 | Visit every discovered route as the right role and wait for readiness | Marker missing, `fail_on` signal, or server error log |
| 5 | Visit a protected route while logged out | Not redirected to login |
| 6 | Visit an admin route as a lower role | Not denied |
| 7 | Call `/__test/*` against a **Production** build | Anything other than 404 |
| 8 | Selector lint | Interactive elements without a label or `data-testid` |

If a step fails, the agent fixes it and runs the gate again. After a set number of attempts it stops and reports the problem.

---

## Authentication & Roles

### Log in once, reuse many times

The engine logs in **once per role** during global setup and saves the browser storage state:

```
.litos/auth/admin.json
.litos/auth/user.json
.litos/auth/readonly.json
```

Each test starts a fresh browser context from the right state file, so it starts already logged in. Only the dedicated login tests (wrong password, lockout, logout, session expiry) use the login form directly.

### Auth types

| `auth.type` | When to use |
|---|---|
| `form` | Standard username/password form (ASP.NET Identity, Devise, Django auth, …) |
| `jwt-localstorage` | SPAs that store tokens in `localStorage` |
| `test-handler` | Apps using **external identity providers** (Entra ID, Google, Auth0). A test-only auth scheme is registered in `Testing`, and the engine never automates a third-party login page. |
| `none` | Public apps |

### Multi-factor authentication

- **Preferred:** MFA is disabled for seeded test users in `Testing`.
- **Alternative:** store a TOTP secret in an environment variable (`totp_secret_env`). The engine generates codes at login time.

### Credentials

- Credentials **never** appear in the repo, in generated test code, or in the LLM's context.
- Tests reference **environment variable names** only (e.g. `TEST_ADMIN_PASS`).
- Values are kept as project secrets in the factory and injected at run time.

---

## Testing Engine Tools

These are the tools the engine provides to the Litos agent (and to external agents through the optional MCP server):

| Tool | Description |
|---|---|
| `start_app(repo)` | Provisions the DB, starts the app with the `Testing` env, and waits for `/health`. |
| `stop_app(repo)` | Stops the app and cleans up containers. |
| `reset_data(repo)` | Runs the reset command or `/__test/reset`. |
| `seed(repo, kind, params)` | Calls a declared test-only seed endpoint. |
| `set_clock(repo, now)` | Moves the app's clock forward or back via `/__test/clock`. |
| `login_as(repo, role)` | Ensures a fresh storage state for the role. |
| `run_tests(repo, filter?)` | Runs the Playwright suite (or a subset) and returns structured results. |
| `get_trace(test_id)` | Returns the Playwright trace, screenshots and video for a test. |
| `get_server_logs(repo, since?)` | Returns server-side logs captured during the run. |
| `explore(repo, route, role)` | *(Optional, token-heavy)* LLM-driven exploratory session on one page. |
| `check_testability(repo)` | Runs the Testability Gate. |

---

## Test Generation & Triage

### Generation inputs

- User stories / specs from the factory
- Routes and authorization rules read from source (`@page` + `[Authorize]`, React Router config, `routes.rb`, Go handlers, Django `urls.py`)
- Forms and validation rules from source
- `litos.test.yaml`

### Generated test suite layout

```
tests/e2e/
├── playwright.config.ts      # generated from litos.test.yaml
├── global-setup.ts           # logs in each role and saves storage states
├── fixtures/
│   ├── ready.ts              # waits for the readiness marker after navigation
│   └── fail-signals.ts       # watches for fail_on signals after every step
├── auth/
│   ├── login.spec.ts
│   └── access-control.spec.ts
└── features/
    ├── orders.spec.ts
    └── admin-users.spec.ts
```

### Triage categories

| Category | Typical evidence | Agent action |
|---|---|---|
| **App bug** | Server exception in logs, wrong data, failed assertion on business behaviour | Report with trace, screenshot, log excerpt and steps to reproduce |
| **Test bug** | Selector not found after a UI change, outdated assertion | Repair the test automatically and run it again |
| **Flaky** | Passes on retry, timing-related failure | Flag it, fix the wait strategy, and quarantine it if it keeps failing |
| **Environment** | App didn't start, DB unavailable | Report as an infrastructure problem, not a test failure |

---

## Conventions for the Coding Agent

The following rules go into the project's `AGENTS.md` so that the app *stays* testable as it grows:

```markdown
## Testability rules
- Every button, input, link, select and form must have an accessible label or role name.
  Use `data-testid` only where an accessible name is not unique or not possible.
- `data-testid` values are kebab-case and describe purpose, not layout:
  `save-order`, not `blue-button-2`.
- Every new page/route must be added to the test plan.
- Never remove or rename a `data-testid` without updating the tests that use it.
- New roles must be added to the seed data and to `litos.test.yaml`.
- Code that sends email, SMS or calls a paid external API must go through an
  interface that is replaced with a fake in the `Testing` environment.
- Use an injectable clock (e.g. `TimeProvider` in .NET) instead of reading the system time directly.
```

---

## Security Rules

| Rule | Enforcement |
|---|---|
| `/__test/*` endpoints exist only in `Testing` | Conditional registration plus Gate step 7 (404 in a Production build) |
| Test auth handlers exist only in `Testing` | Conditional registration plus a CI check |
| Seed users exist only in the test database | Seeds run only against the `Testing` DB connection |
| No credentials in the repo or generated tests | Secret scanning in CI, and tests use env var names only |
| Storage state files are never committed | `.litos/auth/` is added to `.gitignore` |
| Changes to auth or env config need human review | The factory opens a PR and never auto-merges |

---

## Stack Examples

### Blazor Server (.NET 9+)

**Readiness marker** (`MainLayout.razor`):

```razor
@if (RendererInfo.IsInteractive)
{
    <span data-testid="app-interactive" hidden></span>
}
```

**Test-only endpoints** (`Program.cs`):

```csharp
builder.Services.AddHealthChecks();

if (builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddSingleton<FakeTimeProvider>();
    builder.Services.AddSingleton<TimeProvider>(sp => sp.GetRequiredService<FakeTimeProvider>());
    builder.Services.AddScoped<TestSeedService>();
}

var app = builder.Build();
app.MapHealthChecks("/health");

if (app.Environment.IsEnvironment("Testing"))
{
    var test = app.MapGroup("/__test");
    test.MapPost("/reset", async (TestSeedService s) => { await s.ResetAsync(); return Results.Ok(); });
    test.MapPost("/clock", (ClockRequest r, FakeTimeProvider c) => { c.SetUtcNow(r.Now); return Results.Ok(); });
    test.MapPost("/seed/order", async (string state, TestSeedService s) => Results.Ok(await s.CreateOrderAsync(state)));
}
```

### React / Vite SPA

**Readiness marker** (`main.tsx`):

```tsx
function App() {
  const { isLoading } = useInitialData();
  useEffect(() => {
    if (!isLoading) (window as any).__APP_READY__ = true;
  }, [isLoading]);
  return <Routes>{/* ... */}</Routes>;
}
```

```yaml
ready:
  http: /health
  js: "window.__APP_READY__ === true"
auth:
  type: jwt-localstorage
```

### Rails

```ruby
# config/routes.rb
if Rails.env.test? || ENV["LITOS_TESTING"] == "1"
  namespace :__test do
    post :reset
    post :clock
  end
end
```

### Django

```python
# urls.py
if settings.TESTING:
    urlpatterns += [path("__test/", include("testsupport.urls"))]
```

---

## FAQ

**Why not use an LLM to drive the browser on every run?**
It's slow, expensive in tokens and hard to reproduce. The LLM writes and repairs tests, and Playwright runs them. Exploratory LLM sessions are available on demand through `explore`.

**Why not one MCP server per repo?**
The tools are the same for every app. The differences are configuration (`litos.test.yaml`) and stack behaviour (profiles). One engine is far easier to maintain.

**Why write the tests in TypeScript when the app is in C#, Ruby or Python?**
Browser tests treat the app as a black box. One test language keeps the runner, reports and the agent's skills the same across every project.

**What about apps that only use Google / Entra ID login?**
Use `auth.type: test-handler`. The factory registers a test-only authentication scheme in the `Testing` environment. Third-party login pages are never automated.

**Does this slow the app down in production?**
No. Everything test-related is registered only in the `Testing` environment, and the Testability Gate checks that it is absent in Production builds.

---

## Roadmap

- [ ] Testability Contract templates for Blazor Server, React/Vite, Rails, Go, Django
- [ ] `litos.test.yaml` schema + validator
- [ ] Testing Engine core (`start_app`, `reset_data`, `login_as`, `run_tests`, `get_trace`, `get_server_logs`)
- [ ] Testability Gate
- [ ] "Make Testable" job for existing repos (PR-based)
- [ ] Test generation from user stories and source
- [ ] Failure triage and automatic test repair
- [ ] Selector lint
- [ ] Optional MCP server exposing the engine to external agents (Claude Code, VS Code)
- [ ] Exploratory mode (`explore`)
