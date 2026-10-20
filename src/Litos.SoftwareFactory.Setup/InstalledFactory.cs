using System.Diagnostics;

namespace Litos.SoftwareFactory.Setup;

/// <summary>
/// The installed factory's real steps: docker compose, the host's --migrate, and the host itself in
/// a job. setup and the supervisor both use these, so first run and sign-in start cannot drift apart.
/// </summary>
public sealed class InstalledFactory(FactoryPaths paths, FactoryEnv env, Docker docker, IProcessRunner runner, IMachine machine, LauncherLog log) : ISupervisedFactory
{
    /// <summary>How long a host asked to stop gets before it is ended; a run's worker is stopped within it.</summary>
    public static readonly TimeSpan StopGrace = TimeSpan.FromMinutes(2);

    public Task<bool> IsDockerRunningAsync(CancellationToken ct) => docker.IsRunningAsync(ct);

    public bool StartDocker()
    {
        if (machine.FindDockerEngine() is not { } engine)
            return false;
        machine.StartDockerEngine(engine);
        return true;
    }

    /// <summary>
    /// Usually nothing to do: the container restarts with Docker (restart: unless-stopped). The first
    /// time, Docker downloads PostgreSQL and init-roles.sh creates the roles in the new volume.
    /// </summary>
    public Task<ProcessResult> StartDatabaseAsync(CancellationToken ct) =>
        docker.ComposeAsync(paths, ["up", "-d", "--wait"], TimeSpan.FromMinutes(15), ct);

    public Task<ProcessResult> MigrateAsync(CancellationToken ct) =>
        runner.RunAsync(paths.HostExecutable, ["--env-file", paths.EnvFile, "--migrate"], TimeSpan.FromMinutes(10), ct);

    public async Task<int> RunHostAsync(CancellationToken stop)
    {
        var info = new ProcessStartInfo(paths.HostExecutable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = paths.InstallDirectory,
        };
        info.ArgumentList.Add("--env-file");
        info.ArgumentList.Add(paths.EnvFile);

        using var job = new JobObject();
        using var host = Process.Start(info) ?? throw new InvalidOperationException("The factory host did not start.");
        try
        {
            job.Add(host);
        }
        catch
        {
            // Outside the job, nothing would end it with the supervisor.
            host.Kill(entireProcessTree: true);
            throw;
        }

        // The host logs to its own file (FACTORY_LOG_DIR); its console output is drained so it never
        // blocks on a full pipe, and what it writes to stderr (why it cannot start) goes to this log.
        host.OutputDataReceived += (_, _) => { };
        host.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log.Write("host: " + e.Data); };
        host.BeginOutputReadLine();
        host.BeginErrorReadLine();

        try
        {
            await host.WaitForExitAsync(stop);
            return host.ExitCode;
        }
        catch (OperationCanceledException)
        {
            log.Write("Asking the factory host to stop.");
            using var client = HostClient.For(env);
            await client.RequestStopAsync(CancellationToken.None);
            using var grace = new CancellationTokenSource(StopGrace);
            try
            {
                await host.WaitForExitAsync(grace.Token);
            }
            catch (OperationCanceledException)
            {
                log.Write($"The factory host did not stop within {StopGrace.TotalMinutes:0} min; ending it.");
                host.Kill(entireProcessTree: true);
                await host.WaitForExitAsync(CancellationToken.None);
            }

            return 0;
        }
    }
}

/// <summary>
/// One supervisor per user. It holds <c>supervisor.lock</c> in the config folder open while it runs
/// (Windows releases it when the process ends, however it ends; a mutex would have to be released
/// on the thread that took it), and watches a named event that <c>litos-factory stop</c> sets: a
/// hidden process has no console to send Ctrl+C to.
/// </summary>
public sealed class SupervisorSignals : IDisposable
{
    private readonly FileStream _lock;
    private readonly EventWaitHandle _stop;
    private readonly RegisteredWaitHandle _registration;
    private readonly CancellationTokenSource _stopping = new();

    private SupervisorSignals(FileStream lockFile, EventWaitHandle stop)
    {
        _lock = lockFile;
        _stop = stop;
        // Only the lock's holder clears the event: a stop meant for an earlier supervisor is spent.
        _stop.Reset();
        _registration = ThreadPool.RegisterWaitForSingleObject(_stop, (_, _) => _stopping.Cancel(), null, Timeout.Infinite, executeOnlyOnce: true);
    }

    /// <summary>Cancelled when <c>litos-factory stop</c> asks.</summary>
    public CancellationToken Stopping => _stopping.Token;

    private static string LockFile(FactoryPaths paths) => Path.Combine(paths.ConfigDirectory, "supervisor.lock");

    /// <summary>Named after the config folder, so the tests' supervisors never meet an installed one's.</summary>
    private static string StopEventName(FactoryPaths paths)
    {
        var folder = Path.GetFullPath(paths.ConfigDirectory).TrimEnd('\\').ToLowerInvariant();
        return @"Local\LitosSoftwareFactory-stop-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(folder)))[..16];
    }

    /// <summary>Null when another supervisor already runs.</summary>
    public static SupervisorSignals? TryAcquire(FactoryPaths paths)
    {
        Directory.CreateDirectory(paths.ConfigDirectory);
        // The event exists before the lock is taken, so whoever sees the lock held can always signal it.
        var stop = new EventWaitHandle(false, EventResetMode.ManualReset, StopEventName(paths));
        try
        {
            return new SupervisorSignals(new FileStream(LockFile(paths), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None), stop);
        }
        catch (IOException)
        {
            stop.Dispose();
            return null;
        }
    }

    public static bool IsRunning(FactoryPaths paths)
    {
        try
        {
            using var probe = new FileStream(LockFile(paths), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    /// <summary>Sets the running supervisor's stop event; false when there is none to set (another session's).</summary>
    public static bool Signal(FactoryPaths paths)
    {
        if (!EventWaitHandle.TryOpenExisting(StopEventName(paths), out var stop))
            return false;
        using (stop)
            stop.Set();
        return true;
    }

    public static async Task<bool> WaitUntilGoneAsync(FactoryPaths paths, TimeSpan limit, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + limit;
        while (IsRunning(paths))
        {
            if (DateTime.UtcNow >= deadline)
                return false;
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }

        return true;
    }

    public void Dispose()
    {
        // Waits for a callback already running: one that ran after the dispose below would crash
        // the process on a disposed CancellationTokenSource (a stop arriving as the supervisor ends).
        using (var callbacksDone = new ManualResetEvent(false))
        {
            if (_registration.Unregister(callbacksDone))
                callbacksDone.WaitOne();
        }

        _stop.Dispose();
        _stopping.Dispose();
        _lock.Dispose();
    }
}
