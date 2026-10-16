using System.Net;
using System.Text.Json;
using Litos.SoftwareFactory.Core.Lifecycle;

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

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(30, (await _host.GetAsync("api/metrics")).GetProperty("days").GetInt32());
    }
}
