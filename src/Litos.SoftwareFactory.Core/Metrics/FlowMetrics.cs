using Litos.SoftwareFactory.Core.Lessons;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Orchestration;

namespace Litos.SoftwareFactory.Core.Metrics;

// ---- What the metrics are computed from: rows the store already keeps (m4-architecture.md §6) ----

public sealed record FlowThread(Guid Id, Guid ProjectId, string Title, LifecycleState State, DateTimeOffset CreatedAt);

/// <summary>A thread's state and stage as one of its "state" outbox events recorded them, with the waiting reason.</summary>
public sealed record FlowStateEvent(Guid ThreadId, DateTimeOffset At, string State, string Stage, string? Reason);

public sealed record FlowUsage(Guid ThreadId, string? Phase, string Model, long Charged);

public sealed record FlowRun(Guid ThreadId, RunKind Kind);

/// <param name="Title">Its task's title, which may be older than the period.</param>
public sealed record FlowDecision(Guid ThreadId, string Title, string Question, DateTimeOffset CreatedAt, bool Open);

public sealed record FlowLessonUse(Guid ThreadId, LessonInclusion Inclusion, LessonVerdict? Verdict);

public sealed record FlowData(
    IReadOnlyList<FlowThread> Threads, IReadOnlyList<FlowStateEvent> Events, IReadOnlyList<FlowUsage> Usage, IReadOnlyList<FlowRun> Runs,
    IReadOnlyList<FlowDecision> Decisions, IReadOnlyList<FlowLessonUse> Lessons);

// ---- What they come to ----

/// <param name="Count">How many of the tasks spent any time there.</param>
public sealed record TimeIn(string Name, int Count, double TotalHours, double MedianHours, double P90Hours);

public sealed record TokenShare(string Name, long Tokens, double Share);

public sealed record Spread(int Count, double Median, double P90);

public sealed record OldestDecision(Guid ThreadId, string Title, string Question, double AgeHours);

public sealed record LessonUse(int Included, int LeftOut, int Helped, int DidNotMatter, int Misled, int Unjudged);

/// <summary>One task in the period, for the tokens-per-task view.</summary>
public sealed record FlowTask(Guid ThreadId, Guid ProjectId, string Title, string State, long Tokens, int ReworkRounds);

/// <summary>One stretch of the period, oldest first: tasks created in it, and tasks accepted in it.</summary>
/// <param name="AcceptedFirstTime">Of those accepted, how many needed no change request.</param>
public sealed record FlowBucket(DateTimeOffset Start, int Days, int Created, int Accepted, int AcceptedFirstTime);

/// <param name="FirstHandoffAcceptance">Accepted tasks that needed no change request, over all accepted tasks; null with none accepted.</param>
/// <param name="ReworkRounds">Change requests per accepted task, as how many tasks needed each number.</param>
/// <param name="QueueForRepository">Minutes a task waited in the queue for its repository's lock.</param>
/// <param name="QueueForSlot">Minutes a task waited in the queue for a free slot.</param>
/// <param name="PerTask">Every task in the period with what it was charged, most first.</param>
/// <param name="Series">The period in equal stretches, for trends; empty when no period was given.</param>
public sealed record FlowReport(
    int Tasks, int Accepted, int Cancelled, int Open,
    double? FirstHandoffAcceptance,
    IReadOnlyList<(int Rounds, int Tasks)> ReworkRounds,
    IReadOnlyList<TimeIn> TimeInState,
    IReadOnlyList<TimeIn> TimeInStage,
    Spread TokensPerTask, long TokensTotal,
    IReadOnlyList<TokenShare> TokensByPhase,
    IReadOnlyList<TokenShare> TokensByModel,
    int OpenDecisions, OldestDecision? OldestOpenDecision,
    Spread QueueForRepository, Spread QueueForSlot,
    LessonUse Lessons,
    IReadOnlyList<FlowTask> PerTask,
    IReadOnlyList<FlowBucket> Series);

/// <summary>
/// The flow metrics of ReadMe_LitosSoftwareFactory_V1.md §12.1 (m4-architecture.md §6), computed on
/// read from records the factory already keeps: the outbox's state events give the time in each state
/// and stage and the stretches spent queued, with what each waited for; usage gives tokens by phase and
/// model; runs give rework rounds; decisions and lesson snapshots the rest. Lesson inclusion and the
/// verdicts on whether a lesson helped are reported apart (§12.1).
/// </summary>
public static class FlowMetrics
{
    private static readonly HashSet<string> Finished = [nameof(LifecycleState.Accepted), nameof(LifecycleState.Cancelled)];

    /// <param name="days">The period's length, for <see cref="FlowReport.Series"/>; 0 for none.</param>
    public static FlowReport Compute(FlowData data, DateTimeOffset now, int days = 0)
    {
        var threads = data.Threads;
        var ids = threads.Select(t => t.Id).ToHashSet();
        var accepted = threads.Where(t => t.State == LifecycleState.Accepted).ToList();
        var reworks = data.Runs.Where(r => r.Kind == RunKind.Rework).GroupBy(r => r.ThreadId).ToDictionary(g => g.Key, g => g.Count());
        var rounds = accepted.Select(t => reworks.GetValueOrDefault(t.Id)).ToList();

        var (byState, byStage, repository, slot) = Times(threads, data.Events, now);
        var usage = data.Usage.Where(u => ids.Contains(u.ThreadId)).ToList();
        var perTask = threads.Select(t => (double)usage.Where(u => u.ThreadId == t.Id).Sum(u => u.Charged)).Where(t => t > 0).ToList();
        var total = usage.Sum(u => u.Charged);
        var open = data.Decisions.Where(d => d.Open).ToList();
        var oldest = open.OrderBy(d => d.CreatedAt).FirstOrDefault();
        var lessons = data.Lessons.Where(l => ids.Contains(l.ThreadId)).ToList();
        var included = lessons.Where(l => l.Inclusion != LessonInclusion.NotIncluded).ToList();

        return new FlowReport(
            threads.Count,
            accepted.Count,
            threads.Count(t => t.State == LifecycleState.Cancelled),
            threads.Count(t => !Finished.Contains(t.State.ToString())),
            accepted.Count == 0 ? null : (double)rounds.Count(r => r == 0) / accepted.Count,
            [.. rounds.GroupBy(r => r).OrderBy(g => g.Key).Select(g => (g.Key, g.Count()))],
            byState,
            byStage,
            SpreadOf(perTask),
            total,
            Shares(usage.GroupBy(u => string.IsNullOrEmpty(u.Phase) ? "Other" : u.Phase), total),
            Shares(usage.GroupBy(u => u.Model), total),
            open.Count,
            oldest is null
                ? null
                : new OldestDecision(oldest.ThreadId, oldest.Title, oldest.Question, Hours(now - oldest.CreatedAt)),
            SpreadOf(repository),
            SpreadOf(slot),
            new LessonUse(
                included.Count,
                lessons.Count - included.Count,
                included.Count(l => l.Verdict == LessonVerdict.Helped),
                included.Count(l => l.Verdict == LessonVerdict.DidNotMatter),
                included.Count(l => l.Verdict == LessonVerdict.Misled),
                included.Count(l => l.Verdict is null)),
            [.. threads
                .Select(t => new FlowTask(t.Id, t.ProjectId, t.Title, t.State.ToString(), usage.Where(u => u.ThreadId == t.Id).Sum(u => u.Charged), reworks.GetValueOrDefault(t.Id)))
                .OrderByDescending(t => t.Tokens)],
            Buckets(threads, data.Events, reworks, now, days));
    }

    /// <summary>
    /// The period in equal stretches: a day each over a week, three days over a month, and a week
    /// beyond. A task counts as created in the stretch it was created in, and as accepted in the
    /// stretch of its state event that recorded the acceptance.
    /// </summary>
    private static List<FlowBucket> Buckets(
        IReadOnlyList<FlowThread> threads, IReadOnlyList<FlowStateEvent> events, Dictionary<Guid, int> reworks, DateTimeOffset now, int days)
    {
        if (days <= 0)
            return [];
        var size = days <= 7 ? 1 : days <= 30 ? 3 : 7;
        var count = (int)Math.Ceiling((double)days / size);
        var start = now.AddDays(-count * size);
        var ids = threads.Select(t => t.Id).ToHashSet();
        var acceptedAt = events
            .Where(e => ids.Contains(e.ThreadId) && e.State == nameof(LifecycleState.Accepted))
            .GroupBy(e => e.ThreadId)
            .ToDictionary(g => g.Key, g => g.Min(e => e.At));
        // A time outside the period falls in no stretch.
        int? Index(DateTimeOffset at) => Math.Floor((at - start).TotalDays / size) is var i && i >= 0 && i < count ? (int)i : null;

        var buckets = new (int Created, int Accepted, int First)[count];
        foreach (var thread in threads)
        {
            if (Index(thread.CreatedAt) is { } created)
                buckets[created].Created++;
            if (acceptedAt.TryGetValue(thread.Id, out var at) && Index(at) is { } accepted)
            {
                buckets[accepted].Accepted++;
                if (reworks.GetValueOrDefault(thread.Id) == 0)
                    buckets[accepted].First++;
            }
        }

        return [.. buckets.Select((b, i) => new FlowBucket(start.AddDays(i * size), size, b.Created, b.Accepted, b.First))];
    }

    /// <summary>
    /// Walks each task's state events in time order: the time from one to the next belongs to the
    /// earlier one's state and stage, and the last runs to now unless the task is finished. A stretch
    /// in Queued is a queue wait, for the repository if any of its events said a task held it, else for
    /// a slot if one said so; one that waited for neither (claimed at once) counts for neither.
    /// </summary>
    private static (IReadOnlyList<TimeIn> ByState, IReadOnlyList<TimeIn> ByStage, List<double> Repository, List<double> Slot) Times(
        IReadOnlyList<FlowThread> threads, IReadOnlyList<FlowStateEvent> events, DateTimeOffset now)
    {
        var states = new Dictionary<string, Dictionary<Guid, double>>();
        var stages = new Dictionary<string, Dictionary<Guid, double>>();
        var repository = new List<double>();
        var slot = new List<double>();
        var byThread = events.GroupBy(e => e.ThreadId).ToDictionary(g => g.Key, g => g.OrderBy(e => e.At).ToList());

        foreach (var thread in threads)
        {
            var own = byThread.GetValueOrDefault(thread.Id) ?? [];
            if (own.Count == 0 || own[0].At > thread.CreatedAt)
                own.Insert(0, new FlowStateEvent(thread.Id, thread.CreatedAt, nameof(LifecycleState.Draft), nameof(Stage.Discuss), null));

            DateTimeOffset? queuedSince = null;
            var (waitedForRepository, waitedForSlot) = (false, false);
            for (var i = 0; i < own.Count; i++)
            {
                var current = own[i];
                var end = i + 1 < own.Count ? own[i + 1].At : Finished.Contains(current.State) ? current.At : now;
                var hours = Hours(end - current.At);
                Add(states, current.State, thread.Id, hours);
                Add(stages, current.Stage, thread.Id, hours);

                if (current.State == nameof(LifecycleState.Queued))
                {
                    queuedSince ??= current.At;
                    waitedForRepository |= current.Reason?.Contains("held by", StringComparison.Ordinal) == true;
                    waitedForSlot |= current.Reason?.StartsWith("Waiting for a free slot", StringComparison.Ordinal) == true;
                }

                var leavesQueue = i + 1 >= own.Count ? current.State == nameof(LifecycleState.Queued) : own[i + 1].State != nameof(LifecycleState.Queued);
                if (queuedSince is { } since && leavesQueue && current.State == nameof(LifecycleState.Queued))
                {
                    var minutes = (end - since).TotalMinutes;
                    if (waitedForRepository)
                        repository.Add(minutes);
                    else if (waitedForSlot)
                        slot.Add(minutes);
                    (queuedSince, waitedForRepository, waitedForSlot) = (null, false, false);
                }
            }
        }

        return (TimesIn(states), TimesIn(stages), repository, slot);
    }

    private static void Add(Dictionary<string, Dictionary<Guid, double>> into, string name, Guid thread, double hours)
    {
        if (hours <= 0)
            return;
        var perThread = into.TryGetValue(name, out var found) ? found : into[name] = [];
        perThread[thread] = perThread.GetValueOrDefault(thread) + hours;
    }

    private static List<TimeIn> TimesIn(Dictionary<string, Dictionary<Guid, double>> times) =>
        [.. times.OrderByDescending(t => t.Value.Values.Sum()).Select(t =>
        {
            var spread = SpreadOf([.. t.Value.Values]);
            return new TimeIn(t.Key, spread.Count, Round(t.Value.Values.Sum()), spread.Median, spread.P90);
        })];

    private static List<TokenShare> Shares(IEnumerable<IGrouping<string, FlowUsage>> groups, long total) =>
        [.. groups.Select(g => (Name: g.Key, Tokens: g.Sum(u => u.Charged)))
            .Where(g => g.Tokens > 0)
            .OrderByDescending(g => g.Tokens)
            .Select(g => new TokenShare(g.Name, g.Tokens, total == 0 ? 0 : Math.Round((double)g.Tokens / total, 4)))];

    /// <summary>The count, median and 90th percentile (nearest rank) of some values; zeros with none.</summary>
    public static Spread SpreadOf(IReadOnlyCollection<double> values)
    {
        if (values.Count == 0)
            return new Spread(0, 0, 0);
        var sorted = values.Order().ToList();
        return new Spread(sorted.Count, Round(Rank(sorted, 0.5)), Round(Rank(sorted, 0.9)));
    }

    private static double Rank(List<double> sorted, double percentile) => sorted[Math.Max(0, (int)Math.Ceiling(percentile * sorted.Count) - 1)];

    private static double Hours(TimeSpan span) => Math.Max(0, span.TotalHours);

    private static double Round(double value) => Math.Round(value, 2);
}
