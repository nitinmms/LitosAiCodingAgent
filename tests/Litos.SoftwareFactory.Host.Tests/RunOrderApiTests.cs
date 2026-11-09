using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Host.Api;
using Litos.SoftwareFactory.Host.Runs;
using Litos.SoftwareFactory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>Run order and "Blocked by" over the API, and the merge watcher (docs/software-factory/run-order-architecture.md).</summary>
public sealed class RunOrderApiTests : IAsyncLifetime
{
    private TestHost _host = null!;

    // Nothing claims queued work: the order stays as the tests arrange it.
    public async Task InitializeAsync() => _host = await TestHost.StartAsync(startCoordinator: false);

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private async Task<Guid> DelegatedAsync(Guid projectId, string title, HttpClient? client = null)
    {
        client ??= _host.Client;
        using var created = await client.PostAsJsonAsync("api/threads", new { projectId, title, typeLabel = "feature" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var delegated = await client.PostAsJsonAsync($"api/threads/{id}/messages", new { messageId = Guid.NewGuid().ToString(), text = $"@factory {title}." });
        Assert.Equal(HttpStatusCode.Accepted, delegated.StatusCode);
        return id;
    }

    private async Task<List<string>> QueueTitlesAsync(Guid projectId) =>
        [.. (await _host.GetAsync($"api/projects/{projectId}/run-order")).GetProperty("queue").EnumerateArray()
            .Select(e => e.GetProperty("thread").GetProperty("title").GetString()!)];

    /// <summary>An accepted task with an open pull request, as a handoff and acceptance would leave it.</summary>
    private async Task AcceptAsync(Guid threadId, int pullRequest)
    {
        await using var db = await _host.App.Services.GetRequiredService<IDbContextFactory<FactoryDbContext>>().CreateDbContextAsync();
        await db.Threads.Where(t => t.Id == threadId).ExecuteUpdateAsync(s => s
            .SetProperty(t => t.State, LifecycleState.Accepted)
            .SetProperty(t => t.Stage, Stage.Done)
            .SetProperty(t => t.PullRequestNumber, (int?)pullRequest));
    }

    private static async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpClient client, HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        using var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    // ---- The run order ----

    [Fact]
    public async Task TheRunOrder_IsFirstInFirstOut_UntilSomeoneMovesATask()
    {
        var project = await _host.RegisterProjectAsync();
        await DelegatedAsync(project, "a");
        await DelegatedAsync(project, "b");
        var c = await DelegatedAsync(project, "c");
        Assert.Equal(["a", "b", "c"], await QueueTitlesAsync(project));

        var moved = await _host.PostAsync($"api/threads/{c}/queue-position", new { placement = "first" }, HttpStatusCode.OK);

        Assert.Equal(0, moved.GetProperty("queueRank").GetDouble());
        Assert.Equal(["c", "a", "b"], await QueueTitlesAsync(project));
    }

    [Fact]
    public async Task APlacement_MustBeOneTheApiKnows_WithTheTaskItNames()
    {
        var project = await _host.RegisterProjectAsync();
        var a = await DelegatedAsync(project, "a");

        var unknown = await _host.PostAsync($"api/threads/{a}/queue-position", new { placement = "top" }, HttpStatusCode.BadRequest);
        Assert.Equal("placement must be one of: first, after, end, swap.", unknown.GetProperty("error").GetString());
        await _host.PostAsync($"api/threads/{a}/queue-position", new { placement = "after" }, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AMoveFromAStaleView_IsAConflict()
    {
        var project = await _host.RegisterProjectAsync();
        var a = await DelegatedAsync(project, "a");

        await _host.PostAsync($"api/threads/{a}/queue-position", new { placement = "end", revision = 0 }, HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task AMember_SwapsTheirOwnTasks_ButCannotPutOneFirst_OrMoveSomeoneElses()
    {
        var project = await _host.RegisterProjectAsync();
        await _host.CreateMemberAsync("ben", project);
        var ben = await _host.SignedInAsync("ben");
        var bens1 = await DelegatedAsync(project, "ben 1", ben);
        var ashas = await DelegatedAsync(project, "asha");
        var bens2 = await DelegatedAsync(project, "ben 2", ben);

        var (first, firstBody) = await SendAsync(ben, HttpMethod.Post, $"api/threads/{bens2}/queue-position", new { placement = "first" });
        Assert.Equal(HttpStatusCode.Forbidden, first);
        Assert.Contains("Only an Admin can put a task ahead of other people's.", firstBody);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(ben, HttpMethod.Post, $"api/threads/{ashas}/queue-position", new { placement = "swap", otherThreadId = bens1 })).Status);

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(ben, HttpMethod.Post, $"api/threads/{bens2}/queue-position", new { placement = "swap", otherThreadId = bens1 })).Status);
        Assert.Equal(["ben 2", "asha", "ben 1"], await QueueTitlesAsync(project));
    }

    [Fact]
    public async Task TheRunOrder_OfAProjectSomeoneIsNotOn_IsNotFound()
    {
        var project = await _host.RegisterProjectAsync("a");
        var other = await _host.RegisterProjectAsync("b");
        await _host.CreateMemberAsync("ben", other);
        var ben = await _host.SignedInAsync("ben");

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(ben, HttpMethod.Get, $"api/projects/{project}/run-order")).Status);
    }

    // ---- New thread ----

    [Fact]
    public async Task NewThread_KeepsItsRunAfter_AndMakesItsBlockers()
    {
        var project = await _host.RegisterProjectAsync();
        var a = await DelegatedAsync(project, "a");
        var blocker = await _host.CreateThreadAsync(project, "Add column");

        var created = await _host.PostAsync("api/threads", new { projectId = project, title = "Use column", runAfterThreadId = a, blockedBy = new[] { blocker } }, HttpStatusCode.Created);

        var id = created.GetProperty("id").GetGuid();
        Assert.Equal(a, created.GetProperty("runAfterThreadId").GetGuid());
        var links = await _host.GetAsync($"api/threads/{id}/links");
        var link = Assert.Single(links.GetProperty("waitsFor").EnumerateArray());
        Assert.Equal(("Add column", "Draft"), (link.GetProperty("title").GetString(), link.GetProperty("state").GetString()));
        Assert.Single((await _host.GetAsync($"api/threads/{blocker}/links")).GetProperty("waitedOnBy").EnumerateArray());
    }

    [Fact]
    public async Task NewThread_RunFirst_IsAnAdminsAlone_AndNotWithRunAfter()
    {
        var project = await _host.RegisterProjectAsync();
        var a = await DelegatedAsync(project, "a");
        await _host.CreateMemberAsync("ben", project);
        var ben = await _host.SignedInAsync("ben");

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(ben, HttpMethod.Post, "api/threads", new { projectId = project, title = "urgent", runFirst = true })).Status);
        await _host.PostAsync("api/threads", new { projectId = project, title = "both", runFirst = true, runAfterThreadId = a }, HttpStatusCode.BadRequest);
        var urgent = await _host.PostAsync("api/threads", new { projectId = project, title = "urgent", runFirst = true }, HttpStatusCode.Created);
        Assert.True(urgent.GetProperty("runFirst").GetBoolean());
    }

    [Fact]
    public async Task NewThread_NamingATaskOfAnotherProject_IsRefused_AndNothingIsCreated()
    {
        var project = await _host.RegisterProjectAsync("a");
        var other = await _host.RegisterProjectAsync("b");
        var elsewhere = await _host.CreateThreadAsync(other, "elsewhere");

        var refused = await _host.PostAsync("api/threads", new { projectId = project, title = "x", blockedBy = new[] { elsewhere } }, HttpStatusCode.BadRequest);
        Assert.Equal("Run after and Blocked by name tasks of the same project.", refused.GetProperty("error").GetString());
        await _host.PostAsync("api/threads", new { projectId = project, title = "x", runAfterThreadId = Guid.NewGuid() }, HttpStatusCode.BadRequest);
        Assert.Empty(await _host.Store.ListThreadsAsync(project, default));
    }

    // ---- Links ----

    [Fact]
    public async Task ABlocker_IsAdded_AndReleasedNow()
    {
        var project = await _host.RegisterProjectAsync();
        var blocker = await _host.CreateThreadAsync(project, "Add column");
        var waiting = await DelegatedAsync(project, "Use column");

        var added = await _host.PostAsync($"api/threads/{waiting}/blockers", new { blockerThreadId = blocker }, HttpStatusCode.OK);
        Assert.Equal("Waiting for \"Add column\" to be finished and merged.", added.GetProperty("stateReason").GetString());
        var order = await _host.GetAsync($"api/projects/{project}/run-order");
        var entry = Assert.Single(order.GetProperty("queue").EnumerateArray());
        Assert.Equal("Add column", Assert.Single(entry.GetProperty("waitsFor").EnumerateArray()).GetProperty("title").GetString());

        var (status, _) = await SendAsync(_host.Client, HttpMethod.Delete, $"api/threads/{waiting}/blockers/{blocker}");
        Assert.Equal(HttpStatusCode.OK, status);
        var links = await _host.GetAsync($"api/threads/{waiting}/links");
        Assert.Equal("Manual", Assert.Single(links.GetProperty("waitsFor").EnumerateArray()).GetProperty("releaseReason").GetString());
    }

    [Fact]
    public async Task ALoop_IsAConflictThatNamesIt()
    {
        var project = await _host.RegisterProjectAsync();
        var a = await _host.CreateThreadAsync(project, "A");
        var b = await _host.CreateThreadAsync(project, "B");
        await _host.PostAsync($"api/threads/{b}/blockers", new { blockerThreadId = a }, HttpStatusCode.OK);

        var refused = await _host.PostAsync($"api/threads/{a}/blockers", new { blockerThreadId = b }, HttpStatusCode.Conflict);

        Assert.Equal("Not linked: that would make a loop. \"A\" would wait for \"B\", which waits for \"A\".", refused.GetProperty("error").GetString());
    }

    [Fact]
    public async Task AMember_CannotLinkSomeoneElsesTask()
    {
        var project = await _host.RegisterProjectAsync();
        var ashas = await _host.CreateThreadAsync(project, "asha's");
        var blocker = await _host.CreateThreadAsync(project, "blocker");
        await _host.CreateMemberAsync("ben", project);
        var ben = await _host.SignedInAsync("ben");

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(ben, HttpMethod.Post, $"api/threads/{ashas}/blockers", new { blockerThreadId = blocker })).Status);
    }

    [Fact]
    public async Task MarkMerged_ReleasesTheWaitingTasks_ButOnlyForAnAcceptedTask()
    {
        var project = await _host.RegisterProjectAsync();
        var blocker = await _host.CreateThreadAsync(project, "Add column");
        var waiting = await DelegatedAsync(project, "Use column");
        await _host.PostAsync($"api/threads/{waiting}/blockers", new { blockerThreadId = blocker }, HttpStatusCode.OK);

        await _host.PostAsync($"api/threads/{blocker}/merged", null, HttpStatusCode.Conflict);
        await AcceptAsync(blocker, 44);
        var merged = await _host.PostAsync($"api/threads/{blocker}/merged", null, HttpStatusCode.OK);

        Assert.NotEqual(JsonValueKind.Null, merged.GetProperty("pullRequestMergedAt").ValueKind);
        var link = Assert.Single((await _host.GetAsync($"api/threads/{waiting}/links")).GetProperty("waitsFor").EnumerateArray());
        Assert.Equal("Merged", link.GetProperty("releaseReason").GetString());
    }

    // ---- The merge watcher ----

    private MergeWatcher Watcher => _host.App.Services.GetRequiredService<MergeWatcher>();

    private async Task<(Guid Blocker, Guid Waiting)> AwaitingMergeAsync(int pullRequest = 44)
    {
        var project = await _host.RegisterProjectAsync($"repo{pullRequest}");
        var blocker = await _host.CreateThreadAsync(project, $"Add column {pullRequest}");
        var waiting = await DelegatedAsync(project, $"Use column {pullRequest}");
        await _host.PostAsync($"api/threads/{waiting}/blockers", new { blockerThreadId = blocker }, HttpStatusCode.OK);
        await AcceptAsync(blocker, pullRequest);
        return (blocker, waiting);
    }

    [Fact]
    public async Task AMergeIntoTheDefaultBranch_ReleasesTheWaitingTask()
    {
        var (blocker, waiting) = await AwaitingMergeAsync();
        _host.GitHub.State = PullRequestState.Merged;

        Assert.Equal(1, await Watcher.CheckAsync(default));

        Assert.NotNull((await _host.ThreadAsync(blocker)).Thread.PullRequestMergedAt);
        Assert.Equal(BlockerRelease.Merged, (await _host.Store.GetThreadLinksAsync(waiting, default)).WaitsFor.Single().Link.ReleaseReason);
        // Asked once, and not again once merged.
        Assert.Equal(0, await Watcher.CheckAsync(default));
        Assert.Single(_host.GitHub.StateRequests);
    }

    /// <summary>Merged somewhere else, the work has not reached the base the waiting task starts from.</summary>
    [Fact]
    public async Task AMergeIntoAnotherBranch_LeavesTheTaskWaiting()
    {
        var (blocker, waiting) = await AwaitingMergeAsync();
        _host.GitHub.State = PullRequestState.Merged;
        _host.GitHub.BaseBranches[44] = "release/1.2";

        Assert.Equal(0, await Watcher.CheckAsync(default));

        Assert.Null((await _host.ThreadAsync(blocker)).Thread.PullRequestMergedAt);
        Assert.Null((await _host.Store.GetThreadLinksAsync(waiting, default)).WaitsFor.Single().Link.ReleasedAt);
    }

    [Fact]
    public async Task APullRequestClosedUnmerged_PutsTheWaitingTaskWithItsOwner()
    {
        var (_, waiting) = await AwaitingMergeAsync();
        _host.GitHub.State = PullRequestState.Closed;

        Assert.Equal(1, await Watcher.CheckAsync(default));

        var thread = await _host.GetAsync($"api/threads/{waiting}");
        Assert.True(thread.GetProperty("thread").GetProperty("blockerNeedsYou").GetBoolean());
        Assert.Equal("AwaitingYou", thread.GetProperty("thread").GetProperty("turn").GetString());
    }

    [Fact]
    public async Task AnOpenPullRequest_ChangesNothing()
    {
        await AwaitingMergeAsync();
        _host.GitHub.State = PullRequestState.Open;

        Assert.Equal(0, await Watcher.CheckAsync(default));
        Assert.Single(_host.GitHub.StateRequests);
    }

    [Fact]
    public async Task WithoutAGitHubToken_NothingIsAsked()
    {
        await AwaitingMergeAsync();
        _host.GitHub.IsConfigured = false;
        _host.GitHub.State = PullRequestState.Merged;

        Assert.Equal(0, await Watcher.CheckAsync(default));
        Assert.Empty(_host.GitHub.StateRequests);
    }

    [Fact]
    public async Task OnePullRequestGitHubWillNotDescribe_DoesNotStopTheOthers()
    {
        await AwaitingMergeAsync(44);
        var (_, waiting) = await AwaitingMergeAsync(45);
        _host.GitHub.State = PullRequestState.Merged;
        _host.GitHub.Unreadable.Add(44);

        Assert.Equal(1, await Watcher.CheckAsync(default));
        Assert.NotNull((await _host.Store.GetThreadLinksAsync(waiting, default)).WaitsFor.Single().Link.ReleasedAt);
    }

    [Theory]
    [InlineData(PullRequestState.Merged, "main", PullRequestOutcome.Merged)]
    [InlineData(PullRequestState.Merged, null, PullRequestOutcome.Merged)]
    [InlineData(PullRequestState.Merged, "develop", null)]
    [InlineData(PullRequestState.Closed, "main", PullRequestOutcome.Closed)]
    [InlineData(PullRequestState.Open, "main", null)]
    [InlineData(PullRequestState.Draft, "main", null)]
    public void WhatAPullRequestMeans_ForTheTasksWaitingOnIt(PullRequestState state, string? baseBranch, PullRequestOutcome? expected) =>
        Assert.Equal(expected, MergeWatcher.OutcomeOf(new PullRequestDetails(state, baseBranch), "main"));

    [Theory]
    [InlineData("first", null, true)]
    [InlineData("END", null, true)]
    [InlineData(" after ", "x", true)]
    [InlineData("swap", "x", true)]
    [InlineData("after", null, false)]
    [InlineData("swap", null, false)]
    [InlineData("top", null, false)]
    [InlineData(null, null, false)]
    public void APlacementIsParsed(string? placement, string? other, bool valid)
    {
        var parsed = RunOrderApi.ParsePlacement(new QueuePositionRequest(placement!, other is null ? null : Guid.NewGuid()), out var refused);
        Assert.Equal(valid, parsed is not null);
        Assert.Equal(valid, refused is null);
    }
}
