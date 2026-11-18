using Litos.Probe.Web.Core.Reports;
using Litos.Probe.Web.Profiles;
using static Litos.Probe.Web.IntegrationTests.ProbeHarness;

namespace Litos.Probe.Web.IntegrationTests;

/// <summary>README §11 W1 exit check: the Gate passes on both tier-1 test apps.</summary>
public class NodeExpressSpaTests
{
    [SkippableFact]
    public async Task The_gate_passes_every_step()
    {
        await RequireAsync("node", "npm");
        var (report, _) = await GateAsync(App("NodeExpressSpa"));

        Assert.True(report.Outcome == ProbeWebOutcome.Passed, Describe(report));
        Assert.Equal("node-express-spa", report.Contract!.Stack);
        Assert.Contains("snapshot", Check(report, "reset").Message);
        Assert.Equal(CheckStatus.Passed, Check(report, "visit:anonymous:/about").Status);
        Assert.Equal(CheckStatus.Passed, Check(report, "production-test-endpoints").Status);
        Assert.Equal(2, report.Lint!.Pages);
        Assert.Equal(0, report.Lint.Unnamed);
    }

    [SkippableFact]
    public async Task A_dry_run_of_the_profile_defaults_starts_the_app_and_lists_the_gaps()
    {
        await RequireAsync("node", "npm");
        var app = App("NodeExpressSpa");
        // The profile's default build is only `npm run build`; the dependencies come from the gate's own build.
        Skip.If(!Directory.Exists(Path.Combine(app, "node_modules")), "Run the NodeExpressSpa gate once first (npm install).");

        var options = new ProbeWebOptions(new EnvironmentSecretResolver(new Dictionary<string, string>()),
            new FolderArtifactSink(Path.Combine(Path.GetTempPath(), "probe-web-it", "dry-" + Guid.NewGuid().ToString("N")[..8])),
            HostEnvironment(), TimeSpan.FromMinutes(5));
        var profiles = StackProfiles.Embedded();
        var report = await new ProbeWeb(profiles).DryRunAsync(app, profiles.Find("node-express-spa")!, options, CancellationToken.None);

        Assert.Equal(ReportKind.DryRun, report.Kind);
        Assert.Equal("defaults", report.Contract!.Source);
        Assert.True(report.Outcome == ProbeWebOutcome.Passed, Describe(report));
        Assert.Contains(report.Findings, f => f.StartsWith("No contract: these are the node-express-spa profile's defaults", StringComparison.Ordinal));
        Assert.Contains(report.Findings, f => f.StartsWith("No reset declared", StringComparison.Ordinal));
    }
}

public class AspNetBlazorTests
{
    private static readonly Dictionary<string, string> Secrets = new()
    {
        ["TEST_ADMIN_PASS"] = "Clinic-Admin-1!",
        ["TEST_STAFF_PASS"] = "Clinic-Staff-1!",
        ["TEST_PATIENT_PASS"] = "Clinic-Patient-1!",
    };

    [SkippableFact]
    public async Task The_gate_passes_every_step_for_three_roles_and_no_secret_reaches_an_artifact()
    {
        await RequireAsync("dotnet");
        await RequireDockerAsync();
        var (report, artifacts) = await GateAsync(App("AspNetBlazor"), Secrets);

        Assert.True(report.Outcome == ProbeWebOutcome.Passed, Describe(report));
        Assert.Equal(["admin", "patient", "staff"], report.Checks.Where(c => c.Kind == CheckKind.SignIn).Select(c => c.Role!).Order());
        Assert.Equal(3, report.Checks.Count(c => c.Kind == CheckKind.LoggedOutRedirect && c.Status == CheckStatus.Passed));
        Assert.Equal(3, report.Checks.Count(c => c.Kind == CheckKind.Denied && c.Status == CheckStatus.Passed));
        Assert.Equal(CheckStatus.Passed, Check(report, "production-test-endpoints").Status);

        // Postgres and Mailpit came from throwaway containers, and each sign-in sent an email.
        Assert.Equal(CheckStatus.Passed, Check(report, "services").Status);
        Assert.Equal(["db", "mail"], report.App!.Services.Select(s => s.Name));
        Assert.Equal(3, report.Emails.Count(e => e.Subject == "New sign-in to Clinic Portal"));
        Assert.All(report.Emails, e => Assert.True(File.Exists(Path.Combine(artifacts, e.Artifact!))));
        var (_, left) = await RunDockerAsync("ps", "-aq", "--filter", "label=litos.probe.owner=probe-web-tests");
        Assert.Equal("", left.Trim());

        // README §10: a test scans every artifact of a run for the secret values.
        var texts = ArtifactTexts(artifacts).Append(("report.json", report.ToJson())).ToList();
        Assert.Contains(texts, t => t.Item1.Contains("trace-admin.zip", StringComparison.Ordinal));
        foreach (var secret in Secrets.Values)
            Assert.DoesNotContain(texts, t => t.Item2.Contains(secret, StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task Smoke_runs_steps_one_to_six_only()
    {
        await RequireAsync("dotnet");
        await RequireDockerAsync();
        var (report, _) = await GateAsync(App("AspNetBlazor"), Secrets, smoke: true);

        Assert.Equal(ReportKind.Smoke, report.Kind);
        Assert.True(report.Outcome == ProbeWebOutcome.Passed, Describe(report));
        Assert.DoesNotContain(report.Checks, c => c.Kind is CheckKind.ProductionTestEndpoints or CheckKind.SelectorLint);
        Assert.Null(report.Lint);
    }
}
