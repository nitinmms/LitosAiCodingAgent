using Litos.Probe.Web.Cli;
using Litos.Probe.Web.Core.Reports;
using Litos.Probe.Web.Processes;
using Litos.Probe.Web.Running;

namespace Litos.Probe.Web.Tests;

/// <summary>The runtime's pure parts: everything that needs no process and no browser.</summary>
public class RuntimeUnitTests
{
    [Theory]
    [InlineData("1-8", new[] { 1, 2, 3, 4, 5, 6, 7, 8 })]
    [InlineData("1,2,4", new[] { 1, 2, 4 })]
    [InlineData("1-4, 8", new[] { 1, 2, 3, 4, 8 })]
    [InlineData("8", new[] { 8 })]
    public void Gate_scope_parses_steps_and_ranges(string text, int[] steps) =>
        Assert.Equal(steps, GateScope.Parse(text).Steps.Order());

    [Theory]
    [InlineData("0-3")]
    [InlineData("9")]
    [InlineData("4-2")]
    [InlineData("x")]
    [InlineData("")]
    [InlineData("1-2-3")]
    public void Gate_scope_refuses_bad_steps(string text) => Assert.Throws<FormatException>(() => GateScope.Parse(text));

    [Fact]
    public void Steps_one_and_two_always_run()
    {
        var scope = GateScope.Parse("8");
        Assert.True(scope.Includes(1));
        Assert.True(scope.Includes(2));
        Assert.False(scope.Includes(4));
        Assert.False(GateScope.Smoke.Includes(7));
        Assert.False(GateScope.Smoke.Includes(8));
    }

    [Theory]
    [InlineData(404, 400, true)]
    [InlineData(405, 404, true)]
    [InlineData(400, 400, true)]
    [InlineData(200, 200, false)]
    [InlineData(204, 404, false)]
    [InlineData(500, 404, false)]
    [InlineData(401, 404, false)]
    public void A_test_endpoint_is_absent_when_it_answers_as_an_unknown_path_does(int status, int control, bool absent) =>
        Assert.Equal(absent, GateRun.LooksAbsent(status, control));

    [Theory]
    [InlineData("POST /__test/reset", "POST", "/__test/reset")]
    [InlineData("GET /health", "GET", "/health")]
    [InlineData("/__test/clock", "POST", "/__test/clock")]
    public void Endpoints_split_into_method_and_path(string endpoint, string method, string path) =>
        Assert.Equal((method, path), AppEnvironment.SplitEndpoint(endpoint));

    [Theory]
    [InlineData("visit-patient-/my/appointments", "visit-patient-my-appointments")]
    [InlineData("visit-anonymous-/", "visit-anonymous")]
    [InlineData("///", "root")]
    public void Artifact_names_are_file_safe(string text, string expected) => Assert.Equal(expected, GateRun.ArtifactName(text));

    [Fact]
    public void The_output_log_reads_since_a_mark_and_keeps_only_its_capacity()
    {
        var log = new OutputLog(capacity: 3);
        log.Append(OutputStream.Out, "one");
        var mark = log.Mark();
        log.Append(OutputStream.Err, "two");
        log.Append(OutputStream.Out, "three");
        log.Append(OutputStream.Out, "four");

        Assert.Equal(["two", "three", "four"], log.Since(mark).Select(l => l.Text));
        Assert.Equal(["three", "four"], log.Tail(2).Select(l => l.Text));
        Assert.Equal(OutputStream.Err, log.Since(mark)[0].Stream);
        Assert.Equal(3, log.Tail(10).Count);
    }

    [Fact]
    public void Allocated_ports_are_distinct_and_free()
    {
        var ports = Ports.Allocate(["web", "api", "worker"]);
        Assert.Equal(3, ports.Values.Distinct().Count());
        Assert.All(ports.Values, p => Assert.InRange(p, 1024, 65535));
    }

    [Fact]
    public void The_windows_resolver_finds_a_cmd_shim()
    {
        using var folder = new TempFolder();
        folder.Write("npm.cmd", "@echo off");

        Assert.Equal(Path.Combine(folder.Path, "npm.cmd"), ExecutableResolver.ResolveOnWindows("npm", folder.Path, ".EXE;.CMD"), ignoreCase: true);
        Assert.Equal("./tool", ExecutableResolver.ResolveOnWindows("./tool", folder.Path, ".CMD"));
        Assert.Equal("missing", ExecutableResolver.ResolveOnWindows("missing", folder.Path, ".CMD"));
    }

    [Fact]
    public void A_second_lock_on_the_same_working_copy_is_refused()
    {
        using var folder = new TempFolder();
        using (WorkingCopyLock.Acquire(folder.Path))
        {
            Assert.Throws<WorkingCopyBusyException>(() => WorkingCopyLock.Acquire(folder.Path + Path.DirectorySeparatorChar));
        }

        using var again = WorkingCopyLock.Acquire(folder.Path);
    }

    [Fact]
    public async Task The_folder_sink_keeps_ids_unique_and_names_safe()
    {
        using var folder = new TempFolder();
        var sink = new FolderArtifactSink(folder.Path);

        var first = await sink.SaveAsync(ArtifactKind.Screenshot, "visit.png", new MemoryStream([1]), CancellationToken.None);
        var second = await sink.SaveAsync(ArtifactKind.Screenshot, "visit.png", new MemoryStream([2]), CancellationToken.None);
        var odd = await sink.SaveAsync(ArtifactKind.Log, "../web:1.log", new MemoryStream([3]), CancellationToken.None);

        Assert.Equal("screenshots/visit.png", first);
        Assert.Equal("screenshots/visit-2.png", second);
        Assert.Equal("logs/web-1.log", odd);
        Assert.True(File.Exists(Path.Combine(folder.Path, "screenshots", "visit-2.png")));
    }

    [Fact]
    public void The_environment_resolver_reads_the_given_variables()
    {
        var resolver = new EnvironmentSecretResolver(new Dictionary<string, string> { ["TEST_STAFF_PASS"] = "x" });
        Assert.Equal("x", resolver.Resolve("TEST_STAFF_PASS"));
        Assert.Null(resolver.Resolve("OTHER"));
    }

    [Theory]
    [InlineData(ProbeWebOutcome.Passed, 0)]
    [InlineData(ProbeWebOutcome.Failed, 1)]
    [InlineData(ProbeWebOutcome.Inconclusive, 1)]
    [InlineData(ProbeWebOutcome.EnvironmentFailure, 2)]
    [InlineData(ProbeWebOutcome.InvalidContract, 3)]
    public void Cli_exit_codes_follow_the_readme(ProbeWebOutcome outcome, int code) => Assert.Equal(code, CliCommands.ExitCode(outcome));

    [Fact]
    public async Task The_cli_refuses_a_bad_contract_with_exit_code_3_and_lists_its_problems()
    {
        using var repo = new TempFolder();
        repo.Write("litos.test.yaml", "version: 1\napplication:\n  processes: []\n");
        var output = new StringWriter();

        var code = await new CliCommands(output, new CliSettings(false, null, TimeSpan.FromMinutes(1), null)).GateAsync(repo.Path, null, "1-8", smoke: false, CancellationToken.None);

        Assert.Equal(CliCommands.BadContract, code);
        Assert.Contains("at least one process is required", output.ToString());
    }

    [Fact]
    public async Task The_cli_says_where_the_missing_contract_should_be()
    {
        using var repo = new TempFolder();
        var output = new StringWriter();

        var code = await new CliCommands(output, new CliSettings(false, null, TimeSpan.FromMinutes(1), null)).GateAsync(repo.Path, null, "1-8", smoke: true, CancellationToken.None);

        Assert.Equal(CliCommands.BadContract, code);
        Assert.Contains("dry-run", output.ToString());
    }

    [Fact]
    public void The_cli_detect_lists_candidates()
    {
        using var repo = new TempFolder();
        repo.Write("package.json", """{ "dependencies": { "express": "^5" } }""");
        var output = new StringWriter();

        Assert.Equal(0, new CliCommands(output, new CliSettings(false, null, TimeSpan.FromMinutes(1), null)).Detect(repo.Path));
        Assert.Contains("node-express-spa", output.ToString());
    }

    [Fact]
    public void The_summary_shows_failures_with_their_class_message_and_log()
    {
        var report = new ProbeWebReport
        {
            Kind = ReportKind.Smoke,
            Outcome = ProbeWebOutcome.Failed,
            Checks =
            [
                CheckResult.Pass("start", CheckKind.Start),
                CheckResult.Fail("visit:admin:/x", CheckKind.RouteVisit, Classification.ApplicationDefect, "/x answered HTTP 500.") with
                {
                    Evidence = new Evidence { Log = ["web: fail: boom"] },
                },
            ],
            Findings = ["No reset declared."],
        };
        var output = new StringWriter();

        Summary.Write(output, report, "C:/out");

        var text = output.ToString();
        Assert.Contains("Smoke · Failed", text);
        Assert.Contains("✗ visit:admin:/x [ApplicationDefect]", text);
        Assert.Contains("/x answered HTTP 500.", text);
        Assert.Contains("| web: fail: boom", text);
        Assert.Contains("- No reset declared.", text);
    }

    [Fact]
    public async Task An_invalid_contract_comes_back_without_running_anything()
    {
        var engine = new ProbeWeb(Samples.Profiles);
        var c = Samples.Minimal() with { Application = Samples.Minimal().Application with { Reset = null } };
        var options = new ProbeWebOptions(new EnvironmentSecretResolver(), new FolderArtifactSink(Path.GetTempPath()), new Dictionary<string, string>(), TimeSpan.FromMinutes(1));

        var report = await engine.SmokeAsync("C:/does/not/matter", c, options, CancellationToken.None);

        Assert.Equal(ProbeWebOutcome.InvalidContract, report.Outcome);
        Assert.Equal(ReportKind.Smoke, report.Kind);
        Assert.Empty(report.Checks);
        Assert.Contains(report.Findings, f => f.Contains("a reset is required"));
    }

    [Fact]
    public void An_unknown_stack_is_a_validation_error_not_a_crash() =>
        Assert.Contains("no stack profile 'rails'", Assert.Single(new ProbeWeb(Samples.Profiles).Validate(Samples.Minimal("rails"))));
}
