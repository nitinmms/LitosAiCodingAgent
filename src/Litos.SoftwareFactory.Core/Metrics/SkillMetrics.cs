using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Orchestration;

namespace Litos.SoftwareFactory.Core.Metrics;

// ---- What the skill report is computed from: rows the store keeps ----

/// <summary>A task created in the period, with what makes tasks comparable: its project and type.</summary>
public sealed record SkillThread(Guid Id, Guid ProjectId, string TypeLabel, LifecycleState State);

/// <summary>A skill given whole in a run's system prompt (chosen for the task), with its size.</summary>
public sealed record GivenSkill(string Name, int Chars);

/// <summary>What one run of such a task was given, from its capabilities.</summary>
/// <param name="Calls">The model calls the run made, every one carrying its system prompt.</param>
/// <param name="Offered">Skills it could load by name: the library's and the repository's it used.</param>
/// <param name="FromRepository">Of those, the repository's own (each replaces a library skill of its name).</param>
public sealed record SkillRun(
    Guid RunId, Guid ThreadId, RunKind Kind, int Calls, IReadOnlyList<GivenSkill> Chosen, IReadOnlyList<string> Offered, IReadOnlySet<string> FromRepository);

public sealed record SkillLoad(Guid RunId, string Name);

/// <summary>One version of a repository's skill, as found in a working copy.</summary>
public sealed record RepositorySkillVersion(Guid ProjectId, string Name, int Chars, DateTimeOffset LastFoundAt, bool Approved);

/// <param name="LibraryChars">Each library skill's instructions, by name, as the library has them now.</param>
public sealed record SkillData(
    IReadOnlyList<SkillThread> Threads, IReadOnlyList<SkillRun> Runs, IReadOnlyList<SkillLoad> Loads,
    IReadOnlyList<RepositorySkillVersion> Repository, IReadOnlyDictionary<string, int> LibraryChars);

// ---- What it comes to ----

/// <summary>Where a skill stands, from how runs used it and how tasks with it fared. Skills get no verdicts (§12.1).</summary>
public enum SkillStanding
{
    /// <summary>Tasks with it were accepted first time clearly more often.</summary>
    EarnsItsPlace,

    /// <summary>No clear difference either way.</summary>
    NoClearEffect,

    /// <summary>Chosen for tasks, costly, and no clear gain.</summary>
    CostlyNoGain,

    /// <summary>Tasks with it were accepted first time clearly less often.</summary>
    WorseWithIt,

    /// <summary>Offered to load in several runs, and never loaded.</summary>
    NeverLoaded,

    /// <summary>Too few comparable tasks either side to say.</summary>
    TooFewTasks,
}

/// <param name="Origin">"Library" or "Repository".</param>
/// <param name="TokensPerRun">About what its text costs a run that has it, at the cached weight after the first call; null when no run had it.</param>
/// <param name="FirstHandoff">Comparable tasks (same project and type) with it against without; null while either side has fewer than <see cref="SkillMetrics.MinTasksToCompare"/>.</param>
/// <param name="ChangedSinceApproval">A repository skill whose folder changed since its approval: runs no longer get it until an Admin approves the new version.</param>
public sealed record SkillScore(
    string Name, string Origin, int RunsChosen, int RunsOffered, int RunsLoaded, int Loads, long? TokensPerRun,
    Comparison? FirstHandoff, SkillStanding Standing, bool ChangedSinceApproval);

public sealed record SkillReport(int Runs, int RunsWithSkills, int Loads, IReadOnlyList<SkillScore> Skills, IReadOnlyList<Improvement> Improvements);

/// <summary>
/// How the factory's skills are doing, so they can be improved (m4-architecture.md §12.5): per skill,
/// how many runs had it chosen, offered and loaded, about what it costs a run, and whether
/// comparable tasks with it were accepted first time more often; then the changes to make. Computed
/// on read, like <see cref="LessonMetrics"/>.
/// </summary>
public static class SkillMetrics
{
    /// <summary>Both sides of a with-and-without comparison need this many accepted tasks.</summary>
    public const int MinTasksToCompare = 8;

    /// <summary>A skill offered in this many runs and never loaded is never loaded.</summary>
    public const int OfferedAtLeast = 5;

    /// <summary>A chosen skill costing a run this much, with no clear gain, should load on demand instead.</summary>
    public const long CostlyFrom = 5_000;

    /// <summary>A difference in first-time acceptance this large, in either direction, is clear.</summary>
    public const double ClearDifference = 0.05;

    /// <summary>What cached input is charged at, by default (FACTORY_CACHED_INPUT_WEIGHT, blueprint §9.1).</summary>
    public const double CachedWeight = 0.1;

    private const int CharsPerToken = 4;

    /// <summary>About what text of this size costs a run: the first call in full, every later one from the cache.</summary>
    public static long CostOf(int chars, int calls) =>
        (long)Math.Ceiling((double)chars / CharsPerToken * (1 + CachedWeight * Math.Max(0, calls - 1)));

    public static SkillReport Compute(SkillData data, DateTimeOffset now, int days)
    {
        var threads = data.Threads.ToDictionary(t => t.Id);
        // A reflection run reads only its brief and has no skills (m4-architecture.md §5.2): it is not a run that could use one.
        var runs = data.Runs.Where(r => threads.ContainsKey(r.ThreadId) && r.Kind != RunKind.Reflection).ToList();
        var runIds = runs.Select(r => r.RunId).ToHashSet();
        var loads = data.Loads.Where(l => runIds.Contains(l.RunId)).ToList();
        var loadedIn = loads.GroupBy(l => l.Name).ToDictionary(g => g.Key, g => g.Select(l => l.RunId).ToHashSet());
        var reworked = data.Runs.Where(r => r.Kind == RunKind.Rework).Select(r => r.ThreadId).ToHashSet();
        var changed = Changed(data.Repository);

        var names = runs.SelectMany(r => r.Chosen.Select(c => c.Name).Concat(r.Offered))
            .Concat(loads.Select(l => l.Name))
            .Concat(changed)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

        var scores = names.Select(name => Score(name, runs, loadedIn.GetValueOrDefault(name) ?? [], loads.Count(l => l.Name == name),
                threads, reworked, data, changed.Contains(name)))
            .OrderByDescending(s => s.RunsChosen + s.RunsLoaded).ThenBy(s => s.Name, StringComparer.Ordinal)
            .ToList();

        return new SkillReport(
            runs.Count,
            runs.Count(r => r.Chosen.Count > 0 || r.Offered.Count > 0),
            loads.Count,
            scores,
            Improvements(scores));
    }

    /// <summary>Repository skills whose newest version waits for approval while an older one was approved.</summary>
    private static HashSet<string> Changed(IReadOnlyList<RepositorySkillVersion> versions) =>
        [.. versions.GroupBy(v => (v.ProjectId, v.Name))
            .Where(g => g.MaxBy(v => v.LastFoundAt) is { Approved: false } && g.Any(v => v.Approved))
            .Select(g => g.Key.Name)];

    private static SkillScore Score(
        string name, List<SkillRun> runs, HashSet<Guid> loadedRuns, int loads, Dictionary<Guid, SkillThread> threads, HashSet<Guid> reworked,
        SkillData data, bool changed)
    {
        var chosen = runs.Where(r => r.Chosen.Any(c => c.Name == name)).ToList();
        var offered = runs.Where(r => r.Offered.Contains(name)).ToList();
        var fromRepository = runs.Any(r => r.FromRepository.Contains(name)) || data.Repository.Any(v => v.Name == name);
        var repositoryChars = data.Repository.Where(v => v.Name == name).OrderByDescending(v => v.LastFoundAt).Select(v => (int?)v.Chars).FirstOrDefault();

        // What the skill cost each run that had it: in the prompt from the start, or read once loaded.
        var costs = runs
            .Select(r => r.Chosen.FirstOrDefault(c => c.Name == name) is { } given
                ? CostOf(given.Chars, r.Calls)
                : loadedRuns.Contains(r.RunId)
                    ? CostOf(r.FromRepository.Contains(name) ? repositoryChars ?? 0 : data.LibraryChars.GetValueOrDefault(name), r.Calls)
                    : (long?)null)
            .OfType<long>()
            .ToList();

        var withThreads = runs.Where(r => r.Chosen.Any(c => c.Name == name) || loadedRuns.Contains(r.RunId)).Select(r => r.ThreadId).ToHashSet();
        var accepted = threads.Values.Where(t => t.State == LifecycleState.Accepted).ToList();
        var with = accepted.Where(t => withThreads.Contains(t.Id)).ToList();
        // Comparable: the same projects and task types as the tasks that had it.
        var kinds = with.Select(t => (t.ProjectId, t.TypeLabel)).ToHashSet();
        var without = accepted.Where(t => !withThreads.Contains(t.Id) && kinds.Contains((t.ProjectId, t.TypeLabel))).ToList();
        double Rate(List<SkillThread> tasks) => Math.Round((double)tasks.Count(t => !reworked.Contains(t.Id)) / tasks.Count, 4);
        var comparison = with.Count >= MinTasksToCompare && without.Count >= MinTasksToCompare
            ? new Comparison(Rate(with), with.Count, Rate(without), without.Count)
            : null;
        var tokensPerRun = costs.Count == 0 ? (long?)null : (long)Math.Round(costs.Average());

        return new SkillScore(
            name, fromRepository ? "Repository" : "Library", chosen.Count, offered.Count, loadedRuns.Count, loads, tokensPerRun, comparison,
            Standing(chosen.Count, offered.Count, loads, tokensPerRun, comparison), changed);
    }

    public static SkillStanding Standing(int runsChosen, int runsOffered, int loads, long? tokensPerRun, Comparison? comparison)
    {
        if (runsChosen == 0 && runsOffered >= OfferedAtLeast && loads == 0)
            return SkillStanding.NeverLoaded;
        if (comparison is null)
            return SkillStanding.TooFewTasks;
        var difference = comparison.With - comparison.Without;
        if (difference < -ClearDifference)
            return SkillStanding.WorseWithIt;
        if (difference > ClearDifference)
            return SkillStanding.EarnsItsPlace;
        return runsChosen > 0 && tokensPerRun >= CostlyFrom ? SkillStanding.CostlyNoGain : SkillStanding.NoClearEffect;
    }

    private static List<Improvement> Improvements(IReadOnlyList<SkillScore> scores)
    {
        List<(Improvement Item, long Weight)> items = [];
        foreach (var s in scores)
        {
            void Add(ImprovementSeverity severity, long weight, string problem, string action) =>
                items.Add((new Improvement(severity, s.Name, null, null, "", problem, action), weight));
            var c = s.FirstHandoff;

            switch (s.Standing)
            {
                case SkillStanding.WorseWithIt:
                    Add(ImprovementSeverity.FixNow, c!.WithTasks,
                        $"Tasks with it were accepted first time {Points(c)} less often ({Percent(c.With)} of {c.WithTasks}, against {Percent(c.Without)} of {c.WithoutTasks} without).",
                        "Review what it tells the agent.");
                    break;
                case SkillStanding.CostlyNoGain:
                    Add(ImprovementSeverity.Improve, s.TokensPerRun ?? 0,
                        $"Costs about {s.TokensPerRun:N0} tokens a run, and tasks with it did no better ({Percent(c!.With)} against {Percent(c.Without)} accepted first time).",
                        "Let it load on demand instead of choosing it for every task.");
                    break;
                case SkillStanding.NeverLoaded:
                    Add(ImprovementSeverity.Improve, s.RunsOffered, $"Offered to {s.RunsOffered} runs, and never loaded.",
                        "Rewrite its description so the agent knows when to load it, or remove it.");
                    break;
            }

            if (s.ChangedSinceApproval)
                Add(ImprovementSeverity.Improve, 1, "Changed in the repository since its approval, so runs no longer get it.", "Review and approve the new version.");
        }

        return [.. items.OrderBy(i => i.Item.Severity).ThenByDescending(i => i.Weight).ThenBy(i => i.Item.Subject, StringComparer.Ordinal).Select(i => i.Item)];

        static string Points(Comparison c) => $"{Math.Round(Math.Abs(c.With - c.Without) * 100):0} percentage points";
        static string Percent(double share) => $"{Math.Round(share * 100):0}%";
    }
}
