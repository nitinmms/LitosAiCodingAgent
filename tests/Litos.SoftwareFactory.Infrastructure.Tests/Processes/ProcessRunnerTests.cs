using System.Diagnostics;
using Litos.SoftwareFactory.Infrastructure.Processes;

namespace Litos.SoftwareFactory.Infrastructure.Tests.Processes;

/// <summary>
/// Real processes throughout: what this class guarantees (a timeout that actually kills, a
/// process tree that actually dies, no waiting on stdin) can't be shown with a fake.
/// </summary>
public class ProcessRunnerTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ProcessRunner _runner = new();

    public void Dispose() => _temp.Dispose();

    private Task<ProcessResult> RunAsync(string script, TimeSpan? timeout = null, CancellationToken ct = default) =>
        _runner.RunAsync(Shell.Request(script, _temp.Path) with { Timeout = timeout ?? TimeSpan.FromSeconds(30) }, ct);

    [Fact]
    public async Task RunAsync_SuccessfulCommand_ReturnsExitZeroAndItsOutput()
    {
        var result = await RunAsync("echo hello");

        Assert.True(result.Succeeded);
        Assert.True(result.Started);
        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("hello", result.StandardOutput.Trim());
    }

    [Fact]
    public async Task RunAsync_NonZeroExit_IsNotSuccess_AndKeepsTheCode()
    {
        var result = await RunAsync("exit 3");

        Assert.False(result.Succeeded);
        Assert.True(result.Started);
        Assert.Equal(3, result.ExitCode);
    }

    [Fact]
    public async Task RunAsync_CapturesStandardErrorSeparately()
    {
        var result = await RunAsync("echo problem 1>&2");

        Assert.Equal("problem", result.StandardError.Trim());
        Assert.Equal("", result.StandardOutput.Trim());
    }

    [Fact]
    public async Task RunAsync_ArgumentsArePassedAsAnArray_NotThroughAShell()
    {
        // One argument full of shell metacharacters reaches git as one literal value: git echoes
        // it back whole, and nothing after the "&" runs.
        var result = await _runner.RunAsync(
            new ProcessRequest("git", ["-c", "user.name=a & echo injected > pwned.txt", "config", "user.name"], _temp.Path), default);

        Assert.True(result.Succeeded, result.StandardError);
        Assert.Equal("a & echo injected > pwned.txt", result.StandardOutput.Trim());
        Assert.False(File.Exists(_temp.Combine("pwned.txt")));
    }

    [Fact]
    public async Task RunAsync_RunsInTheWorkingDirectory()
    {
        var result = await RunAsync(OperatingSystem.IsWindows() ? "cd" : "pwd");

        Assert.Equal(
            Path.GetFullPath(_temp.Path).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(result.StandardOutput.Trim()).TrimEnd(Path.DirectorySeparatorChar),
            ignoreCase: true);
    }

    [Fact]
    public async Task RunAsync_AddsEnvironmentVariables()
    {
        var request = Shell.Request(OperatingSystem.IsWindows() ? "echo %FACTORY_TEST_VALUE%" : "echo $FACTORY_TEST_VALUE", _temp.Path) with
        {
            Environment = new Dictionary<string, string?> { ["FACTORY_TEST_VALUE"] = "CI-true" },
        };

        var result = await _runner.RunAsync(request, default);

        Assert.Equal("CI-true", result.StandardOutput.Trim());
    }

    [Fact]
    public async Task RunAsync_NullEnvironmentValue_RemovesAnInheritedVariable()
    {
        var name = "FACTORY_INHERITED_" + Guid.NewGuid().ToString("n")[..8].ToUpperInvariant();
        Environment.SetEnvironmentVariable(name, "inherited");
        try
        {
            var script = OperatingSystem.IsWindows() ? $"if defined {name} (echo present) else (echo absent)" : $"[ -n \"${name}\" ] && echo present || echo absent";
            var kept = await RunAsync(script);
            var removed = await _runner.RunAsync(
                Shell.Request(script, _temp.Path) with { Environment = new Dictionary<string, string?> { [name] = null } }, default);

            Assert.Equal("present", kept.StandardOutput.Trim());
            Assert.Equal("absent", removed.StandardOutput.Trim());
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public async Task RunAsync_ExecutableDoesNotExist_ReportsNotStarted_RatherThanThrowing()
    {
        var result = await _runner.RunAsync(new ProcessRequest("no-such-tool-" + Guid.NewGuid().ToString("n"), ["--version"], _temp.Path), default);

        Assert.False(result.Started);
        Assert.False(result.Succeeded);
        Assert.Null(result.ExitCode);
        Assert.Contains("could not be started", result.StandardError);
    }

    [Fact]
    public async Task RunAsync_CommandOutlivesItsTimeout_IsKilledAndReportedAsTimedOut()
    {
        var stopwatch = Stopwatch.StartNew();

        var result = await RunAsync(Shell.Sleep(30), timeout: TimeSpan.FromMilliseconds(500));

        Assert.True(result.TimedOut);
        Assert.False(result.Succeeded);
        Assert.Null(result.ExitCode);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task RunAsync_Timeout_KillsTheWholeProcessTree()
    {
        // The command starts a second shell, which would write the marker after a few seconds.
        // If only the outer process were killed, that grandchild would survive and write it.
        var marker = _temp.Combine("survived.txt");
        Shell.WriteScript(_temp.Path, "child", $"{Shell.Sleep(3)}\necho x> survived.txt", $"{Shell.Sleep(3)}\necho x > survived.txt");
        var parent = Shell.WriteScript(_temp.Path, "parent", "cmd /d /c \"%~dp0child.cmd\"", "sh ./child.sh");

        var result = await _runner.RunAsync(
            new ProcessRequest(parent, [], _temp.Path) { Timeout = TimeSpan.FromMilliseconds(500) }, default);
        await Task.Delay(TimeSpan.FromSeconds(5));

        Assert.True(result.TimedOut, $"exit {result.ExitCode}, started {result.Started}: {result.StandardOutput}{result.StandardError}");
        Assert.False(File.Exists(marker), "A descendant process outlived the timeout.");
    }

    [Fact]
    public async Task RunAsync_Cancelled_KillsTheProcessAndThrows()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(Shell.Sleep(30), ct: cts.Token));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task RunAsync_CommandThatReadsStandardInput_SeesEndOfFile_InsteadOfWaitingForever()
    {
        // `more`/`cat` with no file copies stdin to stdout; with stdin closed it ends at once.
        var result = await RunAsync(OperatingSystem.IsWindows() ? "more" : "cat", timeout: TimeSpan.FromSeconds(20));

        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task RunAsync_LogPath_ReceivesBothStreams_AndAppendsAcrossRuns()
    {
        var log = _temp.Combine("logs", "build.log");
        var request = Shell.Request("echo to-out & echo to-err 1>&2", _temp.Path) with { LogPath = log };

        await _runner.RunAsync(request, default);
        await _runner.RunAsync(request, default);

        var text = File.ReadAllText(log);
        Assert.Equal(2, text.Split("to-out").Length - 1);
        Assert.Equal(2, text.Split("to-err").Length - 1);
    }

    [Fact]
    public async Task RunAsync_OutputBeyondTheCap_IsTruncatedInMemory_ButCompleteInTheLog()
    {
        var log = _temp.Combine("big.log");
        var script = OperatingSystem.IsWindows()
            ? "for /L %i in (1,1,200) do @echo line-%i-0123456789"
            : "i=1; while [ $i -le 200 ]; do echo line-$i-0123456789; i=$((i+1)); done";
        var request = Shell.Request(script, _temp.Path) with { MaxCapturedChars = 500, LogPath = log };

        var result = await _runner.RunAsync(request, default);

        Assert.True(result.Succeeded);
        Assert.True(result.StandardOutput.Length < 700);
        Assert.Contains("[output truncated]", result.StandardOutput);
        Assert.Contains("line-200-", File.ReadAllText(log));
    }

    [Fact]
    public async Task RunAsync_ReportsHowLongTheCommandTook()
    {
        var result = await RunAsync(Shell.Sleep(1));

        Assert.True(result.Duration > TimeSpan.FromMilliseconds(500), $"was {result.Duration}");
    }

    [Fact]
    public void NotStarted_IsNeverASuccess()
    {
        var result = ProcessResult.NotStarted("missing");

        Assert.False(result.Succeeded);
        Assert.False(result.Started);
        Assert.Equal("missing", result.StandardError);
    }
}

public class ExecutableResolverTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ResolveOnWindows_BareName_FindsACmdOnThePath()
    {
        var cmd = _temp.Write("tools/npm.cmd", "@echo off");

        var resolved = ExecutableResolver.ResolveOnWindows("npm", $"{_temp.Combine("empty")};{_temp.Combine("tools")}", ".COM;.EXE;.BAT;.CMD");

        Assert.Equal(cmd, resolved, ignoreCase: true);
    }

    [Fact]
    public void ResolveOnWindows_FollowsPathExtOrder()
    {
        var exe = _temp.Write("tools/tool.exe", "");
        _temp.Write("tools/tool.cmd", "");

        Assert.Equal(exe, ExecutableResolver.ResolveOnWindows("tool", _temp.Combine("tools"), ".EXE;.CMD"), ignoreCase: true);
    }

    [Fact]
    public void ResolveOnWindows_FirstPathEntryWins()
    {
        var first = _temp.Write("a/tool.cmd", "");
        _temp.Write("b/tool.cmd", "");

        Assert.Equal(first, ExecutableResolver.ResolveOnWindows("tool", $"{_temp.Combine("a")};{_temp.Combine("b")}", ".CMD"), ignoreCase: true);
    }

    [Theory]
    [InlineData("tool.exe")]
    [InlineData(@"C:\tools\tool")]
    [InlineData("./tool")]
    public void ResolveOnWindows_PathOrNameWithExtension_IsUsedAsWritten(string executable)
    {
        Assert.Equal(executable, ExecutableResolver.ResolveOnWindows(executable, _temp.Path, ".CMD"));
    }

    [Fact]
    public void ResolveOnWindows_NotFound_ReturnsTheNameUnchanged_SoStartingItReportsTheFailure()
    {
        Assert.Equal("missing", ExecutableResolver.ResolveOnWindows("missing", _temp.Path, ".EXE;.CMD"));
        Assert.Equal("missing", ExecutableResolver.ResolveOnWindows("missing", null, null));
    }

    [Fact]
    public void ResolveOnWindows_QuotedAndEmptyPathEntries_AreTolerated()
    {
        var cmd = _temp.Write("with space/tool.cmd", "");

        var resolved = ExecutableResolver.ResolveOnWindows("tool", $";;\"{_temp.Combine("with space")}\";", ".CMD");

        Assert.Equal(cmd, resolved, ignoreCase: true);
    }
}
