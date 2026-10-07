using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Orchestration;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Host.Api;
using Litos.SoftwareFactory.Host.Auth;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Litos.SoftwareFactory.Host.Tests;

public class FactoryMentionTests
{
    [Theory]
    [InlineData("@factory Add CSV export.", "Add CSV export.")]
    [InlineData("  @factory   Add CSV export.  ", "Add CSV export.")]
    [InlineData("@Factory fix it", "fix it")]
    [InlineData("@factory\nAdd CSV export.", "Add CSV export.")]
    public void LeadingMention_Delegates(string text, string expected)
    {
        Assert.True(FactoryMention.TryParse(text, out var request));
        Assert.Equal(expected, request);
    }

    /// <summary>Acceptance scenario 1: only a leading mention in the user's own message assigns work.</summary>
    [Theory]
    [InlineData("Add CSV export.")]
    [InlineData("Could you ask @factory to add CSV export?")]
    [InlineData("> @factory Add CSV export.")]
    [InlineData("```\n@factory Add CSV export.\n```")]
    [InlineData("@factoryAdd CSV export.")]
    [InlineData("@factory")]
    [InlineData("@factory    ")]
    [InlineData("")]
    [InlineData(null)]
    public void AnythingElse_DoesNot(string? text)
    {
        Assert.False(FactoryMention.TryParse(text, out _));
    }
}

/// <summary>Sign-in and the API, over real HTTP, with no coordinator behind it: queued work stays queued.</summary>
public sealed class ApiTests : IAsyncLifetime
{
    private TestHost _host = null!;

    public async Task InitializeAsync() => _host = await TestHost.StartAsync(startCoordinator: false);

    public async Task DisposeAsync() => await _host.DisposeAsync();

    // ---- Sign-in ----

    [Fact]
    public async Task Login_WithTheSeededAdmin_Works_AndMeSaysWhoYouAre()
    {
        var me = await _host.GetAsync("api/auth/me");

        Assert.Equal("admin", me.GetProperty("userName").GetString());
        Assert.Equal("Admin", me.GetProperty("roles")[0].GetString());
    }

    [Fact]
    public async Task Login_WrongPassword_Is401_AndTheSameMessageAsAnUnknownUser()
    {
        using var browser = _host.NewClient();

        using var wrong = await browser.PostAsJsonAsync("api/auth/login", new { userName = "admin", password = "not-the-password" });
        using var unknown = await browser.PostAsJsonAsync("api/auth/login", new { userName = "nobody", password = "whatever-it-is" });

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(await wrong.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Login_RepeatedFailures_LockTheAccount_EvenForTheRightPassword()
    {
        using var browser = _host.NewClient();
        for (var i = 0; i < 5; i++)
            (await browser.PostAsJsonAsync("api/auth/login", new { userName = "admin", password = $"wrong-password-{i}" })).Dispose();

        using var response = await browser.PostAsJsonAsync("api/auth/login", new { userName = "admin", password = TestHost.AdminPassword });

        Assert.Equal(HttpStatusCode.Locked, response.StatusCode);
    }

    [Fact]
    public async Task Api_WithoutSigningIn_Is401_NotARedirect()
    {
        using var browser = _host.NewClient();

        using var response = await browser.GetAsync("api/projects");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Logout_EndsTheSession()
    {
        using var browser = _host.NewClient();
        await _host.SignInAsync(browser);
        (await browser.PostAsJsonAsync("api/auth/logout", new { })).Dispose();

        using var response = await browser.GetAsync("api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>A state-changing request must carry the custom header a cross-site page cannot send.</summary>
    [Fact]
    public async Task StateChangingRequest_WithoutTheCsrfHeader_IsRefused_EvenWhenSignedIn()
    {
        using var browser = _host.NewClient();
        await _host.SignInAsync(browser);
        browser.DefaultRequestHeaders.Remove(FactoryAuth.CsrfHeader);

        using var post = await browser.PostAsJsonAsync("api/projects", new { gitHubUrl = "https://github.com/acme/salesapp" });
        using var get = await browser.GetAsync("api/projects");

        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Empty((await _host.GetAsync("api/projects")).EnumerateArray());
    }

    [Fact]
    public async Task SessionCookie_IsHttpOnlyAndSameSiteStrict()
    {
        using var browser = _host.NewClient();

        using var response = await browser.PostAsJsonAsync("api/auth/login", new { userName = "admin", password = TestHost.AdminPassword });

        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("factory.session="));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Healthz_NeedsNoSignIn()
    {
        using var browser = _host.NewClient();

        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("healthz")).StatusCode);
    }

    [Fact]
    public async Task Settings_ShowOpenRouterAndTheConfiguredModel()
    {
        var settings = await _host.GetAsync("api/settings");

        Assert.Equal("openrouter", settings.GetProperty("provider").GetString());
        Assert.Equal("deepseek/deepseek-v4.1-flash", settings.GetProperty("model").GetString());
        Assert.Equal(["dotnet", "node-react"], settings.GetProperty("presets").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal(["bug", "feature", "refactor", "chore"], settings.GetProperty("taskTypes").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal(300_000, settings.GetProperty("defaultBudget").GetInt64());
        Assert.True(settings.GetProperty("ptcEnabled").GetBoolean());
        Assert.Equal(0.10, settings.GetProperty("cachedInputWeight").GetDouble());
    }

    // ---- Projects ----

    [Fact]
    public async Task RegisterProject_FromAGitHubUrl_StoresTheRepositoryAndThePresetProfile()
    {
        var project = await _host.PostAsync(
            "api/projects", new { gitHubUrl = "https://github.com/nitinmms/filedb-sharp.git", preset = "dotnet", coverageThresholdPercent = 75 }, HttpStatusCode.Created);

        Assert.Equal("filedb-sharp", project.GetProperty("name").GetString());
        Assert.Equal("nitinmms/filedb-sharp", project.GetProperty("gitHub").GetString());
        Assert.Equal("main", project.GetProperty("defaultBranch").GetString());
        Assert.Equal(75, project.GetProperty("coverageThresholdPercent").GetDouble());
        Assert.Single((await _host.GetAsync("api/projects")).EnumerateArray());
    }

    [Theory]
    [InlineData("https://gitlab.com/acme/salesapp", "dotnet", "GitHub repository URL")]
    [InlineData("not a url", "dotnet", "GitHub repository URL")]
    [InlineData("https://github.com/acme/salesapp", "cobol", "Unknown verification preset")]
    public async Task RegisterProject_BadInput_Is400_WithTheReason(string url, string preset, string expected)
    {
        var error = await _host.PostAsync("api/projects", new { gitHubUrl = url, preset }, HttpStatusCode.BadRequest);

        Assert.Contains(expected, error.GetProperty("error").GetString());
    }

    [Fact]
    public async Task RegisterProject_UnusableProfile_Is400()
    {
        var error = await _host.PostAsync(
            "api/projects", new { gitHubUrl = "https://github.com/acme/salesapp", profileJson = """{"profileVersion":2,"steps":[]}""" }, HttpStatusCode.BadRequest);

        Assert.Contains("at least one step", error.GetProperty("error").GetString());
    }

    [Fact]
    public async Task RegisterProject_SameRepositoryTwice_IsAConflict()
    {
        await _host.RegisterProjectAsync();

        await _host.PostAsync("api/projects", new { gitHubUrl = "https://github.com/acme/salesapp" }, HttpStatusCode.Conflict);
    }

    // ---- Threads ----

    [Fact]
    public async Task CreateThread_GetsTheDefaultBudgetModelAndASession_AndStartsAsADraft()
    {
        var projectId = await _host.RegisterProjectAsync();

        var thread = await _host.PostAsync("api/threads", new { projectId, title = "Add CSV export", typeLabel = "feature" }, HttpStatusCode.Created);

        Assert.Equal("Draft", thread.GetProperty("state").GetString());
        Assert.Equal("Discuss", thread.GetProperty("stage").GetString());
        Assert.Equal(300_000, thread.GetProperty("budgetCap").GetInt64());
        Assert.Equal("deepseek/deepseek-v4.1-flash", thread.GetProperty("model").GetString());
        Assert.Equal("strict", thread.GetProperty("budgetPrecision").GetString());

        var stored = await _host.ThreadAsync(thread.GetProperty("id").GetGuid());
        Assert.False(string.IsNullOrEmpty(stored.Thread.SessionId));
        Assert.Equal(await _host.AdminIdAsync(), stored.Thread.OwnerId);
    }

    [Theory]
    [InlineData("", "feature", null, "title is required")]
    [InlineData("Title", "epic", null, "typeLabel must be one of")]
    [InlineData("Title", "bug", -5L, "budgetCap must be positive")]
    public async Task CreateThread_BadInput_Is400(string title, string type, long? cap, string expected)
    {
        var projectId = await _host.RegisterProjectAsync();

        var error = await _host.PostAsync("api/threads", new { projectId, title, typeLabel = type, budgetCap = cap }, HttpStatusCode.BadRequest);

        Assert.Contains(expected, error.GetProperty("error").GetString());
    }

    [Fact]
    public async Task CreateThread_UnknownProject_Is404()
    {
        await _host.PostAsync("api/threads", new { projectId = Guid.NewGuid(), title = "x", typeLabel = "bug" }, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ListThreads_CanFilterByProject_AndGetThreadReturnsItsMessages()
    {
        var a = await _host.RegisterProjectAsync("a");
        var b = await _host.RegisterProjectAsync("b");
        var threadA = await _host.CreateThreadAsync(a);
        await _host.CreateThreadAsync(b);
        await _host.DelegateAsync(threadA);

        Assert.Equal(2, (await _host.GetAsync("api/threads")).GetArrayLength());
        Assert.Equal(1, (await _host.GetAsync($"api/threads?projectId={a}")).GetArrayLength());
        var details = await _host.GetAsync($"api/threads/{threadA}");
        Assert.Equal("Add CSV export for Orders.", details.GetProperty("messages")[0].GetProperty("text").GetString());
        Assert.Equal("Queued", details.GetProperty("thread").GetProperty("state").GetString());
        Assert.Equal("Implement", details.GetProperty("run").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task GetThread_Unknown_Is404()
    {
        using var response = await _host.Client.GetAsync($"api/threads/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- Dispatch ----

    [Fact]
    public async Task Message_StartingWithFactory_QueuesARun_AttributedToTheUser()
    {
        var threadId = await _host.CreateThreadAsync(await _host.RegisterProjectAsync());

        var result = await _host.DelegateAsync(threadId);

        Assert.Equal("Queued", result.GetProperty("outcome").GetString());
        var details = await _host.ThreadAsync(threadId);
        Assert.Equal(LifecycleState.Queued, details.Thread.State);
        Assert.Equal(await _host.AdminIdAsync(), details.LatestRun!.RequestedBy);
        Assert.Equal("Add CSV export for Orders.", details.LatestRun.Request); // the mention itself is not part of the request
    }

    /// <summary>Acceptance scenario 1: a normal chat message never starts work.</summary>
    [Theory]
    [InlineData("Add CSV export for Orders.")]
    [InlineData("Please could @factory add CSV export")]
    public async Task Message_WithoutALeadingMention_Is400_AndStartsNothing(string text)
    {
        var threadId = await _host.CreateThreadAsync(await _host.RegisterProjectAsync());

        await _host.DelegateAsync(threadId, text, expected: HttpStatusCode.BadRequest);

        var details = await _host.ThreadAsync(threadId);
        Assert.Equal(LifecycleState.Draft, details.Thread.State);
        Assert.Null(details.LatestRun);
    }

    [Fact]
    public async Task Message_WithoutAMessageId_Is400()
    {
        var threadId = await _host.CreateThreadAsync(await _host.RegisterProjectAsync());

        await _host.PostAsync($"api/threads/{threadId}/messages", new { messageId = "", text = "@factory go" }, HttpStatusCode.BadRequest);
    }

    /// <summary>Acceptance scenario 19: a double-click or a retry creates one run.</summary>
    [Fact]
    public async Task SameMessageIdTwice_CreatesOneRun()
    {
        var threadId = await _host.CreateThreadAsync(await _host.RegisterProjectAsync());

        var first = await _host.DelegateAsync(threadId, messageId: "client-message-1");
        var second = await _host.DelegateAsync(threadId, messageId: "client-message-1");

        Assert.Equal("Queued", first.GetProperty("outcome").GetString());
        Assert.Equal("Duplicate", second.GetProperty("outcome").GetString());
        Assert.Equal(first.GetProperty("runId").GetGuid(), second.GetProperty("runId").GetGuid());
        Assert.Single((await _host.ThreadAsync(threadId)).Messages);
    }

    [Fact]
    public async Task Message_ToAnUnknownThread_Is404()
    {
        await _host.DelegateAsync(Guid.NewGuid(), expected: HttpStatusCode.NotFound);
    }

    // ---- User actions ----

    [Fact]
    public async Task PauseAQueuedTask_ThenResume()
    {
        var threadId = await _host.CreateThreadAsync(await _host.RegisterProjectAsync());
        await _host.DelegateAsync(threadId);

        var paused = await _host.PostAsync($"api/threads/{threadId}/pause", null, HttpStatusCode.OK);
        var resumed = await _host.PostAsync($"api/threads/{threadId}/resume", null, HttpStatusCode.OK);

        Assert.Equal("PausedUser", paused.GetProperty("state").GetString());
        Assert.Equal("Queued", resumed.GetProperty("state").GetString());
    }

    [Fact]
    public async Task CancelAQueuedTask()
    {
        var threadId = await _host.CreateThreadAsync(await _host.RegisterProjectAsync());
        await _host.DelegateAsync(threadId);

        var cancelled = await _host.PostAsync($"api/threads/{threadId}/cancel", null, HttpStatusCode.OK);

        Assert.Equal("Cancelled", cancelled.GetProperty("state").GetString());
    }

    [Theory]
    [InlineData("accept")]
    [InlineData("resume")]
    [InlineData("pause")]
    public async Task ActionThatIsNotLegalInTheThreadsState_Is409(string action)
    {
        var threadId = await _host.CreateThreadAsync(await _host.RegisterProjectAsync()); // still a draft

        var error = await _host.PostAsync($"api/threads/{threadId}/{action}", null, HttpStatusCode.Conflict);

        Assert.False(string.IsNullOrEmpty(error.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task Action_OnAnUnknownThread_Is404()
    {
        await _host.PostAsync($"api/threads/{Guid.NewGuid()}/accept", null, HttpStatusCode.NotFound);
        await _host.PostAsync($"api/threads/{Guid.NewGuid()}/cancel", null, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Budget_CanBeChanged_AndMustBePositive()
    {
        var threadId = await _host.CreateThreadAsync(await _host.RegisterProjectAsync());

        var changed = await _host.PostAsync($"api/threads/{threadId}/budget", new { cap = 600_000 }, HttpStatusCode.OK);
        await _host.PostAsync($"api/threads/{threadId}/budget", new { cap = 0 }, HttpStatusCode.BadRequest);

        Assert.Equal(600_000, changed.GetProperty("budgetCap").GetInt64());
    }

    [Fact]
    /// <summary>An id nobody may see is not found whatever the request says; an empty answer to a
    /// real decision is a 400 (RunTests).</summary>
    public async Task AnswerDecision_Unknown_Is404_WhateverTheAnswer()
    {
        await _host.PostAsync($"api/decisions/{Guid.NewGuid()}/answer", new { answer = "yes" }, HttpStatusCode.NotFound);
        await _host.PostAsync($"api/decisions/{Guid.NewGuid()}/answer", new { answer = " " }, HttpStatusCode.NotFound);
    }

    // ---- Events ----

    private static async Task<List<(long Id, string Type, JsonElement Data)>> ReadEventsAsync(HttpResponseMessage response, int count, TimeSpan timeout)
    {
        var events = new List<(long, string, JsonElement)>();
        using var cts = new CancellationTokenSource(timeout);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token));
        long id = 0;
        var type = "";
        while (events.Count < count && await reader.ReadLineAsync(cts.Token) is { } line)
        {
            if (line.StartsWith("id: "))
                id = long.Parse(line[4..]);
            else if (line.StartsWith("event: "))
                type = line[7..];
            else if (line.StartsWith("data: "))
                events.Add((id, type, JsonDocument.Parse(line[6..]).RootElement.Clone()));
        }

        return events;
    }

    [Fact]
    public async Task Events_ReplayEverything_WithDurableIds()
    {
        var threadId = await _host.CreateThreadAsync(await _host.RegisterProjectAsync());
        await _host.DelegateAsync(threadId);

        using var response = await _host.Client.GetAsync($"api/threads/{threadId}/events", HttpCompletionOption.ResponseHeadersRead);
        var events = await ReadEventsAsync(response, 3, TimeSpan.FromSeconds(10));

        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        // The thread's creation, then the delegation's message and state.
        Assert.Equal(["state", "message", "state"], events.Select(e => e.Type));
        Assert.True(events[0].Id < events[1].Id && events[1].Id < events[2].Id);
        Assert.Equal("Draft", events[0].Data.GetProperty("state").GetString());
        Assert.Equal("Add CSV export for Orders.", events[1].Data.GetProperty("text").GetString());
        Assert.Equal("Queued", events[2].Data.GetProperty("state").GetString());
    }

    /// <summary>§12: a reconnecting client replays everything after its last seen event.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Events_ResumeAfterTheLastSeenEvent_ByHeaderOrByQuery(bool useHeader)
    {
        var threadId = await _host.CreateThreadAsync(await _host.RegisterProjectAsync());
        await _host.DelegateAsync(threadId);
        var all = await _host.Store.ReadEventsAsync(threadId, 0, 100, default);
        var lastSeen = all[0].Sequence;

        using var request = new HttpRequestMessage(HttpMethod.Get, useHeader ? $"api/threads/{threadId}/events" : $"api/threads/{threadId}/events?after={lastSeen}");
        if (useHeader)
            request.Headers.Add("Last-Event-ID", lastSeen.ToString());
        using var response = await _host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        var events = await ReadEventsAsync(response, 1, TimeSpan.FromSeconds(10));

        Assert.Equal(all[1].Sequence, Assert.Single(events).Id);
    }

    [Fact]
    public async Task Events_ArriveLive_WhileTheStreamIsOpen()
    {
        var threadId = await _host.CreateThreadAsync(await _host.RegisterProjectAsync());
        using var response = await _host.Client.GetAsync($"api/threads/{threadId}/events", HttpCompletionOption.ResponseHeadersRead);
        var reading = ReadEventsAsync(response, 3, TimeSpan.FromSeconds(15));

        await Task.Delay(300); // the stream is open and idle
        await _host.DelegateAsync(threadId);

        // The creation was replayed when the stream opened; the rest arrived live.
        Assert.Equal(["state", "message", "state"], (await reading).Select(e => e.Type));
    }

    /// <summary>A browser that reconnects by itself repeats the URL it first opened, so its
    /// ?after= is stale and its header is current. The later one wins.</summary>
    [Theory]
    [InlineData(null, null, 0)]
    [InlineData(5L, null, 5)]
    [InlineData(null, "7", 7)]
    [InlineData(5L, "9", 9)]
    [InlineData(9L, "5", 9)]
    [InlineData(5L, "not a number", 5)]
    public void StartCursor_IsTheLaterOfTheQueryAndTheHeader(long? after, string? lastEventId, long expected) =>
        Assert.Equal(expected, EventStream.StartCursor(after, lastEventId));

    [Fact]
    public async Task Events_ReconnectWithAStaleQuery_ResumeFromTheHeader()
    {
        var threadId = await _host.CreateThreadAsync(await _host.RegisterProjectAsync());
        await _host.DelegateAsync(threadId);
        var all = await _host.Store.ReadEventsAsync(threadId, 0, 100, default);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/threads/{threadId}/events?after=0");
        request.Headers.Add("Last-Event-ID", all[0].Sequence.ToString());
        using var response = await _host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        var events = await ReadEventsAsync(response, 1, TimeSpan.FromSeconds(10));

        Assert.Equal(all[1].Sequence, Assert.Single(events).Id);
    }

    /// <summary>The thread snapshot says where to start listening, so a client that has just
    /// loaded a thread hears what happens next without replaying its whole history.</summary>
    [Fact]
    public async Task GetThread_CarriesTheEventCursor_AndListeningFromItHearsOnlyWhatFollows()
    {
        var threadId = await _host.CreateThreadAsync(await _host.RegisterProjectAsync());
        // A new thread has one event: its creation.
        var created = (await _host.GetAsync($"api/threads/{threadId}")).GetProperty("eventCursor").GetInt64();
        Assert.Equal(Assert.Single(await _host.Store.ReadEventsAsync(threadId, 0, 100, default)).Sequence, created);

        await _host.DelegateAsync(threadId);
        var all = await _host.Store.ReadEventsAsync(threadId, 0, 100, default);
        var cursor = (await _host.GetAsync($"api/threads/{threadId}")).GetProperty("eventCursor").GetInt64();
        Assert.Equal(all[^1].Sequence, cursor);
        Assert.Equal(cursor, await _host.Store.LastEventSequenceAsync(threadId, default));

        using var response = await _host.Client.GetAsync($"api/threads/{threadId}/events?after={cursor}", HttpCompletionOption.ResponseHeadersRead);
        var reading = ReadEventsAsync(response, 1, TimeSpan.FromSeconds(15));
        await Task.Delay(300);
        await _host.PostAsync($"api/threads/{threadId}/pause", null, HttpStatusCode.OK);

        var heard = Assert.Single(await reading);
        Assert.True(heard.Id > cursor);
    }

    [Fact]
    public async Task LastEventSequence_IsPerThread()
    {
        var projectId = await _host.RegisterProjectAsync();
        var busy = await _host.CreateThreadAsync(projectId);
        var quiet = await _host.CreateThreadAsync(projectId, "Another task");
        var quietBefore = await _host.Store.LastEventSequenceAsync(quiet, default);
        var busyBefore = await _host.Store.LastEventSequenceAsync(busy, default);
        await _host.DelegateAsync(busy);

        Assert.True(await _host.Store.LastEventSequenceAsync(busy, default) > busyBefore);
        Assert.Equal(quietBefore, await _host.Store.LastEventSequenceAsync(quiet, default));
        Assert.Equal(0, await _host.Store.LastEventSequenceAsync(Guid.NewGuid(), default));
    }

    // ---- Usage ----

    [Fact]
    public async Task Usage_ListsTheThreadsModelCalls_WithWhatWasReservedAndCharged()
    {
        var threadId = await _host.CreateThreadAsync(await _host.RegisterProjectAsync(), budgetCap: 50_000);
        Assert.Empty((await _host.GetAsync($"api/threads/{threadId}/usage")).EnumerateArray());

        await _host.DelegateAsync(threadId);
        var claimed = (await _host.Store.ClaimNextRunAsync(1, DateTimeOffset.UtcNow, default))!;
        var run = new Runs.ActiveRun(claimed.Run.Id, threadId, claimed.Run.RequestedBy, _host.Options.Provider, _host.Options.Model);
        _host.Provider.EnqueueReply("pong", new Litos.Agent.Streaming.UsageInfo(40, 10));
        await _host.App.Services.GetRequiredService<Gateway.ModelGateway>().HandleAsync(
            run,
            new Litos.SoftwareFactory.Contracts.GatewayRequest("call-1", new Litos.Agent.Providers.ChatRequest(
                [Litos.Agent.Messages.ChatMessage.User("ping")], [], "ignored", SessionId: "s")),
            _ => Task.CompletedTask, default);

        var call = Assert.Single((await _host.GetAsync($"api/threads/{threadId}/usage")).EnumerateArray());
        Assert.Equal("Settled", call.GetProperty("status").GetString());
        Assert.Equal(40, call.GetProperty("actualInput").GetInt64());
        Assert.Equal(10, call.GetProperty("actualOutput").GetInt64());
        Assert.Equal(50, call.GetProperty("charged").GetInt64());
        Assert.True(call.GetProperty("reserved").GetInt64() >= 50);
        Assert.Equal("deepseek/deepseek-v4.1-flash", call.GetProperty("model").GetString());
        // The request key is the gateway's idempotency key, not something a browser needs.
        Assert.False(call.TryGetProperty("requestKey", out _));
    }

    [Fact]
    public async Task Usage_NeedsSignIn_AndAnExistingThread()
    {
        using var anonymous = _host.NewClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"api/threads/{Guid.NewGuid()}/usage")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _host.Client.GetAsync($"api/threads/{Guid.NewGuid()}/usage")).StatusCode);
    }

    [Fact]
    public async Task Events_NeedSignIn_AndAnExistingThread()
    {
        using var anonymous = _host.NewClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"api/threads/{Guid.NewGuid()}/events")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _host.Client.GetAsync($"api/threads/{Guid.NewGuid()}/events")).StatusCode);
    }

    [Fact]
    public void EventFormat_IsOneSseFrame_WithTheSequenceAsItsId()
    {
        var frame = EventStream.Format(new OutboxEvent { Sequence = 42, Type = "state", PayloadJson = "{\"state\":\n\"Queued\"}" });

        Assert.Equal("id: 42\nevent: state\ndata: {\"state\": \"Queued\"}\n\n", frame);
    }

    // ---- The worker's callbacks are not the browser's API ----

    [Theory]
    [InlineData("submissions")]
    [InlineData("gateway")]
    [InlineData("ready")]
    public async Task WorkerCallback_ForARunThatIsNotActive_Is401_EvenForASignedInAdmin(string endpoint)
    {
        using var response = await _host.Client.PostAsJsonAsync($"internal/runs/{Guid.NewGuid():N}/{endpoint}", new { });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

public class FactoryOptionsTests
{
    private static FactoryOptions Valid() => new()
    {
        ConnectionString = "Host=127.0.0.1", DataDirectory = "data", OpenRouterApiKey = "key",
    };

    [Fact]
    public void Validate_CompleteOptions_HaveNoProblems()
    {
        Assert.Empty(Valid().Validate());
    }

    [Fact]
    public void Defaults_AreOpenRouter_DeepSeekFlash_AndTwoSlots()
    {
        var options = new FactoryOptions();

        Assert.Equal("openrouter", options.Provider);
        Assert.Equal("deepseek/deepseek-v4.1-flash", options.Model);
        Assert.Equal(2, options.SlotCap);
        Assert.Equal(2, options.Limits.MaxRepairCycles);
    }

    private static FactoryOptions From(params (string Key, string Value)[] settings) => FactoryOptions.From(
        new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value))
            .Build());

    [Fact]
    public void From_NothingSet_KeepsTheDefaultBudgetAndOutputAllowance()
    {
        var options = From();

        Assert.Equal(300_000, options.DefaultBudget);
        Assert.Equal(32_768, options.Budget.OutputAllowanceTokens);
        Assert.Equal(0.10, options.Budget.Margin);
    }

    [Fact]
    public void From_ReadsTheOutputAllowanceAndTheDefaultBudget()
    {
        var options = From(("FACTORY_OUTPUT_ALLOWANCE", "16000"), ("FACTORY_DEFAULT_BUDGET", "1000000"));

        Assert.Equal(16_000, options.Budget.OutputAllowanceTokens);
        Assert.Equal(0.10, options.Budget.Margin);
        Assert.Equal(1_000_000, options.DefaultBudget);
    }

    [Theory]
    [InlineData("1", 1.0)]
    [InlineData("0", 0.0)]
    [InlineData("0.25", 0.25)]
    public void From_ReadsTheCachedInputWeight(string value, double expected) =>
        Assert.Equal(expected, From(("FACTORY_CACHED_INPUT_WEIGHT", value)).Budget.CachedInputWeight);

    [Theory]
    [InlineData("1.5")]
    [InlineData("-0.1")]
    [InlineData("a tenth")]
    public void From_AnUnusableCachedInputWeight_KeepsTheDefault(string value) =>
        Assert.Equal(0.10, From(("FACTORY_CACHED_INPUT_WEIGHT", value)).Budget.CachedInputWeight);

    [Theory]
    [InlineData("0.25", 0.25)]
    [InlineData("1", 1.0)]
    [InlineData("0", 0.0)]
    [InlineData("-1", 0.5)]
    [InlineData("half", 0.5)]
    [InlineData("", 0.5)]
    public void From_ReadsTheReworkTopUp_OrKeepsHalf(string value, double expected) =>
        Assert.Equal(expected, From(("FACTORY_REWORK_TOP_UP", value)).Budget.ReworkTopUpShare);

    [Fact]
    public void From_NothingSet_CountsCachedInputAtATenth() =>
        Assert.Equal(0.10, From().Budget.CachedInputWeight);

    [Theory]
    [InlineData("none")]
    [InlineData("NONE")]
    public void From_DefaultBudgetNone_MeansNoCap(string value) =>
        Assert.Null(From(("FACTORY_DEFAULT_BUDGET", value)).DefaultBudget);

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("lots")]
    public void From_UnusableNumbers_AreIgnored(string value)
    {
        var options = From(("FACTORY_OUTPUT_ALLOWANCE", value), ("FACTORY_DEFAULT_BUDGET", value));

        Assert.Equal(32_768, options.Budget.OutputAllowanceTokens);
        Assert.Equal(300_000, options.DefaultBudget);
    }

    [Fact]
    public void From_NothingSet_KeepsTwoSlotsOneVerificationAndAHalfMinuteSweep()
    {
        var options = From();

        Assert.Equal(2, options.SlotCap);
        Assert.Equal(1, options.VerifyConcurrency);
        Assert.Equal(TimeSpan.FromSeconds(30), options.LivenessInterval);
    }

    [Fact]
    public void From_ReadsTheSlotCapAndTheVerifyConcurrency()
    {
        var options = From(("FACTORY_SLOT_CAP", "3"), ("FACTORY_VERIFY_CONCURRENCY", "2"));

        Assert.Equal(3, options.SlotCap);
        Assert.Equal(2, options.VerifyConcurrency);
    }

    [Theory]
    [InlineData("many")]
    [InlineData("")]
    [InlineData("2.5")]
    public void From_ASlotCapThatIsNotANumber_IsIgnored(string value)
    {
        var options = From(("FACTORY_SLOT_CAP", value), ("FACTORY_VERIFY_CONCURRENCY", value));

        Assert.Equal(2, options.SlotCap);
        Assert.Equal(1, options.VerifyConcurrency);
    }

    /// <summary>A cap of 0 would queue every task for ever, so it is refused rather than ignored.</summary>
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void From_ASlotCapBelowOne_IsRefusedAtStart(string value)
    {
        var options = From(
            ("ConnectionStrings:FactoryState", "Host=127.0.0.1"), ("FACTORY_DATA_DIR", "data"), ("OPENROUTER_API_KEY", "key"),
            ("FACTORY_SLOT_CAP", value), ("FACTORY_VERIFY_CONCURRENCY", value));

        var problems = options.Validate();

        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, p => p.Contains("FACTORY_SLOT_CAP"));
        Assert.Contains(problems, p => p.Contains("FACTORY_VERIFY_CONCURRENCY"));
    }

    [Fact]
    public void Validate_RefusesANonPositiveLivenessInterval()
    {
        var options = Valid();
        options.LivenessInterval = TimeSpan.Zero;

        Assert.Contains(options.Validate(), p => p.Contains("liveness interval"));
    }

    [Fact]
    public void RunTempDirectory_IsInsideTheRunsOwnDirectory()
    {
        var options = Valid();
        var runId = Guid.NewGuid();

        Assert.Equal(Path.Combine(options.RunDirectory(runId), "tmp"), options.RunTempDirectory(runId));
        Assert.NotEqual(options.RunTempDirectory(runId), options.RunTempDirectory(Guid.NewGuid()));
    }

    [Fact]
    public void Validate_ReportsEverythingMissing()
    {
        var problems = new FactoryOptions().Validate();

        Assert.Contains(problems, p => p.Contains("ConnectionStrings__FactoryState"));
        Assert.Contains(problems, p => p.Contains("FACTORY_DATA_DIR"));
        Assert.Contains(problems, p => p.Contains("OPENROUTER_API_KEY"));
    }

    /// <summary>M1 runs on OpenRouter only.</summary>
    [Fact]
    public void Validate_AnyOtherProvider_IsRefused()
    {
        var options = Valid();
        options.Provider = "anthropic";

        Assert.Contains(options.Validate(), p => p.Contains("'openrouter' provider only"));
    }

    [Fact]
    public void EnvFile_ReadsKeyValueLines_MapsDoubleUnderscore_AndSkipsComments()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "# comment\n\nFACTORY_DATA_DIR=C:/data\nConnectionStrings__FactoryState=Host=127.0.0.1;Password=a=b\nQUOTED=\"with spaces\"\nnot a pair\n");

            var values = EnvFile.Read(path);

            Assert.Equal("C:/data", values["FACTORY_DATA_DIR"]);
            Assert.Equal("Host=127.0.0.1;Password=a=b", values["ConnectionStrings:FactoryState"]);
            Assert.Equal("with spaces", values["QUOTED"]);
            Assert.Equal(3, values.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RunDirectory_IsUnderTheDataDirectory()
    {
        var options = Valid();
        var runId = Guid.NewGuid();

        Assert.Equal(Path.Combine("data", "runs", runId.ToString("N")), options.RunDirectory(runId));
        Assert.Equal(Path.Combine("data", "workspaces"), options.WorkspacesDirectory);
    }
}

public class WorkerLocatorTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("litos-factory-locator-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Touch(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    [Fact]
    public void ConfiguredExecutable_IsUsedAsIs()
    {
        var command = WorkerLocator.Resolve(Path.Combine(_root, "custom-worker.exe"));

        Assert.Equal(Path.Combine(_root, "custom-worker.exe"), command.Executable);
        Assert.Empty(command.PrefixArguments);
    }

    [Fact]
    public void ConfiguredDll_IsRunThroughDotnet()
    {
        var command = WorkerLocator.Resolve(Path.Combine(_root, "Litos.SoftwareFactory.Worker.dll"));

        Assert.Equal("dotnet", command.Executable);
        Assert.Equal([Path.Combine(_root, "Litos.SoftwareFactory.Worker.dll")], command.PrefixArguments);
    }

    /// <summary>The worker is built beside these tests, which is how the end-to-end test finds it.</summary>
    [Fact]
    public void NothingConfigured_FindsTheWorkerBesideTheHost()
    {
        var command = WorkerLocator.Resolve(null);

        Assert.True(
            File.Exists(command.Executable) || command.PrefixArguments.Any(File.Exists),
            $"The worker was not found: {command.Executable} {string.Join(' ', command.PrefixArguments)}");
    }

    [Fact]
    public void RunningFromSource_FindsTheWorkersOwnBuildOutput_ForTheSameConfiguration()
    {
        var worker = Touch("src", "Litos.SoftwareFactory.Worker", "bin", "Release", "net10.0", "Litos.SoftwareFactory.Worker.dll");
        Touch("src", "Litos.SoftwareFactory.Worker", "bin", "Debug", "net10.0", "Litos.SoftwareFactory.Worker.dll");
        var hostOutput = Path.Combine(_root, "src", "Litos.SoftwareFactory.Host", "bin", "Release", "net10.0") + Path.DirectorySeparatorChar;

        Assert.Equal(worker, WorkerLocator.FindInSourceTree(hostOutput));
    }

    [Fact]
    public void RunningFromSource_WorkerNotBuilt_OrNotASourceLayout_FindsNothing()
    {
        Assert.Null(WorkerLocator.FindInSourceTree(Path.Combine(_root, "src", "Litos.SoftwareFactory.Host", "bin", "Debug", "net10.0")));
        Assert.Null(WorkerLocator.FindInSourceTree(Path.Combine(_root, "published")));
    }
}

public class HandoffComposerTests
{
    private static HandoffEvidence Evidence(RunState? state = null, HandoffFacts? facts = null) => HandoffComposer.Evidence(
        state ?? RunOrchestrator.NewRun(RunKind.Implement) with
        {
            LastVerification = ScriptedVerifier.Passing("Existing.Test", "New.A", "New.B"),
            Baseline = ScriptedVerifier.Passing("Existing.Test"),
            LastSubmission = FakeWorkerLauncher.Work(),
            Review = Core.Verification.ReviewStatus.Clean,
            ReviewCompleted = true,
        },
        facts ?? new HandoffFacts("factory/7f3a-csv-export", "1c9e2b4d5e6f", 212, "https://github.com/acme/salesapp/pull/212") { CoverageThresholdPercent = 80 });

    /// <summary>The blueprint's example handoff (§4), from the host's evidence alone.</summary>
    [Fact]
    public void Text_ReadsLikeTheBlueprintsExample()
    {
        var text = HandoffComposer.Text(Evidence());

        Assert.Equal(
            "Ready for human testing. Branch factory/7f3a-csv-export pushed (commit 1c9e2b4), draft PR #212. Build passed. " +
            "3 unit tests passed (2 new). Changed-line coverage 90% (threshold 80%). Agent review: clean. " +
            "Please check: Sign in as an administrator and export.",
            text);
    }

    [Fact]
    public void Text_SaysAChangeWasNotReviewed_WhenItNeededNoReview()
    {
        var state = RunOrchestrator.NewRun(RunKind.Implement) with
        {
            LastVerification = ScriptedVerifier.Passing("Existing.Test", "New.A"),
            Baseline = ScriptedVerifier.Passing("Existing.Test"),
            LastSubmission = FakeWorkerLauncher.Work(),
            Review = Core.Verification.ReviewStatus.NotNeeded,
            ReviewCompleted = true,
        };

        var text = HandoffComposer.Text(Evidence(state));

        Assert.Contains("Agent review: not needed for a change this small with no risk signals; the factory's own verification passed.", text);
        Assert.DoesNotContain("Agent review: clean", text);
    }

    [Fact]
    public void Text_NeverLabelsSomethingAsPassingThatDidNotRun()
    {
        var state = RunOrchestrator.NewRun(RunKind.Implement) with
        {
            LastVerification = new Core.Verification.VerificationOutcome(
                Core.Verification.BuildStatus.NotApplicable, Core.Verification.UnitTestStatus.NoTests, Core.Verification.CoverageStatus.NotMeasured, [], null, [], []),
        };

        var text = HandoffComposer.Text(Evidence(state, new HandoffFacts("factory/x", null, null, null)));

        Assert.Contains("Build: not applicable.", text);
        Assert.Contains("Unit tests: no tests.", text);
        Assert.Contains("Changed-line coverage: not measured.", text);
        Assert.Contains("Agent review: not run.", text);
        Assert.DoesNotContain("passed", text);
        Assert.Contains("Application testing is yours.", text);
    }

    [Fact]
    public void Text_DisclosesLimitationsFindingsAndPreExistingFailures()
    {
        var state = RunOrchestrator.NewRun(RunKind.Implement) with
        {
            LastVerification = ScriptedVerifier.Failing("Legacy.Broken"),
            Baseline = ScriptedVerifier.Failing("Legacy.Broken"),
            Review = Core.Verification.ReviewStatus.FindingsFixed,
            Findings =
            [
                new Contracts.ReviewFinding(Contracts.FindingSeverity.Blocking, "a.cs", 1, "Crash."),
                new Contracts.ReviewFinding(Contracts.FindingSeverity.Minor, "a.cs", 2, "Nit."),
            ],
            Disclosures = ["Changed-line coverage is below the project threshold"],
        };

        var text = HandoffComposer.Text(Evidence(state, new HandoffFacts("factory/x", "abc", null, null) { Notes = ["The draft PR could not be opened: forbidden."] }));

        Assert.Contains("1 failing test was already failing before this task.", text);
        Assert.Contains("Agent review: 1 finding fixed, 1 minor finding open.", text);
        Assert.Contains("Known limitations: Changed-line coverage is below the project threshold. The draft PR could not be opened: forbidden.", text);
        Assert.DoesNotContain("draft PR #", text);
    }

    [Fact]
    public void Evidence_TakesFiguresFromTheHostsRecords_AndDescriptionsFromTheSubmission()
    {
        var evidence = Evidence();

        Assert.Equal((3, 0, 2), (evidence.TestsPassed, evidence.TestsFailed, evidence.NewTests));
        Assert.Equal(90, evidence.ChangedLineCoveragePercent);
        Assert.Equal("Added CSV export for Orders.", evidence.Summary);
        Assert.Equal(["Export_Admin_Succeeds"], evidence.TestsAdded);
        Assert.Equal("dotnet test — ok in 2s", Assert.Single(evidence.Commands));

        var json = JsonSerializer.Serialize(evidence, Contracts.FactoryWire.Json);
        Assert.Equal(evidence.Summary, JsonSerializer.Deserialize<HandoffEvidence>(json, Contracts.FactoryWire.Json)!.Summary);
    }
}
