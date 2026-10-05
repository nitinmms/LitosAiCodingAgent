using System.Text.Json;
using Litos.Agent.Tools;
using Litos.SoftwareFactory.Contracts;

namespace Litos.SoftwareFactory.Worker.Tests;

/// <summary>
/// On the fifth real task the agent, cleaning up after a hung test, ran
/// <c>taskkill /F /IM dotnet.exe /T</c>. It stopped every dotnet process on the machine: its own
/// worker, and whatever else was running there.
/// </summary>
public class ShellCommandGuardTests
{
    [Theory]
    // The command that was actually run.
    [InlineData("taskkill /F /IM dotnet.exe /T 2>&1 || echo none")]
    [InlineData("taskkill /im testhost.exe")]
    [InlineData("TASKKILL.EXE /F /IM node.exe")]
    [InlineData("taskkill -im dotnet.exe -f")]
    [InlineData("taskkill /F /FI \"IMAGENAME eq dotnet*\"")]
    [InlineData("cd src && taskkill /f /im dotnet.exe")]
    [InlineData("Stop-Process -Name dotnet -Force")]
    [InlineData("powershell -Command \"Stop-Process -ProcessName testhost\"")]
    [InlineData("Get-Process dotnet | Stop-Process -Force")]
    [InlineData("gps node | kill")]
    [InlineData("pkill -f dotnet")]
    [InlineData("killall node")]
    [InlineData("sleep 1; pkill testhost")]
    [InlineData("kill -9 -1")]
    [InlineData("wmic process where \"name='dotnet.exe'\" delete")]
    [InlineData("wmic process where name='node.exe' call terminate")]
    public void StoppingProcessesByName_IsRefused_AndTheReasonSaysWhatToDoInstead(string command)
    {
        var refusal = ShellCommandGuard.Refusal(command);

        Assert.NotNull(refusal);
        Assert.Contains("stops processes by name", refusal);
        Assert.Contains("the factory's own worker", refusal);
        Assert.Contains("taskkill /PID <id> /T", refusal);
    }

    [Theory]
    [InlineData("shutdown /s /t 0")]
    [InlineData("shutdown -r now")]
    [InlineData("Restart-Computer -Force")]
    [InlineData("Stop-Computer")]
    [InlineData("logoff")]
    [InlineData("sudo reboot")]
    public void ShuttingTheMachineDown_IsRefused(string command)
    {
        Assert.Contains("shut down, restart or log off the machine", ShellCommandGuard.Refusal(command));
    }

    /// <summary>The guard must not get in the way of ordinary work, including stopping one
    /// process the agent started itself.</summary>
    [Theory]
    [InlineData("dotnet build --no-restore -nodeReuse:false")]
    [InlineData("dotnet test --filter \"FullyQualifiedName~AsyncApiTests\" --blame-hang-timeout 60s")]
    [InlineData("npm run test:ci")]
    [InlineData("git status --short")]
    [InlineData("taskkill /PID 4242 /T /F")]
    [InlineData("Stop-Process -Id 4242")]
    [InlineData("kill 4242")]
    [InlineData("kill -9 4242")]
    [InlineData("tasklist /FI \"IMAGENAME eq dotnet.exe\"")]
    [InlineData("Get-Process dotnet | Select-Object Id, StartTime")]
    [InlineData("ps aux | grep dotnet")]
    [InlineData("grep -rn \"taskkill\" docs")]
    [InlineData("echo 'the shutdown hook ran'")]
    [InlineData("dotnet run -- --graceful-shutdown")]
    [InlineData("type src\\Shutdown.cs")]
    [InlineData("")]
    [InlineData(null)]
    public void OrdinaryCommands_AndStoppingOneProcessById_AreAllowed(string? command)
    {
        Assert.Null(ShellCommandGuard.Refusal(command));
    }

    private sealed class RecordingShell : ITool
    {
        public List<string> Ran { get; } = [];

        public string Name => "shell";

        public string Description => "Runs a shell command.";

        public JsonElement ParameterSchema { get; } = JsonSerializer.SerializeToElement(new { type = "object", required = new[] { "command" } });

        public Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken ct)
        {
            Ran.Add(arguments.GetProperty("command").GetString()!);
            return Task.FromResult(ToolResult.Ok("ran"));
        }
    }

    private static JsonElement Command(string command) => JsonSerializer.SerializeToElement(new { command });

    [Fact]
    public async Task GuardedShell_RefusesWithoutRunning_AndSaysWhy()
    {
        var inner = new RecordingShell();
        var guarded = new GuardedShellTool(inner);

        var result = await guarded.InvokeAsync(Command("taskkill /F /IM dotnet.exe /T"), default);

        Assert.True(result.IsError);
        Assert.StartsWith("The factory refused this command: it stops processes by name", result.Text);
        Assert.Empty(inner.Ran);
    }

    [Fact]
    public async Task GuardedShell_PassesAnOrdinaryCommandThrough_Unchanged()
    {
        var inner = new RecordingShell();
        var guarded = new GuardedShellTool(inner);

        var result = await guarded.InvokeAsync(Command("dotnet build -v q"), default);

        Assert.False(result.IsError);
        Assert.Equal(["dotnet build -v q"], inner.Ran);
    }

    // ---- A hanging test names itself ----
    // On F5's re-run a test spun after a locking change; the run had no timeout, the kernel killed
    // the script after five minutes, twice, and the agent blamed the environment.

    [Theory]
    [InlineData("dotnet test", "dotnet test --blame-hang-timeout 2m --blame-hang-dump-type none")]
    [InlineData("dotnet test tests/FileDbSharp.Tests/FileDbSharp.Tests.csproj -v q --nologo 2>&1",
        "dotnet test --blame-hang-timeout 2m --blame-hang-dump-type none tests/FileDbSharp.Tests/FileDbSharp.Tests.csproj -v q --nologo 2>&1")]
    [InlineData("cd tests && dotnet.exe test --no-build | findstr Failed",
        "cd tests && dotnet.exe test --blame-hang-timeout 2m --blame-hang-dump-type none --no-build | findstr Failed")]
    [InlineData("dotnet build && dotnet test", "dotnet build && dotnet test --blame-hang-timeout 2m --blame-hang-dump-type none")]
    public void ADotnetTestWithNoHangTimeout_GetsOne(string command, string expected) =>
        Assert.Equal(expected, ShellCommandGuard.WithHangTimeout(command));

    [Theory]
    [InlineData("dotnet test --blame-hang-timeout 30s")]
    [InlineData("dotnet build")]
    [InlineData("npm test")]
    [InlineData("npx vitest run")]
    [InlineData("echo dotnet testing")]
    [InlineData("")]
    public void OtherCommands_AreLeftAlone(string command) =>
        Assert.Equal(command, ShellCommandGuard.WithHangTimeout(command));

    [Fact]
    public async Task GuardedShell_RunsDotnetTestWithTheHangTimeout_KeepingTheOtherArguments()
    {
        var inner = new RecordingArgumentsShell();
        var guarded = new GuardedShellTool(inner);

        await guarded.InvokeAsync(JsonSerializer.SerializeToElement(new { command = "dotnet test", timeout_seconds = 600 }), default);

        var ran = Assert.Single(inner.Ran);
        Assert.Equal("dotnet test --blame-hang-timeout 2m --blame-hang-dump-type none", ran.GetProperty("command").GetString());
        Assert.Equal(600, ran.GetProperty("timeout_seconds").GetInt32());
    }

    private sealed class RecordingArgumentsShell : ITool
    {
        public List<JsonElement> Ran { get; } = [];

        public string Name => "shell";

        public string Description => "Runs a shell command.";

        public JsonElement ParameterSchema { get; } = JsonSerializer.SerializeToElement(new { type = "object" });

        public Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken ct)
        {
            Ran.Add(arguments.Clone());
            return Task.FromResult(ToolResult.Ok("ran"));
        }
    }

    [Fact]
    public void GuardedShell_LooksLikeTheShellItWraps()
    {
        var inner = new RecordingShell();
        var guarded = new GuardedShellTool(inner);

        Assert.Equal(("shell", inner.Description), (guarded.Name, guarded.Description));
        Assert.Equal(inner.ParameterSchema.GetRawText(), guarded.ParameterSchema.GetRawText());
    }

    /// <summary>Arguments the shell itself would reject are left for it to reject.</summary>
    [Fact]
    public async Task GuardedShell_ArgumentsWithNoCommand_GoToTheShell()
    {
        var guarded = new GuardedShellTool(new NamedShell());

        Assert.Equal("inner", (await guarded.InvokeAsync(JsonSerializer.SerializeToElement(new { }), default)).Text);
        Assert.Equal("inner", (await guarded.InvokeAsync(JsonSerializer.SerializeToElement("not an object"), default)).Text);
    }

    private sealed class NamedShell : ITool
    {
        public string Name => "shell";

        public string Description => "shell";

        public JsonElement ParameterSchema { get; } = JsonSerializer.SerializeToElement(new { type = "object" });

        public Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken ct) => Task.FromResult(ToolResult.Ok("inner"));
    }

    private static readonly string[] Registered = ["read_file", "write_file", "edit_file", "list_directory", "search_code", "shell"];

    private static FactoryToolSetPolicy Policy(RecordingShell shell) => new(
        Registered.Select(n => n == "shell" ? (ITool)shell : new NamedTool(n)), TestOptions.HostClient(new FakeHttpMessageHandler()));

    private sealed class NamedTool(string name) : ITool
    {
        public string Name { get; } = name;

        public string Description => name;

        public JsonElement ParameterSchema { get; } = JsonSerializer.SerializeToElement(new { type = "object" });

        public Task<ToolResult> InvokeAsync(JsonElement arguments, CancellationToken ct) => Task.FromResult(ToolResult.Ok(name));
    }

    /// <summary>The guard is on the shell an agent is given, in every turn that has one, and on
    /// the shell its kernel code reaches through the bridge — the path the real command took.</summary>
    [Theory]
    [InlineData(TurnKind.Implement)]
    [InlineData(TurnKind.Repair)]
    [InlineData(TurnKind.Rework)]
    public async Task TheShellATurnGets_IsTheGuardedOne_DirectlyAndThroughTheKernelBridge(TurnKind kind)
    {
        var shell = new RecordingShell();
        var policy = Policy(shell);

        var direct = policy.Create("session-1", kind.ToString()).Resolve("shell");
        var bridged = policy.CreateForBridge("session-1").Resolve("shell");

        foreach (var tool in new[] { direct, bridged })
        {
            Assert.True((await tool.InvokeAsync(Command("taskkill /F /IM dotnet.exe /T"), default)).IsError);
            Assert.False((await tool.InvokeAsync(Command("dotnet build"), default)).IsError);
        }

        Assert.Equal(["dotnet build", "dotnet build"], shell.Ran);
    }
}
