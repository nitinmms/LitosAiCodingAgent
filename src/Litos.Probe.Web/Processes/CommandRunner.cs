namespace Litos.Probe.Web.Processes;

public sealed record CommandResult(int? ExitCode, bool TimedOut, string? StartError, IReadOnlyList<OutputLine> Tail, TimeSpan Duration)
{
    public bool Succeeded => StartError is null && !TimedOut && ExitCode == 0;

    /// <summary>One line for a check message: why it failed, without the output.</summary>
    public string Describe(string what) =>
        StartError ?? (TimedOut ? $"{what} did not finish within its time limit" : $"{what} exited with code {ExitCode}");
}

/// <summary>Runs a build, seed or reset command to completion, with a hard timeout and tree-kill.</summary>
internal static class CommandRunner
{
    public static async Task<CommandResult> RunAsync(IProcessLauncher launcher, ProcessLaunch launch, TimeSpan timeout, CancellationToken ct, int tailLines = 40)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        ManagedProcess process;
        try
        {
            process = launcher.Start(launch);
        }
        catch (ProcessStartException ex)
        {
            return new CommandResult(null, false, ex.Message, [], started.Elapsed);
        }

        using (process)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout);
            try
            {
                var code = await process.Exited.WaitAsync(limit.Token);
                return new CommandResult(code, false, null, process.Log.Tail(tailLines), started.Elapsed);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new CommandResult(null, true, null, process.Log.Tail(tailLines), started.Elapsed);
            }
        }
    }
}
