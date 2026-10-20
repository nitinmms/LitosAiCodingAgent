namespace Litos.SoftwareFactory.Setup.Tests;

/// <summary><c>litos-factory run</c> (§4.5): wait for Docker as long as it takes, then the database, then the host, restarted after a crash.</summary>
public sealed class SupervisorTests
{
    private sealed class FakeFactory : ISupervisedFactory
    {
        public Queue<bool> DockerAnswers { get; } = new();
        public Queue<int> HostExitCodes { get; } = new();
        public Queue<bool> DatabaseAnswers { get; } = new();
        public List<string> Calls { get; } = [];
        public bool HasDockerApp { get; set; } = true;
        public TaskCompletionSource? HostRunsUntilStopped { get; set; }
        public Exception? ThrowOnFirstHostStart { get; set; }

        public Task<bool> IsDockerRunningAsync(CancellationToken ct)
        {
            Calls.Add("docker?");
            return Task.FromResult(DockerAnswers.Count == 0 || DockerAnswers.Dequeue());
        }

        public bool StartDocker()
        {
            Calls.Add("start docker");
            return HasDockerApp;
        }

        public Task<ProcessResult> StartDatabaseAsync(CancellationToken ct)
        {
            Calls.Add("database");
            var ok = DatabaseAnswers.Count == 0 || DatabaseAnswers.Dequeue();
            return Task.FromResult(new ProcessResult(ok ? 0 : 1, "", ok ? "" : "port is already allocated"));
        }

        public Task<ProcessResult> MigrateAsync(CancellationToken ct)
        {
            Calls.Add("migrate");
            return Task.FromResult(new ProcessResult(0, "", ""));
        }

        public async Task<int> RunHostAsync(CancellationToken stop)
        {
            Calls.Add("host");
            if (ThrowOnFirstHostStart is { } failure)
            {
                ThrowOnFirstHostStart = null;
                throw failure;
            }

            if (HostRunsUntilStopped is { } running)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, stop);
                }
                catch (OperationCanceledException)
                {
                    running.TrySetResult();
                    return 0;
                }
            }

            return HostExitCodes.Count == 0 ? 0 : HostExitCodes.Dequeue();
        }
    }

    private static (Supervisor Supervisor, List<TimeSpan> Delays, StringWriter Log) Create(FakeFactory factory, StartDockerPolicy policy, Func<DateTime>? clock = null)
    {
        var delays = new List<TimeSpan>();
        var log = new StringWriter();
        var supervisor = new Supervisor(factory, policy, new LauncherLog(null, log), (wait, _) =>
        {
            delays.Add(wait);
            return Task.CompletedTask;
        }, clock);
        return (supervisor, delays, log);
    }

    [Fact]
    public async Task WithDockerRunning_TheDatabaseIsStarted_Migrated_ThenTheHostRuns_AndACleanStopEndsIt()
    {
        var factory = new FakeFactory();
        var (supervisor, delays, _) = Create(factory, StartDockerPolicy.Ask);

        await supervisor.RunAsync(CancellationToken.None);

        Assert.Equal(["docker?", "database", "migrate", "host"], factory.Calls);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task WithoutDocker_ItWaitsForAsLongAsItTakes_WithoutStartingDocker_UnlessToldToAlways()
    {
        var factory = new FakeFactory();
        // Ten minutes of polling, far past the old five-minute limit.
        foreach (var _ in Enumerable.Range(0, 121))
            factory.DockerAnswers.Enqueue(false);
        var (supervisor, delays, log) = Create(factory, StartDockerPolicy.Ask);

        await supervisor.RunAsync(CancellationToken.None);

        Assert.DoesNotContain("start docker", factory.Calls);
        Assert.Equal(120, delays.Count(d => d == Supervisor.DockerPoll));
        Assert.Contains("Waiting for Docker", log.ToString());
        Assert.Contains("Still waiting for Docker (10 min)", log.ToString());
        Assert.Equal("host", factory.Calls[^1]);
    }

    [Fact]
    public async Task WithStartDockerAlways_DockerIsStartedOnce_ThenWaitedFor()
    {
        var factory = new FakeFactory();
        foreach (var _ in Enumerable.Range(0, 4))
            factory.DockerAnswers.Enqueue(false);
        var (supervisor, _, log) = Create(factory, StartDockerPolicy.Always);

        await supervisor.RunAsync(CancellationToken.None);

        Assert.Single(factory.Calls, "start docker");
        Assert.Contains("Docker is not running; starting it.", log.ToString());
        Assert.Equal("host", factory.Calls[^1]);
    }

    [Fact]
    public async Task AHostThatCrashes_IsRestarted_WithAGrowingWait()
    {
        var factory = new FakeFactory();
        foreach (var code in new[] { 1, -532462766, 2, 1 })
            factory.HostExitCodes.Enqueue(code);
        var (supervisor, delays, log) = Create(factory, StartDockerPolicy.Ask);

        await supervisor.RunAsync(CancellationToken.None);

        Assert.Equal(5, factory.Calls.Count(c => c == "host"));
        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1)], delays);
        Assert.Contains("The factory host exited with code -532462766.", log.ToString());
        // Every restart looks for Docker and the database again: a crash may have been either.
        Assert.Equal(5, factory.Calls.Count(c => c == "database"));
    }

    [Fact]
    public async Task AHostThatRanForLong_BeforeCrashing_BacksOffFromTheStartAgain()
    {
        var factory = new FakeFactory();
        foreach (var code in new[] { 1, 1, 1 })
            factory.HostExitCodes.Enqueue(code);
        var now = DateTime.UtcNow;
        var ticks = 0;
        // The third run lasts an hour: each RunHostAsync is between two clock reads.
        var (supervisor, delays, _) = Create(factory, StartDockerPolicy.Ask, () => now.AddHours(++ticks == 6 ? 1 : 0));

        await supervisor.RunAsync(CancellationToken.None);

        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(5)], delays);
    }

    [Fact]
    public async Task ADatabaseThatWillNotStart_IsTriedAgain_WithoutStartingTheHost()
    {
        var factory = new FakeFactory();
        factory.DatabaseAnswers.Enqueue(false);
        var (supervisor, delays, log) = Create(factory, StartDockerPolicy.Ask);

        await supervisor.RunAsync(CancellationToken.None);

        Assert.Equal(["docker?", "database", "docker?", "database", "migrate", "host"], factory.Calls);
        Assert.Contains("Starting the database failed: port is already allocated", log.ToString());
        Assert.Equal([TimeSpan.FromSeconds(5)], delays);
    }

    [Fact]
    public async Task AStopRequest_StopsTheHost_AndEndsTheSupervisor()
    {
        var factory = new FakeFactory { HostRunsUntilStopped = new TaskCompletionSource() };
        var supervisor = new Supervisor(factory, StartDockerPolicy.Ask, new LauncherLog(null));
        using var stop = new CancellationTokenSource();

        var running = supervisor.RunAsync(stop.Token);
        while (!factory.Calls.Contains("host"))
            await Task.Delay(10);
        stop.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(factory.HostRunsUntilStopped.Task.IsCompleted);
    }

    [Fact]
    public async Task AStopRequest_WhileWaitingForDocker_EndsTheSupervisor()
    {
        var factory = new FakeFactory();
        foreach (var _ in Enumerable.Range(0, 1000))
            factory.DockerAnswers.Enqueue(false);
        using var stop = new CancellationTokenSource();
        var supervisor = new Supervisor(factory, StartDockerPolicy.Never, new LauncherLog(null), (_, ct) =>
        {
            stop.Cancel();
            return Task.FromCanceled(ct);
        });

        await supervisor.RunAsync(stop.Token);

        Assert.DoesNotContain("host", factory.Calls);
    }

    [Fact]
    public async Task AHostThatCannotBeStarted_IsRetried_NotTheEndOfTheSupervisor()
    {
        var factory = new FakeFactory { ThrowOnFirstHostStart = new System.ComponentModel.Win32Exception(2, "The system cannot find the file specified.") };
        var (supervisor, delays, log) = Create(factory, StartDockerPolicy.Ask);

        await supervisor.RunAsync(CancellationToken.None);

        Assert.Equal(2, factory.Calls.Count(c => c == "host"));
        Assert.Contains("The factory could not be started: The system cannot find the file specified.", log.ToString());
        Assert.Equal([TimeSpan.FromSeconds(5)], delays);
    }

    [Fact]
    public void Backoff_Is5s15s30s_ThenAMinute() =>
        Assert.Equal(
            [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1)],
            Enumerable.Range(1, 5).Select(Supervisor.Backoff));
}
