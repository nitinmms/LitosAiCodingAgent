using System.Net;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Orchestration;
using Litos.SoftwareFactory.Core.Store;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>
/// The resume check (§16, docs/software-factory/m2-architecture.md §3.4): a stopped run records
/// the working copy, and when it resumes the host says what changed in the meantime — in the
/// thread and in the resume brief — and carries on.
/// </summary>
public sealed class ResumeCheckTests : IAsyncLifetime
{
    private const string Differs = "Resuming. The working copy differs";

    private TestHost _host = null!;

    public async Task InitializeAsync() => _host = await TestHost.StartAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    /// <summary>Delegates a task whose first turn edits a file and runs until paused, and pauses it.</summary>
    private async Task<(Guid ProjectId, Guid ThreadId)> PausedMidTurnAsync()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _host.Workers.Script.Enqueue(async call =>
        {
            _host.Workers.WorkspaceOf(call.Worker).Write("src/Orders.cs", "in progress\n");
            started.SetResult();
            await Task.Delay(Timeout.Infinite, call.Token);
            return FakeWorkerLauncher.Done();
        });
        var projectId = await _host.RegisterProjectAsync();
        var threadId = await _host.CreateThreadAsync(projectId);
        await _host.DelegateAsync(threadId);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(20));

        await _host.PostAsync($"api/threads/{threadId}/pause", null, HttpStatusCode.Accepted);
        await _host.WaitForStateAsync(threadId, LifecycleState.PausedUser);
        return (projectId, threadId);
    }

    private string ResumeBrief => _host.Workers.Turns.Last(t => t.Kind == TurnKind.Implement).Brief;

    private static string[] Notes(ThreadDetails details) =>
        [.. details.Messages.Where(m => m.Kind == MessageKind.Status).Select(m => m.Text)];

    [Fact]
    public async Task AStoppedRun_RecordsItsWorkingCopy()
    {
        var (_, threadId) = await PausedMidTurnAsync();

        var run = (await _host.ThreadAsync(threadId)).LatestRun!;

        Assert.Contains("src/Orders.cs", run.WorkspaceSnapshotJson);
    }

    [Fact]
    public async Task EditedWhileStopped_TheThreadAndTheResumeBriefSayWhatChanged_AndTheRunContinues()
    {
        var (projectId, threadId) = await PausedMidTurnAsync();
        var workspace = _host.Workspaces.Of(projectId);
        workspace.Write("src/Manual.cs", "added by hand\n");
        workspace.Write("src/Orders.cs", "edited by hand\n");

        await _host.PostAsync($"api/threads/{threadId}/resume", null, HttpStatusCode.OK);
        var done = await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        var note = Assert.Single(Notes(done), n => n.StartsWith(Differs, StringComparison.Ordinal));
        Assert.Contains("- Changed since then: `src/Orders.cs`.", note);
        Assert.Contains("- Newly changed: `src/Manual.cs`.", note);
        Assert.Contains("compared the working copy with the run's last checkpoint", ResumeBrief);
        Assert.Contains("`src/Manual.cs`", ResumeBrief);
    }

    [Fact]
    public async Task ACommitMadeWhileStopped_IsStated()
    {
        var (projectId, threadId) = await PausedMidTurnAsync();
        _host.Workspaces.Of(projectId).SetHead("handmade1234567");

        await _host.PostAsync($"api/threads/{threadId}/resume", null, HttpStatusCode.OK);
        var done = await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        Assert.Contains(Notes(done), n => n.StartsWith(Differs, StringComparison.Ordinal) && n.Contains("The head commit moved from base000 to handmad."));
    }

    [Fact]
    public async Task UntouchedWhileStopped_NothingIsSaid()
    {
        var (_, threadId) = await PausedMidTurnAsync();

        await _host.PostAsync($"api/threads/{threadId}/resume", null, HttpStatusCode.OK);
        var done = await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        Assert.DoesNotContain(Notes(done), n => n.StartsWith(Differs, StringComparison.Ordinal));
        Assert.DoesNotContain("last checkpoint", ResumeBrief);
    }

    /// <summary>A change request is a new run on a clean, pushed branch: it has nothing to compare.</summary>
    [Fact]
    public async Task AReworkRun_HasNoResumeCheck()
    {
        var projectId = await _host.RegisterProjectAsync();
        var threadId = await _host.CreateThreadAsync(projectId);
        await _host.DelegateAsync(threadId);
        await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        await _host.DelegateAsync(threadId, "@factory Also handle an empty result.");
        var done = await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        Assert.Equal(RunKind.Rework, done.LatestRun!.Kind);
        Assert.DoesNotContain(Notes(done), n => n.StartsWith(Differs, StringComparison.Ordinal));
    }
}
