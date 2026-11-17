using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using Litos.Probe.Web.Core;
using Litos.Probe.Web.Core.Contract;
using Litos.Probe.Web.Core.Profiles;
using Litos.Probe.Web.Core.Reports;
using Litos.Probe.Web.Processes;

namespace Litos.Probe.Web.Running;

/// <summary>Where every process's output stood when a check began.</summary>
internal sealed record LogMark(IReadOnlyDictionary<string, long> Positions);

/// <summary>
/// The app under test: its build, its processes on ports allocated for this run, its reset, and
/// its stop (testability-architecture.md §5 "Start order"). Every outcome is a <see cref="CheckResult"/>,
/// so a failure carries its own diagnosis and log tail rather than an exception.
/// </summary>
internal sealed class AppEnvironment : IAsyncDisposable
{
    /// <summary>Ports frameworks fall back to when they ignore the one they were given, on top of the profile's own.</summary>
    private static readonly int[] CommonPorts = [3000, 4200, 5000, 5001, 5173, 8000, 8080, 8787];
    private const int TailLines = 30;

    private readonly string _workingCopy;
    private readonly WebAppContract _contract;
    private readonly StackProfile _profile;
    private readonly ProbeWebOptions _options;
    private readonly SecretRedactor _redactor;
    private readonly IProcessLauncher _launcher;
    private readonly string _scratch;
    private readonly IReadOnlyDictionary<string, int> _ports;
    private readonly RunValues _values;
    private readonly List<ManagedProcess> _running = [];
    private readonly List<(string Label, ManagedProcess Process)> _everStarted = [];
    private readonly Regex? _logErrorPattern;
    private readonly IReadOnlyList<Regex> _failOnLogPatterns;
    private readonly HttpClient _http;
    private string _label = "";

    public AppEnvironment(string workingCopy, WebAppContract effectiveContract, StackProfile profile, ProbeWebOptions options, SecretRedactor redactor, IProcessLauncher launcher, string scratch)
    {
        _workingCopy = Path.GetFullPath(workingCopy);
        _contract = effectiveContract;
        _profile = profile;
        _options = options;
        _redactor = redactor;
        _launcher = launcher;
        _scratch = scratch;
        // Allocated up front, so a build can bake a port in (a SPA's API URL is fixed at build time).
        _ports = Ports.Allocate(effectiveContract.Application.Processes.Select(p => p.Name));
        _values = new RunValues(_ports, options.Secrets, redactor);
        _logErrorPattern = profile.LogErrorPattern is { } p ? new Regex(p, RegexOptions.None, TimeSpan.FromSeconds(1)) : null;
        _failOnLogPatterns = [.. effectiveContract.Testability.FailOn.Where(f => f.LogPattern is not null).Select(f => new Regex(f.LogPattern!, RegexOptions.None, TimeSpan.FromSeconds(1)))];
        _http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false })
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        BaseUrl = $"http://localhost:{_ports[effectiveContract.Application.BaseUrl]}";
    }

    /// <summary><c>http://localhost:{port}</c>: localhost, not 127.0.0.1, because browsers treat it as a secure context.</summary>
    public string BaseUrl { get; }

    public IReadOnlyList<ProcessInfo> Processes => [.. _contract.Application.Processes.Select(p => new ProcessInfo(p.Name, _ports[p.Name]))];

    /// <summary>Every origin the browser may reach: each process's port on every loopback name.</summary>
    public IReadOnlySet<string> AppOrigins =>
        _ports.Values.SelectMany(port => new[] { $"http://localhost:{port}", $"http://127.0.0.1:{port}", $"http://[::1]:{port}" }).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public TimeSpan StartDuration { get; private set; }

    /// <summary>What happened at stop that a person should know, such as a port still held.</summary>
    public List<string> StopWarnings { get; } = [];

    public async Task<CheckResult> BuildAsync(CancellationToken ct)
    {
        var steps = _contract.Application.Build ?? [];
        if (steps.Count == 0)
            return CheckResult.Pass("build", CheckKind.Build) with { Message = "No build declared." };

        var started = Stopwatch.StartNew();
        foreach (var (step, i) in steps.Select((s, i) => (s, i)))
        {
            var label = steps.Count == 1 ? "build" : $"build[{i}]";
            _options.Progress?.Invoke($"Building: {string.Join(' ', step.Command)}");
            var result = await RunCommandAsync(label, step.Command, step.WorkingDirectory, step.Environment, step.TimeoutSeconds, ct);
            if (!result.Succeeded)
            {
                return Classifier.Fail("build", CheckKind.Build, FailureCause.BuildFailed,
                    $"{result.Describe($"'{string.Join(' ', step.Command)}'")}.") with
                {
                    Evidence = new Evidence { Log = Lines(result.Tail) },
                    DurationMs = started.ElapsedMilliseconds,
                };
            }
        }

        return CheckResult.Pass("build", CheckKind.Build) with { DurationMs = started.ElapsedMilliseconds };
    }

    /// <summary>
    /// The <c>sqlite-snapshot</c> reset, which runs before start because a running app holds its
    /// database file open: the seed command writes the file once, it is kept as a snapshot, and
    /// the snapshot is copied back over the app's file.
    /// </summary>
    public async Task<CheckResult> RestoreSnapshotAsync(CancellationToken ct)
    {
        var reset = _contract.Application.Reset!;
        var started = Stopwatch.StartNew();
        var file = Path.GetFullPath(Path.Combine(_workingCopy, reset.File!));
        var snapshot = Path.Combine(_scratch, "snapshot.db");

        if (!File.Exists(snapshot))
        {
            if (reset.Seed.Count > 0)
            {
                _options.Progress?.Invoke($"Seeding: {string.Join(' ', reset.Seed)}");
                DeleteDatabase(file);
                var seeded = await RunCommandAsync("seed", reset.Seed, reset.WorkingDirectory, reset.Environment, reset.TimeoutSeconds, ct);
                if (!seeded.Succeeded)
                {
                    return Classifier.Fail("reset", CheckKind.Reset, FailureCause.ResetFailed, $"{seeded.Describe("The seed command")}.") with
                    {
                        Evidence = new Evidence { Log = Lines(seeded.Tail) },
                        DurationMs = started.ElapsedMilliseconds,
                    };
                }
            }

            if (!File.Exists(file))
            {
                return Classifier.Fail("reset", CheckKind.Reset, FailureCause.ResetFailed,
                    $"The database file {reset.File} does not exist{(reset.Seed.Count > 0 ? " after the seed command" : "")}.");
            }

            Directory.CreateDirectory(_scratch);
            File.Copy(file, snapshot, overwrite: true);
        }

        try
        {
            DeleteDatabase(file);
            File.Copy(snapshot, file, overwrite: true);
        }
        catch (IOException ex)
        {
            return Classifier.Fail("reset", CheckKind.Reset, FailureCause.ResetFailed, $"Could not restore {reset.File} from the snapshot: {ex.Message}");
        }

        return CheckResult.Pass("reset", CheckKind.Reset) with { Message = $"Restored {reset.File} from its snapshot.", DurationMs = started.ElapsedMilliseconds };
    }

    /// <summary>The <c>command</c>, <c>endpoint</c> and <c>none</c> resets, which run once the app is up.</summary>
    public async Task<CheckResult> ResetAsync(CancellationToken ct)
    {
        var reset = _contract.Application.Reset!;
        var started = Stopwatch.StartNew();
        switch (reset.Strategy)
        {
            case ResetStrategy.None:
                return CheckResult.Pass("reset", CheckKind.Reset) with { Message = "The contract declares no data to reset." };

            case ResetStrategy.Command:
                _options.Progress?.Invoke($"Resetting: {string.Join(' ', reset.Command)}");
                var result = await RunCommandAsync("reset", reset.Command, reset.WorkingDirectory, reset.Environment, reset.TimeoutSeconds, ct);
                return result.Succeeded
                    ? CheckResult.Pass("reset", CheckKind.Reset) with { DurationMs = started.ElapsedMilliseconds }
                    : Classifier.Fail("reset", CheckKind.Reset, FailureCause.ResetFailed, $"{result.Describe("The reset command")}.") with
                    {
                        Evidence = new Evidence { Log = Lines(result.Tail) },
                        DurationMs = started.ElapsedMilliseconds,
                    };

            case ResetStrategy.Endpoint:
                var (method, path) = SplitEndpoint(reset.Endpoint!);
                _options.Progress?.Invoke($"Resetting: {method} {path}");
                try
                {
                    using var response = await SendAsync(method, path, ct);
                    return (int)response.StatusCode is >= 200 and < 300
                        ? CheckResult.Pass("reset", CheckKind.Reset) with { DurationMs = started.ElapsedMilliseconds }
                        : Classifier.Fail("reset", CheckKind.Reset, FailureCause.ResetFailed, $"{method} {path} answered {(int)response.StatusCode}, not 2xx.");
                }
                catch (HttpRequestException ex)
                {
                    return Classifier.Fail("reset", CheckKind.Reset, FailureCause.ResetFailed, $"{method} {path} failed: {ex.Message}");
                }

            default:
                return await RestoreSnapshotAsync(ct);
        }
    }

    /// <summary>
    /// Starts every process in order and waits for each one's readiness. <paramref name="overrides"/>
    /// go over everything, which is how Gate step 7 starts the app as Production.
    /// </summary>
    public async Task<CheckResult> StartAsync(IReadOnlyDictionary<string, string>? overrides, string label, bool strict, CancellationToken ct)
    {
        _label = label;
        var started = Stopwatch.StartNew();
        var listeningBefore = Ports.Listening();

        foreach (var definition in _contract.Application.Processes)
        {
            ProcessLaunch launch;
            try
            {
                launch = Launch(definition.Name, definition.Command, definition.WorkingDirectory, definition.Environment, _values, overrides);
            }
            catch (PlaceholderException ex)
            {
                var cause = ex.Message.Contains("{secret:", StringComparison.Ordinal) ? FailureCause.SecretMissing : FailureCause.ProcessExited;
                return Classifier.Fail("start", CheckKind.Start, cause, $"Could not start {definition.Name}: {ex.Message}");
            }

            _options.Progress?.Invoke($"Starting {definition.Name} on port {_ports[definition.Name]}: {string.Join(' ', definition.Command)}");
            ManagedProcess process;
            try
            {
                process = _launcher.Start(launch);
            }
            catch (ProcessStartException ex)
            {
                return Classifier.Fail("start", CheckKind.Start, FailureCause.ProcessExited, _redactor.Redact(ex.Message));
            }

            _running.Add(process);
            _everStarted.Add((_label.Length == 0 ? definition.Name : $"{definition.Name}-{_label}", process));

            var failure = await WaitReadyAsync(definition, process, definition.Name == _contract.Application.BaseUrl, strict, listeningBefore, ct);
            if (failure is not null)
                return failure with { DurationMs = started.ElapsedMilliseconds };
        }

        StartDuration = started.Elapsed;
        var where = string.Join(", ", Processes.Select(p => $"{p.Name} on {p.Port}"));
        return CheckResult.Pass("start", CheckKind.Start) with
        {
            Message = $"Started {where} in {started.Elapsed.TotalSeconds:0.0}s.",
            DurationMs = started.ElapsedMilliseconds,
        };
    }

    private async Task<CheckResult?> WaitReadyAsync(ProcessDefinition definition, ManagedProcess process, bool isBase, bool strict, HashSet<int> listeningBefore, CancellationToken ct)
    {
        var port = _ports[definition.Name];
        var deadline = Stopwatch.StartNew();
        var limit = TimeSpan.FromSeconds(definition.StartTimeoutSeconds);

        CheckResult Fail(FailureCause cause, string message) =>
            Classifier.Fail("start", CheckKind.Start, cause, _redactor.Redact(message)) with
            {
                Evidence = new Evidence { Log = Lines(process.Log.Tail(TailLines)) },
            };

        // 1. The port accepts a connection.
        while (!await Ports.AcceptsAsync(port, ct))
        {
            if (process.HasExited)
                return Fail(FailureCause.ProcessExited, $"{definition.Name} exited with code {process.Exited.Result} before it was ready.");
            if (deadline.Elapsed > limit)
            {
                var fresh = Ports.Listening().Except(listeningBefore).Except(_ports.Values)
                    .Where(p => _profile.DefaultPorts.Contains(p) || CommonPorts.Contains(p))
                    .Order()
                    .ToList();
                return fresh.Count > 0
                    ? Fail(FailureCause.WrongPort, $"{definition.Name} did not answer on its assigned port {port}, but something new listens on {string.Join(", ", fresh)}: the app ignores the port it was given ({PortHint()}).")
                    : Fail(FailureCause.NotReady, $"{definition.Name} did not answer on port {port} within {limit.TotalSeconds:0}s.");
            }

            await Task.Delay(250, ct);
        }

        // 2. It answers HTTP: its ready path below 400, or GET / below 500.
        var path = definition.Ready?.Http ?? "/";
        string? last = null;
        while (deadline.Elapsed <= limit)
        {
            if (process.HasExited)
                return Fail(FailureCause.ProcessExited, $"{definition.Name} exited with code {process.Exited.Result} before it was ready.");
            try
            {
                using var response = await _http.GetAsync($"http://localhost:{port}{path}", ct);
                var status = (int)response.StatusCode;
                if (isBase && strict && status is >= 300 and < 400 && response.Headers.Location is { } location
                    && (location.IsAbsoluteUri ? location.Scheme : "") == Uri.UriSchemeHttps)
                {
                    return Fail(FailureCause.HttpsRedirect,
                        $"GET {path} redirects to {location}: Testing still forces HTTPS. Turn off HTTPS redirection and Secure-only cookies in Testing, or sign-in over plain HTTP fails.");
                }

                if (definition.Ready?.Http is not null)
                {
                    if (status < 400)
                        return null;
                    last = $"GET {path} answered {status}";
                }
                else
                {
                    if (status < 500 || !strict)
                        return null;
                    return Fail(FailureCause.RootServerError, $"GET / on {definition.Name} answered {status}.");
                }
            }
            catch (HttpRequestException ex)
            {
                last = ex.Message;
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                last = $"GET {path} did not answer within 10s";
            }

            await Task.Delay(250, ct);
        }

        return Fail(FailureCause.NotReady, $"{definition.Name} was not ready within {limit.TotalSeconds:0}s{(last is null ? "" : $": {last}")}.");
    }

    private string PortHint()
    {
        var template = _profile.PortTemplate;
        var hints = template.Environment.Keys.Concat(template.Arguments.Where(a => a.StartsWith('-'))).ToList();
        return hints.Count > 0 ? $"{_profile.Id} passes it as {string.Join(", ", hints)}" : "say where the port goes with {port:<process>}";
    }

    public LogMark Mark() => new(_running.ToDictionary(p => p.Name, p => p.Log.Mark()));

    /// <summary>The app's output lines since <paramref name="mark"/> that count as errors, prefixed with the process name.</summary>
    public IReadOnlyList<string> ErrorLinesSince(LogMark mark)
    {
        var lines = new List<string>();
        foreach (var process in _running)
        {
            var since = mark.Positions.TryGetValue(process.Name, out var position) ? position : 0;
            foreach (var line in process.Log.Since(since))
            {
                var error = (line.Stream == OutputStream.Err && _profile.StderrIsError)
                    || (line.Stream == OutputStream.Out && _logErrorPattern?.IsMatch(line.Text) == true)
                    || _failOnLogPatterns.Any(p => p.IsMatch(line.Text));
                if (error && !string.IsNullOrWhiteSpace(line.Text))
                    lines.Add(_redactor.Redact($"{process.Name}: {line.Text}"));
            }
        }

        return lines.Count > 20 ? [.. lines.Take(20), $"… {lines.Count - 20} more"] : lines;
    }

    public async Task<HttpResponseMessage> SendAsync(string method, string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), BaseUrl + path);
        if (method != "GET")
            request.Content = new StringContent("");
        return await _http.SendAsync(request, ct);
    }

    internal static (string Method, string Path) SplitEndpoint(string endpoint)
    {
        var space = endpoint.IndexOf(' ');
        return space < 0 ? ("POST", endpoint) : (endpoint[..space], endpoint[(space + 1)..]);
    }

    /// <summary>Stops the processes in reverse order, killing each tree, and checks the ports are free again.</summary>
    public async Task StopAsync()
    {
        for (var i = _running.Count - 1; i >= 0; i--)
            _running[i].Dispose();
        _running.Clear();

        foreach (var (name, port) in _ports)
        {
            if (!await Ports.WaitFreeAsync(port, TimeSpan.FromSeconds(5)))
                StopWarnings.Add($"Port {port} ({name}) is still in use after stop.");
        }
    }

    /// <summary>Each process's output, redacted, as one log artifact per process and start.</summary>
    public async Task<IReadOnlyList<string>> SaveLogsAsync(IArtifactSink sink, CancellationToken ct)
    {
        var ids = new List<string>();
        foreach (var (label, process) in _everStarted)
        {
            var text = string.Join('\n', process.Log.Tail(5_000).Select(l => (l.Stream == OutputStream.Err ? "err| " : "out| ") + _redactor.Redact(l.Text)));
            using var content = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text));
            ids.Add(await sink.SaveAsync(ArtifactKind.Log, $"{label}.log", content, ct));
        }

        return ids;
    }

    private async Task<CommandResult> RunCommandAsync(string name, IReadOnlyList<string> command, string? workingDirectory, IReadOnlyDictionary<string, string> environment, int timeoutSeconds, CancellationToken ct)
    {
        ProcessLaunch launch;
        try
        {
            launch = Launch(name, command, workingDirectory, environment, _values.WithoutSecrets(), overrides: null);
        }
        catch (PlaceholderException ex)
        {
            return new CommandResult(null, false, ex.Message, [], TimeSpan.Zero);
        }

        var result = await CommandRunner.RunAsync(_launcher, launch, TimeSpan.FromSeconds(timeoutSeconds), ct, TailLines);
        return result with { StartError = result.StartError is null ? null : _redactor.Redact(result.StartError) };
    }

    /// <summary>
    /// A process's launch: the host's scrubbed environment, then the contract's declared variables
    /// (README §8: added after the scrub, for app processes only), then any overrides.
    /// </summary>
    private ProcessLaunch Launch(string name, IReadOnlyList<string> command, string? workingDirectory, IReadOnlyDictionary<string, string> declared, RunValues values, IReadOnlyDictionary<string, string>? overrides)
    {
        if (command.Count == 0)
            throw new PlaceholderException($"{name} has no command");

        var environment = new Dictionary<string, string>(_options.BaseEnvironment, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in Placeholders.Resolve(declared, values))
            environment[key] = value;
        foreach (var (key, value) in overrides ?? new Dictionary<string, string>())
            environment[key] = value;

        var arguments = command.Select(a => Placeholders.Resolve(a, values)).ToList();
        return new ProcessLaunch(name, arguments[0], arguments[1..], ResolveFolder(workingDirectory), environment);
    }

    private string ResolveFolder(string? relative)
    {
        if (string.IsNullOrEmpty(relative))
            return _workingCopy;
        var full = Path.GetFullPath(Path.Combine(_workingCopy, relative));
        return full.StartsWith(_workingCopy, StringComparison.OrdinalIgnoreCase)
            ? full
            : throw new PlaceholderException($"the folder '{relative}' is outside the working copy");
    }

    private IReadOnlyList<string> Lines(IEnumerable<OutputLine> lines) => [.. lines.Select(l => _redactor.Redact(l.Text))];

    private static void DeleteDatabase(string file)
    {
        foreach (var path in new[] { file, file + "-wal", file + "-shm", file + "-journal" })
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _http.Dispose();
    }
}
