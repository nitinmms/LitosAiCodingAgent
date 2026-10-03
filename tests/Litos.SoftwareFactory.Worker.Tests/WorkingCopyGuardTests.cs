using System.Text.Json;
using Litos.Agent.Tools;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Worker;

namespace Litos.SoftwareFactory.Worker.Tests;

/// <summary>
/// On a task created against the wrong project, the agent listed its working copy's parent,
/// found another project's working copy beside it, and read it with list_directory, search_code
/// and <c>git -C</c>. These are the calls it made, refused.
/// </summary>
public sealed class WorkingCopyGuardTests : IDisposable
{
    private readonly string _workspaces = Path.Combine(Path.GetTempPath(), "litos-guard-tests", Guid.NewGuid().ToString("n"));
    private readonly string _own;
    private readonly string _other;
    private readonly WorkingCopyGuard _guard;

    public WorkingCopyGuardTests()
    {
        _own = Directory.CreateDirectory(Path.Combine(_workspaces, "54e3aaaaaaaaaaaaaaaaaaaaaaaaaaaa")).FullName;
        _other = Directory.CreateDirectory(Path.Combine(_workspaces, "289871f8b3254cbd8ad06e0329fc34a0")).FullName;
        Directory.CreateDirectory(Path.Combine(_own, "src"));
        _guard = new WorkingCopyGuard(_own);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workspaces, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ---- Paths given to the file tools ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("src")]
    [InlineData("src/Orders.cs")]
    [InlineData("src/../README.md")]
    public void PathsInsideTheWorkingCopy_AreAllowed(string? path) => Assert.Null(_guard.PathRefusal(path));

    [Fact]
    public void TheWorkingCopyByItsFullPath_IsAllowed()
    {
        Assert.Null(_guard.PathRefusal(_own));
        Assert.Null(_guard.PathRefusal(Path.Combine(_own, "src", "Orders.cs")));
        Assert.Null(_guard.PathRefusal(_own.Replace('\\', '/') + "/src"));
    }

    [Fact]
    public void TheParentDirectory_IsRefused_WithTheReason()
    {
        var reason = _guard.PathRefusal("..");

        Assert.NotNull(reason);
        Assert.Contains("is outside this task's working copy", reason);
        Assert.Contains("call request_decision and say so", reason);
    }

    [Fact]
    public void AnotherWorkingCopy_IsRefused_HoweverItIsWritten()
    {
        Assert.NotNull(_guard.PathRefusal(_other));
        Assert.NotNull(_guard.PathRefusal(_other.Replace('\\', '/')));
        Assert.NotNull(_guard.PathRefusal(Path.Combine("..", Path.GetFileName(_other), "src")));
        Assert.NotNull(_guard.PathRefusal(Path.GetTempPath()));
    }

    [Fact]
    public void ADirectoryWhoseNameOnlyStartsLikeTheWorkingCopy_IsRefused()
    {
        var lookalike = Directory.CreateDirectory(_own + "-other").FullName;

        Assert.NotNull(_guard.PathRefusal(lookalike));
    }

    // ---- Shell commands ----

    [Fact]
    public void ShellCommandsNamingAnotherWorkingCopy_AreRefused()
    {
        var forward = _other.Replace('\\', '/');
        string[] commands =
        [
            $"git -C \"{forward}\" log --oneline -5",
            $"git -C {_other} branch -a",
            $"cd /d \"{_other}\" && git status --short",
            $"dir {_other}\\src",
            $"git -C ../{Path.GetFileName(_other)} log",
            $"type ..\\{Path.GetFileName(_other)}\\README.md",
        ];

        foreach (var command in commands)
            Assert.True(_guard.CommandRefusal(command) is not null, $"Not refused: {command}");
    }

    [Theory]
    [InlineData("dotnet test")]
    [InlineData("git status --short")]
    [InlineData("cd src && dir")]
    [InlineData("cd .. && cd src")]
    [InlineData("type ..\\no-such-directory\\x.txt")]
    public void OrdinaryCommands_Run(string command) => Assert.Null(_guard.CommandRefusal(command));

    [Fact]
    public void CommandsNamingTheWorkingCopyItself_Run()
    {
        Assert.Null(_guard.CommandRefusal($"git -C \"{_own}\" status"));
        Assert.Null(_guard.CommandRefusal($"dir {_own.Replace('\\', '/')}/src"));
    }

    // ---- In the tool set an agent is given ----

    private sealed class RecordingTool(string name) : ITool
    {
        public List<string> Calls { get; } = [];

        public string Name { get; } = name;

        public string Description => name;

        public JsonElement ParameterSchema { get; } = JsonSerializer.SerializeToElement(new { type = "object" });

        public Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken ct)
        {
            Calls.Add(arguments.GetRawText());
            return Task.FromResult(ToolResult.Ok(Name));
        }
    }

    private static readonly string[] Registered = ["read_file", "write_file", "edit_file", "list_directory", "search_code", "shell"];

    [Theory]
    [InlineData(TurnKind.Implement, "read_file")]
    [InlineData(TurnKind.Implement, "write_file")]
    [InlineData(TurnKind.Implement, "edit_file")]
    [InlineData(TurnKind.Implement, "list_directory")]
    [InlineData(TurnKind.Implement, "search_code")]
    [InlineData(TurnKind.Review, "list_directory")]
    [InlineData(TurnKind.Review, "search_code")]
    public async Task EveryFileTool_IsConfined_DirectlyAndThroughTheKernelBridge(TurnKind kind, string name)
    {
        var tools = Registered.Select(n => new RecordingTool(n)).ToList();
        var policy = new FactoryToolSetPolicy(tools, TestOptions.HostClient(new FakeHttpMessageHandler()), _own);

        var direct = policy.Create("session-1", kind.ToString()).Resolve(name)!;
        var bridged = policy.CreateForBridge("session-1").Resolve(name)!;

        foreach (var tool in new[] { direct, bridged })
        {
            var refused = await tool.InvokeAsync(JsonSerializer.SerializeToElement(new { path = _other, pattern = "DatabaseStats" }), default);
            Assert.True(refused.IsError);
            Assert.StartsWith("The factory refused this:", refused.Text);
            Assert.False((await tool.InvokeAsync(JsonSerializer.SerializeToElement(new { path = "src", pattern = "DatabaseStats" }), default)).IsError);
        }

        Assert.Equal(2, tools.Single(t => t.Name == name).Calls.Count);
    }

    [Fact]
    public async Task TheShell_RefusesAnotherWorkingCopy_DirectlyAndThroughTheKernelBridge()
    {
        var tools = Registered.Select(n => new RecordingTool(n)).ToList();
        var policy = new FactoryToolSetPolicy(tools, TestOptions.HostClient(new FakeHttpMessageHandler()), _own);

        var direct = policy.Create("session-1", nameof(TurnKind.Implement)).Resolve("shell")!;
        var bridged = policy.CreateForBridge("session-1").Resolve("shell")!;

        foreach (var shell in new[] { direct, bridged })
        {
            Assert.True((await shell.InvokeAsync(JsonSerializer.SerializeToElement(new { command = $"git -C \"{_other}\" log" }), default)).IsError);
            Assert.False((await shell.InvokeAsync(JsonSerializer.SerializeToElement(new { command = "git status" }), default)).IsError);
        }

        Assert.Equal(2, tools.Single(t => t.Name == "shell").Calls.Count);
    }
}
