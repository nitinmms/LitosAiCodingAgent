using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Litos.Agent.Messages;
using Litos.SoftwareFactory.Contracts;
using Litos.Tools.Mcp;
using Litos.Tools.Shell;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Litos.SoftwareFactory.Worker.Tests;

/// <summary>
/// The run's MCP servers in the worker (m3-architecture.md §7.2), against a real stdio server
/// (Litos.SoftwareFactory.TestMcpServer) started from an mcp.json the way the host writes it.
/// </summary>
public sealed class WorkerMcpTests : IAsyncLifetime
{
    private readonly TempDirectory _data = new();
    private FakeFactoryHost _host = null!;
    private WebApplication? _worker;

    public async Task InitializeAsync() => _host = await FakeFactoryHost.StartAsync();

    public async Task DisposeAsync()
    {
        if (_worker is not null)
        {
            await _worker.StopAsync();
            await _worker.DisposeAsync();
        }

        await _host.DisposeAsync();
        _data.Dispose();
    }

    private static string TestServerDll => Path.Combine(AppContext.BaseDirectory, "Litos.SoftwareFactory.TestMcpServer.dll");

    private static McpServerDefinition TestServer(
        string name = "test", ToolPermission permission = ToolPermission.Full, Dictionary<string, ToolPermission>? overrides = null,
        Dictionary<string, string>? env = null, string? command = null) =>
        new(name, McpTransportKind.Stdio, command ?? "dotnet", command is null ? [TestServerDll] : [], env, null, true, permission, overrides);

    private string WriteConfig(params McpServerDefinition[] servers)
    {
        var path = Path.Combine(_data.Path, "runs", "run-1", "mcp.json");
        new McpConfig(servers).Save(path);
        return path;
    }

    private static WorkerMcp Mcp(string configPath) => new(configPath, NullLoggerFactory.Instance);

    // ---- Connecting ----

    [Fact]
    public async Task AServer_Connects_AndOffersOnlyTheToolsTheRunMayUse()
    {
        var mcp = Mcp(WriteConfig(TestServer(overrides: new() { ["mcp__test__delete_everything"] = ToolPermission.Deny })));

        var report = Assert.Single(await mcp.ConnectAsync(default));

        Assert.Equal(("test", true, (string?)null), (report.Name, report.Connected, report.Error));
        Assert.Equal(["echo", "read_env"], report.Tools.Order());
        Assert.Equal(["mcp__test__echo", "mcp__test__read_env"], mcp.PermittedTools().Select(t => t.Name).Order());
    }

    [Fact]
    public async Task AServerThatIsDeny_OffersOnlyTheToolsAllowedOnIt()
    {
        var mcp = Mcp(WriteConfig(TestServer(permission: ToolPermission.Deny, overrides: new() { ["mcp__test__echo"] = ToolPermission.Full })));

        await mcp.ConnectAsync(default);

        Assert.Equal(["mcp__test__echo"], mcp.PermittedTools().Select(t => t.Name));
    }

    [Fact]
    public async Task AToolIsCalled_WithTheServersSecretVariablesInItsEnvironment()
    {
        var mcp = Mcp(WriteConfig(TestServer(env: new() { ["GITHUB_TOKEN"] = "ghp_from_the_host" })));
        await mcp.ConnectAsync(default);
        var tools = mcp.PermittedTools().ToDictionary(t => t.Name);

        var echoed = await tools["mcp__test__echo"].InvokeAsync(JsonDocument.Parse("""{"text":"hi"}""").RootElement, default);
        var token = await tools["mcp__test__read_env"].InvokeAsync(JsonDocument.Parse("""{"name":"GITHUB_TOKEN"}""").RootElement, default);

        Assert.Equal((false, "echo: hi"), (echoed.IsError, echoed.Text));
        Assert.Equal("ghp_from_the_host", token.Text);
    }

    [Fact]
    public async Task AServerThatCannotStart_IsReportedWithWhy_AndTheOthersStillConnect()
    {
        var mcp = Mcp(WriteConfig(TestServer(), TestServer(name: "broken", command: "litos-no-such-command-xyz")));

        var reports = (await mcp.ConnectAsync(default)).ToDictionary(r => r.Name);

        Assert.True(reports["test"].Connected);
        Assert.False(reports["broken"].Connected);
        Assert.False(string.IsNullOrWhiteSpace(reports["broken"].Error));
        Assert.Empty(reports["broken"].Tools);
        Assert.All(mcp.PermittedTools(), t => Assert.StartsWith("mcp__test__", t.Name));
    }

    // ---- In the worker ----

    [Fact]
    public async Task TheWorker_ReportsReady_OnlyWithItsServersConnected_AndSaysHow()
    {
        var options = TestOptions.Create(_data.Path, _host.Url) with { McpConfigPath = WriteConfig(TestServer()) };
        _worker = WorkerApp.Build(options, []);

        await WorkerApp.StartAsync(_worker);

        var ready = Assert.Single(_host.ReadyCalls);
        Assert.True(ready.McpReady);
        var server = Assert.Single(ready.McpServers!);
        Assert.True(server.Connected);
        Assert.Contains("echo", server.Tools);
    }

    [Fact]
    public async Task McpTools_JoinTheWorkTurns_ButNeverAReadOnlyOne()
    {
        var options = TestOptions.Create(_data.Path, _host.Url) with
        {
            McpConfigPath = WriteConfig(TestServer(overrides: new() { ["mcp__test__delete_everything"] = ToolPermission.Deny })),
        };
        _worker = WorkerApp.Build(options, []);
        var port = await WorkerApp.StartAsync(_worker);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
        client.DefaultRequestHeaders.Add(FactoryWire.SecretHeader, TestOptions.Secret);
        _host.EnqueueGateway(FakeFactoryHost.ToolCall("mcp__test__echo", new { text = "from the agent" }));
        _host.EnqueueGateway(FakeFactoryHost.Reply("Done."));
        _host.EnqueueGateway(FakeFactoryHost.Reply("Looks fine."));

        using (var implement = await client.PostAsJsonAsync("sessions/work/turns", new { input = "Use the server.", turnKind = "Implement" }))
            await implement.Content.ReadAsStringAsync();
        using (var review = await client.PostAsJsonAsync("sessions/review/turns", new { input = "Review it.", turnKind = "Review" }))
            await review.Content.ReadAsStringAsync();

        var calls = _host.GatewayRequests.ToArray();
        var implementTools = calls[0].ChatRequest.Tools.Select(t => t.Name).ToList();
        Assert.Contains("mcp__test__echo", implementTools);
        Assert.Contains("mcp__test__read_env", implementTools);
        Assert.DoesNotContain("mcp__test__delete_everything", implementTools);
        // The call reached the server, and its answer reached the model.
        var result = calls[1].ChatRequest.Messages.SelectMany(m => m.Content).OfType<ToolResultBlock>().Single();
        Assert.Equal("echo: from the agent", result.Text);
        Assert.DoesNotContain(calls[2].ChatRequest.Tools, t => t.Name.StartsWith("mcp__", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_ReadsTheMcpConfigPath()
    {
        var environment = new Dictionary<string, string?>
        {
            ["FACTORY_WORKER_SECRET"] = "s", ["FACTORY_HOST_URL"] = "http://127.0.0.1:5180", ["FACTORY_RUN_ID"] = "run-1",
        };

        var options = WorkerOptions.Parse(
            ["--provider", "openrouter", "--model", "m", "--data-dir", "d", "--mcp-config", "d/runs/run-1/mcp.json"],
            name => environment.GetValueOrDefault(name));

        Assert.Equal(Path.GetFullPath("d/runs/run-1/mcp.json"), options.McpConfigPath);
        Assert.Null(WorkerOptions.Parse(["--provider", "p", "--model", "m", "--data-dir", "d"], name => environment.GetValueOrDefault(name)).McpConfigPath);
    }
}
