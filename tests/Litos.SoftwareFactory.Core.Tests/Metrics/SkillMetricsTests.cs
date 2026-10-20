using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Metrics;
using Litos.SoftwareFactory.Core.Orchestration;

namespace Litos.SoftwareFactory.Core.Tests.Metrics;

/// <summary>How the factory's skills are doing, judged by outcomes only (m4-architecture.md §12.5).</summary>
public class SkillMetricsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Project = Guid.NewGuid();

    private static SkillThread Task(LifecycleState state = LifecycleState.Accepted, string type = "feature", Guid? project = null) =>
        new(Guid.NewGuid(), project ?? Project, type, state);

    private static SkillRun Run(SkillThread task, int calls = 11, GivenSkill[]? chosen = null, string[]? offered = null, string[]? repository = null) =>
        new(Guid.NewGuid(), task.Id, RunKind.Implement, calls, chosen ?? [], [.. (offered ?? []).Concat(repository ?? [])], new HashSet<string>(repository ?? []));

    private static SkillReport Compute(
        IReadOnlyList<SkillThread> tasks, IReadOnlyList<SkillRun> runs, IReadOnlyList<SkillLoad>? loads = null,
        IReadOnlyList<RepositorySkillVersion>? repository = null, IReadOnlyDictionary<string, int>? library = null) =>
        SkillMetrics.Compute(new SkillData(tasks, runs, loads ?? [], repository ?? [], library ?? new Dictionary<string, int>()), Now, 30);

    [Fact]
    public void Cost_IsTheFirstCallInFull_AndEveryLaterOneAtTheCachedWeight()
    {
        Assert.Equal(1_000, SkillMetrics.CostOf(4_000, 1));
        Assert.Equal(2_000, SkillMetrics.CostOf(4_000, 11)); // 1,000 + 10 × 100
        Assert.Equal(1_000, SkillMetrics.CostOf(4_000, 0));
    }

    [Fact]
    public void ASkillsUse_IsCountedAsChosen_Offered_AndLoaded()
    {
        var a = Task();
        var b = Task();
        var chosenRun = Run(a, calls: 11, chosen: [new("dotnet-testing", 4_000)], offered: ["ef-migrations"]);
        var offeredRun = Run(b, calls: 1, offered: ["ef-migrations"]);
        SkillLoad[] loads = [new(offeredRun.RunId, "ef-migrations"), new(offeredRun.RunId, "ef-migrations")];

        var reflection = Run(a) with { Kind = RunKind.Reflection };

        var report = Compute([a, b], [chosenRun, offeredRun, reflection], loads, library: new Dictionary<string, int> { ["ef-migrations"] = 2_000 });

        Assert.Equal((2, 2, 2), (report.Runs, report.RunsWithSkills, report.Loads)); // the reflection run is not counted
        var testing = report.Skills.Single(s => s.Name == "dotnet-testing");
        Assert.Equal((1, 0, 0, 0, 2_000L, "Library"), (testing.RunsChosen, testing.RunsOffered, testing.RunsLoaded, testing.Loads, testing.TokensPerRun!.Value, testing.Origin));
        var migrations = report.Skills.Single(s => s.Name == "ef-migrations");
        Assert.Equal((0, 2, 1, 2, 500L), (migrations.RunsChosen, migrations.RunsOffered, migrations.RunsLoaded, migrations.Loads, migrations.TokensPerRun!.Value));
    }

    [Fact]
    public void ARepositorySkill_IsCostedFromItsOwnText_AndNamedAsTheRepositorys()
    {
        var task = Task();
        var run = Run(task, calls: 1, repository: ["page-format"]);
        RepositorySkillVersion[] versions = [new(Project, "page-format", 1_200, Now.AddDays(-1), true)];

        var skill = Compute([task], [run], [new(run.RunId, "page-format")], versions, new Dictionary<string, int> { ["page-format"] = 99_999 }).Skills.Single();

        Assert.Equal(("Repository", 300L), (skill.Origin, skill.TokensPerRun!.Value));
    }

    [Fact]
    public void FirstHandoffComparison_IsAgainstTasksOfTheSameProjectAndType_AndNeedsEightEachSide()
    {
        var with = Enumerable.Range(0, 8).Select(_ => Task()).ToList();
        var without = Enumerable.Range(0, 8).Select(_ => Task()).ToList();
        var otherType = Enumerable.Range(0, 8).Select(_ => Task(type: "bug")).ToList();
        var otherProject = Enumerable.Range(0, 8).Select(_ => Task(project: Guid.NewGuid())).ToList();
        List<SkillRun> runs =
        [
            .. with.Select(t => Run(t, chosen: [new("dotnet-testing", 400)])),
            .. without.Concat(otherType).Concat(otherProject).Select(t => Run(t)),
            // Two of those with it, and six of those without, were sent back once.
            .. with.Take(2).Concat(without.Take(6)).Select(t => Run(t) with { Kind = RunKind.Rework }),
        ];

        var skill = Compute([.. with, .. without, .. otherType, .. otherProject], runs).Skills.Single();

        Assert.Equal(new Comparison(0.75, 8, 0.25, 8), skill.FirstHandoff);
        Assert.Equal(SkillStanding.EarnsItsPlace, skill.Standing);
        Assert.Null(Compute([.. with, .. without.Take(7)], runs).Skills.Single().FirstHandoff);
    }

    [Fact]
    public void Standing_FollowsLoadsCostAndTheComparison()
    {
        Comparison Of(double with, double without) => new(with, 10, without, 10);

        Assert.Equal(SkillStanding.NeverLoaded, SkillMetrics.Standing(0, 5, 0, null, null));
        Assert.Equal(SkillStanding.TooFewTasks, SkillMetrics.Standing(0, 4, 0, null, null)); // offered too seldom to say
        Assert.Equal(SkillStanding.TooFewTasks, SkillMetrics.Standing(3, 0, 0, 900, null));
        Assert.Equal(SkillStanding.WorseWithIt, SkillMetrics.Standing(10, 0, 0, 900, Of(0.5, 0.6)));
        Assert.Equal(SkillStanding.EarnsItsPlace, SkillMetrics.Standing(10, 0, 0, 9_000, Of(0.7, 0.6)));
        Assert.Equal(SkillStanding.CostlyNoGain, SkillMetrics.Standing(10, 0, 0, 9_000, Of(0.6, 0.6)));
        Assert.Equal(SkillStanding.NoClearEffect, SkillMetrics.Standing(10, 0, 0, 900, Of(0.6, 0.6))); // cheap
        Assert.Equal(SkillStanding.NoClearEffect, SkillMetrics.Standing(0, 10, 10, 9_000, Of(0.6, 0.6))); // loaded on demand already
    }

    [Fact]
    public void ARepositorySkillChangedSinceItsApproval_IsNamed_EvenWithNoRunInThePeriod()
    {
        RepositorySkillVersion[] versions =
        [
            new(Project, "provider-mocks", 900, Now.AddDays(-20), true),
            new(Project, "provider-mocks", 950, Now.AddDays(-2), false),
            new(Project, "never-approved", 500, Now.AddDays(-2), false),
        ];

        var report = Compute([], [], repository: versions);

        var changed = Assert.Single(report.Skills);
        Assert.Equal(("provider-mocks", true, "Repository"), (changed.Name, changed.ChangedSinceApproval, changed.Origin));
        Assert.Equal("Review and approve the new version.", Assert.Single(report.Improvements).Action);
    }

    [Fact]
    public void Improvements_NameTheChangeToMake_MostUrgentFirst()
    {
        var with = Enumerable.Range(0, 8).Select(_ => Task()).ToList();
        var without = Enumerable.Range(0, 8).Select(_ => Task()).ToList();
        var offeredOnly = Enumerable.Range(0, 5).Select(_ => Task(LifecycleState.Running)).ToList();
        List<SkillRun> runs =
        [
            // "ui-copy" is chosen for the first eight tasks, and "security-review", which is large, for all sixteen.
            .. with.Select(t => Run(t, calls: 21, chosen: [new("ui-copy", 400), new("security-review", 20_000)])),
            .. without.Select(t => Run(t, calls: 21, chosen: [new("security-review", 20_000)])),
            .. with.Take(4).Select(t => Run(t) with { Kind = RunKind.Rework }),
            .. offeredOnly.Select(t => Run(t, offered: ["csv-and-encoding"])),
        ];

        var report = Compute([.. with, .. without, .. offeredOnly], runs);

        Assert.Equal(
            [
                (ImprovementSeverity.FixNow, "ui-copy", "Tasks with it were accepted first time 50 percentage points less often (50% of 8, against 100% of 8 without).", "Review what it tells the agent."),
                (ImprovementSeverity.Improve, "csv-and-encoding", "Offered to 5 runs, and never loaded.", "Rewrite its description so the agent knows when to load it, or remove it."),
            ],
            report.Improvements.Select(i => (i.Severity, i.Subject, i.Problem, i.Action)));
        // security-review was given to every task, so nothing comparable lacked it.
        Assert.Equal(SkillStanding.TooFewTasks, report.Skills.Single(s => s.Name == "security-review").Standing);
    }

    [Fact]
    public void ACostlyChosenSkillWithNoGain_IsToldToLoadOnDemand()
    {
        var with = Enumerable.Range(0, 8).Select(_ => Task()).ToList();
        var without = Enumerable.Range(0, 8).Select(_ => Task()).ToList();
        List<SkillRun> runs = [.. with.Select(t => Run(t, calls: 11, chosen: [new("security-review", 20_000)])), .. without.Select(t => Run(t))];

        var item = Assert.Single(Compute([.. with, .. without], runs).Improvements);

        Assert.Equal(
            (ImprovementSeverity.Improve, "Costs about 10,000 tokens a run, and tasks with it did no better (100% against 100% accepted first time).",
                "Let it load on demand instead of choosing it for every task."),
            (item.Severity, item.Problem, item.Action));
        Assert.Null(item.LessonId);
    }
}
