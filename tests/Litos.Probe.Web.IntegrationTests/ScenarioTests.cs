using Litos.Probe.Web.Core.Reports;
using Litos.Probe.Web.Core.Scenarios;
using static Litos.Probe.Web.IntegrationTests.ProbeHarness;

namespace Litos.Probe.Web.IntegrationTests;

/// <summary>
/// README_Litos.Probe.Web.md W3.5: fixed-step scenarios in a real browser. The W3 exit check's
/// second half is here: a renamed button gives a TestDefect, not a code change.
/// </summary>
[Collection(NodeExpressSpaCollection.Name)]
public class NodeExpressSpaScenarioTests
{
    private const string Scenarios = """
        [
          {
            "id": "add-a-note",
            "criterion": "A person can add a note and sees it in the list.",
            "steps": [
              { "type": "fill", "target": { "label": "New note" }, "value": "Probe {run}" },
              { "type": "click", "target": { "role": "button", "name": "Add note" } },
              { "type": "expectResponse", "method": "POST", "path": "/api/notes", "status": 201 },
              { "type": "expectText", "target": { "testId": "notes" }, "text": "Probe" },
              { "type": "screenshot", "name": "added" }
            ]
          },
          {
            "id": "the-list-starts-clean",
            "criterion": "Each scenario starts from the seeded notes.",
            "steps": [
              { "type": "expectText", "target": { "testId": "notes" }, "text": "Buy flour" },
              { "type": "expectHidden", "target": { "text": "Probe", "within": { "testId": "notes" } } },
              { "type": "click", "target": { "role": "link", "name": "About" } },
              { "type": "expectUrl", "url": "/about" }
            ]
          },
          {
            "id": "renamed-button",
            "steps": [
              { "type": "fill", "target": { "label": "New note" }, "value": "x" },
              { "type": "click", "target": { "role": "button", "name": "Save note" } },
              { "type": "expectText", "target": { "testId": "notes" }, "text": "x" }
            ]
          },
          {
            "id": "wrong-text",
            "steps": [ { "type": "expectText", "target": { "testId": "notes" }, "text": "Walk the dog", "timeoutSeconds": 2 } ]
          },
          {
            "id": "not-a-role-here",
            "role": "admin",
            "steps": [ { "type": "expectUrl", "url": "/" } ]
          }
        ]
        """;

    [SkippableFact]
    public async Task Scenarios_pass_fail_and_tell_a_renamed_button_from_a_broken_app()
    {
        await RequireAsync("node", "npm");
        var (report, artifacts) = await ScenariosAsync(App("NodeExpressSpa"), WebScenario.Parse(Scenarios));

        Assert.Equal(ReportKind.Scenarios, report.Kind);
        Assert.Equal(0, report.Usage.ModelCalls);
        // Only build, start and reset ran around the scenarios: no visits, no lint.
        Assert.DoesNotContain(report.Checks, c => c.Kind is CheckKind.RouteVisit or CheckKind.SelectorLint or CheckKind.SignIn);

        var added = Check(report, "scenario:add-a-note");
        Assert.True(added.Status == CheckStatus.Passed, Describe(report));
        Assert.Equal("A person can add a note and sees it in the list.", added.Scenario!.Criterion);
        Assert.Equal(6, added.Steps!.Count);
        Assert.Equal("open /", added.Steps[0].Description);
        Assert.Equal("click button \"Add note\"", added.Steps[2].Description);
        Assert.All(added.Steps, s => Assert.Equal(CheckStatus.Passed, s.Status));
        Assert.NotNull(added.Steps[5].Screenshot);
        Assert.Equal(2, added.Evidence.Screenshots.Count);
        Assert.NotNull(added.Evidence.Trace);
        Assert.All(added.Evidence.Screenshots, id => Assert.True(File.Exists(Path.Combine(artifacts, id))));

        // The note the first scenario added is gone: the snapshot was restored before the second.
        Assert.True(Check(report, "scenario:the-list-starts-clean").Status == CheckStatus.Passed, Describe(report));

        var renamed = Check(report, "scenario:renamed-button");
        Assert.Equal(Classification.TestDefect, renamed.Classification);
        Assert.Contains("Step 2 of 3, click button \"Save note\": No button named \"Save note\" on /. Buttons on the page: \"Add note\".", renamed.Message);
        Assert.Equal([CheckStatus.Passed, CheckStatus.Passed, CheckStatus.Failed, CheckStatus.Skipped], renamed.Steps!.Select(s => s.Status));
        Assert.NotNull(renamed.Steps[2].Screenshot);

        var wrong = Check(report, "scenario:wrong-text");
        Assert.Equal(Classification.ApplicationDefect, wrong.Classification);
        Assert.Contains("[data-testid=notes] shows \"Buy flour Feed the starter Book the oven\", which does not contain \"Walk the dog\"", wrong.Message);

        // Never started: the app has no login.
        var invalid = Check(report, "scenario:not-a-role-here");
        Assert.Equal(Classification.TestDefect, invalid.Classification);
        Assert.Contains("role: the app has no login", invalid.Message);
        Assert.Null(invalid.Steps);

        Assert.Equal(ProbeWebOutcome.Failed, report.Outcome);
    }
}

public class SignedInScenarioTests
{
    private static readonly Dictionary<string, string> Secrets = new()
    {
        ["BROKEN_ADMIN_PASS"] = "correct-horse",
        ["BROKEN_USER_PASS"] = "correct-horse",
    };

    private const string Scenarios = """
        [
          {
            "id": "admin-opens-admin",
            "role": "admin",
            "start": "/admin",
            "steps": [ { "type": "expectText", "target": { "role": "heading" }, "text": "Admin" } ]
          },
          {
            "id": "user-is-refused",
            "role": "user",
            "start": "/admin",
            "steps": [ { "type": "expectText", "target": { "role": "heading" }, "text": "Access denied" } ]
          },
          {
            "id": "signs-in-through-the-form",
            "steps": [
              { "type": "navigate", "path": "/login" },
              { "type": "fill", "target": { "label": "Email" }, "value": "admin@test" },
              { "type": "fill", "target": { "label": "Password" }, "value": "{secret:BROKEN_ADMIN_PASS}" },
              { "type": "click", "target": { "role": "button", "name": "Log in" } },
              { "type": "expectUrl", "url": "/" },
              { "type": "navigate", "path": "/admin" },
              { "type": "expectVisible", "target": { "text": "Users and settings." } }
            ]
          },
          {
            "id": "a-wrong-password-shows-an-error",
            "steps": [
              { "type": "navigate", "path": "/login" },
              { "type": "fill", "target": { "label": "Email" }, "value": "admin@test" },
              { "type": "fill", "target": { "label": "Password" }, "value": "not-it-{run}" },
              { "type": "click", "target": { "role": "button", "name": "Log in" } },
              { "type": "expectError", "text": "Invalid login attempt." }
            ]
          }
        ]
        """;

    [SkippableFact]
    public async Task Each_role_signs_in_afresh_and_a_typed_secret_never_reaches_an_artifact()
    {
        await RequireAsync("node");
        var (report, artifacts) = await ScenariosAsync(App("BrokenApps", "scenarios"), WebScenario.Parse(Scenarios), Secrets);

        Assert.True(report.Outcome == ProbeWebOutcome.Passed, Describe(report));
        Assert.Equal("admin", Check(report, "scenario:admin-opens-admin").Role);
        Assert.Equal(CheckStatus.Passed, Check(report, "scenario:user-is-refused").Status);

        var typed = Check(report, "scenario:signs-in-through-the-form");
        Assert.Equal("fill the field labelled \"Password\" with a secret", typed.Steps![3].Description);
        // README §10: a scenario that types a secret is not traced.
        Assert.Null(typed.Evidence.Trace);
        Assert.NotNull(Check(report, "scenario:a-wrong-password-shows-an-error").Evidence.Trace);

        var texts = ArtifactTexts(artifacts).Append(("report.json", report.ToJson())).ToList();
        foreach (var (file, text) in texts)
            Assert.False(text.Contains("correct-horse", StringComparison.Ordinal), $"{file} carries a secret value.");
    }
}

[CollectionDefinition(Name)]
public sealed class NodeExpressSpaCollection
{
    /// <summary>The tests that start the NodeExpressSpa app: one at a time, since they share its folder's lock.</summary>
    public const string Name = "NodeExpressSpa";
}
