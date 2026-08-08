using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Litos.Agent.Tools;

namespace Litos.Tools.Shell;

public sealed class ShellTool(IToolApprovalGate approvalGate, TimeSpan? hardTimeout = null) : ITool
{
    /// <summary>
    /// Hard wall-clock cap on a single command, independent of the caller's CancellationToken.
    /// AgentLoop's idle-stream timeout only guards "waiting on the provider" — tool execution is
    /// deliberately unbounded by that, by design (see AgentLoop's _streamIdleTimeout doc comment),
    /// since a slow build or long-running dev command is expected. But a command that never
    /// produces output and never exits (e.g. a CLI blocking on a stdin prompt it will never
    /// receive, since stdin isn't redirected here) has no other backstop: the caller's ct only
    /// fires if a user manually hits Cancel, so a face left unattended mid-turn hangs forever.
    /// Observed: `npx create-vite` against a non-empty directory silently waited on an
    /// overwrite-confirmation prompt for 14+ minutes with zero output before the user gave up.
    /// Overridable (default 5 minutes) purely so tests can exercise the timeout path without
    /// a real 5-minute wait — production callers always get the default.
    /// </summary>
    private readonly TimeSpan _hardTimeout = hardTimeout ?? TimeSpan.FromMinutes(5);

    /// <summary>
    /// On macOS/Linux, a GUI app bundle launched from Finder/Dock/<c>open</c> inherits launchd's
    /// bare-bones PATH (e.g. <c>/usr/bin:/bin:/usr/sbin:/sbin</c>), not the interactive-shell PATH
    /// a user gets in Terminal — tools installed via a `.zshrc`/`.zprofile` PATH export (dotnet,
    /// Homebrew, nvm, etc.) are invisible to every command this tool runs, even though the same
    /// command works fine when the user types it themselves. We resolve the user's real login-shell
    /// PATH once (by asking their actual $SHELL to report it, sourcing their profile the same way
    /// Terminal would) and reuse it for the lifetime of the process. Resolution failure (e.g. no
    /// real shell available, sandboxed/CI environment) just falls back to the inherited PATH —
    /// same behavior as before this existed.
    /// </summary>
    private static readonly Lazy<string?> LoginShellPath = new(ResolveLoginShellPath);

    public string Name => "shell";

    public string Description =>
        "Run a shell command and return its combined stdout/stderr output. " +
        "NEVER use this to search file contents — do not invoke grep, rg, findstr, or Select-String " +
        "through this tool. Use search_code instead; it is faster, token-budgeted, and ignore-aware.";

    public JsonElement ParameterSchema { get; } = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new { command = new { type = "string", description = "The shell command to execute." } },
        required = new[] { "command" },
    });

    public async Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken ct)
    {
        var command = arguments.GetProperty("command").GetString();
        if (string.IsNullOrWhiteSpace(command))
            return ToolResult.Error("A 'command' argument is required.");

        var decision = await approvalGate.RequestAsync(
            new ToolInvocationPreview(Name, "Run shell command", command), ct);
        if (decision == ApprovalDecision.Deny)
            return ToolResult.Error("User denied this shell command.");

        var (fileName, argumentsPrefix) = OperatingSystem.IsWindows()
            ? ("cmd.exe", "/c ")
            : ("/bin/sh", "-c ");

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = argumentsPrefix + EscapeArgument(command),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (!OperatingSystem.IsWindows() && LoginShellPath.Value is { } loginPath)
            startInfo.Environment["PATH"] = loginPath;

        using var process = new Process { StartInfo = startInfo };
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(_hardTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Hit HardTimeout, not the caller's own cancellation. Kill the whole tree — cmd.exe
            // spawning e.g. npx/node means the immediate child exiting isn't enough to stop work.
            TryKillProcessTree(process);
            var partial = output.ToString();
            return ToolResult.Error(
                $"Command timed out after {_hardTimeout.TotalMinutes:0}m and was killed. " +
                $"It may be waiting on interactive input (this tool does not support that) — " +
                $"pass a non-interactive flag (e.g. --yes) or avoid commands that prompt.\n{partial}");
        }
        catch (OperationCanceledException)
        {
            // Genuine user cancel (ct itself, not just the linked timeout token) — still don't
            // leave the process tree running in the background after the turn was aborted.
            TryKillProcessTree(process);
            throw;
        }

        var result = output.ToString();
        return process.ExitCode == 0
            ? ToolResult.Ok($"[exit {process.ExitCode}]\n{result}")
            : ToolResult.Error($"Command exited with code {process.ExitCode}.\n{result}");
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Process may have exited in the gap between the timeout firing and the kill call —
            // nothing left to clean up.
        }
    }

    private static string EscapeArgument(string command) => $"\"{command.Replace("\"", "\\\"")}\"";

    /// <summary>
    /// Asks the user's actual login shell (`$SHELL`, falling back to zsh — the macOS default —
    /// then bash) for its PATH as an interactive login shell would compute it, i.e. after sourcing
    /// `.zprofile`/`.zshrc`/`.bash_profile` the same way Terminal.app does. `-il` covers both login
    /// (`.zprofile`) and interactive (`.zshrc`) profile files since PATH exports commonly live in
    /// either depending on the user's setup. Sentinel markers isolate the PATH value from any
    /// shell-startup banner/MOTD noise that might precede it in stdout.
    /// </summary>
    private static string? ResolveLoginShellPath()
    {
        const string marker = "__LITOS_SHELL_PATH__";
        var candidates = new[] { Environment.GetEnvironmentVariable("SHELL"), "/bin/zsh", "/bin/bash" };

        foreach (var shell in candidates)
        {
            if (string.IsNullOrWhiteSpace(shell) || !File.Exists(shell))
                continue;

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = shell,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                startInfo.ArgumentList.Add("-ilc");
                startInfo.ArgumentList.Add($"echo {marker}$PATH{marker}");

                using var process = Process.Start(startInfo);
                if (process is null)
                    continue;

                process.StandardInput.Close();
                var stdout = process.StandardOutput.ReadToEnd();
                if (!process.WaitForExit(5000))
                {
                    TryKillProcessTree(process);
                    continue;
                }

                var start = stdout.IndexOf(marker, StringComparison.Ordinal);
                if (start < 0)
                    continue;
                start += marker.Length;
                var end = stdout.IndexOf(marker, start, StringComparison.Ordinal);
                if (end < 0)
                    continue;

                var path = stdout[start..end].Trim();
                if (!string.IsNullOrEmpty(path))
                    return path;
            }
            catch
            {
                // Try the next candidate shell.
            }
        }

        return null;
    }
}
