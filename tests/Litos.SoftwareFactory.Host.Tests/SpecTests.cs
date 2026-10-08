using System.Net;
using System.Text.Json;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Orchestration;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Host.Api;
using Litos.SoftwareFactory.Host.Runs;
using Litos.SoftwareFactory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>
/// The spec stage (docs/software-factory/m2-architecture.md §5): "@factory spec" asks for a
/// specification, written by one read-only turn on the reading copy; a person approves it, and
/// only then does @factory build it, held to its criteria.
/// </summary>
public sealed class SpecTests
{
    private const string AskForSpec = "@factory spec Export the orders list as CSV.";

    private static async Task<(TestHost Host, Guid ProjectId, Guid ThreadId)> StartAsync(Action<FactoryOptions>? configure = null)
    {
        var host = await TestHost.StartAsync(configure);
        var projectId = await host.RegisterProjectAsync();
        return (host, projectId, await host.CreateThreadAsync(projectId));
    }

    /// <summary>Waits for the spec to be proposed: the task is a draft at the Spec stage again.</summary>
    private static async Task<ThreadDetails> ProposedAsync(TestHost host, Guid threadId, int revision = 1)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var details = await host.ThreadAsync(threadId);
            if (details.LatestSpec?.Revision == revision && details.Thread.State == LifecycleState.Draft
                && host.App.Services.GetRequiredService<RunRegistry>().Count == 0)
            {
                return details;
            }

            if (DateTime.UtcNow > deadline)
                Assert.Fail($"No revision {revision}. Thread is {details.Thread.State}: {details.Thread.StateReason}");
            await Task.Delay(40);
        }
    }

    private static async Task<int> LeaseCountAsync(TestHost host)
    {
        await using var db = await host.App.Services.GetRequiredService<IDbContextFactory<FactoryDbContext>>().CreateDbContextAsync();
        return await db.Leases.CountAsync();
    }

    /// <summary>A spec turn that waits until it is stopped.</summary>
    private static TaskCompletionSource HoldSpec(TestHost host)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Workers.Script.Enqueue(async call =>
        {
            Assert.Equal(TurnKind.Spec, call.Kind);
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, call.Token);
            return FakeWorkerLauncher.Done();
        });
        return started;
    }

    // ---- Asking for a specification ----

    [Fact]
    public async Task AtFactorySpec_WritesASpecification_OnTheReadingCopy_AndWaitsForYou()
    {
        var (host, projectId, threadId) = await StartAsync();
        await using var _ = host;

        var result = await host.DelegateAsync(threadId, AskForSpec);

        Assert.Equal("Queued", result.GetProperty("outcome").GetString());
        var details = await ProposedAsync(host, threadId);
        Assert.Equal((LifecycleState.Draft, Stage.Spec), (details.Thread.State, details.Thread.Stage));
        Assert.Equal((RunKind.Spec, RunStatus.Finished), (details.LatestRun!.Kind, details.LatestRun.Status));

        // One read-only turn, on the reading copy at the default branch; the task's working copy
        // and the repository's lease are never touched.
        var turn = Assert.Single(host.Workers.Turns);
        Assert.Equal((TurnKind.Spec, $"spec-{details.LatestRun.Id:N}", host.Options.SpecMaxToolCalls), (turn.Kind, turn.SessionId, turn.MaxToolCalls));
        Assert.Contains("> Export the orders list as CSV.", turn.Brief);
        Assert.Contains("the default branch `main`", turn.Brief);
        Assert.Equal(["clone", "fetch", "read main"], host.Workspaces.ReadingOf(projectId).Calls);
        Assert.False(host.Workspaces.Workspaces.ContainsKey(projectId));
        Assert.Equal(0, await LeaseCountAsync(host));
        Assert.Equal(host.Workspaces.ReadingOf(projectId).Path, Assert.Single(host.Workers.Workers).Launch.WorkingCopy);

        // The proposal is a Spec message carrying the revision; the thread says it waits for you.
        var message = details.Messages.OrderBy(m => m.Sequence).Last();
        Assert.Equal((MessageAuthor.Factory, MessageKind.Spec, TestHost.DefaultSpec.Summary), (message.Author, message.Kind, message.Text));
        var view = await host.GetAsync($"api/threads/{threadId}");
        Assert.Equal("AwaitingYou", view.GetProperty("thread").GetProperty("turn").GetString());
        Assert.Equal((1, false), (view.GetProperty("spec").GetProperty("revision").GetInt32(), view.GetProperty("spec").GetProperty("approved").GetBoolean()));
    }

    [Theory]
    [InlineData("@factory spec")]
    [InlineData("@factory Spec   ")]
    public async Task AtFactorySpec_WithNothingAfterIt_Is400(string text)
    {
        var (host, _, threadId) = await StartAsync();
        await using var _ = host;

        var refusal = await host.DelegateAsync(threadId, text, expected: HttpStatusCode.BadRequest);

        Assert.Contains("after @factory spec", refusal.GetProperty("error").GetString());
        Assert.Equal(LifecycleState.Draft, (await host.ThreadAsync(threadId)).Thread.State);
    }

    /// <summary>"spec" must be a word of its own: "@factory specify ..." asks for the work.</summary>
    [Fact]
    public async Task AtFactorySpecify_IsARequestForTheWork()
    {
        var (host, _, threadId) = await StartAsync();
        await using var _ = host;

        await host.DelegateAsync(threadId, "@factory specify the CSV columns in the export");

        Assert.Equal(RunKind.Implement, (await host.ThreadAsync(threadId)).LatestRun!.Kind);
    }

    [Fact]
    public async Task AtFactorySpec_AfterTheWorkWasDelegated_Is409()
    {
        var (host, _, threadId) = await StartAsync();
        await using var _ = host;
        await host.DelegateAsync(threadId);
        await host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        var refusal = await host.DelegateAsync(threadId, AskForSpec, expected: HttpStatusCode.Conflict);

        Assert.Contains("already been delegated", refusal.GetProperty("error").GetString());
    }

    // ---- Approving, then building ----

    [Fact]
    public async Task Delegating_BeforeTheSpecIsApproved_Is409()
    {
        var (host, _, threadId) = await StartAsync();
        await using var _ = host;
        await host.DelegateAsync(threadId, AskForSpec);
        await ProposedAsync(host, threadId);

        var refusal = await host.DelegateAsync(threadId, "@factory Build it.", expected: HttpStatusCode.Conflict);

        Assert.Contains("waiting for approval", refusal.GetProperty("error").GetString());
    }

    [Fact]
    public async Task OnceApproved_AtFactoryBuildsIt_HeldToItsCriteria()
    {
        var (host, _, threadId) = await StartAsync();
        await using var _ = host;
        await host.DelegateAsync(threadId, AskForSpec);
        await ProposedAsync(host, threadId);

        var approved = await host.PostAsync($"api/threads/{threadId}/spec/1/approve", null, HttpStatusCode.OK);
        Assert.True(approved.GetProperty("approved").GetBoolean());
        Assert.Equal(await host.AdminIdAsync(), approved.GetProperty("approvedBy").GetGuid());

        var turns = host.Workers.Turns.Count;
        await host.DelegateAsync(threadId, "@factory Build it.");
        var handedOff = await host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        // The run is told the approved specification and its criteria.
        Assert.Equal(1, handedOff.LatestRun!.SpecificationRevision);
        var implement = host.Workers.Turns.Skip(turns).First(t => t.Kind == TurnKind.Implement);
        Assert.Contains("## Approved specification", implement.Brief);
        Assert.Contains(TestHost.DefaultSpec.Summary, implement.Brief);
        Assert.Contains("2. Others get a 403.", implement.Brief);
        Assert.Contains("each in exactly the words written here", implement.Brief);

        // The handoff names the revision, and the criterion the work did not report on.
        var evidence = JsonDocument.Parse(handedOff.LatestHandoff!.EvidenceJson).RootElement;
        Assert.Equal(1, evidence.GetProperty("specificationRevision").GetInt32());
        var limitations = evidence.GetProperty("knownLimitations").EnumerateArray().Select(l => l.GetString()).ToList();
        Assert.Contains("Approved criterion not reported on: Others get a 403.", limitations);
        Assert.DoesNotContain("Approved criterion not reported on: Administrators can export.", limitations);
    }

    [Fact]
    public async Task Approving_AnOldOrUnknownRevision_IsRefused()
    {
        var (host, _, threadId) = await StartAsync();
        await using var _ = host;
        await host.DelegateAsync(threadId, AskForSpec);
        await ProposedAsync(host, threadId);
        await host.DelegateAsync(threadId, "@factory spec Leave cancelled orders out.");
        await ProposedAsync(host, threadId, revision: 2);

        var stale = await host.PostAsync($"api/threads/{threadId}/spec/1/approve", null, HttpStatusCode.Conflict);
        Assert.Contains("replaced by revision 2", stale.GetProperty("error").GetString());
        await host.PostAsync($"api/threads/{threadId}/spec/7/approve", null, HttpStatusCode.NotFound);
    }

    /// <summary>A task without a specification is delegated as it always was.</summary>
    [Fact]
    public async Task WithoutASpec_TheRunBuildsNone()
    {
        var (host, _, threadId) = await StartAsync();
        await using var _ = host;

        await host.DelegateAsync(threadId);
        var details = await host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        Assert.Null(details.LatestRun!.SpecificationRevision);
        Assert.DoesNotContain("## Approved specification", host.Workers.Turns.First(t => t.Kind == TurnKind.Implement).Brief);
        Assert.False(JsonDocument.Parse(details.LatestHandoff!.EvidenceJson).RootElement.TryGetProperty("specificationRevision", out var revision)
            && revision.ValueKind != JsonValueKind.Null);
    }

    // ---- Revising ----

    [Fact]
    public async Task AskingAgain_RevisesTheDraft_WithTheThreadSoFar()
    {
        var (host, _, threadId) = await StartAsync();
        await using var _ = host;
        await host.DelegateAsync(threadId, AskForSpec);
        await ProposedAsync(host, threadId);

        await host.DelegateAsync(threadId, "@factory spec Leave cancelled orders out.");
        var details = await ProposedAsync(host, threadId, revision: 2);

        var brief = host.Workers.Turns.Last().Brief;
        Assert.Contains("> Leave cancelled orders out.", brief);
        Assert.Contains("## The draft to revise (revision 1)", brief);
        Assert.Contains("1. Administrators can export.", brief);
        Assert.Contains("**Factory:**\n> " + TestHost.DefaultSpec.Summary, brief); // the earlier proposal, in the thread
        Assert.Null(details.LatestSpec!.ApprovedAt);
    }

    // ---- Stops ----

    [Fact]
    public async Task ATurnThatProposesNothing_Blocks_AndResumingWritesItAgain()
    {
        var (host, _, threadId) = await StartAsync();
        await using var _ = host;
        host.Workers.Script.Enqueue(_ => Task.FromResult(new TurnStreamResult(true, 3, null)));

        await host.DelegateAsync(threadId, AskForSpec);
        var blocked = await host.WaitForStateAsync(threadId, LifecycleState.Blocked);

        Assert.Equal(Stage.Spec, blocked.Thread.Stage);
        Assert.StartsWith(SpecExecutor.NoSpec, blocked.Thread.StateReason);

        await host.PostAsync($"api/threads/{threadId}/resume", null, HttpStatusCode.OK);
        await ProposedAsync(host, threadId);
        Assert.Equal(2, host.Workers.Turns.Count(t => t.Kind == TurnKind.Spec));
    }

    [Fact]
    public async Task PausingTheSpecTurn_PausesTheTask_AndResumingWritesItAgain()
    {
        var (host, _, threadId) = await StartAsync();
        await using var _ = host;
        var started = HoldSpec(host);
        await host.DelegateAsync(threadId, AskForSpec);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(20));

        await host.PostAsync($"api/threads/{threadId}/pause", null, HttpStatusCode.Accepted);
        var paused = await host.WaitForStateAsync(threadId, LifecycleState.PausedUser);
        Assert.Equal(Stage.Spec, paused.Thread.Stage);

        await host.PostAsync($"api/threads/{threadId}/resume", null, HttpStatusCode.OK);
        await ProposedAsync(host, threadId);
    }

    [Fact]
    public async Task CancellingDuringTheSpecTurn_CancelsTheTask()
    {
        var (host, _, threadId) = await StartAsync();
        await using var _ = host;
        var started = HoldSpec(host);
        await host.DelegateAsync(threadId, AskForSpec);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(20));

        await host.PostAsync($"api/threads/{threadId}/cancel", null, HttpStatusCode.Accepted);

        await host.WaitForStateAsync(threadId, LifecycleState.Cancelled);
        Assert.Null((await host.ThreadAsync(threadId)).LatestSpec);
    }

    [Fact]
    public async Task AReadingCopyThatCannotBeFetched_Blocks_WithTheReason()
    {
        var (host, projectId, threadId) = await StartAsync();
        await using var _ = host;
        host.Workspaces.ReadingOf(projectId).FailFetch = new WorkspaceException("The remote could not be reached.");

        await host.DelegateAsync(threadId, AskForSpec);
        var blocked = await host.WaitForStateAsync(threadId, LifecycleState.Blocked);

        Assert.Contains("The remote could not be reached.", blocked.Thread.StateReason);
        Assert.Empty(host.Workers.Workers);
    }

    // ---- Budget, slots and liveness ----

    /// <summary>A specification is task work: its model calls are charged to the task.</summary>
    [Fact]
    public async Task ASpecsModelCalls_AreChargedToTheTask()
    {
        var (host, _, threadId) = await StartAsync();
        await using var _ = host;
        host.Workers.Script.Enqueue(async call =>
        {
            await call.Worker.CallModelAsync(call.SessionId, "what does Orders.cs do?");
            await call.Worker.SubmitAsync(call.SessionId, TestHost.DefaultSpec);
            return FakeWorkerLauncher.Done();
        });

        await host.DelegateAsync(threadId, AskForSpec);
        var details = await ProposedAsync(host, threadId);

        Assert.True(details.Thread.TokensUsed > 0);
        Assert.Contains(await host.Store.ListUsageAsync(threadId, default), u => u.Phase == "Spec");
    }

    /// <summary>It holds no lease, so it runs beside a task that holds the repository.</summary>
    [Fact]
    public async Task ASpecRun_RunsBesideATaskHoldingTheRepository()
    {
        var (host, projectId, working) = await StartAsync(o => o.SlotCap = 2);
        await using var _ = host;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Workers.Script.Enqueue(async call =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, call.Token);
            return FakeWorkerLauncher.Done();
        });
        await host.DelegateAsync(working);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var other = await host.CreateThreadAsync(projectId, "Another task");

        await host.DelegateAsync(other, AskForSpec);

        await ProposedAsyncBeside(host, other);
        Assert.Equal(LifecycleState.Running, (await host.ThreadAsync(working)).Thread.State);
        await host.PostAsync($"api/threads/{working}/cancel", null, HttpStatusCode.Accepted);
    }

    /// <summary>As <see cref="ProposedAsync"/>, while another run is still going.</summary>
    private static async Task ProposedAsyncBeside(TestHost host, Guid threadId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while ((await host.ThreadAsync(threadId)) is not { LatestSpec.Revision: 1, Thread.State: LifecycleState.Draft })
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("The spec was not proposed beside the running task.");
            await Task.Delay(40);
        }
    }

    [Fact]
    public async Task ASpecRunNoExecutorOwns_IsInterrupted_AndRecoveringWritesItAgain()
    {
        await using var host = await TestHost.StartAsync(startCoordinator: false);
        var threadId = await host.CreateThreadAsync(await host.RegisterProjectAsync());
        await host.DelegateAsync(threadId, AskForSpec);
        var orphan = (await host.Store.ClaimNextRunAsync(1, DateTimeOffset.UtcNow, default))!;
        Assert.Equal(RunKind.Spec, orphan.Run.Kind);

        Assert.Equal(1, await host.App.Services.GetRequiredService<RunLiveness>().SweepAsync(default));

        var details = await host.ThreadAsync(threadId);
        Assert.Equal((LifecycleState.Interrupted, Stage.Spec), (details.Thread.State, details.Thread.Stage));
        await host.PostAsync($"api/threads/{threadId}/resume", null, HttpStatusCode.OK);
        Assert.Equal(LifecycleState.Queued, (await host.ThreadAsync(threadId)).Thread.State);
    }
}

/// <summary>"@factory spec ..." is told apart from a request for the work.</summary>
public sealed class SpecMentionTests
{
    [Theory]
    [InlineData("spec Export orders", "Export orders")]
    [InlineData("SPEC  Export orders  ", "Export orders")]
    [InlineData("spec\nExport orders", "Export orders")]
    [InlineData("spec", "")]
    public void AWordOfItsOwn_AsksForASpecification(string text, string expected)
    {
        Assert.True(SpecMention.TryParse(text, out var request));
        Assert.Equal(expected, request);
    }

    [Theory]
    [InlineData("specify the export")]
    [InlineData("special characters in names")]
    [InlineData("Add a spec page")]
    public void AnythingElse_IsARequestForTheWork(string text)
    {
        Assert.False(SpecMention.TryParse(text, out _));
    }
}
