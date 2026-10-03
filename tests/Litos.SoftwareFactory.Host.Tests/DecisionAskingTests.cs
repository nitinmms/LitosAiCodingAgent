using System.Net;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Host.Runs;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>
/// On F6 the agent changed the file format so existing databases could not be read, and reported
/// that as a known limitation instead of asking. A submission whose limitation says something
/// existing stops working is now sent back once, as a decision to ask.
/// </summary>
public class BreakingLimitationTests
{
    private const string Breaking = "Version 1 files are rejected with InvalidDatabaseFileException.";

    private static ActiveRun Run() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "openrouter", "m");

    private static WorkSubmission Work(params string[] limitations) => new("Added expiry.", [], [], limitations, []);

    [Fact]
    public void ABreakingLimitation_IsSentBackOnce_ThenRecorded()
    {
        var run = Run();
        run.BeginTurn(TurnKind.Implement, TurnKind.Implement, "s", default);

        var first = run.Accept("s", Work("Large exports are not streamed.", Breaking));

        Assert.False(first.Accepted);
        Assert.Contains($"\"{Breaking}\"", first.Message);
        Assert.Contains("call `request_decision`", first.Message);
        Assert.Null(run.Submission);

        Assert.True(run.Accept("s", Work("Large exports are not streamed.", Breaking)).Accepted);
        Assert.IsType<WorkSubmission>(run.Submission);
    }

    [Fact]
    public void OrdinaryLimitations_AreRecordedAtOnce()
    {
        var run = Run();
        run.BeginTurn(TurnKind.Implement, TurnKind.Implement, "s", default);

        Assert.True(run.Accept("s", Work("Negative TTLs are rejected with ArgumentOutOfRangeException.", "Not tried on Safari.")).Accepted);
    }

    [Fact]
    public void AfterADecision_ABreakingLimitation_IsRecordedAtOnce()
    {
        var run = Run();
        run.BeginTurn(TurnKind.Implement, TurnKind.Implement, "s", default, askAboutBreakingLimitations: false);

        Assert.True(run.Accept("s", Work(Breaking)).Accepted);
    }

    [Theory]
    [InlineData(TurnKind.Repair)]
    [InlineData(TurnKind.Rework)]
    public void EveryWorkTurn_IsChecked(TurnKind kind)
    {
        var run = Run();
        run.BeginTurn(kind, kind, "s", default);

        Assert.False(run.Accept("s", Work(Breaking)).Accepted);
    }

    [Fact]
    public void ANewTurn_IsCheckedAgain()
    {
        var run = Run();
        run.BeginTurn(TurnKind.Implement, TurnKind.Implement, "s", default);
        Assert.False(run.Accept("s", Work(Breaking)).Accepted);
        Assert.True(run.Accept("s", Work(Breaking)).Accepted);

        run.BeginTurn(TurnKind.Repair, TurnKind.Repair, "s", default);

        Assert.False(run.Accept("s", Work(Breaking)).Accepted);
    }

    [Fact]
    public void ADecisionOrAReview_IsNotChecked()
    {
        var run = Run();
        run.BeginTurn(TurnKind.Implement, TurnKind.Implement, "s", default);
        Assert.True(run.Accept("s", new DecisionSubmission("Keep reading version 1 files?", "Existing databases would stop opening.", ["Keep reading them", "Drop them"])).Accepted);

        run.BeginTurn(TurnKind.Review, TurnKind.Review, "r", default);
        Assert.True(run.Accept("r", new ReviewSubmission([new ReviewFinding(FindingSeverity.Blocking, "a.cs", 1, Breaking)])).Accepted);
    }
}

/// <summary>The same, through a run: the agent is told, and the run continues.</summary>
public sealed class BreakingLimitationRunTests : IAsyncLifetime
{
    private TestHost _host = null!;

    public async Task InitializeAsync() => _host = await TestHost.StartAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static WorkSubmission BreakingWork() => FakeWorkerLauncher.Work() with { KnownLimitations = ["Version 1 files cannot be read by this version."] };

    [Fact]
    public async Task TheAgentIsToldToAsk_AndAResubmissionIsHandedOff()
    {
        var responses = new List<SubmissionResponse>();
        _host.Workers.Script.Enqueue(async call =>
        {
            _host.Workers.WorkspaceOf(call.Worker).Write("src/Orders.cs", "format v2\n");
            responses.Add(await call.Worker.SubmitAsync(call.SessionId, BreakingWork()));
            responses.Add(await call.Worker.SubmitAsync(call.SessionId, BreakingWork()));
            return FakeWorkerLauncher.Done();
        });
        var threadId = await _host.CreateThreadAsync(await _host.RegisterProjectAsync());

        await _host.DelegateAsync(threadId);
        var details = await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        Assert.False(responses[0].Accepted);
        Assert.Contains("That is a choice for a person, not a limitation to report.", responses[0].Message);
        Assert.True(responses[1].Accepted);
        Assert.Contains("Version 1 files cannot be read", details.Messages.Single(m => m.Kind == MessageKind.Handoff).PayloadJson);
    }

    [Fact]
    public async Task OnceTheTaskHasADecision_TheLimitationIsRecordedAtOnce()
    {
        var responses = new List<SubmissionResponse>();
        _host.Workers.Script.Enqueue(async call =>
        {
            await call.Worker.SubmitAsync(call.SessionId, new DecisionSubmission(
                "Should version 1 files still be readable?", "Persisting expiry changes the file format.", ["Keep reading them", "Drop them"]));
            await Task.Delay(Timeout.Infinite, call.Token);
            return FakeWorkerLauncher.Done();
        });
        _host.Workers.Script.Enqueue(async call =>
        {
            _host.Workers.WorkspaceOf(call.Worker).Write("src/Orders.cs", "format v2\n");
            responses.Add(await call.Worker.SubmitAsync(call.SessionId, BreakingWork()));
            return FakeWorkerLauncher.Done();
        });
        var threadId = await _host.CreateThreadAsync(await _host.RegisterProjectAsync());

        await _host.DelegateAsync(threadId);
        var decision = Assert.Single((await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingDecision)).Decisions);
        await _host.PostAsync($"api/decisions/{decision.Id}/answer", new { answer = "Drop them." }, HttpStatusCode.OK);
        await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        Assert.True(Assert.Single(responses).Accepted);
    }
}
