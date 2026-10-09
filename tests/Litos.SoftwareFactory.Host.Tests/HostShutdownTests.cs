using System.Net;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Orchestration;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Host.Runs;
using Microsoft.Extensions.DependencyInjection;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>
/// Stopping the host while a run is in a turn (§16). The M2 check found this: on Ctrl+C the web
/// server, and with it the model gateway, stops before the coordinator's token is cancelled, so
/// the turn failed with "the model gateway could not be reached" and the run was recorded Blocked
/// as if the task had failed. A run cut off by the host stopping stays Running, and the next start
/// marks it Interrupted. In these tests the turn signals application stopping itself and then
/// fails, which is that window: the host has begun to stop, and nothing has stopped the run yet.
/// </summary>
public sealed class HostShutdownTests
{
    private const string GatewayRefused =
        "The model gateway could not be reached: No connection could be made because the target machine actively refused it. (127.0.0.1:5180)";

    public static TheoryData<string> TurnFailures => new() { "faulted", "unreachable", "broken" };

    /// <summary>A turn that the host begins to stop under, and that then fails as the given kind.</summary>
    private static TaskCompletionSource StopTheHostDuringTheNextTurn(TestHost host, string failure)
    {
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Workers.Script.Enqueue(call =>
        {
            host.App.Lifetime.StopApplication();
            stopped.TrySetResult();
            return failure switch
            {
                "faulted" => Task.FromResult(new TurnStreamResult(false, 0, GatewayRefused)),
                "unreachable" => Task.FromException<TurnStreamResult>(new HttpRequestException("Connection refused")),
                "broken" => Task.FromException<TurnStreamResult>(new IOException("The response ended prematurely.")),
                _ => throw new ArgumentOutOfRangeException(nameof(failure)),
            };
        });
        return stopped;
    }

    /// <summary>Waits until no executor holds a run: whatever the run recorded, it has recorded.</summary>
    private static async Task ExecutorsFinishedAsync(TestHost host)
    {
        var registry = host.App.Services.GetRequiredService<RunRegistry>();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (registry.Count > 0)
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("The run's executor did not finish after the host began to stop.");
            await Task.Delay(40);
        }
    }

    /// <summary>What the next start does: the worker is gone, and the run is found still Running.</summary>
    private static async Task RestartAsync(TestHost host, Guid runId)
    {
        await host.Store.SetRunWorkerAsync(runId, int.MaxValue - 5, DateTimeOffset.UtcNow.AddHours(-1), default);
        await host.App.Services.GetRequiredService<RunCoordinator>().RecoverAsync(default);
    }

    [Theory]
    [MemberData(nameof(TurnFailures))]
    public async Task ATurnThatFailsAsTheHostStops_LeavesTheRunRunning_AndTheNextStartMarksItInterrupted(string failure)
    {
        await using var host = await TestHost.StartAsync();
        var threadId = await host.CreateThreadAsync(await host.RegisterProjectAsync());
        var stopped = StopTheHostDuringTheNextTurn(host, failure);

        await host.DelegateAsync(threadId);
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await ExecutorsFinishedAsync(host);

        var details = await host.ThreadAsync(threadId);
        Assert.Equal(LifecycleState.Running, details.Thread.State);
        Assert.Equal(RunStatus.Running, details.LatestRun!.Status);
        Assert.Null(details.LatestRun.StopReason);
        Assert.DoesNotContain(details.Messages, m => m.Text.Contains("could not be reached", StringComparison.Ordinal));

        await RestartAsync(host, details.LatestRun.Id);

        details = await host.ThreadAsync(threadId);
        Assert.Equal(LifecycleState.Interrupted, details.Thread.State);
        Assert.Equal((RunStatus.Suspended, StopReason.Interrupted), (details.LatestRun!.Status, details.LatestRun.StopReason));
        Assert.Contains("The factory host stopped while this task was running", details.Thread.StateReason);
        var recovered = await host.PostAsync($"api/threads/{threadId}/resume", null, HttpStatusCode.OK);
        Assert.Equal("Queued", recovered.GetProperty("state").GetString());
    }

    /// <summary>The same failure with the host running is the task's, and blocks it as before.</summary>
    [Fact]
    public async Task ATurnThatFailsWhileTheHostRuns_StillBlocksTheTask()
    {
        await using var host = await TestHost.StartAsync();
        var threadId = await host.CreateThreadAsync(await host.RegisterProjectAsync());
        host.Workers.Script.Enqueue(_ => Task.FromResult(new TurnStreamResult(false, 0, GatewayRefused)));

        await host.DelegateAsync(threadId);

        var details = await host.WaitForStateAsync(threadId, LifecycleState.Blocked);
        Assert.Equal(StopReason.TurnFaulted, details.LatestRun!.StopReason);
        Assert.Contains("could not be reached", details.Thread.StateReason);
    }

    [Fact]
    public async Task OnceTheHostIsStopping_NoNewRunIsClaimed()
    {
        await using var host = await TestHost.StartAsync();
        var threadId = await host.CreateThreadAsync(await host.RegisterProjectAsync());
        host.App.Lifetime.StopApplication();

        await host.DelegateAsync(threadId);
        await Task.Delay(500); // several of the coordinator's 100 ms polls

        Assert.Equal(LifecycleState.Queued, (await host.ThreadAsync(threadId)).Thread.State);
        Assert.Empty(host.Workers.Turns);
    }

    [Theory]
    [MemberData(nameof(TurnFailures))]
    public async Task AChatThatFailsAsTheHostStops_SaysTheHostWasStopping_NotWhatBroke(string failure)
    {
        await using var host = await TestHost.StartAsync();
        var threadId = await host.CreateThreadAsync(await host.RegisterProjectAsync());
        var stopped = StopTheHostDuringTheNextTurn(host, failure);

        await host.DelegateAsync(threadId, "Where are orders exported?");
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await ExecutorsFinishedAsync(host);

        var details = await host.ThreadAsync(threadId);
        var answer = details.Messages.OrderBy(m => m.Sequence).Last();
        Assert.Equal(MessageAuthor.Factory, answer.Author);
        Assert.Equal(ChatExecutor.Stopped, answer.Text);
        Assert.Equal(LifecycleState.Draft, details.Thread.State);
    }

    [Theory]
    [MemberData(nameof(TurnFailures))]
    public async Task ASpecTurnThatFailsAsTheHostStops_LeavesTheRunRunning_AndTheNextStartMarksItInterrupted(string failure)
    {
        await using var host = await TestHost.StartAsync();
        var threadId = await host.CreateThreadAsync(await host.RegisterProjectAsync());
        var stopped = StopTheHostDuringTheNextTurn(host, failure);

        await host.DelegateAsync(threadId, "@factory spec Export the orders list as CSV.");
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await ExecutorsFinishedAsync(host);

        var details = await host.ThreadAsync(threadId);
        Assert.Equal(LifecycleState.Running, details.Thread.State);
        Assert.Equal((RunKind.Spec, RunStatus.Running), (details.LatestRun!.Kind, details.LatestRun.Status));
        Assert.Null(details.LatestSpec);

        await RestartAsync(host, details.LatestRun.Id);

        details = await host.ThreadAsync(threadId);
        Assert.Equal((LifecycleState.Interrupted, Stage.Spec), (details.Thread.State, details.Thread.Stage));
    }
}
