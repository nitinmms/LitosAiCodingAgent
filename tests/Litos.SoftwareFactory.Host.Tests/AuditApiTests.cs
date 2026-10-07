using System.Net;
using System.Net.Http.Json;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Store;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>
/// The two actions M1 left unrecorded, through the API (§13.3: every action records the acting
/// user): stopping a running task, and changing a task's budget. Ben, a Member, does both.
/// </summary>
public sealed class AuditApiTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private HttpClient _ben = null!;
    private Guid _benId, _project;

    public async Task InitializeAsync()
    {
        _host = await TestHost.StartAsync();
        _project = await _host.RegisterProjectAsync();
        _benId = await _host.CreateMemberAsync("ben", _project);
        _ben = await _host.SignedInAsync("ben");
    }

    public async Task DisposeAsync()
    {
        _ben.Dispose();
        await _host.DisposeAsync();
    }

    private async Task<AuditEvent[]> RowsAsync(string action) =>
        [.. (await _host.Store.ListAuditAsync(null, 500, default)).Where(r => r.Action == action)];

    private async Task<Guid> RunningTaskAsync()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _host.Workers.Script.Enqueue(async call =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, call.Token);
            return FakeWorkerLauncher.Done();
        });
        var threadId = await _host.CreateThreadAsync(_project);
        await _host.DelegateAsync(threadId);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(20));
        return threadId;
    }

    [Theory]
    [InlineData("pause", AuditActions.ThreadPause, LifecycleState.PausedUser)]
    [InlineData("cancel", AuditActions.ThreadCancel, LifecycleState.Cancelled)]
    public async Task StoppingARunningTask_RecordsWhoAskedOnce(string route, string action, LifecycleState ends)
    {
        var threadId = await RunningTaskAsync();

        using var response = await _ben.PostAsJsonAsync($"api/threads/{threadId}/{route}", new { });
        await _host.WaitForStateAsync(threadId, ends);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var row = Assert.Single(await RowsAsync(action)); // the run reaching the stop adds none
        Assert.Equal((_benId, (Guid?)threadId, (Guid?)_project), (row.ActorId, row.TargetId, row.ProjectId));
        Assert.Contains("Running", row.DetailsJson);
    }

    [Fact]
    public async Task ChangingABudget_ThroughTheApi_RecordsWhoChangedIt()
    {
        var threadId = await _host.CreateThreadAsync(_project, budgetCap: 300_000);

        using var response = await _ben.PostAsJsonAsync($"api/threads/{threadId}/budget", new { cap = 450_000 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var row = Assert.Single(await RowsAsync(AuditActions.ThreadBudget));
        Assert.Equal(_benId, row.ActorId);
        Assert.Contains("450000", row.DetailsJson);
    }

    /// <summary>A refused change is not recorded: the log never claims what did not happen.</summary>
    [Fact]
    public async Task ARefusedBudget_IsNotRecorded()
    {
        var threadId = await _host.CreateThreadAsync(_project);

        using var response = await _ben.PostAsJsonAsync($"api/threads/{threadId}/budget", new { cap = -5 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await RowsAsync(AuditActions.ThreadBudget));
    }

    [Fact]
    public async Task DelegatingThroughTheApi_RecordsTheMemberWhoDidIt()
    {
        var threadId = await _host.CreateThreadAsync(_project);

        using var response = await _ben.PostAsJsonAsync($"api/threads/{threadId}/messages", new { messageId = "m1", text = "@factory Add CSV export." });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(_benId, Assert.Single(await RowsAsync(AuditActions.ThreadDelegate)).ActorId);
    }
}
