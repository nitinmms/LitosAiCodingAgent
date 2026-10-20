namespace Litos.SoftwareFactory.Setup.Tests;

/// <summary>
/// Programs that answer from a script, keyed by the start of their command line ("docker.exe info",
/// "schtasks.exe /Create"); the longest matching key wins, and anything unscripted succeeds. A key
/// given several answers gives them in turn, then keeps the last.
/// </summary>
public sealed class FakeRunner : IProcessRunner
{
    private readonly Dictionary<string, Queue<ProcessResult>> _answers = [];

    public List<string> Calls { get; } = [];

    public void Answer(string commandStart, params ProcessResult[] results) => _answers[commandStart] = new Queue<ProcessResult>(results);

    public static ProcessResult Fails(string error) => new(1, "", error);

    public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        var line = string.Join(' ', [Path.GetFileName(fileName), .. arguments]);
        Calls.Add(line);
        var key = _answers.Keys.Where(line.StartsWith).OrderByDescending(k => k.Length).FirstOrDefault();
        if (key is null)
            return Task.FromResult(new ProcessResult(0, line.StartsWith("docker.exe compose version") ? "2.40.3" : "", ""));
        var queue = _answers[key];
        return Task.FromResult(queue.Count > 1 ? queue.Dequeue() : queue.Peek());
    }

    public int Count(string commandStart) => Calls.Count(c => c.StartsWith(commandStart));
}

public sealed class FakeMachine : IMachine
{
    public string? Docker { get; set; } = "docker.exe";
    public DockerEngine? Engine { get; set; } = new("Docker Desktop", @"C:\Docker\Docker Desktop.exe", AutoStart: true);
    public List<DockerEngine> Started { get; } = [];
    public HashSet<int> TakenPorts { get; } = [];
    public List<string> Opened { get; } = [];
    public Dictionary<string, string> RunKeys { get; } = [];

    /// <summary>Runs when the Docker app is started: a test makes `docker info` answer from then on.</summary>
    public Action? OnDockerStarted { get; set; }

    public string? FindDocker() => Docker;
    public DockerEngine? FindDockerEngine() => Engine;

    public void StartDockerEngine(DockerEngine engine)
    {
        Started.Add(engine);
        OnDockerStarted?.Invoke();
    }

    public bool IsPortFree(int port) => !TakenPorts.Contains(port);
    public string CaptureToolchainPath() => @"C:\Windows;C:\tools";
    public bool LongPathsEnabled() => true;
    public void Open(string target) => Opened.Add(target);

    public void SetRunKey(string name, string? command)
    {
        if (command is null)
            RunKeys.Remove(name);
        else
            RunKeys[name] = command;
    }

    public bool HasRunKey(string name) => RunKeys.ContainsKey(name);
}

public sealed class FakeHost : IHostControl
{
    public bool Ready { get; set; } = true;
    public bool? MustChangePassword { get; set; } = true;
    public int StopRequests { get; private set; }

    public Task<(bool Ready, string Detail)> ReadinessAsync(CancellationToken ct) => Task.FromResult((Ready, Ready ? "ready" : "not answering"));
    public Task<bool> WaitUntilReadyAsync(TimeSpan limit, CancellationToken ct) => Task.FromResult(Ready);

    public Task<bool> RequestStopAsync(CancellationToken ct)
    {
        StopRequests++;
        return Task.FromResult(Ready);
    }

    public Task<bool?> AdminMustChangePasswordAsync(CancellationToken ct) => Task.FromResult(MustChangePassword);
    public void Dispose() { }
}

public sealed class FakeInterface : IUserInterface
{
    public List<(string Message, string? Link)> Failures { get; } = [];
    public (bool Start, bool Always) DockerAnswer { get; set; }
    public int DockerQuestions { get; private set; }

    public void Fail(string message, string? link = null) => Failures.Add((message, link));

    public (bool Start, bool Always) AskToStartDocker(string engineName)
    {
        DockerQuestions++;
        return DockerAnswer;
    }
}
