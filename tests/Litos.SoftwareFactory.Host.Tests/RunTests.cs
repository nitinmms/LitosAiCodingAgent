using System.Net;
using System.Text.Json;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Orchestration;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Core.Verification;
using Litos.SoftwareFactory.Host.Runs;
using Litos.SoftwareFactory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>
/// Whole runs through the real host: the API, the store, the coordinator, the orchestrator and
/// the worker callbacks are all real. The model, git, verification, GitHub and the worker
/// process are fakes that the tests script.
/// </summary>
public sealed class RunTests : IAsyncLifetime
{
    private TestHost _host = null!;

    public async Task InitializeAsync()
    {
        WorkerCallbacks.DecisionStopDelay = TimeSpan.FromMilliseconds(50);
        _host = await TestHost.StartAsync();
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private async Task<(Guid ProjectId, Guid ThreadId)> DelegatedAsync(long? cap = null, string repository = "salesapp")
    {
        var projectId = await _host.RegisterProjectAsync(repository);
        var threadId = await _host.CreateThreadAsync(projectId, budgetCap: cap);
        await _host.DelegateAsync(threadId);
        return (projectId, threadId);
    }

    private TurnKind[] TurnKinds => [.. _host.Workers.Turns.Select(t => t.Kind)];

    private static string[] Texts(ThreadDetails details, MessageKind kind) => [.. details.Messages.Where(m => m.Kind == kind).Select(m => m.Text)];

    private async Task<int> LeaseCountAsync()
    {
        await using var db = await _host.App.Services.GetRequiredService<IDbContextFactory<FactoryDbContext>>().CreateDbContextAsync();
        return await db.Leases.CountAsync();
    }

    // ---- The straight path ----

    /// <summary>Acceptance scenario 2: a small feature produces a pushed branch, a draft PR and
    /// real evidence, then waits for human testing.</summary>
    [Fact]
    public async Task Delegated_RunsImplementVerifyReviewHandoff_ThenWaitsForHumanTesting()
    {
        var (projectId, threadId) = await DelegatedAsync();

        var details = await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        Assert.Equal([TurnKind.Implement, TurnKind.Review], TurnKinds);
        Assert.Equal(Stage.Handoff, details.Thread.Stage);

        // The host did the git work, on a factory branch, as the factory with the user as co-author.
        var workspace = _host.Workspaces.Of(projectId);
        Assert.StartsWith("factory/", details.Thread.Branch);
        Assert.EndsWith("-add-csv-export", details.Thread.Branch);
        Assert.Contains($"branch {details.Thread.Branch} from main", workspace.Calls);
        var commit = Assert.Single(workspace.Commits);
        Assert.StartsWith("Add CSV export\n\nAdded CSV export for Orders.", commit.Message);
        Assert.Equal("Litos Software Factory", commit.Author.Name);
        Assert.Equal("Admin <admin@example.invalid>", commit.CoAuthor);
        Assert.Equal([$"{details.Thread.Branch}@commit001"], workspace.Pushed);

        // A draft PR, recorded on the thread.
        var draft = Assert.Single(_host.GitHub.Drafts);
        Assert.Equal(("acme", "salesapp", details.Thread.Branch, "main"), (draft.Owner, draft.Repository, draft.Head, draft.Base));
        Assert.Equal(212, details.Thread.PullRequestNumber);

        // The handoff message is composed from the host's evidence.
        var handoff = Assert.Single(Texts(details, MessageKind.Handoff));
        Assert.StartsWith($"Ready for human testing. Branch {details.Thread.Branch} pushed (commit commit0), draft PR #212. Build passed. 2 unit tests passed (1 new).", handoff);
        Assert.Contains("Changed-line coverage 90% (threshold 80%).", handoff);
        Assert.Contains("Agent review: clean.", handoff);
        Assert.Equal("commit001", details.LatestHandoff!.CommitSha);
        Assert.Equal(UnitTestStatus.Passed, details.LatestVerification!.UnitTests);

        // The run is finished, the repository is free, and the worker was told to stop.
        Assert.Equal((RunStatus.Finished, StopReason.HandedOff), (details.LatestRun!.Status, details.LatestRun.StopReason));
        Assert.Equal(0, await LeaseCountAsync());
        var worker = Assert.Single(_host.Workers.Workers);
        Assert.True(worker.ShutdownRequested && worker.HasExited);
    }

    [Fact]
    public async Task TheWorker_IsLaunchedInTheWorkingCopy_WithTheTasksModel_AndARunSecret()
    {
        var (projectId, threadId) = await DelegatedAsync();
        await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        var launch = Assert.Single(_host.Workers.Workers).Launch;

        Assert.Equal(_host.Workspaces.Of(projectId).Path, launch.WorkingCopy);
        Assert.Equal(("openrouter", "deepseek/deepseek-v4.1-flash"), (launch.Provider, launch.Model));
        Assert.Equal(_host.Options.DataDirectory, launch.DataDirectory);
        Assert.Equal(64, launch.Secret.Length);
        Assert.StartsWith("http://127.0.0.1:", launch.HostUrl);
    }

    [Fact]
    public async Task Briefs_AreComposedByTheHost_AndTheReviewRunsInItsOwnSession()
    {
        var (_, threadId) = await DelegatedAsync();
        var details = await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        var turns = _host.Workers.Turns.ToArray();

        Assert.Contains("Add CSV export for Orders.", turns[0].Brief);
        Assert.Contains("call `submit_work`", turns[0].Brief);
        Assert.Contains("- dotnet: dotnet test", turns[0].Brief);
        Assert.Equal(details.Thread.SessionId, turns[0].SessionId);

        Assert.Contains("You are reviewing a change made by another agent", turns[1].Brief);
        Assert.Contains("+++ b/src/Orders.cs", turns[1].Brief); // the diff
        Assert.NotEqual(details.Thread.SessionId, turns[1].SessionId);
        Assert.StartsWith("review-", turns[1].SessionId);
    }

    [Fact]
    public async Task TheBaseline_IsMeasuredOnce_AndReusedForTheNextTaskOnTheSameCommit()
    {
        var (projectId, first) = await DelegatedAsync();
        await _host.WaitForStateAsync(first, LifecycleState.AwaitingHumanTesting);
        var second = await _host.CreateThreadAsync(projectId, "Fix the footer");
        _host.Workspaces.Of(projectId).Calls.Clear();
        await _host.DelegateAsync(second);
        await _host.WaitForStateAsync(second, LifecycleState.AwaitingHumanTesting);

        Assert.Single(_host.Verifier.Requests, r => r.ChangedFiles is null);     // one baseline run in total
        Assert.Equal(2, _host.Verifier.Requests.Count(r => r.ChangedFiles is not null));
    }

    // ---- Repair ----

    /// <summary>Acceptance scenario 9: a failing unit test triggers bounded repair.</summary>
    [Fact]
    public async Task FailingTest_GetsARepairTurn_WithTheFailureInItsBrief_ThenVerifiesAgain()
    {
        _host.Verifier.Outcomes.Enqueue(ScriptedVerifier.Failing("Orders.Export_Commas"));
        var (_, threadId) = await DelegatedAsync();

        var details = await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        Assert.Equal([TurnKind.Implement, TurnKind.Repair, TurnKind.Review], TurnKinds);
        var repair = _host.Workers.Turns.ElementAt(1).Brief;
        Assert.Contains("repair cycle 1 of 2", repair);
        Assert.Contains("`Orders.Export_Commas`", repair);
        Assert.Contains(Texts(details, MessageKind.Status), t => t.Contains("unit tests Failed"));
    }

    [Fact]
    public async Task PersistentFailure_IsBlocked_NotHandedOff_AndKeepsTheRepository()
    {
        _host.Verifier.Outcomes.Enqueue(ScriptedVerifier.Failing("T.A"));
        _host.Verifier.Outcomes.Enqueue(ScriptedVerifier.Failing("T.B"));
        _host.Verifier.Outcomes.Enqueue(ScriptedVerifier.Failing("T.C"));
        var (projectId, threadId) = await DelegatedAsync();

        var details = await _host.WaitForStateAsync(threadId, LifecycleState.Blocked);

        Assert.Equal(StopReason.RepairCyclesExhausted, details.LatestRun!.StopReason);
        Assert.Contains("Still failing after 2 repair cycles", details.Thread.StateReason);
        Assert.Empty(_host.Workspaces.Of(projectId).Pushed);
        Assert.Null(details.LatestHandoff);
        Assert.Equal(1, await LeaseCountAsync()); // uncommitted work is in the working copy
    }

    [Fact]
    public async Task PreExistingFailure_IsNotBlamedOnTheTask()
    {
        _host.Verifier.Baseline = ScriptedVerifier.Failing("Legacy.Broken");
        _host.Verifier.Outcomes.Enqueue(ScriptedVerifier.Failing("Legacy.Broken"));
        var (_, threadId) = await DelegatedAsync();

        var details = await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        Assert.Equal([TurnKind.Implement, TurnKind.Review], TurnKinds);
        Assert.Contains("1 failing test was already failing before this task.", Assert.Single(Texts(details, MessageKind.Handoff)));
    }

    // ---- Review ----

    [Fact]
    public async Task BlockingReviewFinding_IsRepairedAndVerified_ThenHandedOffAsFixed()
    {
        _host.Workers.Script.Enqueue(_host.DefaultTurnAsync);
        _host.Workers.Script.Enqueue(async call =>
        {
            await call.Worker.SubmitAsync(call.SessionId, new ReviewSubmission(
            [
                new ReviewFinding(FindingSeverity.Blocking, "src/Orders.cs", 42, "Crashes on an empty list."),
                new ReviewFinding(FindingSeverity.Minor, "src/Orders.cs", 7, "Leftover debug output."),
            ]));
            return FakeWorkerLauncher.Done();
        });
        var (_, threadId) = await DelegatedAsync();

        var details = await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        Assert.Equal([TurnKind.Implement, TurnKind.Review, TurnKind.Repair], TurnKinds); // the review is not repeated
        Assert.Contains("- `src/Orders.cs:42` — Crashes on an empty list.", _host.Workers.Turns.Last().Brief);
        Assert.Contains("Agent review: 1 finding fixed, 1 minor finding open.", Assert.Single(Texts(details, MessageKind.Handoff)));
        Assert.Equal(FindingStatus.Fixed, details.Findings.Single(f => f.Severity == FindingSeverity.Blocking).Status);
        Assert.Equal(FindingStatus.Open, details.Findings.Single(f => f.Severity == FindingSeverity.Minor).Status);
    }

    // ---- Decisions ----

    /// <summary>Acceptance scenario 5: a decision pauses execution, and a linked answer resumes
    /// it without losing context.</summary>
    [Fact]
    public async Task Decision_StopsTheRun_AndTheAnswerResumesTheSameThreadSession()
    {
        _host.Workers.Script.Enqueue(async call =>
        {
            _host.Workers.WorkspaceOf(call.Worker).Write("src/Orders.cs", "half done\n");
            await call.Worker.SubmitAsync(call.SessionId, new DecisionSubmission(
                "Should export include all filtered rows or only the current page?", "The request does not say.", ["All filtered rows", "Current page"], "All filtered rows"));
            // The model was told to stop; the host ends the turn.
            await Task.Delay(Timeout.Infinite, call.Token);
            return FakeWorkerLauncher.Done();
        });
        var (_, threadId) = await DelegatedAsync();

        var waiting = await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingDecision);
        var decision = Assert.Single(waiting.Decisions);
        Assert.Equal(DecisionStatus.Open, decision.Status);
        Assert.Contains("Decision needed: Should export include all filtered rows", Assert.Single(Texts(waiting, MessageKind.Decision)));
        Assert.Equal(decision.Id, waiting.Messages.Single(m => m.Kind == MessageKind.Decision).DecisionId);
        Assert.Equal(1, await LeaseCountAsync()); // half-done work holds the repository

        await _host.PostAsync($"api/decisions/{decision.Id}/answer", new { answer = "All filtered rows." }, HttpStatusCode.OK);
        var done = await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        Assert.Equal([TurnKind.Implement, TurnKind.Implement, TurnKind.Review], TurnKinds);
        var answerTurn = _host.Workers.Turns.ElementAt(1);
        Assert.Contains("> All filtered rows.", answerTurn.Brief);
        Assert.Equal(done.Thread.SessionId, answerTurn.SessionId);          // the same conversation
        Assert.Equal(_host.Workers.Turns.First().SessionId, answerTurn.SessionId);
        Assert.Equal(DecisionStatus.Answered, Assert.Single(done.Decisions).Status);
    }

    [Fact]
    public async Task AnswerToADecisionAlreadyAnswered_Is409()
    {
        _host.Workers.Script.Enqueue(async call =>
        {
            await call.Worker.SubmitAsync(call.SessionId, new DecisionSubmission("Q?", "why", ["a", "b"]));
            await Task.Delay(Timeout.Infinite, call.Token);
            return FakeWorkerLauncher.Done();
        });
        var (_, threadId) = await DelegatedAsync();
        var decision = Assert.Single((await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingDecision)).Decisions);
        await _host.PostAsync($"api/decisions/{decision.Id}/answer", new { answer = "a" }, HttpStatusCode.OK);

        await _host.PostAsync($"api/decisions/{decision.Id}/answer", new { answer = "b" }, HttpStatusCode.Conflict);
    }

    // ---- No completion call ----

    [Fact]
    public async Task TurnThatEndsWithoutSubmitting_IsNudgedOnce_ThenBlocked()
    {
        for (var i = 0; i < 2; i++)
        {
            _host.Workers.Script.Enqueue(call =>
            {
                _host.Workers.WorkspaceOf(call.Worker).Write("src/Orders.cs", $"edit {Guid.NewGuid()}\n");
                return Task.FromResult(FakeWorkerLauncher.Done()); // edits, but never calls submit_work
            });
        }

        var (_, threadId) = await DelegatedAsync();
        var details = await _host.WaitForStateAsync(threadId, LifecycleState.Blocked);

        Assert.Equal([TurnKind.Implement, TurnKind.Nudge], TurnKinds);
        Assert.Contains("You stopped without calling `submit_work` or `request_decision`.", _host.Workers.Turns.Last().Brief);
        Assert.Equal(StopReason.NoCompletionCall, details.LatestRun!.StopReason);
    }

    [Fact]
    public async Task TurnThatChangesNothingAndSubmitsNothing_IsBlockedAtOnce()
    {
        _host.Workers.Script.Enqueue(_ => Task.FromResult(FakeWorkerLauncher.Done()));
        var (_, threadId) = await DelegatedAsync();

        var details = await _host.WaitForStateAsync(threadId, LifecycleState.Blocked);

        Assert.Equal([TurnKind.Implement], TurnKinds);
        Assert.Equal(StopReason.NoProgress, details.LatestRun!.StopReason);
    }

    // ---- Submissions are checked against the turn ----

    [Fact]
    public async Task SubmissionOfTheWrongKind_IsRefused_AndDoesNotAdvanceTheRun()
    {
        SubmissionResponse? refused = null;
        _host.Workers.Script.Enqueue(async call =>
        {
            // An implement turn trying to declare its own review clean.
            refused = await call.Worker.SubmitAsync(call.SessionId, new ReviewSubmission([]));
            return await _host.DefaultTurnAsync(call);
        });
        var (_, threadId) = await DelegatedAsync();
        await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        Assert.False(refused!.Accepted);
        Assert.Equal("submit_review is not valid in a Implement turn.", refused.Message);
        Assert.Equal([TurnKind.Implement, TurnKind.Review], TurnKinds); // the real review still ran
    }

    [Fact]
    public async Task SubmissionForAnotherSession_OrWithTheWrongSecret_IsRefused()
    {
        SubmissionResponse? otherSession = null;
        HttpStatusCode? wrongSecret = null;
        _host.Workers.Script.Enqueue(async call =>
        {
            otherSession = await call.Worker.SubmitAsync("some-other-session", FakeWorkerLauncher.Work());

            using var http = new HttpClient { BaseAddress = new Uri(call.Worker.Launch.HostUrl) };
            http.DefaultRequestHeaders.Add(FactoryWire.SecretHeader, new string('0', 64));
            using var response = await http.PostAsync(FactoryWire.SubmissionsPath(call.Worker.Launch.RunId), new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
            wrongSecret = response.StatusCode;

            return await _host.DefaultTurnAsync(call);
        });
        var (_, threadId) = await DelegatedAsync();
        await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        Assert.False(otherSession!.Accepted);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongSecret);
    }

    // ---- Budget ----

    /// <summary>Acceptance scenarios 6 and 7: an unaffordable call is refused before sending and
    /// the run pauses; raising the cap preserves usage and resumes the same task.</summary>
    [Fact]
    public async Task UnaffordableModelCall_PausesForBudget_AndRaisingTheCapResumes()
    {
        List<GatewayEvent>? refused = null;
        _host.Workers.Script.Enqueue(async call =>
        {
            refused = await call.Worker.CallModelAsync(call.SessionId, new string('x', 60_000)); // about 15,000 tokens
            return new TurnStreamResult(false, 0, ((GatewayError)refused[0]).Message);
        });
        var (_, threadId) = await DelegatedAsync(cap: 20_000);

        var paused = await _host.WaitForStateAsync(threadId, LifecycleState.PausedBudget);

        Assert.Equal(GatewayErrorCodes.BudgetExhausted, Assert.IsType<GatewayError>(Assert.Single(refused!)).Code);
        Assert.Empty(_host.Provider.Requests); // nothing was sent
        Assert.Contains("the task's budget has 20,000 left", paused.Thread.StateReason);
        Assert.Equal(StopReason.BudgetExhausted, paused.LatestRun!.StopReason);

        // Posting @factory again does not get around it.
        var bypass = await _host.DelegateAsync(threadId, "@factory just carry on", expected: HttpStatusCode.Conflict);
        Assert.Contains("budget", bypass.GetProperty("error").GetString());

        await _host.PostAsync($"api/threads/{threadId}/budget", new { cap = 500_000 }, HttpStatusCode.OK);
        await _host.PostAsync($"api/threads/{threadId}/resume", null, HttpStatusCode.OK);
        var done = await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        Assert.Equal([TurnKind.Implement, TurnKind.Implement, TurnKind.Review], TurnKinds);
        Assert.Contains("This run was stopped and has been resumed.", _host.Workers.Turns.ElementAt(1).Brief);
        Assert.Equal(500_000, done.Thread.BudgetCap);
    }

    [Fact]
    public async Task ModelCallsThroughTheGateway_AreChargedToTheTask()
    {
        _host.Provider.EnqueueReply("thinking done", new Litos.Agent.Streaming.UsageInfo(12_000, 800));
        _host.Workers.Script.Enqueue(async call =>
        {
            await call.Worker.CallModelAsync(call.SessionId, "do the work");
            return await _host.DefaultTurnAsync(call);
        });
        var (_, threadId) = await DelegatedAsync(cap: 300_000);

        var details = await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        Assert.Equal(12_800, details.Thread.TokensUsed);
        Assert.Equal(0, details.Thread.TokensReserved);
        var entry = Assert.Single(await _host.Store.ListUsageAsync(threadId, default));
        Assert.Equal(details.LatestRun!.Id, entry.RunId);
    }

    // ---- Pause and cancel ----

    private TaskCompletionSource HangTheFirstTurn()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _host.Workers.Script.Enqueue(async call =>
        {
            _host.Workers.WorkspaceOf(call.Worker).Write("src/Orders.cs", "in progress\n");
            started.SetResult();
            await Task.Delay(Timeout.Infinite, call.Token);
            return FakeWorkerLauncher.Done();
        });
        return started;
    }

    [Fact]
    public async Task PauseARunningTask_StopsTheTurn_AndResumeContinuesIt()
    {
        var started = HangTheFirstTurn();
        var (_, threadId) = await DelegatedAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(20));

        await _host.PostAsync($"api/threads/{threadId}/pause", null, HttpStatusCode.Accepted);
        var paused = await _host.WaitForStateAsync(threadId, LifecycleState.PausedUser);
        Assert.Equal(StopReason.PausedByUser, paused.LatestRun!.StopReason);
        Assert.Equal(1, await LeaseCountAsync());

        await _host.PostAsync($"api/threads/{threadId}/resume", null, HttpStatusCode.OK);
        await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        Assert.Equal([TurnKind.Implement, TurnKind.Implement, TurnKind.Review], TurnKinds);
    }

    [Fact]
    public async Task CancelARunningTask_StopsIt_KeepsTheEdits_AndFreesTheRepository()
    {
        var started = HangTheFirstTurn();
        var (projectId, threadId) = await DelegatedAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(20));

        await _host.PostAsync($"api/threads/{threadId}/cancel", null, HttpStatusCode.Accepted);
        var cancelled = await _host.WaitForStateAsync(threadId, LifecycleState.Cancelled);

        Assert.Equal((RunStatus.Finished, StopReason.Cancelled), (cancelled.LatestRun!.Status, cancelled.LatestRun.StopReason));
        Assert.Equal("in progress\n", _host.Workspaces.Of(projectId).Files["src/Orders.cs"]); // never reverted
        Assert.Empty(_host.Workspaces.Of(projectId).Pushed);
        Assert.Equal(0, await LeaseCountAsync());
        await _host.PostAsync($"api/threads/{threadId}/resume", null, HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task FollowUpWhileRunning_IsRecorded_AndSteeredIntoTheTurn_NotASecondRun()
    {
        var started = HangTheFirstTurn();
        var (_, threadId) = await DelegatedAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(20));

        var result = await _host.DelegateAsync(threadId, "@factory Also handle an empty result.");

        Assert.Equal("FollowUp", result.GetProperty("outcome").GetString());
        Assert.Equal(["Also handle an empty result."], Assert.Single(_host.Workers.Workers).Steered);
        Assert.Equal(2, (await _host.ThreadAsync(threadId)).Messages.Count(m => m.Author == MessageAuthor.User));
        await _host.PostAsync($"api/threads/{threadId}/cancel", null, HttpStatusCode.Accepted);
        await _host.WaitForStateAsync(threadId, LifecycleState.Cancelled);
    }

    // ---- Failures around the run ----

    [Fact]
    public async Task PreflightFailure_BlocksWithTheReason_AndResumeTriesAgain()
    {
        var projectId = await _host.RegisterProjectAsync();
        _host.Workspaces.Of(projectId).FailFetch = new WorkspaceException("git fetch failed (exit code 128): could not resolve host github.com");
        var threadId = await _host.CreateThreadAsync(projectId);
        await _host.DelegateAsync(threadId);

        var blocked = await _host.WaitForStateAsync(threadId, LifecycleState.Blocked);
        Assert.Contains("could not resolve host", blocked.Thread.StateReason);
        Assert.Empty(_host.Workers.Turns);

        _host.Workspaces.Of(projectId).FailFetch = null;
        await _host.PostAsync($"api/threads/{threadId}/resume", null, HttpStatusCode.OK);
        await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);
    }

    /// <summary>§16: a push failure blocks with the reason; local commits are kept and the push
    /// is retried on Resume.</summary>
    [Fact]
    public async Task PushFailure_Blocks_AndResumeRetriesTheHandoff_WithoutRedoingTheWork()
    {
        var projectId = await _host.RegisterProjectAsync();
        _host.Workspaces.Of(projectId).FailPush.Enqueue(new WorkspaceException("git push failed (exit code 1): protected branch"));
        var threadId = await _host.CreateThreadAsync(projectId);
        await _host.DelegateAsync(threadId);

        var blocked = await _host.WaitForStateAsync(threadId, LifecycleState.Blocked);
        Assert.Equal(StopReason.HandoffFailed, blocked.LatestRun!.StopReason);
        Assert.Contains("protected branch", blocked.Thread.StateReason);
        Assert.Single(_host.Workspaces.Of(projectId).Commits);

        await _host.PostAsync($"api/threads/{threadId}/resume", null, HttpStatusCode.OK);
        var done = await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        Assert.Equal([TurnKind.Implement, TurnKind.Review], TurnKinds); // no turn was run again
        Assert.Single(_host.Workspaces.Of(projectId).Pushed);
        Assert.Single(Texts(done, MessageKind.Handoff));
    }

    [Fact]
    public async Task DraftPrFailure_DoesNotFailTheHandoff_ButIsDisclosed()
    {
        _host.GitHub.Fail = new HttpRequestException("GitHub refused to create the draft pull request (HTTP 403).");
        var (_, threadId) = await DelegatedAsync();

        var details = await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        var handoff = Assert.Single(Texts(details, MessageKind.Handoff));
        Assert.DoesNotContain("draft PR #", handoff);
        Assert.Contains("The draft PR could not be opened", handoff);
        Assert.Null(details.Thread.PullRequestNumber);
    }

    [Fact]
    public async Task WorkerCannotBeStarted_BlocksTheRun_WithTheReason()
    {
        _host.Workers.FailLaunch = new InvalidOperationException("The worker executable was not found.");
        var (_, threadId) = await DelegatedAsync();

        var details = await _host.WaitForStateAsync(threadId, LifecycleState.Blocked);

        Assert.Contains("The worker executable was not found.", details.Thread.StateReason);
    }

    [Fact]
    public async Task TurnThatFaults_BlocksWithTheWorkersError()
    {
        _host.Workers.Script.Enqueue(_ => Task.FromResult(new TurnStreamResult(false, 2, "Compaction failed: the provider returned 500.")));
        var (_, threadId) = await DelegatedAsync();

        var details = await _host.WaitForStateAsync(threadId, LifecycleState.Blocked);

        Assert.Equal(StopReason.TurnFaulted, details.LatestRun!.StopReason);
        Assert.Equal("Compaction failed: the provider returned 500.", details.Thread.StateReason);
    }

    // ---- Rework ----

    /// <summary>Acceptance scenario 4: rework re-acquires the lock and adds commits to the same branch.</summary>
    [Fact]
    public async Task ReworkAfterHandoff_RunsOnTheSameBranchAndSession_AndAddsACommit()
    {
        var (projectId, threadId) = await DelegatedAsync();
        var first = await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        await _host.DelegateAsync(threadId, "@factory CSV values containing commas are incorrect. Fix this.");
        var second = await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        Assert.Equal([TurnKind.Implement, TurnKind.Review, TurnKind.Rework, TurnKind.Review], TurnKinds);
        var rework = _host.Workers.Turns.ElementAt(2);
        Assert.Contains("CSV values containing commas are incorrect. Fix this.", rework.Brief);
        Assert.Contains("Added CSV export for Orders.", rework.Brief);   // the previous handoff
        Assert.Contains("- `src/Orders.cs`", rework.Brief);
        Assert.Equal(first.Thread.SessionId, rework.SessionId);

        var workspace = _host.Workspaces.Of(projectId);
        Assert.Equal(first.Thread.Branch, second.Thread.Branch);
        Assert.Equal(2, workspace.Commits.Count);
        Assert.Single(workspace.Calls, c => c.StartsWith("branch "));       // the branch was created once
        Assert.NotEqual(first.LatestRun!.Id, second.LatestRun!.Id);
        Assert.Equal(RunKind.Rework, second.LatestRun.Kind);
    }

    /// <summary>Acceptance scenario 15: Accept records approval without merging or deploying.</summary>
    [Fact]
    public async Task Accept_AfterHandoff_ClosesTheTask_AndTouchesNoRepository()
    {
        var (projectId, threadId) = await DelegatedAsync();
        await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);
        var pushedBefore = _host.Workspaces.Of(projectId).Pushed.Count;

        var accepted = await _host.PostAsync($"api/threads/{threadId}/accept", null, HttpStatusCode.OK);

        Assert.Equal("Accepted", accepted.GetProperty("state").GetString());
        Assert.Equal("Done", accepted.GetProperty("stage").GetString());
        Assert.Equal(pushedBefore, _host.Workspaces.Of(projectId).Pushed.Count);
        await _host.DelegateAsync(threadId, "@factory one more thing", expected: HttpStatusCode.Conflict);
    }

    // ---- Concurrency ----

    /// <summary>Acceptance scenario 3: two threads on the same repository never run at the same time.</summary>
    [Fact]
    public async Task TwoThreadsOnOneRepository_RunOneAfterTheOther()
    {
        var started = HangTheFirstTurn();
        var (projectId, first) = await DelegatedAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var second = await _host.CreateThreadAsync(projectId, "Fix the footer");
        await _host.DelegateAsync(second);
        await Task.Delay(500);

        Assert.Equal(LifecycleState.Queued, (await _host.ThreadAsync(second)).Thread.State);
        Assert.Single(_host.Workers.Workers);

        await _host.PostAsync($"api/threads/{first}/cancel", null, HttpStatusCode.Accepted);
        await _host.WaitForStateAsync(second, LifecycleState.AwaitingHumanTesting);
    }

    // ---- Events ----

    [Fact]
    public async Task ARun_LeavesAReplayableTrailOfEvents()
    {
        var (_, threadId) = await DelegatedAsync();
        await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        var events = await _host.Store.ReadEventsAsync(threadId, 0, 500, default);
        var states = events.Where(e => e.Type == EventTypes.StateChanged)
            .Select(e => JsonDocument.Parse(e.PayloadJson).RootElement)
            .Select(p => $"{p.GetProperty("stage").GetString()}/{p.GetProperty("state").GetString()}")
            .Distinct().ToList();

        Assert.Equal(
            ["Implement/Queued", "Implement/Running", "Verify/Running", "Review/Running", "Handoff/Running", "Handoff/AwaitingHumanTesting"],
            states);
        Assert.Contains(events, e => e.Type == EventTypes.MessageAdded && e.PayloadJson.Contains("Ready for human testing."));
    }

    [Fact]
    public void BranchName_IsFactoryShortIdSlug()
    {
        var thread = new TaskThread
        {
            Id = Guid.Parse("7f3a1111-2222-3333-4444-555566667777"), Title = "Add CSV export — for Orders!!", SessionId = "s", Provider = "p", Model = "m",
        };

        Assert.Equal("factory/7f3a-add-csv-export-for-orders", RunExecutor.BranchName(thread));
        Assert.Equal("factory/7f3a-task", RunExecutor.BranchName(new TaskThread { Id = thread.Id, Title = "!!!", SessionId = "s", Provider = "p", Model = "m" }));
    }
}

/// <summary>What the host does on startup with runs a previous host left in progress (§16).</summary>
public sealed class RecoveryTests
{
    /// <summary>Acceptance scenario 12: a host restart recovers task state without re-running work.</summary>
    [Fact]
    public async Task RunLeftRunningByAPreviousHost_IsMarkedInterrupted_AndRecoverContinuesIt()
    {
        await using var host = await TestHost.StartAsync(startCoordinator: false);
        var threadId = await host.CreateThreadAsync(await host.RegisterProjectAsync());
        await host.DelegateAsync(threadId);
        var claimed = (await host.Store.ClaimNextRunAsync(1, DateTimeOffset.UtcNow, default))!;
        await host.Store.SetRunWorkerAsync(claimed.Run.Id, int.MaxValue - 5, DateTimeOffset.UtcNow.AddHours(-1), default); // a worker that is gone

        await host.App.Services.GetRequiredService<RunCoordinator>().RecoverAsync(default);

        var details = await host.ThreadAsync(threadId);
        Assert.Equal(LifecycleState.Interrupted, details.Thread.State);
        Assert.Equal((RunStatus.Suspended, StopReason.Interrupted), (details.LatestRun!.Status, details.LatestRun.StopReason));
        Assert.Contains("The factory host stopped while this task was running", details.Thread.StateReason);
        Assert.Empty(host.Workers.Turns); // nothing was re-run

        var recovered = await host.PostAsync($"api/threads/{threadId}/resume", null, HttpStatusCode.OK);
        Assert.Equal("Queued", recovered.GetProperty("state").GetString());
    }

    /// <summary>
    /// The first real run stopped here. A run cut off mid-turn has a checkpoint that says "a turn
    /// is in progress", not a stop with a resume point; recovering it must continue that turn
    /// and carry the run through to a handoff.
    /// </summary>
    [Fact]
    public async Task RunCutOffMidTurn_Recovered_ContinuesTheTurn_AndFinishes()
    {
        await using var host = await TestHost.StartAsync(startCoordinator: false);
        var threadId = await host.CreateThreadAsync(await host.RegisterProjectAsync());
        await host.DelegateAsync(threadId);
        var claimed = (await host.Store.ClaimNextRunAsync(1, DateTimeOffset.UtcNow, default))!;

        // The checkpoint a real host leaves behind while the implement turn is running.
        var orchestrator = new RunOrchestrator();
        var midTurn = orchestrator.Next(RunOrchestrator.NewRun(RunKind.Implement), new RunStarted(), DateTimeOffset.UtcNow).State;
        midTurn = orchestrator.Next(midTurn, new PreflightCompleted(true), DateTimeOffset.UtcNow).State;
        Assert.Equal(RunPhase.Turn, midTurn.Phase);
        await host.Store.SaveCheckpointAsync(claimed.Run.Id, RunStateJson.Serialize(midTurn), Stage.Implement, DateTimeOffset.UtcNow, default);
        await host.Store.SetRunWorkerAsync(claimed.Run.Id, int.MaxValue - 5, DateTimeOffset.UtcNow.AddHours(-1), default);

        await host.App.Services.GetRequiredService<RunCoordinator>().RecoverAsync(default);
        Assert.Equal(LifecycleState.Interrupted, (await host.ThreadAsync(threadId)).Thread.State);
        await host.PostAsync($"api/threads/{threadId}/resume", null, HttpStatusCode.OK);

        var again = (await host.Store.ClaimNextRunAsync(1, DateTimeOffset.UtcNow, default))!;
        Assert.Equal(RunEntry.Resume, again.Run.Entry);
        await host.App.Services.GetRequiredService<RunExecutor>().ExecuteAsync(again, default);

        var details = await host.ThreadAsync(threadId);
        Assert.Equal(LifecycleState.AwaitingHumanTesting, details.Thread.State);
        Assert.Null(details.Thread.StateReason);
        Assert.Equal([TurnKind.Implement, TurnKind.Review], host.Workers.Turns.Select(t => t.Kind));
        // The continued turn is told it was interrupted and to re-check the working copy.
        Assert.Contains("stopped and has been resumed", host.Workers.Turns.First().Brief);
    }

    /// <summary>A run that was claimed but cut off before its first checkpoint has nothing to
    /// continue: recovering it starts it.</summary>
    [Fact]
    public async Task RunCutOffBeforeItsFirstCheckpoint_Recovered_Starts()
    {
        await using var host = await TestHost.StartAsync(startCoordinator: false);
        var threadId = await host.CreateThreadAsync(await host.RegisterProjectAsync());
        await host.DelegateAsync(threadId);
        var claimed = (await host.Store.ClaimNextRunAsync(1, DateTimeOffset.UtcNow, default))!;
        await host.Store.SetRunWorkerAsync(claimed.Run.Id, int.MaxValue - 5, DateTimeOffset.UtcNow.AddHours(-1), default);
        await host.App.Services.GetRequiredService<RunCoordinator>().RecoverAsync(default);
        await host.PostAsync($"api/threads/{threadId}/resume", null, HttpStatusCode.OK);

        var again = (await host.Store.ClaimNextRunAsync(1, DateTimeOffset.UtcNow, default))!;
        await host.App.Services.GetRequiredService<RunExecutor>().ExecuteAsync(again, default);

        Assert.Equal(LifecycleState.AwaitingHumanTesting, (await host.ThreadAsync(threadId)).Thread.State);
        Assert.DoesNotContain("has been resumed", host.Workers.Turns.First().Brief);
    }

    [Fact]
    public async Task Recovery_WithNothingRunning_DoesNothing()
    {
        await using var host = await TestHost.StartAsync(startCoordinator: false);
        var threadId = await host.CreateThreadAsync(await host.RegisterProjectAsync());
        await host.DelegateAsync(threadId);

        await host.App.Services.GetRequiredService<RunCoordinator>().RecoverAsync(default);

        Assert.Equal(LifecycleState.Queued, (await host.ThreadAsync(threadId)).Thread.State);
    }
}
