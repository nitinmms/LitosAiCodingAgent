using Litos.SoftwareFactory.Core.Lessons;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Metrics;
using Litos.SoftwareFactory.Core.Orchestration;

namespace Litos.SoftwareFactory.Core.Tests.Metrics;

/// <summary>How the project lessons are doing, so they can be improved (m4-architecture.md §12).</summary>
public class LessonMetricsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Project = Guid.NewGuid();
    private static long _number;

    private static LessonRow Lesson(
        string rule = "Rule.", LessonSource source = LessonSource.HumanCorrection, bool usable = true, LessonStatus status = LessonStatus.Approved,
        int? approvedRevision = 1, bool proposed = false, double approvedDaysAgo = 60, double createdDaysAgo = 60, Guid? project = null) =>
        new(Guid.NewGuid(), project ?? Project, ++_number, status, usable, rule, source, approvedRevision ?? 1, approvedRevision, proposed,
            Now.AddDays(-createdDaysAgo), status == LessonStatus.Approved ? Now.AddDays(-approvedDaysAgo) : null);

    private static FlowThread Task(LifecycleState state = LifecycleState.Accepted, Guid? project = null) =>
        new(Guid.NewGuid(), project ?? Project, "Task", state, Now.AddDays(-10));

    private static LessonGiven Given(LessonRow lesson, FlowThread task, LessonVerdict? verdict = null, int position = 0,
        LessonInclusion inclusion = LessonInclusion.Retrieved, double daysAgo = 5) =>
        new(lesson.Id, task.Id, inclusion, position, verdict, Now.AddDays(-daysAgo));

    /// <summary>The lesson given once per verdict listed, each on a task of its own.</summary>
    private static List<LessonGiven> Uses(LessonRow lesson, params LessonVerdict?[] verdicts) =>
        [.. verdicts.Select(v => Given(lesson, Task(), v))];

    private static LessonReport Compute(IReadOnlyList<LessonRow> lessons, IReadOnlyList<LessonGiven> given,
        IReadOnlyList<FlowThread>? tasks = null, IReadOnlyList<FlowRun>? runs = null, int days = 30) =>
        LessonMetrics.Compute(new LessonData(lessons, given, tasks ?? [], runs ?? []), Now, days);

    private const LessonVerdict Helped = LessonVerdict.Helped;
    private const LessonVerdict NoMatter = LessonVerdict.DidNotMatter;
    private const LessonVerdict Misled = LessonVerdict.Misled;

    [Fact]
    public void ALessonsUses_AreCounted_GivenApartFromLeftOut_AndByVerdict()
    {
        var lesson = Lesson();
        var task = Task();
        List<LessonGiven> given =
        [
            Given(lesson, task, Helped), Given(lesson, task, Misled, inclusion: LessonInclusion.Manual), Given(lesson, task, null),
            Given(lesson, task, NoMatter, daysAgo: 1), Given(lesson, task, inclusion: LessonInclusion.NotIncluded),
        ];

        var report = Compute([lesson], given);

        var score = Assert.Single(report.Lessons);
        Assert.Equal((4, 1, 1), (score.Given, score.Manual, score.LeftOut));
        Assert.Equal((1, 1, 1, 1), (score.Helped, score.DidNotMatter, score.Misled, score.Unjudged));
        Assert.Equal(0.0, score.Net); // (1 helped - 1 misled) / 3 judged
        Assert.Equal(Now.AddDays(-1), score.LastGiven);
        Assert.Equal((4, 3, 1, 1, 1), (report.Given, report.Judged, report.Helped, report.Misled, report.LeftOut));
    }

    [Fact]
    public void Standing_FollowsHowOftenItWasGivenAndWhatPeopleSaid()
    {
        var working = Lesson();
        var rarely = Lesson();
        var noEffect = Lesson();
        var misleading = Lesson();
        var unjudged = Lesson();
        var unmatched = Lesson();
        var fresh = Lesson(approvedDaysAgo: 2);
        List<LessonGiven> given =
        [
            .. Uses(working, Helped, Helped, Helped, NoMatter, null),
            .. Uses(rarely, Helped, Helped),
            .. Uses(noEffect, NoMatter, NoMatter, NoMatter, Helped, null),
            .. Uses(misleading, Helped, Helped, Misled),
            .. Uses(unjudged, Helped, null, null),
        ];

        var standing = Compute([working, rarely, noEffect, misleading, unjudged, unmatched, fresh], given).Lessons.ToDictionary(s => s.LessonId, s => s.Standing);

        Assert.Equal(LessonStanding.Working, standing[working.Id]);
        Assert.Equal(LessonStanding.RarelyMatched, standing[rarely.Id]); // helps, but under five uses in 30 days
        Assert.Equal(LessonStanding.LittleEffect, standing[noEffect.Id]);
        Assert.Equal(LessonStanding.Misleading, standing[misleading.Id]); // a third of its verdicts
        Assert.Equal(LessonStanding.TooFewVerdicts, standing[unjudged.Id]);
        Assert.Equal(LessonStanding.NeverMatched, standing[unmatched.Id]);
        Assert.Equal(LessonStanding.New, standing[fresh.Id]);
    }

    [Fact]
    public void Misleading_NeedsAQuarterOfTheVerdicts_OrTwoOnALessonThatDoesNotClearlyHelp()
    {
        var lesson = Lesson();
        LessonStanding Of(int helped, int misled, int noMatter = 0)
        {
            var judged = helped + misled + noMatter;
            return LessonMetrics.Standing(lesson, 20, judged, misled, (double)(helped - misled) / judged, Now, 30);
        }

        Assert.Equal(LessonStanding.Working, Of(helped: 9, misled: 1)); // one in ten, and it clearly helps
        Assert.Equal(LessonStanding.Working, Of(helped: 10, misled: 2)); // two of twelve, and net 0.67
        Assert.Equal(LessonStanding.Misleading, Of(helped: 3, misled: 1)); // a quarter
        Assert.Equal(LessonStanding.Misleading, Of(helped: 3, misled: 2, noMatter: 4)); // two of nine, and net 0.11
        Assert.Equal(LessonStanding.LittleEffect, Of(helped: 3, misled: 1, noMatter: 5)); // one of nine, net 0.22
    }

    [Theory]
    [InlineData(7, 2)]
    [InlineData(30, 5)]
    [InlineData(90, 15)]
    public void RarelyMatched_ScalesWithThePeriod(int days, int below) => Assert.Equal(below, LessonMetrics.RarelyBelow(days));

    [Fact]
    public void Scored_AreTheUsableLessons_AndAnyOtherGivenInThePeriod_MostGivenFirst()
    {
        var usable = Lesson();
        var busy = Lesson();
        var disabledButGiven = Lesson(usable: false);
        var disabledUnused = Lesson(usable: false);
        var suggestion = Lesson(status: LessonStatus.Suggested, usable: false, approvedRevision: null);

        var report = Compute([usable, busy, disabledButGiven, disabledUnused, suggestion],
            [.. Uses(busy, Helped, Helped), .. Uses(disabledButGiven, Misled)]);

        Assert.Equal([busy.Id, disabledButGiven.Id, usable.Id], report.Lessons.Select(s => s.LessonId));
        Assert.False(report.Lessons.Single(s => s.LessonId == disabledButGiven.Id).Usable);
    }

    [Fact]
    public void FirstHandoffComparison_IsWithinTheLessonsProject_AndNeedsFiveAcceptedTasksEachSide()
    {
        var lesson = Lesson();
        var with = Enumerable.Range(0, 5).Select(_ => Task()).ToList();
        var without = Enumerable.Range(0, 5).Select(_ => Task()).ToList();
        var openTask = Task(LifecycleState.Running);
        var elsewhere = Enumerable.Range(0, 5).Select(_ => Task(project: Guid.NewGuid())).ToList();
        // Four of the five given it were accepted first time; two of the five without it.
        FlowRun[] runs = [new(with[0].Id, RunKind.Rework), .. without.Take(3).Select(t => new FlowRun(t.Id, RunKind.Rework))];
        List<LessonGiven> given = [.. with.Select(t => Given(lesson, t, Helped)), Given(lesson, openTask, Helped)];

        var score = Compute([lesson], given, [.. with, .. without, openTask, .. elsewhere], runs).Lessons.Single();

        Assert.Equal(new Comparison(0.8, 5, 0.4, 5), score.FirstHandoff);
        Assert.Null(Compute([lesson], given, [.. with, .. without.Take(4)], runs).Lessons.Single().FirstHandoff);
    }

    [Fact]
    public void ByRank_CountsVerdictsAtEachRankRetrievalGave()
    {
        var a = Lesson();
        var b = Lesson();
        var task = Task();
        List<LessonGiven> given =
        [
            Given(a, task, Helped, position: 0), Given(a, task, Helped, position: 0), Given(b, task, Misled, position: 1), Given(b, task, null, position: 4),
            Given(b, task, position: 5, inclusion: LessonInclusion.NotIncluded),
        ];

        var ranks = Compute([a, b], given).ByRank;

        Assert.Equal([1, 2, 3, 4, 5], ranks.Select(r => r.Rank));
        Assert.Equal(new RankVerdicts(1, 2, 0, 0, 0), ranks[0]);
        Assert.Equal(new RankVerdicts(2, 0, 0, 1, 0), ranks[1]);
        Assert.Equal(new RankVerdicts(5, 0, 0, 0, 1), ranks[4]);
    }

    [Fact]
    public void Proposals_InThePeriod_AreSortedByWhatBecameOfThem_AndPeoplesOwnCountedApart()
    {
        LessonRow[] lessons =
        [
            Lesson(proposed: true, createdDaysAgo: 3), Lesson(proposed: true, approvedRevision: 2, createdDaysAgo: 3),
            Lesson(proposed: true, status: LessonStatus.Dismissed, usable: false, approvedRevision: null, createdDaysAgo: 3),
            Lesson(proposed: true, status: LessonStatus.Suggested, usable: false, approvedRevision: null, createdDaysAgo: 3),
            Lesson(createdDaysAgo: 3), Lesson(proposed: true, createdDaysAgo: 45),
        ];

        Assert.Equal(new ProposalOutcomes(1, 1, 1, 1, 1), Compute(lessons, []).Proposals);
    }

    [Fact]
    public void BySource_IsHowOftenLessonsOfEachKindHelped_BestFirst()
    {
        var correction = Lesson(source: LessonSource.HumanCorrection);
        var finding = Lesson(source: LessonSource.ReviewFinding);
        var finding2 = Lesson(source: LessonSource.ReviewFinding);

        var sources = Compute([correction, finding, finding2],
            [.. Uses(correction, Helped, Helped, null), .. Uses(finding, Helped, Misled), .. Uses(finding2, NoMatter)]).BySource;

        Assert.Equal(
            [new SourceHelp(LessonSource.HumanCorrection, 1, 2, 2), new SourceHelp(LessonSource.ReviewFinding, 2, 3, 1)],
            sources);
    }

    [Fact]
    public void Improvements_NameTheChangeToMake_MostUrgentFirst()
    {
        var misleading = Lesson("Prefer Span<byte>.");
        var noEffect = Lesson();
        var unmatched = Lesson();
        var rarely = Lesson();
        var unjudged = Lesson();
        var crowded = Lesson();
        var task = Task();
        List<LessonGiven> given =
        [
            .. Uses(misleading, Misled, Misled, Misled, Helped, NoMatter),
            .. Uses(noEffect, NoMatter, NoMatter, NoMatter, Helped),
            .. Uses(rarely, Helped, Helped),
            .. Uses(unjudged, null, null, null),
            .. Uses(crowded, Helped, Helped, Helped, Helped, Helped),
            Given(crowded, task, inclusion: LessonInclusion.NotIncluded), Given(crowded, task, inclusion: LessonInclusion.NotIncluded),
        ];

        var items = Compute([misleading, noEffect, unmatched, rarely, unjudged, crowded], given).Improvements;

        Assert.Equal(
            [
                (ImprovementSeverity.FixNow, $"L-{misleading.Number}", "Misled 3 of the 5 runs that judged it.", "Edit the rule, or disable it."),
                (ImprovementSeverity.Improve, $"L-{noEffect.Number}", "Did not matter in 3 of 4 judged runs, and it is input on every call.", "Narrow its topics, or disable it."),
                (ImprovementSeverity.Improve, $"L-{unmatched.Number}", "No task matched it in the last 30 days.", "Check its topics against how tasks are worded."),
                (ImprovementSeverity.Opportunity, $"L-{rarely.Number}", "Helped 2 of 2 judged runs, but only 2 runs were given it.", "Add topics or paths so more tasks match it."),
                (ImprovementSeverity.Opportunity, $"L-{crowded.Number}", "Left out past the lesson limit 2 times.", "Raise the limit, or merge lessons that overlap."),
                (ImprovementSeverity.MissingData, $"L-{unjudged.Number}", "Given 3 times, judged 0.", "Ask testers to judge it in the run's details."),
            ],
            items.Select(i => (i.Severity, i.Subject, i.Problem, i.Action)));
        Assert.Equal((misleading.Id, Project, "Prefer Span<byte>."), (items[0].LessonId, items[0].ProjectId, items[0].Rule));
    }

    [Fact]
    public void Improvements_LeaveOutADisabledLessonThatHadLittleEffectOrNoMatch()
    {
        var disabled = Lesson(usable: false);

        Assert.Empty(Compute([disabled], Uses(disabled, NoMatter, NoMatter, NoMatter)).Improvements);
    }
}
