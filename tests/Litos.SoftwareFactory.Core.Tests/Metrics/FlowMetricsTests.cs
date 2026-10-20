using Litos.SoftwareFactory.Core.Lessons;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Metrics;
using Litos.SoftwareFactory.Core.Orchestration;

namespace Litos.SoftwareFactory.Core.Tests.Metrics;

/// <summary>The flow metrics of §12.1, from the rows the factory keeps (m4-architecture.md §6).</summary>
public class FlowMetricsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Project = Guid.NewGuid();

    private static FlowThread Thread(LifecycleState state, string title = "Task") => new(Guid.NewGuid(), Project, title, state, T0);

    private static FlowStateEvent At(FlowThread thread, double hours, LifecycleState state, Stage stage, string? reason = null) =>
        new(thread.Id, T0.AddHours(hours), state.ToString(), stage.ToString(), reason);

    private static FlowData Data(
        IReadOnlyList<FlowThread> threads, IReadOnlyList<FlowStateEvent>? events = null, IReadOnlyList<FlowUsage>? usage = null,
        IReadOnlyList<FlowRun>? runs = null, IReadOnlyList<FlowDecision>? decisions = null, IReadOnlyList<FlowLessonUse>? lessons = null) =>
        new(threads, events ?? [], usage ?? [], runs ?? [], decisions ?? [], lessons ?? []);

    [Fact]
    public void TimeInEachStateAndStage_RunsFromOneEventToTheNext_AndStopsWhenTheTaskIsFinished()
    {
        var task = Thread(LifecycleState.Accepted);
        FlowStateEvent[] events =
        [
            At(task, 0, LifecycleState.Draft, Stage.Discuss),
            At(task, 1, LifecycleState.Queued, Stage.Implement),
            At(task, 1.5, LifecycleState.Running, Stage.Implement),
            At(task, 3.5, LifecycleState.AwaitingHumanTesting, Stage.Handoff),
            At(task, 9.5, LifecycleState.Accepted, Stage.Done),
        ];

        var report = FlowMetrics.Compute(Data([task], events), T0.AddHours(100));

        Assert.Equal(6.0, report.TimeInState.Single(t => t.Name == "AwaitingHumanTesting").TotalHours);
        Assert.Equal(2.0, report.TimeInState.Single(t => t.Name == "Running").TotalHours);
        Assert.Equal("AwaitingHumanTesting", report.TimeInState[0].Name); // the longest first
        Assert.DoesNotContain(report.TimeInState, t => t.Name == "Accepted");
        Assert.Equal(2.5, report.TimeInStage.Single(t => t.Name == "Implement").TotalHours);
    }

    [Fact]
    public void AnUnfinishedTasksLastState_CountsUntilNow()
    {
        var task = Thread(LifecycleState.Running);

        var report = FlowMetrics.Compute(Data([task], [At(task, 0, LifecycleState.Running, Stage.Implement)]), T0.AddHours(4));

        Assert.Equal(4.0, report.TimeInState.Single(t => t.Name == "Running").TotalHours);
        Assert.Equal(1, report.Open);
    }

    [Fact]
    public void QueueWaits_AreSplitByWhatTheTaskWaitedFor_AndAClaimAtOnceCountsForNeither()
    {
        var forLock = Thread(LifecycleState.Running);
        var forSlot = Thread(LifecycleState.Running);
        var atOnce = Thread(LifecycleState.Running);
        FlowStateEvent[] events =
        [
            At(forLock, 0, LifecycleState.Queued, Stage.Implement),
            At(forLock, 0.1, LifecycleState.Queued, Stage.Implement, "Waiting for salesapp — held by \"Other\" (running)."),
            At(forLock, 0.5, LifecycleState.Running, Stage.Implement),
            At(forSlot, 0, LifecycleState.Queued, Stage.Implement, "Waiting for a free slot (2 of 2 busy)."),
            At(forSlot, 0.25, LifecycleState.Running, Stage.Implement),
            At(atOnce, 0, LifecycleState.Queued, Stage.Implement),
            At(atOnce, 0.01, LifecycleState.Running, Stage.Implement),
        ];

        var report = FlowMetrics.Compute(Data([forLock, forSlot, atOnce], events), T0.AddHours(1));

        Assert.Equal(new Spread(1, 30, 30), report.QueueForRepository);
        Assert.Equal(new Spread(1, 15, 15), report.QueueForSlot);
    }

    [Fact]
    public void ReworkRounds_AndFirstHandoffAcceptance_CountAcceptedTasks()
    {
        var clean = Thread(LifecycleState.Accepted);
        var reworked = Thread(LifecycleState.Accepted);
        var open = Thread(LifecycleState.AwaitingHumanTesting);
        FlowRun[] runs = [new(clean.Id, RunKind.Implement), new(reworked.Id, RunKind.Implement), new(reworked.Id, RunKind.Rework), new(reworked.Id, RunKind.Rework), new(open.Id, RunKind.Rework)];

        var report = FlowMetrics.Compute(Data([clean, reworked, open], runs: runs), T0);

        Assert.Equal((3, 2, 0, 1), (report.Tasks, report.Accepted, report.Cancelled, report.Open));
        Assert.Equal(0.5, report.FirstHandoffAcceptance);
        Assert.Equal([(0, 1), (2, 1)], report.ReworkRounds);
        Assert.Null(FlowMetrics.Compute(Data([open]), T0).FirstHandoffAcceptance);
    }

    [Fact]
    public void Tokens_ArePerTask_AndByPhaseAndModel_WithTheirShares()
    {
        var a = Thread(LifecycleState.Accepted);
        var b = Thread(LifecycleState.Accepted);
        FlowUsage[] usage =
        [
            new(a.Id, "Implement", "m1", 600), new(a.Id, "Review", "m1", 200), new(b.Id, "Implement", "m2", 100), new(b.Id, null, "m2", 100),
            new(Guid.NewGuid(), "Implement", "m1", 9_999), // another project's task, not among these
        ];

        var report = FlowMetrics.Compute(Data([a, b], usage: usage), T0);

        Assert.Equal(1_000, report.TokensTotal);
        Assert.Equal(new Spread(2, 200, 800), report.TokensPerTask);
        Assert.Equal([("Implement", 700L, 0.7), ("Review", 200L, 0.2), ("Other", 100L, 0.1)], report.TokensByPhase.Select(s => (s.Name, s.Tokens, s.Share)));
        Assert.Equal(["m1", "m2"], report.TokensByModel.Select(s => s.Name));
    }

    [Fact]
    public void OpenDecisions_NameTheOldestWaiting()
    {
        var task = Thread(LifecycleState.AwaitingDecision, "Export");
        FlowDecision[] decisions = [new(task.Id, "Export", "Newer?", T0.AddHours(5), true), new(task.Id, "Export", "Older?", T0.AddHours(1), true), new(task.Id, "Export", "Answered?", T0, false)];

        var report = FlowMetrics.Compute(Data([task], decisions: decisions), T0.AddHours(25));

        Assert.Equal(2, report.OpenDecisions);
        Assert.Equal(new OldestDecision(task.Id, "Export", "Older?", 24), report.OldestOpenDecision);
    }

    [Fact]
    public void LessonInclusion_IsReportedApartFromTheVerdictsOnWhetherItHelped()
    {
        var task = Thread(LifecycleState.Accepted);
        FlowLessonUse[] lessons =
        [
            new(task.Id, LessonInclusion.Retrieved, LessonVerdict.Helped), new(task.Id, LessonInclusion.Manual, LessonVerdict.Misled),
            new(task.Id, LessonInclusion.Retrieved, null), new(task.Id, LessonInclusion.NotIncluded, null),
        ];

        Assert.Equal(new LessonUse(3, 1, 1, 0, 1, 1), FlowMetrics.Compute(Data([task], lessons: lessons), T0).Lessons);
    }

    [Fact]
    public void PerTask_ListsEveryTaskWithItsChargeAndChangeRequests_MostFirst()
    {
        var small = Thread(LifecycleState.Accepted, "Small");
        var big = Thread(LifecycleState.Running, "Big");
        var idle = Thread(LifecycleState.Draft, "Idle");
        FlowUsage[] usage = [new(small.Id, "Implement", "m", 100), new(big.Id, "Implement", "m", 900), new(big.Id, "Review", "m", 100)];
        FlowRun[] runs = [new(small.Id, RunKind.Implement), new(small.Id, RunKind.Rework)];

        var report = FlowMetrics.Compute(Data([small, big, idle], usage: usage, runs: runs), T0);

        Assert.Equal(
            [("Big", "Running", 1_000L, 0), ("Small", "Accepted", 100L, 1), ("Idle", "Draft", 0L, 0)],
            report.PerTask.Select(t => (t.Title, t.State, t.Tokens, t.ReworkRounds)));
        Assert.All(report.PerTask, t => Assert.Equal(Project, t.ProjectId));
    }

    [Theory]
    [InlineData(7, 1, 7)]
    [InlineData(30, 3, 10)]
    [InlineData(90, 7, 13)]
    public void Series_SplitsThePeriodIntoEqualStretches_OldestFirst_EndingNow(int days, int size, int count)
    {
        var now = T0.AddDays(100);

        var series = FlowMetrics.Compute(Data([]), now, days).Series;

        Assert.Equal(count, series.Count);
        Assert.All(series, b => Assert.Equal(size, b.Days));
        Assert.Equal(now, series[^1].Start.AddDays(size));
        Assert.True(series[0].Start <= now.AddDays(-days));
        Assert.Empty(FlowMetrics.Compute(Data([]), now).Series); // no period, no trend
    }

    [Fact]
    public void Series_CountsATaskWhenCreated_AndWhenItsAcceptanceWasRecorded_SayingWhetherItWasFirstTime()
    {
        var now = T0.AddDays(7);
        FlowThread Created(double daysBeforeNow, LifecycleState state) => new(Guid.NewGuid(), Project, "Task", state, now.AddDays(-daysBeforeNow));
        var firstTime = Created(6.5, LifecycleState.Accepted);
        var reworked = Created(6.5, LifecycleState.Accepted);
        var open = Created(0.5, LifecycleState.Running);
        FlowStateEvent Accepted(FlowThread t, double daysBeforeNow) => new(t.Id, now.AddDays(-daysBeforeNow), "Accepted", "Done", null);

        var series = FlowMetrics.Compute(
            Data([firstTime, reworked, open], [Accepted(firstTime, 4.5), Accepted(reworked, 0.5)], runs: [new(reworked.Id, RunKind.Rework)]),
            now, 7).Series;

        Assert.Equal([2, 0, 0, 0, 0, 0, 1], series.Select(b => b.Created));
        Assert.Equal([0, 0, 1, 0, 0, 0, 1], series.Select(b => b.Accepted));
        Assert.Equal([0, 0, 1, 0, 0, 0, 0], series.Select(b => b.AcceptedFirstTime));
    }

    [Fact]
    public void Spread_IsTheMedianAndNinetiethPercentile_ByNearestRank()
    {
        Assert.Equal(new Spread(10, 5, 9), FlowMetrics.SpreadOf([.. Enumerable.Range(1, 10).Select(i => (double)i)]));
        Assert.Equal(new Spread(0, 0, 0), FlowMetrics.SpreadOf([]));
    }
}
