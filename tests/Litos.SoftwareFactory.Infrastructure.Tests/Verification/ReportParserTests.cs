using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Verification;
using Litos.SoftwareFactory.Infrastructure.Verification;

namespace Litos.SoftwareFactory.Infrastructure.Tests.Verification;

public class TrxReportParserTests
{
    /// <summary>The report `dotnet test --logger trx` wrote for filedb-sharp at its M1 baseline.</summary>
    [Fact]
    public void Parse_RealFileDbSharpReport_ReadsEveryTestAsPassed()
    {
        var tests = TrxReportParser.Parse(Fixtures.Read("filedb-sharp.trx"));

        Assert.Equal(40, tests.Count);
        Assert.All(tests, t => Assert.Equal(TestOutcome.Passed, t.Outcome));
        Assert.Contains(tests, t => t.Name == "FileDbSharp.Tests.TransactionTests.Completed_transaction_cannot_be_reused");
        // Theory rows keep their arguments, so each row is its own test.
        Assert.Contains(tests, t => t.Name == "FileDbSharp.Tests.DurabilityTests.Data_survives_reopen(durability: FlushToOperatingSystem)");
        Assert.All(tests, t => Assert.True(t.DurationSeconds >= 0));
    }

    private const string FailingTrx = """
        <?xml version="1.0" encoding="utf-8"?>
        <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <Results>
            <UnitTestResult testName="Orders.Tests.Export_Admin_Succeeds" duration="00:00:00.0120000" outcome="Passed" />
            <UnitTestResult testName="Orders.Tests.Export_Commas_AreQuoted" duration="00:00:01.5000000" outcome="Failed">
              <Output>
                <ErrorInfo>
                  <Message>Assert.Equal() Failure: Values differ
        Expected: "a,b"
        Actual:   a,b</Message>
                  <StackTrace>   at Orders.Tests.Export_Commas_AreQuoted() in C:\src\OrdersTests.cs:line 42</StackTrace>
                </ErrorInfo>
              </Output>
            </UnitTestResult>
            <UnitTestResult testName="Orders.Tests.Export_Skipped" outcome="NotExecuted" />
            <UnitTestResult testName="Orders.Tests.Export_Hangs" outcome="Timeout" />
            <UnitTestResult testName="Orders.Tests.Export_Mystery" outcome="SomethingNew" />
          </Results>
        </TestRun>
        """;

    [Fact]
    public void Parse_FailedTest_CarriesItsMessageAndStackTrace()
    {
        var failed = TrxReportParser.Parse(FailingTrx).Single(t => t.Name == "Orders.Tests.Export_Commas_AreQuoted");

        Assert.Equal(TestOutcome.Failed, failed.Outcome);
        Assert.Contains("Expected: \"a,b\"", failed.Message);
        Assert.Contains("OrdersTests.cs:line 42", failed.Message);
        Assert.Equal(1.5, failed.DurationSeconds);
    }

    [Fact]
    public void Parse_Outcomes_MapToPassedFailedSkipped()
    {
        var byName = TrxReportParser.Parse(FailingTrx).ToDictionary(t => t.Name.Split('.')[^1], t => t.Outcome);

        Assert.Equal(TestOutcome.Passed, byName["Export_Admin_Succeeds"]);
        Assert.Equal(TestOutcome.Failed, byName["Export_Commas_AreQuoted"]);
        Assert.Equal(TestOutcome.Skipped, byName["Export_Skipped"]);
        Assert.Equal(TestOutcome.Failed, byName["Export_Hangs"]);
    }

    /// <summary>A result that is not known to be a pass is never counted as one.</summary>
    [Fact]
    public void Parse_UnrecognisedOutcome_IsAFailure_AndSaysWhatItWas()
    {
        var mystery = TrxReportParser.Parse(FailingTrx).Single(t => t.Name.EndsWith("Export_Mystery"));

        Assert.Equal(TestOutcome.Failed, mystery.Outcome);
        Assert.Equal("Outcome: SomethingNew", mystery.Message);
    }

    [Fact]
    public void Parse_NoResults_IsEmpty()
    {
        Assert.Empty(TrxReportParser.Parse("""<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results /></TestRun>"""));
    }

    [Theory]
    [InlineData("<testsuites />")]
    [InlineData("not xml at all")]
    [InlineData("<TestRun><Results>")]
    public void Parse_NotATrxReport_Throws(string content)
    {
        Assert.Throws<ReportFormatException>(() => TrxReportParser.Parse(content));
    }
}

public class JUnitReportParserTests
{
    /// <summary>The report Vitest wrote for insta-story-generator (`npm run test:ci`).</summary>
    [Fact]
    public void Parse_RealVitestReport_ReadsAll123TestsAsPassed()
    {
        var tests = JUnitReportParser.Parse(Fixtures.Read("insta-story.junit.xml"));

        Assert.Equal(123, tests.Count); // the report's own tests="123"
        Assert.All(tests, t => Assert.Equal(TestOutcome.Passed, t.Outcome));
        Assert.Contains(tests, t => t.Name == "server/app.test.ts :: GET /api/config > lists configured providers without keys");
        Assert.Equal(tests.Count, tests.Select(t => t.Name).Distinct().Count());
    }

    private const string MixedJUnit = """
        <?xml version="1.0" encoding="UTF-8"?>
        <testsuites name="suite" tests="5" failures="1" errors="1">
          <testsuite name="outer">
            <testsuite name="nested">
              <testcase classname="src/export.test.ts" name="quotes commas" time="0.25">
                <failure message="expected 'a,b' to equal '&quot;a,b&quot;'" type="AssertionError">AssertionError: expected 'a,b' to equal '"a,b"'
            at src/export.test.ts:12:5</failure>
              </testcase>
            </testsuite>
            <testcase classname="src/export.test.ts" name="throws" time="0.01">
              <error message="boom">Error: boom</error>
            </testcase>
            <testcase classname="src/export.test.ts" name="is skipped"><skipped /></testcase>
            <testcase classname="src/export.test.ts" name="passes" time="0.002" />
            <testcase name="no classname" />
          </testsuite>
        </testsuites>
        """;

    [Fact]
    public void Parse_FailureAndError_AreBothFailedTests_WithTheirText()
    {
        var tests = JUnitReportParser.Parse(MixedJUnit);

        var failure = tests.Single(t => t.Name == "src/export.test.ts :: quotes commas");
        Assert.Equal(TestOutcome.Failed, failure.Outcome);
        Assert.Contains("AssertionError: expected 'a,b'", failure.Message);
        Assert.Contains("src/export.test.ts:12:5", failure.Message);
        Assert.Equal(0.25, failure.DurationSeconds);

        var error = tests.Single(t => t.Name == "src/export.test.ts :: throws");
        Assert.Equal(TestOutcome.Failed, error.Outcome);
        Assert.Contains("boom", error.Message);
    }

    [Fact]
    public void Parse_FindsTestCasesInNestedSuites_AndCountsEachOnce()
    {
        var tests = JUnitReportParser.Parse(MixedJUnit);

        Assert.Equal(5, tests.Count);
        Assert.Equal(2, tests.Count(t => t.Outcome == TestOutcome.Failed));
        Assert.Equal(1, tests.Count(t => t.Outcome == TestOutcome.Skipped));
        Assert.Equal(2, tests.Count(t => t.Outcome == TestOutcome.Passed));
    }

    [Fact]
    public void Parse_NoClassname_UsesTheBareName_AndAMissingTimeIsNull()
    {
        var test = JUnitReportParser.Parse(MixedJUnit).Single(t => t.Name == "no classname");

        Assert.Null(test.DurationSeconds);
    }

    [Fact]
    public void Parse_SingleTestsuiteRoot_IsAccepted()
    {
        var tests = JUnitReportParser.Parse("""<testsuite name="s"><testcase classname="c" name="n" /></testsuite>""");

        Assert.Equal("c :: n", Assert.Single(tests).Name);
    }

    [Fact]
    public void Parse_FailureWithOnlyAMessageAttribute_UsesIt()
    {
        var tests = JUnitReportParser.Parse("""<testsuite><testcase name="n"><failure message="only attribute" /></testcase></testsuite>""");

        Assert.Equal("only attribute", Assert.Single(tests).Message);
    }

    [Theory]
    [InlineData("<coverage />")]
    [InlineData("<testsuites><testsuite>")]
    [InlineData("")]
    public void Parse_NotAJUnitReport_Throws(string content)
    {
        Assert.Throws<ReportFormatException>(() => JUnitReportParser.Parse(content));
    }
}

public class CoberturaParserTests
{
    /// <summary>coverlet's report for filedb-sharp; its own header says 632 of 675 lines covered.</summary>
    [Fact]
    public void Parse_RealCoverletReport_MatchesTheReportsOwnTotals()
    {
        var report = CoberturaParser.Parse(Fixtures.Read("filedb-sharp.cobertura.xml"));

        Assert.Equal([@"C:\GenAI\filedb-sharp\src\FileDbSharp\"], report.SourceRoots);
        Assert.Equal(675, report.Files.Sum(f => f.LineHits.Count));
        Assert.Equal(632, report.Files.Sum(f => f.LineHits.Count(l => l.Value > 0)));
        Assert.Contains(report.Files, f => f.Path == "Collection.cs");
    }

    /// <summary>Vitest's (Istanbul) report for insta-story-generator, which has a DOCTYPE and
    /// Windows path separators; its header says 548 of 639.</summary>
    [Fact]
    public void Parse_RealVitestReport_MatchesTheReportsOwnTotals()
    {
        var report = CoberturaParser.Parse(Fixtures.Read("insta-story.cobertura.xml"));

        Assert.Equal(["C:/GenAI/insta-story-generator"], report.SourceRoots);
        Assert.Equal(639, report.Files.Sum(f => f.LineHits.Count));
        Assert.Equal(548, report.Files.Sum(f => f.LineHits.Count(l => l.Value > 0)));
        Assert.Contains(report.Files, f => f.Path == @"server\app.ts");
    }

    [Fact]
    public void Parse_SeveralClassesInOneFile_AreMergedPerLine_TakingTheHigherHitCount()
    {
        const string xml = """
            <coverage>
              <packages><package><classes>
                <class name="Outer" filename="src/Outer.cs">
                  <methods><method name="M"><lines><line number="10" hits="3" /></lines></method></methods>
                  <lines><line number="10" hits="3" /><line number="11" hits="0" /></lines>
                </class>
                <class name="Outer/Nested" filename="src/Outer.cs">
                  <lines><line number="11" hits="2" /><line number="20" hits="0" /></lines>
                </class>
              </classes></package></packages>
            </coverage>
            """;

        var file = Assert.Single(CoberturaParser.Parse(xml).Files);

        Assert.Equal("src/Outer.cs", file.Path);
        Assert.Equal(new Dictionary<int, int> { [10] = 3, [11] = 2, [20] = 0 }, file.LineHits);
    }

    /// <summary>Istanbul lists each function's declaration line under its method but not in the
    /// class's line list, and does not count it in its totals; neither does the factory.</summary>
    [Fact]
    public void Parse_MethodOnlyLines_AreNotCounted_WhenTheClassHasItsOwnLineList()
    {
        const string xml = """
            <coverage><packages><package><classes>
              <class name="app.ts" filename="server/app.ts">
                <methods><method name="createApp" hits="12"><lines><line number="22" hits="12" /></lines></method></methods>
                <lines><line number="23" hits="12" /><line number="24" hits="0" /></lines>
              </class>
            </classes></package></packages></coverage>
            """;

        var file = Assert.Single(CoberturaParser.Parse(xml).Files);

        Assert.Equal(new Dictionary<int, int> { [23] = 12, [24] = 0 }, file.LineHits);
    }

    [Fact]
    public void Parse_ClassWithOnlyMethodLines_FallsBackToThem()
    {
        const string xml = """
            <coverage><packages><package><classes>
              <class name="A" filename="a.cs">
                <methods><method name="M"><lines><line number="5" hits="1" /><line number="6" hits="0" /></lines></method></methods>
              </class>
            </classes></package></packages></coverage>
            """;

        var file = Assert.Single(CoberturaParser.Parse(xml).Files);

        Assert.Equal(new Dictionary<int, int> { [5] = 1, [6] = 0 }, file.LineHits);
    }

    [Fact]
    public void Parse_HitCountBeyondInt_IsClamped_NotDropped()
    {
        const string xml = """<coverage><packages><package><classes><class filename="a.cs"><lines><line number="1" hits="9999999999" /></lines></class></classes></package></packages></coverage>""";

        Assert.Equal(int.MaxValue, CoberturaParser.Parse(xml).Files[0].LineHits[1]);
    }

    [Fact]
    public void Parse_NoSources_IsFine()
    {
        Assert.Empty(CoberturaParser.Parse("<coverage><packages /></coverage>").SourceRoots);
    }

    /// <summary>The DOCTYPE names a DTD on the web. It must be ignored, not fetched and not refused.</summary>
    [Fact]
    public void Parse_DoctypeIsIgnored_AndExternalEntitiesAreNotResolved()
    {
        const string xml = """
            <?xml version="1.0" ?>
            <!DOCTYPE coverage SYSTEM "http://cobertura.sourceforge.net/xml/coverage-04.dtd">
            <coverage><packages><package><classes><class filename="a.cs"><lines><line number="1" hits="1" /></lines></class></classes></package></packages></coverage>
            """;

        Assert.Single(CoberturaParser.Parse(xml).Files);
    }

    [Theory]
    [InlineData("<testsuites />")]
    [InlineData("TN:\nSF:a.ts\n")]
    public void Parse_NotACoberturaReport_Throws(string content)
    {
        Assert.Throws<ReportFormatException>(() => CoberturaParser.Parse(content));
    }
}

public class LcovParserTests
{
    /// <summary>Vitest's lcov.info for insta-story-generator: the same run as its Cobertura
    /// report, so the two must agree.</summary>
    [Fact]
    public void Parse_RealVitestReport_AgreesWithTheCoberturaReportOfTheSameRun()
    {
        var lcov = LcovParser.Parse(Fixtures.Read("insta-story.lcov.info"));
        var cobertura = CoberturaParser.Parse(Fixtures.Read("insta-story.cobertura.xml"));

        Assert.Equal(27, lcov.Files.Count);
        Assert.Equal(639, lcov.Files.Sum(f => f.LineHits.Count));
        Assert.Equal(548, lcov.Files.Sum(f => f.LineHits.Count(l => l.Value > 0)));

        var app = lcov.Files.Single(f => f.Path == @"server\app.ts");
        var appFromCobertura = cobertura.Files.Single(f => f.Path == @"server\app.ts");
        Assert.Equal(appFromCobertura.LineHits.OrderBy(l => l.Key), app.LineHits.OrderBy(l => l.Key));
    }

    [Fact]
    public void Parse_ReadsLineHitsPerFile_AndIgnoresFunctionAndBranchRecords()
    {
        const string lcov = "TN:\nSF:src/a.ts\nFN:1,f\nFNDA:3,f\nDA:1,3\nDA:2,0\nBRDA:2,0,0,1\nLF:2\nLH:1\nend_of_record\nSF:src/b.ts\nDA:7,1,abc123\nend_of_record\n";

        var report = LcovParser.Parse(lcov);

        Assert.Equal(["src/a.ts", "src/b.ts"], report.Files.Select(f => f.Path));
        Assert.Equal(new Dictionary<int, int> { [1] = 3, [2] = 0 }, report.Files[0].LineHits);
        Assert.Equal(new Dictionary<int, int> { [7] = 1 }, report.Files[1].LineHits); // the checksum is ignored
    }

    [Fact]
    public void Parse_SameFileTwice_IsMerged()
    {
        const string lcov = "SF:a.ts\nDA:1,0\nend_of_record\nSF:a.ts\nDA:1,4\nDA:2,0\nend_of_record\n";

        var file = Assert.Single(LcovParser.Parse(lcov).Files);

        Assert.Equal(new Dictionary<int, int> { [1] = 4, [2] = 0 }, file.LineHits);
    }

    [Fact]
    public void Parse_WindowsLineEndings_AreHandled()
    {
        var file = Assert.Single(LcovParser.Parse("SF:a.ts\r\nDA:1,1\r\nend_of_record\r\n").Files);

        Assert.Equal("a.ts", file.Path);
        Assert.Equal(1, file.LineHits[1]);
    }

    [Fact]
    public void Parse_Empty_IsAnEmptyReport()
    {
        Assert.Empty(LcovParser.Parse("").Files);
    }

    [Fact]
    public void Parse_NotAnLcovReport_Throws()
    {
        Assert.Throws<ReportFormatException>(() => LcovParser.Parse("<coverage />"));
    }
}

public class ChangedLineCoverageCalculatorTests : IDisposable
{
    private readonly TempDirectory _repo = new();

    public void Dispose() => _repo.Dispose();

    private static CoverageFile Coverage(string path, params (int Line, int Hits)[] lines) =>
        new(path, lines.ToDictionary(l => l.Line, l => l.Hits));

    private static FileChange Change(string path, params (int Start, int End)[] ranges) =>
        new(path, [.. ranges.Select(r => new LineRange(r.Start, r.End))]);

    [Fact]
    public void Calculate_CountsOnlyChangedLinesTheReportMeasures()
    {
        // Lines 10-14 changed. The report measures 10, 11, 13 and 14 (12 is a brace); 13 never ran.
        var coverage = Coverage("src/Orders.cs", (5, 9), (10, 2), (11, 2), (13, 0), (14, 1), (30, 0));

        var result = ChangedLineCoverageCalculator.Calculate([Change("src/Orders.cs", (10, 14))], [coverage]);

        Assert.Equal(3, result.Covered);
        Assert.Equal(4, result.Measurable);
        Assert.Equal(75, result.Percent);
        var uncovered = Assert.Single(result.Uncovered);
        Assert.Equal("src/Orders.cs", uncovered.File);
        Assert.Equal([13], uncovered.Lines);
    }

    [Fact]
    public void Calculate_UnchangedUncoveredLines_DoNotCount()
    {
        var coverage = Coverage("src/Orders.cs", (1, 0), (2, 0), (3, 0), (50, 1));

        var result = ChangedLineCoverageCalculator.Calculate([Change("src/Orders.cs", (50, 50))], [coverage]);

        Assert.Equal(100, result.Percent);
        Assert.Empty(result.Uncovered);
    }

    [Fact]
    public void Calculate_FileTheReportDoesNotMention_IsNotMeasurable()
    {
        // A changed test file, README or config: no executable lines as far as coverage knows.
        var coverage = Coverage("src/Orders.cs", (1, 1));

        var result = ChangedLineCoverageCalculator.Calculate(
            [Change("README.md", (1, 40)), Change("tests/OrdersTests.cs", (1, 90))], [coverage]);

        Assert.Equal(0, result.Measurable);
        Assert.Equal(100, result.Percent);
    }

    [Fact]
    public void Calculate_SeveralFilesAndRanges_AreSummed()
    {
        var result = ChangedLineCoverageCalculator.Calculate(
            [Change("a.cs", (1, 2), (10, 10)), Change("b.cs", (5, 6))],
            [Coverage("a.cs", (1, 1), (2, 0), (10, 1)), Coverage("b.cs", (5, 0), (6, 0))]);

        Assert.Equal(2, result.Covered);
        Assert.Equal(5, result.Measurable);
        Assert.Equal(40, result.Percent);
        Assert.Equal(["a.cs", "b.cs"], result.Uncovered.Select(u => u.File));
        Assert.Equal([5, 6], result.Uncovered[1].Lines);
    }

    [Fact]
    public void Calculate_TwoReportsForOneFile_AreMerged_SoALineCoveredByEitherCounts()
    {
        var result = ChangedLineCoverageCalculator.Calculate(
            [Change("src/Shared.cs", (1, 2))],
            [Coverage("src/Shared.cs", (1, 0), (2, 3)), Coverage("src/Shared.cs", (1, 5), (2, 0))]);

        Assert.Equal(2, result.Covered);
    }

    [Fact]
    public void Calculate_MatchesAcrossPathSeparatorsAndCase()
    {
        var result = ChangedLineCoverageCalculator.Calculate([Change("server/app.ts", (1, 1))], [Coverage(@"Server\App.ts", (1, 1))]);

        Assert.Equal(1, result.Measurable);
    }

    [Fact]
    public void Calculate_ReportPathIsASuffixOfTheChangedPath_Matches()
    {
        // The report is relative to the project directory; the diff is relative to the repository.
        var result = ChangedLineCoverageCalculator.Calculate(
            [Change("src/FileDbSharp/Collection.cs", (33, 34))], [Coverage("Collection.cs", (33, 4), (34, 0))]);

        Assert.Equal(2, result.Measurable);
        Assert.Equal(1, result.Covered);
    }

    [Fact]
    public void Calculate_ReportPathIsLongerThanTheChangedPath_Matches()
    {
        var result = ChangedLineCoverageCalculator.Calculate(
            [Change("server/app.ts", (1, 1))], [Coverage("D:/elsewhere/checkout/server/app.ts", (1, 1))]);

        Assert.Equal(1, result.Covered);
    }

    [Fact]
    public void Calculate_SuffixMatchesTwoFiles_IsAmbiguous_SoNeitherIsUsed()
    {
        var result = ChangedLineCoverageCalculator.Calculate(
            [Change("src/a/Program.cs", (1, 1))],
            [Coverage("x/src/a/Program.cs", (1, 1)), Coverage("y/src/a/Program.cs", (1, 0))]);

        Assert.Equal(0, result.Measurable);
    }

    [Fact]
    public void Calculate_SuffixMatchRespectsPathSegments()
    {
        // "OtherCollection.cs" ends with "Collection.cs" as text, but is a different file.
        var result = ChangedLineCoverageCalculator.Calculate(
            [Change("src/OtherCollection.cs", (1, 1))], [Coverage("Collection.cs", (1, 0))]);

        Assert.Equal(0, result.Measurable);
    }

    [Fact]
    public void Calculate_NoChanges_IsOneHundredPercent()
    {
        var result = ChangedLineCoverageCalculator.Calculate([], [Coverage("a.cs", (1, 0))]);

        Assert.Equal(100, result.Percent);
    }

    // ---- Normalize ----

    [Fact]
    public void Normalize_PathRelativeToASourceRootInsideTheRepository_BecomesRepositoryRelative()
    {
        _repo.Write("src/FileDbSharp/Collection.cs", "");
        var report = new CoverageReport([_repo.Combine("src", "FileDbSharp") + Path.DirectorySeparatorChar], [Coverage("Collection.cs", (1, 1))]);

        var file = Assert.Single(ChangedLineCoverageCalculator.Normalize(report, _repo.Path, _repo.Path));

        Assert.Equal("src/FileDbSharp/Collection.cs", file.Path);
    }

    [Fact]
    public void Normalize_PathRelativeToTheStepDirectory_BecomesRepositoryRelative()
    {
        _repo.Write("client/src/App.tsx", "");
        var report = new CoverageReport([], [Coverage(@"src\App.tsx", (1, 1))]);

        var file = Assert.Single(ChangedLineCoverageCalculator.Normalize(report, _repo.Path, _repo.Combine("client")));

        Assert.Equal("client/src/App.tsx", file.Path);
    }

    [Fact]
    public void Normalize_AbsolutePathInsideTheRepository_BecomesRepositoryRelative()
    {
        var absolute = _repo.Write("server/app.ts", "");
        var report = new CoverageReport([], [Coverage(absolute, (1, 1))]);

        Assert.Equal("server/app.ts", Assert.Single(ChangedLineCoverageCalculator.Normalize(report, _repo.Path, _repo.Path)).Path);
    }

    [Fact]
    public void Normalize_SeveralSourceRoots_PicksTheOneWhereTheFileExists()
    {
        _repo.Write("src/B/Thing.cs", "");
        var report = new CoverageReport([_repo.Combine("src", "A"), _repo.Combine("src", "B")], [Coverage("Thing.cs", (1, 1))]);

        Assert.Equal("src/B/Thing.cs", Assert.Single(ChangedLineCoverageCalculator.Normalize(report, _repo.Path, _repo.Path)).Path);
    }

    [Fact]
    public void Normalize_RelativeSourceRoot_IsRelativeToTheStepDirectory()
    {
        _repo.Write("api/src/Thing.py", "");
        var report = new CoverageReport(["src"], [Coverage("Thing.py", (1, 1))]);

        Assert.Equal("api/src/Thing.py", Assert.Single(ChangedLineCoverageCalculator.Normalize(report, _repo.Path, _repo.Combine("api"))).Path);
    }

    [Fact]
    public void Normalize_PathOutsideTheRepository_IsKeptAsWritten_ForSuffixMatching()
    {
        var outside = OperatingSystem.IsWindows() ? @"Z:\elsewhere\src\Collection.cs" : "/elsewhere/src/Collection.cs";
        var report = new CoverageReport([], [Coverage(outside, (1, 1))]);

        var file = Assert.Single(ChangedLineCoverageCalculator.Normalize(report, _repo.Path, _repo.Path));

        Assert.Equal(outside.Replace('\\', '/'), file.Path);
    }

    /// <summary>End to end on the real filedb-sharp report: its source root is another machine's
    /// checkout, so the file is found by suffix against a repository-relative diff path.</summary>
    [Fact]
    public void RealCoverletReport_MeasuresAChangedLineInCollection()
    {
        var report = CoberturaParser.Parse(Fixtures.Read("filedb-sharp.cobertura.xml"));
        var coverage = ChangedLineCoverageCalculator.Normalize(report, _repo.Path, _repo.Path);

        // In the real report, Collection.cs line 28 (Upsert) has 1,318 hits.
        var result = ChangedLineCoverageCalculator.Calculate([Change("src/FileDbSharp/Collection.cs", (28, 28))], coverage);

        Assert.Equal(1, result.Measurable);
        Assert.Equal(1, result.Covered);
    }
}
