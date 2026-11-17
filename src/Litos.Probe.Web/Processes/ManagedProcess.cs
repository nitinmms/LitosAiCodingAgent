using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Litos.Probe.Web.Processes;

/// <summary>
/// One process to start: an executable and its arguments (never a shell string), in a folder,
/// with exactly <see cref="Environment"/> as its environment: nothing is inherited.
/// </summary>
public sealed record ProcessLaunch(string Name, string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory, IReadOnlyDictionary<string, string> Environment);

/// <summary>Starts processes. The default starts them on this machine; a host may supply its own (a sandbox, later).</summary>
public interface IProcessLauncher
{
    /// <exception cref="ProcessStartException">The executable could not be started at all.</exception>
    ManagedProcess Start(ProcessLaunch launch);
}

public sealed class ProcessStartException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class ProcessLauncher : IProcessLauncher
{
    public ManagedProcess Start(ProcessLaunch launch) => ManagedProcess.Start(launch);
}

/// <summary>
/// A started process whose whole tree goes when it is disposed: in a Job Object on Windows, and
/// by <see cref="Process.Kill(bool)"/> everywhere. Standard input is closed, so nothing waits
/// for a person to type. Both output streams go to <see cref="Log"/>.
/// </summary>
public sealed class ManagedProcess : IDisposable
{
    private readonly Process _process;
    private readonly IDisposable? _job;
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    private ManagedProcess(string name, Process process, IDisposable? job, OutputLog log)
    {
        Name = name;
        _process = process;
        _job = job;
        Log = log;
    }

    public string Name { get; }
    public OutputLog Log { get; }
    public int Id => _process.Id;

    /// <summary>Completes with the exit code once the process has exited and its output is drained.</summary>
    public Task<int> Exited => _exited.Task;

    public bool HasExited => _exited.Task.IsCompleted;

    public static ManagedProcess Start(ProcessLaunch launch)
    {
        var path = launch.Environment.FirstOrDefault(e => string.Equals(e.Key, "PATH", StringComparison.OrdinalIgnoreCase)).Value;
        var startInfo = new ProcessStartInfo
        {
            FileName = ExecutableResolver.Resolve(launch.Executable, path),
            WorkingDirectory = launch.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in launch.Arguments)
            startInfo.ArgumentList.Add(argument);
        startInfo.Environment.Clear();
        foreach (var (name, value) in launch.Environment)
            startInfo.Environment[name] = value;

        var log = new OutputLog();
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                log.Append(OutputStream.Out, e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                log.Append(OutputStream.Err, e.Data);
        };

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException or DirectoryNotFoundException or InvalidOperationException)
        {
            process.Dispose();
            throw new ProcessStartException($"'{launch.Executable}' could not be started: {ex.Message}", ex);
        }

        IDisposable? job = null;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var windowsJob = new JobObject();
                windowsJob.Add(process);
                job = windowsJob;
            }
            catch (Win32Exception)
            {
                // Already in a job that forbids nesting: Kill(entireProcessTree) still covers it.
            }
        }

        var managed = new ManagedProcess(launch.Name, process, job, log);
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _ = managed.WatchExitAsync();
        return managed;
    }

    private async Task WatchExitAsync()
    {
        try
        {
            await _process.WaitForExitAsync();
            // WaitForExitAsync can return before the last redirected lines arrive; this drains them.
            _process.WaitForExit();
            _exited.TrySetResult(_process.ExitCode);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            _exited.TrySetResult(-1);
        }
    }

    /// <summary>Kills the whole tree and waits briefly for it to go.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(10_000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already gone.
        }
        finally
        {
            _job?.Dispose();
            _process.Dispose();
        }
    }
}
