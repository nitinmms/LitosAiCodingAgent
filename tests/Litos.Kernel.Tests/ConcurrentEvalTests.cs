using System.Text.Json;
using Litos.Agent.Tools;

namespace Litos.Kernel.Tests;

/// <summary>
/// A model can put several run_kernel_code calls in one reply, and they reach the session at once.
/// The wire protocol runs one eval at a time and the subprocess ignores an overlapping one, so the
/// second call waited for a result that could never come; on a fresh session both calls also raced
/// to start the kernel. A factory review sat silent for over ten minutes this way, with two
/// read_file calls as its first reply. These run through a real subprocess, because both races were
/// between real start-up and real evals.
/// </summary>
public sealed class ConcurrentEvalTests
{
    private sealed class ReadFileTool : ITool
    {
        public string Name => "read_file";

        public string Description => "Reads a file.";

        public JsonElement ParameterSchema { get; } = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { path = new { type = "string" } },
            required = new[] { "path" },
        });

        public Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken ct) =>
            Task.FromResult(ToolResult.Ok($"contents of {arguments.GetProperty("path").GetString()}"));
    }

    private static KernelSession NewSession(string name, TimeSpan hardTimeout)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "litos-concurrent-start", name, Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(scratch);
        return new KernelSession(
            sessionId: name,
            workingDirectory: Path.GetTempPath(),
            scratchDirectory: scratch,
            bridgedToolsSource: () => new ToolRegistry([new ReadFileTool()]),
            mcpToolProvider: null,
            hardTimeout: hardTimeout);
    }

    [Fact]
    public async Task SeveralEvals_OnAFreshSession_AllComplete_OnOneKernel()
    {
        await using var session = NewSession("plain", TimeSpan.FromSeconds(90));

        var results = await Task.WhenAll(Enumerable.Range(1, 4).Select(i => session.RunAsync($"{i} * 10", CancellationToken.None)))
            .WaitAsync(TimeSpan.FromSeconds(80));

        Assert.All(results, r => Assert.False(r.IsError, r.Text));
        Assert.Equal(["10", "20", "30", "40"], results.Select(r => r.Text.Trim()));
    }

    [Fact]
    public async Task TheReviewsFirstReply_TwoBridgedReads_BothComplete()
    {
        await using var session = NewSession("review", TimeSpan.FromSeconds(90));

        var first = session.RunAsync("var fd = await read_file(\"path\", \"src/FileDbSharp/FileDatabase.cs\");\nSystem.Console.WriteLine(fd);", CancellationToken.None);
        var second = session.RunAsync("var lf = await read_file(\"path\", \"src/FileDbSharp/Storage/LogFormat.cs\");\nSystem.Console.WriteLine(lf);", CancellationToken.None);
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(80));

        Assert.False(results[0].IsError, results[0].Text);
        Assert.False(results[1].IsError, results[1].Text);
        Assert.Contains("contents of src/FileDbSharp/FileDatabase.cs", results[0].Text);
        Assert.Contains("contents of src/FileDbSharp/Storage/LogFormat.cs", results[1].Text);
    }

    [Fact]
    public async Task TwoBridgedEvals_OnARunningKernel_BothComplete()
    {
        await using var session = NewSession("running", TimeSpan.FromSeconds(90));
        Assert.False((await session.RunAsync("1", CancellationToken.None)).IsError);

        var results = await Task.WhenAll(
            session.RunAsync("System.Console.WriteLine(await read_file(\"path\", \"a.cs\"));", CancellationToken.None),
            session.RunAsync("System.Console.WriteLine(await read_file(\"path\", \"b.cs\"));", CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(80));

        Assert.Contains("contents of a.cs", results[0].Text);
        Assert.Contains("contents of b.cs", results[1].Text);
    }

    [Fact]
    public async Task AfterAConcurrentStart_TheSessionKeepsItsState()
    {
        await using var session = NewSession("state", TimeSpan.FromSeconds(90));

        await Task.WhenAll(session.RunAsync("var a = 1;", CancellationToken.None), session.RunAsync("var b = 2;", CancellationToken.None))
            .WaitAsync(TimeSpan.FromSeconds(80));
        var sum = await session.RunAsync("a + b", CancellationToken.None);

        Assert.False(sum.IsError, sum.Text);
        Assert.StartsWith("3", sum.Text.Trim());
    }
}
