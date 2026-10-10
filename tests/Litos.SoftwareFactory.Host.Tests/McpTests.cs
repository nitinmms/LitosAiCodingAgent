using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Settings;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Host.Runs;
using Litos.SoftwareFactory.Host.Settings;
using Litos.Tools.Mcp;
using Litos.Tools.Shell;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>Test connection results a test hands out, and the servers it was asked about.</summary>
public sealed class FakeMcpTester : IMcpConnectionTester
{
    public List<McpServerSettings> Tested { get; } = [];

    public McpTestResult Result { get; set; } = new(true, [new McpTestedTool("create_issue", "Opens an issue.")], null);

    public Task<McpTestResult> TestAsync(McpServerSettings server, CancellationToken ct)
    {
        Tested.Add(server);
        return Task.FromResult(Result);
    }
}

/// <summary>MCP servers in the factory (m3-architecture.md §7): the tab, Test connection, and what a run is given.</summary>
public sealed class McpTests : IAsyncLifetime
{
    private readonly FakeMcpTester _tester = new();
    private TestHost _host = null!;
    private FactorySettings _settings = null!;

    public async Task InitializeAsync() => await StartAsync(startCoordinator: false);

    private async Task StartAsync(bool startCoordinator)
    {
        _host = await TestHost.StartAsync(startCoordinator: startCoordinator, services: s => s.AddSingleton<IMcpConnectionTester>(_tester));
        _settings = _host.App.Services.GetRequiredService<FactorySettings>();
    }

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    private static string TestServerDll => Path.Combine(AppContext.BaseDirectory, "Litos.SoftwareFactory.TestMcpServer.dll");

    private static McpServerSettings GitHub(McpAccess permission = McpAccess.Full) => new()
    {
        Name = "github",
        Command = "npx",
        Args = ["-y", "@modelcontextprotocol/server-github"],
        SecretVariables = ["GITHUB_TOKEN"],
        Permission = permission,
        ToolOverrides = new Dictionary<string, McpAccess> { ["delete_repository"] = McpAccess.Deny },
    };

    private async Task SaveMcpAsync(params McpServerSettings[] servers) =>
        await _settings.SaveAsync(SettingsSections.Mcp, new McpSettings { Servers = servers }, _settings.RevisionOf(SettingsSections.Mcp), null, default);

    private async Task<HttpResponseMessage> PutMcpAsync(object settings, HttpClient? client = null) =>
        await (client ?? _host.Client).PutAsJsonAsync("api/admin/settings/mcp", new { revision = _settings.RevisionOf(SettingsSections.Mcp), settings });

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    // ---- The tab ----

    [Fact]
    public async Task TheSection_StartsWithNoServer()
    {
        var mcp = (await _host.GetAsync("api/admin/settings")).GetProperty("mcp");

        Assert.Equal(0, mcp.GetProperty("revision").GetInt64());
        Assert.Empty(mcp.GetProperty("settings").GetProperty("servers").EnumerateArray());
    }

    [Fact]
    public async Task AnAdmin_SavesTheServers_AndTheHostRefusesOnesThatCannotWork()
    {
        using var refused = await PutMcpAsync(new { servers = new[] { new { name = "git__hub", command = "x" } } });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("is not a usable server name", (await BodyAsync(refused)).GetProperty("error").GetString());

        using var saved = await PutMcpAsync(new McpSettings { Servers = [GitHub()] });

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var server = Assert.Single(_settings.Mcp.Servers);
        Assert.Equal(("github", McpAccess.Deny), (server.Name, server.AccessTo("delete_repository")));
    }

    [Fact]
    public async Task TheApp_ReadsAndWritesTransportAndAccess_AsWords()
    {
        using var saved = await PutMcpAsync(new
        {
            servers = new[]
            {
                new { name = "docs", transport = "Http", url = "https://mcp.example.com/", permission = "Deny", toolOverrides = new Dictionary<string, string> { ["search"] = "Full" } },
            },
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var server = (await _host.GetAsync("api/admin/settings")).GetProperty("mcp").GetProperty("settings").GetProperty("servers")[0];

        Assert.Equal(("Http", "Deny", "Full"), (server.GetProperty("transport").GetString(), server.GetProperty("permission").GetString(),
            server.GetProperty("toolOverrides").GetProperty("search").GetString()));
    }

    [Fact]
    public async Task ASecretVariable_IsSetLikeAnyKey_AndGoesWithItsServer()
    {
        await SaveMcpAsync(GitHub());
        using (var set = await _host.Client.PutAsJsonAsync($"api/admin/secrets/{Uri.EscapeDataString(SecretNames.Mcp("github", "GITHUB_TOKEN"))}", new { value = "ghp_x" }))
            Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);

        // The variable is dropped: its secret is cleared with it.
        using var saved = await PutMcpAsync(new McpSettings { Servers = [GitHub() with { SecretVariables = [] }] });

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.False(_settings.IsSet(SecretNames.Mcp("github", "GITHUB_TOKEN")));
    }

    [Fact]
    public async Task OnlyAnAdmin_ChangesOrTestsServers()
    {
        await _host.CreateMemberAsync("maria");
        var maria = await _host.SignedInAsync("maria");

        using var save = await PutMcpAsync(new McpSettings { Servers = [GitHub()] }, maria);
        using var test = await maria.PostAsJsonAsync("api/admin/mcp/test", GitHub());

        Assert.Equal((HttpStatusCode.Forbidden, HttpStatusCode.Forbidden), (save.StatusCode, test.StatusCode));
        Assert.Empty(_tester.Tested);
    }

    // ---- Test connection ----

    [Fact]
    public async Task TestConnection_StartsTheServerAsTheFormDescribesIt_AndListsItsTools()
    {
        using var response = await _host.Client.PostAsJsonAsync("api/admin/mcp/test", GitHub());

        var result = await BodyAsync(response);
        Assert.True(result.GetProperty("connected").GetBoolean());
        Assert.Equal("create_issue", result.GetProperty("tools")[0].GetProperty("name").GetString());
        Assert.Equal("github", Assert.Single(_tester.Tested).Name);
    }

    [Fact]
    public async Task TestConnection_OfAServerThatCannotWork_IsRefusedUntested()
    {
        using var response = await _host.Client.PostAsJsonAsync("api/admin/mcp/test", new { name = "docs", transport = "Http", url = "not a url" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_tester.Tested);
    }

    [Fact]
    public async Task TheRealTester_ConnectsToAStdioServer_WithItsSecrets_AndListsItsTools()
    {
        await _settings.SetSecretAsync(SecretNames.Mcp("test", "TOKEN"), "secret-value", null, default);
        var tester = new McpConnectionTester(_settings, NullLoggerFactory.Instance);

        var result = await tester.TestAsync(
            new McpServerSettings { Name = "test", Command = "dotnet", Args = [TestServerDll], SecretVariables = ["TOKEN"] }, default);

        Assert.True(result.Connected, result.Error);
        Assert.Equal(["delete_everything", "echo", "read_env"], result.Tools.Select(t => t.Name).Order());
    }

    [Fact]
    public async Task TheRealTester_SaysWhyAServerCouldNotStart()
    {
        var tester = new McpConnectionTester(_settings, NullLoggerFactory.Instance);

        var result = await tester.TestAsync(new McpServerSettings { Name = "broken", Command = "litos-no-such-command-xyz" }, default);

        Assert.False(result.Connected);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public void ATestedServer_GetsTheToolchainAllowlistAndItsSecrets_NeverTheHostsOwnSettings()
    {
        var host = new Dictionary<string, string>
        {
            ["PATH"] = "C:\\tools", ["ConnectionStrings__Factory"] = "Host=db;Password=p", ["OPENROUTER_API_KEY"] = "sk-or", ["FACTORY_ADMIN_PASSWORD"] = "x",
        };

        var environment = FactoryMcp.TestEnvironment(host, new Dictionary<string, string> { ["GITHUB_TOKEN"] = "ghp_x" });

        Assert.Equal("C:\\tools", environment["PATH"]);
        Assert.Equal("ghp_x", environment["GITHUB_TOKEN"]);
        Assert.DoesNotContain("ConnectionStrings__Factory", environment.Keys);
        Assert.DoesNotContain("OPENROUTER_API_KEY", environment.Keys);
        Assert.DoesNotContain("FACTORY_ADMIN_PASSWORD", environment.Keys);
    }

    [Fact]
    public void ADefinition_IsFullOrDeny_WithItsOverridesUnderTheirFullNames()
    {
        var definition = FactoryMcp.Definition(GitHub(McpAccess.Deny), new Dictionary<string, string> { ["GITHUB_TOKEN"] = "t" }, inheritEnvironment: false);

        Assert.Equal((McpTransportKind.Stdio, "npx", ToolPermission.Deny, false), (definition.Transport, definition.Command, definition.DefaultPermission, definition.InheritEnvironment));
        Assert.Equal(ToolPermission.Deny, definition.PermissionFor("mcp__github__delete_repository"));
        Assert.Equal("t", definition.Env!["GITHUB_TOKEN"]);
    }

    // ---- What a run is given ----

    private async Task<FakeWorker> WaitForWorkerAsync(int count = 1)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (_host.Workers.Workers.Count < count)
        {
            Assert.True(DateTime.UtcNow < deadline, "No worker was launched.");
            await Task.Delay(25);
        }

        return _host.Workers.Workers.ElementAt(count - 1);
    }

    private async Task<Guid> DelegatedThreadAsync()
    {
        var threadId = await _host.CreateThreadAsync(await _host.RegisterProjectAsync());
        await _host.DelegateAsync(threadId);
        return threadId;
    }

    private async Task<RunCapabilities> CapabilitiesAsync(Guid threadId)
    {
        var json = (await _host.ThreadAsync(threadId)).LatestRun!.CapabilitiesJson!;
        return JsonSerializer.Deserialize<RunCapabilities>(json, FactoryWire.Json)!;
    }

    [Fact]
    public async Task ARun_IsGivenItsEnabledServers_WithSecrets_WaitsForThem_AndRecordsHowTheyConnected()
    {
        await _host.DisposeAsync();
        await StartAsync(startCoordinator: true);
        await SaveMcpAsync(GitHub(), GitHub() with { Name = "off", Enabled = false });
        await _settings.SetSecretAsync(SecretNames.Mcp("github", "GITHUB_TOKEN"), "ghp_for_runs", null, default);
        var threadId = await DelegatedThreadAsync();

        var worker = await WaitForWorkerAsync();
        var path = worker.Launch.McpConfigPath!;
        var written = McpConfig.Load(path);
        var server = Assert.Single(written.Servers);
        Assert.Equal(("github", "ghp_for_runs", true), (server.Name, server.Env!["GITHUB_TOKEN"], server.InheritEnvironment));
        Assert.Equal(ToolPermission.Deny, server.PermissionFor("mcp__github__delete_repository"));
        // No turn has started: the host is waiting for the worker's MCP servers.
        await Task.Delay(300);
        Assert.Empty(_host.Workers.Turns);

        await worker.ReadyAsync(new McpServerReport("github", true, ["create_issue"], null));

        await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);
        var capabilities = await CapabilitiesAsync(threadId);
        Assert.Equal(["github"], capabilities.McpServers.Select(s => s.Name));
        Assert.Equal(["create_issue"], Assert.Single(capabilities.McpStatus!).Tools);
        Assert.False(File.Exists(path)); // the secrets are gone with the run
        var shown = (await _host.GetAsync($"api/threads/{threadId}")).GetProperty("run").GetProperty("capabilities");
        Assert.Equal("github", shown.GetProperty("mcpServers")[0].GetProperty("name").GetString());
        Assert.DoesNotContain("ghp_for_runs", shown.GetRawText());
    }

    [Fact]
    public async Task AWorkerThatNeverSaysItIsReady_DoesNotHoldTheRun_AndItsServersAreRecordedAsUnavailable()
    {
        await _host.DisposeAsync();
        await StartAsync(startCoordinator: true);
        await SaveMcpAsync(GitHub());
        var timeout = RunExecutor.McpReadyTimeout;
        RunExecutor.McpReadyTimeout = TimeSpan.FromMilliseconds(300);
        try
        {
            var threadId = await DelegatedThreadAsync();

            await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

            var status = Assert.Single((await CapabilitiesAsync(threadId)).McpStatus!);
            Assert.False(status.Connected);
            Assert.Contains("did not report its MCP servers ready", status.Error);
        }
        finally
        {
            RunExecutor.McpReadyTimeout = timeout;
        }
    }

    [Fact]
    public async Task ARunWithNoServer_IsGivenNoMcpConfig_AndDoesNotWait()
    {
        await _host.DisposeAsync();
        await StartAsync(startCoordinator: true);
        await SaveToolsAsync(new ToolSettings { ShellTimeoutSeconds = 600 });

        var threadId = await DelegatedThreadAsync();
        await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        Assert.Null((await WaitForWorkerAsync()).Launch.McpConfigPath);
        var capabilities = await CapabilitiesAsync(threadId);
        Assert.Equal((600, 0), (capabilities.ShellTimeoutSeconds, capabilities.McpServers.Count));
    }

    [Fact]
    public async Task ARework_ReusesItsTasksSnapshot_NotTheSettingsAsTheyAreNow()
    {
        await _host.DisposeAsync();
        await StartAsync(startCoordinator: true);
        await SaveToolsAsync(new ToolSettings { ShellTimeoutSeconds = 600 });
        var timeout = RunExecutor.McpReadyTimeout;
        RunExecutor.McpReadyTimeout = TimeSpan.FromMilliseconds(300);
        try
        {
            await SaveMcpAsync(GitHub());
            var threadId = await DelegatedThreadAsync();
            await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

            // An Admin changes the settings after the handoff.
            await SaveToolsAsync(new ToolSettings { ShellTimeoutSeconds = 1_200 });
            await SaveMcpAsync();
            await _host.DelegateAsync(threadId, "@factory CSV values containing commas are wrong.");

            var rework = await WaitForWorkerAsync(2);
            Assert.Equal(600, rework.Launch.ShellTimeoutSeconds);
            Assert.NotNull(rework.Launch.McpConfigPath);
            await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);
            Assert.Equal(["github"], (await CapabilitiesAsync(threadId)).McpServers.Select(s => s.Name));
        }
        finally
        {
            RunExecutor.McpReadyTimeout = timeout;
        }
    }

    private async Task SaveToolsAsync(ToolSettings tools) =>
        await _settings.SaveAsync(SettingsSections.Tools, tools, _settings.RevisionOf(SettingsSections.Tools), null, default);
}
