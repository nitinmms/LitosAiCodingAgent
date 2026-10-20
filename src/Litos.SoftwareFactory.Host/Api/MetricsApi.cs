using System.Security.Claims;
using Litos.SoftwareFactory.Core.Metrics;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Core.Settings;
using Litos.SoftwareFactory.Host.Auth;
using Litos.SoftwareFactory.Host.Settings;

namespace Litos.SoftwareFactory.Host.Api;

/// <summary>
/// The flow metrics (ReadMe_LitosSoftwareFactory_V1.md §12.1, m4-architecture.md §6) and the lesson
/// report (§12 there), for everyone, over the projects they belong to: one project, or all of theirs.
/// Computed on read; nothing is stored.
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
            var (scope, refused) = await ScopeAsync(projectId, days, user, access, ct);
            if (refused is not null)
                return refused;

            var now = clock.UtcNow;
            var report = FlowMetrics.Compute(await store.GetFlowDataAsync(scope!.Projects, now.AddDays(-scope.Days), ct), now, scope.Days);
            return Results.Ok(new
            {
                ProjectId = projectId,
                scope.Days,
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
                report.PerTask,
                report.Series,
            });
        }).RequireAuthorization();

        app.MapGet("/api/metrics/lessons", async (
            Guid? projectId, int? days, ClaimsPrincipal user, IFactoryStore store, ProjectAccess access, IClock clock, CancellationToken ct) =>
        {
            var (scope, refused) = await ScopeAsync(projectId, days, user, access, ct);
            if (refused is not null)
                return refused;

            var now = clock.UtcNow;
            var report = LessonMetrics.Compute(await store.GetLessonMetricsDataAsync(scope!.Projects, now.AddDays(-scope.Days), ct), now, scope.Days);
            return Results.Ok(new
            {
                ProjectId = projectId,
                scope.Days,
                report.Given,
                report.Judged,
                report.Helped,
                report.Misled,
                report.LeftOut,
                // Given fewer times than this, a helpful lesson counts as rarely matched; the lesson map draws the line.
                RarelyBelow = LessonMetrics.RarelyBelow(scope.Days),
                Lessons = report.Lessons.Select(l => new
                {
                    l.LessonId,
                    l.ProjectId,
                    l.Number,
                    l.Rule,
                    Source = l.Source.ToString(),
                    l.Revision,
                    l.Usable,
                    l.Given,
                    l.Manual,
                    l.LeftOut,
                    l.Helped,
                    l.DidNotMatter,
                    l.Misled,
                    l.Unjudged,
                    l.Net,
                    l.FirstHandoff,
                    l.LastGiven,
                    Standing = l.Standing.ToString(),
                }),
                report.ByRank,
                report.Proposals,
                BySource = report.BySource.Select(s => new { Source = s.Source.ToString(), s.Lessons, s.Judged, s.Helped }),
                Improvements = report.Improvements.Select(i => new { Severity = i.Severity.ToString(), i.Subject, i.LessonId, i.ProjectId, i.Rule, i.Problem, i.Action }),
            });
        }).RequireAuthorization();

        app.MapGet("/api/metrics/skills", async (
            Guid? projectId, int? days, ClaimsPrincipal user, IFactoryStore store, ProjectAccess access, FactorySettings settings, IClock clock,
            CancellationToken ct) =>
        {
            var (scope, refused) = await ScopeAsync(projectId, days, user, access, ct);
            if (refused is not null)
                return refused;

            var now = clock.UtcNow;
            var library = settings.ReadWithRevision<SkillSettings>(SettingsSections.Skills).Settings.Library.ToDictionary(s => s.Name, s => s.Body.Length);
            var data = await store.GetSkillMetricsDataAsync(scope!.Projects, now.AddDays(-scope.Days), ct);
            var report = SkillMetrics.Compute(data with { LibraryChars = library }, now, scope.Days);
            return Results.Ok(new
            {
                ProjectId = projectId,
                scope.Days,
                report.Runs,
                report.RunsWithSkills,
                report.Loads,
                MinTasksToCompare = SkillMetrics.MinTasksToCompare,
                Skills = report.Skills.Select(s => new
                {
                    s.Name,
                    s.Origin,
                    s.RunsChosen,
                    s.RunsOffered,
                    s.RunsLoaded,
                    s.Loads,
                    s.TokensPerRun,
                    s.FirstHandoff,
                    Standing = s.Standing.ToString(),
                    s.ChangedSinceApproval,
                }),
                Improvements = report.Improvements.Select(i => new { Severity = i.Severity.ToString(), i.Subject, i.Problem, i.Action }),
            });
        }).RequireAuthorization();

        return app;
    }

    /// <param name="Projects">Null for every project (an Admin asking for all).</param>
    private sealed record Scope(IReadOnlyCollection<Guid>? Projects, int Days);

    /// <summary>The period, and the projects to read: the one asked for, or every project the caller belongs to.</summary>
    private static async Task<(Scope? Scope, IResult? Refused)> ScopeAsync(
        Guid? projectId, int? days, ClaimsPrincipal user, ProjectAccess access, CancellationToken ct)
    {
        var period = days ?? 30;
        if (!Periods.Contains(period))
            return (null, Results.BadRequest(new { error = $"days must be one of {string.Join(", ", Periods)}." }));

        if (projectId is { } one)
        {
            // Outside the caller's projects, a project's metrics are not found, as the project itself is not.
            return await access.CanSeeProjectAsync(user, one, ct)
                ? (new Scope([one], period), null)
                : (null, Results.NotFound(new { error = "The project does not exist." }));
        }

        return (new Scope(await access.VisibleProjectsAsync(user, ct), period), null);
    }
}
