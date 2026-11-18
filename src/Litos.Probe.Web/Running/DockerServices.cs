using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Litos.Probe.Web.Core;
using Litos.Probe.Web.Core.Contract;
using Litos.Probe.Web.Core.Profiles;
using Litos.Probe.Web.Core.Reports;
using Litos.Probe.Web.Processes;

namespace Litos.Probe.Web.Running;

/// <summary>One service as this run starts it: its image entry, its host ports and its resolved values.</summary>
internal sealed record PlannedService(ServiceDefinition Definition, ServiceImage Image, IReadOnlyDictionary<string, int> HostPorts, IReadOnlyDictionary<string, string> Environment)
{
    public string Name => Definition.Name;
}

/// <summary>
/// The contract's <c>services</c> as throwaway Docker containers (testability-architecture.md §6.7),
/// driven through the <c>docker</c> CLI, so there is no Docker SDK dependency. Each container binds
/// loopback only, on a port allocated for this run, and carries labels so a crash can be swept up
/// afterwards. Stopping removes the container and its anonymous volumes.
/// </summary>
internal sealed class DockerServices(IProcessLauncher launcher, ProbeWebOptions options, SecretRedactor redactor, string runId)
{
    /// <summary>On every container Probe.Web starts.</summary>
    public const string Label = "litos.probe";

    /// <summary>Which run started it.</summary>
    public const string RunLabel = "litos.probe.run";

    /// <summary>Who started it (<c>factory</c> or <c>cli</c>), so one sweeps only its own.</summary>
    public const string OwnerLabel = "litos.probe.owner";

    private const int TailLines = 30;
    private static readonly HttpClient Http = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
    private readonly List<string> _containers = [];

    public static string ContainerName(string runId, string service) => $"litos-probe-{runId}-{service}";

    public async Task<CheckResult> StartAsync(IReadOnlyList<PlannedService> services, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        var info = await DockerAsync(["info", "--format", "{{.ServerVersion}}"], TimeSpan.FromSeconds(30), ct);
        if (!info.Succeeded)
        {
            return Classifier.Fail("services", CheckKind.Services, FailureCause.DockerUnavailable,
                $"Docker is not running, and the contract's services need it: start Docker and run again. ({FirstLine(info)})");
        }

        foreach (var service in services)
        {
            options.Progress?.Invoke($"Starting service {service.Name} ({service.Definition.Image})");
            var container = ContainerName(runId, service.Name);
            List<string> arguments =
            [
                "run", "-d", "--name", container,
                "--label", $"{Label}=1", "--label", $"{RunLabel}={runId}", "--label", $"{OwnerLabel}={options.ServiceOwner}",
            ];
            foreach (var (port, hostPort) in service.HostPorts)
                arguments.AddRange(["-p", $"127.0.0.1:{hostPort}:{service.Image.Ports[port]}"]);
            foreach (var (name, value) in service.Environment)
                arguments.AddRange(["-e", $"{name}={value}"]);
            arguments.Add(service.Definition.Image);

            // The first run of an image pulls it, which can take minutes.
            var run = await DockerAsync(arguments, TimeSpan.FromSeconds(600), ct);
            if (!run.Succeeded)
            {
                return Classifier.Fail("services", CheckKind.Services, FailureCause.ServiceNotReady,
                    $"The service {service.Name} ({service.Definition.Image}) could not be started: {FirstLine(run)}") with
                {
                    Evidence = new Evidence { Log = Lines(run.Tail) },
                    DurationMs = started.ElapsedMilliseconds,
                };
            }

            _containers.Add(container);
            if (await WaitReadyAsync(service, container, ct) is { } failure)
                return failure with { DurationMs = started.ElapsedMilliseconds };
        }

        var where = string.Join(", ", services.Select(s => $"{s.Name} on {string.Join("/", s.HostPorts.Values)}"));
        return CheckResult.Pass("services", CheckKind.Services) with
        {
            Message = $"Started {where} in {started.Elapsed.TotalSeconds:0.0}s.",
            DurationMs = started.ElapsedMilliseconds,
        };
    }

    private async Task<CheckResult?> WaitReadyAsync(PlannedService service, string container, CancellationToken ct)
    {
        var limit = TimeSpan.FromSeconds(service.Image.StartTimeoutSeconds);
        var waited = Stopwatch.StartNew();
        var ready = service.Image.Ready;
        var probe = ready.Exec.Select(a => ExpandEnvironment(a, service.Environment)).ToList();

        for (var attempt = 0; waited.Elapsed < limit; attempt++)
        {
            if (probe.Count > 0)
            {
                if ((await DockerAsync(["exec", container, .. probe], TimeSpan.FromSeconds(30), ct)).Succeeded)
                    return null;
            }
            else if (ready.HttpPath is { } path && service.HostPorts.TryGetValue(ready.HttpPort ?? service.Image.PrimaryPort, out var port))
            {
                try
                {
                    using var response = await Http.GetAsync($"http://127.0.0.1:{port}{path}", ct);
                    if ((int)response.StatusCode < 400)
                        return null;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    // Not up yet.
                }
            }
            else
            {
                return null;
            }

            // Now and then, whether it is still running at all: a container that exited won't become ready.
            if (attempt % 5 == 4)
            {
                var state = await DockerAsync(["inspect", "-f", "{{.State.Running}}", container], TimeSpan.FromSeconds(15), ct);
                if (state.Succeeded && state.Tail.LastOrDefault()?.Text.Trim() == "false")
                    return await NotReadyAsync(service, container, $"The service {service.Name} ({service.Definition.Image}) exited before it was ready.", ct);
            }

            await Task.Delay(500, ct);
        }

        return await NotReadyAsync(service, container, $"The service {service.Name} ({service.Definition.Image}) was not ready within {limit.TotalSeconds:0}s.", ct);
    }

    private async Task<CheckResult> NotReadyAsync(PlannedService service, string container, string message, CancellationToken ct)
    {
        var logs = await DockerAsync(["logs", "--tail", TailLines.ToString(System.Globalization.CultureInfo.InvariantCulture), container], TimeSpan.FromSeconds(15), ct);
        return Classifier.Fail("services", CheckKind.Services, FailureCause.ServiceNotReady, redactor.Redact(message)) with
        {
            Evidence = new Evidence { Log = Lines(logs.Tail) },
        };
    }

    /// <summary>
    /// The emails each Mailpit service caught (README_Litos.Probe.Web.md §13.1: captured as evidence;
    /// assertions on them come with scenarios). Each raw message is saved as an <c>.eml</c> artifact.
    /// </summary>
    public async Task<IReadOnlyList<CapturedEmail>> CaptureEmailsAsync(IReadOnlyList<PlannedService> services, IArtifactSink sink, CancellationToken ct)
    {
        var emails = new List<CapturedEmail>();
        foreach (var service in services.Where(s => s.Image.CapturesEmail))
        {
            if (!service.HostPorts.TryGetValue("http", out var port))
                continue;
            try
            {
                using var list = JsonDocument.Parse(await Http.GetStringAsync($"http://127.0.0.1:{port}/api/v1/messages?limit=100", ct));
                var messages = list.RootElement.GetProperty("messages").EnumerateArray().Reverse().ToList();
                foreach (var (message, i) in messages.Select((m, i) => (m, i)))
                {
                    var id = message.GetProperty("ID").GetString()!;
                    var subject = message.TryGetProperty("Subject", out var s) ? s.GetString() ?? "" : "";
                    var from = message.TryGetProperty("From", out var f) && f.ValueKind == JsonValueKind.Object ? f.GetProperty("Address").GetString() ?? "" : "";
                    List<string> to = message.TryGetProperty("To", out var t) && t.ValueKind == JsonValueKind.Array
                        ? [.. t.EnumerateArray().Select(a => a.GetProperty("Address").GetString() ?? "")]
                        : [];
                    DateTimeOffset? sent = message.TryGetProperty("Created", out var c) && c.TryGetDateTimeOffset(out var when) ? when : null;

                    var raw = redactor.Redact(await Http.GetStringAsync($"http://127.0.0.1:{port}/api/v1/message/{id}/raw", ct));
                    using var content = new MemoryStream(Encoding.UTF8.GetBytes(raw));
                    var artifact = await sink.SaveAsync(ArtifactKind.Log, $"email-{i + 1:00}-{GateRun.ArtifactName(subject)}.eml", content, ct);
                    emails.Add(new CapturedEmail(redactor.Redact(from), [.. to.Select(redactor.Redact)], redactor.Redact(subject), sent, artifact));
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
            {
                // Emails are evidence, not a check: a Mailpit that won't answer leaves them out.
            }
        }

        return emails;
    }

    /// <summary>Removes this run's containers and their anonymous volumes. Never throws.</summary>
    public async Task StopAsync()
    {
        if (_containers.Count == 0)
            return;
        await DockerAsync(["rm", "-f", "-v", .. _containers], TimeSpan.FromSeconds(60), CancellationToken.None);
        _containers.Clear();
    }

    /// <summary>
    /// Removes every container a crashed run of <paramref name="owner"/> left behind (README §10:
    /// swept at stop and on host start). Returns how many it removed.
    /// </summary>
    public static async Task<int> SweepAsync(IProcessLauncher launcher, IReadOnlyDictionary<string, string> environment, string owner, CancellationToken ct, string docker = "docker")
    {
        var list = await RunAsync(launcher, environment, docker, ["ps", "-aq", "--filter", $"label={OwnerLabel}={owner}"], TimeSpan.FromSeconds(30), ct);
        if (!list.Succeeded)
            return 0;
        var ids = list.Tail.Where(l => l.Stream == OutputStream.Out && l.Text.Trim().Length > 0).Select(l => l.Text.Trim()).ToList();
        if (ids.Count > 0)
            await RunAsync(launcher, environment, docker, ["rm", "-f", "-v", .. ids], TimeSpan.FromSeconds(60), ct);
        return ids.Count;
    }

    private Task<CommandResult> DockerAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct) =>
        RunAsync(launcher, options.BaseEnvironment, options.DockerExecutable, arguments, timeout, ct);

    private static Task<CommandResult> RunAsync(IProcessLauncher launcher, IReadOnlyDictionary<string, string> environment, string docker, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct) =>
        CommandRunner.RunAsync(launcher, new ProcessLaunch("docker", docker, arguments, Environment.CurrentDirectory, environment), timeout, ct, tailLines: 200);

    /// <summary><c>{env:NAME}</c> in a catalog's readiness command, from the service's own values.</summary>
    internal static string ExpandEnvironment(string text, IReadOnlyDictionary<string, string> environment)
    {
        foreach (var (name, value) in environment)
            text = text.Replace($"{{env:{name}}}", value, StringComparison.Ordinal);
        return text;
    }

    private string FirstLine(CommandResult result) =>
        redactor.Redact(result.StartError
            ?? result.Tail.LastOrDefault(l => l.Stream == OutputStream.Err && l.Text.Trim().Length > 0)?.Text.Trim()
            ?? result.Describe("docker"));

    private IReadOnlyList<string> Lines(IEnumerable<OutputLine> lines) => [.. lines.Select(l => redactor.Redact(l.Text))];
}
