using System.Net;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Settings;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Host.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>
/// W3.8 finding 8 (README_Litos.Probe.Web.md §11.5): clinic-portal's light review made the agent's
/// stale "four tests still fail" a blocking finding although the verification had run them green.
/// With no repair cycle left the task was Blocked, and Resume could only start a repair that had
/// nothing to fix. A person may now judge the blocking findings mistaken and hand off as it is.
/// </summary>
public sealed class DismissFindingsTests : IAsyncLifetime
{
    private TestHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await TestHost.StartAsync();
        var settings = _host.App.Services.GetRequiredService<FactorySettings>();
        await settings.SaveAsync(SettingsSections.Budgets, settings.Budgets with { RepairCyclesPerRun = 0 }, settings.RevisionOf(SettingsSections.Budgets), null, default);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private const string Stale = "The implementer reports four tests still failing, yet the verification says 0 failed.";

    /// <summary>Implement, then a review with one blocking and one minor finding.</summary>
    private async Task<Guid> BlockedOnReviewAsync()
    {
        _host.Options.Limits = _host.Options.Limits with { FullReviewFromScore = 0 };
        _host.Workers.Script.Enqueue(_host.DefaultTurnAsync);
        _host.Workers.Script.Enqueue(async call =>
        {
            await call.Worker.SubmitAsync(call.SessionId, new ReviewSubmission([
                new ReviewFinding(FindingSeverity.Blocking, "Testing/TestData.cs", null, Stale),
                new ReviewFinding(FindingSeverity.Minor, "Tests/BookingTests.cs", null, "The heading is not checked.")]));
            return FakeWorkerLauncher.Done();
        });
        var threadId = await _host.CreateThreadAsync(await _host.RegisterProjectAsync());
        await _host.DelegateAsync(threadId);
        var blocked = await _host.WaitForStateAsync(threadId, LifecycleState.Blocked);
        Assert.Contains("no repair cycles are left", blocked.Thread.StateReason);
        return threadId;
    }

    [Fact]
    public async Task A_person_dismisses_the_blocking_finding_and_the_task_hands_off_without_a_repair()
    {
        var threadId = await BlockedOnReviewAsync();
        Assert.True((await _host.GetAsync($"api/threads/{threadId}")).GetProperty("canDismissFindings").GetBoolean());

        await _host.PostAsync($"api/threads/{threadId}/dismiss-findings", null, HttpStatusCode.OK);
        var done = await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        // No repair turn ran, and the review was not repeated.
        Assert.Equal([TurnKind.Implement, TurnKind.Review], _host.Workers.Turns.Select(t => t.Kind));

        var blocking = Assert.Single(done.Findings, f => f.Severity == FindingSeverity.Blocking);
        Assert.Equal(FindingStatus.Dismissed, blocking.Status);
        Assert.Equal(FindingVerdict.Wrong, blocking.Verdict);
        Assert.NotNull(blocking.VerdictBy);
        Assert.Equal(FindingStatus.Open, Assert.Single(done.Findings, f => f.Severity == FindingSeverity.Minor).Status);

        Assert.Contains(done.Messages, m => m.Kind == MessageKind.Status && m.Text.StartsWith("Dismissed the review's blocking finding as mistaken", StringComparison.Ordinal));
        var handoff = done.Messages.Single(m => m.Kind == MessageKind.Handoff);
        var limitations = System.Text.Json.JsonDocument.Parse(handoff.PayloadJson!).RootElement.GetProperty("knownLimitations")
            .EnumerateArray().Select(l => l.GetString()!).ToList();
        Assert.Contains($"A person judged the review's blocking finding mistaken, so it was handed off without a repair: “{Stale}”", limitations);

        var audit = await _host.Store.ListAuditAsync(null, 200, default);
        Assert.Contains(audit, a => a.Action == AuditActions.ThreadDismissFindings && a.TargetId == threadId);
        Assert.Contains(audit, a => a.Action == AuditActions.FindingVerdict && a.TargetId == blocking.Id);
        Assert.False((await _host.GetAsync($"api/threads/{threadId}")).GetProperty("canDismissFindings").GetBoolean());
    }

    [Fact]
    public async Task Resume_still_repairs_the_findings_instead()
    {
        var threadId = await BlockedOnReviewAsync();
        _host.Workers.Script.Enqueue(_host.DefaultTurnAsync); // the repair

        await _host.PostAsync($"api/threads/{threadId}/resume", null, HttpStatusCode.OK);
        await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        Assert.Equal([TurnKind.Implement, TurnKind.Review, TurnKind.Repair], _host.Workers.Turns.Select(t => t.Kind));
    }

    [Fact]
    public async Task Any_other_stop_is_refused()
    {
        // Blocked on failing tests with no cycle left: nothing a review found.
        _host.Verifier.Outcomes.Enqueue(ScriptedVerifier.Failing("Orders.Export_Works"));
        var threadId = await _host.CreateThreadAsync(await _host.RegisterProjectAsync());
        await _host.DelegateAsync(threadId);
        await _host.WaitForStateAsync(threadId, LifecycleState.Blocked);

        Assert.False((await _host.GetAsync($"api/threads/{threadId}")).GetProperty("canDismissFindings").GetBoolean());
        await _host.PostAsync($"api/threads/{threadId}/dismiss-findings", null, HttpStatusCode.Conflict);
        Assert.Equal(LifecycleState.Blocked, (await _host.ThreadAsync(threadId)).Thread.State);
    }
}
