using Litos.SoftwareFactory.Core.Metrics;

namespace Litos.SoftwareFactory.Core.Tests.Metrics;

/// <summary>What chat spends: every answer in the period, plain or with an artifact, apart from task work.</summary>
public class ChatMetricsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Garmin = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid Insta = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid Load = Guid.NewGuid();
    private static readonly Guid Sleep = Guid.NewGuid();
    private static readonly Guid Stories = Guid.NewGuid();

    private static ChatAnswer Answer(Guid project, Guid thread, string title, long tokens, bool artifact = false, double daysAgo = 1) =>
        new(Guid.NewGuid(), thread, project, title, Now.AddDays(-daysAgo), tokens, artifact);

    private static readonly ChatAnswer[] Sample =
    [
        Answer(Garmin, Load, "Training load", 60_000, artifact: true),
        Answer(Garmin, Load, "Training load", 4_000),
        Answer(Garmin, Sleep, "Sleep import", 2_000),
        Answer(Insta, Stories, "Story picker", 30_000, artifact: true),
        Answer(Insta, Stories, "Story picker", 4_000),
    ];

    [Fact]
    public void Totals_SplitPlainFromArtifact_AndSetChatBesideTaskWork()
    {
        var report = ChatMetrics.Compute(new ChatData(Sample, TaskTokens: 300_000), Now);

        Assert.Equal((100_000L, 90_000L, 10_000L, 300_000L), (report.Tokens, report.ArtifactTokens, report.PlainTokens, report.TaskTokens));
        Assert.Equal(0.25, report.Share);
        Assert.Equal((5, 2), (report.Answers, report.ArtifactAnswers));
    }

    [Fact]
    public void PerAnswer_IsTheSpreadOfAnswersThatSpentAnything_WithPlainAndArtifactMediansApart()
    {
        ChatAnswer[] answers = [.. Sample, Answer(Garmin, Sleep, "Sleep import", 0)]; // refused before its first call

        var report = ChatMetrics.Compute(new ChatData(answers, 0), Now);

        Assert.Equal(6, report.Answers);
        Assert.Equal(new Spread(5, 4_000, 60_000), report.PerAnswer);
        Assert.Equal(4_000, report.PlainMedian);
        Assert.Equal(30_000, report.ArtifactMedian);
    }

    [Fact]
    public void ByProject_IsMostFirst_WithItsShareOfChat()
    {
        var report = ChatMetrics.Compute(new ChatData(Sample, 0), Now);

        Assert.Equal(
            [(Garmin, 66_000L, 60_000L, 3, 4_000.0, 0.66), (Insta, 34_000L, 30_000L, 2, 4_000.0, 0.34)],
            report.ByProject.Select(p => (p.ProjectId, p.Tokens, p.ArtifactTokens, p.Answers, p.Median, p.Share)));
    }

    [Fact]
    public void TopTasks_AreTheTasksWithTheMostChat_AtMostTen()
    {
        var many = Enumerable.Range(1, 12).Select(i => Answer(Garmin, Guid.NewGuid(), $"Task {i:00}", i * 1_000)).ToList();
        many.Add(Answer(Garmin, Guid.NewGuid(), "Nothing spent", 0));

        var report = ChatMetrics.Compute(new ChatData(Sample, 0), Now);
        var top = ChatMetrics.Compute(new ChatData(many, 0), Now).TopTasks;

        Assert.Equal(
            [("Training load", 2, 1, 64_000L), ("Story picker", 2, 1, 34_000L), ("Sleep import", 1, 0, 2_000L)],
            report.TopTasks.Select(t => (t.Title, t.Answers, t.ArtifactAnswers, t.Tokens)));
        Assert.Equal(ChatMetrics.TopTaskCount, top.Count);
        Assert.Equal("Task 12", top[0].Title);
        Assert.DoesNotContain(top, t => t.Title == "Nothing spent");
    }

    /// <summary>The same stretches as the flow trend; an answer counts where it was asked.</summary>
    [Fact]
    public void Series_SplitsThePeriodIntoStretches_ByProjectAndKind()
    {
        ChatAnswer[] answers =
        [
            Answer(Garmin, Load, "Training load", 60_000, artifact: true, daysAgo: 1),
            Answer(Insta, Stories, "Story picker", 4_000, daysAgo: 1.5),
            Answer(Garmin, Load, "Training load", 2_000, daysAgo: 29),
            Answer(Garmin, Load, "Training load", 9_999, daysAgo: 45), // before the period
        ];

        var series = ChatMetrics.Compute(new ChatData(answers, 0), Now, days: 30).Series;

        Assert.Equal(10, series.Count);
        Assert.All(series, b => Assert.Equal(3, b.Days));
        var last = series[^1];
        Assert.Equal((64_000L, 60_000L), (last.Tokens, last.ArtifactTokens));
        Assert.Equal([(Garmin, 60_000L), (Insta, 4_000L)], last.ByProject.Select(p => (p.ProjectId, p.Tokens)));
        Assert.Equal(2_000L, series[0].Tokens);
        Assert.Equal(66_000L, series.Sum(b => b.Tokens));
        Assert.Empty(ChatMetrics.Compute(new ChatData(answers, 0), Now).Series); // no period, no trend
    }

    [Fact]
    public void NoChat_IsZeros_AndNoShareWhenNothingWasSpent()
    {
        var empty = ChatMetrics.Compute(new ChatData([], 0), Now);
        var onlyTasks = ChatMetrics.Compute(new ChatData([], 5_000), Now);

        Assert.Equal((0L, 0, (double?)null), (empty.Tokens, empty.Answers, empty.Share));
        Assert.Equal(new Spread(0, 0, 0), empty.PerAnswer);
        Assert.Empty(empty.ByProject);
        Assert.Equal(0.0, onlyTasks.Share);
    }
}
