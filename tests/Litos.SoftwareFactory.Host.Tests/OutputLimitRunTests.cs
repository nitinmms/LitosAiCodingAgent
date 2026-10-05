using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Host.Gateway;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>On F7 a light review of one test file reasoned for 36,006 tokens without a reply, twice,
/// and the task sat blocked at its last step. A review cut off at its output limit no longer
/// blocks a change the factory has verified.</summary>
public sealed class OutputLimitRunTests : IAsyncLifetime
{
    private TestHost _host = null!;

    public async Task InitializeAsync() => _host = await TestHost.StartAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private const string CutOff = ModelGateway.CutOffPrefix + " of 32,768 tokens without producing a reply (36,006 of them were reasoning). Nothing was lost; resuming tries the step again.";

    private void Turn(Func<TurnCall, Task<TurnStreamResult>> turn) => _host.Workers.Script.Enqueue(turn);

    [Fact]
    public async Task AReviewCutOffTwice_StillHandsOff_AndTheHandoffSaysTheReviewDidNotRun()
    {
        Turn(_host.DefaultTurnAsync);                                                    // implement
        Turn(_ => Task.FromResult(new TurnStreamResult(false, 1, CutOff)));              // review, cut off
        Turn(_ => Task.FromResult(new TurnStreamResult(false, 1, CutOff)));              // review again, cut off
        var threadId = await _host.CreateThreadAsync(await _host.RegisterProjectAsync());

        await _host.DelegateAsync(threadId);
        var done = await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        Assert.Equal([TurnKind.Implement, TurnKind.Review, TurnKind.Review], _host.Workers.Turns.Select(t => t.Kind));
        var handoff = done.Messages.Single(m => m.Kind == MessageKind.Handoff);
        Assert.Contains("The agent review could not complete", handoff.PayloadJson);
        Assert.Contains("\"review\":\"NotRun\"", handoff.PayloadJson);
    }

    [Fact]
    public async Task AReviewCutOffOnce_IsRunAgain_AndItsFindingsCount()
    {
        Turn(_host.DefaultTurnAsync);
        Turn(_ => Task.FromResult(new TurnStreamResult(false, 1, CutOff)));
        Turn(async call =>
        {
            await call.Worker.SubmitAsync(call.SessionId, new ReviewSubmission([new ReviewFinding(FindingSeverity.Minor, "src/Orders.cs", 3, "A name is unclear.")]));
            return FakeWorkerLauncher.Done();
        });
        var threadId = await _host.CreateThreadAsync(await _host.RegisterProjectAsync());

        await _host.DelegateAsync(threadId);
        var done = await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        Assert.Contains(done.Findings, f => f.Text == "A name is unclear.");
        Assert.DoesNotContain(done.Messages, m => m.Text.Contains("could not complete"));
    }
}
