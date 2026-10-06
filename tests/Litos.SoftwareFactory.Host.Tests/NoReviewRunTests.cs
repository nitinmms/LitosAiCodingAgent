using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Store;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>
/// Review was a quarter of a task's tokens on average in M1, and a tiny check task spent 44% of
/// its tokens on a light review that found nothing. A small change with no risk signal, a clean
/// verification and every criterion tested is handed off without one, and the thread and the
/// handoff say so.
/// </summary>
public sealed class NoReviewRunTests : IAsyncLifetime
{
    private TestHost _host = null!;

    public async Task InitializeAsync() =>
        _host = await TestHost.StartAsync(options => options.Limits = options.Limits with { AllowNoReview = true });

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task ASmallCleanChange_IsHandedOffWithoutAReviewTurn_AndSaysWhy()
    {
        var threadId = await _host.CreateThreadAsync(await _host.RegisterProjectAsync());

        await _host.DelegateAsync(threadId);
        var done = await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        Assert.Equal([TurnKind.Implement], _host.Workers.Turns.Select(t => t.Kind));
        Assert.Contains(done.Messages, m => m.Kind == MessageKind.Status && m.Text.StartsWith("Review: none needed (risk score 0)."));
        var handoff = done.Messages.Single(m => m.Kind == MessageKind.Handoff);
        Assert.Contains("\"review\":\"NotNeeded\"", handoff.PayloadJson);
        Assert.Contains("Agent review: not needed", handoff.Text);
    }
}
