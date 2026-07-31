using System.Text.Json;
using Litos.Tools.Shell;
using Litos.Tools.Tests.Fakes;

namespace Litos.Tools.Tests.Shell;

// Deliberately no real-process (Process.Start) tests here — ShellTool has no injectable
// process-runner seam, and this suite covers the guard-rail logic (validation, approval-gate
// interaction) without spawning real OS processes, which would be slow and platform-specific.
public class ShellToolTests
{
    private static JsonElement Args(object obj) => JsonSerializer.SerializeToElement(obj);

    [Fact]
    public async Task InvokeAsync_WhitespaceCommand_ReturnsError_WithoutCallingGate()
    {
        var gate = new FakeApprovalGate();
        var tool = new ShellTool(gate);

        var result = await tool.InvokeAsync(Args(new { command = "   " }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("A 'command' argument is required.", result.Text);
        Assert.Equal(0, gate.CallCount);
    }

    [Fact]
    public async Task InvokeAsync_Denied_ReturnsError_WithoutRunningCommand()
    {
        var gate = new FakeApprovalGate { Decision = ApprovalDecision.Deny };
        var tool = new ShellTool(gate);

        var result = await tool.InvokeAsync(Args(new { command = "echo hi" }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("User denied this shell command.", result.Text);
    }

    [Fact]
    public async Task InvokeAsync_GateReceivesCommandAsPreview()
    {
        var gate = new FakeApprovalGate { Decision = ApprovalDecision.Deny };
        var tool = new ShellTool(gate);

        await tool.InvokeAsync(Args(new { command = "rm -rf /" }), CancellationToken.None);

        Assert.NotNull(gate.LastPreview);
        Assert.Equal("shell", gate.LastPreview!.ToolName);
        Assert.Equal("Run shell command", gate.LastPreview.Summary);
        Assert.Equal("rm -rf /", gate.LastPreview.DiffOrCommand);
    }

    [Fact]
    public async Task InvokeAsync_MissingCommandProperty_Throws()
    {
        var gate = new FakeApprovalGate();
        var tool = new ShellTool(gate);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => tool.InvokeAsync(Args(new { }), CancellationToken.None));
    }

    [Fact]
    public void Description_PointsModelToSearchCode_InsteadOfShellingOutForSearch()
    {
        var tool = new ShellTool(new FakeApprovalGate());

        Assert.Contains("search_code", tool.Description);
    }

    // These exercise the real Process.Start path (unlike the suite above), because the behavior
    // under test — a hung command actually gets killed on a wall-clock timeout, independent of
    // the caller's own CancellationToken — can only be observed by running a real process.
    //
    // `ping -n <count> 127.0.0.1` is used as a stand-in for a genuinely hung command: it blocks
    // for a predictable, real wall-clock duration regardless of console/stdin state. `pause` was
    // tried first (it's the closer analogue to the real npx interactive-prompt hang this fix
    // targets) but was rejected — under CreateNoWindow with no real console attached, `pause`
    // reads EOF from the closed stdin handle and returns in ~30ms instead of blocking, so it
    // can't actually simulate a hang here.
    private const string LongRunningCommand = "ping -n 30 127.0.0.1 >nul";

    [Fact]
    public async Task InvokeAsync_CommandExceedsHardTimeout_KillsProcessAndReturnsError()
    {
        var gate = new FakeApprovalGate { Decision = ApprovalDecision.Approve };
        var tool = new ShellTool(gate, hardTimeout: TimeSpan.FromMilliseconds(500));

        var result = await tool.InvokeAsync(Args(new { command = LongRunningCommand }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("timed out", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvokeAsync_CommandExceedsHardTimeout_DoesNotWaitForFullCommandDuration()
    {
        var gate = new FakeApprovalGate { Decision = ApprovalDecision.Approve };
        var tool = new ShellTool(gate, hardTimeout: TimeSpan.FromMilliseconds(500));

        // LongRunningCommand runs ~30s uninterrupted; a passing test proves the hard timeout
        // actually cut it short rather than this just being the test's own default timeout.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await tool.InvokeAsync(Args(new { command = LongRunningCommand }), CancellationToken.None);
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"Expected early return, took {sw.Elapsed}");
    }

    [Fact]
    public async Task InvokeAsync_FastCommand_CompletesNormally_HardTimeoutDoesNotInterfere()
    {
        var gate = new FakeApprovalGate { Decision = ApprovalDecision.Approve };
        var tool = new ShellTool(gate, hardTimeout: TimeSpan.FromSeconds(30));

        var result = await tool.InvokeAsync(Args(new { command = "echo hello" }), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("hello", result.Text);
    }

    [Fact]
    public async Task InvokeAsync_UserCancelsBeforeHardTimeout_PropagatesCancellation()
    {
        var gate = new FakeApprovalGate { Decision = ApprovalDecision.Approve };
        var tool = new ShellTool(gate, hardTimeout: TimeSpan.FromMinutes(5));

        using var cts = new CancellationTokenSource();
        var invokeTask = tool.InvokeAsync(Args(new { command = LongRunningCommand }), cts.Token);
        cts.Cancel();

        // TaskCanceledException (thrown by Process.WaitForExitAsync) derives from
        // OperationCanceledException — ThrowsAnyAsync accepts either, which is what matters here:
        // that cancellation propagates rather than being swallowed into a ToolResult.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invokeTask);
    }
}
