using System.Text.Json;
using Litos.Probe.Web.Cli;
using Litos.Probe.Web.Core.Reports;
using Litos.Probe.Web.Core.Scenarios;

namespace Litos.Probe.Web.Tests;

/// <summary>README_Litos.Probe.Web.md W3.5: the scenario format, its validator and the engine's refusal to start for nothing.</summary>
public class ScenarioTests
{
    private const string Cancel = """
        {
          // Comments and trailing commas are allowed, as in a profile.
          "id": "cancel-upcoming",
          "title": "Cancel an upcoming appointment",
          "revision": 2,
          "role": "patient",
          "criterion": "A patient can cancel an upcoming appointment.",
          "start": "/my/appointments",
          "steps": [
            { "type": "click", "target": { "role": "button", "name": "Cancel", "within": { "role": "row", "name": "Dr Smith 10:00" } } },
            { "type": "expectText", "target": { "testId": "status" }, "text": "Cancelled" },
            { "type": "expectState", "target": { "role": "button", "name": "Cancel", "nth": 0 }, "state": "disabled" },
            { "type": "expectResponse", "method": "POST", "path": "/api/appointments/*", "status": 200 },
          ],
        }
        """;

    [Fact]
    public void A_scenario_reads_with_camel_case_steps_and_writes_back_the_same()
    {
        var scenario = Assert.Single(WebScenario.Parse(Cancel));

        Assert.Equal(("cancel-upcoming", 2, "patient", "/my/appointments"), (scenario.Id, scenario.Revision, scenario.Role, scenario.Start));
        Assert.Equal([StepType.Click, StepType.ExpectText, StepType.ExpectState, StepType.ExpectResponse], scenario.Steps.Select(s => s.Type));
        Assert.Equal("Dr Smith 10:00", scenario.Steps[0].Target!.Within!.Name);
        Assert.Equal(ElementState.Disabled, scenario.Steps[2].State);

        var json = scenario.ToJson();
        Assert.Contains("\"type\": \"expectText\"", json);
        Assert.Contains("\"state\": \"disabled\"", json);
        Assert.Equal(scenario.ToJson(), Assert.Single(WebScenario.Parse(json)).ToJson());
    }

    [Fact]
    public void A_file_may_hold_an_array_of_scenarios() =>
        Assert.Equal(["a", "b"], WebScenario.Parse("""[ { "id": "a", "steps": [] }, { "id": "b", "steps": [] } ]""").Select(s => s.Id));

    [Theory]
    [InlineData("""{ "id": "a", "stpes": [] }""")]
    [InlineData("""{ "id": "a", "steps": [ { "target": { "text": "x" } } ] }""")]
    [InlineData("""{ "id": "a", "steps": [ { "type": "hover" } ] }""")]
    public void A_misspelt_field_a_missing_type_or_an_unknown_step_is_refused(string json) =>
        Assert.ThrowsAny<JsonException>(() => WebScenario.Parse(json));

    private static IReadOnlyList<string> Problems(string json, bool clinic = true) =>
        ScenarioValidator.Validate(Assert.Single(WebScenario.Parse(json)), clinic ? Samples.Clinic() : Samples.Minimal());

    [Fact]
    public void A_good_scenario_has_no_problems() => Assert.Empty(Problems(Cancel));

    [Theory]
    [InlineData("""{ "id": "Bad Id", "steps": [ { "type": "expectUrl", "url": "/" } ] }""", "id: \"Bad Id\" must be lower-case")]
    [InlineData("""{ "id": "a", "role": "nurse", "steps": [ { "type": "expectUrl", "url": "/" } ] }""", "role: \"nurse\" is not one of the contract's roles (admin, patient)")]
    [InlineData("""{ "id": "a", "start": "https://evil.example/", "steps": [ { "type": "expectUrl", "url": "/" } ] }""", "start: a route on the app")]
    [InlineData("""{ "id": "a", "steps": [] }""", "steps: a scenario needs at least one step")]
    [InlineData("""{ "id": "a", "steps": [ { "type": "navigate", "path": "/x" } ] }""", "a scenario must check something")]
    [InlineData("""{ "id": "a", "steps": [ { "type": "navigate", "path": "//evil.example" }, { "type": "expectUrl", "url": "/" } ] }""", "steps[1] (navigate): path: a route on the app")]
    [InlineData("""{ "id": "a", "steps": [ { "type": "click" }, { "type": "expectUrl", "url": "/" } ] }""", "steps[1] (click): target is required")]
    [InlineData("""{ "id": "a", "steps": [ { "type": "click", "target": { "role": "button", "testId": "x" } }, { "type": "expectUrl", "url": "/" } ] }""", "give exactly one of role, label, testId, text or css")]
    [InlineData("""{ "id": "a", "steps": [ { "type": "click", "target": { "role": "buton" } }, { "type": "expectUrl", "url": "/" } ] }""", "target.role: \"buton\" is not an ARIA role")]
    [InlineData("""{ "id": "a", "steps": [ { "type": "click", "target": { "text": "x", "name": "y" } }, { "type": "expectUrl", "url": "/" } ] }""", "target.name goes with role")]
    [InlineData("""{ "id": "a", "steps": [ { "type": "click", "target": { "text": "x", "within": { "css": "tr", "label": "y" } } }, { "type": "expectUrl", "url": "/" } ] }""", "target.within: give exactly one")]
    [InlineData("""{ "id": "a", "steps": [ { "type": "fill", "target": { "label": "Email" } }, { "type": "expectUrl", "url": "/" } ] }""", "steps[1] (fill): value is required")]
    [InlineData("""{ "id": "a", "steps": [ { "type": "fill", "target": { "label": "Email" }, "value": "{port:web}" }, { "type": "expectUrl", "url": "/" } ] }""", "{port:web} is not allowed here")]
    [InlineData("""{ "id": "a", "steps": [ { "type": "click", "target": { "text": "x" }, "value": "y" }, { "type": "expectUrl", "url": "/" } ] }""", "value is only for fill and select")]
    [InlineData("""{ "id": "a", "steps": [ { "type": "expectText", "target": { "text": "x" } } ] }""", "steps[1] (expectText): text is required")]
    [InlineData("""{ "id": "a", "steps": [ { "type": "expectState", "target": { "text": "x" } } ] }""", "state is required")]
    [InlineData("""{ "id": "a", "steps": [ { "type": "expectResponse", "path": "api", "status": 999 } ] }""", "path is required")]
    [InlineData("""{ "id": "a", "steps": [ { "type": "expectResponse", "path": "/api", "status": 999 } ] }""", "status is required")]
    [InlineData("""{ "id": "a", "steps": [ { "type": "expectResponse", "path": "/api", "status": 200, "method": "FETCH" } ] }""", "method: one of GET")]
    [InlineData("""{ "id": "a", "steps": [ { "type": "expectUrl", "url": "/", "timeoutSeconds": 120 } ] }""", "timeoutSeconds: between 1 and 60")]
    [InlineData("""{ "id": "a", "steps": [ { "type": "expectUrl", "url": "/", "target": { "text": "x" } } ] }""", "takes no target")]
    [InlineData("""{ "id": "a", "steps": [ { "type": "screenshot", "name": "Before Save" }, { "type": "expectUrl", "url": "/" } ] }""", "name: lower-case letters")]
    public void A_scenario_that_cannot_run_says_why(string json, string problem) =>
        Assert.Contains(Problems(json), p => p.Contains(problem, StringComparison.Ordinal));

    [Fact]
    public void A_role_needs_a_login() =>
        Assert.Contains(Problems("""{ "id": "a", "role": "admin", "steps": [ { "type": "expectUrl", "url": "/" } ] }""", clinic: false),
            p => p.StartsWith("role: the app has no login", StringComparison.Ordinal));

    [Fact]
    public void Secrets_and_run_tokens_are_fine_in_a_value() =>
        Assert.Empty(Problems("""{ "id": "a", "steps": [ { "type": "fill", "target": { "label": "Password" }, "value": "{secret:TEST_ADMIN_PASS}-{run}" }, { "type": "expectUrl", "url": "/" } ] }"""));

    [Theory]
    [InlineData("""{ "role": "button", "name": "Add note" }""", "button \"Add note\"")]
    [InlineData("""{ "role": "row" }""", "a row")]
    [InlineData("""{ "label": "Email" }""", "the field labelled \"Email\"")]
    [InlineData("""{ "testId": "notes" }""", "[data-testid=notes]")]
    [InlineData("""{ "role": "button", "name": "Cancel", "nth": 1, "within": { "testId": "list" } }""", "button \"Cancel\" (match 2) in [data-testid=list]")]
    public void A_target_reads_as_words(string json, string words) =>
        Assert.Equal(words, JsonSerializer.Deserialize<Target>(json, Core.ProbeWebJson.Options)!.ToString());

    [Fact]
    public async Task Scenarios_that_all_fail_validation_never_start_the_app()
    {
        var scenarios = WebScenario.Parse("""[ { "id": "a", "steps": [] }, { "id": "a", "steps": [ { "type": "expectUrl", "url": "/" } ] } ]""");
        var options = new ProbeWebOptions(new EnvironmentSecretResolver(new Dictionary<string, string>()), new FolderArtifactSink(Path.GetTempPath()),
            new Dictionary<string, string>(), TimeSpan.FromMinutes(1));

        // A folder that does not exist: anything that tried to build or start would fail on it.
        var report = await new ProbeWeb(Samples.Profiles).ScenariosAsync(Path.Combine(Path.GetTempPath(), "no-such-app-" + Guid.NewGuid()), Samples.Minimal(), scenarios, options, CancellationToken.None);

        Assert.Equal((ReportKind.Scenarios, ProbeWebOutcome.Failed), (report.Kind, report.Outcome));
        Assert.Equal(2, report.Checks.Count);
        Assert.All(report.Checks, c => Assert.Equal((CheckKind.Scenario, Classification.TestDefect), (c.Kind, c.Classification!.Value)));
        Assert.Contains("is used by more than one scenario", report.Checks[1].Message);
        Assert.Null(report.App);
    }

    [Fact]
    public void The_cli_takes_files_folders_and_patterns()
    {
        var folder = Directory.CreateTempSubdirectory("probe-scenarios-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, "b.json"), "{}");
            File.WriteAllText(Path.Combine(folder, "a.json"), "{}");
            File.WriteAllText(Path.Combine(folder, "notes.txt"), "");

            Assert.Equal(["a.json", "b.json"], CliCommands.ScenarioFiles(folder).Select(Path.GetFileName));
            Assert.Equal(["a.json", "b.json"], CliCommands.ScenarioFiles(Path.Combine(folder, "*.json")).Select(Path.GetFileName));
            Assert.Equal(["b.json"], CliCommands.ScenarioFiles(Path.Combine(folder, "b.json")).Select(Path.GetFileName));
            Assert.Empty(CliCommands.ScenarioFiles(Path.Combine(folder, "c.json")));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
