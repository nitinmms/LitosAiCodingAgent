using Litos.Probe.Web.Core;
using Litos.Probe.Web.Core.Reports;
using static Litos.Probe.Web.IntegrationTests.ProbeHarness;

namespace Litos.Probe.Web.IntegrationTests;

/// <summary>
/// README §11 W1 exit check: each broken app fails with the right message. Split into three
/// classes so xunit runs them in parallel; each broken app has its own folder, so their
/// working-copy locks never meet.
/// </summary>
public abstract class BrokenAppsBase
{
    /// <summary>The secrets the broken apps' form login reads; the server accepts only this password.</summary>
    protected static readonly Dictionary<string, string> Secrets = new()
    {
        ["BROKEN_ADMIN_PASS"] = "correct-horse",
        ["BROKEN_USER_PASS"] = "correct-horse",
    };

    protected static async Task AssertFailsAsync(string app, string checkId, Classification classification, string fragment)
    {
        await RequireAsync("node");
        var (report, _) = await GateAsync(App("BrokenApps", app), Secrets, pageTimeout: TimeSpan.FromSeconds(8));

        var check = Check(report, checkId);
        Assert.True(check.Status == CheckStatus.Failed, $"{checkId} should fail.\n{Describe(report)}");
        Assert.Equal(classification, check.Classification);
        Assert.Contains(fragment, check.Message);
        Assert.Equal(classification == Classification.EnvironmentFailure ? ProbeWebOutcome.EnvironmentFailure : ProbeWebOutcome.Failed, report.Outcome);
        Assert.Equal(0, report.Usage.ModelCalls);
    }
}

public class StartFailureTests : BrokenAppsBase
{
    [SkippableTheory]
    [InlineData("wrong-port", "start", Classification.ApplicationDefect, "something new listens on 8787")]
    [InlineData("https-redirect", "start", Classification.ApplicationDefect, "Testing still forces HTTPS")]
    [InlineData("root-500", "start", Classification.ApplicationDefect, "GET / on web answered 500")]
    [InlineData("crash-on-start", "start", Classification.EnvironmentFailure, "exited with code 3")]
    [InlineData("reset-fails", "reset", Classification.EnvironmentFailure, "exited with code 4")]
    [InlineData("test-endpoint-in-production", "production-test-endpoints", Classification.ApplicationDefect, "POST /__test/reset answered 200")]
    public Task Fails_with_the_right_message(string app, string checkId, Classification c, string fragment) => AssertFailsAsync(app, checkId, c, fragment);

    [SkippableFact]
    public async Task A_crashed_app_reports_its_own_last_words()
    {
        await RequireAsync("node");
        var (report, _) = await GateAsync(App("BrokenApps", "crash-on-start"));
        Assert.Contains("Error: Cannot find module './config.json'", Check(report, "start").Evidence.Log);
    }
}

public class PageFailureTests : BrokenAppsBase
{
    [SkippableTheory]
    [InlineData("page-error", "visit:anonymous:/", Classification.ApplicationDefect, "Uncaught error")]
    [InlineData("api-500", "visit:anonymous:/", Classification.ApplicationDefect, "HTTP 500 on GET /api/data")]
    [InlineData("server-log-error", "visit:anonymous:/", Classification.ApplicationDefect, "could not load the dashboard widgets")]
    [InlineData("error-marker", "visit:anonymous:/", Classification.ApplicationDefect, "Error marker visible: #error-ui")]
    [InlineData("marker-missing", "visit:anonymous:/", Classification.ApplicationDefect, "readiness marker [data-testid=app-interactive] never appeared")]
    [InlineData("route-404", "visit:anonymous:/missing", Classification.TestDefect, "answered 404")]
    [InlineData("unnamed-button", "lint", Classification.ApplicationDefect, "button.icon-only")]
    public Task Fails_with_the_right_message(string app, string checkId, Classification c, string fragment) => AssertFailsAsync(app, checkId, c, fragment);

    [SkippableFact]
    public async Task A_failure_that_passes_on_its_rerun_is_flaky_not_failed()
    {
        await RequireAsync("node");
        var (report, _) = await GateAsync(App("BrokenApps", "flaky"));

        var check = Check(report, "visit:anonymous:/sometimes");
        Assert.Equal(CheckStatus.Passed, check.Status);
        Assert.Equal(Classification.Flaky, check.Classification);
        Assert.Contains("answered HTTP 500", check.Message);
        Assert.Equal(2, check.Evidence.Screenshots.Count);
        Assert.Equal(ProbeWebOutcome.Passed, report.Outcome);
    }

    [SkippableFact]
    public async Task The_run_limit_stops_everything_and_says_so()
    {
        await RequireAsync("node");
        var (report, _) = await GateAsync(App("BrokenApps", "marker-missing"), timeout: TimeSpan.FromSeconds(6), pageTimeout: TimeSpan.FromSeconds(60));

        var timeout = Check(report, "timeout");
        Assert.Equal(Classification.EnvironmentFailure, timeout.Classification);
        Assert.Equal(ProbeWebOutcome.EnvironmentFailure, report.Outcome);
        Assert.True(report.Usage.WallSeconds < 40, $"took {report.Usage.WallSeconds}s");
    }
}

public class AccessFailureTests : BrokenAppsBase
{
    [SkippableTheory]
    [InlineData("unlabelled-login", "signin:admin", Classification.ApplicationDefect, "There is no field labelled 'Email' on /login")]
    [InlineData("not-protected", "logged-out:/admin", Classification.ApplicationDefect, "opened without signing in")]
    [InlineData("denied-opened", "denied:user:/admin", Classification.ApplicationDefect, "user opened /admin (HTTP 200)")]
    public Task Fails_with_the_right_message(string app, string checkId, Classification c, string fragment) => AssertFailsAsync(app, checkId, c, fragment);

    [SkippableFact]
    public async Task A_wrong_password_is_an_authentication_failure_and_skips_that_roles_pages()
    {
        await RequireAsync("node");
        var wrong = new Dictionary<string, string> { ["BROKEN_ADMIN_PASS"] = "not-the-password", ["BROKEN_USER_PASS"] = "correct-horse" };
        var (report, _) = await GateAsync(App("BrokenApps", "bad-credentials"), wrong);

        var signIn = Check(report, "signin:admin");
        Assert.Equal(Classification.AuthenticationFailure, signIn.Classification);
        Assert.Contains("was not reached", signIn.Message);
        Assert.Equal(CheckStatus.Skipped, Check(report, "visit:admin:/admin").Status);
        Assert.Equal(CheckStatus.Passed, Check(report, "denied:user:/admin").Status);
    }

    [SkippableFact]
    public async Task A_missing_secret_is_an_authentication_failure()
    {
        await RequireAsync("node");
        var (report, _) = await GateAsync(App("BrokenApps", "healthy"), new Dictionary<string, string> { ["BROKEN_USER_PASS"] = "correct-horse" });

        var signIn = Check(report, "signin:admin");
        Assert.Equal(Classification.AuthenticationFailure, signIn.Classification);
        Assert.Contains("the secret BROKEN_ADMIN_PASS is not set", signIn.Message);
    }

    [SkippableFact]
    public async Task The_healthy_app_passes_every_step_and_leaves_nothing_running()
    {
        await RequireAsync("node");
        var (report, artifacts) = await GateAsync(App("BrokenApps", "healthy"), Secrets);

        Assert.True(report.Outcome == ProbeWebOutcome.Passed, Describe(report));
        Assert.Equal(
            ["build", "start", "reset", "signin:admin", "signin:user", "visit:anonymous:/", "visit:admin:/admin", "logged-out:/admin", "denied:user:/admin", "production-test-endpoints", "lint"],
            report.Checks.Select(c => c.Id));

        // Evidence: a screenshot per visit, a trace per role, the server log.
        Assert.All(report.Checks.Where(c => c.Kind is CheckKind.RouteVisit or CheckKind.LoggedOutRedirect or CheckKind.Denied), c =>
        {
            Assert.Single(c.Evidence.Screenshots);
            Assert.NotNull(c.Evidence.Trace);
            Assert.True(File.Exists(Path.Combine(artifacts, c.Evidence.Screenshots[0])));
        });
        Assert.True(File.Exists(Path.Combine(artifacts, "logs", "web.log")));
        Assert.Empty(report.Findings);

        // The port is free again: the process tree went with the run.
        var port = report.App!.Processes.Single().Port;
        using var client = new System.Net.Sockets.TcpClient();
        Assert.ThrowsAny<System.Net.Sockets.SocketException>(() => client.Connect("127.0.0.1", port));
    }

    [SkippableFact]
    public async Task A_second_run_in_the_same_folder_is_refused_while_one_is_running()
    {
        using var held = Processes.WorkingCopyLock.Acquire(App("BrokenApps", "root-500"));
        var (report, _) = await GateAsync(App("BrokenApps", "root-500"));

        Assert.Equal(ProbeWebOutcome.EnvironmentFailure, report.Outcome);
        Assert.Contains("already running", Check(report, "start").Message);
    }
}
