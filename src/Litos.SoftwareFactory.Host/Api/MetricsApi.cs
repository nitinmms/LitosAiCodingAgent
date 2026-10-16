using System.Security.Claims;
using Litos.SoftwareFactory.Core.Metrics;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Host.Auth;

namespace Litos.SoftwareFactory.Host.Api;

/// <summary>
/// The flow metrics (ReadMe_LitosSoftwareFactory_V1.md §12.1, m4-architecture.md §6), for everyone,
/// over the projects they belong to: one project, or all of theirs. Computed on read; nothing is stored.
/// </summary>
public static class MetricsApi
{
    /// <summary>The periods the Metrics tab offers, in days.</summary>
    public static readonly int[] Periods = [7, 30, 90];

    public static IEndpointRouteBuilder MapFactoryMetrics(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/metrics", async (
            Guid? projectId, int? days, ClaimsPrincipal user, IFactoryStore store, ProjectAccess access, IClock clock, CancellationToken ct) =>
        {
            var period = days ?? 30;
            if (!Periods.Contains(period))
                return Results.BadRequest(new { error = $"days must be one of {string.Join(", ", Periods)}." });

            var visible = await access.VisibleProjectsAsync(user, ct);
            IReadOnlyCollection<Guid>? projects = visible;
            if (projectId is { } one)
            {
                // Outside the caller's projects, a project's metrics are not found, as the project itself is not.
                if (!await access.CanSeeProjectAsync(user, one, ct))
                    return Results.NotFound(new { error = "The project does not exist." });
                projects = [one];
            }

            var now = clock.UtcNow;
            var report = FlowMetrics.Compute(await store.GetFlowDataAsync(projects, now.AddDays(-period), ct), now);
            return Results.Ok(new
            {
                ProjectId = projectId,
                Days = period,
                report.Tasks,
                report.Accepted,
                report.Cancelled,
                report.Open,
                report.FirstHandoffAcceptance,
                ReworkRounds = report.ReworkRounds.Select(r => new { r.Rounds, r.Tasks }),
                report.TimeInState,
                report.TimeInStage,
                report.TokensPerTask,
                report.TokensTotal,
                report.TokensByPhase,
                report.TokensByModel,
                report.OpenDecisions,
                report.OldestOpenDecision,
                report.QueueForRepository,
                report.QueueForSlot,
                report.Lessons,
            });
        }).RequireAuthorization();

        return app;
    }
}
