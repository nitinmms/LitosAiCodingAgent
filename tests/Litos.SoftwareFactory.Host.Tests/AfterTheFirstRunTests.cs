using System.Net;
using System.Text.Json;
using Litos.Agent.Streaming;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Budget;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Orchestration;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Host.Api;
using Litos.SoftwareFactory.Host.Runs;
using Litos.SoftwareFactory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>
/// What the first real task (F1 on filedb-sharp) showed was missing once it had been handed
/// off: a question sent with @factory became a rework run with no way back, a model call cut
/// off with the host kept its reservation for ever, and the pull request stayed "draft" in the
/// factory after it was merged on GitHub.
/// </summary>
public sealed class WithdrawTests : IAsyncLifetime
{
    private TestHost _host = null!;

    public async Task InitializeAsync() => _host = await TestHost.StartAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private async Task<int> LeaseCountAsync()
    {
        await using var db = await _host.App.Services.GetRequiredService<IDbContextFactory<FactoryDbContext>>().CreateDbContextAsync();
        return await db.Leases.CountAsync();
    }

    /// <summary>A task handed off once, with the two turns that took already played.</summary>
    private async Task<(Guid ProjectId, Guid ThreadId, ThreadDetails Handoff)> HandedOffAsync()
    {
        var projectId = await _host.RegisterProjectAsync();
        var threadId = await _host.CreateThreadAsync(projectId);
        await _host.DelegateAsync(threadId);
        return (projectId, threadId, await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting));
    }

    /// <summary>The next turn edits the working copy and then fails, leaving the run Blocked.</summary>
    private void TheNextTurnEditsThenFails() => _host.Workers.Script.Enqueue(call =>
    {
        var workspace = _host.Workers.WorkspaceOf(call.Worker);
        workspace.Write("src/Orders.cs", "half reworked\n");
        workspace.Write("src/Unwanted.cs", "class Unwanted { }\n");
        return Task.FromResult(new TurnStreamResult(false, 2, "The model failed."));
    });

    [Fact]
    public async Task Withdraw_ABlockedChangeRequest_DiscardsItsEdits_FreesTheRepository_AndTheHandoffCanBeAccepted()
    {
        var (projectId, threadId, handoff) = await HandedOffAsync();
        var workspace = _host.Workspaces.Of(projectId);
        var handedOff = workspace.Files["src/Orders.cs"];
        TheNextTurnEditsThenFails();
        await _host.DelegateAsync(threadId, "@factory How do I set up manual tests?");
        var blocked = await _host.WaitForStateAsync(threadId, LifecycleState.Blocked);
        Assert.Equal(RunKind.Rework, blocked.LatestRun!.Kind);
        Assert.Equal("half reworked\n", workspace.Files["src/Orders.cs"]);
        Assert.Equal(1, await LeaseCountAsync());

        var withdrawn = await _host.PostAsync($"api/threads/{threadId}/withdraw", null, HttpStatusCode.OK);

        Assert.Equal("AwaitingHumanTesting", withdrawn.GetProperty("state").GetString());
        Assert.Equal("Handoff", withdrawn.GetProperty("stage").GetString());
        // The rework's edits are gone and the branch is where the handoff left it.
        Assert.Contains($"discard {handoff.Thread.Branch}", workspace.Calls);
        Assert.Equal(handedOff, workspace.Files["src/Orders.cs"]);
        Assert.False(workspace.Files.ContainsKey("src/Unwanted.cs"));
        Assert.True((await workspace.GetStatusAsync(default)).IsClean);
        Assert.Single(workspace.Commits);
        Assert.Equal(0, await LeaseCountAsync());

        var details = await _host.ThreadAsync(threadId);
        Assert.Equal((RunStatus.Finished, StopReason.Withdrawn), (details.LatestRun!.Status, details.LatestRun.StopReason));
        Assert.Equal(handoff.LatestHandoff!.CommitSha, details.LatestHandoff!.CommitSha);
        Assert.Equal("Change request withdrawn. The task is back at its last handoff.", details.Messages[^1].Text);

        var accepted = await _host.PostAsync($"api/threads/{threadId}/accept", null, HttpStatusCode.OK);
        Assert.Equal("Accepted", accepted.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Withdraw_ARunningChangeRequest_IsRefused_ThenWorksOncePaused()
    {
        var (_, threadId, _) = await HandedOffAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _host.Workers.Script.Enqueue(async call =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, call.Token);
            return FakeWorkerLauncher.Done();
        });
        await _host.DelegateAsync(threadId, "@factory How do I set up manual tests?");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(20));

        var refusal = await _host.PostAsync($"api/threads/{threadId}/withdraw", null, HttpStatusCode.Conflict);
        Assert.Contains("Pause the task, then withdraw it", refusal.GetProperty("error").GetString());

        await _host.PostAsync($"api/threads/{threadId}/pause", null, HttpStatusCode.Accepted);
        await _host.WaitForStateAsync(threadId, LifecycleState.PausedUser);
        await _host.PostAsync($"api/threads/{threadId}/withdraw", null, HttpStatusCode.OK);

        Assert.Equal(LifecycleState.AwaitingHumanTesting, (await _host.ThreadAsync(threadId)).Thread.State);
        Assert.Equal(0, await LeaseCountAsync());
    }

    /// <summary>The request is withdrawn whatever happens to the working copy; the thread says
    /// that the edits are still there, and the repository is released either way.</summary>
    [Fact]
    public async Task Withdraw_WhenTheEditsCannotBeDiscarded_StillWithdraws_SaysSo_AndReleasesTheRepository()
    {
        var (projectId, threadId, _) = await HandedOffAsync();
        TheNextTurnEditsThenFails();
        await _host.DelegateAsync(threadId, "@factory How do I set up manual tests?");
        await _host.WaitForStateAsync(threadId, LifecycleState.Blocked);
        _host.Workspaces.Of(projectId).FailDiscard = new WorkspaceException("The working copy is on 'main', not 'factory/x'; nothing was discarded.");

        await _host.PostAsync($"api/threads/{threadId}/withdraw", null, HttpStatusCode.OK);

        var details = await _host.ThreadAsync(threadId);
        Assert.Equal(LifecycleState.AwaitingHumanTesting, details.Thread.State);
        Assert.Contains("could not be discarded: The working copy is on 'main'", details.Messages[^1].Text);
        Assert.Equal(0, await LeaseCountAsync());
    }

    /// <summary>A change request that never started has touched nothing, and the working copy
    /// may be another task's by now: it is left alone.</summary>
    [Fact]
    public async Task Withdraw_AChangeRequestThatNeverStarted_DoesNotTouchTheWorkingCopy()
    {
        await using var host = await TestHost.StartAsync(startCoordinator: false);
        var projectId = await host.RegisterProjectAsync();
        var threadId = await host.CreateThreadAsync(projectId);
        await host.DelegateAsync(threadId);
        await host.App.Services.GetRequiredService<RunExecutor>().ExecuteAsync((await host.Store.ClaimNextRunAsync(1, DateTimeOffset.UtcNow, default))!, default);
        Assert.Equal(LifecycleState.AwaitingHumanTesting, (await host.ThreadAsync(threadId)).Thread.State);
        await host.DelegateAsync(threadId, "@factory How do I set up manual tests?"); // queued; nothing claims it

        await host.PostAsync($"api/threads/{threadId}/withdraw", null, HttpStatusCode.OK);

        Assert.DoesNotContain(host.Workspaces.Of(projectId).Calls, c => c.StartsWith("discard"));
        Assert.Equal(LifecycleState.AwaitingHumanTesting, (await host.ThreadAsync(threadId)).Thread.State);
    }

    [Fact]
    public async Task Withdraw_TheOriginalWork_IsRefused_AndSoIsAnUnknownThread()
    {
        TheNextTurnEditsThenFails();
        var threadId = await _host.CreateThreadAsync(await _host.RegisterProjectAsync());
        await _host.DelegateAsync(threadId);
        await _host.WaitForStateAsync(threadId, LifecycleState.Blocked);

        var refusal = await _host.PostAsync($"api/threads/{threadId}/withdraw", null, HttpStatusCode.Conflict);

        Assert.Contains("no change request to withdraw", refusal.GetProperty("error").GetString());
        Assert.Equal(LifecycleState.Blocked, (await _host.ThreadAsync(threadId)).Thread.State);
        await _host.PostAsync($"api/threads/{Guid.NewGuid()}/withdraw", null, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Withdraw_NeedsSignIn()
    {
        using var anonymous = _host.NewClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync($"api/threads/{Guid.NewGuid()}/withdraw", null)).StatusCode);
    }
}

/// <summary>
/// A project has one working copy. A task that is cancelled, or given up on after a failure,
/// leaves its uncommitted edits there — and the second real task (F2) could not start because
/// of one edited file the first had left behind.
/// </summary>
public sealed class LeftoverEditsTests : IAsyncLifetime
{
    private TestHost _host = null!;

    public async Task InitializeAsync() => _host = await TestHost.StartAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task ATaskCancelledMidWork_DoesNotBlockTheNextTaskOnTheRepository_AndItsEditsAreSetAsideNotDeleted()
    {
        var projectId = await _host.RegisterProjectAsync();
        var workspace = _host.Workspaces.Of(projectId);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _host.Workers.Script.Enqueue(async call =>
        {
            _host.Workers.WorkspaceOf(call.Worker).Write("README.md", "half an edit\n");
            started.SetResult();
            await Task.Delay(Timeout.Infinite, call.Token);
            return FakeWorkerLauncher.Done();
        });
        var first = await _host.CreateThreadAsync(projectId, "Enforce size limits");
        await _host.DelegateAsync(first);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await _host.PostAsync($"api/threads/{first}/cancel", null, HttpStatusCode.Accepted);
        var cancelled = await _host.WaitForStateAsync(first, LifecycleState.Cancelled);
        Assert.False((await workspace.GetStatusAsync(default)).IsClean);

        var second = await _host.CreateThreadAsync(projectId, "JSON Lines export");
        await _host.DelegateAsync(second);
        var handedOff = await _host.WaitForStateAsync(second, LifecycleState.AwaitingHumanTesting);

        // The first task's edit was moved out of the way, with a label saying whose it was.
        var label = Assert.Single(workspace.SetAside);
        Assert.Contains($"Left on {cancelled.Thread.Branch} by an earlier task", label);
        Assert.Contains("JSON Lines export", label);
        Assert.Contains(handedOff.Messages, m => m.Text.StartsWith("Set aside 1 uncommitted path(s) an earlier task left on") && m.Text.Contains("README.md") && m.Text.Contains("not deleted"));
        // It is not part of the second task's work.
        Assert.Equal("readme\n", workspace.Files["README.md"]);
        Assert.NotEqual(cancelled.Thread.Branch, handedOff.Thread.Branch);
        Assert.Single(workspace.Commits);
    }

    /// <summary>A run's own uncommitted work, on its own branch, is exactly what a resume
    /// continues from: it must never be set aside.</summary>
    [Fact]
    public async Task ARunsOwnUncommittedWork_IsLeftInPlace_WhenItResumes()
    {
        var projectId = await _host.RegisterProjectAsync();
        _host.Workers.Script.Enqueue(call =>
        {
            _host.Workers.WorkspaceOf(call.Worker).Write("src/Orders.cs", "half done\n");
            return Task.FromResult(new TurnStreamResult(false, 1, "The model failed."));
        });
        var threadId = await _host.CreateThreadAsync(projectId);
        await _host.DelegateAsync(threadId);
        await _host.WaitForStateAsync(threadId, LifecycleState.Blocked);

        await _host.PostAsync($"api/threads/{threadId}/resume", null, HttpStatusCode.OK);
        await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        Assert.Empty(_host.Workspaces.Of(projectId).SetAside);
        Assert.DoesNotContain(_host.Workspaces.Of(projectId).Calls, c => c.StartsWith("set aside"));
    }

    [Fact]
    public async Task ACleanWorkingCopy_HasNothingSetAside_AndTheThreadSaysNothingAboutIt()
    {
        var projectId = await _host.RegisterProjectAsync();
        var threadId = await _host.CreateThreadAsync(projectId);
        await _host.DelegateAsync(threadId);

        var details = await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        Assert.Empty(_host.Workspaces.Of(projectId).SetAside);
        Assert.DoesNotContain(details.Messages, m => m.Text.Contains("Set aside"));
    }
}

/// <summary>What the third real task (F3) showed about a rework round.</summary>
public sealed class ReworkRoundTests : IAsyncLifetime
{
    private TestHost _host = null!;

    public async Task InitializeAsync() => _host = await TestHost.StartAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private async Task<(Guid ProjectId, Guid ThreadId, ThreadDetails First)> HandedOffAsync()
    {
        var projectId = await _host.RegisterProjectAsync();
        var threadId = await _host.CreateThreadAsync(projectId);
        await _host.DelegateAsync(threadId);
        return (projectId, threadId, await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting));
    }

    /// <summary>
    /// The review after a rework re-reviewed the whole task, and cost more than the task's first
    /// implementation. It now gets only what the rework changed, with what the tester asked for.
    /// </summary>
    [Fact]
    public async Task TheReviewOfARework_GetsOnlyTheReworksDiff_AndWhatTheTesterAsked()
    {
        var (_, threadId, first) = await HandedOffAsync();
        _host.Workers.Script.Enqueue(async call =>
        {
            _host.Workers.WorkspaceOf(call.Worker).Write("src/Quoting.cs", "quote fields\n");
            await call.Worker.SubmitAsync(call.SessionId, FakeWorkerLauncher.Work("Quoted fields that contain commas."));
            return FakeWorkerLauncher.Done();
        });

        await _host.DelegateAsync(threadId, "@factory CSV values containing commas are incorrect. Fix this.");
        await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        var reviews = _host.Workers.Turns.Where(t => t.Kind == TurnKind.Review).ToList();
        Assert.Equal(2, reviews.Count);

        // The first review saw the whole change against the base branch, and nothing about a rework.
        Assert.Contains("src/Orders.cs", reviews[0].Brief);
        Assert.DoesNotContain("Scope: a rework", reviews[0].Brief);

        // The second saw the rework: its own file, the handoff it follows, and the tester's words.
        Assert.Contains("## Scope: a rework", reviews[1].Brief);
        Assert.Contains($"handed off at commit `{first.LatestHandoff!.CommitSha![..7]}`", reviews[1].Brief);
        Assert.Contains("> CSV values containing commas are incorrect. Fix this.", reviews[1].Brief);
        Assert.Contains("src/Quoting.cs", reviews[1].Brief);
        Assert.DoesNotContain("src/Orders.cs", reviews[1].Brief);
    }

    /// <summary>
    /// Both rework handoffs on the first real tasks failed to update the pull request, and each
    /// time the handoff lost its link to a pull request that was still there.
    /// </summary>
    [Fact]
    public async Task WhenThePullRequestCannotBeUpdated_TheHandoffStillNamesIt_AndSaysWhatFailed()
    {
        var (_, threadId, first) = await HandedOffAsync();
        Assert.Equal(212, first.LatestHandoff!.PullRequestNumber);
        _host.GitHub.Fail = new HttpRequestException("An error occurred while sending the request.");

        await _host.DelegateAsync(threadId, "@factory Quote fields that contain commas.");
        var second = await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        Assert.NotEqual(first.LatestHandoff.CommitSha, second.LatestHandoff!.CommitSha);
        Assert.Equal((212, "https://github.com/acme/salesapp/pull/212"), (second.LatestHandoff.PullRequestNumber, second.LatestHandoff.PullRequestUrl));
        var evidence = JsonSerializer.Deserialize<HandoffEvidence>(second.LatestHandoff.EvidenceJson, FactoryWire.Json)!;
        Assert.Equal(212, evidence.PullRequestNumber);
        Assert.Contains(evidence.KnownLimitations, l => l == "The pull request's description could not be updated: An error occurred while sending the request.");
        Assert.DoesNotContain(evidence.KnownLimitations, l => l.Contains("could not be opened"));
        Assert.Contains("draft PR #212", second.Messages[^1].Text);
    }

    [Fact]
    public async Task WhenTheFirstPullRequestCannotBeOpened_TheHandoffSaysSo_AndNamesNone()
    {
        _host.GitHub.Fail = new HttpRequestException("api.github.com could not be reached.");
        var threadId = await _host.CreateThreadAsync(await _host.RegisterProjectAsync());
        await _host.DelegateAsync(threadId);

        var details = await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        Assert.Null(details.LatestHandoff!.PullRequestNumber);
        var evidence = JsonSerializer.Deserialize<HandoffEvidence>(details.LatestHandoff.EvidenceJson, FactoryWire.Json)!;
        Assert.Contains(evidence.KnownLimitations, l => l == "The draft PR could not be opened: api.github.com could not be reached.");
    }
}

/// <summary>A model call whose usage will never be reported is settled, not held for ever.</summary>
public sealed class UsageReconciliationTests
{
    private static readonly ReserveCommand Call = new("left-behind", Guid.Empty, null, Guid.Empty, "openrouter", "deepseek/deepseek-v4.1-flash", 23_900, 23_900);

    /// <summary>The first real run: the host was stopped in the middle of a model call, and
    /// 52,577 tokens stayed "reserved for a call in flight" on the task from then on.</summary>
    [Fact]
    public async Task HostStartup_ChargesCallsAPreviousHostLeftInFlightTheirInputEstimate_AndFreesTheRest()
    {
        await using var host = await TestHost.StartAsync(startCoordinator: false);
        var threadId = await host.CreateThreadAsync(await host.RegisterProjectAsync(), budgetCap: 300_000);
        await host.DelegateAsync(threadId);
        var claimed = (await host.Store.ClaimNextRunAsync(1, DateTimeOffset.UtcNow, default))!;
        await host.Store.ReserveAsync(Call with { ThreadId = threadId, RunId = claimed.Run.Id, UserId = claimed.Run.RequestedBy }, host.Options.Budget, DateTimeOffset.UtcNow, default);
        await host.Store.SetRunWorkerAsync(claimed.Run.Id, int.MaxValue - 5, DateTimeOffset.UtcNow.AddHours(-1), default);
        Assert.True((await host.ThreadAsync(threadId)).Thread.TokensReserved > 23_900);

        await host.App.Services.GetRequiredService<RunCoordinator>().RecoverAsync(default);

        var details = await host.ThreadAsync(threadId);
        Assert.Equal(LifecycleState.Interrupted, details.Thread.State);
        Assert.Equal((23_900L, 0L), (details.Thread.TokensUsed, details.Thread.TokensReserved));
        var entry = Assert.Single(await host.Store.ListUsageAsync(threadId, default));
        Assert.Equal((UsageStatus.Estimated, 23_900L), (entry.Status, entry.Charged));
    }

    /// <summary>The same without a restart: the provider failed mid-reply, so the call's usage is
    /// unknown, and once its run has stopped nothing can report it.</summary>
    [Fact]
    public async Task WhenARunStops_ItsCallsWithUnknownUsageAreSettled()
    {
        await using var host = await TestHost.StartAsync();
        host.Provider.Enqueue((_, _) => ScriptedProvider.Events(new TextDelta("partial"), new ErrorOccurred(new IOException("The connection was reset."))));
        host.Workers.Script.Enqueue(async call =>
        {
            await call.Worker.CallModelAsync(call.SessionId);
            return new TurnStreamResult(false, 0, "The connection was reset.");
        });
        var threadId = await host.CreateThreadAsync(await host.RegisterProjectAsync(), budgetCap: 300_000);

        await host.DelegateAsync(threadId);
        var blocked = await host.WaitForStateAsync(threadId, LifecycleState.Blocked);

        // The run is over by the time its thread reads Blocked or just after; wait for the books.
        UsageEntry entry = null!;
        for (var i = 0; i < 100; i++)
        {
            entry = Assert.Single(await host.Store.ListUsageAsync(threadId, default));
            if (entry.Status != UsageStatus.Unknown)
                break;
            await Task.Delay(50);
        }

        Assert.Equal(UsageStatus.Estimated, entry.Status);
        Assert.Equal(entry.EstimatedInput, entry.Charged);
        var thread = (await host.ThreadAsync(threadId)).Thread;
        Assert.Equal((entry.Charged, 0L), (thread.TokensUsed, thread.TokensReserved));
        Assert.Equal(StopReason.TurnFaulted, blocked.LatestRun!.StopReason);
    }
}

/// <summary>People merge and close pull requests on GitHub, so the factory asks GitHub.</summary>
public sealed class PullRequestStatusTests
{
    private sealed class ManualClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);
    }

    private static PullRequestStatus Status(FakeGitHub? gitHub, ManualClock clock) =>
        new(clock, NullLogger<PullRequestStatus>.Instance, gitHub);

    [Fact]
    public async Task Endpoint_SaysWhereThePullRequestStands_AndFollowsGitHub()
    {
        await using var host = await TestHost.StartAsync();
        host.App.Services.GetRequiredService<PullRequestStatus>().Freshness = TimeSpan.Zero;
        var threadId = await host.CreateThreadAsync(await host.RegisterProjectAsync());
        await host.DelegateAsync(threadId);
        await host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        var draft = await host.GetAsync($"api/threads/{threadId}/pull-request");
        Assert.Equal((212, "Draft"), (draft.GetProperty("number").GetInt32(), draft.GetProperty("state").GetString()));
        Assert.Equal("https://github.com/acme/salesapp/pull/212", draft.GetProperty("url").GetString());
        Assert.Equal("acme/salesapp#212", host.GitHub.StateRequests[^1]);

        host.GitHub.State = PullRequestState.Merged;
        Assert.Equal("Merged", (await host.GetAsync($"api/threads/{threadId}/pull-request")).GetProperty("state").GetString());

        // GitHub being down is not the factory's failure: the page still loads.
        host.GitHub.FailState = new HttpRequestException("api.github.com could not be reached.");
        Assert.Equal("Unknown", (await host.GetAsync($"api/threads/{threadId}/pull-request")).GetProperty("state").GetString());
    }

    [Fact]
    public async Task Endpoint_ForATaskWithNoPullRequest_OrAnUnknownThread_Is404_AndNeedsSignIn()
    {
        await using var host = await TestHost.StartAsync(startCoordinator: false);
        var threadId = await host.CreateThreadAsync(await host.RegisterProjectAsync());
        using var anonymous = host.NewClient();

        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync($"api/threads/{threadId}/pull-request")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync($"api/threads/{Guid.NewGuid()}/pull-request")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"api/threads/{threadId}/pull-request")).StatusCode);
        Assert.Empty(host.GitHub.StateRequests);
    }

    [Fact]
    public async Task Answer_IsReusedWhileFresh_SoEveryPageLoadIsNotAGitHubRequest()
    {
        var clock = new ManualClock();
        var gitHub = new FakeGitHub();
        var status = Status(gitHub, clock);

        Assert.Equal(PullRequestState.Draft, await status.GetAsync("acme", "salesapp", 12, default));
        gitHub.State = PullRequestState.Merged;
        clock.UtcNow += TimeSpan.FromSeconds(59);
        Assert.Equal(PullRequestState.Draft, await status.GetAsync("acme", "salesapp", 12, default));
        Assert.Single(gitHub.StateRequests);

        clock.UtcNow += TimeSpan.FromSeconds(2);
        Assert.Equal(PullRequestState.Merged, await status.GetAsync("acme", "salesapp", 12, default));
        Assert.Equal(2, gitHub.StateRequests.Count);
    }

    [Fact]
    public async Task EachPullRequest_IsAskedAboutSeparately()
    {
        var gitHub = new FakeGitHub();
        var status = Status(gitHub, new ManualClock());

        await status.GetAsync("acme", "salesapp", 12, default);
        await status.GetAsync("acme", "salesapp", 13, default);
        await status.GetAsync("acme", "other", 12, default);

        Assert.Equal(["acme/salesapp#12", "acme/salesapp#13", "acme/other#12"], gitHub.StateRequests);
    }

    /// <summary>A failure is remembered for the same short time, so an outage is not hammered.</summary>
    [Fact]
    public async Task Failure_IsUnknown_AndIsNotRetriedUntilItIsStale()
    {
        var clock = new ManualClock();
        var gitHub = new FakeGitHub { FailState = new HttpRequestException("down") };
        var status = Status(gitHub, clock);

        Assert.Null(await status.GetAsync("acme", "salesapp", 12, default));
        Assert.Null(await status.GetAsync("acme", "salesapp", 12, default));
        Assert.Single(gitHub.StateRequests);

        gitHub.FailState = null;
        clock.UtcNow += TimeSpan.FromSeconds(61);
        Assert.Equal(PullRequestState.Draft, await status.GetAsync("acme", "salesapp", 12, default));
    }

    [Fact]
    public async Task WithNoGitHubTokenConfigured_TheStateIsUnknown()
    {
        Assert.Null(await Status(null, new ManualClock()).GetAsync("acme", "salesapp", 12, default));
    }

    [Fact]
    public async Task ACancelledRequest_IsNotRememberedAsAFailure()
    {
        var gitHub = new FakeGitHub { FailState = new OperationCanceledException() };
        var status = Status(gitHub, new ManualClock());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => status.GetAsync("acme", "salesapp", 12, default));

        gitHub.FailState = null;
        Assert.Equal(PullRequestState.Draft, await status.GetAsync("acme", "salesapp", 12, default));
    }
}
