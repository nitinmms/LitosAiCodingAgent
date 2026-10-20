namespace Litos.SoftwareFactory.Setup;

/// <summary>The steps of bringing the installed factory up; <see cref="InstalledFactory"/> is the real one, tests fake it.</summary>
public interface ISupervisedFactory
{
    Task<bool> IsDockerRunningAsync(CancellationToken ct);

    /// <summary>Starts the Docker app; false when none is installed.</summary>
    bool StartDocker();

    Task<ProcessResult> StartDatabaseAsync(CancellationToken ct);

    Task<ProcessResult> MigrateAsync(CancellationToken ct);

    /// <summary>Runs the host until it exits, or until <paramref name="stop"/>, when it is asked to
    /// stop as on Ctrl+C. Returns its exit code: 0 for a clean stop.</summary>
    Task<int> RunHostAsync(CancellationToken stop);
}

/// <summary>
/// <c>litos-factory run</c> (§4.5): what the Scheduled Task runs at sign-in. Waits for Docker for
/// as long as it takes (starting it first if FACTORY_START_DOCKER=always), starts the database,
/// migrates, then runs the host and restarts it after a crash. A clean exit, which is what
/// <c>litos-factory stop</c> causes, ends the supervisor too.
/// </summary>
public sealed class Supervisor(ISupervisedFactory factory, StartDockerPolicy startDocker, LauncherLog log, Func<TimeSpan, CancellationToken, Task>? delay = null, Func<DateTime>? clock = null)
{
    /// <summary>How often the supervisor looks for Docker while it waits.</summary>
    public static readonly TimeSpan DockerPoll = TimeSpan.FromSeconds(5);

    /// <summary>A host that ran this long before failing is not failing repeatedly: back off from the start again.</summary>
    public static readonly TimeSpan HealthyRun = TimeSpan.FromMinutes(10);

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private readonly Func<DateTime> _clock = clock ?? (() => DateTime.UtcNow);

    /// <summary>Returns once the host has stopped cleanly or <paramref name="stop"/> was requested.</summary>
    public async Task RunAsync(CancellationToken stop)
    {
        var failures = 0;
        try
        {
            while (true)
            {
                try
                {
                    await WaitForDockerAsync(stop);

                    if (await StepAsync("Starting the database", factory.StartDatabaseAsync, stop)
                        && await StepAsync("Updating the database", factory.MigrateAsync, stop))
                    {
                        log.Write("Starting the factory host.");
                        var started = _clock();
                        var exitCode = await factory.RunHostAsync(stop);
                        if (exitCode == 0 || stop.IsCancellationRequested)
                        {
                            log.Write("The factory host has stopped.");
                            return;
                        }

                        if (_clock() - started >= HealthyRun)
                            failures = 0;
                        log.Write($"The factory host exited with code {exitCode}.");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A host that cannot be started (missing, quarantined) is retried like one that
                    // crashed: ending the supervisor would leave only Task Scheduler's 3 restarts.
                    log.Write($"The factory could not be started: {ex.Message}");
                }

                var wait = Backoff(++failures);
                log.Write($"Trying again in {wait.TotalSeconds:0} s.");
                await _delay(wait, stop);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            log.Write("Stopped before the factory host was started.");
        }
    }

    /// <summary>5 s, 15 s, 30 s, then every minute.</summary>
    internal static TimeSpan Backoff(int failures) => failures switch
    {
        <= 1 => TimeSpan.FromSeconds(5),
        2 => TimeSpan.FromSeconds(15),
        3 => TimeSpan.FromSeconds(30),
        _ => TimeSpan.FromMinutes(1),
    };

    private async Task<bool> StepAsync(string name, Func<CancellationToken, Task<ProcessResult>> step, CancellationToken stop)
    {
        log.Write(name + ".");
        var result = await step(stop);
        if (!result.Succeeded)
            log.Write($"{name} failed: {result.Message}");
        return result.Succeeded;
    }

    private async Task WaitForDockerAsync(CancellationToken stop)
    {
        if (await factory.IsDockerRunningAsync(stop))
            return;

        if (startDocker == StartDockerPolicy.Always)
            log.Write(factory.StartDocker() ? "Docker is not running; starting it." : "Docker is not running, and no Docker app was found to start.");
        else
            log.Write("Waiting for Docker. Start Docker, or set FACTORY_START_DOCKER=always in factory.env to have it started.");

        var waited = TimeSpan.Zero;
        while (!await factory.IsDockerRunningAsync(stop))
        {
            await _delay(DockerPoll, stop);
            waited += DockerPoll;
            if (waited.Ticks % TimeSpan.FromMinutes(5).Ticks == 0)
                log.Write($"Still waiting for Docker ({waited.TotalMinutes:0} min).");
        }

        log.Write("Docker is running.");
    }
}
