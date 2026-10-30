using System.Net;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Orchestration;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Store;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>
/// A person's @factory message to a task that is working. First (m2-architecture.md §10.1, found on
/// check 5): the agent answered "what are you working on now?" and ended its turn, the turn had
/// changed nothing yet, so the run was blocked as making no progress, and the answer was never
/// shown. Then (§5.4): one that no work turn can take when it is sent is held for the next one.
/// </summary>
public sealed class FollowUpTests
{
    private const string Question = "what are you working on now?";
    private const string Answer = "Adding the CSV export to OrdersController; tests next.";

    /// <summary>An implement turn that waits for a follow-up, then answers it and stops, as the model did.</summary>
    private static TaskCompletionSource AnswerTheFollowUpAndStop(TestHost host)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Workers.Script.Enqueue(async call =>
        {
            started.TrySetResult();
            while (call.Worker.Steered.Count == 0)
                await Task.Delay(20, call.Token);
            return new TurnStreamResult(true, 2, null, Answer);
        });
        return started;
    }

    private static async Task<Guid> WorkingThreadAsync(TestHost host, TaskCompletionSource started)
    {
        var threadId = await host.CreateThreadAsync(await host.RegisterProjectAsync());
        await host.DelegateAsync(threadId);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(20));
        return threadId;
    }

    [Fact]
    public async Task AFollowUp_ReachesTheAgent_AsAnAsideToAnswerWithoutStopping()
    {
        await using var host = await TestHost.StartAsync();
        var threadId = await WorkingThreadAsync(host, AnswerTheFollowUpAndStop(host));

        var said = await host.DelegateAsync(threadId, "@factory " + Question);

        Assert.Equal("FollowUp", said.GetProperty("outcome").GetString());
        Assert.True(said.GetProperty("delivered").GetBoolean());
        var steered = Assert.Single(Assert.Single(host.Workers.Workers).Steered);
        Assert.Contains(Question, steered);
        Assert.Contains("Do not stop the work just to answer.", steered);
        await host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);
    }

    [Fact]
    public async Task ATurnThatStoppedToAnswer_PostsItsAnswer_AndTheWorkCarriesOnToAHandoff()
    {
        await using var host = await TestHost.StartAsync();
        var threadId = await WorkingThreadAsync(host, AnswerTheFollowUpAndStop(host));

        await host.DelegateAsync(threadId, "@factory " + Question);

        var details = await host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);
        var messages = details.Messages.OrderBy(m => m.Sequence).ToList();
        var asked = messages.FindIndex(m => m.Author == MessageAuthor.User && m.Text == Question);
        var answered = messages.FindIndex(m => m.Author == MessageAuthor.Factory && m.Kind == MessageKind.Text && m.Text == Answer);
        Assert.True(asked >= 0 && answered > asked, "The answer is posted after the question.");
        Assert.DoesNotContain(messages, m => m.Text.Contains("changed no files", StringComparison.Ordinal));

        // The next turn carried the implementation on, with its own brief, and nothing was nudged.
        var turns = host.Workers.Turns.ToList();
        Assert.Equal(TurnKind.Implement, turns[^3].Kind); // the turn that answered
        Assert.Equal(TurnKind.Implement, turns[^2].Kind);
        Assert.Contains("Your answer to the person has been posted on the thread.", turns[^2].Brief);
        Assert.DoesNotContain(turns, t => t.Kind == TurnKind.Nudge);
        Assert.Equal(StopReason.HandedOff, details.LatestRun!.StopReason);
    }

    [Fact]
    public async Task TheSameEmptyTurn_WithNoFollowUp_StillBlocksAsBefore()
    {
        await using var host = await TestHost.StartAsync();
        host.Workers.Script.Enqueue(_ => Task.FromResult(new TurnStreamResult(true, 2, null, Answer)));
        var threadId = await host.CreateThreadAsync(await host.RegisterProjectAsync());

        await host.DelegateAsync(threadId);

        var details = await host.WaitForStateAsync(threadId, LifecycleState.Blocked);
        Assert.Equal(StopReason.NoProgress, details.LatestRun!.StopReason);
        Assert.DoesNotContain(details.Messages, m => m.Author == MessageAuthor.Factory && m.Text == Answer);
    }

    // ---- Held @factory messages (m2-architecture.md §5.4) ----

    private static async Task<SteerStatus?> SteerStatusAsync(TestHost host, Guid threadId, string text) =>
        SteerPayload.Read((await host.ThreadAsync(threadId)).Messages.Single(m => m.Text == text).PayloadJson);

    [Fact]
    public async Task ASteerToAQueuedTask_IsHeld_ThenCarriedByItsFirstWorkTurn()
    {
        await using var host = await TestHost.StartAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Workers.Script.Enqueue(async call =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, call.Token);
            return FakeWorkerLauncher.Done();
        });
        var project = await host.RegisterProjectAsync();
        var first = await host.CreateThreadAsync(project);
        await host.DelegateAsync(first);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var second = await host.CreateThreadAsync(project, "Another task");
        await host.DelegateAsync(second); // waits: the one slot is busy

        var said = await host.DelegateAsync(second, "@factory Use semicolons, not commas.");

        Assert.Equal("FollowUp", said.GetProperty("outcome").GetString());
        Assert.False(said.GetProperty("delivered").GetBoolean());
        Assert.Equal(SteerStatus.Held, await SteerStatusAsync(host, second, "Use semicolons, not commas."));

        await host.PostAsync($"api/threads/{first}/cancel", null, HttpStatusCode.Accepted);
        await host.WaitForStateAsync(second, LifecycleState.AwaitingHumanTesting);

        var implement = host.Workers.Turns.Last(t => t.Kind == TurnKind.Implement);
        Assert.Contains("Use semicolons, not commas.", implement.Brief);
        Assert.Contains("Do not stop the work just to answer.", implement.Brief);
        Assert.Equal(SteerStatus.Delivered, await SteerStatusAsync(host, second, "Use semicolons, not commas."));
        // It went in the brief, not through the worker's steering.
        Assert.DoesNotContain(host.Workers.Workers.SelectMany(w => w.Steered), s => s.Contains("Use semicolons"));
    }

    [Fact]
    public async Task ASteerNoWorkTurnTakes_IsDroppedAtTheHandoff_AndTheThreadSaysSo()
    {
        await using var host = await TestHost.StartAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Workers.Script.Enqueue(async call =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(call.Token);
            return await host.Workers.Default(call);
        });
        var threadId = await host.CreateThreadAsync(await host.RegisterProjectAsync());
        await host.DelegateAsync(threadId);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(20));
        host.Workers.RefuseSteering = true; // as a worker whose turn has just ended

        var said = await host.DelegateAsync(threadId, "@factory Also handle an empty result.");
        Assert.False(said.GetProperty("delivered").GetBoolean());
        release.SetResult();

        var details = await host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);
        Assert.Equal(SteerStatus.Dropped, await SteerStatusAsync(host, threadId, "Also handle an empty result."));
        Assert.Contains(details.Messages, m => m.Kind == MessageKind.Status && m.Text.Contains("did not reach the agent") && m.Text.Contains("Request changes"));
    }

    [Fact]
    public async Task ASteerTheTurnEndedWithoutReading_IsHeldAgain()
    {
        await using var host = await TestHost.StartAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Workers.Script.Enqueue(async call =>
        {
            started.TrySetResult();
            while (call.Worker.Steered.Count == 0)
                await Task.Delay(20, call.Token);
            // The worker took it as the turn ended, after its last look for steering. The turn
            // edited a file but submitted nothing, so the next turn is a nudge: still work.
            host.Workers.UnreadSteering.AddRange(call.Worker.Steered);
            host.Workers.WorkspaceOf(call.Worker).Write("src/Orders.cs", "half done\n");
            return new TurnStreamResult(true, 2, null, Answer);
        });
        var threadId = await host.CreateThreadAsync(await host.RegisterProjectAsync());
        await host.DelegateAsync(threadId);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(20));

        await host.DelegateAsync(threadId, "@factory Keep it small.");

        // Never read, so it goes into the next work turn's brief instead, and the turn's last words
        // are not posted as an answer to it.
        var details = await host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);
        Assert.Contains(host.Workers.Turns.Skip(1), t => t.Kind != TurnKind.Review && t.Brief.Contains("Keep it small."));
        Assert.Equal(SteerStatus.Delivered, await SteerStatusAsync(host, threadId, "Keep it small."));
        Assert.DoesNotContain(details.Messages, m => m.Author == MessageAuthor.Factory && m.Kind == MessageKind.Text && m.Text == Answer);
    }
}
