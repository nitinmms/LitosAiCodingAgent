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

Local-folder projects (§6.2) are moved to M4 (§2), and on 2026-10-14 were not taken up at all.

## 2. Decisions

Made with the user on 2026-10-09, before any M3 code:

- **Shape.** This document first, then one commit per build step (§10), each with comprehensive tests, as in M1 and M2.
- **Settings live in the factory database** and are edited in the app. `.env.factory` seeds them on first start only. It keeps what the host needs before it can read the database: the connection strings, the data directory and the first Admin.
- **Secrets are encrypted in the database** with ASP.NET Data Protection, its key ring in `<data>/keys`. Provider keys, MCP secrets and the GitHub token are set or replaced, never shown again. Backing up the data directory becomes necessary to read them (noted for M4's backup work).
- **MCP permissions in the factory are Full or Deny.** There is no Ask: nobody watches a run to approve a call. The other Litos faces keep Ask.
- **Local-folder projects move to M4.** M3 stays GitHub-only. *Later, 2026-10-14:* the user decided not to take them up for now, in M4 or V1; the design in the V1 readme §6.2 is kept.
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

- **The `tools` section** holds PTC for new threads (on) and whether Members may choose otherwise (yes; Admins always may), the shell command limit (300 seconds, from 30 to 3,600), web search (on, the user's choice on 2026-10-10; it searches nothing until its key is set) and web search on read-only turns (off). The first start writes it with the host's PTC option as the default. The web search key is the secret `websearch:tavily`.
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

As built (step 5):

- **The `mcp` section** lists servers by name (letters, digits, `.`, `-`, single `_`; no `__`, which would make `mcp__{server}__{tool}` ambiguous), each a command with arguments or an http(s) URL, on or off, Full or Deny, with Full/Deny exceptions per tool, and the names of its secret variables, whose values are the secrets `mcp:{server}:{variable}`. Saving the section clears the secrets of servers or variables it no longer lists. At most 20 servers. Its enums travel as words in the app's API, as `FactoryWire.Json` already sends them.
- **Test connection** (`POST /api/admin/mcp/test`) starts the server as the form describes it, with its secrets, in the host rather than a scratch worker: the engine's `McpServerDefinition` gained `InheritEnvironment`, and the host starts the server with only the toolchain allowlist and its own variables, never the host's environment. It lists the tools, and the tab sets an exception for any of them.
- **Runs:** the capability snapshot is `TaskRun.CapabilitiesJson` (migration `M3RunCapabilities`): PTC, the shell limit, web search, the enabled servers (no secrets) and, once the worker reports, how each connected and which tools the run may use. A resumed run keeps its snapshot; a rework copies its task's last one (blueprint §8.2), so a settings change after a handoff does not reach the rework; any other run snapshots the settings as they are. Only implement and rework runs (the task's work) start servers; chat and spec runs, whose turns only read, never do.
- **Worker:** the host writes `<data>/runs/<runId>/mcp.json` (secrets included) before launch, passes `--mcp-config`, and deletes it when the run ends. The worker connects every server (30-second handshake each), then reports ready with a report per server. The host waits up to 45 seconds for that; a server that failed, or a worker that never says, is logged and recorded in the snapshot, and the run goes on without it. A run is offered a server's tool only where its access is Full; the engine's approval gate refuses a denied one as well. MCP tools join implement, repair and rework turns, directly and through the PTC bridge, and never a read-only turn.
- **Thread details** show the latest run's tool settings and each server with its tool count, "connecting" or "not available".
- **Tests** start a real stdio server, `tests/Litos.SoftwareFactory.TestMcpServer`, built beside the worker and host tests.

### 7.3 Skills

- **First (build step 1):** stop advertising repository skills. Until the policy exists, the worker discovers no skills at all.
- **Factory library:** skills stored in the `skills` section are written to `<data>/skills/<revision>/` and passed to the worker as its skill root.
- **Repository skills:** a policy, *after an Admin approves each one* (default), *always* or *never*. A found skill is recorded by name and content hash; an unapproved or changed one is skipped and the run log says so. Approvals are listed on the Skills tab.
- The `skill` tool joins the Implement and Repair tool sets.

As built (step 6):

- **The `skills` section** holds the repository-skill policy (`ApproveEach`, the default, `Always` or `Never`) and the library: up to 100 skills, each a name (letters, digits, `.`, `-`, `_`, up to 64), a one-line description of at most 1,024 characters, instructions of at most 100,000, and on or off. Its enums travel as words.
- **The library on disk:** the first run to need a revision writes its enabled skills to `<data>/skills/<revision>/NNN/SKILL.md` (staged beside it and moved into place, so no worker reads half a library) and no run changes it after, so a running worker's skills stay as they were. The folders are numbered, not named after the skill, so no skill name can be a reserved file name.
- **Repository skills** are those directly in the working copy's `.litos/skills`, read with the engine's own `SkillDiscovery.ScanRoot` (made public for this, `b4f9d9e`). A skill's identity is the SHA-256 of every file in its folder, by relative path and bytes, so changing a helper script beside the SKILL.md also needs a new approval. Each version found is a row of `repository_skills` (migration `M3RepositorySkills`, keyed by project, name and hash) with its SKILL.md and the names of its other files, for the Admin who approves it. Approvals are audited (`repository_skill.approve` / `.revoke`).
- **At worker start** the host finds the working copy's skills, records them, decides under the policy which are used, and writes `<data>/runs/<runId>/skills.json` (the library's folder and the allowed repository skills by name and hash), passed as `--skills`. The worker offers a repository skill only while its folder still has the allowed hash, so a skill the agent writes or changes during the run is never offered. A used repository skill replaces a library skill of the same name. Nothing under the user profile and nothing above the working copy is read.
- **Which turns:** the `skill` tool joins **every** turn when the run has any skill, not only Implement and Repair: it only reads a SKILL.md, and every turn's system prompt already lists the skills, so withholding the tool from read-only turns would advertise skills they cannot load. Without any skill, no turn gets it.
- **Runs:** the capability snapshot records the library revision and its skills, the policy, and what became of each repository skill. The thread is told once per run what was skipped (waiting for approval, changed since approved, not used by policy) and what replaced a library skill. A rework reuses its task's library revision and policy but looks at the working copy again. Chat and spec runs get the library and their reading copy's allowed skills, record what they found, and post no note.
- **The Skills tab** edits the library and the policy, saved together; below it, the skills found in repositories, each latest version with its SKILL.md and other files' names, Approve or Take back, which apply at once. An earlier version still approved is listed so its approval can be taken back. The thread's details list a run's skills, marking repository ones and those skipped.

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

Step 7 was split on 2026-10-13 into 7a (presets editable, a project's profile replaced, registration checks, the allowlist) and 7b (the Python, Java and Go presets with their report formats). The user also chose then to keep a Python project's venv in the factory's data folder, as planned, and had JDK 21, Maven 3.9 and Go installed on the factory's machine.

As built (7a):

- **The `presets` section** holds the presets in order, each a name (lower-case letters, digits and `-`), the name people see, and its profile as JSON text, so an Admin edits it as written and its formats keep their spelling. It is seeded on the host's next start from the shipped JSON, which stays in the code only for that. At most 30; at least one, since registration needs one; each profile must pass `VerificationProfile.Validate`. Registration without a preset takes the first, and `/api/settings` lists the presets with their names for the Projects page.
- **`PATCH /api/projects/{id}`** (Admin) replaces a project's profile with a preset's, with one written out, or only its coverage threshold, through the same checks as registration, and moves `ProfileRevision` on (audited `project.profile_update`). A run already going keeps the profile it read. The project list gives Admins the profile's JSON, and nobody else.
- **Registration checks:** every bare program a profile names must be on the PATH the verifier gives its commands (the host's, through the allowlist), found as the process runner finds it (PATHEXT on Windows); otherwise registration or the replacement is refused, naming it. A path such as `./gradlew` is left alone, since the repository is not there yet.
- **The first baseline at once:** registering a project or replacing its profile starts measuring its baseline on the project's reading copy at the default branch's head: holding the reading copy as chats do, taking a verification permit as runs do, and saving an ordinary baseline record, which a task starting from that commit then reuses. Logs are in `<data>/projects/<id>/baseline-r<revision>-<commit>`. A profile replaced while one is measured is measured next. The Projects page shows each project's baseline (measuring, how many tests pass or fail, a build that fails, or why the repository could not be read) and looks again every 3 seconds while one is being measured. Whether the baseline is still being measured is read before the saved baselines, so one that finishes in between is never shown as not measured. `FactoryOptions.BaselineOnRegistration` turns it off for the host tests that script their own verifications.
- **The allowlist** gains the prefixes `JAVA_`, `MAVEN_`, `GRADLE_`, `PIP_` and `UV_`, and Go's variables by name (`GOROOT`, `GOPATH`, `GOCACHE`, `GOMODCACHE`, `GOFLAGS`, `GOPROXY` and the rest) rather than a `GO` prefix, which would also pass `GOOGLE_` and the like. A name carrying a secret marker is still dropped. A pip index URL with credentials in it would pass; it is noted, not stopped.
- **The Presets tab** edits the presets, each profile in a text box that says at once when it is not JSON; the host's reasons are shown as it gives them. The Projects page gains the baseline column and, for Admins, "Change profile".

As built (7b):

- **The profile format** gains, all optional: a step's `prepare` commands, run in order before restore and failing as a restore would; a command's `standardOutput`, a file in the step's folder its standard output is also written to, for a tool that prints its report; the variables `{venv}` (the project's virtual environment, `<data>/venvs/<projectId>`) and `{venvBin}` (its `Scripts` on Windows, `bin` elsewhere), filled in by the verifier in commands, arguments and the profile's environment; and three report formats, `goTest`, `jacoco` and `goCover`. The verifier creates the folder part of every declared report and output path before a step runs, since `go test -coverprofile` does not.
- **Reports stay out of commits:** the new presets write their reports to `.factory/` in the working copy, and every working copy lists `/.factory/` in its own `.git/info/exclude`, so they never reach a diff or commit whatever the repository's `.gitignore` says. Maven's reports stay in `target/`, which Maven repositories ignore.
- **Python (pytest):** `python -m venv {venv}`, then `{venvBin}/python -m pip install -r requirements.txt pytest pytest-cov`, then pytest with JUnit XML and Cobertura coverage, `COVERAGE_FILE` and the cache kept out of the repository. A repository without a requirements.txt needs its restore edited. A Python project's worker gets `VIRTUAL_ENV` and the venv first on its PATH, so the agent runs the verifier's pytest. Two runs of one project share its venv; the verification permit (one at a time by default) keeps them from installing at once.
- **Java (Maven):** `mvn -B -ntp -DskipTests test-compile`, then `mvn` with JaCoCo 0.8.13's `prepare-agent`, `test` and `report` named on the command line, so the project's pom needs no plugin, and `-Dmaven.test.failure.ignore=true`, so a failing test still leaves its report and coverage. Surefire's `**/target/surefire-reports/TEST-*.xml` and JaCoCo's `**/target/site/jacoco/jacoco.xml` are read; JaCoCo's files, named by package, are found under the module's `src/main/java` (or kotlin, scala, groovy). Gradle is not a preset.
- **Go:** `go mod download`, `go build ./...`, then `go test -json -coverprofile=.factory/coverage.out ./...` with its output saved as the report. Each test's result is read, a failure's output kept as its message; a package that fails with no failed test (it did not build) is a failure of its own. Coverage file names are import paths, turned into repository paths with go.mod's module path.
- **Shipped presets on an existing factory:** the section records which shipped presets it has offered; a new one is added on the next start, after the Admin's own, and one an Admin removed is not offered again. Saving the tab keeps that record.
- **Tests:** each preset runs on a sample repository (`tests/.../Fixtures/Samples`) with the real toolchain, passing and with a failing test, and skips on a machine without it.

## 9. The M3 check

Manual, with real providers:

- a task runs on a second provider (Anthropic or OpenAI), and on Gemini with the cap enforced and labelled strict;
- an Admin changes a setting in the app and the next thread uses it, with no restart;
- an MCP server set to Full is used by a run, and one set to Deny is refused; an unapproved repository skill is skipped and said so;
- a task on a Python, a Java and a Go sample repository each reaches a handoff with tests and coverage measured;
- **the M1 task set does not regress:** four tasks spanning both repositories and sizes, F2, F4, R3 and F8, each against its M1 result, rather than all twelve, which cost several million tokens. M2's check 5 cost regression is reported alongside, not fixed here.

### 9.1 Results

**Python, Java and Go presets, on OpenAI (2026-10-14): passed.** Three private sample repositories in one domain, dynamic digital forms: `nitinmms/forms-api` (Python, FastAPI; storage behind an interface with a Postgres adapter), `forms-rules` (Java, Maven; submission validation) and `forms-export` (Go; CSV and JSON Lines export). Each seed passed its preset's exact commands before registration, and each baseline was measured at registration (16, 14 and 8 tests). Five tasks ran on `openai` / `gpt-6-luna`, every brief naming the library skills it should use (`tdd`, `fix-root-causes`, `prove-it-works`); all were accepted and merged.

| Task | Repository | Result | Tokens | Questions |
|---|---|---|---|---|
| P1 master-detail forms | forms-api | accepted after one change request (negative `minRows` accepted) | 95,251 | 1 |
| G1 master-detail export | forms-export | accepted | 113,974 | 2 |
| J1 detail-row validation and `sumMustEqual` | forms-rules | accepted as handed off; one minor finding left open | 166,809 | 3 |
| J2 validator crashes on NaN (bug seeded on purpose) | forms-rules | first run cancelled at 33,084: it started from `main` before J1 merged; re-run accepted | 48,868 | 0 |
| P2 AI form generator (Anthropic Messages API) | forms-api | accepted after one change request from a live test | 194,391 | 3 |

Every handoff had tests passing and changed-line coverage measured (91.5–100%). Found along the way:

- **Skills are loaded and followed.** Every run loaded the skills its brief named; the transcripts show tests written and seen failing before the code. The J2 re-run is the clearest case: J1 had added two more `new BigDecimal(number.toString())` conversions, and the run found all three and replaced them with one helper rather than guarding the one in the report; the first run, on the older code, had guarded the one site there was. P2's repair turn loaded `fix-root-causes` unprompted.
- **The kernel's `skill` call is a trap.** `skill("tdd")` fails with "A 'name' argument is required"; it must be `skill("name", "tdd")`. Most runs failed once or twice before finding the form.
- **The library's `tdd` skill covers bug fixes only** (its text is "TDD bug fix"); feature runs applied it anyway.
- **Over-asking persists** (M1 open item). Nine questions across five tasks; in each, the agent's own recommended option was the one chosen. About half were gaps in the briefs, half edge details with an obvious default.
- **Out-of-scope reformatting.** J1 collapsed untouched methods into one-liners and fully qualified `java.util` types, against the run brief's "do not reformat".
- **Partial follow-through on a change request.** P2's rework did two of the four instruction changes asked for; the result still passed the live test (three generated forms, all valid on the first attempt with `claude-haiku-5-5`, where one of three had failed before).
- **Tasks on one repository start from the same `main`.** J2 waited for J1's lease, but started from the commit before J1, because J1 was not yet merged; its PR conflicted. Dispatch dependent work after the merge.
- **Accept does not merge.** The factory reads the PR's state; merging is a separate step on GitHub.

**A setting changed with no restart (2026-10-14): passed.** The default task budget was changed from 300,000 to 250,000 through the Budgets API with the host running; the next thread, created without a budget, got 250,000. The default was set back to 300,000.

**An unapproved repository skill (2026-10-14): passed.** `forms-export` was given `.litos/skills/csv-conventions/SKILL.md`; under the default policy (approve each), the next run's thread said "Skipped repository skills waiting for an Admin's approval: csv-conventions.", and its `skills.json` listed no repository skill.

**Gemini, cap enforced and labelled strict (2026-10-14): passed after four provider fixes.** Task G2 (prefix CSV cells that a spreadsheet would run as a formula) ran with the default budget, which is how it also showed the setting above.

- On `gemini-2.5-flash` the first run was blocked: Gemini answered with no content, and the provider passed that on as an empty reply, so the turn "changed no files" with no cause given. The model was overloaded at the time (503 on direct calls). **Fix `5db10db`:** an answer with no text and no function call raises `ChatProviderEmptyResponseException` naming Gemini's `finishReason`; transient (sent again by the gateway) for a malformed call or no reason, not for safety or recitation; `MAX_TOKENS` stays with the gateway's cut-off report.
- On `gemini-3.1-flash-lite` every run failed on its second call with 400. Two causes, each found by replaying the conversation against the live API one change at a time:
  - **`8d92099`:** Gemini 3 attaches a thought signature to each function call and refuses a history that sends the call back without it. `ToolUseBlock` keeps an optional provider `Signature` (through the transcript and the gateway), which the provider reads and sends back. **`ec7129e`:** a call recorded without one (before the fix, or on another provider) goes with Google's documented placeholder, which Gemini 2.5 and 3 both accept.
  - **`f9589a4`:** the provider named a function response after the call's id; Gemini 3 requires the function's name.
  - Step 3 had been tried on Gemini 2.5 only, which tolerates all three.
- After the fixes G2 was accepted and merged: 155,674 of 300,000 tokens, never over the cap and nothing left reserved; 12 tests, 100% changed-line coverage, review not needed (7 lines). It loaded no skill, although the brief named two (every OpenAI run did), and cost three times the OpenAI bug fix (J2, 48,868).

**MCP Full and Deny (2026-10-14): passed.** `erpplus-db-mcp-server` (HTTP, three tools) was left at Full with `run_select_query` set to Deny; task P3 on `forms-api` (`gpt-6-luna`) was asked to call all three and build an example form from a real order table. The run's MCP status offered only `list_tables` and `get_table_schema`; the agent used both (finding `pohead` and `poitems` and their columns) and its call to `run_select_query` failed, which its handoff reported. Accepted at 131,798 tokens after a review repair. Two things to improve: a denied tool reaches the agent as a C# compile error ("the name … does not exist"), indistinguishable from a typo, where a message naming the policy would be clearer; and, as with `skill` and `search_code`, the agent guessed MCP argument shapes several times (`request` must be an object, `table_name` not `table`). The override was removed afterwards. While MCP was not running, earlier runs logged the refused connection and carried on without it.

**A defect in starting a run's C# kernel (2026-10-14), found during the F2/F4/R3/F8 re-runs.** F4 was blocked three times with "The worker did not report its port within 30 seconds", and R3's review stalled for 12 minutes after one model call. Both had the same cause, in `KernelHostLocator` (§12): a worker running from source builds `Litos.Kernel.Host` before its kernel starts; that build had files to copy (a Release build of the solution, run while testing the Gemini fixes, had refreshed `Litos.Kernel.Wire.dll`), a running kernel of another session held them locked, and the build then hung instead of failing. Recovered by stopping the stale kernel and the hung build and building the kernel host once; not fixed in code yet (§12).

**The M1 task set, F2, F4, R3 and F8 (2026-10-14): three pass at or under M1's cost, one fails on cost.** Run as m1-task-set.md says: on M1's provider and model (`openrouter`, `deepseek/deepseek-v4.1-flash`), from M1's baseline commits (each project's default branch pointed for the check at an `m3-check-base` branch on `07fb8b8` and `324ec0e`), the request text exactly as written, M1's budgets, at most one rework, judged on every acceptance criterion. Their PRs are not merged.

| Task | Result | Tokens | M1 | Questions |
|---|---|---|---|---|
| F2 JSON Lines export and import | Accepted after one rework (criteria 2, 3 and 5 under-tested at first) | 430,086 | 575,884, accepted after one rework | 4: 1 fair, 3 unnecessary |
| R3 Cancel a running generation | Accepted after one rework (the late-answer race untested; the rework found the code could let it through and fixed it) | 686,741 | 701,093, accepted after one rework | 2: 1 fair, 1 unnecessary |
| F4 Read-only open mode | **Failed: paused for budget at 592,783 of 600,000**, during its full review. Raised to 700,000 to see the work out: 69 tests, the criteria met, but it had also carried F1's size limits over from `main` (about 245 of 636 lines, and the repair that tested them), so it was cancelled | 609,277 | 401,521, accepted after one rework | 2: 1 fair, 1 unnecessary |
| F8 Secondary indexes | Accepted after one rework. The first handoff had no test that a lookup skips non-matching documents (criterion 4); the rework found `FindByIndex` still deserialized the whole collection and fixed it, and split a merged null/empty-string index value that made a lookup for "" disagree with `Find` (criterion 2). The first review found a blocking transaction bug, repaired | 533,605 | 577,612, accepted after one rework | 3: 2 fair, 1 whose recommendation would have failed criterion 2 |

Found by these runs:

- **The decision scan's recommendation can contradict the acceptance criteria**, which it does not see: for F2 it recommended refusing to import over existing keys (criterion 4 requires upsert), for F4 a new exception type where criterion 4 names `InvalidOperationException`. An answer taken from its recommendation would have failed the task.
- **A review that does not finish still lets a handoff through.** F8's rework had a light review that ended with "DidNotFinish" and no findings, and the run handed off anyway; the first round's full review had finished and found a real bug.
- **An agent can read newer code on other branches.** F4's working copy holds the whole repository, and the agent copied `main`'s size limits into a branch made from an older commit. It matters for re-runs from an old baseline; for ordinary tasks `main` is the base.
- **F4's cost went to a repair for changed-line coverage** (77.9% against 80%): 23 calls and 209,890 tokens, its context near 70,000 tokens mostly of command output. About 19,000 more went to a second decision scan after the kernel defect above blocked its start.

Against M1, then: F2 −25%, R3 −2%, F8 −8%, all accepted after one rework as in M1; F4 failed on budget (+52% at its cap) and went out of scope. Every question the four asked came from the decision scan, which M1's F2 and F4 ran without; 11 in all, about half of them unnecessary. Afterwards the projects' default branches were set back to `main`, and the `m3-check-base` branches deleted.

**Skills: used when the brief names them, otherwise not (2026-10-14).** The library held three skills (`tdd`, `fix-root-causes`, `prove-it-works`), their descriptions sent with every request. Every `gpt-6-luna` run whose brief named them loaded them, and its work followed them (tests written and seen failing first; the J2 re-run fixed all three crash sites), though no run without them was made to compare, so their effect on quality is not proven. Where the brief did not name them, they were not used: the eight deepseek runs of F2, F4, R3 and F8 (reworks included) made no skill call, and the Gemini run made none although its brief named two. The only unprompted load was one `gpt-6-luna` repair turn. Their cost is not visible: the deepseek re-runs came in under M1. Follow-up in §13.

With this the M3 check (§9) has been run in full.

## 10. Build order

Each step ends green (all test projects and the web tests) and is committed separately.

1. **Carried fixes:** a question to a working task no longer blocks it, and its answer is shown (m2-architecture.md §10.1); repository skills are no longer advertised (§7.3). **Done 2026-10-09:** `93114ae` (a follow-up is framed as an aside; a work turn that still stops to answer has its answer posted and is continued with a `Continue` brief, keeping its nudge), `5432b39` (the worker finds no skills until step 6).
2. **Settings store, secrets and budgets** (§3, §5), with the Settings area's shell and its Budgets tab. **Done 2026-10-09:** `e26d22c` (tables, revisions, audit), `2882ce8` (key ring in `<data>/keys`), `dd208c4` (budgets read from settings; `/api/admin`), `e712a2e` (quotas applied per call; output allowance moved here), `8db9481` (Settings area and Budgets tab; New thread knows the maximum). Secrets have no screen yet: the provider key and GitHub token get theirs on the Providers tab in step 3.
3. **Providers and models** (§4), including Gemini's output cap. Split on 2026-10-09 into 3a and 3b. Decided then: a model's context length is kept per allowed model (looked up when it is added, editable) and copied to the thread, with `FACTORY_CONTEXT_LENGTH` left only for threads created before M3; all six providers can be enabled, strict-only starts on and hides MeshApi and Local from Members only; OpenAI's cache reporting is fixed alongside Gemini's.
   - **3a, done 2026-10-09:** `8c0b33a` (Gemini sends its output limit and temperature; Gemini and OpenAI report cached input apart and thinking as output), `423f570` (the `providers` section; `FactoryProviders` builds the engine's providers from settings and keys, rebuilt on change; no key needed to start), `3acda03` (a thread chooses its provider and model; `TaskThread.ContextLength`, migration `M3ThreadContextLength`; strict/estimated per provider), `25f15d5` (the GitHub token read from settings when used), `15275a3` (Providers tab; provider and model in New thread).
   - **3b, done 2026-10-10:** the catalog fetch into `model_catalog` (migration `M3ModelCatalog`), the Providers tab's picker with its filters, retired models, "allow every model", the save bar, and New thread's searchable model list with the default and three recent models pinned (§4.3, as built). Decided then, without asking: catalogs are fetched on an Admin's action only; a retired model is refused to Admins too; "allow every model" leaves out models reported unable to take tools.
4. **Tools** (§6). **Done 2026-10-10:** the Tools tab and section, PTC per thread, the shell limit and web search through the host (§6, as built).
5. **MCP servers** (§7.1, §7.2, §7.4). **Done 2026-10-10:** the MCP servers tab and section, Test connection, the run capability snapshot, and MCP in work runs (§7, as built). Decided then, without asking: Deny is a server's default for its tools with Full exceptions possible (and the reverse), so Deny still means something beside "off"; Test connection runs in the host with a scrubbed environment rather than in a scratch worker.
6. **Skills** (§7.3). **Done 2026-10-13:** `b4f9d9e` (the engine scans one skills folder), `301c489` (backend incl. migration `M3RepositorySkills`), `6234518` (Skills tab; skills in the run details): the Skills tab and section, the library written per revision, repository skills found, recorded and approved per folder hash, and skills in every worker (§7.3, as built). Decided then, without asking: the `skill` tool joins every turn of a run that has skills, read-only turns included; a repository skill replaces a library skill of the same name; an approval is of every file in the skill's folder, not only its SKILL.md.
7. **Verification presets** (§8). Split into 7a and 7b on 2026-10-13. **7a, done 2026-10-13:** the Presets tab and section, `PATCH /api/projects/{id}`, the PATH check at registration, the baseline measured at once, and the wider allowlist (§8, as built). Decided then, without asking: a missing program refuses registration rather than warning; the baseline runs on the reading copy, not the task copy; presets are edited as JSON text. **7b, done 2026-10-13:** the Python, Java (Maven) and Go presets, with prepare commands, printed reports, the `{venv}` variables, the `goTest`, `jacoco` and `goCover` formats, and `.factory/` excluded in every working copy (§8, as built). Decided then, without asking: reports go to `.factory/` rather than relying on a repository's `.gitignore`; JaCoCo is named on Maven's command line rather than required in the pom; Gradle is left for an Admin's own preset.
8. **M3 check** (§9).

## 11. Deferred: organising the Board and Threads lists

Raised by the user on 2026-10-14, during the M3 check, and folded for later. Not built in M3.

**The problem.** With 41 threads, of which 5 were live (24 accepted, 12 cancelled), both screens had become hard to use:

- The Threads sidebar lists every thread under its project, finished or not, with no ordering by state.
- The Board keeps a cancelled task in the column of the stage it stopped at, so 7 cancelled cards sat in Implement, 3 in Handoff and 2 in Review: the in-progress columns were mostly dead work.

**Proposed.**

- **Board:** live tasks only by default (not Accepted, not Cancelled), with a toggle "Show finished (n)". Finished tasks shown leave the stage columns for one **Closed** column at the end: accepted first, then cancelled, newest first, the last 10 with "Show all". A strip above the board counts what matters ("1 needs you · 2 working · 1 queued"), each count a filter.
- **Threads sidebar:** per project, live threads first, those awaiting the user at the top, then by latest activity. Below them a collapsed **Finished (n)** group per project, the 10 most recent with an Accepted or Cancelled pill. A project with nothing live collapses to one line with its finished count. A search box above the list filters by title.
- **Scope:** the web app only (`BoardPage.tsx`, `ThreadsPage.tsx` and their tests); everything is derived from each thread's state, so no migration and no new API.

**Left out on purpose:** an explicit Archive action, and server-side filtering or paging, which become worth having at hundreds of threads, not tens.

**Open when it is picked up:** whether the finished group shows the last 10 or a time window such as the last 7 days.

**Built 2026-10-14**, as proposed, in the web app only (`BoardPage.tsx`, `ThreadsPage.tsx`, the ordering in `domain/task.ts`). Decided with the user: the finished group shows the last 10; the Closed column replaces the Done stage column, which only accepted tasks ever reach; "needs you" counts only the tasks the user owns (§7.1), and its count sets the Owner and Turn filters rather than a filter of its own; with no thread in the address, the Threads page opens the top of the new order; the search matches every title, finished ones past the 10 most recent included. Decided then, without asking: the Turn filter no longer offers Done; a finished thread named in the address opens its group and is listed even when older than the 10; the order uses `updatedAt` from the last load, so a live thread does not move while its events arrive.

## 12. Follow-up: starting a run's kernel without building

Found on 2026-10-14 during the M3 check (§9.1). **Both defects fixed on 2026-10-16** (see "Built" below); the two smaller items at the end were done in M4's step 1 (m4-architecture.md §10.1).

**What happens.** A factory worker started from a build directory, not a published layout, finds no published `Litos.Kernel.Host` beside it, so `KernelHostLocator.Resolve()` (in `Litos.Kernel`) runs `dotnet build src/Litos.Kernel.Host -c Release` synchronously, then launches the result with `dotnet exec`. It does this whenever a kernel starts: once at worker start-up (the PTC probe in `Litos.Hosting`) and again for every session's kernel.

**Two defects.**

1. **A worker builds while it starts.** When the build has anything to copy and another run's kernel is running from `src/Litos.Kernel.Host/bin/Release`, the files are locked: MSBuild retries ten times a second apart and fails. Together with the build, that is longer than the host's 30 s for the worker to report its port, so the run is blocked with "The worker did not report its port within 30 seconds" (F4, three times). Any Release build of the solution while runs are live sets this up, because it refreshes the kernel's dependencies.
2. **A failing build hangs instead of failing.** `BuildAndLocateDll` redirects the build's output but calls `WaitForExit()` before reading it. A failing build's locked-file warnings (about 8 KB) fill the pipe, so the build blocks writing and the worker blocks waiting: R3's review session waited 12 minutes on one kernel start, on files its own earlier kernel held.

**To do.**

- A factory worker must not build anything when it starts. Ship `Litos.Kernel.Host` beside the worker (a build step of the worker, or a path the host passes), or have the host build it once at its own start-up and tell each worker where it is.
- `BuildAndLocateDll`, where a build is still needed (local development of other faces), reads stdout and stderr while it waits (or discards them), and has a timeout, so a failing build fails with its message.
- A test that a failing build with a large output ends with an error rather than hanging.

**Built (2026-10-16).**

- **The kernel ships beside the worker.** `Litos.SoftwareFactory.Worker` references `Litos.Kernel.Host`, so every worker build puts `Litos.Kernel.Host.exe` and its dependencies in the worker's own output, where `KernelHostLocator.Resolve()` finds it before considering a build. Checked by starting a worker from `bin/Debug`: it reported its port and `src/Litos.Kernel.Host/bin/Release` was not touched. A test reads the worker's `deps.json`, so dropping the reference fails it. Release builds of the solution no longer collide with live runs, whose kernels now run from the worker's folder. A Debug rebuild of the worker while runs are live fails on locked files, as it already did for the worker's own DLLs: the failure is the developer's build, not a run.
- **`KernelHostLocator.RunBuild`** reads both streams while the build runs, passes `-nodeReuse:false` (a reused MSBuild node would hold the pipes open), stops the build after 5 minutes, and puts the last 4,000 characters of each stream in the error (MSBuild reports errors on stdout, which was never read). Tests: a failing command writing far more than a pipe holds ends with its output tail, a command past its timeout is stopped and reported.

**Also from the M3 check, smaller.** A tool denied by the MCP settings reaches the agent as a C# compile error ("the name … does not exist"), indistinguishable from a typo; a message naming the policy would be clearer. And agents repeatedly guess the argument shapes of the kernel's helpers (`skill("name", "tdd")`, `search_code("pattern", …)`, MCP tools' `request` object), costing calls in most runs: their descriptions should show a call.

## 13. Follow-up: skills chosen for the task, kept in the system prompt

Found by the M3 check (§9.1): skills in the library are used only when a task's brief names them, and agents ignore them otherwise. On 2026-10-14 the first answer was a phase per skill, set by the factory, with the skill's text put in each turn's brief. **Replaced on 2026-10-16, before it was built**, by the user's design below, which is built.

**Why not name skills in the run brief.** A brief that names a skill breaks quietly once the skill is gone: the agent's `skill("tdd")` returns "not found" and it carries on without it, and with an empty library there is no `skill` tool at all, so the call fails as a C# compile error, the same message as a denied MCP tool. Skills are configuration an Admin may change or delete at any time; nothing that runs should depend on one being there.

**Why the system prompt, not the brief.** A brief is a user message. Compaction runs inside a turn (`AgentLoop`), so on a long implement turn, the kind skills matter most for, it summarises the brief and the skill's text with it. The system prompt is rebuilt for every turn by `ISystemPromptProvider` and is never part of the transcript, so compaction cannot touch it. It is also the same text on every call of a run, so the provider caches it and the task is charged mostly for cache reads.

**Decisions (2026-10-16, the user's):**

- Skills are chosen for a task when it is created, from the factory library. New thread starts with the task type's defaults ticked; the person can untick them or tick others.
- An Admin sets those defaults per task type on the Skills tab ("New tasks start with it chosen").
- Every session of the task gets them: chat, spec, implement, repair and review.
- They can be changed later. A change made while the task works is applied after it is paused and resumed, and the thread says so.
- A task's skills together are at most 12,000 characters of instructions.

**Built (2026-10-16).**

- **Choosing.** `TaskThread.SkillsJson` holds the chosen names (migration `M3TaskSkills`, `[]` for older threads). `POST /api/threads` takes `skills`; left out, it takes the type's defaults (`TaskSkillSelection.DefaultsFor`). `TaskSkillSelection.Choose` refuses a name that is not an enabled library skill, a name given twice, or a total over 12,000 characters (the trimmed body, what the prompt carries). `GET /api/settings` lists the skills a task can take, with each one's size and defaults, for New thread. `LibrarySkill.DefaultFor` is validated on save: known task types only, and each type's defaults must fit in a task together.
- **Changing.** `POST /api/threads/{id}/skills` can be used by anyone on the project, as with the budget. The store records `thread.skills` in the audit trail and posts a status note naming when the change applies (`TaskSkillSelection.ChangeNote`): running, "after the task is paused and resumed"; stopped (paused, waiting on a decision, blocked, interrupted), "when the task resumes"; queued, "when the task starts"; otherwise "from the task's next run". An unchanged choice records nothing.
- **What a run is given.** `RunCapabilities.TaskSkills` holds each chosen skill's name and text, resolved from the library when the run starts (`TaskSkillSelection.Resolve`). It is the one part of the snapshot a resumed run takes afresh: if the thread's skills (or their text) differ from what the run held, the snapshot is updated and the thread is told "it now continues with: …". A rework takes them from the thread too. A skill deleted or turned off since it was chosen is left out and named in a note, as is one that no longer fits after a skill grew; nothing fails. Chat and spec workers get the thread's skills as they are now.
- **In the worker.** `RunSkills.TaskSkills` carries them in the run's skills file. `FactorySystemPromptProvider` wraps the engine's provider and puts "Skills for this task", with each skill's full text, at the head of the skills section of every turn's system prompt, so the context breakdown counts them as skills. Chosen skills are left out of the loadable catalog under either name, including a repository skill of the same name. The `skill` tool is offered only when there are other skills to load.
- **In the app.** New thread has the picker with a running total against 12,000 characters. The thread header shows the task's skills, with Change; while the task works, the editor says the change waits for a pause and resume. Run details list the task skills (each one's text on hover). The Skills tab has the per-type defaults.
- **Tests.** Core: `TaskSkillSelectionTests` (defaults, choosing, limit, resolving, notes). Store (SQLite and PostgreSQL): the change is audited and noted, and an unchanged one is not. Host: New thread defaults and refusals, a run given its skills whole, a change while running applied only after pause and resume, a member's change audited, chat given the skills. Worker: the section in every session's prompt, the catalog without chosen skills, no skill tool with only chosen skills. Web: `TaskSkills.test.tsx`.

**Checked on J3 (2026-10-16).** A feature task on forms-rules, "conditional required fields" (`requiredIf`), whose brief names no skill and says nothing of tests first. Run on `anthropic` / `claude-haiku-5-5`, created with `tdd` chosen; `prove-it-works` added while it worked. Accepted and merged (PR #4), 521,370 tokens in all.

- **The rules held.** The change while working waited, and the thread said so; the decision answer resumed the run, the snapshot then held both skills and the thread said "it now continues with: tdd, prove-it-works", before any implementation. A rework took both from the thread.
- **The skills reached the model.** Unprompted, the agent planned "I'll write the failing tests first … and confirm they fail before the implementation exists"; on the rework it reasoned "the feedback asked for a coverage case, not a bug fix", the then `tdd` text's own scope. Nothing else in its context said either.
- **But the texts fell short.** `tdd` was written for bug fixes: the agent ran its new tests before the code, saw "exited with code 1" (a compile error, the right failure), called it a shell error without reading it, then wrote all the code at once and reported no failing-first evidence. `prove-it-works` did not stop a false claim on the rework: an edit anchored on `\n` in a CRLF file matched nothing, the agent said "the test is in place" although the test count stayed at 31, and submitted. A full review found the test missing; a repair added it.
- **A factory gap, now fixed.** That submit had changed nothing, yet it was verified and then fully reviewed (41,018 tokens) under "the diff could not be read". Since prompt revision m2.4, work submitted while the run has no change (nothing differs from the base commit, or from a rework's handoff) is sent back once with a brief saying so and asking for the edits to be read back (`BriefKind.EmptySubmission`); submitted again unchanged, it is taken as meant and verified, and the review planner skips the review of an empty diff ("There is no change to review: the diff is empty."). "Could not be read" is kept for a diff with text but no files found.
- **Texts revised (skill library revision 2).** `tdd` now covers new behaviour as well as bugs, one rule at a time, and says to read the failing output before believing it. `prove-it-works` now starts with reading edits back and checking the test count went up, and asks for one concrete example of the new behaviour in the summary.

**Not done.** Measure the effect with an A/B run: the same task twice on one model, once with skills chosen and once without, comparing tests written first, the fix's scope and tokens.

## 14. Proposed: files and images as task input

Asked for by the user on 2026-10-14. Researched and recorded only; nothing is built.

**Today.** A task takes text only. A message is `PostMessageRequest(MessageId, Text)` (`FactoryApi.cs`); the factory has no upload, storage or attachment UI. Every brief reaches the worker as one string (`RunExecutor` calls `client.RunTurnAsync(sessionId, kind, brief, …)`), and the worker's `/sessions/{id}/turns` uses `Litos.Hosting`'s default request reader, which turns the input into one `TextBlock`. "Attach" and "files" were left in VsCodeHost in M1 (m1-architecture.md, "What stays in VsCodeHost"). Committing a file to the repository and naming its path works for text, but not for an image: a tool result is text only (`ToolResultBlock(CallId, Text)`), so an agent that reads a PNG from disk never sees it.

**What already exists to build on.**

- **The engine carries images.** `ImageBlock(MediaType, Data)` is a content block, compaction estimates it (`EstimatedImageChars`), and Api, Gui, Console, Telegram and VsCodeHost all send images this way.
- **Documents convert to Markdown.** `Litos.Tools.Attachments` (`IAttachmentConverter`, MarkItDown) handles PDF, Word, PowerPoint, Excel, EPUB and mail, and passes plain text through. `PlainTextMedia` refuses unknown binaries, `ImageMedia` recognises images by extension, and `UntrustedContent.Wrap` fences converted content as untrusted. `AttachmentContentBuilder` (Api) and `AttachEndpoints` (VsCodeHost) are two working callers, both with a 20 MB cap.
- **The worker's turn reader is pluggable.** `Litos.Hosting`'s turns endpoint takes a `readTurnRequest`; VsCodeHost passes its own so a turn's content can be `[TextBlock, …attachments]`. The factory worker can do the same.
- **`.factory/` is already outside every commit.** Every working copy lists `/.factory/` in `.git/info/exclude` (§8), so files placed there never reach a diff.

**Proposed.**

1. **Stored per thread, by the host.** A new `thread_attachments` table: id, thread, project, the message it came with, file name, media type, size, SHA-256, uploader and time. The bytes go to `<data>/attachments/<threadId>/<id>`, not the database and not `wwwroot`. Uploading is `POST /api/threads/{id}/attachments` (multipart, one file), which returns an id. `PostMessageRequest` gains `AttachmentIds`, and `GET /api/attachments/{id}` downloads a file under the same membership rule as threads (a 404 outside the user's projects). Downloads are sent with `Content-Disposition: attachment` and `nosniff`.
2. **Limits, in settings (§3).** Allowed types: images (PNG, JPEG, GIF, WebP), the documents MarkItDown converts, and plain text by extension, with unknown binaries refused as `PlainTextMedia` does. Proposed defaults: 20 MB per file, matching the other faces; 5 files per message; 50 MB per thread. Images over a size such as 1,568 px on the long side are scaled down before they are sent, since providers resize them anyway and charge for what they receive.
3. **Into the run.** At preflight the host copies the thread's attachments into the working copy's `.factory/attachments/`. The worker gets them as files, so the agent can open a CSV sample, or copy an icon to where the task wants it and commit it there. The brief gains an **Attachments** section that lists each file by path, with the message it came with:
   - **Documents** are converted to Markdown and included in the brief, wrapped as untrusted, up to a cap of about 50,000 characters each. Beyond the cap the brief says so and points to the file.
   - **Images** go into the first turn as `ImageBlock`s, after the brief's text. A factory turn reader takes `attachments: [{ path, mediaType }]`, relative to the working copy, and reads the bytes on the worker's side, so no base64 crosses the host-to-worker HTTP call.
4. **Which runs see them.** Implement, rework, spec and chat runs get every attachment on the thread up to the run's start, since a thread is one change. A rework's brief marks the attachments that came with the change request (a screenshot of what is wrong) as new. Review turns get none in V1: they judge the diff.
5. **Budget.** An image is charged by the provider's own count where it reports one. Until the run's first call reports, the reservation estimate adds a fixed allowance per image, as compaction does, so the budget check (§5) stays honest. Converted documents count as text.
6. **Models that cannot see images.** The catalog (§4.3) does not record whether a model accepts images. Add `acceptsImages` to each allowed model, filled from the catalog where the provider says (OpenRouter lists input modalities) and editable by an Admin. A thread on a model without it gets each image as a file and a line in the brief, not an `ImageBlock`, and the composer says so when an image is attached.
7. **In the app.** The composer gets an attach button, paste and drag-and-drop, and shows each pending file as a chip that can be removed. Files upload when they are added and are sent with the message by id. A message shows its attachments: thumbnails for images and a chip for anything else, each downloadable. The run details list what the run was given.

**Not in V1.** A tool that lets the agent look at an image again after compaction: it needs image tool results in the engine, which `ToolResultBlock` cannot carry today. Also out: attachments on a follow-up to a running task, since steering a running turn takes text only (`SteerAsync`) and Api's `AgentWorker` notes that it drops `ImageBlock`s; such a message is refused with a reason. Also out: virus scanning, storage outside the data directory, and deleting attachments, since threads are never deleted.

**To verify first, before any UI.** Check that an `ImageBlock` survives the whole factory path (worker, `ModelGateway`, provider) on each strict provider, and what each one reports for it. The other faces call providers directly; the factory's gateway has never carried an image. Then measure what one screenshot costs on the default model, to set the per-image allowance.

**Build order, when picked up.** (1) The worker's turn reader, gateway pass-through and the budget allowance, tested with a fixture image. (2) The store, migration, upload, download and limits. (3) Copying into the working copy, the brief's Attachments section (a prompt revision) and the runs that receive them. (4) The composer and message display. (5) A check: a UI bug fixed from a screenshot alone, and a feature built from an attached CSV sample and a PDF note.

**Open when it is picked up:** the limits above; whether a member's upload needs any Admin switch to turn attachments on; and whether review turns should see images for UI tasks.
