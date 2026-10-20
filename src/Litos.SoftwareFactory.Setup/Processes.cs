using System.Diagnostics;
using System.Text;

namespace Litos.SoftwareFactory.Setup;

/// <param name="ExitCode">-1 when the program could not be started or did not finish in time.</param>
public sealed record ProcessResult(int ExitCode, string Output, string Error)
{
    public bool Succeeded => ExitCode == 0;

    /// <summary>What to show when it failed: its error output, else its output.</summary>
    public string Message => (string.IsNullOrWhiteSpace(Error) ? Output : Error).Trim();
}

/// <summary>Runs the programs litos-factory drives (docker, git, schtasks, the host); faked in tests.</summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct);
}

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        // No window: the windowless build has no console for a child to share, and Windows would
        // open a new one for every docker call.
        var info = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        Process process;
        try
        {
            process = Process.Start(info) ?? throw new InvalidOperationException($"{fileName} did not start.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new ProcessResult(-1, "", $"{fileName} could not be started: {ex.Message}");
        }

        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync(ct);
            var error = process.StandardError.ReadToEndAsync(ct);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(limit.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                ct.ThrowIfCancellationRequested();
                return new ProcessResult(-1, "", $"{Path.GetFileName(fileName)} did not finish within {timeout.TotalSeconds:0} s.");
            }

            return new ProcessResult(process.ExitCode, await output, await error);
        }
    }
}

/// <summary>Finds a program on a PATH, as Windows would run it: only real executables (G10).</summary>
public static class ExecutableSearch
{
    /// <param name="extension">.exe, or .cmd for a script such as npm.</param>
    public static string? Find(string name, string? path, Func<string, bool>? exists = null, string extension = ".exe")
    {
        exists ??= File.Exists;
        foreach (var directory in (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // An extensionless "docker" file earlier on the PATH (found in C:\WINDOWS\system32 on
            // this machine, 2026-10-17) is not a program; only the .exe is.
            var candidate = Path.Combine(directory.Trim('"'), name + extension);
            if (exists(candidate))
                return candidate;
        }

        return null;
    }
}
