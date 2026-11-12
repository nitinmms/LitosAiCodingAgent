namespace Litos.SoftwareFactory.Core.Metrics;

// ---- What the chat report is computed from: rows the store already keeps ----

/// <summary>One chat answer given in the period: what it cost, and whether it published an artifact.</summary>
/// <param name="At">When it was asked (its run's creation).</param>
/// <param name="Tokens">Everything its model calls were charged, against its own allowance.</param>
/// <param name="Artifact">It published or updated at least one artifact (a page) on its thread.</param>
public sealed record ChatAnswer(Guid RunId, Guid ThreadId, Guid ProjectId, string TaskTitle, DateTimeOffset At, long Tokens, bool Artifact);

/// <param name="TaskTokens">What task work (every run that is not a chat) was charged in the same period, on the same projects.</param>
public sealed record ChatData(IReadOnlyList<ChatAnswer> Answers, long TaskTokens);

// ---- What it comes to ----

/// <param name="Median">The median tokens of its answers.</param>
public sealed record ChatProject(Guid ProjectId, long Tokens, long ArtifactTokens, int Answers, double Median, double Share);

/// <summary>One stretch of the period: the chat tokens spent in it, split by project and by kind.</summary>
public sealed record ChatBucket(DateTimeOffset Start, int Days, long Tokens, long ArtifactTokens, IReadOnlyList<ChatProjectTokens> ByProject);

public sealed record ChatProjectTokens(Guid ProjectId, long Tokens);

/// <summary>A task's chat in the period, for the table of tasks with the most.</summary>
public sealed record ChatTask(Guid ThreadId, Guid ProjectId, string Title, int Answers, int ArtifactAnswers, long Tokens);

/// <param name="Share">Chat over all the tokens spent in the period (chat and task work); null when nothing was spent.</param>
/// <param name="PerAnswer">Tokens per answer, over the answers that spent any.</param>
/// <param name="PlainMedian">The median of the plain answers; 0 with none.</param>
/// <param name="ArtifactMedian">The median of the answers that published an artifact; 0 with none.</param>
/// <param name="TopTasks">The tasks with the most chat tokens, most first, at most <see cref="ChatMetrics.TopTaskCount"/>.</param>
/// <param name="Series">The period in equal stretches (FlowMetrics.Stretches); empty when no period was given.</param>
public sealed record ChatReport(
    long Tokens, long ArtifactTokens, long PlainTokens, long TaskTokens, double? Share,
    int Answers, int ArtifactAnswers,
    Spread PerAnswer, double PlainMedian, double ArtifactMedian,
    IReadOnlyList<ChatProject> ByProject,
    IReadOnlyList<ChatTask> TopTasks,
    IReadOnlyList<ChatBucket> Series);

/// <summary>
/// How many tokens chat spends (the Metrics tab's Chat view): every chat answer in the period, plain
/// or with an artifact, on any task. Chat is charged to each answer's own allowance, never to a task's
/// budget, so it is reported here and left out of the flow metrics' task tokens. Computed on read.
/// </summary>
public static class ChatMetrics
{
    public const int TopTaskCount = 10;

    /// <param name="days">The period's length, for <see cref="ChatReport.Series"/>; 0 for none.</param>
    public static ChatReport Compute(ChatData data, DateTimeOffset now, int days = 0)
    {
        var answers = data.Answers;
        var tokens = answers.Sum(a => a.Tokens);
        var artifactTokens = answers.Where(a => a.Artifact).Sum(a => a.Tokens);
        var all = tokens + data.TaskTokens;

        return new ChatReport(
            tokens,
            artifactTokens,
            tokens - artifactTokens,
            data.TaskTokens,
            all == 0 ? null : Math.Round((double)tokens / all, 4),
            answers.Count,
            answers.Count(a => a.Artifact),
            SpreadOf(answers),
            SpreadOf(answers.Where(a => !a.Artifact)).Median,
            SpreadOf(answers.Where(a => a.Artifact)).Median,
            [.. answers.GroupBy(a => a.ProjectId)
                .Select(g => new ChatProject(
                    g.Key, g.Sum(a => a.Tokens), g.Where(a => a.Artifact).Sum(a => a.Tokens), g.Count(), SpreadOf(g).Median,
                    tokens == 0 ? 0 : Math.Round((double)g.Sum(a => a.Tokens) / tokens, 4)))
                .OrderByDescending(p => p.Tokens).ThenBy(p => p.ProjectId)],
            [.. answers.GroupBy(a => a.ThreadId)
                .Select(g => new ChatTask(g.Key, g.First().ProjectId, g.First().TaskTitle, g.Count(), g.Count(a => a.Artifact), g.Sum(a => a.Tokens)))
                .Where(t => t.Tokens > 0)
                .OrderByDescending(t => t.Tokens).ThenBy(t => t.Title, StringComparer.Ordinal)
                .Take(TopTaskCount)],
            Buckets(answers, now, days));
    }

    /// <summary>An answer counts in the stretch it was asked in; one outside the period falls in none.</summary>
    private static List<ChatBucket> Buckets(IReadOnlyList<ChatAnswer> answers, DateTimeOffset now, int days)
    {
        if (days <= 0)
            return [];
        var (start, size, count) = FlowMetrics.Stretches(now, days);
        int? Index(DateTimeOffset at) => Math.Floor((at - start).TotalDays / size) is var i && i >= 0 && i < count ? (int)i : null;
        var placed = answers.Select(a => (Answer: a, Index: Index(a.At))).Where(p => p.Index is not null).ToList();

        return [.. Enumerable.Range(0, count).Select(i =>
        {
            var inIt = placed.Where(p => p.Index == i).Select(p => p.Answer).ToList();
            return new ChatBucket(
                start.AddDays(i * size), size, inIt.Sum(a => a.Tokens), inIt.Where(a => a.Artifact).Sum(a => a.Tokens),
                [.. inIt.GroupBy(a => a.ProjectId).Select(g => new ChatProjectTokens(g.Key, g.Sum(a => a.Tokens))).OrderByDescending(p => p.Tokens).ThenBy(p => p.ProjectId)]);
        })];
    }

    /// <summary>The spread of the answers that spent anything: one refused before its first call says nothing about cost.</summary>
    private static Spread SpreadOf(IEnumerable<ChatAnswer> answers) =>
        FlowMetrics.SpreadOf([.. answers.Where(a => a.Tokens > 0).Select(a => (double)a.Tokens)]);
}
