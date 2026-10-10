# Software Factory M3 — implementation architecture

M3 is "factory settings and breadth" in [ReadMe_LitosSoftwareFactory_V1.md](../../ReadMe_LitosSoftwareFactory_V1.md) §18. It builds on M2 ([m2-architecture.md](m2-architecture.md)), whose check ran on 2026-10-09: checks 1–4 passed, and check 5 failed on cost (§10.1 there). This document turns the M3 list into changes, schema, tests and a build order. Where it cites existing code, the names were checked against `litos-software-factory-v1` at `45b06bd`.

## 1. Starting point

| M3 item | Already there | Missing |
| --- | --- | --- |
| Factory settings (§8.3) | `FactoryOptions`, read once from the environment and `.env.factory`; a read-only `GET /api/settings` | A settings store, an Admin API, a settings area in the app, and settings that change without a restart |
| Providers and models (§8.4, §9.4) | Anthropic, OpenAI, Gemini, MeshApi, OpenRouter and Local providers in the engine (`LitosHostBuilder`); `TaskThread.Provider`/`Model` | The factory allows OpenRouter only (`HostProviders`, `OnlyProvider`); keys are fixed at start; every thread says "strict"; no catalog, allowed models or picker; **Gemini ignores `MaxOutputTokens`** |
| Secrets | — | No encrypted storage anywhere; one GitHub token in the environment |
| Budgets, limits, quotas | Default budget, slot cap, repair cycles (code), and quota refusal in `BudgetLedger` | A maximum budget; quotas are never filled in (`EfFactoryStore` leaves them at zero); repair cycles are not settable |
| Tools | PTC on for every thread; the shell's fixed 5-minute limit | PTC per thread and whether members may turn it off; a shell limit setting; web search (the worker has no key, and the tool is in no set) |
| MCP (§8.1) | `AddLitosMcp`, `McpConfigStore(path)`, `McpAwareApprovalGate`; `WorkerReady.McpReady` on the wire | Workers start no MCP server; the host never waits for "MCP ready"; no server list, secrets, Test connection or run snapshot |
| Skills (§8.2) | Workers already skip the user-profile roots (`SkillDiscovery(..., userRoots: [])`) | The factory library and the repository-skill policy. **Repository `.litos/skills` are advertised in every worker's system prompt today, with no approval, while the `skill` tool is in no set** |
| Presets (§10.2) | `dotnet` and `node-react`; JUnit, TRX, Cobertura and LCOV parsers | Python, Java and Go; editable presets; editing a project's profile (`ProfileRevision` never changes); the registration checks |
| Re-run the M1 task set | The task set and M1's results | — |

Local-folder projects (§6.2) are moved to M4 (§2).

## 2. Decisions

Made with the user on 2026-10-09, before any M3 code:

- **Shape.** This document first, then one commit per build step (§10), each with comprehensive tests, as in M1 and M2.
- **Settings live in the factory database** and are edited in the app. `.env.factory` seeds them on first start only. It keeps what the host needs before it can read the database: the connection strings, the data directory and the first Admin.
- **Secrets are encrypted in the database** with ASP.NET Data Protection, its key ring in `<data>/keys`. Provider keys, MCP secrets and the GitHub token are set or replaced, never shown again. Backing up the data directory becomes necessary to read them (noted for M4's backup work).
- **MCP permissions in the factory are Full or Deny.** There is no Ask: nobody watches a run to approve a call. The other Litos faces keep Ask.
- **Local-folder projects move to M4.** M3 stays GitHub-only.
- **M2's cost regression is recorded and parked** (m2-architecture.md §10.1). It is looked at again later, not in M3's first steps.
- **Carried from M2 and done first:** a question sent to a working task blocks it (m2-architecture.md §10.1). It is an M2 feature that does not work.

- **One factory-wide GitHub token,** moved into the encrypted settings. The blueprint's per-project credential waits until a project needs a different account.
- **The M3 check re-runs four M1 tasks,** F2, F4, R3 and F8, not all twelve (§9).
- **Web search is the engine's Tavily tool behind a host proxy** (§6).

## 3. Settings store (build step 2)

### 3.1 Storage

One table, `factory_settings`, of named sections, each a JSON document with a revision:

| Column | |
| --- | --- |
| `Section` | key: `providers`, `tools`, `budgets`, `mcp`, `skills`, `presets` |
| `Json` | the section's settings, never secrets |
| `Revision` | bumped on every change; a write names the revision it read (409 if stale) |
| `UpdatedAt`, `UpdatedBy` | for the audit trail, which also records each change |

Secrets go in their own table, `factory_secrets`: `Name` (`provider:openrouter`, `mcp:<server>:<VAR>`, `github`), `Ciphertext`, `SetAt`, `SetBy`. The API reports only whether a secret is set and when.

A section's shape is a C# record in Core (`ProviderSettings`, `ToolSettings`, `BudgetSettings`, …) with defaults equal to today's behaviour, so an empty table behaves as M2 does.

### 3.2 Reading settings at run time

`FactoryOptions` stays for the bootstrap values. Everything else is read through `ISettings`, which holds the current snapshot of every section in memory and reloads a section when it is written. Consumers take the snapshot when they start something (a thread, a run, a call), never mid-way:

- a thread copies its provider, model, budget and PTC choice at creation, as it does now;
- a run copies its capability snapshot (§7.4) at start;
- the gateway reads the budget policy per call.

So a change affects what starts afterwards, as §8.2 of the blueprint requires.

### 3.3 Seeding from the environment

On first start, when `factory_settings` is empty, the host writes each section from the environment variables M2 reads (`FACTORY_DEFAULT_BUDGET`, `FACTORY_SLOT_CAP`, `FACTORY_MODEL`, `OPENROUTER_API_KEY`, `FACTORY_GITHUB_TOKEN`, …). After that the environment is ignored for those settings, and the host logs once which variables it no longer reads. `Validate()` stops requiring `OPENROUTER_API_KEY`: a factory with no usable provider starts, and the app says what to set.

### 3.4 API and app

- `GET /api/admin/settings` returns every section with its revision and the set/not-set state of each secret. Admin only.
- `PUT /api/admin/settings/{section}` with `{ revision, settings }`.
- `PUT /api/admin/secrets/{name}` sets or replaces a secret; `DELETE` clears it.
- `GET /api/settings` (every user) grows to what members need: the enabled providers and their allowed models, the defaults, the maximum budget, and whether PTC can be turned off.

The app gets a **Settings** area for Admins, one tab per section (blueprint §8.3). Projects and People stay where they are.

## 4. Providers and models (build step 3)

### 4.1 Providers from settings

`HostProviders` and `OnlyProvider` are replaced by a factory-owned `IChatProviderFactory` that builds a provider on first use from the current settings and decrypted key, and drops it when either changes. It builds providers through the engine's own types, so the factory runs exactly the provider code the other faces run. A provider is usable when it is enabled and has a key (Local: a base URL).

### 4.2 Budget precision per provider

A small Core table replaces the hard-coded `"strict"` in `ThreadView`:

| Provider | Label |
| --- | --- |
| Anthropic, OpenRouter, OpenAI | Strict |
| Gemini | Strict once its output cap is fixed (this step) |
| Local, MeshApi | Estimated; the host charges its own estimate when usage comes back as zero |

A "strict-budget providers only" switch hides the estimated ones from members.

**Gemini's fix** (in the shared provider, so every face gets it): set a `GenerationConfig` with `MaxOutputTokens` and `Temperature`, and map thinking and cached-content counts into `UsageInfo`. The CLAUDE.md token-accounting rules apply.

### 4.3 Catalog and allowed models

- **Catalog:** the host fetches each enabled provider's list (`ListModelsAsync`) and stores it in `model_catalog` with the fetch time. For OpenRouter it reads the `/models` response itself, to record tool support (`supported_parameters`) and price; other providers report context length only, and the table says "not reported".
- **Allowed models** live in the `providers` section: per provider, a short list and a default. An Admin adds them from a catalog picker (search, tool-calling filter on by default, minimum context, family, hide allowed; virtualized; multi-select).
- **Retired models:** a refresh that no longer lists an allowed model marks it "not offered by provider". Members cannot pick it; running tasks keep it; a default falls back as §8.4 says.
- **Allow every model** per provider, off by default, with its warning.

As built (step 3b):

- **When a catalog is fetched:** only when an Admin asks, from the Providers tab, which asks when a provider's key is set, when a provider with a key is first enabled, when the picker opens on a provider never fetched, and from its Refresh button. There is no background schedule. One endpoint, `POST /api/admin/models/catalog/{provider}/refresh`, fetches and stores; `GET /api/admin/models/catalog` lists every provider's catalog with the allowed models it no longer lists. `model_catalog` holds the rows and `model_catalog_fetches` each provider's last attempt, last success and error; a failed fetch keeps the last good list. Gemini's ids are stored without their `models/` prefix and compared without it.
- **Retired:** an allowed model is retired only when its provider's list has been fetched successfully and no longer has it. Nobody, Admins included, can start a new thread on it ("no longer offered by OpenAI"). A default on a retired model falls back to the first allowed model still listed, and a provider left with none is not offered. A local server never retires a model, since it lists only what it has loaded.
- **Allow every model** offers the catalog's models after the allowed ones, leaving out those reported unable to take tools (only OpenRouter reports it) and those without a context length the factory accepts; such a model's context length is the catalog's.
- **Recent models** are the distinct provider and model pairs of the person's own newest threads, read when New thread opens: the first three still offered are pinned below the default.
- **Unsaved changes:** the Providers and Budgets tabs keep their Save and Discard buttons in a bar fixed to the bottom of the window that says when there are unsaved changes, and the browser asks before the page is left with any. Switching tabs inside the app still discards them.

### 4.4 Threads

`CreateThreadRequest` gains `Provider` and `Model`, validated against the allowed list (400 otherwise). The New thread dialog gets the grouped, searchable picker, pinning the default and the member's three most recent. The thread view shows its provider, model and strict/estimated label.

## 5. Budgets and limits (build step 2, with the store)

The `budgets` section holds: the default and **maximum** task budget (creation and raising refuse above it), **daily and monthly per-user quotas** (filled into the ledger's snapshot from `usage_entries`; the refusal path already exists), repair cycles per run, and the slot cap (read by the coordinator on each claim). The rework top-up share and the output allowance move here too.

How quotas count, as built:

- Every model call charged to the person who asked for the run counts, chats included (a chat is charged to its own budget, not the task's, but it is still the person's spending).
- A call counts in the UTC day and month it was reserved in: settled and estimated calls at what they were charged, reserved and unknown ones at what they hold.
- With both quotas set, whichever has less left decides. A call that fits only with a shorter reply is sent with that shorter output limit, as under a task cap.
- Two of one person's tasks hold different thread locks, so a reservation under a quota also takes that person's advisory lock (PostgreSQL), and two calls cannot both fit into the same last tokens.
- The quotas and the output allowance are read on every call, so a change applies from the next one, even mid-task. Budgets, repair cycles and the slot cap apply to what starts afterwards.

## 6. Tools (build step 4)

- **PTC:** a default for new threads and whether members may turn it off. `TaskThread` gains `PtcEnabled`, read at worker launch, so a thread's choice survives reworks.
- **Shell command time limit:** passed to the worker (`--shell-timeout`) into its `LitosConfig`.
- **Web search:** on or off, with its key held by the host. The worker gets a `web_search` tool that calls the host (`/internal/runs/{id}/web-search`), which calls the provider and writes the query and result URLs to the run log. Implement and Repair turns get it; read-only turns get it only if the Admin says so.

As built (step 4):

- **The `tools` section** holds PTC for new threads (on) and whether Members may choose otherwise (yes; Admins always may), the shell command limit (300 seconds, from 30 to 3,600), web search (off) and web search on read-only turns (off). The first start writes it with the host's PTC option as the default. The web search key is the secret `websearch:tavily`.
- **PTC per thread:** `TaskThread.PtcEnabled` (migration `M3ThreadPtc`) is set at creation from `CreateThreadRequest.PtcEnabled` or the default; a Member asking for other than the default while Members may not choose is refused. A thread from before M3 has none and follows the host's `PtcEnabled`. Every worker the thread's runs launch, chats and specs included, uses it.
- **Launch:** the worker gets `--shell-timeout` (into its `LitosConfig.ShellCommandTimeoutSeconds`) and `--web-search work|all`, read from the settings at launch; with web search on but no key set, it gets none.
- **Searching:** the engine's `WebSearchTool` now takes a backend. The other faces keep Tavily with their own key; the worker's backend posts to `/internal/runs/{id}/web-search`. The host reads the settings again on every search, so turning web search off or clearing the key stops a run mid-way; it refuses a read-only turn unless allowed, cuts a query to 400 characters and asks for at most 10 results, and appends one JSON line per search (time, turn, query, URLs or the refusal or error) to `<data>/runs/<runId>/web-search.log`.

## 7. MCP servers and skills (build steps 5 and 6)

### 7.1 MCP servers

The `mcp` section lists servers in the engine's `McpServerDefinition` shape (name, stdio command and arguments or a URL, enabled, permission Full or Deny), with each secret environment variable held in `factory_secrets`. **Test connection** starts the server once in a scratch worker-like process and reports its tool count or its error.

### 7.2 MCP in the worker

At run start the host writes the snapshot's servers, secrets included, to `<data>/runs/<runId>/mcp.json`, readable by the worker's account only, and passes its path. The worker calls `AddLitosMcp` with that `McpConfigStore`. Under the trusted-user rule (blueprint §17) the agent's shell can read that file; this is noted, not hidden.

- **Ready means ready:** the engine's MCP initialization becomes awaitable, the worker reports `McpReady` only once every enabled server has connected or failed, and the host waits for it (up to 30 seconds) before the first turn. A server that failed is named in the run log.
- **Tool sets:** MCP tools join Implement and Repair turns only. Read-only turns never get them.

### 7.3 Skills

- **First (build step 1):** stop advertising repository skills. Until the policy exists, the worker discovers no skills at all.
- **Factory library:** skills stored in the `skills` section are written to `<data>/skills/<revision>/` and passed to the worker as its skill root.
- **Repository skills:** a policy, *after an Admin approves each one* (default), *always* or *never*. A found skill is recorded by name and content hash; an unapproved or changed one is skipped and the run log says so. Approvals are listed on the Skills tab.
- The `skill` tool joins the Implement and Repair tool sets.

### 7.4 Run capability snapshot

`TaskRun.CapabilitiesJson` records, at run start, the MCP servers (without secrets), the skills and their hashes, and the tool settings. A rework copies its task's last snapshot, as §8.2 of the blueprint says. The thread's details show it.

## 8. Verification presets (build step 7)

- **Editable presets:** the `presets` section holds them, seeded from today's embedded JSON. Editing a preset never changes a registered project.
- **Editing a project's profile:** a new `PATCH /api/projects/{id}` replaces its profile and bumps `ProfileRevision`, so the next run's baseline is measured again.
- **Registration checks:** each step's executables are found on the host's `PATH`, and the first baseline runs straight away rather than at the first task.
- **New presets**, each with a sample repository test in Infrastructure.Tests:
  - **Python:** a per-project venv in `<data>/venvs/<projectId>`; `pip install -r requirements.txt` (or the project's declared command); `pytest --junitxml` with `pytest-cov --cov-report=xml` (Cobertura).
  - **Java:** Maven `-B` with Surefire JUnit XML; coverage from JaCoCo XML, which needs a new `jacoco` parser.
  - **Go:** `go test -json`, read by a new `gotest` report format, and `-coverprofile`, read by a new `gocover` format, so no extra tools are needed.
- The toolchain environment allowlist gains `JAVA_`, `MAVEN_`, `GRADLE_`, `GO`, `PIP_` and `UV_`.

## 9. The M3 check

Manual, with real providers:

- a task runs on a second provider (Anthropic or OpenAI), and on Gemini with the cap enforced and labelled strict;
- an Admin changes a setting in the app and the next thread uses it, with no restart;
- an MCP server set to Full is used by a run, and one set to Deny is refused; an unapproved repository skill is skipped and said so;
- a task on a Python, a Java and a Go sample repository each reaches a handoff with tests and coverage measured;
- **the M1 task set does not regress:** four tasks spanning both repositories and sizes, F2, F4, R3 and F8, each against its M1 result, rather than all twelve, which cost several million tokens. M2's check 5 cost regression is reported alongside, not fixed here.

## 10. Build order

Each step ends green (all test projects and the web tests) and is committed separately.

1. **Carried fixes:** a question to a working task no longer blocks it, and its answer is shown (m2-architecture.md §10.1); repository skills are no longer advertised (§7.3). **Done 2026-10-09:** `93114ae` (a follow-up is framed as an aside; a work turn that still stops to answer has its answer posted and is continued with a `Continue` brief, keeping its nudge), `5432b39` (the worker finds no skills until step 6).
2. **Settings store, secrets and budgets** (§3, §5), with the Settings area's shell and its Budgets tab. **Done 2026-10-09:** `e26d22c` (tables, revisions, audit), `2882ce8` (key ring in `<data>/keys`), `dd208c4` (budgets read from settings; `/api/admin`), `e712a2e` (quotas applied per call; output allowance moved here), `8db9481` (Settings area and Budgets tab; New thread knows the maximum). Secrets have no screen yet: the provider key and GitHub token get theirs on the Providers tab in step 3.
3. **Providers and models** (§4), including Gemini's output cap. Split on 2026-10-09 into 3a and 3b. Decided then: a model's context length is kept per allowed model (looked up when it is added, editable) and copied to the thread, with `FACTORY_CONTEXT_LENGTH` left only for threads created before M3; all six providers can be enabled, strict-only starts on and hides MeshApi and Local from Members only; OpenAI's cache reporting is fixed alongside Gemini's.
   - **3a, done 2026-10-09:** `8c0b33a` (Gemini sends its output limit and temperature; Gemini and OpenAI report cached input apart and thinking as output), `423f570` (the `providers` section; `FactoryProviders` builds the engine's providers from settings and keys, rebuilt on change; no key needed to start), `3acda03` (a thread chooses its provider and model; `TaskThread.ContextLength`, migration `M3ThreadContextLength`; strict/estimated per provider), `25f15d5` (the GitHub token read from settings when used), `15275a3` (Providers tab; provider and model in New thread).
   - **3b, done 2026-10-10:** the catalog fetch into `model_catalog` (migration `M3ModelCatalog`), the Providers tab's picker with its filters, retired models, "allow every model", the save bar, and New thread's searchable model list with the default and three recent models pinned (§4.3, as built). Decided then, without asking: catalogs are fetched on an Admin's action only; a retired model is refused to Admins too; "allow every model" leaves out models reported unable to take tools.
4. **Tools** (§6). **Done 2026-10-10:** the Tools tab and section, PTC per thread, the shell limit and web search through the host (§6, as built).
5. **MCP servers** (§7.1, §7.2, §7.4).
6. **Skills** (§7.3).
7. **Verification presets** (§8).
8. **M3 check** (§9).
