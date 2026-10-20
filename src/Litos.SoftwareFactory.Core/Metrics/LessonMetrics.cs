using Litos.SoftwareFactory.Core.Lessons;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Orchestration;

namespace Litos.SoftwareFactory.Core.Metrics;

// ---- What the lesson report is computed from: rows the store already keeps ----

/// <summary>A lesson in one of the caller's projects, with its approved revision (or its newest, before approval).</summary>
/// <param name="Usable">Approved, enabled and not superseded: runs may be given it.</param>
/// <param name="ProposedByReflection">An agent proposed it; otherwise a person wrote it.</param>
public sealed record LessonRow(
    Guid Id, Guid ProjectId, long Number, LessonStatus Status, bool Usable, string Rule, LessonSource Source, int Revision,
    int? ApprovedRevision, bool ProposedByReflection, DateTimeOffset CreatedAt, DateTimeOffset? ApprovedAt);

/// <summary>One lesson in one run's snapshot, given or left out, with the run's task.</summary>
/// <param name="Position">Its place in the order retrieval ranked the run's lessons, from 0.</param>
public sealed record LessonGiven(Guid LessonId, Guid ThreadId, LessonInclusion Inclusion, int Position, LessonVerdict? Verdict, DateTimeOffset IncludedAt);

/// <param name="Given">Snapshots taken in the period.</param>
/// <param name="Threads">Tasks created in the period, whose outcomes the comparisons use.</param>
public sealed record LessonData(IReadOnlyList<LessonRow> Lessons, IReadOnlyList<LessonGiven> Given, IReadOnlyList<FlowThread> Threads, IReadOnlyList<FlowRun> Runs);

// ---- What it comes to ----

/// <summary>Where a lesson stands, from how often it was given and what people said it did.</summary>
public enum LessonStanding
{
    /// <summary>Given often, and it helps.</summary>
    Working,

    /// <summary>It helps when given, but few tasks match it.</summary>
    RarelyMatched,

    /// <summary>Given, but people mostly said it did not matter.</summary>
    LittleEffect,

    /// <summary>People said it misled the agent.</summary>
    Misleading,

    /// <summary>Given, but judged too seldom to say.</summary>
    TooFewVerdicts,

    /// <summary>Usable for a while, and no task matched it in the period.</summary>
    NeverMatched,

    /// <summary>Approved too recently to have been matched.</summary>
    New,
}

/// <summary>First-handoff acceptance of the project's accepted tasks given the lesson, and of those not given it.</summary>
public sealed record Comparison(double With, int WithTasks, double Without, int WithoutTasks);

/// <param name="Net">Helped less misled, over judged uses; null with none judged.</param>
/// <param name="FirstHandoff">Null while either side has fewer than <see cref="LessonMetrics.MinTasksToCompare"/> tasks.</param>
public sealed record LessonScore(
    Guid LessonId, Guid ProjectId, long Number, string Rule, LessonSource Source, int Revision, bool Usable,
    int Given, int Manual, int LeftOut, int Helped, int DidNotMatter, int Misled, int Unjudged, double? Net,
    Comparison? FirstHandoff, DateTimeOffset? LastGiven, LessonStanding Standing);

/// <summary>Verdicts on the lessons given at one rank, from 1.</summary>
public sealed record RankVerdicts(int Rank, int Helped, int DidNotMatter, int Misled, int Unjudged);

/// <summary>What became of the lessons proposed or written in the period.</summary>
public sealed record ProposalOutcomes(int ApprovedAsWritten, int ApprovedAfterEdit, int Dismissed, int Waiting, int WrittenByPeople);

/// <summary>How often lessons from one kind of source helped once given.</summary>
public sealed record SourceHelp(LessonSource Source, int Lessons, int Judged, int Helped);

public enum ImprovementSeverity
{
    FixNow,
    Improve,
    Opportunity,
    MissingData,
}

/// <summary>A change to make to a lesson (or, later, a skill), with why.</summary>
/// <param name="Subject">How people refer to it: "L-12".</param>
public sealed record Improvement(ImprovementSeverity Severity, string Subject, Guid? LessonId, Guid? ProjectId, string Rule, string Problem, string Action);

public sealed record LessonReport(
    int Given, int Judged, int Helped, int Misled, int LeftOut,
    IReadOnlyList<LessonScore> Lessons,
    IReadOnlyList<RankVerdicts> ByRank,
    ProposalOutcomes Proposals,
    IReadOnlyList<SourceHelp> BySource,
    IReadOnlyList<Improvement> Improvements);

/// <summary>
/// How the project lessons are doing, so they can be improved: per lesson, how often runs were
/// given it and left it out, what people said it did (§12.1 keeps that apart from inclusion), and
/// whether tasks given it were accepted first time more often than the project's others; then
/// whether ranking puts the useful ones first, which sources make lessons that help, and a ranked
/// list of changes to make. Computed on read, like <see cref="FlowMetrics"/>.
/// </summary>
public static class LessonMetrics
{
    /// <summary>Both sides of a with-and-without comparison need this many accepted tasks.</summary>
    public const int MinTasksToCompare = 5;

    /// <summary>Fewer judged uses than this say too little.</summary>
    public const int MinVerdicts = 2;

    /// <summary>A net verdict at least this high counts as helping.</summary>
    public const double HelpsAt = 0.4;

    /// <summary>A lesson approved more recently than this is new, not unmatched.</summary>
    public static readonly TimeSpan NewFor = TimeSpan.FromDays(7);

    /// <summary>Given fewer times than this in the period, a helpful lesson is rarely matched: five a month, scaled, at least two.</summary>
    public static int RarelyBelow(int days) => Math.Max(2, (int)Math.Round(5 * days / 30.0));

    public static LessonReport Compute(LessonData data, DateTimeOffset now, int days)
    {
        var given = data.Given.Where(g => g.Inclusion != LessonInclusion.NotIncluded).ToList();
        var leftOut = data.Given.Where(g => g.Inclusion == LessonInclusion.NotIncluded).ToList();
        var outcomes = Outcomes(data);
        var givenThreads = given.GroupBy(g => g.LessonId).ToDictionary(g => g.Key, g => g.Select(x => x.ThreadId).ToHashSet());

        var scores = data.Lessons
            .Where(l => l.Usable || data.Given.Any(g => g.LessonId == l.Id))
            .Select(l => Score(l, given, leftOut, outcomes, givenThreads.GetValueOrDefault(l.Id) ?? [], now, days))
            .OrderByDescending(s => s.Given).ThenBy(s => s.Number)
            .ToList();

        var judged = given.Where(g => g.Verdict is not null).ToList();
        var since = now.AddDays(-days);
        var recent = data.Lessons.Where(l => l.CreatedAt >= since).ToList();
        var proposed = recent.Where(l => l.ProposedByReflection).ToList();
        var lessonsById = data.Lessons.ToDictionary(l => l.Id);

        return new LessonReport(
            given.Count, judged.Count, judged.Count(g => g.Verdict == LessonVerdict.Helped), judged.Count(g => g.Verdict == LessonVerdict.Misled), leftOut.Count,
            scores,
            [.. Enumerable.Range(1, LessonRetrieval.MaxLessons).Select(rank =>
            {
                var at = given.Where(g => g.Position + 1 == rank).ToList();
                return new RankVerdicts(rank, Count(at, LessonVerdict.Helped), Count(at, LessonVerdict.DidNotMatter), Count(at, LessonVerdict.Misled), at.Count(g => g.Verdict is null));
            })],
            new ProposalOutcomes(
                proposed.Count(l => l.Status == LessonStatus.Approved && l.ApprovedRevision == 1),
                proposed.Count(l => l.Status == LessonStatus.Approved && l.ApprovedRevision > 1),
                proposed.Count(l => l.Status == LessonStatus.Dismissed),
                proposed.Count(l => l.Status == LessonStatus.Suggested),
                recent.Count(l => !l.ProposedByReflection)),
            [.. given.Where(g => lessonsById.ContainsKey(g.LessonId))
                .GroupBy(g => lessonsById[g.LessonId].Source)
                .Select(g => new SourceHelp(g.Key, g.Select(x => x.LessonId).Distinct().Count(), g.Count(x => x.Verdict is not null), Count(g, LessonVerdict.Helped)))
                .OrderByDescending(s => s.Judged == 0 ? -1 : (double)s.Helped / s.Judged).ThenBy(s => s.Source)],
            Improvements(scores, days));
    }

    private static int Count(IEnumerable<LessonGiven> uses, LessonVerdict verdict) => uses.Count(g => g.Verdict == verdict);

    /// <summary>Each accepted task in the period: its project, and whether it was accepted at its first handoff.</summary>
    private static Dictionary<Guid, (Guid ProjectId, bool FirstTime)> Outcomes(LessonData data)
    {
        var reworked = data.Runs.Where(r => r.Kind == RunKind.Rework).Select(r => r.ThreadId).ToHashSet();
        return data.Threads.Where(t => t.State == LifecycleState.Accepted).ToDictionary(t => t.Id, t => (t.ProjectId, !reworked.Contains(t.Id)));
    }

    private static LessonScore Score(
        LessonRow lesson, List<LessonGiven> given, List<LessonGiven> leftOut, Dictionary<Guid, (Guid ProjectId, bool FirstTime)> outcomes,
        HashSet<Guid> threadsGiven, DateTimeOffset now, int days)
    {
        var own = given.Where(g => g.LessonId == lesson.Id).ToList();
        var (helped, didNotMatter, misled) = (Count(own, LessonVerdict.Helped), Count(own, LessonVerdict.DidNotMatter), Count(own, LessonVerdict.Misled));
        var judged = helped + didNotMatter + misled;
        double? net = judged == 0 ? null : (double)(helped - misled) / judged;

        var accepted = outcomes.Where(o => o.Value.ProjectId == lesson.ProjectId).ToList();
        var with = accepted.Where(o => threadsGiven.Contains(o.Key)).ToList();
        var without = accepted.Where(o => !threadsGiven.Contains(o.Key)).ToList();
        var comparison = with.Count >= MinTasksToCompare && without.Count >= MinTasksToCompare
            ? new Comparison(Rate(with), with.Count, Rate(without), without.Count)
            : null;

        return new LessonScore(
            lesson.Id, lesson.ProjectId, lesson.Number, lesson.Rule, lesson.Source, lesson.ApprovedRevision ?? lesson.Revision, lesson.Usable,
            own.Count, own.Count(g => g.Inclusion == LessonInclusion.Manual), leftOut.Count(g => g.LessonId == lesson.Id),
            helped, didNotMatter, misled, own.Count - judged, net, comparison,
            own.Count == 0 ? null : own.Max(g => g.IncludedAt),
            Standing(lesson, own.Count, judged, misled, net, now, days));

        static double Rate(List<KeyValuePair<Guid, (Guid ProjectId, bool FirstTime)>> tasks) =>
            Math.Round((double)tasks.Count(t => t.Value.FirstTime) / tasks.Count, 4);
    }

    /// <summary>
    /// Misleading wins over everything once judged: a quarter of the verdicts, or two misled uses
    /// on a lesson that does not otherwise clearly help.
    /// </summary>
    public static LessonStanding Standing(LessonRow lesson, int given, int judged, int misled, double? net, DateTimeOffset now, int days)
    {
        if (given == 0)
            return lesson.ApprovedAt is { } approved && now - approved < NewFor ? LessonStanding.New : LessonStanding.NeverMatched;
        if (judged < MinVerdicts)
            return LessonStanding.TooFewVerdicts;
        if (misled > 0 && (misled * 4 >= judged || (misled >= 2 && net < HelpsAt)))
            return LessonStanding.Misleading;
        if (net >= HelpsAt)
            return given < RarelyBelow(days) ? LessonStanding.RarelyMatched : LessonStanding.Working;
        return LessonStanding.LittleEffect;
    }

    /// <summary>The changes to make, most urgent first and, within that, the best-evidenced first.</summary>
    private static List<Improvement> Improvements(IReadOnlyList<LessonScore> scores, int days)
    {
        List<(Improvement Item, int Weight, long Number)> items = [];
        foreach (var s in scores)
        {
            var judged = s.Helped + s.DidNotMatter + s.Misled;
            void Add(ImprovementSeverity severity, int weight, string problem, string action) =>
                items.Add((new Improvement(severity, $"L-{s.Number}", s.LessonId, s.ProjectId, s.Rule, problem, action), weight, s.Number));

            switch (s.Standing)
            {
                case LessonStanding.Misleading:
                    Add(ImprovementSeverity.FixNow, s.Misled, $"Misled {s.Misled} of the {judged} runs that judged it.", "Edit the rule, or disable it.");
                    break;
                case LessonStanding.LittleEffect when s.Usable:
                    Add(ImprovementSeverity.Improve, s.DidNotMatter, $"Did not matter in {s.DidNotMatter} of {judged} judged runs, and it is input on every call.", "Narrow its topics, or disable it.");
                    break;
                case LessonStanding.NeverMatched when s.Usable:
                    Add(ImprovementSeverity.Improve, 0, $"No task matched it in the last {days} days.", "Check its topics against how tasks are worded.");
                    break;
                case LessonStanding.RarelyMatched:
                    Add(ImprovementSeverity.Opportunity, s.Helped, $"Helped {s.Helped} of {judged} judged runs, but only {s.Given} {Runs(s.Given)} given it.", "Add topics or paths so more tasks match it.");
                    break;
                case LessonStanding.TooFewVerdicts when s.Given >= 3:
                    Add(ImprovementSeverity.MissingData, s.Given, $"Given {s.Given} times, judged {judged}.", "Ask testers to judge it in the run's details.");
                    break;
            }

            if (s.LeftOut >= 2 && s.Standing is LessonStanding.Working or LessonStanding.RarelyMatched)
                Add(ImprovementSeverity.Opportunity, s.LeftOut, $"Left out past the lesson limit {s.LeftOut} times.", "Raise the limit, or merge lessons that overlap.");
        }

        return [.. items.OrderBy(i => i.Item.Severity).ThenByDescending(i => i.Weight).ThenBy(i => i.Number).Select(i => i.Item)];

        static string Runs(int n) => n == 1 ? "run was" : "runs were";
    }
}
