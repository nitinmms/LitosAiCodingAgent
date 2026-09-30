# Software Factory M1 — implementation architecture

M1 is the thin slice defined in [ReadMe_LitosSoftwareFactory_V1.md](../../ReadMe_LitosSoftwareFactory_V1.md) §18:

- one admin;
- one GitHub project at a time;
- `@factory` → worker → gateway → verification → review → branch push and draft PR → handoff and rework.

It is judged by the [M1 task set](evaluation/m1-task-set.md). This document turns the blueprint into projects, types, contracts and a build order. Where it cites existing code, the names were checked against `litos-software-factory-v1` at `160b9dd`.

## 1. Solution layout

```text
src/
  Litos.Hosting/                      NEW  class library (FrameworkReference Microsoft.AspNetCore.App)
  Litos.VsCodeHost/                   CHANGED  thin exe on Litos.Hosting; no behaviour change
  Litos.SoftwareFactory.Contracts/    NEW  wire DTOs shared by host and worker (no ASP.NET, no EF)
  Litos.SoftwareFactory.Core/         NEW  domain: lifecycle, orchestration, budget, estimator (pure)
  Litos.SoftwareFactory.Infrastructure/ NEW  EF Core/Npgsql, git, GitHub, verification, worker launcher
  Litos.SoftwareFactory.Host/         NEW  ASP.NET Core exe: API, SSE, auth, gateway, coordinator, serves Web
  Litos.SoftwareFactory.Worker/       NEW  per-run exe on Litos.Hosting
  Litos.SoftwareFactory.Web/          NEW  React + Vite + TypeScript SPA (npm project, built into Host/wwwroot)
tests/
  Litos.Hosting.Tests/  Litos.SoftwareFactory.Core.Tests/  Litos.SoftwareFactory.Infrastructure.Tests/
  Litos.SoftwareFactory.Host.Tests/  Litos.SoftwareFactory.Worker.Tests/
```

**Dependencies** (arrows point to what a project uses):

- **Worker** → Hosting → Host (the existing DI library), Agent, Tools, Kernel, Tools.Mcp.
- **Worker** → Contracts.
- **Host** → Core, Infrastructure, Contracts, and `Litos.Host` for the real providers.
- **Core** → Contracts only.
- **Nothing depends on** Litos.Api or VsCodeHost.

## 2. Litos.Hosting extraction (PR 1, behaviour-preserving)

### What moves

Moved from `Litos.VsCodeHost` into `Litos.Hosting`, keeping namespaces aligned (`Litos.Hosting`, `Litos.Hosting.Turns`, `Litos.Hosting.Approvals`):

| From VsCodeHost | Becomes | Notes |
| --- | --- | --- |
| `AgentWorker` | `Litos.Hosting.AgentWorker` | Per-session turns, steering, `CancelTurn`, PTC runner and kernel toggle. Provider/model state moves behind `IModelSelection` (below) |
| `ChannelContext` | same | |
| `Approvals/PendingApprovalRelay`, `PendingApprovalWireEvents` | same | |
| `Turns/TurnsEndpoints` (turns, cancel, approvals, sessions, history), `KernelEndpoints` | `MapLitosTurnEndpoints()`, `MapLitosKernelEndpoints()` | SSE keep-alive and `ToSseData` unchanged |
| `AutoApprovalGate` | same | |
| Loopback startup and port handshake from `Program.cs` | `LoopbackHost.ConfigureLoopback(builder)` / `LoopbackHost.StartAndAnnounceAsync(app)` | Port 0, `MinResponseDataRate = null`, first stdout line `{"port":N}` |
| MCP wiring from `Program.cs` | `services.AddLitosMcp(options)` | Fire-and-forget init, refresh service, approval gate composition |

### What stays in VsCodeHost

Config and keys endpoints, files, skills, attach, MCP management, settings (model/provider switching), session actions, reflect, context endpoints, `ShareFileTool`.

### New extension points

All are optional, and their defaults reproduce today's VsCodeHost behaviour:

| Seam | Default (VsCodeHost) | Factory worker |
| --- | --- | --- |
| `IModelSelection` { current provider/model/context length; `Switch…`; `EnsureResolvedAsync` } | `PersistedModelSelection`, which is today's logic including saving to `~/.litos/config.json` | `FixedModelSelection` from launch arguments, never saved |
| `IToolSetPolicy.Create(sessionId, turnKind)` → `ToolRegistry` | full registry, or `[KernelCodeTool]` when PTC is on (today's branch) | chat turns: read-only subset; run turns: factory set; PTC on |
| `IWorkingDirectoryResolver` | transcript's directory, else the process current directory | always the working copy (the process current directory) |
| Transcript root | `JsonlTranscriptStore()` → `~/.litos/sessions` | `JsonlTranscriptStore(<factory data>/runs/<runId>/sessions)` |
| Skill roots | `SkillDiscovery(startDirectory)`, user-profile roots | new optional `SkillDiscovery(startDirectory, IReadOnlyList<string>? userRoots)`; the worker passes only the factory skill folder (M3; empty in M1) |

### PR 1 exit check

- **Tests:** all existing `Litos.VsCodeHost.Tests` pass unmodified, apart from `using` lines where types moved. `Litos.Hosting.Tests` covers the new seams with their defaults.
- **Manual VS Code smoke test:**
  - a turn;
  - cancel;
  - a steering message;
  - PTC on/off/reset;
  - an Ask-mode MCP approval;
  - switching the model, which still persists to `config.json`.

## 3. Wire contracts (`Litos.SoftwareFactory.Contracts`)

### Host ↔ worker control

The host calls the worker's loopback HTTP API. Every request carries header `X-Factory-Secret`, and the worker also checks `Host`.

| Call | Purpose |
| --- | --- |
| `POST /sessions/{id}/turns` (existing SSE contract) | start a turn. Body adds `TurnKind` (`Chat`, `Spec`, `Implement`, `Repair`, `Review`, `Rework`, `Nudge`) |
| `POST /sessions/{id}/cancel` (existing) | pause/cancel/decision stop |
| `POST /sessions/{id}/compact` | compaction before large turns (§8.6) |
| `POST /shutdown` | graceful exit |

### Worker → host callbacks

The worker calls `FACTORY_HOST_URL` with the same secret.

| Call | Purpose |
| --- | --- |
| `POST /internal/runs/{runId}/submissions` | the completion tools (`submit_work`, `request_decision`, `submit_review`, `submit_spec`) post their typed payloads here |
| `POST /internal/runs/{runId}/gateway` | model gateway (below) |
| `POST /internal/runs/{runId}/ready` | worker started, MCP settled, port known |

The completion tools call the host **from inside `ITool.InvokeAsync`**. That makes them work the same whether the model calls them directly or through PTC kernel code, where tool calls produce no `ToolCall*` events on the turn stream. The tool result tells the model what happens next: "Recorded; stop and wait for the reviewer" for `request_decision`, and "Recorded" for the rest. The host acts on the callback, not on the event stream.

### Model gateway contract

`GatewayChatProvider : IChatProvider` in the worker implements `StreamAsync(ChatRequest, ct)`:

- **Request:** `POST /internal/runs/{runId}/gateway` with body `{ requestKey, chatRequest }`. `ChatMessage`/`ContentBlock` serialize with their existing polymorphic JSON.
- **Response:** NDJSON, one `GatewayEvent` per line. `GatewayEvent` is a closed union mirroring what providers emit: `TextDelta`, `ReasoningDelta`, `ToolCallStarted`, `ToolCallArgsDelta`, `ToolCallCompleted`, `MessageCompleted` (message + `UsageInfo`), `Heartbeat`, `Error { code, message }`.
- **Refused admission:** the response is `Error{ code: "budget_exhausted" | "quota_exhausted" }`. The provider throws `GatewayRefusedException`, which surfaces as `ErrorOccurred` in the loop. The host has already moved the run to `PausedBudget` before responding.
- **`ListModelsAsync`:** returns the single model fixed at launch.
- **`requestKey`:** a GUID per call, so a repeated callback cannot double-charge (§9.2).

## 4. Core (`Litos.SoftwareFactory.Core`, no I/O)

- **`TaskLifecycle`:** the §7 state machine as an explicit transition table plus guards. Illegal transitions throw.
- **`Stage`:** separate from lifecycle state (§5.1).
- **`RunOrchestrator`:** implements §8.5 as a resumable step machine. Each step is a record (`Preflight`, `StartTurn(kind)`, `Verify`, `Review`, `Handoff`, `Stop(reason)`), and `Next(RunContext, StepOutcome)` returns the next step. All side effects go through ports, so orchestration is fully unit-testable with fakes.
- **Limits and no-progress rules:**
  - repair cycles;
  - the nudge;
  - no changes and no submission;
  - the same failing set after a repair;
  - maximum decisions;
  - tool-call and wall-clock caps.
- **`BudgetLedger` rules:** reserve/settle arithmetic and admission (§9.2), with `UsageStatus` = `Reserved/Settled/Unknown`.
- **`RequestEstimator`:** implements §9.5. It uses the session baseline plus a characters ÷ 4 delta times a calibration ratio, and the first call estimates the system prompt, tools and messages. Characters are counted with the same rules as `Litos.Agent/Session/Compaction.cs` `EstimateChars` (internal today, so either expose it or port the few lines).
- **`Briefs`:** versioned prompt templates (run, repair, rework, review, nudge, spec), stored as embedded resources with a revision string.

**Ports** implemented by Infrastructure or Host:

| Port | Responsibility |
| --- | --- |
| `IFactoryStore` | load/save threads, runs, messages, decisions, usage, verification, handoffs, leases, outbox |
| `IWorkspace` | clone, fetch, branch, checkout, status, diff, commit, push |
| `IGitHub` | create/update draft PR |
| `IVerifier` | run a profile and return structured results |
| `IWorkerLauncher` / `IWorkerClient` | start/stop a worker; start a turn; cancel; compact |
| `IClock` | current time |
| `IEventSink` | append an outbox event |

## 5. Infrastructure

- **Persistence:** `FactoryDbContext` (EF Core + Npgsql, schema `public` of database `litos_factory`), with the migrations for the M1 tables (§7). Leases are claimed through a serialized transaction with `pg_advisory_xact_lock`.
- **`GitWorkspace`:** uses the `git` CLI with argument arrays and never a shell. The working copy lives at `<data>/workspaces/<projectId>`. It rejects a dirty tree before a run, and gets the diff from `git diff --unified=0 <base>..HEAD` (also used for changed-line coverage).
- **`GitHubClient`:** `HttpClient` against the REST API to create and update draft PRs. The token comes from host configuration only (M1: `FACTORY_GITHUB_TOKEN`, a fine-grained PAT scoped to the evaluation repos).
- **`ProfileVerifier`:**
  - runs profile steps with a timeout, `CI=true`, no stdin and process-tree kill;
  - deletes stale report files first;
  - parses reports with `JUnitReportParser`, `TrxReportParser`, `CoberturaParser` and `LcovParser`;
  - `ChangedLineCoverage` intersects coverage with the diff hunks;
  - a baseline run is cached per `(project, baseCommit, profileRevision)`.
- **`LocalProcessWorkerLauncher`:**
  - starts `Litos.SoftwareFactory.Worker.exe` with `WorkingDirectory` set to the working copy and a *cleared* environment. It passes through only `PATH`, `SystemRoot`, `TEMP`, `USERPROFILE`, `DOTNET_*`, `NUGET_*` and `npm_config_*`, plus `FACTORY_WORKER_SECRET`, `FACTORY_HOST_URL`, `FACTORY_RUN_ID` and `CI=true`. It never passes the database string, provider keys or GitHub token.
  - arguments: `--parent-pid`, `--model`, `--provider`, `--data-dir`.
  - reads the port handshake;
  - pipes stdout/stderr to `<data>/runs/<runId>/worker.log`;
  - `Kill(entireProcessTree: true)` on hard cancel.

## 6. Host and Web

**Host:**

- **Startup:** `Litos.SoftwareFactory.Host` binds `127.0.0.1:5180` by default and runs EF migrations through a separate `--migrate` command, never on normal start (§24).
- **Auth:** ASP.NET Core Identity with cookie auth. M1 seeds one Admin from `FACTORY_ADMIN_USER`/`FACTORY_ADMIN_PASSWORD` on first start; invitations arrive in M2. CSRF protection comes from same-site cookies plus an antiforgery header on state-changing requests.
- **Endpoints (M1):**
  - `POST /api/projects` (register: GitHub URL, default branch, profile preset, coverage threshold) and `GET /api/projects`;
  - `POST /api/threads` and `GET /api/threads`;
  - `GET /api/threads/{id}`, plus `POST /api/threads/{id}/messages` (`@factory` dispatch with a message-ID idempotency key);
  - `POST /api/decisions/{id}/answer`;
  - `POST /api/threads/{id}/budget`;
  - `POST /api/threads/{id}/accept`;
  - `POST /api/threads/{id}/cancel` and `.../pause` and `.../resume`;
  - `GET /api/threads/{id}/events?after=<seq>`: SSE backed by `OutboxEvent`, with `Last-Event-ID` replay.
- **`ModelGateway`:**
  - builds the real `IChatProvider`s through `Litos.Host`'s `AddLitosAgent` with a `LitosConfig` constructed from host settings, never from `~/.litos/config.json`;
  - admits and reserves inside a short transaction;
  - streams provider events to the worker as NDJSON;
  - settles on `MessageCompleted`;
  - leaves usage `Unknown` on abort.
- **`RunCoordinator`:** a `BackgroundService`. It wakes on a `Channel` signal or a 5-second poll, claims queued runs (slot cap 1 in M1), and drives `RunOrchestrator`. It also marks runs as `Interrupted` on startup when their worker PID and start time no longer match.

**Web** (React + Vite + TypeScript, Vitest + React Testing Library):

- **Screens:** sign-in; project registration; thread list; thread view with the stage rail, conversation and events, decision card, handoff card and budget panel; composer with `@factory`.
- **Design:** visual tokens and components follow the prototype (`docs/software-factory/prototype`).
- **Build:** output goes to `Host/wwwroot`, and the dev server proxies `/api` to the host.

## 7. M1 schema

The M1 tables are a subset of §14, with the same column intent:

`AspNetUsers`/Identity tables · `Project` · `TaskThread` (stage, state, budget cap, branch, PR, session id) · `Message` (dispatch key unique) · `Specification` · `TaskRun` (kind, status, base/head commit, worker PID and start time, heartbeat, prompt revision, stop reason) · `Decision` · `UsageEntry` (request key unique, estimate, reserved, actual input/cached/output, status) · `Verification` (+ report/log artifact paths) · `ReviewFinding` · `Handoff` · `WorkspaceLease` · `OutboxEvent` (sequence).

Deferred to later milestones: `ProjectMember`, `Invitation`, the settings tables, lessons and `AuditEvent`. The columns for owner and actor are there from M1.

## 8. Testing

| Project | What it proves |
| --- | --- |
| Core.Tests | every legal and illegal lifecycle transition; the orchestrator's full §8.5 paths with fakes (success, repair, review repair, decision, nudge then block, budget pause/resume, rework); reservation arithmetic; estimator calibration |
| Infrastructure.Tests | git operations against temporary repositories; report parsers against real sample reports from both evaluation repos; changed-line coverage; verifier timeouts and process-tree kill; launcher environment scrubbing (the worker's `env` must not contain the forbidden variables); EF mapping |
| Worker.Tests | secret and `Host` check; completion tools posting to a fake host from both the direct and the kernel-bridged path; `GatewayChatProvider` NDJSON parsing and refusal handling |
| Host.Tests | API with `WebApplicationFactory`; idempotent dispatch; SSE replay; gateway admission and settlement; a PostgreSQL-backed suite that runs when `FACTORY_TEST_DB` is set (a CI service container), otherwise skipped with a message |
| Web (Vitest) | screens and flows against a fake API |

**End-to-end smoke test** (manual, with a real provider): M1 task F1 on `filedb-sharp`, before the full evaluation.

## 9. Build order

Each step ends green and is committed separately.

1. **Litos.Hosting extraction and the VsCodeHost move** (PR 1 exit check, §2).
2. **Contracts and Core:** lifecycle, orchestrator, budget, estimator, briefs, with Core.Tests.
3. **Infrastructure:** git, verifier and parsers, launcher.
4. **Worker:** hosting, secret, completion tools, `GatewayChatProvider`.
5. **Host:**
   - database and migrations;
   - auth and admin seed;
   - projects and threads API;
   - gateway;
   - coordinator;
   - SSE.
6. **Web:** the M1 screens.
7. **Smoke test on F1, then the M1 evaluation:** 12 tasks, with results recorded as described in the task set.

## 10. Prerequisites and risks

**Prerequisites:**

- **Docker:** Docker Desktop running for `compose.factory-state.yml`. The engine was not running on this machine on 2026-09-30.
- **GitHub token:** a fine-grained token with contents and pull-request write access to `filedb-sharp` and `insta-story-generator` only.
- **Provider key:** at least one key for the host, choosing which provider and model runs the evaluation.

**Risks:**

- **VsCodeHost regression from PR 1.** Mitigated by moving the code without changing it and by the manual smoke test.
- **Gateway serialization gaps.** Some provider-specific content blocks may not round-trip. The mitigation is a round-trip test per provider using recorded events.
- **Windows process trees and file locks.** Build servers and child processes can outlive a cancel. The mitigation is verifier flags (§10) plus process-tree kill and a lock check before a new run.
- **Estimator accuracy.** Measured in M1 by design (§9.5).
