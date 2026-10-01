using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Infrastructure.Processes;

namespace Litos.SoftwareFactory.Infrastructure.Workers;

/// <summary>How to start the worker: the executable itself, or `dotnet` with the worker's dll
/// as a prefix argument during development.</summary>
public sealed record WorkerCommand(string Executable, IReadOnlyList<string> PrefixArguments)
{
    public WorkerCommand(string executable) : this(executable, [])
    {
    }
}

public sealed class WorkerLaunchException(string message) : Exception(message);

/// <summary>
/// The worker's environment, built from nothing rather than inherited
/// (docs/software-factory/m1-architecture.md §5). A worker runs agent-chosen commands, so it is
/// given only what a build needs to work: no database connection string, no provider keys and
/// no GitHub token can reach it, whatever the host's own environment holds.
/// </summary>
public static class WorkerEnvironment
{
    /// <summary>Passed through by exact name when the host has them.</summary>
    private static readonly string[] AllowedNames =
    [
        "PATH", "PATHEXT", "SystemRoot", "SystemDrive", "windir", "ComSpec", "TEMP", "TMP", "TMPDIR",
        "USERPROFILE", "HOME", "HOMEDRIVE", "HOMEPATH", "APPDATA", "LOCALAPPDATA", "ProgramData",
        "ProgramFiles", "ProgramFiles(x86)", "CommonProgramFiles", "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE",
        "OS", "LANG", "LC_ALL",
    ];

    /// <summary>Passed through by prefix: toolchain configuration such as DOTNET_ROOT,
    /// NUGET_PACKAGES and npm_config_cache.</summary>
    private static readonly string[] AllowedPrefixes = ["DOTNET_", "NUGET_", "npm_config_"];

    /// <summary>Never passed, even when a name or prefix above would allow it: these carry
    /// credentials (npm's registry token, a NuGet feed key).</summary>
    private static readonly string[] SecretMarkers = ["TOKEN", "SECRET", "PASSWORD", "PASSWD", "CREDENTIAL", "APIKEY", "API_KEY", "_AUTH", "CONNECTIONSTRING"];

    public static IReadOnlyDictionary<string, string> Build(IReadOnlyDictionary<string, string> hostEnvironment, WorkerLaunch launch)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in hostEnvironment)
        {
            if (IsAllowed(name))
                environment[name] = value;
        }

        environment[FactoryWire.WorkerSecretVariable] = launch.Secret;
        environment[FactoryWire.HostUrlVariable] = launch.HostUrl;
        environment[FactoryWire.RunIdVariable] = launch.RunId;
        environment["CI"] = "true";
        return environment;
    }

    internal static bool IsAllowed(string name)
    {
        var allowed = AllowedNames.Contains(name, StringComparer.OrdinalIgnoreCase)
            || AllowedPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return allowed && !SecretMarkers.Any(marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyDictionary<string, string> CurrentHostEnvironment()
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && entry.Value is string value)
                environment[name] = value;
        }

        return environment;
    }
}

/// <summary>
/// V1's worker launcher: one local process per run, started in the run's working copy. That
/// directory matters — the agent's shell tool inherits the process directory and sets none of
/// its own. A container or VM per run is a different IWorkerLauncher, not a change here.
/// </summary>
public sealed class LocalProcessWorkerLauncher(
    WorkerCommand command, Func<IReadOnlyDictionary<string, string>>? hostEnvironment = null, TimeSpan? handshakeTimeout = null)
    : IWorkerLauncher
{
    private readonly Func<IReadOnlyDictionary<string, string>> _hostEnvironment = hostEnvironment ?? WorkerEnvironment.CurrentHostEnvironment;
    private readonly TimeSpan _handshakeTimeout = handshakeTimeout ?? TimeSpan.FromSeconds(30);

    /// <summary>The worker's arguments: who its parent is, which model it uses and where its
    /// transcripts go. Nothing secret travels here — command lines are visible to other processes.</summary>
    public static IReadOnlyList<string> BuildArguments(WorkerLaunch launch, int parentProcessId)
    {
        List<string> arguments =
        [
            "--parent-pid", parentProcessId.ToString(),
            "--provider", launch.Provider,
            "--model", launch.Model,
            "--data-dir", launch.DataDirectory,
        ];
        if (launch.ContextLength is { } contextLength)
            arguments.AddRange(["--context-length", contextLength.ToString()]);
        return arguments;
    }

    public static string LogPath(WorkerLaunch launch) => Path.Combine(launch.DataDirectory, "runs", launch.RunId, "worker.log");

    public async Task<IWorkerHandle> LaunchAsync(WorkerLaunch launch, CancellationToken ct)
    {
        if (!Directory.Exists(launch.WorkingCopy))
            throw new WorkerLaunchException($"The working copy '{launch.WorkingCopy}' does not exist.");

        var environment = WorkerEnvironment.Build(_hostEnvironment(), launch);
        var startInfo = new ProcessStartInfo
        {
            FileName = ExecutableResolver.Resolve(command.Executable, environment.GetValueOrDefault("PATH")),
            WorkingDirectory = launch.WorkingCopy,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in command.PrefixArguments.Concat(BuildArguments(launch, Environment.ProcessId)))
            startInfo.ArgumentList.Add(argument);

        // Cleared, then rebuilt from the allowlist: nothing is inherited by default.
        startInfo.Environment.Clear();
        foreach (var (name, value) in environment)
            startInfo.Environment[name] = value;

        var logPath = LogPath(launch);
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        var log = new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.Read), Encoding.UTF8) { AutoFlush = true };
        var logLock = new Lock();
        var handshake = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        void Log(string prefix, string? line)
        {
            if (line is null)
                return;
            lock (logLock)
            {
                try
                {
                    log.WriteLine(prefix + line);
                }
                catch (ObjectDisposedException)
                {
                    // The worker wrote after its handle was disposed; the line is dropped.
                }
            }
        }

        process.OutputDataReceived += (_, e) =>
        {
            // The first stdout line is the port handshake; everything after it is ordinary output.
            if (e.Data is not null && !handshake.Task.IsCompleted && TryParseHandshake(e.Data, out var port))
                handshake.TrySetResult(port);
            Log("", e.Data);
        };
        process.ErrorDataReceived += (_, e) => Log("[stderr] ", e.Data);
        process.Exited += (_, _) => handshake.TrySetException(
            new WorkerLaunchException($"The worker exited before reporting its port. See {logPath}."));

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            log.Dispose();
            process.Dispose();
            throw new WorkerLaunchException($"The worker '{command.Executable}' could not be started: {ex.Message}");
        }

        var startTime = SafeStartTime(process);
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            var port = await handshake.Task.WaitAsync(_handshakeTimeout, ct);
            return new LocalWorkerHandle(process, startTime, new Uri($"http://127.0.0.1:{port}/"), log);
        }
        catch (Exception ex)
        {
            ProcessRunner.TryKillProcessTree(process);
            process.Dispose();
            lock (logLock)
                log.Dispose();
            throw ex is TimeoutException
                ? new WorkerLaunchException($"The worker did not report its port within {_handshakeTimeout.TotalSeconds:0} seconds. See {logPath}.")
                : ex;
        }
    }

    internal static bool TryParseHandshake(string line, out int port)
    {
        port = 0;
        try
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("port", out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out port)
                && port is > 0 and <= 65_535;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static DateTimeOffset SafeStartTime(Process process)
    {
        try
        {
            return process.StartTime.ToUniversalTime();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return DateTimeOffset.UtcNow; // exited already; the handshake wait reports that
        }
    }

    private sealed class LocalWorkerHandle(Process process, DateTimeOffset startTime, Uri baseAddress, StreamWriter log) : IWorkerHandle
    {
        public int ProcessId { get; } = process.Id;

        public DateTimeOffset StartTime { get; } = startTime;

        public Uri BaseAddress { get; } = baseAddress;

        public bool HasExited
        {
            get
            {
                try
                {
                    return process.HasExited;
                }
                catch (InvalidOperationException)
                {
                    return true;
                }
            }
        }

        public void Kill() => ProcessRunner.TryKillProcessTree(process);

        public ValueTask DisposeAsync()
        {
            Kill();
            process.Dispose();
            log.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// Whether the process that was a run's worker is still that process (§16): the id alone is not
/// enough, because the operating system reuses ids, so the start time must match too.
/// </summary>
public static class WorkerLiveness
{
    public static bool IsAlive(int processId, DateTimeOffset startTime)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited && Math.Abs((process.StartTime.ToUniversalTime() - startTime.UtcDateTime).TotalSeconds) < 2;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
