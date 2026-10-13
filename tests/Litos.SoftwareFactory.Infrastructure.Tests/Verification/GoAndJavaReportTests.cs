using Litos.SoftwareFactory.Core.Verification;
using Litos.SoftwareFactory.Infrastructure.Verification;

namespace Litos.SoftwareFactory.Infrastructure.Tests.Verification;

/// <summary>The report formats the Go and Java presets read (m3-architecture.md §8).</summary>
public class GoAndJavaReportTests
{
    // ---- go test -json ----

    private const string GoEvents = """
        go: downloading example.com/dep v1.0.0
        {"Action":"start","Package":"example.com/calc"}
        {"Action":"run","Package":"example.com/calc","Test":"TestAdd"}
        {"Action":"output","Package":"example.com/calc","Test":"TestAdd","Output":"=== RUN   TestAdd\n"}
        {"Action":"pass","Package":"example.com/calc","Test":"TestAdd","Elapsed":0.01}
        {"Action":"run","Package":"example.com/calc","Test":"TestScale"}
        {"Action":"output","Package":"example.com/calc","Test":"TestScale","Output":"    calc_test.go:12: Scale(2, 0) = 0, want an error\n"}
        {"Action":"fail","Package":"example.com/calc","Test":"TestScale","Elapsed":0.02}
        {"Action":"run","Package":"example.com/calc","Test":"TestTable/zero"}
        {"Action":"skip","Package":"example.com/calc","Test":"TestTable/zero","Elapsed":0}
        {"Action":"fail","Package":"example.com/calc","Elapsed":0.05}
        """;

    [Fact]
    public void GoTest_EachTestsResult_WithAFailuresOutputAsItsMessage()
    {
        var cases = GoTestReportParser.Parse(GoEvents);

        Assert.Equal(
            [("example.com/calc :: TestAdd", TestOutcome.Passed), ("example.com/calc :: TestScale", TestOutcome.Failed), ("example.com/calc :: TestTable/zero", TestOutcome.Skipped)],
            cases.Select(c => (c.Name, c.Outcome)));
        Assert.Contains("want an error", cases[1].Message);
        Assert.Equal(0.01, cases[0].DurationSeconds);
    }

    [Fact]
    public void GoTest_APackageThatFailsWithNoFailedTest_IsAFailedCaseOfItsOwn()
    {
        var cases = GoTestReportParser.Parse("""
            {"Action":"build-output","ImportPath":"example.com/broken","Output":"./a.go:3:2: undefined: nope\n"}
            {"Action":"build-fail","ImportPath":"example.com/broken"}
            {"Action":"output","Package":"example.com/broken","Output":"FAIL\texample.com/broken [build failed]\n"}
            {"Action":"fail","Package":"example.com/broken","Elapsed":0}
            {"Action":"pass","Package":"example.com/fine","Test":"TestOk"}
            """);

        var failed = Assert.Single(cases, c => c.Outcome == TestOutcome.Failed);
        Assert.Equal("example.com/broken :: (package)", failed.Name);
        Assert.Contains("undefined: nope", failed.Message);
    }

    [Fact]
    public void GoTest_TextWithNoEvents_IsNotAReport_ButAnEmptyFileIsNoTests()
    {
        Assert.Throws<ReportFormatException>(() => GoTestReportParser.Parse("PASS\nok  \texample.com/calc\t0.01s\n"));
        Assert.Empty(GoTestReportParser.Parse(""));
    }

    [Fact]
    public void GoTest_AVeryLongFailure_KeepsItsEnd()
    {
        var output = string.Concat(Enumerable.Range(0, 2_000).Select(i => $"{{\"Action\":\"output\",\"Package\":\"p\",\"Test\":\"T\",\"Output\":\"line {i}\\n\"}}\n"));
        var message = GoTestReportParser.Parse(output + """{"Action":"fail","Package":"p","Test":"T"}""").Single().Message!;

        Assert.True(message.Length <= 4_001);
        Assert.EndsWith("line 1999", message);
    }

    // ---- go test -coverprofile ----

    [Fact]
    public void GoCover_EveryLineOfABlockGetsItsCount_TheHighestWhereBlocksMeet_AndTheModulePathIsTakenOff()
    {
        var report = GoCoverParser.Parse("""
            mode: set
            example.com/calc/calc.go:3.24,5.2 1 1
            example.com/calc/calc.go:5.2,7.3 2 0
            example.com/calc/internal/x.go:1.1,1.9 1 0
            not a block line
            """, "example.com/calc");

        var calc = report.Files.Single(f => f.Path == "calc.go");
        Assert.Equal([(3, 1), (4, 1), (5, 1), (6, 0), (7, 0)], calc.LineHits.OrderBy(h => h.Key).Select(h => (h.Key, h.Value)));
        Assert.Contains(report.Files, f => f.Path == "internal/x.go");
    }

    [Fact]
    public void GoCover_WithoutAModeLine_IsNotAProfile()
    {
        Assert.Throws<ReportFormatException>(() => GoCoverParser.Parse("calc.go:1.1,2.2 1 1\n"));
        Assert.Empty(GoCoverParser.Parse("").Files);
    }

    [Fact]
    public void GoCover_TheModulePath_IsReadFromGoMod()
    {
        var directory = Directory.CreateTempSubdirectory("litos-gomod-").FullName;
        try
        {
            Assert.Null(GoCoverParser.ModulePathIn(directory));
            File.WriteAllText(Path.Combine(directory, "go.mod"), "// a comment\nmodule \"example.com/quoted\"\n\ngo 1.22\n");
            Assert.Equal("example.com/quoted", GoCoverParser.ModulePathIn(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ---- JaCoCo ----

    private const string Jacoco = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <!DOCTYPE report PUBLIC "-//JACOCO//DTD Report 1.1//EN" "report.dtd">
        <report name="calc">
          <package name="com/acme">
            <class name="com/acme/Calc" sourcefilename="Calc.java"/>
            <sourcefile name="Calc.java">
              <line nr="3" mi="3" ci="0" mb="0" cb="0"/>
              <line nr="5" mi="0" ci="4" mb="0" cb="0"/>
              <line nr="9" mi="2" ci="0" mb="2" cb="0"/>
            </sourcefile>
          </package>
          <package name="">
            <sourcefile name="Main.java"><line nr="1" mi="0" ci="1"/></sourcefile>
          </package>
        </report>
        """;

    [Fact]
    public void Jacoco_ALineRanWhenAnyOfItsInstructionsDid_AndFilesAreNamedByPackage()
    {
        var report = JacocoParser.Parse(Jacoco);

        var calc = report.Files.Single(f => f.Path == "com/acme/Calc.java");
        Assert.Equal([(3, 0), (5, 4), (9, 0)], calc.LineHits.OrderBy(h => h.Key).Select(h => (h.Key, h.Value)));
        Assert.Contains(report.Files, f => f.Path == "Main.java");
    }

    [Fact]
    public void Jacoco_TheModulesSourceFolders_AreFoundFromWhereTheReportIs()
    {
        var path = Path.Combine(Path.GetTempPath(), "repo", "orders", "target", "site", "jacoco", "jacoco.xml");

        var roots = JacocoParser.Parse(Jacoco, path).SourceRoots;

        var module = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "repo", "orders")).Replace('\\', '/');
        Assert.Contains($"{module}/src/main/java", roots);
        Assert.Contains($"{module}/src/main/kotlin", roots);
        Assert.Empty(JacocoParser.Parse(Jacoco, Path.Combine(Path.GetTempPath(), "jacoco.xml")).SourceRoots);
    }

    [Fact]
    public void Jacoco_AnotherRootElement_IsNotAJacocoReport()
    {
        Assert.Throws<ReportFormatException>(() => JacocoParser.Parse("<coverage/>"));
    }
}
