using System.Net;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Orchestration;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Store;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>
/// A person's message to a task that is working (m2-architecture.md §10.1, found on check 5): the
/// agent answered "what are you working on now?" and ended its turn, the turn had changed nothing
/// yet, so the run was blocked as making no progress, and the answer was never shown.
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

        var said = await host.DelegateAsync(threadId, Question);

        Assert.Equal("FollowUp", said.GetProperty("outcome").GetString());
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

        await host.DelegateAsync(threadId, Question);

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
}
