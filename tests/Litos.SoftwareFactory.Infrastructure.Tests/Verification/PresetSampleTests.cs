using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Verification;
using Litos.SoftwareFactory.Infrastructure.Verification;

namespace Litos.SoftwareFactory.Infrastructure.Tests.Verification;

/// <summary>
/// The Python, Java and Go presets (m3-architecture.md §8) run on a small sample repository each,
/// with the real toolchain: tests counted from the report, changed-line coverage measured, and a
/// failing test named. Each skips on a machine without its toolchain, as the PostgreSQL tests do
/// without a database. The first Python and Java runs download pytest and the Maven plugins.
/// </summary>
public sealed class PresetSampleTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("litos-preset-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A build server may still hold a file for a moment; the temp folder is cleaned later.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void RequireOnPath(params string[] programs)
    {
        var path = ProfileExecutables.HostPath();
        var missing = programs.Where(p => ProfileExecutables.Find(p, path) is null).ToList();
        Skip.If(missing.Count > 0, $"Not on this machine's PATH: {string.Join(", ", missing)}.");
    }

    /// <summary>The sample copied where the verifier can write into it, as a working copy is.</summary>
    private string Repository(string sample)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Samples", sample);
        var target = Path.Combine(_root, "repo");
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var copy = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy);
        }

        return target;
    }

    private async Task<VerificationOutcome> VerifyAsync(string preset, string repository, IReadOnlyList<FileChange>? changed)
    {
        var request = new VerificationRequest(repository, VerificationPresets.Load(preset), changed, Path.Combine(_root, "logs", Guid.NewGuid().ToString("N")))
        {
            TempDirectory = Path.Combine(_root, "tmp"),
            Variables = ProfileVariables.For(Path.Combine(_root, "data"), Guid.Parse("00000000-0000-0000-0000-000000000042")),
        };
        return await new ProfileVerifier().VerifyAsync(request, CancellationToken.None);
    }

    private static string Problems(VerificationOutcome outcome) => string.Join(" | ", outcome.Problems);

    /// <summary>Passing tests, a measured change that is partly covered, and then a failing test named.</summary>
    private async Task CheckAsync(string preset, string repository, FileChange changedSource, string testFile, string failingTest, string failingTestName)
    {
        var passing = await VerifyAsync(preset, repository, [changedSource]);

        Assert.True(passing.UnitTests == UnitTestStatus.Passed, $"{passing.UnitTests}: {Problems(passing)}");
        Assert.NotEqual(BuildStatus.Failed, passing.Build);
        Assert.True(passing.PassedCount >= 1, Problems(passing));
        var coverage = passing.ChangedLines;
        Assert.True(coverage is not null, Problems(passing));
        Assert.Empty(coverage!.UnmeasuredFiles);
        // add() ran and the zero-factor branch of scale() did not.
        Assert.InRange(coverage.Covered, 1, coverage.Measurable - 1);
        Assert.Equal(CoverageStatus.BelowThreshold, passing.Coverage);

        File.AppendAllText(Path.Combine(repository, testFile), failingTest);
        var failing = await VerifyAsync(preset, repository, null);

        Assert.Equal(UnitTestStatus.Failed, failing.UnitTests);
        var failed = Assert.Single(failing.Tests, t => t.Outcome == TestOutcome.Failed);
        Assert.Contains(failingTestName, failed.Name);
        Assert.False(string.IsNullOrWhiteSpace(failed.Message));
    }

    [SkippableFact]
    public async Task ThePythonPreset_RunsPytest_InTheProjectsVirtualEnvironment()
    {
        RequireOnPath("python");
        var repository = Repository("python");

        await CheckAsync(
            VerificationPresets.Python, repository,
            new FileChange("calc.py", [new LineRange(1, 8)]),
            "test_calc.py", "\n\ndef test_broken():\n    assert add(1, 1) == 3\n", "test_broken");

        // The environment is the project's, in the data folder, never in the working copy.
        Assert.True(Directory.Exists(Path.Combine(_root, "data", "venvs", "00000000000000000000000000000042")));
        Assert.False(File.Exists(Path.Combine(repository, ".coverage")));
        Assert.True(File.Exists(Path.Combine(repository, ".factory", "junit.xml")));
    }

    [SkippableFact]
    public async Task TheJavaPreset_RunsMaven_WithJaCoCo()
    {
        RequireOnPath("mvn", "java");

        await CheckAsync(
            VerificationPresets.JavaMaven, Repository("java"),
            new FileChange("src/main/java/com/acme/Calc.java", [new LineRange(1, 15)]),
            Path.Combine("src", "test", "java", "com", "acme", "BrokenTest.java"),
            "package com.acme;\nimport static org.junit.jupiter.api.Assertions.assertEquals;\nimport org.junit.jupiter.api.Test;\nclass BrokenTest {\n  @Test\n  void isBroken() { assertEquals(3, Calc.add(1, 1)); }\n}\n",
            "isBroken");
    }

    [SkippableFact]
    public async Task TheGoPreset_ReadsGoTestJson_AndTheCoverProfile()
    {
        RequireOnPath("go");
        var repository = Repository("go");

        await CheckAsync(
            VerificationPresets.Go, repository,
            new FileChange("calc.go", [new LineRange(1, 14)]),
            "calc_test.go", "\nfunc TestBroken(t *testing.T) {\n\tif Add(1, 1) != 3 {\n\t\tt.Fatal(\"1 + 1 is not 3\")\n\t}\n}\n", "TestBroken");

        // go test printed its report; the verifier kept it.
        Assert.True(File.Exists(Path.Combine(repository, ".factory", "go-test.json")));
    }

    [SkippableFact]
    public async Task AGoPackageThatDoesNotBuild_IsAFailure_NotAnEmptyPass()
    {
        RequireOnPath("go");
        var repository = Repository("go");
        File.AppendAllText(Path.Combine(repository, "calc_test.go"), "\nfunc TestTypo(t *testing.T) { undefinedName() }\n");

        var outcome = await VerifyAsync(VerificationPresets.Go, repository, null);

        Assert.Equal(UnitTestStatus.Failed, outcome.UnitTests);
    }
}
