using System.Net;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Orchestration;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Host.Runs;
using Microsoft.Extensions.Configuration;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>The decision scan, through a real run: what it asks, what it assumes, and what the
/// implementer and the tester are told.</summary>
public sealed class DecisionScanRunTests : IAsyncLifetime
{
    private TestHost _host = null!;

    public async Task InitializeAsync() =>
        _host = await TestHost.StartAsync(options => options.Limits = options.Limits with { DecisionScan = true });

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static OpenChoice FormatChoice() => new(
        "What happens to version-1 files?", ChoiceCategories.ExistingData, ["Keep reading them", "Stop reading them"],
        "Keep reading them", "Every existing database is version 1.");

    private static OpenChoice SweepChoice() => new("On read or a background sweep?", ChoiceCategories.Other, ["On read", "A sweep"], "On read");

    private void ScanFinds(params OpenChoice[] choices) =>
        _host.Workers.Script.Enqueue(async call =>
        {
            Assert.Equal(TurnKind.Scan, call.Kind);
            await call.Worker.SubmitAsync(call.SessionId, new PlanSubmission("Store expiry per put record.", ["src/LogFormat.cs"], choices));
            return FakeWorkerLauncher.Done();
        });

    private async Task<Guid> DelegatedAsync()
    {
        var threadId = await _host.CreateThreadAsync(await _host.RegisterProjectAsync());
        await _host.DelegateAsync(threadId);
        return threadId;
    }

    [Fact]
    public async Task AScanThatFindsAChoiceForAPerson_AsksIt_ThenImplementsWithTheAnswerAndThePlan()
    {
        ScanFinds(FormatChoice(), SweepChoice());
        var threadId = await DelegatedAsync();

        var waiting = await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingDecision);
        var decision = Assert.Single(waiting.Decisions);
        Assert.Equal("What happens to version-1 files?", decision.Question);
        Assert.Contains(waiting.Messages, m => m.Text == "Decision scan: 2 open choices, asking you 1. Assuming:\n- On read or a background sweep: On read");
        Assert.Contains(waiting.Messages, m => m.Kind == MessageKind.Decision && m.Text.StartsWith("Decision needed: What happens to version-1 files?"));

        await _host.PostAsync($"api/decisions/{decision.Id}/answer", new { answer = "Keep reading them." }, HttpStatusCode.OK);
        var done = await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        var turns = _host.Workers.Turns.ToArray();
        Assert.Equal([TurnKind.Scan, TurnKind.Implement, TurnKind.Review], turns.Select(t => t.Kind));
        Assert.StartsWith("scan-", turns[0].SessionId);
        Assert.Equal(done.Thread.SessionId, turns[1].SessionId); // the implementer works in the thread's own session
        Assert.Contains("**What happens to version-1 files?** — Keep reading them.", turns[1].Brief);
        Assert.Contains("## The plan from the decision scan", turns[1].Brief);
        Assert.Contains("- On read or a background sweep: On read", turns[1].Brief);

        var handoff = done.Messages.Single(m => m.Kind == MessageKind.Handoff);
        Assert.Contains("Assumed, not asked: On read or a background sweep: On read", handoff.PayloadJson);
    }

    /// <summary>On R2, R3 and R3's re-run a queued question repeated one a person had just
    /// answered. After an answer the scan session re-checks the rest, and one the answer settles
    /// is not asked.</summary>
    [Fact]
    public async Task AfterAnAnswer_TheScanRechecksTheRest_AndAQuestionTheAnswerSettlesIsNotAsked()
    {
        var partial = new OpenChoice("Is a partial response kept?", ChoiceCategories.ExistingData, ["Discard it", "Keep it"], "Discard it");
        ScanFinds(FormatChoice(), partial);
        _host.Workers.Script.Enqueue(async call =>
        {
            Assert.Equal(TurnKind.Scan, call.Kind);
            Assert.StartsWith("# Factory decision scan: re-check the remaining questions", call.Brief);
            Assert.Contains("1. Is a partial response kept?", call.Brief);
            await call.Worker.SubmitAsync(call.SessionId, new PlanSubmission("recheck", [], [partial with { SettledBy = "Keep reading them, and nothing partial" }]));
            return FakeWorkerLauncher.Done();
        });
        var threadId = await DelegatedAsync();

        var waiting = await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingDecision);
        await _host.PostAsync($"api/decisions/{waiting.Decisions.Single().Id}/answer", new { answer = "Keep reading them, and nothing partial is kept." }, HttpStatusCode.OK);
        var done = await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        var turns = _host.Workers.Turns.ToArray();
        Assert.Equal([TurnKind.Scan, TurnKind.Scan, TurnKind.Implement, TurnKind.Review], turns.Select(t => t.Kind));
        Assert.Equal(turns[0].SessionId, turns[1].SessionId);            // the re-check continues the scan's session
        Assert.Single(done.Decisions);                                    // the second question was never asked
        Assert.Contains(done.Messages, m => m.Text == "Re-checked the remaining questions against your answers: 1 is settled, so nothing more needs asking.");
        Assert.Contains("Is a partial response kept: Discard it (Keep reading them, and nothing partial)", turns[2].Brief);
    }

    [Fact]
    public async Task AScanWithNothingForAPerson_GoesStraightToImplementation()
    {
        ScanFinds(SweepChoice());
        var threadId = await DelegatedAsync();

        var done = await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        Assert.Equal([TurnKind.Scan, TurnKind.Implement, TurnKind.Review], _host.Workers.Turns.Select(t => t.Kind));
        Assert.Empty(done.Decisions);
        Assert.Contains(done.Messages, m => m.Text.StartsWith("Decision scan: 1 open choice, none needs you."));
    }

    [Fact]
    public async Task TheScan_HasItsOwnSmallAllowance()
    {
        ScanFinds();
        var threadId = await DelegatedAsync();

        await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        Assert.Equal(new RunLimits().ScanMaxToolCalls, _host.Workers.Turns.First().MaxToolCalls);
    }
}

public sealed class ScanOnlyRunTests : IAsyncLifetime
{
    private TestHost _host = null!;

    public async Task InitializeAsync() =>
        _host = await TestHost.StartAsync(options => options.Limits = options.Limits with { DecisionScan = true, ScanOnly = true });

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task ScanOnly_StopsAfterTheScan_SayingWhatItWouldAsk_WithoutImplementing()
    {
        _host.Workers.Script.Enqueue(async call =>
        {
            await call.Worker.SubmitAsync(call.SessionId, new PlanSubmission("a", [], [
                new OpenChoice("Should images be generated for every slide?", ChoiceCategories.UserVisible, ["Every slide", "Only on request"], "Only on request", "Generation costs money."),
            ]));
            return FakeWorkerLauncher.Done();
        });
        var threadId = await _host.CreateThreadAsync(await _host.RegisterProjectAsync());
        await _host.DelegateAsync(threadId);

        var stopped = await _host.WaitForStateAsync(threadId, LifecycleState.Blocked);

        Assert.Equal([TurnKind.Scan], _host.Workers.Turns.Select(t => t.Kind));
        Assert.Contains("Scan only. Would ask: Should images be generated for every slide?", stopped.Thread.StateReason);
        Assert.Empty(stopped.Decisions);
    }
}

public class ScanActiveRunTests
{
    private static ActiveRun Run() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "openrouter", "m");

    private static PlanSubmission Plan() => new("a", [], []);

    [Fact]
    public void APlan_IsAcceptedOnlyInAScanTurn_AndFinishesIt()
    {
        var run = Run();
        run.BeginTurn(TurnKind.Implement, TurnKind.Implement, "s", default);
        Assert.Equal("submit_plan is not valid in a Implement turn.", run.Accept("s", Plan()).Message);

        run.BeginTurn(TurnKind.Scan, TurnKind.Scan, "scan-1", default, "Scan", TurnAllowance.ForScan(new RunLimits()));
        Assert.True(run.Accept("scan-1", Plan()).Accepted);
        Assert.True(run.HasFinished("scan-1"));
    }

    [Fact]
    public void AScanPastItsAllowance_IsAskedToSubmitItsPlan()
    {
        var run = Run();
        run.BeginTurn(TurnKind.Scan, TurnKind.Scan, "scan-1", default, "Scan", new TurnAllowance(1, null, 10));

        var steer = run.RecordTurnCall(1_000);

        Assert.StartsWith("This scan has used 1 model calls, its allowance of 1. Stop reading now and call `submit_plan`", steer);
    }
}

public class ScanOptionsTests
{
    private static FactoryOptions From(params (string Key, string Value)[] values) =>
        FactoryOptions.From(new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build());

    [Fact]
    public void TheScanIsOnByDefault_AndScanOnlyIsOff()
    {
        var limits = From().Limits;

        Assert.True(limits.DecisionScan);
        Assert.False(limits.ScanOnly);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("0")]
    [InlineData("false")]
    public void TheScanCanBeTurnedOff(string value) => Assert.False(From(("FACTORY_DECISION_SCAN", value)).Limits.DecisionScan);

    [Fact]
    public void ScanOnlyCanBeTurnedOn() => Assert.True(From(("FACTORY_SCAN_ONLY", "1")).Limits.ScanOnly);

    [Fact]
    public void NoReviewIsAllowedByDefault_AndCanBeTurnedOff()
    {
        Assert.True(From().Limits.AllowNoReview);
        Assert.False(From(("FACTORY_REVIEW_NONE", "off")).Limits.AllowNoReview);
    }

    [Fact]
    public void TheRecheckIsOnByDefault_AndCanBeTurnedOff()
    {
        Assert.True(From().Limits.ScanRecheck);
        Assert.False(From(("FACTORY_SCAN_RECHECK", "off")).Limits.ScanRecheck);
    }

    [Fact]
    public void CostNotesAreOnByDefault_AndCanBeTurnedOff()
    {
        Assert.True(From().Limits.CostNotes);
        Assert.False(From(("FACTORY_COST_NOTES", "off")).Limits.CostNotes);
    }
}
