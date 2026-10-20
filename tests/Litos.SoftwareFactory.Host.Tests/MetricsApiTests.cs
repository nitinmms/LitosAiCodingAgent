using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Host.Runs;
using Microsoft.Extensions.DependencyInjection;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>The flow metrics through the API (m4-architecture.md §6): everyone, over the projects they belong to.</summary>
public sealed class MetricsApiTests : IAsyncLifetime
{
    private TestHost _host = null!;

    public async Task InitializeAsync() => _host = await TestHost.StartAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private async Task<(Guid ProjectId, Guid ThreadId)> AcceptedAsync(string repository = "salesapp")
    {
        var projectId = await _host.RegisterProjectAsync(repository);
        var threadId = await _host.CreateThreadAsync(projectId);
        await _host.DelegateAsync(threadId);
        await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);
        await _host.PostAsync($"api/threads/{threadId}/accept", null, HttpStatusCode.OK);
        return (projectId, threadId);
    }

    [Fact]
    public async Task TheMetrics_CountTasksAndAcceptance_AndTheTimeEachSpentInEachStateAndStage()
    {
        var (projectId, _) = await AcceptedAsync();

        var metrics = await _host.GetAsync($"api/metrics?projectId={projectId}&days=7");

        Assert.Equal((1, 1, 0, 7), (metrics.GetProperty("tasks").GetInt32(), metrics.GetProperty("accepted").GetInt32(),
            metrics.GetProperty("open").GetInt32(), metrics.GetProperty("days").GetInt32()));
        Assert.Equal(1.0, metrics.GetProperty("firstHandoffAcceptance").GetDouble());
        var states = metrics.GetProperty("timeInState").EnumerateArray().Select(s => s.GetProperty("name").GetString()).ToList();
        Assert.Contains("Running", states);
        Assert.Contains("Implement", metrics.GetProperty("timeInStage").EnumerateArray().Select(s => s.GetProperty("name").GetString()));
        Assert.Equal(0, metrics.GetProperty("openDecisions").GetInt32());
        Assert.Equal(JsonValueKind.Null, metrics.GetProperty("oldestOpenDecision").ValueKind);
        Assert.Equal(0, metrics.GetProperty("lessons").GetProperty("included").GetInt32());
    }

    [Fact]
    public async Task AMember_SeesTheirProjectsOnly_AndAnotherProjectsMetricsAreNotFound()
    {
        var (mine, _) = await AcceptedAsync();
        var (other, _) = await AcceptedAsync("billing");
        await _host.CreateMemberAsync("maria", mine);
        var maria = await _host.SignedInAsync("maria");

        using var all = await maria.GetAsync("api/metrics");
        using var theirs = await maria.GetAsync($"api/metrics?projectId={other}");

        Assert.Equal(1, JsonDocument.Parse(await all.Content.ReadAsStringAsync()).RootElement.GetProperty("tasks").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, theirs.StatusCode);
        Assert.Equal(2, (await _host.GetAsync("api/metrics")).GetProperty("tasks").GetInt32()); // an Admin sees every project
    }

    [Fact]
    public async Task OnlyTheOfferedPeriods_AreAccepted()
    {
        using var refused = await _host.Client.GetAsync("api/metrics?days=12");
        using var refusedLessons = await _host.Client.GetAsync("api/metrics/lessons?days=12");

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, refusedLessons.StatusCode);
        Assert.Equal(30, (await _host.GetAsync("api/metrics")).GetProperty("days").GetInt32());
    }

    [Fact]
    public async Task TheMetrics_ListEachTask_AndATrendOverThePeriod()
    {
        var (projectId, threadId) = await AcceptedAsync();

        var metrics = await _host.GetAsync($"api/metrics?projectId={projectId}&days=7");

        var task = Assert.Single(metrics.GetProperty("perTask").EnumerateArray());
        Assert.Equal((threadId, projectId, "Accepted", 0), (task.GetProperty("threadId").GetGuid(), task.GetProperty("projectId").GetGuid(),
            task.GetProperty("state").GetString(), task.GetProperty("reworkRounds").GetInt32()));
        Assert.Equal(JsonValueKind.Number, task.GetProperty("tokens").ValueKind); // the fake worker makes no model call
        var series = metrics.GetProperty("series").EnumerateArray().ToList();
        Assert.Equal(7, series.Count);
        Assert.Equal(1, series[^1].GetProperty("created").GetInt32());
        Assert.Equal((1, 1), (series[^1].GetProperty("accepted").GetInt32(), series[^1].GetProperty("acceptedFirstTime").GetInt32()));
    }

    // ---- The lesson report (m4-architecture.md §12) ----

    [Fact]
    public async Task TheLessonReport_ScoresEachLesson_FromTheRunsGivenItAndThePeoplesVerdicts()
    {
        var projectId = await _host.RegisterProjectAsync();
        var given = (await _host.PostAsync($"api/projects/{projectId}/lessons", new { rule = "Quote every CSV value.", topics = new[] { "csv" }, approve = true },
            HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await _host.PostAsync($"api/projects/{projectId}/lessons", new { rule = "Dates are UTC.", topics = new[] { "dates" }, approve = true }, HttpStatusCode.Created);
        var threadId = await _host.CreateThreadAsync(projectId);
        await _host.DelegateAsync(threadId);
        await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);
        var runId = (await _host.ThreadAsync(threadId)).LatestRun!.Id;
        await _host.PostAsync($"api/runs/{runId}/lessons/{given}/verdict", new { verdict = "Helped" }, HttpStatusCode.OK);

        var report = await _host.GetAsync($"api/metrics/lessons?projectId={projectId}&days=7");

        Assert.Equal((1, 1, 1, 0, 7), (report.GetProperty("given").GetInt32(), report.GetProperty("judged").GetInt32(),
            report.GetProperty("helped").GetInt32(), report.GetProperty("misled").GetInt32(), report.GetProperty("days").GetInt32()));
        var lessons = report.GetProperty("lessons").EnumerateArray().ToList();
        Assert.Equal(2, lessons.Count);
        var csv = lessons.Single(l => l.GetProperty("lessonId").GetGuid() == given);
        Assert.Equal((1, 1, "TooFewVerdicts", "Manual", true), (csv.GetProperty("given").GetInt32(), csv.GetProperty("helped").GetInt32(),
            csv.GetProperty("standing").GetString(), csv.GetProperty("source").GetString(), csv.GetProperty("usable").GetBoolean()));
        Assert.Equal(1.0, csv.GetProperty("net").GetDouble());
        Assert.Equal(JsonValueKind.Null, csv.GetProperty("firstHandoff").ValueKind);
        Assert.Equal("New", lessons.Single(l => l.GetProperty("lessonId").GetGuid() != given).GetProperty("standing").GetString());
        Assert.Equal(1, report.GetProperty("byRank")[0].GetProperty("helped").GetInt32());
        Assert.Equal(2, report.GetProperty("proposals").GetProperty("writtenByPeople").GetInt32());
        Assert.Equal("Manual", Assert.Single(report.GetProperty("bySource").EnumerateArray()).GetProperty("source").GetString());
        Assert.Empty(report.GetProperty("improvements").EnumerateArray());
    }

    // ---- The skill report (m4-architecture.md §12.5) ----

    /// <summary>The task's latest run, active again, as its worker would be while it calls back.</summary>
    private async Task<ActiveRun> ActiveAsync(Guid threadId)
    {
        var active = new ActiveRun((await _host.ThreadAsync(threadId)).LatestRun!.Id, threadId, Guid.NewGuid(), "openrouter", "m");
        Assert.True(_host.App.Services.GetRequiredService<RunRegistry>().TryAdd(active));
        return active;
    }

    /// <summary>Calls back as a worker of the run would: the run's id and a secret only it holds.</summary>
    private async Task<HttpResponseMessage> TellLoadAsync(ActiveRun active, string name, bool rightSecret = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, FactoryWire.SkillLoadsPath(active.RunId.ToString("N")))
        {
            Content = JsonContent.Create(new SkillLoadReport("session-1", name), options: FactoryWire.Json),
        };
        request.Headers.Add(FactoryWire.SecretHeader, rightSecret ? active.Secret : "not-the-secret");
        return await _host.NewClient().SendAsync(request);
    }

    [Fact]
    public async Task ASkillAWorkerLoaded_IsCountedInTheSkillReport()
    {
        var (projectId, threadId) = await AcceptedAsync();
        var active = await ActiveAsync(threadId);

        using (var told = await TellLoadAsync(active, "ef-migrations"))
            Assert.Equal(HttpStatusCode.OK, told.StatusCode);
        using (var refused = await TellLoadAsync(active, "ef-migrations", rightSecret: false))
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        using (var empty = await TellLoadAsync(active, " "))
            Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        var report = await _host.GetAsync($"api/metrics/skills?projectId={projectId}&days=7");

        Assert.Equal((1, 1, 7, 8), (report.GetProperty("runs").GetInt32(), report.GetProperty("loads").GetInt32(), report.GetProperty("days").GetInt32(),
            report.GetProperty("minTasksToCompare").GetInt32()));
        var skill = Assert.Single(report.GetProperty("skills").EnumerateArray());
        Assert.Equal(("ef-migrations", "Library", 1, 1, "TooFewTasks"), (skill.GetProperty("name").GetString(), skill.GetProperty("origin").GetString(),
            skill.GetProperty("runsLoaded").GetInt32(), skill.GetProperty("loads").GetInt32(), skill.GetProperty("standing").GetString()));
        Assert.Empty(report.GetProperty("improvements").EnumerateArray());
    }

    [Fact]
    public async Task AMember_SeesTheSkillReportOfTheirProjectsOnly()
    {
        var (mine, mineThread) = await AcceptedAsync();
        var (other, otherThread) = await AcceptedAsync("billing");
        (await TellLoadAsync(await ActiveAsync(mineThread), "mine")).Dispose();
        (await TellLoadAsync(await ActiveAsync(otherThread), "theirs")).Dispose();
        await _host.CreateMemberAsync("maria", mine);
        var maria = await _host.SignedInAsync("maria");

        using var all = await maria.GetAsync("api/metrics/skills");
        using var theirs = await maria.GetAsync($"api/metrics/skills?projectId={other}");
        using var refused = await _host.Client.GetAsync("api/metrics/skills?days=12");

        var names = JsonDocument.Parse(await all.Content.ReadAsStringAsync()).RootElement.GetProperty("skills").EnumerateArray().Select(s => s.GetProperty("name").GetString());
        Assert.Equal(["mine"], names);
        Assert.Equal(HttpStatusCode.NotFound, theirs.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(2, (await _host.GetAsync("api/metrics/skills")).GetProperty("skills").GetArrayLength()); // an Admin sees every project
    }

    [Fact]
    public async Task AMember_SeesTheLessonReportOfTheirProjectsOnly()
    {
        var mine = await _host.RegisterProjectAsync();
        var other = await _host.RegisterProjectAsync("billing");
        await _host.PostAsync($"api/projects/{mine}/lessons", new { rule = "Mine.", topics = new[] { "csv" }, approve = true }, HttpStatusCode.Created);
        await _host.PostAsync($"api/projects/{other}/lessons", new { rule = "Theirs.", topics = new[] { "csv" }, approve = true }, HttpStatusCode.Created);
        await _host.CreateMemberAsync("maria", mine);
        var maria = await _host.SignedInAsync("maria");

        using var all = await maria.GetAsync("api/metrics/lessons");
        using var theirs = await maria.GetAsync($"api/metrics/lessons?projectId={other}");

        var rules = JsonDocument.Parse(await all.Content.ReadAsStringAsync()).RootElement.GetProperty("lessons").EnumerateArray().Select(l => l.GetProperty("rule").GetString());
        Assert.Equal(["Mine."], rules);
        Assert.Equal(HttpStatusCode.NotFound, theirs.StatusCode);
        Assert.Equal(2, (await _host.GetAsync("api/metrics/lessons")).GetProperty("lessons").GetArrayLength()); // an Admin sees every project
    }
}
