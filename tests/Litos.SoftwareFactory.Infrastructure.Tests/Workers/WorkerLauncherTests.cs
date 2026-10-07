using System.Diagnostics;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Infrastructure.Workers;

namespace Litos.SoftwareFactory.Infrastructure.Tests.Workers;

public class WorkerEnvironmentTests
{
    private static readonly WorkerLaunch Launch = new(
        "run-1", @"C:\data\workspaces\p1", "openrouter", "vendor/model", 200_000, @"C:\data", "http://127.0.0.1:5180", "launch-secret");

    /// <summary>What a factory host's environment plausibly holds, including everything a worker
    /// must never see.</summary>
    private static readonly Dictionary<string, string> Host = new()
    {
        ["PATH"] = @"C:\tools;C:\dotnet",
        ["SystemRoot"] = @"C:\Windows",
        ["TEMP"] = @"C:\Temp",
        ["USERPROFILE"] = @"C:\Users\factory",
        ["DOTNET_ROOT"] = @"C:\dotnet",
        ["NUGET_PACKAGES"] = @"C:\nuget",
        ["npm_config_cache"] = @"C:\npm-cache",

        ["ConnectionStrings__FactoryState"] = "Host=127.0.0.1;Password=db-secret",
        ["ANTHROPIC_API_KEY"] = "sk-ant-secret",
        ["OPENROUTER_API_KEY"] = "sk-or-secret",
        ["OPENAI_API_KEY"] = "sk-openai-secret",
        ["GEMINI_API_KEY"] = "gemini-secret",
        ["TAVILY_API_KEY"] = "tavily-secret",
        ["FACTORY_GITHUB_TOKEN"] = "ghp_secret",
        ["FACTORY_ADMIN_PASSWORD"] = "admin-secret",
        ["FACTORY_DB_ADMIN_PASSWORD"] = "db-admin-secret",
        ["GITHUB_TOKEN"] = "ghp_other",
        ["npm_config__authToken"] = "npm-secret",
        ["NUGET_API_KEY"] = "nuget-secret",
        ["NUGET_CREDENTIALPROVIDER_SESSIONTOKENCACHE_ENABLED"] = "true",
        ["DOTNET_SOME_PASSWORD"] = "dotnet-secret",
        ["SOME_RANDOM_VARIABLE"] = "whatever",
    };

    [Fact]
    public void Build_PassesThroughWhatABuildNeeds()
    {
        var environment = WorkerEnvironment.Build(Host, Launch);

        Assert.Equal(@"C:\tools;C:\dotnet", environment["PATH"]);
        Assert.Equal(@"C:\Windows", environment["SystemRoot"]);
        Assert.Equal(@"C:\Temp", environment["TEMP"]);
        Assert.Equal(@"C:\Users\factory", environment["USERPROFILE"]);
        Assert.Equal(@"C:\dotnet", environment["DOTNET_ROOT"]);
        Assert.Equal(@"C:\nuget", environment["NUGET_PACKAGES"]);
        Assert.Equal(@"C:\npm-cache", environment["npm_config_cache"]);
    }

    [Fact]
    public void Build_AddsTheLaunchValues_AndCiTrue()
    {
        var environment = WorkerEnvironment.Build(Host, Launch);

        Assert.Equal("launch-secret", environment["FACTORY_WORKER_SECRET"]);
        Assert.Equal("http://127.0.0.1:5180", environment["FACTORY_HOST_URL"]);
        Assert.Equal("run-1", environment["FACTORY_RUN_ID"]);
        Assert.Equal("true", environment["CI"]);
    }

    /// <summary>The worker, and every command its agent starts, uses the run's own temporary
    /// directory instead of the host's, so concurrent runs never share temporary files.</summary>
    [Fact]
    public void Build_WithARunTempDirectory_PointsEveryTempVariableAtIt_AndCreatesIt()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"litos-run-tmp-{Guid.NewGuid():n}");
        try
        {
            var environment = WorkerEnvironment.Build(Host, Launch with { TempDirectory = temp });

            Assert.Equal(temp, environment["TEMP"]);
            Assert.Equal(temp, environment["TMP"]);
            Assert.Equal(temp, environment["TMPDIR"]);
            Assert.True(Directory.Exists(temp));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void Build_WithNoRunTempDirectory_KeepsTheHostsTemp()
    {
        var environment = WorkerEnvironment.Build(Host, Launch);

        Assert.Equal(@"C:\Temp", environment["TEMP"]);
        Assert.False(environment.ContainsKey("TMPDIR"));
    }

    /// <summary>Acceptance scenario 14: workers never receive provider keys, the GitHub
    /// credential or the database connection string.</summary>
    [Theory]
    [InlineData("ConnectionStrings__FactoryState")]
    [InlineData("ANTHROPIC_API_KEY")]
    [InlineData("OPENROUTER_API_KEY")]
    [InlineData("OPENAI_API_KEY")]
    [InlineData("GEMINI_API_KEY")]
    [InlineData("TAVILY_API_KEY")]
    [InlineData("FACTORY_GITHUB_TOKEN")]
    [InlineData("FACTORY_ADMIN_PASSWORD")]
    [InlineData("FACTORY_DB_ADMIN_PASSWORD")]
    [InlineData("GITHUB_TOKEN")]
    [InlineData("SOME_RANDOM_VARIABLE")]
    public void Build_NeverPassesForbiddenOrUnknownVariables(string name)
    {
        Assert.False(WorkerEnvironment.Build(Host, Launch).ContainsKey(name));
    }

    /// <summary>The toolchain prefixes are allowed, but not when the name says it is a credential.</summary>
    [Theory]
    [InlineData("npm_config__authToken")]
    [InlineData("NUGET_API_KEY")]
    [InlineData("NUGET_CREDENTIALPROVIDER_SESSIONTOKENCACHE_ENABLED")]
    [InlineData("DOTNET_SOME_PASSWORD")]
    public void Build_AllowedPrefixDoesNotLetACredentialThrough(string name)
    {
        Assert.False(WorkerEnvironment.Build(Host, Launch).ContainsKey(name));
    }

    [Fact]
    public void Build_NoSecretValueFromTheHostAppearsAnywhere()
    {
        var values = string.Join("\n", WorkerEnvironment.Build(Host, Launch).Values);

        foreach (var secret in new[] { "db-secret", "sk-ant-secret", "sk-or-secret", "ghp_secret", "admin-secret", "npm-secret", "nuget-secret", "dotnet-secret" })
            Assert.DoesNotContain(secret, values);
    }

    [Fact]
    public void Build_IsAnAllowlist_SoAnEmptyHostEnvironmentStillYieldsTheLaunchValues()
    {
        var environment = WorkerEnvironment.Build(new Dictionary<string, string>(), Launch);

        Assert.Equal(
            new[] { "CI", "FACTORY_HOST_URL", "FACTORY_RUN_ID", "FACTORY_WORKER_SECRET" },
            environment.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Build_HostCannotPreSetTheWorkersSecretOrRunId()
    {
        var host = new Dictionary<string, string> { ["FACTORY_WORKER_SECRET"] = "stale", ["FACTORY_RUN_ID"] = "other-run", ["CI"] = "false" };

        var environment = WorkerEnvironment.Build(host, Launch);

        Assert.Equal("launch-secret", environment["FACTORY_WORKER_SECRET"]);
        Assert.Equal("run-1", environment["FACTORY_RUN_ID"]);
        Assert.Equal("true", environment["CI"]);
    }

    [Theory]
    [InlineData("path", true)]
    [InlineData("Path", true)]
    [InlineData("dotnet_root", true)]
    [InlineData("NPM_CONFIG_CACHE", true)]
    [InlineData("PATHOLOGICAL", false)]
    [InlineData("MY_DOTNET_ROOT", false)]
    public void IsAllowed_NamesAreExact_PrefixesAreLeading_AndCaseDoesNotMatter(string name, bool expected)
    {
        Assert.Equal(expected, WorkerEnvironment.IsAllowed(name));
    }

    [Fact]
    public void CurrentHostEnvironment_ReadsTheProcessEnvironment()
    {
        Assert.True(WorkerEnvironment.CurrentHostEnvironment().ContainsKey("PATH"));
    }
}

/// <summary>
/// The launcher against a real child process: a small script standing in for the worker, which
/// prints the port handshake and then stays alive like a server.
/// </summary>
public class LocalProcessWorkerLauncherTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly string _workingCopy;
    private readonly string _dataDirectory;

    public LocalProcessWorkerLauncherTests()
    {
        _workingCopy = _temp.Combine("workspaces", "project-1");
        _dataDirectory = _temp.Combine("data");
        Directory.CreateDirectory(_workingCopy);
    }

    public void Dispose() => _temp.Dispose();

    private WorkerLaunch Launch(string runId = "run-1") => new(
        runId, _workingCopy, "openrouter", "vendor/model", 200_000, _dataDirectory, "http://127.0.0.1:5180", "launch-secret");

    /// <summary>Prints the handshake, its arguments, its directory and its whole environment,
    /// then waits.</summary>
    private string FakeWorker(string handshake = "{\"port\":43210}", int sleepSeconds = 30) => Shell.WriteScript(
        _temp.Path, "fake-worker-" + Guid.NewGuid().ToString("n")[..6],
        $"echo {handshake}\necho ARGS %*\necho CWD %CD%\nset\necho to-stderr 1>&2\n{Shell.Sleep(sleepSeconds)}",
        $"echo '{handshake}'\necho ARGS \"$@\"\necho CWD $(pwd)\nenv\necho to-stderr 1>&2\n{Shell.Sleep(sleepSeconds)}");

    private static readonly Dictionary<string, string> HostWithSecrets = new(WorkerEnvironment.CurrentHostEnvironment())
    {
        ["ANTHROPIC_API_KEY"] = "sk-ant-must-not-leak",
        ["ConnectionStrings__FactoryState"] = "Password=db-must-not-leak",
        ["FACTORY_GITHUB_TOKEN"] = "ghp_must_not_leak",
    };

    private LocalProcessWorkerLauncher Launcher(string script, TimeSpan? handshakeTimeout = null) =>
        new(new WorkerCommand(script), () => HostWithSecrets, handshakeTimeout ?? TimeSpan.FromSeconds(20));

    private static async Task<string> ReadLogWhenItContainsAsync(string path, string marker)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            string text;
            await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream))
                text = await reader.ReadToEndAsync();

            if (text.Contains(marker) || DateTime.UtcNow > deadline)
                return text;
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task LaunchAsync_ReadsThePortHandshake_AndReturnsARunningWorker()
    {
        await using var worker = await Launcher(FakeWorker()).LaunchAsync(Launch(), default);

        Assert.Equal(new Uri("http://127.0.0.1:43210/"), worker.BaseAddress);
        Assert.False(worker.HasExited);
        Assert.True(worker.ProcessId > 0);
        Assert.True(WorkerLiveness.IsAlive(worker.ProcessId, worker.StartTime));
    }

    [Fact]
    public async Task LaunchAsync_StartsTheWorkerInTheWorkingCopy_WithItsArguments()
    {
        await using var worker = await Launcher(FakeWorker()).LaunchAsync(Launch(), default);
        var log = await ReadLogWhenItContainsAsync(LocalProcessWorkerLauncher.LogPath(Launch()), "to-stderr");

        Assert.Contains($"CWD {Path.GetFullPath(_workingCopy)}", log, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"--parent-pid {Environment.ProcessId}", log);
        Assert.Contains("--provider openrouter", log);
        Assert.Contains("--model vendor/model", log);
        Assert.Contains("--context-length 200000", log);
        Assert.Contains("--data-dir", log);
    }

    /// <summary>The launched process's real environment, as it printed it: the launch values are
    /// there and none of the host's secrets are.</summary>
    [Fact]
    public async Task LaunchAsync_WorkerEnvironment_HasTheLaunchValues_AndNoneOfTheHostsSecrets()
    {
        await using var worker = await Launcher(FakeWorker()).LaunchAsync(Launch(), default);
        var log = await ReadLogWhenItContainsAsync(LocalProcessWorkerLauncher.LogPath(Launch()), "to-stderr");

        Assert.Contains("FACTORY_WORKER_SECRET=launch-secret", log);
        Assert.Contains("FACTORY_HOST_URL=http://127.0.0.1:5180", log);
        Assert.Contains("FACTORY_RUN_ID=run-1", log);
        Assert.Contains("CI=true", log);

        Assert.DoesNotContain("must-not-leak", log);
        Assert.DoesNotContain("must_not_leak", log);
        Assert.DoesNotContain("ANTHROPIC_API_KEY", log);
        Assert.DoesNotContain("ConnectionStrings", log);
        Assert.DoesNotContain("FACTORY_GITHUB_TOKEN", log);
    }

    [Fact]
    public async Task LaunchAsync_PipesStdoutAndStderrToTheRunsWorkerLog()
    {
        await using var worker = await Launcher(FakeWorker()).LaunchAsync(Launch("run-7"), default);
        var path = LocalProcessWorkerLauncher.LogPath(Launch("run-7"));
        var log = await ReadLogWhenItContainsAsync(path, "to-stderr");

        Assert.Equal(Path.Combine(_dataDirectory, "runs", "run-7", "worker.log"), path);
        Assert.Contains("{\"port\":43210}", log);
        Assert.Contains("[stderr] to-stderr", log);
    }

    [Fact]
    public async Task Kill_StopsTheWorker()
    {
        await using var worker = await Launcher(FakeWorker()).LaunchAsync(Launch(), default);

        worker.Kill();

        Assert.True(worker.HasExited);
        Assert.False(WorkerLiveness.IsAlive(worker.ProcessId, worker.StartTime));
    }

    [Fact]
    public async Task DisposeAsync_KillsTheWorker()
    {
        var worker = await Launcher(FakeWorker()).LaunchAsync(Launch(), default);
        var (processId, startTime) = (worker.ProcessId, worker.StartTime);

        await worker.DisposeAsync();

        Assert.False(WorkerLiveness.IsAlive(processId, startTime));
    }

    [Fact]
    public async Task LaunchAsync_WorkerExitsBeforeItsHandshake_Fails_AndPointsAtTheLog()
    {
        var script = Shell.WriteScript(_temp.Path, "dies", "echo starting up\nexit /b 3", "echo starting up\nexit 3");

        var ex = await Assert.ThrowsAsync<WorkerLaunchException>(() => Launcher(script).LaunchAsync(Launch(), default));

        Assert.Contains("exited before reporting its port", ex.Message);
        Assert.Contains("worker.log", ex.Message);
    }

    [Fact]
    public async Task LaunchAsync_NoHandshakeInTime_Fails_AndLeavesNoProcessBehind()
    {
        var script = Shell.WriteScript(_temp.Path, "silent", $"echo not a handshake\n{Shell.Sleep(30)}", $"echo not a handshake\n{Shell.Sleep(30)}");
        var stopwatch = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<WorkerLaunchException>(
            () => Launcher(script, TimeSpan.FromSeconds(1)).LaunchAsync(Launch(), default));

        Assert.Contains("did not report its port within 1 seconds", ex.Message);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task LaunchAsync_Cancelled_Throws()
    {
        var script = Shell.WriteScript(_temp.Path, "slow", Shell.Sleep(30), Shell.Sleep(30));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Launcher(script).LaunchAsync(Launch(), cts.Token));
    }

    [Fact]
    public async Task LaunchAsync_WorkerExecutableMissing_Fails()
    {
        var launcher = new LocalProcessWorkerLauncher(new WorkerCommand(_temp.Combine("no-such-worker.exe")));

        var ex = await Assert.ThrowsAsync<WorkerLaunchException>(() => launcher.LaunchAsync(Launch(), default));

        Assert.Contains("could not be started", ex.Message);
    }

    [Fact]
    public async Task LaunchAsync_WorkingCopyMissing_Fails_BeforeStartingAnything()
    {
        var launch = Launch() with { WorkingCopy = _temp.Combine("no-such-copy") };

        var ex = await Assert.ThrowsAsync<WorkerLaunchException>(() => Launcher(FakeWorker()).LaunchAsync(launch, default));

        Assert.Contains("does not exist", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(_dataDirectory, "runs")));
    }

    // ---- Pure parts ----

    [Fact]
    public void BuildArguments_CarryNothingSecret()
    {
        var arguments = LocalProcessWorkerLauncher.BuildArguments(Launch(), parentProcessId: 4242);

        Assert.Equal(
            ["--parent-pid", "4242", "--provider", "openrouter", "--model", "vendor/model", "--data-dir", _dataDirectory, "--context-length", "200000"],
            arguments);
        Assert.DoesNotContain("launch-secret", arguments);
    }

    [Fact]
    public void BuildArguments_UnknownContextLength_IsOmitted()
    {
        var arguments = LocalProcessWorkerLauncher.BuildArguments(Launch() with { ContextLength = null }, 1);

        Assert.DoesNotContain("--context-length", arguments);
    }

    [Theory]
    [InlineData("{\"port\":51234}", true, 51234)]
    [InlineData("  {\"port\": 80}  ", true, 80)]
    [InlineData("{\"port\":0}", false, 0)]
    [InlineData("{\"port\":70000}", false, 0)]
    [InlineData("{\"port\":\"51234\"}", false, 0)]
    [InlineData("{\"other\":1}", false, 0)]
    [InlineData("[51234]", false, 0)]
    [InlineData("info: Now listening on: http://127.0.0.1:51234", false, 0)]
    [InlineData("", false, 0)]
    public void TryParseHandshake(string line, bool expected, int expectedPort)
    {
        Assert.Equal(expected, LocalProcessWorkerLauncher.TryParseHandshake(line, out var port));
        if (expected)
            Assert.Equal(expectedPort, port);
    }

    [Fact]
    public void WorkerCommand_PrefixArguments_ComeBeforeTheWorkersOwn()
    {
        var command = new WorkerCommand("dotnet", ["Litos.SoftwareFactory.Worker.dll"]);

        Assert.Equal("dotnet", command.Executable);
        Assert.Equal(["Litos.SoftwareFactory.Worker.dll"], command.PrefixArguments);
        Assert.Empty(new WorkerCommand("worker.exe").PrefixArguments);
    }
}

public class WorkerLivenessTests
{
    [Fact]
    public void IsAlive_ThisProcess_WithItsRealStartTime_IsAlive()
    {
        using var current = Process.GetCurrentProcess();

        Assert.True(WorkerLiveness.IsAlive(current.Id, current.StartTime.ToUniversalTime()));
    }

    /// <summary>§16: liveness is checked by id *and* start time, because ids are reused. The
    /// same id with a different start time is a different process.</summary>
    [Fact]
    public void IsAlive_RightIdWrongStartTime_IsNotTheWorker()
    {
        using var current = Process.GetCurrentProcess();

        Assert.False(WorkerLiveness.IsAlive(current.Id, current.StartTime.ToUniversalTime().AddMinutes(-10)));
    }

    [Fact]
    public void IsAlive_NoSuchProcess_IsFalse()
    {
        Assert.False(WorkerLiveness.IsAlive(int.MaxValue - 7, DateTimeOffset.UtcNow));
    }
}
