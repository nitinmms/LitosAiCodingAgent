using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Settings;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Host.Runs;
using Litos.SoftwareFactory.Host.Settings;
using Litos.Tools.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>Tavily as a test scripts it: what it answers, and the searches it was sent.</summary>
public sealed class FakeTavily : HttpMessageHandler
{
    public List<string> Requests { get; } = [];

    public Func<HttpResponseMessage> Answer { get; set; } = () => Results(("CSV on Wikipedia", "https://en.wikipedia.org/wiki/Comma-separated_values", "A CSV file..."));

    public static HttpResponseMessage Results(params (string Title, string Url, string Content)[] results) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(new { results = results.Select(r => new { title = r.Title, url = r.Url, content = r.Content }) }),
            Encoding.UTF8, "application/json"),
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(await request.Content!.ReadAsStringAsync(ct));
        return Answer();
    }
}

/// <summary>The Tools tab and what it governs (m3-architecture.md §6): PTC per thread, the shell limit, and web search.</summary>
public sealed class ToolsTests : IAsyncLifetime
{
    private readonly FakeTavily _tavily = new();
    private TestHost _host = null!;
    private FactorySettings _settings = null!;

    public async Task InitializeAsync() => await StartAsync(startCoordinator: false);

    private async Task StartAsync(bool startCoordinator)
    {
        _host = await TestHost.StartAsync(startCoordinator: startCoordinator, services: s =>
            s.AddSingleton(new TavilySearchClient(new HttpClient(_tavily) { BaseAddress = new Uri("https://api.tavily.com/") })));
        _settings = _host.App.Services.GetRequiredService<FactorySettings>();
    }

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    private async Task SaveToolsAsync(ToolSettings tools) =>
        await _settings.SaveAsync(SettingsSections.Tools, tools, _settings.RevisionOf(SettingsSections.Tools), null, default);

    private async Task<HttpResponseMessage> PutToolsAsync(object settings, HttpClient? client = null) =>
        await (client ?? _host.Client).PutAsJsonAsync("api/admin/settings/tools", new { revision = _settings.RevisionOf(SettingsSections.Tools), settings });

    private async Task<HttpResponseMessage> CreateAsync(Guid projectId, bool? ptcEnabled, HttpClient? client = null) =>
        await (client ?? _host.Client).PostAsJsonAsync("api/threads", new { projectId, title = "Add CSV export", ptcEnabled });

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    // ---- The settings ----

    [Fact]
    public async Task TheFirstStart_WritesTheToolsWithTheHostsPtcOption()
    {
        var tools = (await _host.GetAsync("api/admin/settings")).GetProperty("tools");

        Assert.Equal(1, tools.GetProperty("revision").GetInt64());
        var settings = tools.GetProperty("settings");
        Assert.Equal(_host.Options.PtcEnabled, settings.GetProperty("ptcByDefault").GetBoolean());
        Assert.Equal(300, settings.GetProperty("shellTimeoutSeconds").GetInt32());
        Assert.True(settings.GetProperty("webSearchEnabled").GetBoolean());
        Assert.False(settings.GetProperty("webSearchOnReadOnlyTurns").GetBoolean());
    }

    [Fact]
    public async Task AnAdmin_SavesTheTools_AndTheHostRefusesWhatCannotWork()
    {
        using var refused = await PutToolsAsync(new { ptcByDefault = true, membersMayChoosePtc = true, shellTimeoutSeconds = 5, webSearchEnabled = false, webSearchOnReadOnlyTurns = true });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var error = (await BodyAsync(refused)).GetProperty("error").GetString();
        Assert.Contains("between 30 and 3600 seconds", error);
        Assert.Contains("needs web search to be on", error);

        using var saved = await PutToolsAsync(new { ptcByDefault = false, membersMayChoosePtc = false, shellTimeoutSeconds = 900, webSearchEnabled = true });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal((false, 900, true), (_settings.Tools.PtcByDefault, _settings.Tools.ShellTimeoutSeconds, _settings.Tools.WebSearchEnabled));
    }

    [Fact]
    public async Task OnlyAnAdmin_ChangesTheTools()
    {
        await _host.CreateMemberAsync("maria");

        using var refused = await PutToolsAsync(new { shellTimeoutSeconds = 900 }, await _host.SignedInAsync("maria"));

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [Fact]
    public async Task TheWebSearchKey_IsASecretTheAppCanSet()
    {
        using var set = await _host.Client.PutAsJsonAsync($"api/admin/secrets/{Uri.EscapeDataString(SecretNames.WebSearch)}", new { value = "tvly-key" });

        Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);
        Assert.Equal("tvly-key", _settings.Secret(SecretNames.WebSearch));
    }

    // ---- PTC per thread ----

    [Fact]
    public async Task ANewThread_GetsTheDefaultPtc_AndTheAppIsToldIt()
    {
        await SaveToolsAsync(new ToolSettings { PtcByDefault = false });

        using var created = await CreateAsync(await _host.RegisterProjectAsync(), ptcEnabled: null);

        var view = await BodyAsync(created);
        Assert.False(view.GetProperty("ptcEnabled").GetBoolean());
        Assert.False((await _host.ThreadAsync(view.GetProperty("id").GetGuid())).Thread.PtcEnabled);
        var settings = await _host.GetAsync("api/settings");
        Assert.Equal((false, true), (settings.GetProperty("ptcEnabled").GetBoolean(), settings.GetProperty("canChoosePtc").GetBoolean()));
    }

    [Fact]
    public async Task AMember_MayTurnPtcOff_OnlyWhenTheAdminAllowsIt()
    {
        var projectId = await _host.RegisterProjectAsync();
        await _host.CreateMemberAsync("maria", projectId);
        var maria = await _host.SignedInAsync("maria");

        using (var allowed = await CreateAsync(projectId, ptcEnabled: false, maria))
            Assert.False((await BodyAsync(allowed)).GetProperty("ptcEnabled").GetBoolean());

        await SaveToolsAsync(new ToolSettings { MembersMayChoosePtc = false });
        using var refused = await CreateAsync(projectId, ptcEnabled: false, maria);
        using var asTheDefault = await CreateAsync(projectId, ptcEnabled: true, maria);
        using var byAnAdmin = await CreateAsync(projectId, ptcEnabled: false);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("only an Admin can change that", (await BodyAsync(refused)).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.Created, asTheDefault.StatusCode);
        Assert.False((await BodyAsync(byAnAdmin)).GetProperty("ptcEnabled").GetBoolean());
        Assert.False((await maria.GetFromJsonAsync<JsonElement>("api/settings")).GetProperty("canChoosePtc").GetBoolean());
    }

    [Fact]
    public void AThreadFromBeforeM3_FollowsTheHostsPtcOption()
    {
        var options = new FactoryOptions { PtcEnabled = false };
        TaskThread Thread(bool? ptc) => new() { Provider = "openrouter", Model = "m", SessionId = "s", Title = "t", TypeLabel = "feature", PtcEnabled = ptc };

        Assert.False(options.PtcOf(Thread(null)));
        Assert.True(options.PtcOf(Thread(true)));
    }

    // ---- What a worker is launched with ----

    [Fact]
    public async Task AWorker_IsLaunchedWithItsThreadsPtc_TheShellLimit_AndWhichTurnsMaySearch()
    {
        await _host.DisposeAsync();
        await StartAsync(startCoordinator: true);
        await SaveToolsAsync(new ToolSettings { ShellTimeoutSeconds = 900, WebSearchEnabled = true, WebSearchOnReadOnlyTurns = true });
        await _settings.SetSecretAsync(SecretNames.WebSearch, "tvly-key", null, default);
        using var created = await CreateAsync(await _host.RegisterProjectAsync(), ptcEnabled: false);

        await _host.DelegateAsync((await BodyAsync(created)).GetProperty("id").GetGuid());

        var launch = await WaitForLaunchAsync();
        Assert.Equal((false, (int?)900, WebSearchAccess.AllTurns), (launch.PtcEnabled, launch.ShellTimeoutSeconds, launch.WebSearch));
    }

    [Fact]
    public async Task WithWebSearchOnByDefaultButNoKey_AWorkerCannotSearch()
    {
        await _host.DisposeAsync();
        await StartAsync(startCoordinator: true);
        using var created = await CreateAsync(await _host.RegisterProjectAsync(), ptcEnabled: null);

        await _host.DelegateAsync((await BodyAsync(created)).GetProperty("id").GetGuid());

        Assert.Equal(WebSearchAccess.Off, (await WaitForLaunchAsync()).WebSearch);
    }

    private async Task<Core.Ports.WorkerLaunch> WaitForLaunchAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (_host.Workers.Workers.IsEmpty)
        {
            Assert.True(DateTime.UtcNow < deadline, "No worker was launched.");
            await Task.Delay(25);
        }

        return _host.Workers.Workers.First().Launch;
    }

    // ---- Searching, through the host ----

    /// <summary>A run in the middle of a turn of <paramref name="kind"/>, as a worker would call back for.</summary>
    private ActiveRun Running(TurnKind kind)
    {
        var active = new ActiveRun(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "openrouter", "m");
        _host.App.Services.GetRequiredService<RunRegistry>().TryAdd(active);
        active.BeginTurn(kind, TurnKind.Implement, "session-1", CancellationToken.None);
        return active;
    }

    private async Task<WebSearchResponse> SearchAsync(ActiveRun active, string query = "csv rfc 4180", string? secret = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, FactoryWire.WebSearchPath(active.RunId.ToString("N")))
        {
            Content = JsonContent.Create(new WebSearchRequest("session-1", query, 3), options: FactoryWire.Json),
        };
        request.Headers.Add(FactoryWire.SecretHeader, secret ?? active.Secret);
        using var response = await _host.NewClient().SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
        return expected == HttpStatusCode.OK ? (await response.Content.ReadFromJsonAsync<WebSearchResponse>(FactoryWire.Json))! : null!;
    }

    private async Task WebSearchOnAsync(bool readOnlyTurns = false)
    {
        await SaveToolsAsync(new ToolSettings { WebSearchEnabled = true, WebSearchOnReadOnlyTurns = readOnlyTurns });
        await _settings.SetSecretAsync(SecretNames.WebSearch, "tvly-key", null, default);
    }

    private List<JsonElement> LogOf(ActiveRun active)
    {
        var path = RunWebSearch.LogPath(_host.Options.DataDirectory, active.RunId);
        return File.Exists(path) ? [.. File.ReadAllLines(path).Select(l => JsonDocument.Parse(l).RootElement.Clone())] : [];
    }

    [Fact]
    public async Task AWorkTurn_Searches_WithTheHostsKey_AndTheQueryAndUrlsAreLogged()
    {
        await WebSearchOnAsync();
        var active = Running(TurnKind.Implement);

        var response = await SearchAsync(active);

        Assert.False(response.IsError);
        Assert.Equal("CSV on Wikipedia — https://en.wikipedia.org/wiki/Comma-separated_values\nA CSV file...", response.Text);
        var sent = Assert.Single(_tavily.Requests);
        Assert.Contains("\"api_key\":\"tvly-key\"", sent);
        Assert.Contains("\"query\":\"csv rfc 4180\"", sent);
        var logged = Assert.Single(LogOf(active));
        Assert.Equal(("Implement", "csv rfc 4180"), (logged.GetProperty("turn").GetString(), logged.GetProperty("query").GetString()));
        Assert.Equal(["https://en.wikipedia.org/wiki/Comma-separated_values"], logged.GetProperty("urls").EnumerateArray().Select(u => u.GetString()));
    }

    [Theory]
    [InlineData(TurnKind.Review)]
    [InlineData(TurnKind.Spec)]
    [InlineData(TurnKind.Chat)]
    public async Task AReadOnlyTurn_IsRefused_UnlessTheAdminAllowsIt(TurnKind kind)
    {
        await WebSearchOnAsync();
        var refused = await SearchAsync(Running(kind));

        Assert.True(refused.IsError);
        Assert.Contains("not available on a turn that only reads", refused.Text);
        Assert.Empty(_tavily.Requests);

        await WebSearchOnAsync(readOnlyTurns: true);
        Assert.False((await SearchAsync(Running(kind))).IsError);
    }

    [Fact]
    public async Task WebSearchTurnedOff_OrWithNoKey_IsRefusedMidRun_AndTheRefusalIsLogged()
    {
        await SaveToolsAsync(new ToolSettings { WebSearchEnabled = false });
        var active = Running(TurnKind.Implement);

        var off = await SearchAsync(active);
        await SaveToolsAsync(new ToolSettings { WebSearchEnabled = true });
        var noKey = await SearchAsync(active);

        Assert.Equal((true, "Web search is turned off for this factory."), (off.IsError, off.Text));
        Assert.Equal((true, "Web search has no key set. An Admin sets it under Settings, Tools."), (noKey.IsError, noKey.Text));
        Assert.Empty(_tavily.Requests);
        Assert.Equal(2, LogOf(active).Count(l => l.GetProperty("error").ValueKind == JsonValueKind.String));
    }

    [Fact]
    public async Task ASearchTavilyRefuses_SaysWhy_AndIsLogged()
    {
        await WebSearchOnAsync();
        _tavily.Answer = () => new HttpResponseMessage(HttpStatusCode.Unauthorized) { ReasonPhrase = "Unauthorized" };
        var active = Running(TurnKind.Implement);

        var failed = await SearchAsync(active);

        Assert.Equal((true, "Web search failed: 401 Unauthorized"), (failed.IsError, failed.Text));
        Assert.Equal("Web search failed: 401 Unauthorized", Assert.Single(LogOf(active)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task AVeryLongQuery_IsCut_AndAnEmptyOneRefused()
    {
        await WebSearchOnAsync();
        var active = Running(TurnKind.Implement);

        await SearchAsync(active, new string('q', 1_000));
        var empty = await SearchAsync(active, "   ");

        Assert.Equal(RunWebSearch.MaxQueryLength, Assert.Single(LogOf(active)).GetProperty("query").GetString()!.Length);
        Assert.Equal((true, "A 'query' argument is required."), (empty.IsError, empty.Text));
    }

    [Fact]
    public async Task ASearchWithoutTheRunsSecret_IsUnauthorized()
    {
        await WebSearchOnAsync();

        await SearchAsync(Running(TurnKind.Implement), secret: "not-the-secret", expected: HttpStatusCode.Unauthorized);

        Assert.Empty(_tavily.Requests);
    }
}
