using System.Diagnostics;
using System.Reflection;

namespace Litos.SoftwareFactory.Setup;

/// <summary>The litos-factory commands (SoftwareFactoryInstallation.md §4.1), Windows parts.</summary>
/// <param name="hosts">How the running host is reached; tests replace it.</param>
/// <param name="startLimit">How long a start waits for Docker, and then for the host, before saying it failed.</param>
public sealed class Commands(
    FactoryPaths paths, IProcessRunner runner, IMachine machine, IUserInterface ui, TextWriter output,
    Func<FactoryEnv, IHostControl>? hosts = null, TimeSpan? startLimit = null)
{
    public const string DockerInstallLink = "https://docs.docker.com/desktop/setup/install/windows-install/";
    public const string GitInstallLink = "https://git-scm.com/download/win";
    public const string VolumeName = "litos-factory_state";

    private const long MinimumFreeBytes = 10L * 1024 * 1024 * 1024;

    private readonly Func<FactoryEnv, IHostControl> _hosts = hosts ?? HostClient.For;
    private readonly TimeSpan _startLimit = startLimit ?? TimeSpan.FromMinutes(3);
    private readonly SignInStart _signIn = new(runner, machine, paths);

    /// <summary>Why a command could not do its job, with the fix; shown as a dialog or printed.</summary>
    private sealed class SetupFailed(string message, string? link = null) : Exception(message)
    {
        public string? Link { get; } = link;
    }

    public static string Version =>
        typeof(Commands).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "unknown";

    /// <summary>
    /// First run, and repair: every step is safe to repeat (§4.3). From the installer a failure is a
    /// dialog and exit code 0, so the files stay installed and the shortcut finishes setup later.
    /// </summary>
    public Task<int> SetupAsync(bool fromInstaller, CancellationToken ct) => GuardedAsync(async () =>
    {
        var (docker, startDockerAlways) = await PreflightAsync(ct);
        var env = LoadOrCreateEnv();
        if (startDockerAlways)
            env.Set(FactoryEnv.StartDocker, "always");
        env.Set(FactoryEnv.ToolchainPath, machine.CaptureToolchainPath());
        env.Save(paths.EnvFile);

        // A running factory (setup run again, or an upgrade's) is stopped while the database changes.
        if (!await StopFactoryAsync(env, ct))
            throw new SetupFailed("The running factory did not stop, so its database was left as it is. Run setup again in a few minutes.");
        await PrepareDatabaseAsync(env, docker, ct);

        var (kind, problem) = await _signIn.RegisterAsync(ct);
        output.WriteLine(kind == SignInStart.Kind.ScheduledTask
            ? $"The factory will start when you sign in to Windows (Scheduled Task \"{SignInStart.TaskName}\")."
            : $"The factory will start when you sign in to Windows (the Run key; the Scheduled Task was refused: {problem}).");

        await StartAndWaitAsync(env, ct);
        output.WriteLine($"Litos Software Factory is running at {env.Url}.");
        WarnIfDockerDoesNotStartAtSignIn(env);

        if (!fromInstaller)
            await ShowAsync(env, ct);
        return 0;
    }, failureExitCode: fromInstaller ? 0 : 1);

    /// <summary>The background supervisor the Scheduled Task runs at sign-in (§4.5).</summary>
    public Task<int> RunAsync(CancellationToken ct) => GuardedAsync(async () =>
    {
        var env = LoadEnv();
        using var signals = SupervisorSignals.TryAcquire(paths);
        if (signals is null)
        {
            output.WriteLine("The factory is already running.");
            return 0;
        }

        var log = new LauncherLog(FactoryPaths.LogDirectory(env.DataDirectory), output);
        var docker = FindDocker() ?? throw DockerMissing();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, signals.Stopping);
        var factory = new InstalledFactory(paths, env, docker, runner, machine, log);
        await new Supervisor(factory, env.StartDockerPolicy, log).RunAsync(stop.Token);
        return 0;
    });

    /// <summary>Starts the factory in the background, starting Docker if the setting says to, and waits until it is ready.</summary>
    public Task<int> StartAsync(CancellationToken ct) => GuardedAsync(async () =>
    {
        var env = LoadEnv();
        await EnsureDockerAsync(env, interactive: false, ct);
        await StartAndWaitAsync(env, ct);
        output.WriteLine($"Litos Software Factory is running at {env.Url}.");
        return 0;
    });

    /// <summary>
    /// The Start-menu shortcut: sets up if that never finished, starts Docker (asking first, unless
    /// the setting says otherwise) and the factory if they are not running, then opens the browser.
    /// </summary>
    public Task<int> OpenAsync(CancellationToken ct)
    {
        if (!File.Exists(paths.EnvFile))
            return SetupAsync(fromInstaller: false, ct);

        return GuardedAsync(async () =>
        {
            var env = LoadEnv();
            await EnsureDockerAsync(env, interactive: true, ct);
            await StartAndWaitAsync(env, ct);
            await ShowAsync(env, ct);
            return 0;
        });
    }

    /// <summary>Stops the host cleanly (runs come back Interrupted, §16); the database too with --all.</summary>
    public async Task<int> StopAsync(bool all, CancellationToken ct)
    {
        if (!File.Exists(paths.EnvFile))
        {
            output.WriteLine("The factory is not set up; there is nothing to stop.");
            return 0;
        }

        var env = LoadEnv();
        if (!await StopFactoryAsync(env, ct))
        {
            output.WriteLine("The factory did not stop in time.");
            return 1;
        }

        if (all && FindDocker() is { } docker)
        {
            var result = await docker.ComposeAsync(paths, ["stop"], TimeSpan.FromMinutes(2), ct);
            output.WriteLine(result.Succeeded ? "The database is stopped." : $"The database could not be stopped: {result.Message}");
        }

        output.WriteLine("Litos Software Factory is stopped.");
        return 0;
    }

    public async Task<int> StatusAsync(CancellationToken ct)
    {
        output.WriteLine($"Litos Software Factory {Version}");
        output.WriteLine($"  Install folder: {paths.InstallDirectory}");
        output.WriteLine($"  Settings:       {paths.EnvFile}");
        if (!File.Exists(paths.EnvFile))
        {
            output.WriteLine("  Not set up yet: run `litos-factory setup`, or open Litos Software Factory from the Start menu.");
            return 1;
        }

        var env = LoadEnv();
        output.WriteLine($"  Data folder:    {env.DataDirectory}");
        output.WriteLine($"  Logs:           {FactoryPaths.LogDirectory(env.DataDirectory)}");
        output.WriteLine($"  Address:        {env.Url}");
        output.WriteLine($"  Database port:  {env[FactoryEnv.DbPort]}");
        output.WriteLine($"  Supervisor:     {(SupervisorSignals.IsRunning(paths) ? "running" : "not running")}");
        using (var host = _hosts(env))
        {
            var (ready, detail) = await host.ReadinessAsync(ct);
            output.WriteLine($"  Host:           {(ready ? "ready" : "not ready")} ({detail.Trim()})");
        }

        var docker = FindDocker();
        output.WriteLine($"  Docker:         {(docker is null ? "not installed" : await docker.IsRunningAsync(ct) ? "running" : "not running")}");
        var engine = machine.FindDockerEngine();
        var autoStart = engine?.AutoStart switch { true => ", starts at sign-in", false => ", does not start at sign-in", null => "" };
        output.WriteLine($"  Docker app:     {engine?.Name ?? "none found"}{autoStart}");
        output.WriteLine($"  Start Docker:   {env.StartDockerPolicy.ToString().ToLowerInvariant()} (FACTORY_START_DOCKER)");
        var signIn = await _signIn.FindAsync(ct) switch
        {
            SignInStart.Kind.ScheduledTask => "Scheduled Task",
            SignInStart.Kind.RunKey => "Run key",
            _ => "not registered: run `litos-factory setup`",
        };
        output.WriteLine($"  At sign-in:     {signIn}");
        return 0;
    }

    /// <summary>Everything status says, and what else could stop a run, with the fix for each.</summary>
    public async Task<int> DoctorAsync(CancellationToken ct)
    {
        await StatusAsync(ct);
        output.WriteLine();
        output.WriteLine("Checks:");

        var problems = 0;
        void Check(bool ok, string what, string fix)
        {
            output.WriteLine($"  [{(ok ? "ok" : "!!")}] {what}");
            if (ok)
                return;
            output.WriteLine($"       {fix}");
            problems++;
        }

        var docker = FindDocker();
        Check(docker is not null, "Docker is installed", $"Install Docker Desktop (winget install Docker.DockerDesktop) or Rancher Desktop: {DockerInstallLink}");
        if (docker is not null)
        {
            var running = await docker.IsRunningAsync(ct);
            Check(running, "Docker is running", "Start Docker, or set FACTORY_START_DOCKER=always in the settings file.");
            if (running)
                Check(await docker.ComposeMajorVersionAsync(ct) >= 2, "Docker Compose v2 is available", "Update Docker Desktop; the factory needs `docker compose` v2.");
        }

        Check(await IsGitInstalledAsync(ct), "git is installed", $"Install Git: {GitInstallLink}");

        if (File.Exists(paths.EnvFile))
        {
            var env = LoadEnv();
            Check(!string.IsNullOrEmpty(env[FactoryEnv.ToolchainPath]), "The toolchain PATH is captured", "Run `litos-factory refresh-path`.");
            ListToolchains(env[FactoryEnv.ToolchainPath]);

            if (machine.FindDockerEngine() is { AutoStart: false } engine && env.StartDockerPolicy != StartDockerPolicy.Always)
                Check(false, "Docker starts at sign-in",
                    $"Turn on \"Start {engine.Name} when you sign in\" in its settings, or set FACTORY_START_DOCKER=always, so the factory comes back after a restart.");

            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(env.DataDirectory))!);
            Check(drive.AvailableFreeSpace >= MinimumFreeBytes, $"At least 10 GB free on {drive.Name} ({drive.AvailableFreeSpace / (1024 * 1024 * 1024)} GB)",
                "Working copies and builds need space; free some up.");
        }

        Check(machine.LongPathsEnabled(), "Long paths are enabled in Windows",
            "Deep node_modules folders can pass 260 characters. As an administrator: Set-ItemProperty HKLM:\\SYSTEM\\CurrentControlSet\\Control\\FileSystem LongPathsEnabled 1");
        output.WriteLine("  Tip: excluding the data folder from Microsoft Defender's real-time scan makes builds much faster.");

        output.WriteLine();
        output.WriteLine(problems == 0 ? "No problems found." : $"{problems} problem(s) found.");
        return problems == 0 ? 0 : 1;
    }

    /// <summary>Captures the PATH again (a toolchain was installed); restarts a running factory so it applies.</summary>
    public Task<int> RefreshPathAsync(CancellationToken ct) => GuardedAsync(async () =>
    {
        var env = LoadEnv();
        env.Set(FactoryEnv.ToolchainPath, machine.CaptureToolchainPath());
        env.Save(paths.EnvFile);
        output.WriteLine("The toolchain PATH was captured again.");

        if (!SupervisorSignals.IsRunning(paths))
            return 0;
        output.WriteLine("Restarting the factory so it applies.");
        await StopFactoryAsync(env, ct);
        return await StartAsync(ct);
    });

    /// <summary>Starts the database and runs the migrations, with the host stopped meanwhile.</summary>
    public Task<int> UpgradeDatabaseAsync(CancellationToken ct) => GuardedAsync(async () =>
    {
        var env = LoadEnv();
        var docker = FindDocker() ?? throw DockerMissing();
        var wasRunning = SupervisorSignals.IsRunning(paths);
        if (!await StopFactoryAsync(env, ct))
            throw new SetupFailed("The running factory did not stop, so its database was left as it is.");
        await PrepareDatabaseAsync(env, docker, ct);
        output.WriteLine("The factory database is up to date.");
        return wasRunning ? await StartAsync(ct) : 0;
    });

    /// <summary>
    /// Removes the start at sign-in and stops the factory. Without --purge, the settings, the data
    /// and the database volume stay, so a reinstall picks them up. With it, all three are deleted,
    /// but only once the volume is gone: the settings hold the passwords the volume was created
    /// with, and a new install could never open an old volume without them.
    /// </summary>
    public async Task<int> UninstallAsync(bool purge, CancellationToken ct)
    {
        await _signIn.RemoveAsync(ct);
        if (!File.Exists(paths.EnvFile))
        {
            output.WriteLine("Litos Software Factory was removed; it had not been set up.");
            return 0;
        }

        var env = LoadEnv();
        var dataDirectory = env.DataDirectory;
        await StopAsync(all: false, ct);

        if (!purge)
        {
            output.WriteLine("Litos Software Factory was removed. Its settings, data and database were kept:");
            output.WriteLine($"  Settings: {paths.ConfigDirectory}");
            output.WriteLine($"  Data:     {dataDirectory}");
            output.WriteLine($"  Database: the Docker volume {VolumeName}");
            return 0;
        }

        var removed = FindDocker() is { } docker
            ? await docker.ComposeAsync(paths, ["down", "--volumes"], TimeSpan.FromMinutes(2), ct)
            : new ProcessResult(-1, "", "Docker is not installed.");
        if (!removed.Succeeded)
        {
            output.WriteLine($"The database could not be removed: {Shorten(removed.Message)}");
            output.WriteLine("The settings and data were kept, because the settings hold the database's passwords. To remove them all:");
            output.WriteLine($"  start Docker, run `docker volume rm {VolumeName}`, then delete {paths.ConfigDirectory} and {dataDirectory}.");
            return 1;
        }

        output.WriteLine("The database and its volume were removed.");
        if (FactoryPaths.IsOurDataDirectory(dataDirectory))
            DeleteDirectory(dataDirectory);
        else
            output.WriteLine($"Kept {dataDirectory}: setup did not create it.");
        DeleteDirectory(paths.ConfigDirectory);
        return 0;
    }

    // ---- steps ----

    private async Task<int> GuardedAsync(Func<Task<int>> command, int failureExitCode = 1)
    {
        try
        {
            return await command();
        }
        catch (SetupFailed failed)
        {
            ui.Fail(failed.Message, failed.Link);
            return failureExitCode;
        }
    }

    /// <summary>
    /// Docker installed and running (started if the person agrees), compose v2, and git (§4.3 step 1).
    /// Says whether the person asked for Docker to be started from now on without asking.
    /// </summary>
    private async Task<(Docker Docker, bool StartDockerAlways)> PreflightAsync(CancellationToken ct)
    {
        var docker = FindDocker() ?? throw DockerMissing();
        var always = false;
        if (!await docker.IsRunningAsync(ct))
        {
            var policy = File.Exists(paths.EnvFile) ? LoadEnv().StartDockerPolicy : StartDockerPolicy.Ask;
            always = await StartDockerAsync(docker, policy, interactive: true, ct);
        }

        if (await docker.ComposeMajorVersionAsync(ct) is not >= 2)
            throw new SetupFailed("Docker Compose v2 was not found. Update Docker Desktop, or install the compose plugin.", DockerInstallLink);
        if (!await IsGitInstalledAsync(ct))
            throw new SetupFailed("Git isn't installed. Install it, then open Litos Software Factory from the Start menu to finish setting up.", GitInstallLink);
        return (docker, always);
    }

    private async Task EnsureDockerAsync(FactoryEnv env, bool interactive, CancellationToken ct)
    {
        var docker = FindDocker() ?? throw DockerMissing();
        if (await docker.IsRunningAsync(ct) || !await StartDockerAsync(docker, env.StartDockerPolicy, interactive, ct))
            return;
        env.Set(FactoryEnv.StartDocker, "always");
        env.Save(paths.EnvFile);
    }

    /// <summary>
    /// Starts the Docker app and waits for it, as the setting says: always, after asking, or never.
    /// Returns whether the person ticked "always start Docker automatically".
    /// </summary>
    private async Task<bool> StartDockerAsync(Docker docker, StartDockerPolicy policy, bool interactive, CancellationToken ct)
    {
        var notRunning = new SetupFailed("Docker isn't running. Start Docker, then open Litos Software Factory from the Start menu.", DockerInstallLink);
        var engine = machine.FindDockerEngine();
        if (engine is null || policy == StartDockerPolicy.Never)
            throw notRunning;

        var always = false;
        if (policy == StartDockerPolicy.Ask)
        {
            if (!interactive)
                throw notRunning;
            (var start, always) = ui.AskToStartDocker(engine.Name);
            if (!start)
                throw notRunning;
        }

        output.WriteLine($"Starting {engine.Name}...");
        machine.StartDockerEngine(engine);
        if (!await docker.WaitUntilRunningAsync(_startLimit, ct))
            throw new SetupFailed($"{engine.Name} was started but didn't answer within {_startLimit.TotalMinutes:0} minutes. When it shows that it is running, open Litos Software Factory from the Start menu.");
        return always;
    }

    /// <summary>
    /// factory.env, written the first time (§4.3 step 2) and never overwritten after: the database
    /// keeps the passwords it was created with. Beside a dev setup, its own data folder and ports (§14.5).
    /// </summary>
    private FactoryEnv LoadOrCreateEnv()
    {
        if (File.Exists(paths.EnvFile))
            return LoadEnv();

        var (dataDirectory, besideDevSetup) = paths.ChooseDataDirectory();
        var skip = besideDevSetup ? 1 : 0;
        var env = FactoryEnv.CreateNew(dataDirectory, Ports.FirstFree(5180 + skip, machine.IsPortFree), Ports.FirstFree(5433 + skip, machine.IsPortFree));

        Directory.CreateDirectory(dataDirectory);
        File.WriteAllText(Path.Combine(dataDirectory, FactoryPaths.InstallMarker), "Created by litos-factory setup.\n");
        Directory.CreateDirectory(paths.ConfigDirectory);
        OwnerOnly.Apply(paths.ConfigDirectory);
        env.Save(paths.EnvFile);
        WriteFirstSignIn(env);
        output.WriteLine($"Wrote {paths.EnvFile} (owner-only). Data folder: {dataDirectory}");
        return env;
    }

    /// <summary>The generated admin password, shown once and only to this user (§4.4).</summary>
    private void WriteFirstSignIn(FactoryEnv env)
    {
        File.WriteAllText(paths.FirstSignInFile, $"""
            Litos Software Factory - first sign-in

            Address:    {env.Url}
            User name:  {env[FactoryEnv.AdminUser]}
            Password:   {env[FactoryEnv.AdminPassword]}

            This password works once: you will be asked to choose your own as soon as you sign in.
            This file is deleted once you have.

            """);
        OwnerOnly.Apply(paths.FirstSignInFile);
    }

    /// <summary>docker compose up --wait, then the host's --migrate (§4.3 steps 4–5), as the supervisor runs them.</summary>
    private async Task PrepareDatabaseAsync(FactoryEnv env, Docker docker, CancellationToken ct)
    {
        var factory = new InstalledFactory(paths, env, docker, runner, machine, new LauncherLog(null));
        output.WriteLine("Starting the database (the first time, Docker downloads PostgreSQL)...");
        var up = await factory.StartDatabaseAsync(ct);
        if (!up.Succeeded)
            throw new SetupFailed($"The database didn't start: {Shorten(up.Message)}");

        output.WriteLine("Updating the database...");
        var migrate = await factory.MigrateAsync(ct);
        if (!migrate.Succeeded)
            throw new SetupFailed($"The database couldn't be updated: {Shorten(migrate.Message)}");
    }

    /// <summary>Starts the supervisor if it is not running, and waits for the host to be ready.</summary>
    private async Task StartAndWaitAsync(FactoryEnv env, CancellationToken ct)
    {
        if (!SupervisorSignals.IsRunning(paths) && !await _signIn.RunNowAsync(ct))
        {
            // No Scheduled Task (the Run-key fallback): started directly instead.
            var info = new ProcessStartInfo(paths.WindowlessExecutable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = paths.InstallDirectory };
            info.ArgumentList.Add("run");
            Process.Start(info)?.Dispose();
        }

        using var host = _hosts(env);
        if (!await host.WaitUntilReadyAsync(_startLimit, ct))
            throw new SetupFailed(
                $"The factory didn't start within {_startLimit.TotalMinutes:0} minutes. Its logs are in {FactoryPaths.LogDirectory(env.DataDirectory)}; `litos-factory doctor` checks what is missing.");
    }

    /// <summary>
    /// Through the supervisor, so it does not restart the host; to the host directly when the
    /// supervisor's stop event cannot be reached (it runs in another session), or when the host
    /// was started some other way. False if the supervisor is still there after the grace period.
    /// </summary>
    private async Task<bool> StopFactoryAsync(FactoryEnv env, CancellationToken ct)
    {
        using var host = _hosts(env);
        if (!SupervisorSignals.IsRunning(paths))
        {
            if (await host.RequestStopAsync(ct))
                output.WriteLine("Asked the factory host to stop.");
            return true;
        }

        output.WriteLine("Stopping the factory...");
        if (!SupervisorSignals.Signal(paths))
            await host.RequestStopAsync(ct);
        return await SupervisorSignals.WaitUntilGoneAsync(paths, InstalledFactory.StopGrace + TimeSpan.FromSeconds(30), ct);
    }

    /// <summary>Opens the app, and the first sign-in's password until it has been changed (§4.4).</summary>
    private async Task ShowAsync(FactoryEnv env, CancellationToken ct)
    {
        machine.Open(env.Url);
        if (!File.Exists(paths.FirstSignInFile))
            return;

        using var host = _hosts(env);
        switch (await host.AdminMustChangePasswordAsync(ct))
        {
            case false:
                File.Delete(paths.FirstSignInFile);
                break;
            case true:
                machine.Open(paths.FirstSignInFile);
                break;
        }
    }

    private void WarnIfDockerDoesNotStartAtSignIn(FactoryEnv env)
    {
        if (env.StartDockerPolicy != StartDockerPolicy.Always && machine.FindDockerEngine() is { AutoStart: false } engine)
            output.WriteLine(
                $"Note: {engine.Name} doesn't start when you sign in, so after a restart the factory waits for it. " +
                $"Turn on \"Start {engine.Name} when you sign in\" in its settings, or set FACTORY_START_DOCKER=always in {paths.EnvFile}.");
    }

    private void ListToolchains(string? path)
    {
        output.WriteLine("  Toolchains on the captured PATH (only the ones your projects use are needed):");
        foreach (var tool in new[] { "dotnet", "node", "npm", "python", "java", "mvn", "go" })
        {
            // npm and mvn are .cmd scripts, not programs.
            var found = ExecutableSearch.Find(tool, path) ?? ExecutableSearch.Find(tool, path, extension: ".cmd");
            output.WriteLine($"       {tool,-7} {found ?? "not found"}");
        }
    }

    private void DeleteDirectory(string directory)
    {
        if (!Directory.Exists(directory))
            return;
        try
        {
            Directory.Delete(directory, recursive: true);
            output.WriteLine($"Deleted {directory}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            output.WriteLine($"{directory} could not be deleted completely: {ex.Message}");
        }
    }

    private async Task<bool> IsGitInstalledAsync(CancellationToken ct) =>
        (await runner.RunAsync("git", ["--version"], TimeSpan.FromSeconds(20), ct)).Succeeded;

    private Docker? FindDocker() => machine.FindDocker() is { } executable ? new Docker(runner, executable) : null;

    private FactoryEnv LoadEnv() => File.Exists(paths.EnvFile)
        ? FactoryEnv.Load(paths.EnvFile)
        : throw new SetupFailed("Litos Software Factory isn't set up yet. Open it from the Start menu, or run `litos-factory setup`.");

    private static SetupFailed DockerMissing() => new(
        "Docker isn't installed. Install Docker Desktop (winget install Docker.DockerDesktop; larger companies need a paid subscription) " +
        "or Rancher Desktop (free), then open Litos Software Factory from the Start menu.", DockerInstallLink);

    private static string Shorten(string message) => message.Length <= 600 ? message : message[..600] + "…";
}
