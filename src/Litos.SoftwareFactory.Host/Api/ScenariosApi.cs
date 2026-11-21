using System.Security.Claims;
using System.Text.Json;
using Litos.Probe.Web.Core.Contract;
using Litos.Probe.Web.Core.Reports;
using Litos.Probe.Web.Core.Scenarios;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Core.Verification;
using Litos.SoftwareFactory.Host.Auth;
using Litos.SoftwareFactory.Host.Testability;

namespace Litos.SoftwareFactory.Host.Api;

/// <param name="Json">One scenario in the W3.5 format.</param>
public sealed record ScenarioRequest(string? Json);

/// <summary>
/// A project's browser scenarios (README_Litos.Probe.Web.md W3.6): written and revised on the
/// Testability panel, checked against the adopted contract, approved by anyone on the project
/// (audited, as adopting a contract is), and run after the smoke on every task once approved.
/// Revisions are never edited; saving makes a new draft.
/// </summary>
public static class ScenariosApi
{
    public static IEndpointRouteBuilder MapFactoryScenarios(this IEndpointRouteBuilder app)
    {
        var scenarios = app.MapGroup("/api/projects/{id:guid}/scenarios").RequireAuthorization().RequireProjectAccess(ProjectScoped.Project);

        scenarios.MapGet("", async (Guid id, IFactoryStore store, CancellationToken ct) =>
        {
            if (await store.GetProjectAsync(id, ct) is not { } project)
                return Results.NotFound();
            var contract = Adopted(project);
            var rows = await store.ListScenariosAsync(id, ct);
            return Results.Ok(new
            {
                Adopted = contract is not null,
                Scenarios = rows.GroupBy(r => r.ScenarioId, StringComparer.Ordinal).Select(g => View([.. g], contract)).ToList(),
            });
        });

        // The editor checks as you type: nothing is saved.
        scenarios.MapPost("/validate", async (Guid id, ScenarioRequest request, IFactoryStore store, CancellationToken ct) =>
        {
            if (await store.GetProjectAsync(id, ct) is not { } project)
                return Results.NotFound();
            if (Adopted(project) is not { } contract)
                return NoContract();
            var (scenario, problem) = Read(request.Json);
            return Results.Ok(new { Problems = scenario is null ? [problem!] : ScenarioValidator.Validate(scenario, contract) });
        });

        // Save: a new draft revision. A scenario with problems is saved too, so its author can come
        // back to it; only approving needs it clean.
        scenarios.MapPost("", async (Guid id, ScenarioRequest request, ClaimsPrincipal user, IFactoryStore store, IClock clock, CancellationToken ct) =>
        {
            if (await store.GetProjectAsync(id, ct) is not { } project)
                return Results.NotFound();
            if (Adopted(project) is not { } contract)
                return NoContract();
            var (scenario, problem) = Read(request.Json);
            if (scenario is null)
                return Results.BadRequest(new { error = problem });

            var problems = ScenarioValidator.Validate(scenario, contract);
            if (problems.FirstOrDefault(p => p.StartsWith("id:", StringComparison.Ordinal)) is { } badId)
                return Results.BadRequest(new { error = badId, problems });
            if (scenario.Title?.Length > ProjectScenario.MaxTitleLength || scenario.Criterion?.Length > ProjectScenario.MaxCriterionLength)
                return Results.BadRequest(new { error = $"title: at most {ProjectScenario.MaxTitleLength} characters; criterion: at most {ProjectScenario.MaxCriterionLength}." });

            var row = await store.AddScenarioRevisionAsync(id, scenario.Id, revision => (scenario with { Revision = revision }).ToJson(),
                scenario.Title, scenario.Criterion, user.UserId(), clock.UtcNow, ct);
            await AuditAsync(store, user, AuditActions.ScenarioRevise, project, new { row.ScenarioId, row.Revision }, clock);
            return Results.Ok(new { row.ScenarioId, row.Revision, Status = row.Status.ToString(), Problems = problems });
        });

        scenarios.MapPost("/{scenarioId}/revisions/{revision:int}/approve", async (Guid id, string scenarioId, int revision, ClaimsPrincipal user,
            IFactoryStore store, IClock clock, TestabilityChecks checks, CancellationToken ct) =>
        {
            if (await store.GetProjectAsync(id, ct) is not { } project)
                return Results.NotFound();
            if (Adopted(project) is not { } contract)
                return NoContract();
            if (await RowAsync(store, id, scenarioId, revision, ct) is not { } row)
                return Results.NotFound(new { error = $"Scenario {scenarioId} has no revision {revision}." });
            if (ProjectScenarios.Parse(row) is not { } scenario)
                return Results.BadRequest(new { error = "This revision no longer reads as a scenario: save it again." });
            if (ScenarioValidator.Validate(scenario, contract) is { Count: > 0 } problems)
                return Results.BadRequest(new { error = "Fix its problems first: only a scenario that runs against the adopted contract can be approved.", problems });

            try
            {
                await store.ApproveScenarioAsync(id, scenarioId, revision, user.UserId(), clock.UtcNow, ct);
            }
            catch (StoreConflictException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }

            await AuditAsync(store, user, AuditActions.ScenarioApprove, project, new { ScenarioId = scenarioId, Revision = revision }, clock);
            // Main's baseline now runs it, so a task is not blamed if main already fails it.
            checks.CheckNow(id);
            return Results.Ok(new { ScenarioId = scenarioId, Revision = revision, Status = nameof(ScenarioStatus.Approved) });
        });

        scenarios.MapPost("/{scenarioId}/retire", async (Guid id, string scenarioId, ClaimsPrincipal user, IFactoryStore store, IClock clock,
            TestabilityChecks checks, CancellationToken ct) =>
        {
            if (await store.GetProjectAsync(id, ct) is not { } project)
                return Results.NotFound();
            int retired;
            try
            {
                retired = await store.RetireScenarioAsync(id, scenarioId, user.UserId(), clock.UtcNow, ct);
            }
            catch (StoreNotFoundException)
            {
                return Results.NotFound(new { error = $"The project has no scenario {scenarioId}." });
            }

            if (retired > 0)
            {
                await AuditAsync(store, user, AuditActions.ScenarioRetire, project, new { ScenarioId = scenarioId, Revisions = retired }, clock);
                checks.CheckNow(id);
            }

            return Results.Ok(new { ScenarioId = scenarioId, Retired = retired });
        });

        // "Run on main": one revision, draft or not, on the default branch's head. It builds and runs
        // the project's code, so it is a deliberate press; it takes as long as the app's start.
        scenarios.MapPost("/{scenarioId}/revisions/{revision:int}/run", async (Guid id, string scenarioId, int revision, IFactoryStore store,
            TestabilityChecks checks, CancellationToken ct) =>
        {
            if (await store.GetProjectAsync(id, ct) is not { } project)
                return Results.NotFound();
            if (Adopted(project) is not { } contract)
                return NoContract();
            if (await RowAsync(store, id, scenarioId, revision, ct) is not { } row || ProjectScenarios.Parse(row) is not { } scenario)
                return Results.NotFound(new { error = $"Scenario {scenarioId} has no revision {revision} that reads." });

            var report = await checks.TryScenarioAsync(project, contract, scenario, ct);
            return Results.Ok(RunView(report));
        });

        return app;
    }

    private static WebAppContract? Adopted(Project project) => VerificationProfile.Parse(project.VerificationProfileJson).WebApp();

    private static IResult NoContract() =>
        Results.Conflict(new { error = "Adopt a testability contract first: a scenario signs in and visits pages as the contract describes them." });

    private static (WebScenario? Scenario, string? Problem) Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return (null, "Give the scenario as JSON.");
        try
        {
            return WebScenario.Parse(json) is [var one]
                ? (one, null)
                : (null, "Give one scenario, not a list: each is saved and approved on its own.");
        }
        catch (JsonException ex)
        {
            return (null, $"Not a scenario: {ex.Message}");
        }
    }

    private static async Task<ProjectScenario?> RowAsync(IFactoryStore store, Guid projectId, string scenarioId, int revision, CancellationToken ct) =>
        (await store.ListScenariosAsync(projectId, ct)).FirstOrDefault(r => r.ScenarioId == scenarioId && r.Revision == revision);

    /// <summary>One scenario: its newest revision in full, checked against the adopted contract, and its history.</summary>
    private static object View(IReadOnlyList<ProjectScenario> revisions, WebAppContract? contract)
    {
        var latest = revisions[0];
        var scenario = ProjectScenarios.Parse(latest);
        IReadOnlyList<string> problems = scenario is null ? ["This revision no longer reads as a scenario."]
            : contract is null ? []
            : ScenarioValidator.Validate(scenario, contract);
        return new
        {
            latest.ScenarioId,
            latest.Title,
            latest.Criterion,
            Role = scenario?.Role,
            ApprovedRevision = revisions.FirstOrDefault(r => r.Status == ScenarioStatus.Approved)?.Revision,
            Retired = latest.Status == ScenarioStatus.Retired,
            Latest = new
            {
                latest.Revision,
                Status = latest.Status.ToString(),
                Json = scenario?.ToJson() ?? latest.ScenarioJson,
                latest.CreatedAt,
                latest.CreatedBy,
            },
            Problems = problems,
            Revisions = revisions.Select(r => new { r.Revision, Status = r.Status.ToString(), r.CreatedAt, r.ApprovedAt, r.RetiredAt }),
        };
    }

    /// <summary>What "Run on main" shows: the scenario's steps and screenshots, and why the app did not come up if it did not.</summary>
    private static object RunView(ProbeWebReport report) => new
    {
        Outcome = report.Outcome.ToString(),
        report.Commit,
        report.Usage.WallSeconds,
        report.Findings,
        Checks = report.Checks.Select(c => new
        {
            c.Id,
            Kind = c.Kind.ToString(),
            Status = c.Status.ToString(),
            Classification = c.Classification?.ToString(),
            c.Message,
            Steps = c.Steps?.Select(s => new
            {
                s.Index,
                s.Description,
                Status = s.Status.ToString(),
                s.Message,
                s.DurationMs,
                Screenshot = s.Screenshot is null ? null : TestabilityApi.ArtifactUrl(s.Screenshot),
            }),
            Screenshots = c.Evidence.Screenshots.Select(TestabilityApi.ArtifactUrl),
            c.Evidence.Log,
        }),
    };

    private static Task AuditAsync(IFactoryStore store, ClaimsPrincipal user, string action, Project project, object details, IClock clock) =>
        store.AddAuditAsync(new AuditEvent
        {
            ActorId = user.UserId(),
            Action = action,
            TargetType = AuditTargets.Project,
            TargetId = project.Id,
            ProjectId = project.Id,
            DetailsJson = JsonSerializer.Serialize(details),
            CreatedAt = clock.UtcNow,
        }, CancellationToken.None);
}
