using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Orchestration;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Host.Auth;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>
/// Every call checks project membership (§13.3, m2-architecture.md §4). Ben is a Member of
/// SalesApp only; anything in filedb-sharp answers him exactly as an id that does not exist.
/// The Admin sees both.
/// </summary>
public sealed class MembershipApiTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private HttpClient _ben = null!;
    private Guid _sales, _filedb, _salesThread, _filedbThread;

    public async Task InitializeAsync()
    {
        _host = await TestHost.StartAsync(startCoordinator: false);
        _sales = await _host.RegisterProjectAsync("salesapp");
        _filedb = await _host.RegisterProjectAsync("filedb-sharp");
        _salesThread = await _host.CreateThreadAsync(_sales, "Add CSV export");
        _filedbThread = await _host.CreateThreadAsync(_filedb, "Compact the file");
        await _host.CreateMemberAsync("ben", _sales);
        _ben = await _host.SignedInAsync("ben");
    }

    public async Task DisposeAsync()
    {
        _ben.Dispose();
        await _host.DisposeAsync();
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private async Task<HttpStatusCode> StatusAsync(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (method != HttpMethod.Get)
            request.Content = JsonContent.Create(body ?? new { });
        using var response = await _ben.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        return response.StatusCode;
    }

    /// <summary>A decision opened on a thread, as a run asking a question would.</summary>
    private async Task<Guid> OpenDecisionAsync(Guid threadId)
    {
        await _host.Store.DispatchAsync(threadId, await _host.AdminIdAsync(), Guid.NewGuid().ToString(), "Do it.", DateTimeOffset.UtcNow, default);
        var claimed = (await _host.Store.ClaimNextRunAsync(5, DateTimeOffset.UtcNow, default))!;
        var decision = await _host.Store.OpenDecisionAsync(claimed.Run.Id, new DecisionSubmission("All rows?", "why", ["Yes", "No"]), DateTimeOffset.UtcNow, default);
        await _host.Store.StopRunAsync(new StopRunCommand(claimed.Run.Id, LifecycleTrigger.RequestDecision, StopReason.DecisionNeeded, "Q"), DateTimeOffset.UtcNow, default);
        return decision.Id;
    }

    // ---- Lists show only what the person may see ----

    [Fact]
    public async Task Projects_AMemberSeesOnlyTheirs_TheAdminSeesAll()
    {
        var ben = await JsonAsync(await _ben.GetAsync("api/projects"));
        var admin = await _host.GetAsync("api/projects");

        Assert.Equal([_sales], ben.EnumerateArray().Select(p => p.GetProperty("id").GetGuid()));
        Assert.Equal(2, admin.GetArrayLength());
    }

    [Fact]
    public async Task Threads_AMemberSeesOnlyTheirProjects_EvenWhenAskingForAnother()
    {
        var all = await JsonAsync(await _ben.GetAsync("api/threads"));
        var other = await JsonAsync(await _ben.GetAsync($"api/threads?projectId={_filedb}"));

        Assert.Equal([_salesThread], all.EnumerateArray().Select(t => t.GetProperty("id").GetGuid()));
        Assert.Equal(0, other.GetArrayLength());
        Assert.Equal(2, (await _host.GetAsync("api/threads")).GetArrayLength());
    }

    [Fact]
    public async Task ReviewYield_AcrossTasks_CountsOnlyTheMembersProjects()
    {
        var ben = await JsonAsync(await _ben.GetAsync("api/review-yield"));

        Assert.Equal([_salesThread], ben.GetProperty("threads").EnumerateArray().Select(t => t.GetProperty("threadId").GetGuid()));
    }

    // ---- Anything in another project is not found ----

    public static TheoryData<string, string> ThreadRoutes => new()
    {
        { "GET", "" }, { "GET", "/usage" }, { "GET", "/pull-request" }, { "GET", "/review-yield" }, { "GET", "/events" },
        { "POST", "/messages" }, { "POST", "/budget" }, { "POST", "/accept" }, { "POST", "/pause" }, { "POST", "/cancel" },
        { "POST", "/withdraw" }, { "POST", "/resume" },
    };

    [Theory]
    [MemberData(nameof(ThreadRoutes))]
    public async Task EveryThreadRoute_InAnotherProject_IsNotFound_AndChangesNothing(string method, string route)
    {
        var before = await _host.ThreadAsync(_filedbThread);

        var status = await StatusAsync(new HttpMethod(method), $"api/threads/{_filedbThread}{route}",
            new { messageId = "m1", text = "@factory Compact it.", cap = 1_000 });

        Assert.Equal(HttpStatusCode.NotFound, status);
        var after = await _host.ThreadAsync(_filedbThread);
        Assert.Equal((before.Thread.Revision, before.Thread.State, before.Thread.BudgetCap), (after.Thread.Revision, after.Thread.State, after.Thread.BudgetCap));
        Assert.Empty(after.Messages);
    }

    /// <summary>The same answer as for an id that does not exist anywhere.</summary>
    [Fact]
    public async Task AThreadInAnotherProject_LooksExactlyLikeOneThatDoesNotExist()
    {
        using var hidden = await _ben.GetAsync($"api/threads/{_filedbThread}");
        using var missing = await _ben.GetAsync($"api/threads/{Guid.NewGuid()}");

        Assert.Equal((hidden.StatusCode, await hidden.Content.ReadAsStringAsync()), (missing.StatusCode, await missing.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task TheirOwnProjectsThread_IsVisible()
    {
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(HttpMethod.Get, $"api/threads/{_salesThread}"));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(HttpMethod.Post, $"api/threads/{_salesThread}/budget", new { cap = 400_000 }));
    }

    [Fact]
    public async Task CreatingAThread_InAnotherProject_IsNotFound_InTheirOwnIsCreated()
    {
        using var other = await _ben.PostAsJsonAsync("api/threads", new { projectId = _filedb, title = "Sneak in", typeLabel = "feature" });
        using var own = await _ben.PostAsJsonAsync("api/threads", new { projectId = _sales, title = "Fix the footer", typeLabel = "bug" });

        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        Assert.Equal(HttpStatusCode.Created, own.StatusCode);
        Assert.DoesNotContain(await _host.Store.ListThreadsAsync(_filedb, default), t => t.Title == "Sneak in");
    }

    [Fact]
    public async Task ADecisionInAnotherProject_IsNotFound_AndStaysOpen()
    {
        var decision = await OpenDecisionAsync(_filedbThread);

        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(HttpMethod.Post, $"api/decisions/{decision}/answer", new { answer = "Yes" }));
        Assert.Equal(LifecycleState.AwaitingDecision, (await _host.ThreadAsync(_filedbThread)).Thread.State);
    }

    [Fact]
    public async Task ADecisionInTheirOwnProject_CanBeAnswered()
    {
        var decision = await OpenDecisionAsync(_salesThread);

        Assert.Equal(HttpStatusCode.OK, await StatusAsync(HttpMethod.Post, $"api/decisions/{decision}/answer", new { answer = "Yes" }));
    }

    [Fact]
    public async Task AFindingInAnotherProject_IsNotFound_AndKeepsNoVerdict()
    {
        await _host.Store.DispatchAsync(_filedbThread, await _host.AdminIdAsync(), "m", "Do it.", DateTimeOffset.UtcNow, default);
        var claimed = (await _host.Store.ClaimNextRunAsync(5, DateTimeOffset.UtcNow, default))!;
        await _host.Store.SaveFindingsAsync(claimed.Run.Id, [new ReviewFinding(FindingSeverity.Minor, "src/A.cs", 1, "Leftover debug output.")], default);
        var finding = Assert.Single(await _host.Store.ListFindingsAsync(_filedbThread, default));

        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(HttpMethod.Post, $"api/findings/{finding.Id}/verdict", new { verdict = "Wrong" }));
        Assert.Null(Assert.Single(await _host.Store.ListFindingsAsync(_filedbThread, default)).Verdict);
    }

    [Fact]
    public async Task TheAdmin_SeesAThreadInEveryProject()
    {
        Assert.Equal(_filedbThread, (await _host.GetAsync($"api/threads/{_filedbThread}")).GetProperty("thread").GetProperty("id").GetGuid());
    }

    /// <summary>
    /// The guard against a route added later without the check: every route that names a thread,
    /// decision or finding by id carries it.
    /// </summary>
    [Fact]
    public void EveryRouteNamingAProjectsThing_ChecksMembership()
    {
        var routes = _host.App.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText is { } raw
                && (raw.Contains("threads/{id", StringComparison.Ordinal) || raw.Contains("decisions/{id", StringComparison.Ordinal) || raw.Contains("findings/{id", StringComparison.Ordinal)))
            .ToList();

        // 12 thread routes (the events stream among them), a decision's answer and a finding's verdict.
        Assert.True(routes.Count >= 14, $"Expected the thread, decision and finding routes, found {routes.Count}.");
        Assert.All(routes, e => Assert.True(e.Metadata.GetMetadata<ProjectAccessMetadata>() is not null, $"{e.RoutePattern.RawText} does not check membership."));
    }

    /// <summary>
    /// Found on the M2 check: the app labelled every person's message with the viewer's own name,
    /// so Ben saw the Admin's request as his. Each message now names who wrote it.
    /// </summary>
    [Fact]
    public async Task EachPersonsMessage_NamesWhoWroteIt_WhoeverReadsTheThread()
    {
        await _host.DelegateAsync(_salesThread, "@factory Add CSV export for Orders.");
        using (var said = await _ben.PostAsJsonAsync($"api/threads/{_salesThread}/messages", new { messageId = Guid.NewGuid().ToString(), text = "Use semicolons." }))
            Assert.Equal(HttpStatusCode.Accepted, said.StatusCode);

        foreach (var reader in new[] { _ben, _host.Client })
        {
            var messages = (await JsonAsync(await reader.GetAsync($"api/threads/{_salesThread}"))).GetProperty("messages").EnumerateArray().ToList();
            string? NameOn(string text) => messages.Single(m => m.GetProperty("text").GetString() == text).GetProperty("authorName").GetString();

            Assert.Equal("admin", NameOn("Add CSV export for Orders."));
            Assert.Equal("ben", NameOn("Use semicolons."));
            Assert.All(
                messages.Where(m => m.GetProperty("author").GetString() == "Factory"),
                m => Assert.Equal(JsonValueKind.Null, m.GetProperty("authorName").ValueKind));
        }
    }
}
