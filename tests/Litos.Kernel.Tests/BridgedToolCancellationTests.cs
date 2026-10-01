using System.Text.Json;
using Litos.Agent.Tools;
using Litos.Kernel;

namespace Litos.Kernel.Tests;

/// <summary>
/// Drives a REAL Litos.Kernel.Host subprocess, like CrashIsolationTests: what is asserted is that
/// a tool called from kernel code is handed the eval's cancellation token rather than
/// CancellationToken.None. Before that, cancelling a turn could not stop a long bridged tool call
/// (a shell command, say); only the process-tree kill afterwards ended it.
/// </summary>
public sealed class BridgedToolCancellationTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    /// <summary>Signals when it starts, then waits until its token is cancelled (or it is told to finish).</summary>
    private sealed class WaitingTool : ITool
    {
        public string Name => "wait_tool";

        public string Description => "Waits until cancelled.";

        public JsonElement ParameterSchema { get; } = JsonSerializer.SerializeToElement(new { type = "object" });

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool TokenCouldBeCancelled { get; private set; }

        public bool ReturnImmediately { get; init; }

        public async Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken ct)
        {
            TokenCouldBeCancelled = ct.CanBeCanceled;
            Started.TrySetResult();
            if (ReturnImmediately)
            {
                Finished.TrySetResult(false);
                return ToolResult.Ok("done");
            }

            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                Finished.TrySetResult(true);
                throw;
            }

            return ToolResult.Ok("unreachable");
        }
    }

    private static KernelSession NewSession(string name, ITool tool, TimeSpan hardTimeout)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "litos-kernel-cancel-tests", name, Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(scratch);
        return new KernelSession(
            sessionId: name,
            workingDirectory: Path.GetTempPath(),
            scratchDirectory: scratch,
            bridgedToolsSource: () => new ToolRegistry([tool]),
            mcpToolProvider: null,
            hardTimeout: hardTimeout);
    }

    [Fact]
    public async Task CancellingTheTurn_CancelsTheBridgedToolCallInFlight()
    {
        var tool = new WaitingTool();
        await using var session = NewSession("cancel", tool, hardTimeout: TimeSpan.FromMinutes(2));
        using var turn = new CancellationTokenSource();

        var eval = session.RunAsync("await wait_tool(\"{}\")", turn.Token);
        await tool.Started.Task.WaitAsync(Patience);

        turn.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => eval);
        Assert.True(await tool.Finished.Task.WaitAsync(Patience), "The bridged tool never saw the cancellation.");
        Assert.True(tool.TokenCouldBeCancelled);
    }

    [Fact]
    public async Task EvalHardTimeout_AlsoCancelsTheBridgedToolCallInFlight()
    {
        var tool = new WaitingTool();
        await using var session = NewSession("timeout", tool, hardTimeout: TimeSpan.FromSeconds(4));

        var result = await session.RunAsync("await wait_tool(\"{}\")", CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("timed out", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(await tool.Finished.Task.WaitAsync(Patience), "The bridged tool outlived the eval's hard timeout.");
    }

    [Fact]
    public async Task BridgedToolThatCompletes_StillWorks_AndTheNextEvalGetsAFreshToken()
    {
        var tool = new WaitingTool { ReturnImmediately = true };
        await using var session = NewSession("normal", tool, hardTimeout: TimeSpan.FromMinutes(2));
        using var firstTurn = new CancellationTokenSource();

        var first = await session.RunAsync("await wait_tool(\"{}\")", firstTurn.Token);
        firstTurn.Cancel(); // the first turn is over; cancelling its token must not touch the next eval
        var second = await session.RunAsync("await wait_tool(\"{}\")", CancellationToken.None);

        Assert.False(first.IsError, first.Text);
        Assert.Contains("done", first.Text);
        Assert.False(second.IsError, second.Text);
        Assert.Contains("done", second.Text);
        Assert.True(tool.TokenCouldBeCancelled);
    }

    [Fact]
    public async Task AfterACancelledTurn_TheSessionRecoversForTheNextEval()
    {
        var tool = new WaitingTool();
        await using var session = NewSession("recover", tool, hardTimeout: TimeSpan.FromMinutes(2));
        using var turn = new CancellationTokenSource();
        var eval = session.RunAsync("await wait_tool(\"{}\")", turn.Token);
        await tool.Started.Task.WaitAsync(Patience);
        turn.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => eval);

        var after = await session.RunAsync("21 * 2", CancellationToken.None);

        Assert.False(after.IsError, after.Text);
        Assert.Contains("42", after.Text);
    }
}
