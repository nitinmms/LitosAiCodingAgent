using System.Text.Json;
using System.Text.RegularExpressions;
using Litos.Agent.Tools;

namespace Litos.SoftwareFactory.Worker;

/// <summary>
/// Refuses shell commands that reach beyond the task: stopping processes by name, and shutting
/// the machine down. On the fifth real task the agent ran <c>taskkill /F /IM dotnet.exe /T</c>
/// to clear a hung test host. That stops every dotnet process on the machine — its own worker,
/// and anything else the person at that machine was running.
///
/// This is a guard against a mistake, not a sandbox. A worker runs with the user's rights, and
/// code the agent writes can still do what this refuses; isolating a run properly is what a
/// container or VM per run is for (ReadMe_LitosSoftwareFactory_V1.md §17). What this does is
/// make the obvious, well-meant, destructive command fail with an explanation instead of
/// succeeding.
/// </summary>
public static partial class ShellCommandGuard
{
    private const string ByName =
        "it stops processes by name, which stops every matching program on this machine: the factory's own worker, " +
        "and anything else running here. Stop only a process you started, by its id: `taskkill /PID <id> /T` or `Stop-Process -Id <id>`. " +
        "If a test run hangs, give the test command a timeout instead.";

    private const string Machine = "it would shut down, restart or log off the machine the factory runs on.";

    /// <summary>Why the command is refused, or null when it may run.</summary>
    public static string? Refusal(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;

        if (TaskkillByName().IsMatch(command) || StopProcessByName().IsMatch(command) || KillByName().IsMatch(command) || WmicTerminate().IsMatch(command))
            return ByName;
        if (MachinePower().IsMatch(command))
            return Machine;
        return null;
    }

    // taskkill with an image name or a filter. `taskkill /PID 1234` names one process and is fine.
    [GeneratedRegex(@"\btaskkill(\.exe)?\b[^|&;\r\n]*\s[/-](im|fi)\b", RegexOptions.IgnoreCase)]
    private static partial Regex TaskkillByName();

    // Stop-Process (or its aliases) by name, or fed from Get-Process. `Stop-Process -Id 1234` is fine.
    [GeneratedRegex(@"(\b(stop-process|spps)\b[^|&;\r\n]*\s-(name|processname)\b)|(\b(get-process|gps|ps)\b[^&;\r\n]*\|\s*(stop-process|spps|kill)\b)", RegexOptions.IgnoreCase)]
    private static partial Regex StopProcessByName();

    // pkill and killall take a name; `kill -1`/`kill -9 -1` signals every process the user owns.
    [GeneratedRegex(@"(^|[\s;&|(])(pkill|killall)\b|(^|[\s;&|(])kill\s+(-\w+\s+)*-1\b", RegexOptions.IgnoreCase)]
    private static partial Regex KillByName();

    [GeneratedRegex(@"\bwmic\b[^|&;\r\n]*\bprocess\b[^|&;\r\n]*\b(delete|terminate)\b", RegexOptions.IgnoreCase)]
    private static partial Regex WmicTerminate();

    [GeneratedRegex(@"(^|[\s;&|(])(shutdown(\.exe)?\s+[/-]|restart-computer\b|stop-computer\b|logoff\b|reboot\b|poweroff\b|halt\b)", RegexOptions.IgnoreCase)]
    private static partial Regex MachinePower();
}

/// <summary>The shell tool as the factory gives it to an agent: the same tool, behind <see cref="ShellCommandGuard"/>.</summary>
public sealed class GuardedShellTool(ITool shell, WorkingCopyGuard? workingCopy = null) : ITool
{
    public string Name => shell.Name;

    public string Description => shell.Description;

    public JsonElement ParameterSchema => shell.ParameterSchema;

    public Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken ct)
    {
        var command = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("command", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

        return (ShellCommandGuard.Refusal(command) ?? workingCopy?.CommandRefusal(command)) is { } reason
            ? Task.FromResult(ToolResult.Error($"The factory refused this command: {reason}"))
            : shell.InvokeAsync(arguments, ct);
    }
}
