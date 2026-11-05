using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Litos.Artifacts;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Host.Api;
using Litos.SoftwareFactory.Host.Runs;
using Microsoft.Extensions.DependencyInjection;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>
/// Pages a chat answer publishes on its thread (ReadMe_ArtifactsFeature.md §11): the worker's
/// callbacks, what only a chat run may do through them, the answer that shows the page, and the
/// browser route that serves it — sandboxed, and only to the thread's people.
/// </summary>
public sealed class ArtifactsApiTests
{
    private const string Page = "<!doctype html><title>Export Pipeline</title><p>v1</p>";

    private static async Task<(TestHost Host, Guid ProjectId, Guid ThreadId)> StartAsync()
    {
        var host = await TestHost.StartAsync();
        var projectId = await host.RegisterProjectAsync();
        return (host, projectId, await host.CreateThreadAsync(projectId));
    }

    private static async Task<ArtifactPublishResponse> PublishAsync(FakeWorker worker, string html, Guid? id = null, string? title = null)
    {
        using var response = await worker.HostHttp.PostAsJsonAsync(
            FactoryWire.ArtifactsPath(worker.Launch.RunId), new ArtifactPublishRequest(html, title, id), FactoryWire.Json);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ArtifactPublishResponse>(FactoryWire.Json))!;
    }

    /// <summary>A chat turn that runs <paramref name="during"/> as the agent's tool calls, then answers.</summary>
    private static void ChatThat(TestHost host, Func<TurnCall, Task> during, string reply = "Here is the page.") =>
        host.Workers.Script.Enqueue(async call =>
        {
            Assert.Equal(TurnKind.Chat, call.Kind);
            await during(call);
            return new TurnStreamResult(true, 1, null, reply);
        });

    private static async Task<JsonElement> AnswerAsync(TestHost host, Guid threadId, int factoryMessages = 1)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var view = await host.GetAsync($"api/threads/{threadId}");
            var factory = view.GetProperty("messages").EnumerateArray().Where(m => m.GetProperty("author").GetString() == "Factory").ToList();
            if (factory.Count >= factoryMessages && !view.GetProperty("chatPending").GetBoolean()
                && host.App.Services.GetRequiredService<RunRegistry>().Count == 0)
            {
                return factory[^1];
            }

            if (DateTime.UtcNow > deadline)
                Assert.Fail("No answer: " + view);
            await Task.Delay(40);
        }
    }

    // ---- Publishing from a chat answer ----

    [Fact]
    public async Task AChatAnswer_PublishesAPage_ShownAsACardOnTheAnswer_AndServedSandboxed()
    {
        var (host, _, threadId) = await StartAsync();
        await using var _ = host;
        ArtifactPublishResponse? published = null;
        ChatThat(host, async call => published = await PublishAsync(call.Worker, Page));

        await host.DelegateAsync(threadId, "Draw me the export pipeline.");
        var answer = await AnswerAsync(host, threadId);

        Assert.True(published!.Accepted, published.Error);
        Assert.True(published.Created);
        var page = published.Artifact!;
        Assert.Equal(("Export Pipeline", 1), (page.Title, page.LatestVersion));

        var card = Assert.Single(answer.GetProperty("artifacts").EnumerateArray());
        Assert.Equal(page.Id, card.GetProperty("id").GetGuid());
        Assert.Equal(("Export Pipeline", 1, 1), (card.GetProperty("title").GetString(), card.GetProperty("version").GetInt32(), card.GetProperty("latestVersion").GetInt32()));
        Assert.Equal($"/api/artifacts/{page.Id}/v/1", card.GetProperty("url").GetString());
        Assert.Equal($"/api/artifacts/{page.Id}", card.GetProperty("latestUrl").GetString());

        using var served = await host.Client.GetAsync(card.GetProperty("url").GetString()!.TrimStart('/'));
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal(Page, await served.Content.ReadAsStringAsync());
        Assert.Equal("text/html", served.Content.Headers.ContentType?.MediaType);
        Assert.Equal(ArtifactContentPolicy.ContentSecurityPolicy, string.Join(", ", served.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal("nosniff", Assert.Single(served.Headers.GetValues("X-Content-Type-Options")));
        Assert.True(served.Headers.CacheControl?.NoStore);
        Assert.Null(served.Content.Headers.ContentDisposition);
    }

    [Fact]
    public async Task AFollowUp_IsToldThePagesOnTheThread_AndUpdatesOneById_SoTheEarlierCardPointsToTheNewer()
    {
        var (host, _, threadId) = await StartAsync();
        await using var _ = host;
        ArtifactSummary? first = null;
        ChatThat(host, async call => first = (await PublishAsync(call.Worker, Page)).Artifact);
        await host.DelegateAsync(threadId, "Draw me the export pipeline.");
        await AnswerAsync(host, threadId);

        string? followUpBrief = null;
        ArtifactPublishResponse? second = null;
        ChatThat(host, async call =>
        {
            followUpBrief = call.Brief;
            second = await PublishAsync(call.Worker, Page.Replace("v1", "v2"), first!.Id);
        }, "Updated it.");
        await host.DelegateAsync(threadId, "Make the failed steps red.");
        var answer = await AnswerAsync(host, threadId, factoryMessages: 2);

        Assert.Contains("## Pages published on this thread", followUpBrief);
        Assert.Contains($"`{first!.Id}`: \"Export Pipeline\", version 1", followUpBrief);
        Assert.Equal((first.Id, 2, false), (second!.Artifact!.Id, second.Artifact.LatestVersion, second.Created));
        Assert.Equal(2, Assert.Single(answer.GetProperty("artifacts").EnumerateArray()).GetProperty("version").GetInt32());

        var view = await host.GetAsync($"api/threads/{threadId}");
        var earlier = view.GetProperty("messages").EnumerateArray().First(m => m.GetProperty("author").GetString() == "Factory");
        var earlierCard = Assert.Single(earlier.GetProperty("artifacts").EnumerateArray());
        Assert.Equal((1, 2), (earlierCard.GetProperty("version").GetInt32(), earlierCard.GetProperty("latestVersion").GetInt32()));

        using var v1 = await host.Client.GetAsync($"api/artifacts/{first.Id}/v/1");
        using var latest = await host.Client.GetAsync($"api/artifacts/{first.Id}");
        Assert.Contains("v1", await v1.Content.ReadAsStringAsync());
        Assert.Contains("v2", await latest.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TheChatRun_CanListAndReadItsThreadsPages_ButNotAnotherThreads()
    {
        var (host, projectId, threadId) = await StartAsync();
        await using var _ = host;
        var otherThread = await host.CreateThreadAsync(projectId, "Another task");
        ArtifactSummary? theirs = null;
        ChatThat(host, async call => theirs = (await PublishAsync(call.Worker, "<title>Theirs</title>")).Artifact);
        await host.DelegateAsync(otherThread, "Draw it.");
        await AnswerAsync(host, otherThread);

        List<ArtifactSummary>? listed = null;
        ArtifactReadResponse? own = null, foreign = null, missingVersion = null;
        ArtifactPublishResponse? hijack = null;
        ChatThat(host, async call =>
        {
            var mine = (await PublishAsync(call.Worker, Page)).Artifact!;
            var http = call.Worker.HostHttp;
            var runId = call.Worker.Launch.RunId;
            listed = await http.GetFromJsonAsync<List<ArtifactSummary>>(FactoryWire.ArtifactsPath(runId), FactoryWire.Json);
            own = await http.GetFromJsonAsync<ArtifactReadResponse>(FactoryWire.ArtifactPath(runId, mine.Id, null), FactoryWire.Json);
            missingVersion = await http.GetFromJsonAsync<ArtifactReadResponse>(FactoryWire.ArtifactPath(runId, mine.Id, 7), FactoryWire.Json);
            foreign = await http.GetFromJsonAsync<ArtifactReadResponse>(FactoryWire.ArtifactPath(runId, theirs!.Id, null), FactoryWire.Json);
            hijack = await PublishAsync(call.Worker, "<p>mine now</p>", theirs.Id);
        });
        await host.DelegateAsync(threadId, "Draw mine.");
        await AnswerAsync(host, threadId);

        Assert.Equal(["Export Pipeline"], listed!.Select(a => a.Title));
        Assert.Equal((Page, 1), (own!.Html, own.Version));
        Assert.Null(missingVersion!.Html);
        Assert.NotNull(missingVersion.Artifact);
        Assert.Null(foreign!.Artifact);
        Assert.Null(foreign.Html);
        Assert.False(hijack!.Accepted);
        Assert.Contains("no artifact with id", hijack.Error);
        Assert.Equal(1, (await host.Store.GetArtifactAsync(theirs!.Id, default))!.CurrentVersion);
    }

    [Fact]
    public async Task Publishing_ARefusedPage_SaysWhy_AndStoresNothing()
    {
        var (host, _, threadId) = await StartAsync();
        await using var _ = host;
        ArtifactPublishResponse? empty = null, oversized = null;
        ChatThat(host, async call =>
        {
            empty = await PublishAsync(call.Worker, "   ");
            oversized = await PublishAsync(call.Worker, new string('x', ArtifactsApi.MaxPageBytes + 1));
        });

        await host.DelegateAsync(threadId, "Draw it.");
        var answer = await AnswerAsync(host, threadId);

        Assert.False(empty!.Accepted);
        Assert.Contains("'html' argument is required", empty.Error);
        Assert.False(oversized!.Accepted);
        Assert.Contains("over the 1024 KB limit", oversized.Error);
        Assert.Empty(await host.Store.ListArtifactsAsync(threadId, default));
        Assert.Empty(answer.GetProperty("artifacts").EnumerateArray());
    }

    [Fact]
    public async Task APageWithNoTitle_IsNamedUntitled_AndAnExplicitTitleWins()
    {
        var (host, _, threadId) = await StartAsync();
        await using var _ = host;
        ArtifactSummary? untitled = null, named = null;
        ChatThat(host, async call =>
        {
            untitled = (await PublishAsync(call.Worker, "<p>no title</p>")).Artifact;
            named = (await PublishAsync(call.Worker, Page, title: "  Chosen\n name ")).Artifact;
        });

        await host.DelegateAsync(threadId, "Draw it.");
        await AnswerAsync(host, threadId);

        Assert.Equal(("Untitled page", "Chosen name"), (untitled!.Title, named!.Title));
    }

    // ---- Only a chat run, with its own secret ----

    [Fact]
    public async Task AWorkRun_CannotPublish_NorAnyoneWithoutTheRunsSecret()
    {
        var (host, _, threadId) = await StartAsync();
        await using var _ = host;
        HttpStatusCode? workRun = null, wrongSecret = null, listWrongSecret = null;
        host.Workers.Script.Enqueue(async call =>
        {
            Assert.NotEqual(TurnKind.Chat, call.Kind);
            using (var response = await call.Worker.HostHttp.PostAsJsonAsync(
                FactoryWire.ArtifactsPath(call.Worker.Launch.RunId), new ArtifactPublishRequest(Page, null, null), FactoryWire.Json))
                workRun = response.StatusCode;

            using var stranger = new HttpClient { BaseAddress = host.Url };
            stranger.DefaultRequestHeaders.Add(FactoryWire.SecretHeader, "not-the-secret");
            using (var response = await stranger.PostAsJsonAsync(
                FactoryWire.ArtifactsPath(call.Worker.Launch.RunId), new ArtifactPublishRequest(Page, null, null), FactoryWire.Json))
                wrongSecret = response.StatusCode;
            using (var response = await stranger.GetAsync(FactoryWire.ArtifactsPath(call.Worker.Launch.RunId)))
                listWrongSecret = response.StatusCode;

            return await host.DefaultTurnAsync(call);
        });

        await host.DelegateAsync(threadId);
        await host.WaitForStateAsync(threadId, Core.Lifecycle.LifecycleState.AwaitingHumanTesting);

        Assert.Equal((HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized), (workRun, wrongSecret, listWrongSecret));
        Assert.Empty(await host.Store.ListArtifactsAsync(threadId, default));
    }

    // ---- Who can open a page ----

    private static async Task<(TestHost Host, Guid ProjectId, Guid ArtifactId)> PublishedAsync()
    {
        var (host, projectId, threadId) = await StartAsync();
        ArtifactSummary? page = null;
        ChatThat(host, async call => page = (await PublishAsync(call.Worker, Page)).Artifact);
        await host.DelegateAsync(threadId, "Draw it.");
        await AnswerAsync(host, threadId);
        return (host, projectId, page!.Id);
    }

    [Fact]
    public async Task APage_IsServedToTheProjectsMembers_AndIsNotFoundForAnyoneElse()
    {
        var (host, projectId, artifactId) = await PublishedAsync();
        await using var _ = host;
        var otherProject = await host.RegisterProjectAsync("billing");
        await host.CreateMemberAsync("ben", projectId);
        await host.CreateMemberAsync("cara", otherProject);
        using var ben = await host.SignedInAsync("ben");
        using var cara = await host.SignedInAsync("cara");
        using var anonymous = host.NewClient();

        using var member = await ben.GetAsync($"api/artifacts/{artifactId}");
        using var outsider = await cara.GetAsync($"api/artifacts/{artifactId}");
        using var signedOut = await anonymous.GetAsync($"api/artifacts/{artifactId}");

        Assert.Equal(HttpStatusCode.OK, member.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, outsider.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, signedOut.StatusCode);
    }

    [Fact]
    public async Task AnUnknownPageOrVersion_IsNotFound()
    {
        var (host, _, artifactId) = await PublishedAsync();
        await using var _ = host;

        using var unknown = await host.Client.GetAsync($"api/artifacts/{Guid.NewGuid()}");
        using var noVersion = await host.Client.GetAsync($"api/artifacts/{artifactId}/v/2");
        using var notAVersion = await host.Client.GetAsync($"api/artifacts/{artifactId}/v/latest");

        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.NotFound), (unknown.StatusCode, noVersion.StatusCode, notAVersion.StatusCode));
    }

    [Fact]
    public async Task ATrayAccessToken_CannotOpenAPage()
    {
        var (host, _, artifactId) = await PublishedAsync();
        await using var _ = host;
        using var created = await host.Client.PostAsJsonAsync("api/access-tokens", new { name = "Tray" });
        var secret = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("secret").GetString()!;
        using var tray = new HttpClient { BaseAddress = host.Url };
        tray.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);

        using var response = await tray.GetAsync($"api/artifacts/{artifactId}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
