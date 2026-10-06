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

    /// <summary>The hang timeout added to a `dotnet test` that has none.</summary>
    public const string HangTimeoutArguments = "--blame-hang-timeout 2m --blame-hang-dump-type none";

    /// <summary>
    /// The command with a hang timeout added to each `dotnet test` that has none. On F5's re-run a
    /// test spun after a locking change; the run had no timeout, the kernel killed the whole
    /// script after five minutes, twice, and the agent concluded the environment could not run
    /// tests. With this, a hanging test stops after two minutes and the output names it. No dump
    /// is written. JavaScript runners (Vitest, Jest) already time out each test.
    /// </summary>
    public static string? WithHangTimeout(string? command)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Contains("--blame-hang", StringComparison.OrdinalIgnoreCase))
            return command;
        return DotnetTest().Replace(command, match => $"{match.Value} {HangTimeoutArguments}");
    }

    [GeneratedRegex(@"\bdotnet(\.exe)?\s+test\b", RegexOptions.IgnoreCase)]
    private static partial Regex DotnetTest();

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
/// <param name="testOutputDirectory">Where the whole output of a test run is kept when
/// <see cref="TestOutputTrimmer"/> cuts it; null leaves test output whole.</param>
public sealed class GuardedShellTool(ITool shell, WorkingCopyGuard? workingCopy = null, string? testOutputDirectory = null) : ITool
{
    public string Name => shell.Name;

    public string Description => shell.Description;

    public JsonElement ParameterSchema => shell.ParameterSchema;

    public async Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken ct)
    {
        var command = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("command", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

        if ((ShellCommandGuard.Refusal(command) ?? workingCopy?.CommandRefusal(command)) is { } reason)
            return ToolResult.Error($"The factory refused this command: {reason}");

        var result = ShellCommandGuard.WithHangTimeout(command) is { } adjusted && adjusted != command
            ? await shell.InvokeAsync(WithCommand(arguments, adjusted), ct)
            : await shell.InvokeAsync(arguments, ct);

        if (testOutputDirectory is null || !TestOutputTrimmer.IsTestRun(command) || result.Text.Length <= TestOutputTrimmer.KeepWholeUpTo)
            return result;

        Directory.CreateDirectory(testOutputDirectory);
        var path = Path.Combine(testOutputDirectory, $"test-run-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.txt");
        await File.WriteAllTextAsync(path, result.Text, ct);
        return TestOutputTrimmer.Trim(result.Text, path) is { } trimmed ? result with { Text = trimmed } : result;
    }

    private static JsonElement WithCommand(JsonElement arguments, string command)
    {
        var fields = new Dictionary<string, JsonElement>();
        foreach (var property in arguments.EnumerateObject())
            fields[property.Name] = property.Value;
        fields["command"] = JsonSerializer.SerializeToElement(command);
        return JsonSerializer.SerializeToElement(fields);
    }
}
