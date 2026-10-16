using Litos.SoftwareFactory.Core.Lessons;

namespace Litos.SoftwareFactory.Core.Tests.Lessons;

/// <summary>Which approved lessons a run is given (m4-architecture.md §4.1).</summary>
public class LessonRetrievalTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private static LessonCandidate Lesson(long number, string[] topics, string[]? paths = null, string rule = "Do it this way.", int daysAgo = 0) =>
        new(Guid.NewGuid(), number, 1, rule, topics, paths ?? [], T0.AddDays(-daysAgo));

    private static IReadOnlyList<LessonPick> Select(IEnumerable<LessonCandidate> lessons, string task, IReadOnlyCollection<Guid>? manual = null, long? remaining = null) =>
        LessonRetrieval.Select(lessons, task, manual ?? [], remaining);

    [Fact]
    public void ALessonWhoseTopicsTheTaskMentions_IsRetrieved_AndOneWithNoMatch_IsNot()
    {
        var money = Lesson(1, ["money"]);
        var dates = Lesson(2, ["dates"]);

        var picks = Select([money, dates], "Add CSV export of invoices with their MONEY totals.");

        var pick = Assert.Single(picks);
        Assert.Equal((money, 2, LessonInclusion.Retrieved), (pick.Lesson, pick.Score, pick.Inclusion));
    }

    [Fact]
    public void ATopicMatchesWholeWords_AndAPhraseMatchesInOrder()
    {
        Assert.Empty(Select([Lesson(1, ["date"])], "Update the dates on invoices."));
        Assert.Single(Select([Lesson(1, ["line items"])], "Sum the line-items of an order."));
        Assert.Empty(Select([Lesson(1, ["line items"])], "Items per line."));
    }

    [Fact]
    public void MoreMatchingTopics_RankHigher_ThenTheMostRecentlyApproved_ThenTheNumber()
    {
        var one = Lesson(1, ["money"], daysAgo: 5);
        var two = Lesson(2, ["money", "csv"], daysAgo: 9);
        var three = Lesson(3, ["money"], daysAgo: 1);
        var four = Lesson(4, ["money"], daysAgo: 1);

        var picks = Select([one, two, three, four], "CSV export of money.");

        Assert.Equal([2L, 3L, 4L, 1L], picks.Select(p => p.Lesson.Number));
        Assert.Equal([4, 2, 2, 2], picks.Select(p => p.Score));
    }

    [Fact]
    public void ALessonPathMatchingAPathTheTaskNames_AddsToItsScore_AndCanRetrieveItAlone()
    {
        var byPath = Lesson(1, ["unrelated"], paths: ["src/Billing/**"]);
        var both = Lesson(2, ["export"], paths: ["src/Billing/*.cs"]);

        var picks = Select([byPath, both], "Fix the export in src/Billing/Invoices.cs please.");

        Assert.Equal([(2L, 3), (1L, 1)], picks.Select(p => (p.Lesson.Number, p.Score)));
    }

    [Fact]
    public void AtMostFiveLessons_AreGiven_AndTheRestAreRecordedAsLeftOut()
    {
        var lessons = Enumerable.Range(1, 7).Select(i => Lesson(i, ["money"])).ToList();

        var picks = Select(lessons, "money");

        Assert.Equal(5, picks.Count(p => p.Inclusion == LessonInclusion.Retrieved));
        Assert.Equal(2, picks.Count(p => p.Inclusion == LessonInclusion.NotIncluded));
        Assert.Equal(5, LessonRetrieval.Included(picks).Count);
    }

    [Fact]
    public void TheTokenLimit_AndTwoPercentOfTheRemainingBudget_BoundWhatIsGiven()
    {
        var long1 = Lesson(1, ["money"], rule: new string('a', 4_000)); // about 1,000 tokens
        var long2 = Lesson(2, ["money"], rule: new string('b', 4_000));

        Assert.Equal([LessonInclusion.Retrieved, LessonInclusion.NotIncluded], Select([long1, long2], "money").Select(p => p.Inclusion));
        // 2% of 30,000 is 600 tokens: neither fits.
        Assert.All(Select([long1, long2], "money", remaining: 30_000), p => Assert.Equal(LessonInclusion.NotIncluded, p.Inclusion));
        Assert.All(Select([Lesson(3, ["money"])], "money", remaining: 0), p => Assert.Equal(LessonInclusion.NotIncluded, p.Inclusion));
    }

    [Fact]
    public void AManualPick_ComesFirst_EvenWithoutAMatch_AndIsReportedWhenItCannotFit()
    {
        var matching = Lesson(1, ["money"]);
        var picked = Lesson(2, ["unrelated"]);
        var huge = Lesson(3, ["unrelated"], rule: new string('x', 8_000));

        var picks = Select([matching, picked, huge], "money", manual: [picked.LessonId, huge.LessonId]);

        Assert.Equal([(2L, LessonInclusion.Manual), (3L, LessonInclusion.NotIncluded), (1L, LessonInclusion.Retrieved)], picks.Select(p => (p.Lesson.Number, p.Inclusion)));
        Assert.True(picks[1].Manual);
    }

    [Fact]
    public void Describe_NamesWhatWasGivenAndWhatWasLeftOut()
    {
        var lessons = Enumerable.Range(1, 6).Select(i => Lesson(i, ["money"])).ToList();

        var note = LessonRetrieval.Describe(Select(lessons, "money"));

        Assert.Equal("Project lessons given to this run: L-1, L-2, L-3, L-4, L-5.\nLeft out, past the limit of 5 lessons and 1,500 tokens (or 2% of the remaining budget): L-6.", note);
        Assert.Null(LessonRetrieval.Describe([]));
    }

    [Fact]
    public void ALessonReads_WithItsNumberAndRevision()
    {
        Assert.Equal("[L-12 r3] Use cents.", new LessonCandidate(Guid.NewGuid(), 12, 3, "Use cents.", ["money"], [], T0).Text);
    }

    [Theory]
    [InlineData("src/**", "src/a/b/c.cs", true)]
    [InlineData("src/*.cs", "src/a.cs", true)]
    [InlineData("src/*.cs", "src/a/b.cs", false)]
    [InlineData("**/Invoices.cs", "src/Billing/Invoices.cs", true)]
    [InlineData("web/src/??.ts", "web/src/ab.ts", true)]
    public void AGlob_MatchesAsUsual(string glob, string path, bool matches) => Assert.Equal(matches, LessonRetrieval.Glob(glob).IsMatch(path));

    [Fact]
    public void NamedPaths_FindsPathsAndFileNamesInTheTaskText()
    {
        Assert.Equal(["src/Billing/Invoices.cs", "README.md"], LessonRetrieval.NamedPaths("Change src/Billing/Invoices.cs, and README.md."));
    }
}
