# Litos Software Factory: installing on Windows, macOS and Linux

Status: proposal, 2026-10-19. Nothing here is built yet. This evaluates whether the factory can run on all three platforms and how a simple installer would work on each, and lists the code changes needed first.

Decisions this plan follows (agreed 2026-10-19):

- **Desktop first, server second.** The usual install is one person running the factory for themselves on `127.0.0.1`. The same package can also run as a background service on a shared machine (§8).
- **Native host and worker, PostgreSQL in Docker.** The host, worker and kernel are native self-contained binaries that use the toolchains already on the machine (git, dotnet, node, python, java, go). Only the factory's state database runs in a container.
- **Signed releases.** An Apple Developer ID for macOS (signed and notarized) and a Windows code-signing certificate.

---

## 1. Verdict

**Feasible on all three platforms, with a modest amount of work.** The factory code is already mostly platform-neutral. Every process-, path- and shell-level decision checks `OperatingSystem.IsWindows()` and has a Unix branch. What is missing is mostly packaging: a cross-platform setup/control program, publish scripts for the host, three thin installers, and a short list of real bugs that only show up off Windows (§3.2).

The main design idea: **one small control program, `litos-factory`, does all the real work on every OS** (prerequisite checks, generating secrets, starting Postgres, migrations, registering the background service, upgrades, backups). The per-OS installers only copy files and call `litos-factory setup`. That keeps the three installers thin and puts the logic in one tested C# codebase, not in three sets of shell, PowerShell and installer-script logic.

| Platform | Installer | Background service (desktop) | Docker |
|---|---|---|---|
| Windows 10/11 x64 | Signed MSI (WiX), also on winget | Per-user Scheduled Task at logon | Docker Desktop (WSL 2), or Rancher Desktop |
| macOS 13+ arm64 / x64 | Signed and notarized `.pkg`, also a Homebrew cask | Per-user LaunchAgent | Docker Desktop, OrbStack, Colima or Rancher Desktop |
| Linux x64 / arm64 | `.deb` and `.rpm` (nfpm), plus a `tar.gz` with `install.sh` | systemd **user** unit | Docker Engine with the compose plugin |

What the person installing it sees, on every OS:

1. Install Docker if it isn't installed (the installer checks and links to it).
2. Run the installer. No questions beyond the usual install-location page.
3. The browser opens on `http://127.0.0.1:5180` with the admin sign-in. The first admin password is shown once (§4.4).
4. Provider keys, the GitHub token, MCP servers and the rest are set in the app's Settings, as they are today (M3: settings live in the DB). The installer doesn't ask for any of them.

---

## 2. What has to be installed

```
 ┌──────────────── install folder (read-only, replaced on upgrade) ────────────────┐
 │  litos-factory                  control CLI (setup/start/stop/open/backup/…)     │
 │  Litos.SoftwareFactory.Host     API + SPA + gateway + coordinator, :5180         │
 │  Litos.SoftwareFactory.Worker   one process per run, started by the host         │
 │  Litos.Kernel.Host              PTC kernel, started by the worker                │
 │  wwwroot/                       the React build                                  │
 │  db/compose.yml, db/init-roles.sh                                                │
 └──────────────────────────────────────────────────────────────────────────────────┘
 ┌──────────── config folder (user-only permissions, kept on upgrade) ─────────────┐
 │  factory.env       connection strings, DB role passwords, admin seed, ports     │
 └──────────────────────────────────────────────────────────────────────────────────┘
 ┌──────────── data folder (kept on upgrade and, by default, on uninstall) ────────┐
 │  keys/ workspaces/ reading/ runs/ venvs/ skills/ baselines/ logs/                │
 └──────────────────────────────────────────────────────────────────────────────────┘
 ┌──────────── Docker ─────────────────────────────────────────────────────────────┐
 │  compose project "litos-factory": postgres:18 on 127.0.0.1:5433,                 │
 │  named volume litos-factory_state                                                │
 └──────────────────────────────────────────────────────────────────────────────────┘
 Prerequisites found on PATH: git (required); dotnet / node+npm / python3 / java+mvn / go
 (only the ones the user's projects need — checked at project registration, as today)
```

The binary layout matters. `WorkerLocator` (`src/Litos.SoftwareFactory.Host/FactoryHostApp.cs`) looks for the worker beside the host. `KernelHostLocator` (`src/Litos.Kernel/KernelHostLocator.cs`) looks for the kernel beside the worker. So all three must go in one folder, as `deploy/publish-vscodehost-macos.sh` already does for VsCodeHost and the kernel.

### Per-OS locations

| | Install folder | Config folder | Data folder |
|---|---|---|---|
| Windows | `%LOCALAPPDATA%\Programs\LitosSoftwareFactory` | `%APPDATA%\LitosSoftwareFactory` | `%LOCALAPPDATA%\LitosSoftwareFactory` (same as `new-env.py` today) |
| macOS | `/Applications/Litos Software Factory.app/Contents/MacOS` (binaries inside the bundle) | `~/Library/Application Support/LitosSoftwareFactory/config` | `~/Library/Application Support/LitosSoftwareFactory/data` |
| Linux | `/opt/litos-factory` (packages) or `~/.local/lib/litos-factory` (tarball) | `$XDG_CONFIG_HOME/litos-factory` | `$XDG_DATA_HOME/litos-factory` |

All three are per-user in desktop mode. That is deliberate: the factory runs as the person who owns the toolchains, the git credentials and the Docker context (§5.2 explains why this matters on Windows).

---

## 3. Readiness today

### 3.1 Already portable (no change needed)

- **Worker launch and lifetime**: `LocalProcessWorkerLauncher` sets the working directory and passes arguments as an `ArgumentList`. `ParentProcessWatcher` polls the parent PID, which works on every OS.
- **Shell**: `ShellTool` uses `cmd.exe /c` on Windows and the user's login shell elsewhere, picking up the login shell's PATH (`ResolveLoginShellPath`).
- **Shell guard**: `ShellCommandGuard` blocks `taskkill /IM`, `Stop-Process -Name`, and also `pkill`, `killall` and `kill -1`.
- **Executable resolution**: `ProcessRunner` uses PATHEXT on Windows and a plain PATH search elsewhere. `ProfileExecutables.Find` matches it.
- **Paths**: case-insensitive comparisons only on Windows (`GitWorkspace`, `WorkingCopyGuard`). venvs use `Scripts` vs `bin` (`VerificationProfile.BinOf`).
- **Kernel on macOS**: the kernel's single-file publish, hardened-runtime signing and `deploy/entitlements.plist` were already worked out for VsCodeHost (see the header of `deploy/publish-vscodehost-macos.sh`). The same entitlements apply to the factory's binaries.
- **Postgres**: the `postgres:18` image is multi-arch (amd64 and arm64), so the same compose file works on Apple Silicon and ARM Linux.
- **Key ring**: Data Protection keys are files in `<data>/keys` (`SecretProtector.cs`), with no DPAPI or Keychain dependency.

### 3.2 Gaps found (must fix before shipping installers)

| # | Gap | Where | Effect off Windows (or under a service) |
|---|---|---|---|
| G1 | The Python preset's prepare step runs `python -m venv` | `Infrastructure/Verification/Presets/python.json` | Stock macOS and Ubuntu/Debian have only `python3`, so registering a Python project fails the PATH check. **Real bug.** |
| G2 | The host's content root is the current directory (`WebApplication.CreateBuilder(args)` with no `ContentRootPath`) | `FactoryHostApp.Build` | A host started by launchd, systemd or Task Scheduler with a different working directory doesn't find `wwwroot`: the API works, the SPA returns 404. Affects Windows too. |
| G3 | No publish scripts for the factory, only for VsCodeHost and Console | `deploy/` | Nothing to package. The host has never been published for any RID; the only build output is win-x64 VsCodeHost/kernel. |
| G4 | Backup and restore are PowerShell scripts (`factory-backup.ps1`, `factory-restore.ps1`, `backup-common.ps1`) | `deploy/` | Need `pwsh` on macOS/Linux. They also find the container by its directory-derived name (`litosaiagent10-factory-db-1`). |
| G5 | `new-env.py` needs Python, writes to the repo root, and uses `~/.local/share` on macOS | `deploy/factory/new-env.py` | An installer can't depend on a Python install. Wrong location on macOS. |
| G6 | Verification commands inherit the **host's** PATH (`ToolchainEnvironment.CurrentHostEnvironment`) | `Infrastructure/Processes/ToolchainEnvironment.cs` | launchd and systemd services start with a minimal PATH (`/usr/bin:/bin:…`), so Homebrew's `/opt/homebrew/bin`, `~/.dotnet`, nvm/pyenv/sdkman shims, and `~/go/bin` are missing. On Windows, a PATH change after login (like the Maven install on 2026-10-13) isn't seen until the task restarts. The agent's shell is fine, because it reads the login shell PATH; verification isn't. |
| G7 | Compose project name comes from the folder (`litosaiagent10`), and `init-roles.sh` is mounted from the repo | `compose.factory-state.yml` | Container and volume names would change with the install folder. Needs a fixed `name: litos-factory`, with the init script shipped in the install folder. |
| G8 | Native libraries untested off Windows: MarkItDown (attachments), SkiaSharp/pdfium in `Litos.Tools` | `Litos.Host` → `Litos.Tools` | Attachments may fail at runtime on macOS/Linux if a native asset isn't in the single-file bundle. Needs a test, not necessarily a fix. |
| G9 | No CI for the factory on any OS | `.github/workflows/` | The Core, Infrastructure, Host and Worker test suites have only ever run on Windows. Case-sensitive paths, `/` vs `\` in reports, and file locking all behave differently on Unix. |
| G10 | Docker resolution trap: an empty `C:\WINDOWS\system32\docker` file shadows `docker.exe` for PowerShell (found 2026-10-17) | machine-specific | `litos-factory` must look for `docker` itself and ignore files that aren't executable, as `backup-common.ps1` already does. |
| G11 | Linux: self-contained .NET apps need ICU unless they use invariant globalization | the host/worker csprojs | The host and worker don't set `InvariantGlobalization` (only the kernel does). Either declare `libicu` as a package dependency or decide invariant mode is fine for the host. Keep ICU: model text and user names are not ASCII. |

None of these are design problems. G1, G2, G5, G6 and G7 are small. G3 and G4 are packaging work. G8 and G9 are verification work.

---

## 4. Common design (all platforms)

### 4.1 `litos-factory`: the control CLI

A new small project, `src/Litos.SoftwareFactory.Setup` (published self-contained, single-file, named `litos-factory`). It references `Litos.SoftwareFactory.Infrastructure` for the backup/manifest code (`FactoryBackup`) and process running. It never references the host itself; it starts the host as a process.

| Command | What it does |
|---|---|
| `setup` | First run. Idempotent. Steps in §4.3. |
| `start` | Waits for Docker → `compose up -d --wait` → runs host `--migrate` → starts the host in the background. Used by the service definition. |
| `run` | Same as `start` but stays in the foreground (the service actually runs this). It supervises the host and restarts it if it crashes. |
| `stop` | Stops the host gracefully: dispatch pause, then a stop signal. Leaves Postgres running unless `--all`. |
| `open` | Starts the factory if it isn't running, waits for `/healthz`, then opens the browser. The Start-menu / Launchpad / desktop-entry shortcut runs this. |
| `status` | Host up? DB up? Version, ports, data folder, and toolchains found. |
| `doctor` | Full check: Docker reachable, ports free, `git` present, toolchains, PATH snapshot age, disk space, schema version vs app version. Prints fixes. |
| `refresh-path` | Re-captures the user's login-shell PATH into the config (G6). |
| `backup <folder>` / `restore <folder>` | A C# port of `factory-backup.ps1`/`factory-restore.ps1`, using `docker exec … pg_dump` and the host's `--backup-data` / `--verify-backup` verbs (G4). |
| `upgrade-db` | Runs migrations explicitly (also done by `start`). |
| `uninstall [--purge]` | Removes the service registration. With `--purge`, also removes the container, the volume, the config and the data. Without it, keeps them and says where they are. |

The installers call `setup` on install and `uninstall` on removal. Everything else is for the user and the service definitions.

### 4.2 Why a CLI, and not logic in each installer

- MSI custom actions, macOS `postinstall` scripts and `.deb` maintainer scripts are each hard to test and behave differently. One C# program can be unit-tested like the rest of the factory.
- Backup, restore, PATH capture and Docker checks are needed after install too, not only during it.
- The same commands are what a team-server admin uses over SSH (§8).

### 4.3 First-run sequence (`litos-factory setup`)

1. **Preflight.** Find `docker` (G10). Check `docker info` succeeds (daemon running, user allowed) and that `docker compose version` is v2 or later. Check `git --version`. Check ports 5180 and 5433 are free, or pick the next free ones and record them. On failure, print one clear sentence and a link (install Docker, start Docker Desktop, add yourself to the `docker` group, …) and exit non-zero. The installer shows that text.
2. **Config.** If `factory.env` doesn't exist, generate it (a C# port of `new-env.py`): four random passwords, both connection strings, `FACTORY_DATA_DIR`, `ASPNETCORE_URLS`, the admin user. Restrict the file to its owner (`chmod 600`; on Windows, an ACL that removes inherited access for anyone but the user). **Never overwrite an existing file**: the database keeps its first passwords, as `new-env.py`'s header warns.
3. **PATH snapshot** (macOS/Linux): run `$SHELL -l -i -c 'printf %s "$PATH"'` with a timeout and save the result as `FACTORY_TOOLCHAIN_PATH` in `factory.env` (G6). On Windows, read the Machine and User PATH from the registry the same way the 2026-10-13 `freshenv.sh` did.
4. **Database.** `docker compose -p litos-factory -f <install>/db/compose.yml --env-file <config>/factory.env up -d --wait`. On first creation of the volume, `init-roles.sh` creates the roles.
5. **Migrate.** `Litos.SoftwareFactory.Host --env-file <config>/factory.env --migrate`.
6. **Register the background service** (per-OS: §5.3, §6.3, §7.3) and start it.
7. **Wait for health**, then open the browser and show the first sign-in (§4.4).

Every step is safe to re-run. Re-running `setup` repairs a half-finished install.

### 4.4 The first admin password

The host already seeds the admin from `FACTORY_ADMIN_USER`/`FACTORY_ADMIN_PASSWORD` on its first start. Setup generates the password, so the installer needs no password dialog (which `.pkg` and `.deb` can't show anyway). It then:

- shows it once: printed by `setup`, and on the desktop also written to `<config>/first-sign-in.txt` (owner-only), which `open` displays until the admin has signed in once;
- **small host change (recommended):** mark the seeded admin "must change password", so the generated password works once only. After the first change, `open` deletes `first-sign-in.txt`. `FACTORY_ADMIN_PASSWORD` can stay in `factory.env` because it only seeds an empty user table.

### 4.5 Start-up order and Docker timing

At login, Docker Desktop, OrbStack and Colima start *after* the user's login items, sometimes a minute later. `litos-factory run` therefore:

1. retries `docker info` with backoff for up to 5 minutes, logging "waiting for Docker";
2. starts Postgres (`compose up -d --wait`). `restart: unless-stopped` already brings it back with the daemon, so this is usually a no-op;
3. migrates, then starts the host and supervises it.

If the user quits Docker while the factory runs, the host's store calls fail (scenario 20, `DatabaseLossTests`, covers that runs stop safely). `run` notices the DB is down, waits for Docker, and restarts the host.

### 4.6 Upgrades

The installer replaces the install folder only. Config, data and the Docker volume stay. Next `start`:

- runs `--migrate`. M4 §24: upgrades go through the migration role before dispatch starts. The host already refuses to start on a schema/app mismatch;
- if `db/compose.yml` changed the Postgres **minor** image, `compose up` recreates the container on the same volume (safe);
- a Postgres **major** upgrade (18 → 19) is never automatic. `doctor` reports it, and the release notes say: `litos-factory backup`, upgrade, `restore`.

Before replacing binaries, the installer calls `litos-factory stop`. A running worker or kernel locks its files on Windows, which the redeploy notes have hit repeatedly. Stop pauses dispatch first, so no run is cut off mid-step. Runs that are still active come back as Interrupted, which recovery already handles (§16 of the V1 readme).

### 4.7 Toolchains

The installer does **not** install dotnet/node/python/java/go. The factory is stack-agnostic, and the user's projects decide what's needed. This matches how registration already works: `ProfileExecutables.Missing` checks the profile's commands at registration and says what's missing. `doctor` and `status` list what's found. The docs page gives one install line per OS per toolchain (winget, Homebrew, apt/dnf).

`git` is the one hard requirement (`GitWorkspace` shells out to it). On macOS, `xcode-select --install` provides it. The macOS installer checks and offers it.

### 4.8 Packaging the binaries

Publish host, worker and kernel **self-contained** for each RID (`win-x64`, `osx-arm64`, `osx-x64`, `linux-x64`, `linux-arm64`), each as a **single file**, into one folder. This is the VsCodeHost + kernel arrangement, which avoids two apps' `.deps.json` files fighting over shared DLLs in one folder. Notes:

- `IncludeNativeLibrariesForSelfExtract=true` for the host (MarkItDown, Skia, pdfium natives; G8).
- `EnableCompressionInSingleFile=false` on macOS, as the VsCodeHost script learned the hard way.
- The SPA is built once (`npm ci && npm run build` in `Litos.SoftwareFactory.Web`) and copied as `wwwroot/` beside the host. The host must use `AppContext.BaseDirectory` as its content root (G2).
- Three self-contained runtimes make the package large (likely a few hundred MB unpacked; measure in step 2 of §11). If that matters later, publish framework-dependent against one bundled shared runtime folder. Not worth it for V1.

---

## 5. Windows

### 5.1 Installer

- **A signed MSI built with the WiX Toolset**, per-user scope (`InstallScope="perUser"`, no UAC prompt), installing to `%LOCALAPPDATA%\Programs\LitosSoftwareFactory`.
- UI: Welcome → License → Install → Finish with "Open Litos Software Factory" ticked. No other pages.
- A deferred custom action runs `litos-factory setup --from-installer`. If preflight fails (no Docker, Docker not running), the MSI shows the message and a link instead of failing with an error code. The files stay installed, and the Start-menu shortcut re-runs `setup` until it succeeds.
- **Docker missing:** the message offers `winget install Docker.DockerDesktop` (needs WSL 2 and one reboot) and mentions Rancher Desktop as the no-licence-fee alternative. Docker Desktop needs a paid subscription in larger companies; the doc and the message say so.
- Start menu: **Litos Software Factory** (`litos-factory open`) and **Litos Software Factory – Status** (`litos-factory status`, opens a console).
- Uninstall (Apps & Features) runs `litos-factory uninstall` (keeps data). A checkbox in the uninstall UI or `msiexec … PURGE=1` runs `--purge`.
- Also publish a **winget manifest** pointing at the MSI, so a terminal install is `winget install Litos.SoftwareFactory`.

Inno Setup is a reasonable alternative (simpler scripting, EXE installer). MSI is preferred because winget, Intune and GPO all handle MSI natively, and per-user MSI avoids admin rights.

### 5.2 Why not a Windows Service on the desktop

A Windows Service runs under LocalSystem or a service account. On a developer desktop that account:

- doesn't have the user's PATH. Maven on this machine is on the **User** PATH only;
- can't reach Docker Desktop's engine unless it's in `docker-users`, and Docker Desktop itself only runs while the user is logged in;
- doesn't have the user's git credential helper or SSH keys (the factory uses its own token for GitHub, but MCP servers and toolchains may rely on the user's profile).

So on the desktop the factory runs **as the logged-in user**.

### 5.3 Background start

A per-user **Scheduled Task** "Litos Software Factory", created by `setup` with `schtasks`, or better through the Task Scheduler COM API from C#:

- trigger: at logon of this user, with a 30 s delay;
- action: `litos-factory.exe run`, hidden window (the CLI is built as a Windows GUI-subsystem app when started with `run`, or wrapped with a small `litos-factory-w.exe`, so no console flashes);
- settings: restart on failure (3 times, 1 minute apart), no stop-after-time limit, run only when the user is logged on.

Logs go to `%LOCALAPPDATA%\LitosSoftwareFactory\logs\host-YYYYMMDD.log`, because a hidden process's stdout/stderr is otherwise lost.

### 5.4 Signing

- `signtool sign /fd sha256 /tr <timestamp-url> /td sha256` on every `.exe` (host, worker, kernel, litos-factory) and on the MSI. Azure Trusted Signing can replace a certificate file in CI.
- Signed binaries also reduce Defender/SmartScreen friction when the worker starts the kernel, and the kernel compiles code with Roslyn at runtime.

### 5.5 Windows-specific checks

- `docker` resolution (G10).
- Long paths: workspaces under `%LOCALAPPDATA%` plus `node_modules` can pass 260 characters. `setup` sets `git config --global core.longpaths true` only if the user agrees. Better: the factory passes `-c core.longpaths=true` on its own git calls. `doctor` warns if `LongPathsEnabled` is 0.
- Defender real-time scanning slows `npm install` and `dotnet build` in workspaces a lot. `doctor` suggests (doesn't apply) a Defender exclusion for the data folder.

---

## 6. macOS

### 6.1 Installer

- **A signed and notarized `.pkg`** per architecture (`osx-arm64`, `osx-x64`), built with `pkgbuild` + `productbuild`, signed with a *Developer ID Installer* certificate, then `notarytool submit --wait` and `stapler staple`. A universal binary isn't practical for .NET single-file builds, so ship two packages. The download page and the Homebrew cask pick the right one.
- The payload is `/Applications/Litos Software Factory.app`. The bundle exists so the user has something to click and something to drag to the Trash. Its main executable is `litos-factory` (launched as `open`), and host, worker and kernel sit beside it in `Contents/MacOS`. Every Mach-O binary is signed with the *Developer ID Application* identity, hardened runtime, and `deploy/entitlements.plist` (JIT and unsigned executable memory, required by CoreCLR and Roslyn scripting).
- The `postinstall` script runs as root, so it finds the console user (`stat -f %Su /dev/console`) and runs `sudo -u <user> "/Applications/Litos Software Factory.app/Contents/MacOS/litos-factory" setup --from-installer`. If preflight fails, it opens a short HTML page in the browser with the reason and the fix. The `.pkg` installer can't show a custom dialog.
- **Homebrew:** a cask (`brew install --cask litos-software-factory`) that installs the same notarized app and runs `setup` in its postflight. This is the easiest install for developers who already use Homebrew.
- Uninstall: a `litos-factory uninstall` command, an "Uninstall…" item reachable from `litos-factory status`, and `brew uninstall --cask` (with `zap` for `--purge`).

### 6.2 Docker on macOS

All of Docker Desktop, OrbStack, Colima and Rancher Desktop provide a `docker` CLI and compose v2. Preflight only needs `docker info` to work. The context may point to a non-default socket (`~/.colima/default/docker.sock`, `~/.orbstack/run/docker.sock`): `setup` records `docker context show` and the resolved `DOCKER_HOST` into the config, so the LaunchAgent (which doesn't read the user's shell profile) reaches the same engine.

### 6.3 Background start

A **LaunchAgent** at `~/Library/LaunchAgents/com.litos.softwarefactory.plist`, loaded with `launchctl bootstrap gui/$(id -u)`:

- `ProgramArguments`: `…/litos-factory run`;
- `RunAtLoad` true, `KeepAlive` `{ SuccessfulExit: false }` (restart after a crash, not after a clean stop);
- `EnvironmentVariables`: `PATH` = the captured login-shell PATH (G6), plus `DOCKER_HOST` if one was recorded;
- `StandardOutPath`/`StandardErrorPath` → `<data>/logs/`;
- `ProcessType` `Interactive` (dev tools and builds shouldn't be throttled as background work).

`litos-factory refresh-path` rewrites the plist and reloads the agent. `doctor` warns when a toolchain the user's projects use isn't on the captured PATH.

### 6.4 macOS-specific checks

- **Gatekeeper and quarantine.** Notarization plus stapling covers the downloaded `.pkg`. The binaries inside the app aren't quarantined after a `.pkg` install. A hand-copied tarball would need `xattr -dr com.apple.quarantine`. That's one more reason to ship only the `.pkg` and the cask.
- **Privacy prompts (TCC).** Working copies under `~/Library/Application Support` need no permission. If a user registers a local folder under `~/Documents` or `~/Desktop` (§6.2 of the V1 readme, deferred), macOS will prompt. Not an issue while the factory stays GitHub-only.
- **`git`.** If `xcode-select -p` fails, preflight offers `xcode-select --install`.
- **Python.** G1: the preset must use `python3` here.
- **Sleep.** A laptop that sleeps pauses runs. When it wakes, the workers' model calls may have timed out. Recovery already marks such runs Interrupted. `doctor` mentions `caffeinate`, and the app could hold a power assertion while runs are active (later; not V1).

---

## 7. Linux

### 7.1 Installer

Two formats, because Linux users split between "give me a package" and "give me a script":

- **`.deb` and `.rpm`, built with nfpm** from the same publish folder. They install to `/opt/litos-factory`, symlink `/usr/bin/litos-factory`, add a `.desktop` entry ("Litos Software Factory" → `litos-factory open`), and declare dependencies `git`, `libicu` (G11), `ca-certificates`, and recommend `docker-ce | docker.io`, `docker-compose-plugin`. Packages are signed with a GPG key and served from a small apt/dnf repository (GitHub Pages works), so upgrades arrive with `apt upgrade`.
- **`tar.gz` + `install.sh`** (the pattern of the existing `deploy/install.sh`): `curl -fsSL …/install-factory.sh | sh` downloads the right archive for `uname -m`, checks its SHA-256, extracts to `~/.local/lib/litos-factory`, links `~/.local/bin/litos-factory`, and runs `setup`. No root needed. For distributions without a package, and for people who don't want a repository added.

Package maintainer scripts don't run per-user setup: they run as root and there may be no logged-in user. After a package install, the `.desktop` entry's first click (or `litos-factory setup` in a terminal) does it. The package's post-install message says so in one line.

Flatpak, Snap and AppImage are **not** recommended. Flatpak and Snap sandboxes block the access the factory exists for (host toolchains, the Docker socket, arbitrary build commands). AppImage gives no service integration and has the quarantine-like "make executable" step.

### 7.2 Docker on Linux

- Docker Engine with the compose plugin (v2) is supported. Preflight checks `docker info` without `sudo`. If that fails with a permission error, the message explains the two options: add yourself to the `docker` group (root-equivalent: say so plainly) or use rootless Docker.
- **Podman** with `podman compose` may work but is not supported in V1. `doctor` reports "podman detected, untested".
- Supported distributions for V1: Ubuntu 22.04+, Debian 12+, Fedora 40+, all x64 and arm64. Anything with glibc and .NET 10's runtime prerequisites should work. Alpine (musl) is out of scope.

### 7.3 Background start

A **systemd user unit**, `~/.config/systemd/user/litos-factory.service`:

```ini
[Unit]
Description=Litos Software Factory
After=default.target

[Service]
Type=simple
ExecStart=%h/.local/bin/litos-factory run
EnvironmentFile=%h/.config/litos-factory/path.env
Restart=on-failure
RestartSec=10
KillMode=mixed
TimeoutStopSec=120

[Install]
WantedBy=default.target
```

- Enabled with `systemctl --user enable --now litos-factory`. Logs go to the journal (`journalctl --user -u litos-factory`) as well as `<data>/logs/`.
- `path.env` holds the captured login-shell PATH (G6), rewritten by `refresh-path`.
- `KillMode=mixed` + `TimeoutStopSec`: `litos-factory run` gets SIGTERM first, pauses dispatch and stops the host, then the remaining workers and kernels are killed. Without `mixed`, systemd would kill the workers at the same moment as the host.
- `loginctl enable-linger` keeps it running when the user logs out. That isn't wanted on a desktop, but is how §8's server mode works with a per-user install.

### 7.4 Linux-specific checks

- **inotify limits:** `npm`/`vite`-based projects in many working copies can exhaust `fs.inotify.max_user_watches`. `doctor` checks and suggests the sysctl.
- **Case-sensitive file systems:** the code already compares paths ordinally off Windows. CI (G9) is what proves that.
- **Python:** G1 applies (Debian/Ubuntu ship `python3` only, unless `python-is-python3` is installed).

---

## 8. Team server mode

Same package, different service definition. `litos-factory setup --server`:

- runs under a **dedicated low-privilege account** (`litos-factory`), as §17 of the V1 readme already recommends. On Linux that is a system user created by the `.deb`/`.rpm` plus a **system** systemd unit. On Windows it is a Windows Service (here a Service is right: there is no desktop session, and toolchains are installed machine-wide for that account). On macOS it is a LaunchDaemon with `UserName` set, though a macOS team server is unusual and best-effort;
- writes `ASPNETCORE_URLS=http://127.0.0.1:5180` (still loopback) and prints a **reverse proxy** example. Caddy is simplest: automatic HTTPS, one line with `flush_interval -1` for SSE, as §13.3 of the V1 readme requires buffering off. nginx is also documented (`proxy_buffering off; proxy_read_timeout 1h;`);
- keeps Postgres on `127.0.0.1:5433`, never published to the network;
- doesn't open a browser or write `first-sign-in.txt`: the admin password is printed once to the terminal that ran `setup`;
- `backup` is meant to run from cron or a systemd timer. A sample timer is shipped (disabled).

**The §17 tripwire still holds.** Server mode is for trusted colleagues only. Any untrusted user requires the per-run container launcher behind `IWorkerLauncher` first. The installer's `--server` output repeats this in one line, and the same reminder sits in `doctor`'s server checks.

A fully containerised server (host and worker in Docker too) was considered and not chosen for V1. Every target-repo toolchain would have to be baked into the image, and on Mac and Windows builds would run inside the Docker VM. It becomes the natural shape later, together with the per-run container launcher.

---

## 9. Code changes needed before installers

In priority order. Each one lands with unit tests, like every other factory change.

1. **G2: content root.** In `FactoryHostApp.Build`, use `WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory })`. Test: host started with a different current directory serves `index.html`.
2. **G1: Python executable.** Either the preset names `python3` on non-Windows (a per-OS executable form in the profile schema: `"executable": { "windows": "python", "default": "python3" }`), or the process runner gets an alias list (`python` → `python3` when `python` is missing). The per-OS form is more explicit and also helps `mvn`/`mvnw`, `gradlew`/`gradlew.bat`. It is a profile schema change (`profileVersion` 3) with a migration of the stored presets, like 7a's preset revisions.
3. **G6: toolchain PATH.** `ToolchainEnvironment` takes `FACTORY_TOOLCHAIN_PATH` when set, in place of the host's own PATH, for verification and for the worker's environment. Test: a scrubbed environment with the setting gets that PATH.
4. **G7: compose file for installs.** `deploy/factory/compose.yml` with `name: litos-factory`, the init script mounted relative to the compose file, and ports read from the env file. The dev `compose.factory-state.yml` stays for the source tree.
5. **G4/G5: the `litos-factory` CLI** (§4.1). It contains the env generator, Docker preflight, compose/migrate/start, service registration per OS, PATH capture, and backup/restore ported from the PowerShell scripts. Keep the `.ps1` scripts working for the source tree until the CLI's backup has passed the same live restore check as M4 step 7.
6. **First-sign-in password change** (§4.4). A small host change: a flag on the seeded admin, enforced by the SPA's sign-in flow.
7. **A readiness check.** `/healthz` exists (`FactoryHostApp.cs`) but always answers OK. Add a readiness form (`/healthz?ready`, or `/readyz`) that answers OK only once `PrepareAsync` has finished and the store is reachable. `open`, `run` and the installers wait on it.
8. **Log to file.** The host writes a rolling log in `<data>/logs` when started by `litos-factory run` (a `--log-dir` argument). The worker's stderr is already captured per run.

---

## 10. Build and release pipeline

A new workflow, `.github/workflows/release-factory.yml`, triggered by a `factory-v*.*.*` tag, which keeps factory releases separate from the `v*` (Gui) and `console-v*` tags:

| Job | Runner | Produces |
|---|---|---|
| test-linux | `ubuntu-latest` | Core, Infrastructure (**with** the PG tests against a `postgres:18` service container), Host, Worker, vitest |
| test-macos | `macos-latest` | Core, Host, Worker, vitest; PG tests skipped (no Docker on macOS runners) |
| test-windows | `windows-latest` | everything, PG via a service started in the job |
| web | `ubuntu-latest` | `wwwroot` artifact (built once, reused by every package job) |
| package-windows | `windows-latest` | signed MSI + winget manifest update PR |
| package-macos (arm64, x64) | `macos-latest` | signed and notarized `.pkg` ×2, cask update |
| package-linux (x64, arm64) | `ubuntu-latest` | `.deb`, `.rpm`, `tar.gz` ×2, GPG-signed repository metadata |
| release | `ubuntu-latest` | GitHub Release with all assets and `SHA256SUMS` |

The test jobs also run on every push to the factory branch, without packaging. That closes G9. The first Linux run will probably find some path and casing failures in tests that hard-code `\`. Budget time for that.

Secrets needed in CI: `APPLE_SIGN_IDENTITY`, `APPLE_INSTALLER_IDENTITY`, `APPLE_ID`, `APPLE_TEAM_ID`, `APPLE_APP_PASSWORD` (the names the VsCodeHost script already uses, plus the installer identity), the Windows signing certificate or Trusted Signing credentials, and a GPG key for the Linux repository.

---

## 11. Build order

1. **CI on all three OSes** (G9), test jobs only. Fix what breaks. Nothing else is trustworthy until the suites pass on macOS and Linux.
2. **Host fixes**: G2 content root, G6 toolchain PATH, readiness check, file logging, first-sign-in password change. **Built 2026-10-19** (see §11.1).
3. **G1: per-OS executables in profiles**, with a preset revision.
4. **Publish scripts** for host + worker + kernel per RID into one folder, plus `wwwroot` (G3). Smoke-test each RID by hand: start, sign in, register a repo, run one small task. Measure the package size.
5. **`litos-factory` CLI**: setup, run/start/stop/open/status/doctor, compose file (G7), env generator (G5), service registration for all three OSes.
6. **Backup/restore in the CLI** (G4), checked with the same live restore check as M4 step 7 on each OS.
7. **Linux packages** (tarball first, then `.deb`/`.rpm`). Linux comes first because it's the easiest to automate and to test in CI containers.
8. **macOS `.pkg`** + notarization, then the Homebrew cask.
9. **Windows MSI**, then the winget manifest.
10. **Server mode** (`--server`, system services, reverse-proxy examples).
11. **Installation check** (§12) on clean machines.

### 11.1 Built

**Step 1, CI, Windows part (2026-10-19).** `.github/workflows/factory-ci.yml`, job `test-windows` on `windows-latest`: builds the four factory test projects (not the whole solution), runs Core, Infrastructure (against the runner's preinstalled PostgreSQL, superuser `postgres`), Worker and Host, then the web app's typecheck and vitest; every suite runs even after one fails, and each failed test is written as a public annotation. Java 21 is set up (the Java preset's sample compiles for 21). Green on run #4, about 16 minutes. Its first run found that the branch did not build: `.gitignore`'s Visual Studio `Backup*/` rule had kept `Infrastructure/Backup/FactoryBackup.cs` and its tests out of git since M4 step 7 (fixed in `cf47523`). macOS and Linux jobs are still to come.

**Step 2, host fixes (2026-10-19).**

- **G2:** the host's content root is its binary's folder (`FactoryOptions.ContentRoot` overrides it, for tests), and the build copies `wwwroot` there, as publishing does. A started-from-anywhere host serves the app. In the source tree, rebuild the host after `npm run build` so the new app is copied beside it.
- **G6:** `FACTORY_TOOLCHAIN_PATH` (setting or environment) is the PATH of every worker, verification command, MCP server and registration PATH check, in `ToolchainEnvironment.Scrub`; the host's own PATH is untouched.
- **Readiness:** `GET /readyz`, anonymous: 200 with `{ready, database, coordinator}` once the database answers (5 s limit) and the coordinator has started, otherwise 503 saying which. `/healthz` stays "listening".
- **Log file:** `FACTORY_LOG_DIR` adds a file logger, `host-YYYYMMDD.log` by UTC date, kept 14 days, flushed per line, shared for reading.
- **Passwords:** every account can change its own password (`POST /api/auth/password`, current password required, must differ, Identity's rules; other sessions end at their next stamp check), from the top bar. With `FACTORY_ADMIN_PASSWORD_ONE_TIME=1` the seeded Admin is marked `MustChangePassword` (migration `UserMustChangePassword`): its session carries a claim, every other API call answers 403 until it changes the password, and the app shows only the change screen. Off by default, so existing installs are unchanged.

Steps 1–4 are worth doing even if installers wait. They are what makes "runs on Mac and Linux" true at all.

---

## 12. Installation check (acceptance)

On a **clean** VM or machine per platform (Windows 11, macOS on Apple Silicon, macOS Intel if still supported, Ubuntu 24.04, Fedora), with only Docker and git installed:

1. Install with the default options. The browser opens on the sign-in page within 2 minutes of the installer finishing, with no terminal commands.
2. Sign in with the shown password; the app requires a new one.
3. Set a provider key and GitHub token in Settings; register `filedb-sharp` (.NET) and one `forms-*` repo (Python), so G1 and the PATH snapshot are exercised.
4. Run one small task to Accepted on each platform, using the attachment of a PDF (G8).
5. Reboot (log out and back in on Linux). The factory comes back on its own after Docker starts, and the board shows the earlier task.
6. Install the next version over it. Migrations run, the data is still there, and no run is left Running.
7. `litos-factory backup`, then `uninstall --purge`, reinstall, `restore`. The task and its lessons are back.
8. Without Docker running: the installer and `open` say what to do in one sentence, and nothing is left half-configured.
9. Uninstall without `--purge`. Binaries and the service are gone, data and the volume remain, and the message says where.

Record results the way the M-checks are recorded (pass/fail per item, findings).

---

## 13. Risks and open questions

- **Docker Desktop licensing** applies to larger organisations on Windows and macOS. Rancher Desktop, OrbStack (macOS, also paid for business use), Colima (macOS, free) and plain Docker Engine (Linux) are the alternatives. The installer must not assume Docker Desktop specifically. Preflight only checks for a working `docker` CLI and compose v2.
- **An embedded PostgreSQL** (shipping Postgres binaries and running `initdb` in the data folder) would remove Docker from the desktop install entirely. It isn't chosen now (Docker was agreed as acceptable), but the `litos-factory` CLI is the place it would go later. The host only needs a connection string.
- **Package size** with three self-contained runtimes. Measure in step 4. Framework-dependent publishing is the fallback.
- **Intel Mac support**: worth confirming whether `osx-x64` is still needed, or arm64 only.
- **Windows on ARM**: not planned. `win-arm64` would be one more RID in the same pipeline if asked for.
- **Toolchains under a server account** (§8) are the server admin's job. `doctor --server` lists what the service account can find.
- **Same machine as the dev setup**: an installed factory and a source-tree dev host both default to ports 5180/5433 and the same `%LOCALAPPDATA%\LitosSoftwareFactory`. `setup` should detect an existing dev `.env.factory`/container and either adopt it (offer) or choose other ports and folders. That has to be decided before testing installers on this machine.
