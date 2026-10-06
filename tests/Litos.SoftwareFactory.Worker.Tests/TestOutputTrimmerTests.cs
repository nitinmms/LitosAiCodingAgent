using System.Text.Json;
using Litos.Agent.Tools;

namespace Litos.SoftwareFactory.Worker.Tests;

/// <summary>
/// On R3, test-run output was 27% of the tool output in an implementation's context, mostly
/// lists of passing tests, and every later call re-read it. A test run's output is cut to its
/// failures and its summary as it is produced; the whole output is kept in a file.
/// </summary>
public sealed class TestOutputTrimmerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("litos-test-output-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string Passing(int tests) =>
        "[exit 0]\n"
        + string.Concat(Enumerable.Range(1, tests).Select(i => $" ✓ src/story/feature{i}.test.ts (4 tests) 12ms\n"))
        + " Test Files  " + tests + " passed (" + tests + ")\n      Tests  " + (tests * 4) + " passed (" + (tests * 4) + ")\n   Duration  4.2s";

    [Theory]
    [InlineData("dotnet test tests/A.Tests -v q", true)]
    [InlineData("npx vitest run", true)]
    [InlineData("npm test", true)]
    [InlineData("npm run test -- --coverage", true)]
    [InlineData("pnpm test", true)]
    [InlineData("npx jest src/api.test.ts", true)]
    [InlineData("python -m pytest -q", true)]
    [InlineData("go test ./...", true)]
    [InlineData("cargo test", true)]
    [InlineData("mvn -q test", true)]
    [InlineData("./gradlew test", true)]
    [InlineData("dotnet build -v q", false)]
    [InlineData("npm run build", false)]
    [InlineData("git status", false)]
    public void RecognisesTestRuns(string command, bool expected) => Assert.Equal(expected, TestOutputTrimmer.IsTestRun(command));

    [Fact]
    public void ShortOutput_IsKeptWhole() => Assert.Null(TestOutputTrimmer.Trim(Passing(5), "x.txt"));

    [Fact]
    public void APassingRun_KeepsItsSummary_AndDropsTheListOfPassingTests()
    {
        var output = Passing(120);

        var trimmed = TestOutputTrimmer.Trim(output, @"C:\runs\r1\test-output\run.txt")!;

        Assert.StartsWith("[exit 0]\n[The factory kept the failures and the summary of this test run:", trimmed);
        Assert.Contains(@"The whole output is in C:\runs\r1\test-output\run.txt", trimmed);
        Assert.Contains("Tests  480 passed (480)", trimmed);
        Assert.DoesNotContain("feature7.test.ts", trimmed);
        Assert.True(trimmed.Length < output.Length / 4, "A passing run shrinks to its summary.");
    }

    [Fact]
    public void AFailingRun_KeepsEachFailureWithItsMessage_AndTheSummary()
    {
        var output = "Command exited with code 1.\n"
            + string.Concat(Enumerable.Range(1, 60).Select(i => $" ✓ src/a{i}.test.ts (3 tests) 9ms\n"))
            + " FAIL  src/App.test.tsx > cancelling > keeps the brief\n"
            + "AssertionError: expected 'Writing' to be 'Cancelled'\n"
            + " ❯ src/App.test.tsx:188:63\n"
            + string.Concat(Enumerable.Range(1, 60).Select(i => $" ✓ src/b{i}.test.ts (3 tests) 9ms\n"))
            + " Test Files  1 failed | 120 passed (121)\n      Tests  1 failed | 360 passed (361)";

        var trimmed = TestOutputTrimmer.Trim(output, "run.txt")!;

        Assert.StartsWith("Command exited with code 1.", trimmed);
        Assert.Contains("FAIL  src/App.test.tsx > cancelling > keeps the brief", trimmed);
        Assert.Contains("AssertionError: expected 'Writing' to be 'Cancelled'", trimmed);
        Assert.Contains("src/App.test.tsx:188:63", trimmed);
        Assert.Contains("Tests  1 failed | 360 passed (361)", trimmed);
        Assert.DoesNotContain("src/a30.test.ts", trimmed);
        Assert.Contains("lines ...]", trimmed);
    }

    /// <summary>"Failed: 0" in a passing summary is not a failure to keep context for.</summary>
    [Fact]
    public void ASummaryCountOfZero_IsNotTreatedAsAFailure()
    {
        var output = "[exit 0]\nPassed!  - Failed:     0, Passed:   164\n"
            + string.Concat(Enumerable.Range(1, 200).Select(i => $"  Passed Tests.Case{i} [1 ms]\n"))
            + "Total tests: 200";

        var trimmed = TestOutputTrimmer.Trim(output, "run.txt")!;

        Assert.DoesNotContain("Tests.Case1 ", trimmed);
        Assert.Contains("Total tests: 200", trimmed);
    }

    [Fact]
    public void ManyFailures_AreCappedAtTheFailureLimit()
    {
        var output = "Command exited with code 1.\n"
            + string.Concat(Enumerable.Range(1, 400).Select(i => $"error CS0103: The name 'x{i}' does not exist\n"))
            + "Build FAILED.";

        var lines = TestOutputTrimmer.Trim(output, "run.txt")!.Split('\n');

        Assert.True(lines.Length <= 2 + TestOutputTrimmer.MaxFailureLines + TestOutputTrimmer.SummaryLines + 2);
        Assert.Contains("Build FAILED.", lines);
    }

    // ---- In the guarded shell ----

    private sealed class FixedShell(ToolResult result) : ITool
    {
        public string Name => "shell";

        public string Description => "shell";

        public JsonElement ParameterSchema { get; } = JsonSerializer.SerializeToElement(new { type = "object" });

        public Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken ct) => Task.FromResult(result);
    }

    private static JsonElement Command(string command) => JsonSerializer.SerializeToElement(new { command });

    [Fact]
    public async Task TheShell_CutsALongTestRun_AndKeepsTheWholeOutputInAFile()
    {
        var output = Passing(120);
        var shell = new GuardedShellTool(new FixedShell(ToolResult.Ok(output)), testOutputDirectory: _dir);

        var result = await shell.InvokeAsync(Command("npx vitest run"), default);

        var saved = Assert.Single(Directory.GetFiles(_dir));
        Assert.Equal(output, await File.ReadAllTextAsync(saved));
        Assert.Contains(saved, result.Text);
        Assert.False(result.IsError);
        Assert.True(result.Text.Length < output.Length);
    }

    [Fact]
    public async Task TheShell_KeepsAFailedRunAnError()
    {
        var output = "Command exited with code 1.\n" + string.Concat(Enumerable.Range(1, 300).Select(i => $"  Passed Case{i}\n")) + "Failed Case301";
        var shell = new GuardedShellTool(new FixedShell(ToolResult.Error(output)), testOutputDirectory: _dir);

        var result = await shell.InvokeAsync(Command("dotnet test"), default);

        Assert.True(result.IsError);
        Assert.Contains("Failed Case301", result.Text);
    }

    [Theory]
    [InlineData("npm run build", true)]   // not a test run
    [InlineData("npx vitest run", false)] // no directory to keep the whole output in
    public async Task TheShell_LeavesOtherOutputWhole(string command, bool withDirectory)
    {
        var output = Passing(120);
        var shell = new GuardedShellTool(new FixedShell(ToolResult.Ok(output)), testOutputDirectory: withDirectory ? _dir : null);

        var result = await shell.InvokeAsync(Command(command), default);

        Assert.Equal(output, result.Text);
        Assert.Empty(Directory.GetFiles(_dir));
    }
}
