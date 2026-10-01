using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Litos.SoftwareFactory.Infrastructure.Processes;

/// <summary>A command to run: an executable and its arguments as an array, never a shell string.</summary>
public sealed record ProcessRequest(string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory)
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Variables added to (or, with a null value, removed from) the inherited environment.</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();

    /// <summary>When set, everything the command writes is also appended to this file.</summary>
    public string? LogPath { get; init; }

    /// <summary>How much of each stream is kept in memory. The log file always gets all of it.</summary>
    public int MaxCapturedChars { get; init; } = 200_000;
}

public sealed record ProcessResult(int? ExitCode, string StandardOutput, string StandardError, TimeSpan Duration, bool TimedOut, bool Started)
{
    public bool Succeeded => Started && !TimedOut && ExitCode == 0;

    /// <summary>The executable could not be started at all — typically it is not installed.</summary>
    public static ProcessResult NotStarted(string reason) => new(null, "", reason, TimeSpan.Zero, TimedOut: false, Started: false);
}

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct);
}

/// <summary>
/// Runs one command to completion for the factory's git and verification steps: no shell, no
/// standard input, a hard timeout, and the whole process tree killed on timeout or cancellation
/// so a test runner left in watch mode, or a build server it spawned, cannot outlive the step.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ExecutableResolver.Resolve(request.Executable, EffectivePath(request)),
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Redirected and closed straight away: a command that reads stdin sees end-of-file
            // instead of waiting forever for input nobody will type.
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in request.Arguments)
            startInfo.ArgumentList.Add(argument);
        foreach (var (name, value) in request.Environment)
        {
            if (value is null)
                startInfo.Environment.Remove(name);
            else
                startInfo.Environment[name] = value;
        }

        using var process = new Process { StartInfo = startInfo };
        var stdout = new BoundedBuffer(request.MaxCapturedChars);
        var stderr = new BoundedBuffer(request.MaxCapturedChars);
        using var log = OpenLog(request.LogPath);
        var logLock = new Lock();

        void Capture(BoundedBuffer buffer, string? line)
        {
            if (line is null)
                return;
            buffer.AppendLine(line);
            if (log is not null)
            {
                lock (logLock)
                    log.WriteLine(line);
            }
        }

        process.OutputDataReceived += (_, e) => Capture(stdout, e.Data);
        process.ErrorDataReceived += (_, e) => Capture(stderr, e.Data);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException or DirectoryNotFoundException)
        {
            return ProcessResult.NotStarted($"'{request.Executable}' could not be started: {ex.Message}");
        }

        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(request.Timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The timeout fired, not the caller: report it rather than throwing.
            TryKillProcessTree(process);
            return new ProcessResult(null, stdout.ToString(), stderr.ToString(), stopwatch.Elapsed, TimedOut: true, Started: true);
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            throw;
        }

        // WaitForExitAsync can return before the last redirected lines are delivered; the
        // parameterless WaitForExit drains them.
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString(), stopwatch.Elapsed, TimedOut: false, Started: true);
    }

    /// <summary>The PATH the command will run with: the request's own, when it sets one.</summary>
    private static string? EffectivePath(ProcessRequest request) =>
        request.Environment.FirstOrDefault(e => string.Equals(e.Key, "PATH", StringComparison.OrdinalIgnoreCase)) is { Key: not null } path
            ? path.Value
            : System.Environment.GetEnvironmentVariable("PATH");

    private static StreamWriter? OpenLog(string? path)
    {
        if (path is null)
            return null;

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        return new StreamWriter(path, append: true, Encoding.UTF8) { AutoFlush = true };
    }

    internal static void TryKillProcessTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5_000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already exited, or could not be killed; nothing more can be done from here.
        }
    }

    /// <summary>Keeps the first part of a stream and notes that the rest was dropped.</summary>
    private sealed class BoundedBuffer(int maxChars)
    {
        private readonly StringBuilder _text = new();
        private bool _truncated;

        public void AppendLine(string line)
        {
            lock (_text)
            {
                if (_truncated)
                    return;
                if (_text.Length + line.Length > maxChars)
                {
                    _text.AppendLine("[output truncated]");
                    _truncated = true;
                    return;
                }

                _text.AppendLine(line);
            }
        }

        public override string ToString()
        {
            lock (_text)
                return _text.ToString();
        }
    }
}

/// <summary>
/// Finds what a bare command name refers to on Windows. Without a shell, Windows only finds
/// `.exe` files by name, but tools such as npm are installed as `npm.cmd`; this applies the same
/// PATH and PATHEXT search a shell would, so a profile can say "npm" on every platform.
/// </summary>
public static class ExecutableResolver
{
    public static string Resolve(string executable, string? path) =>
        OperatingSystem.IsWindows() ? ResolveOnWindows(executable, path, Environment.GetEnvironmentVariable("PATHEXT")) : executable;

    internal static string ResolveOnWindows(string executable, string? path, string? pathExtensions)
    {
        // A path, or a name that already has an extension, is used as written.
        if (executable.IndexOfAny(['\\', '/']) >= 0 || Path.HasExtension(executable))
            return executable;

        var extensions = (pathExtensions ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var directory in (path ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var extension in extensions)
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(directory.Trim('"'), executable + extension);
                }
                catch (ArgumentException)
                {
                    continue; // a malformed PATH entry
                }

                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return executable;
    }
}
