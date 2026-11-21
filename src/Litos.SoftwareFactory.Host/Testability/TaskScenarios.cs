using System.Text.Json;
using Litos.Probe.Web.Core.Contract;
using Litos.Probe.Web.Core.Scenarios;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Briefs;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Core.Testability;
using Litos.SoftwareFactory.Core.Verification;

namespace Litos.SoftwareFactory.Host.Testability;

/// <summary>
/// A task's own browser scenarios (README_Litos.Probe.Web.md W3.7): its approved specification's,
/// which run like a project's own, and the ones its work turn drafted, which run for evidence only.
/// </summary>
public sealed record TaskScenarios(IReadOnlyList<WebScenario> Spec, IReadOnlyList<WebScenario> Drafted)
{
    public static TaskScenarios None { get; } = new([], []);

    /// <summary>From the stored JSON objects; one that no longer reads is left out.</summary>
    public static TaskScenarios From(IReadOnlyList<string>? spec, IReadOnlyList<string>? drafted) => new(Read(spec), Read(drafted));

    public static IReadOnlyList<WebScenario> Read(IReadOnlyList<string>? json) =>
        [.. (json ?? []).Select(j =>
        {
            try
            {
                return WebScenario.Parse(j) is [var one] ? one : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }).OfType<WebScenario>()];

    /// <summary>A JSON array of scenario objects, as a specification stores them, as one string per scenario.</summary>
    public static IReadOnlyList<string> Split(string? arrayJson)
    {
        if (string.IsNullOrWhiteSpace(arrayJson))
            return [];
        try
        {
            using var document = JsonDocument.Parse(arrayJson);
            return document.RootElement.ValueKind == JsonValueKind.Array
                ? [.. document.RootElement.EnumerateArray().Select(e => e.GetRawText())]
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// What a brief says about scenarios when the project has an adopted contract: its roles and
    /// routes, and the approved specification's scenarios in words, if the task builds them.
    /// </summary>
    public static ScenarioGuide? Guide(VerificationProfile profile, IReadOnlyList<WebScenario> approved)
    {
        if (profile.WebApp() is not { } contract)
            return null;
        var auth = contract.Testability.Auth;
        var roles = auth.Type == AuthType.Form ? auth.Roles.Keys.ToList() : [];
        var routes = contract.Testability.Routes;
        var known = routes.Protected.Values.SelectMany(r => r).Concat(routes.Public).Distinct(StringComparer.Ordinal).ToList();
        return new ScenarioGuide(roles, known)
        {
            Approved = [.. approved.Select(s => $"{s.Id}: {ScenarioText.Summarise(s)}")],
        };
    }
}

/// <summary>
/// The host's check of the scenarios a spec or work turn submits (W3.7): only the host knows the
/// contract's roles and the project's scenarios, so a scenario that cannot run is refused here and
/// the agent fixes it at once, rather than a person finding a TestDefect after the handoff.
/// </summary>
public sealed class ScenarioSubmissionCheck(IFactoryStore store)
{
    public async Task<string?> ProblemAsync(Guid threadId, Submission submission, CancellationToken ct)
    {
        var (scenarios, criteria, tool) = submission switch
        {
            WorkSubmission { Scenarios.Count: > 0 } w => (w.Scenarios, (IReadOnlyList<string>)[.. w.Criteria.Select(c => c.Criterion)], "submit_work"),
            SpecSubmission { Scenarios.Count: > 0 } s => (s.Scenarios, s.AcceptanceCriteria, "submit_spec"),
            _ => (null, (IReadOnlyList<string>)[], ""),
        };
        if (scenarios is null)
            return null;

        if (await store.GetThreadAsync(threadId, ct) is not { } details || await store.GetProjectAsync(details.Thread.ProjectId, ct) is not { } project)
            return null;
        if (VerificationProfile.Parse(project.VerificationProfileJson).WebApp() is not { } contract)
            return "This project is not tested in a browser (no contract is adopted): leave 'scenarios' out and call " + tool + " again.";

        var projectIds = (await store.ApprovedScenariosAsync(project.Id, ct)).Select(s => s.ScenarioId).ToHashSet(StringComparer.Ordinal);
        var wanted = criteria.Select(Normal).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var problems = new List<string>();
        for (var i = 0; i < scenarios.Count; i++)
        {
            WebScenario scenario;
            try
            {
                if (WebScenario.Parse(scenarios[i]) is not [var one])
                {
                    problems.Add($"scenarios[{i + 1}]: one scenario object, not a list.");
                    continue;
                }

                scenario = one;
            }
            catch (JsonException ex)
            {
                problems.Add($"scenarios[{i + 1}]: not a scenario: {ex.Message}");
                continue;
            }

            var where = $"scenarios[{i + 1}] ({scenario.Id})";
            problems.AddRange(ScenarioValidator.Validate(scenario, contract).Select(p => $"{where}: {p}"));
            if (!seen.Add(scenario.Id))
                problems.Add($"{where}: id: used by another scenario of this task.");
            if (projectIds.Contains(scenario.Id))
                problems.Add($"{where}: id: \"{scenario.Id}\" is already one of the project's approved scenarios: choose another.");
            if (string.IsNullOrWhiteSpace(scenario.Criterion))
                problems.Add($"{where}: criterion: copy the acceptance criterion it checks.");
            else if (wanted.Count > 0 && !wanted.Contains(Normal(scenario.Criterion)))
                problems.Add($"{where}: criterion: \"{scenario.Criterion}\" is not one of this task's acceptance criteria: copy one exactly.");
        }

        return problems.Count == 0
            ? null
            : $"Some scenarios cannot run as written. Fix them and call {tool} again with all of its arguments:\n" + string.Join("\n", problems.Select(p => "- " + p));
    }

    /// <summary>A criterion as matched: case, spacing and a closing full stop are not held against it.</summary>
    internal static string Normal(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.').ToLowerInvariant();
}

/// <summary>
/// On Accept (W3.7, decided 2026-11-21): each of the task's own scenarios that passed in its last
/// browser checks becomes a draft revision of a project scenario, for a person to approve on the
/// Testability panel. The regression set grows from real tasks, but only with review.
/// </summary>
public sealed class ScenarioPromotion(IFactoryStore store, IClock clock)
{
    public async Task<IReadOnlyList<string>> PromoteAsync(Guid threadId, Guid userId, CancellationToken ct)
    {
        if (await store.GetThreadAsync(threadId, ct) is not { LatestHandoff: { } handoff } details
            || await store.GetProjectAsync(details.Thread.ProjectId, ct) is not { } project
            || VerificationProfile.Parse(project.VerificationProfileJson).WebApp() is null)
        {
            return [];
        }

        var evidence = JsonSerializer.Deserialize<Core.Orchestration.HandoffEvidence>(handoff.EvidenceJson, FactoryWire.Json);
        if (evidence?.UiTesting is not { TaskScenarios.Count: > 0 } ui)
            return [];

        var passed = ui.Checks.Where(c => c.Kind == "Scenario" && c.Status == "Passed").Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var copied = new List<string>();
        foreach (var scenario in TaskScenarios.Read([.. ui.TaskScenarios.Select(t => t.Json)]))
        {
            if (!passed.Contains($"scenario:{scenario.Id}"))
                continue;
            await store.AddScenarioRevisionAsync(project.Id, scenario.Id, revision => (scenario with { Revision = revision }).ToJson(),
                scenario.Title, scenario.Criterion, userId, clock.UtcNow, ct);
            copied.Add(scenario.Id);
        }

        if (copied.Count > 0)
        {
            await store.AddAuditAsync(new AuditEvent
            {
                ActorId = userId,
                Action = AuditActions.ScenarioRevise,
                TargetType = AuditTargets.Project,
                TargetId = project.Id,
                ProjectId = project.Id,
                DetailsJson = JsonSerializer.Serialize(new { FromThread = threadId, Scenarios = copied }),
                CreatedAt = clock.UtcNow,
            }, CancellationToken.None);
            await store.AddFactoryMessageAsync(threadId, MessageKind.Status,
                $"Copied {(copied.Count == 1 ? "1 browser scenario" : $"{copied.Count} browser scenarios")} that passed into the project's scenarios as drafts: "
                + $"{string.Join(", ", copied)}. Approve the ones worth running on every task on the project's Testability panel.",
                null, clock.UtcNow, CancellationToken.None);
        }

        return copied;
    }
}
