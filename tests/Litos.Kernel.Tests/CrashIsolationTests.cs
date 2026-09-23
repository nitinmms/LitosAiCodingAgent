using System.Diagnostics;
using Litos.Agent.Tools;

namespace Litos.Kernel.Tests;

/// <summary>
/// Drives a REAL Litos.Kernel.Host subprocess — unlike InProcessKernelHostFixture, which runs the
/// host's RunLoop over an in-memory pipe and so cannot die — through the failure modes that would
/// take the whole agent down if the kernel were hosted in-process instead of out-of-process.
///
/// Two distinct properties are under test, and they are the reason the kernel is a subprocess at
/// all (ReadMe_PTCPersistentKernel.md §4.2):
///   1. ISOLATION: model-written script code that kills the interpreter (a StackOverflowException
///      is uncatchable in .NET; Environment.Exit is immediate) must not kill the PARENT.
///   2. PROMPT RECOVERY: the caller must be told quickly, not stalled until the hard timeout
///      expires. Before FailPendingEvalsOnProcessDeathAsync existed, a crash was only ever noticed
///      by RunAsync's own timeout — measured at ~21s against a 20s timeout, which at the 5-minute
///      production default meant a five-minute spinner for what is really an instant failure.
///
/// The timing assertions are what pin property 2, so they use a hard timeout deliberately far
/// larger than the time a correct implementation needs; they fail loudly if crash detection ever
/// silently regresses back to the timeout path.
/// </summary>
public sealed class CrashIsolationTests
{
    /// <summary>Generous enough to be nowhere near a correct implementation's actual latency, so this is not a flaky timing test — but far below the hard timeout, so a regression to the timeout path fails it.</summary>
    private static readonly TimeSpan PromptFailureBudget = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan HardTimeout = TimeSpan.FromSeconds(60);

    private static KernelSession NewSession(string name)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "litos-kernel-crash-tests", name, Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(scratch);
        return new KernelSession(
            sessionId: name,
            workingDirectory: Path.GetTempPath(),
            scratchDirectory: scratch,
            bridgedToolsSource: () => new ToolRegistry([]),
            mcpToolProvider: null,
            hardTimeout: HardTimeout);
    }

    [Fact]
    public async Task StackOverflowInScript_KillsOnlyTheSubprocess_AndIsReportedPromptly()
    {
        await using var session = NewSession("stackoverflow");

        // Unbounded recursion -> StackOverflowException, which .NET cannot catch: the runtime
        // terminates the process outright. In-process, this would take the agent down with it.
        var sw = Stopwatch.StartNew();
        var crash = await session.RunAsync("int Boom(int n) => Boom(n + 1); Boom(0)", CancellationToken.None);
        sw.Stop();

        // Getting any answer at all is itself the isolation assertion — the parent is still alive.
        Assert.True(crash.IsError, $"expected an error result, got: {crash.Text}");
        Assert.Contains("kernel", crash.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            sw.Elapsed < PromptFailureBudget,
            $"crash took {sw.Elapsed.TotalSeconds:0.0}s to report; expected well under {PromptFailureBudget.TotalSeconds:0}s. "
            + "A value at or near the hard timeout means crash detection regressed to the timeout path.");

        // The session must transparently respawn a fresh interpreter for the next call.
        var after = await session.RunAsync("21 * 2", CancellationToken.None);
        Assert.False(after.IsError, $"recovery eval failed: {after.Text}");
        Assert.Contains("42", after.Text);
    }

    [Fact]
    public async Task EnvironmentExitInScript_KillsOnlyTheSubprocess_AndIsReportedPromptly()
    {
        await using var session = NewSession("exit");

        var sw = Stopwatch.StartNew();
        var crash = await session.RunAsync("Environment.Exit(1);", CancellationToken.None);
        sw.Stop();

        Assert.True(crash.IsError, $"expected an error result, got: {crash.Text}");
        Assert.True(
            sw.Elapsed < PromptFailureBudget,
            $"crash took {sw.Elapsed.TotalSeconds:0.0}s to report; expected well under {PromptFailureBudget.TotalSeconds:0}s.");

        var after = await session.RunAsync("\"alive\"", CancellationToken.None);
        Assert.False(after.IsError, $"recovery eval failed: {after.Text}");
        Assert.Contains("alive", after.Text);
    }

    [Fact]
    public async Task InfiniteLoopInScript_HitsTheHardTimeout_AndTheSessionStillRecovers()
    {
        // A hang is the one case the hard timeout legitimately owns: the process is alive and
        // stdout is still open, so there is no death to detect. Short timeout here to keep the
        // test fast — this asserts the timeout path itself, not crash detection.
        var scratch = Path.Combine(Path.GetTempPath(), "litos-kernel-crash-tests", "hang", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(scratch);
        await using var session = new KernelSession(
            sessionId: "hang",
            workingDirectory: Path.GetTempPath(),
            scratchDirectory: scratch,
            bridgedToolsSource: () => new ToolRegistry([]),
            mcpToolProvider: null,
            hardTimeout: TimeSpan.FromSeconds(10));

        var hung = await session.RunAsync("while (true) { }", CancellationToken.None);
        Assert.True(hung.IsError, $"expected a timeout error, got: {hung.Text}");
        Assert.Contains("timed out", hung.Text, StringComparison.OrdinalIgnoreCase);

        var after = await session.RunAsync("1 + 1", CancellationToken.None);
        Assert.False(after.IsError, $"recovery eval failed: {after.Text}");
        Assert.Contains("2", after.Text);
    }

    [Fact]
    public async Task OrdinaryScriptException_IsReported_WithoutKillingTheKernelOrLosingState()
    {
        // The complement to the crash cases: a NORMAL exception is caught inside ScriptSession and
        // must NOT trip any of the kill/reset machinery, or every failed eval would silently wipe
        // the persistent state that makes the kernel worth having.
        await using var session = NewSession("throw");

        await session.RunAsync("var keep = 7;", CancellationToken.None);
        var threw = await session.RunAsync("throw new InvalidOperationException(\"boom\");", CancellationToken.None);
        Assert.True(threw.IsError);

        var after = await session.RunAsync("keep * 6", CancellationToken.None);
        Assert.False(after.IsError, $"kernel state was lost after an ordinary exception: {after.Text}");
        Assert.Contains("42", after.Text);
    }
}
