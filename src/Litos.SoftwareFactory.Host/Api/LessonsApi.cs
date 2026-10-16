using System.Security.Claims;
using System.Text.Json;
using Litos.SoftwareFactory.Core.Lessons;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Host.Auth;
using Litos.SoftwareFactory.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;

namespace Litos.SoftwareFactory.Host.Api;

/// <param name="Source">Manual, HumanCorrection, Decision, ReviewFinding or Evidence; Manual when left out.</param>
/// <param name="Approve">An Admin's Save and approve.</param>
public sealed record LessonRequest(
    string? Rule, IReadOnlyList<string>? Topics, IReadOnlyList<string>? Paths, string? Source, Guid? SourceRef, string? Reason, bool Approve = false);

/// <param name="ExpectedRevision">The revision the caller read; a stale one is a conflict.</param>
public sealed record LessonRevisionRequest(
    int ExpectedRevision, string? Rule, IReadOnlyList<string>? Topics, IReadOnlyList<string>? Paths, string? Source, Guid? SourceRef, string? Reason,
    bool Approve = false);

/// <param name="IdempotencyKey">Accepted for §23's contract. A repeated command is safe without it: approving
/// what is approved, dismissing what is dismissed and so on change nothing.</param>
public sealed record LessonCommandRequest(int ExpectedRevision, string? IdempotencyKey = null);

public sealed record SupersedeLessonRequest(int ExpectedRevision, Guid ReplacementId);

/// <summary>
/// Lessons (ReadMe_LitosSoftwareFactory_V1.md §23, m4-architecture.md §3.3). Everyone on a project
/// sees its lessons and may write and edit suggestions; only an Admin approves, edits an approved
/// lesson, dismisses, disables, re-enables or supersedes one (decided 2026-10-16).
/// </summary>
public static class LessonsApi
{
    public static IEndpointRouteBuilder MapFactoryLessons(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        var projectLessons = api.MapGroup("/projects/{id:guid}/lessons").RequireProjectAccess(ProjectScoped.Project);
        var lessonRoutes = api.MapGroup("/lessons/{id:guid}").RequireProjectAccess(ProjectScoped.Lesson);

        projectLessons.MapGet("", async (Guid id, string? status, IFactoryStore store, UserManager<FactoryUser> users, CancellationToken ct) =>
        {
            LessonStatus? filter = null;
            if (!string.IsNullOrWhiteSpace(status))
            {
                if (!Enum.TryParse<LessonStatus>(status, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
                    return Problem("status must be Suggested, Approved or Dismissed.");
                filter = parsed;
            }

            var lessons = await store.ListLessonsAsync(id, filter, ct);
            var names = Names(users, lessons);
            return Results.Ok(lessons.Select(l => View(l, names)));
        });

        projectLessons.MapPost("", async (
            Guid id, LessonRequest request, ClaimsPrincipal user, IFactoryStore store, UserManager<FactoryUser> users, IClock clock, CancellationToken ct) =>
        {
            if (request.Approve && !ProjectAccess.IsAdmin(user))
                return Forbidden("Only an Admin approves a lesson. Save it as a suggestion instead.");
            if (Draft(request.Rule, request.Topics, request.Paths, request.Source, request.SourceRef, request.Reason, out var draft) is { } refused)
                return Problem(refused);

            return await ActAsync(users, () => store.AddLessonAsync(id, draft, user.UserId(), request.Approve, null, clock.UtcNow, ct), created: true);
        });

        lessonRoutes.MapGet("", async (Guid id, IFactoryStore store, UserManager<FactoryUser> users, CancellationToken ct) =>
            await store.GetLessonAsync(id, ct) is { } lesson ? Results.Ok(View(lesson, Names(users, [lesson]))) : Results.NotFound());

        lessonRoutes.MapPost("/revisions", async (
            Guid id, LessonRevisionRequest request, ClaimsPrincipal user, IFactoryStore store, UserManager<FactoryUser> users, IClock clock, CancellationToken ct) =>
        {
            var admin = ProjectAccess.IsAdmin(user);
            if (request.Approve && !admin)
                return Forbidden("Only an Admin approves a lesson.");
            if (!admin && await store.GetLessonAsync(id, ct) is { Lesson.Status: not LessonStatus.Suggested })
                return Forbidden("Only an Admin edits a lesson that has been approved or dismissed.");
            if (Draft(request.Rule, request.Topics, request.Paths, request.Source, request.SourceRef, request.Reason, out var draft) is { } refused)
                return Problem(refused);

            return await ActAsync(users, () => store.ReviseLessonAsync(id, request.ExpectedRevision, draft, user.UserId(), request.Approve, clock.UtcNow, ct));
        });

        lessonRoutes.MapPost("/approve", (Guid id, LessonCommandRequest request, ClaimsPrincipal user, IFactoryStore store, UserManager<FactoryUser> users, IClock clock, CancellationToken ct) =>
            AdminOnly(user, () => ActAsync(users, () => store.ApproveLessonAsync(id, request.ExpectedRevision, user.UserId(), clock.UtcNow, ct))));

        lessonRoutes.MapPost("/dismiss", (Guid id, LessonCommandRequest request, ClaimsPrincipal user, IFactoryStore store, UserManager<FactoryUser> users, IClock clock, CancellationToken ct) =>
            AdminOnly(user, () => ActAsync(users, () => store.DismissLessonAsync(id, request.ExpectedRevision, user.UserId(), clock.UtcNow, ct))));

        lessonRoutes.MapPost("/disable", (Guid id, LessonCommandRequest request, ClaimsPrincipal user, IFactoryStore store, UserManager<FactoryUser> users, IClock clock, CancellationToken ct) =>
            AdminOnly(user, () => ActAsync(users, () => store.SetLessonEnabledAsync(id, request.ExpectedRevision, false, user.UserId(), clock.UtcNow, ct))));

        lessonRoutes.MapPost("/enable", (Guid id, LessonCommandRequest request, ClaimsPrincipal user, IFactoryStore store, UserManager<FactoryUser> users, IClock clock, CancellationToken ct) =>
            AdminOnly(user, () => ActAsync(users, () => store.SetLessonEnabledAsync(id, request.ExpectedRevision, true, user.UserId(), clock.UtcNow, ct))));

        lessonRoutes.MapPost("/supersede", (Guid id, SupersedeLessonRequest request, ClaimsPrincipal user, IFactoryStore store, UserManager<FactoryUser> users, IClock clock, CancellationToken ct) =>
            AdminOnly(user, () => ActAsync(users, () => store.SupersedeLessonAsync(id, request.ExpectedRevision, request.ReplacementId, user.UserId(), clock.UtcNow, ct))));

        return app;
    }

    /// <summary>The request as a cleaned draft, or why it cannot be one.</summary>
    private static string? Draft(
        string? rule, IReadOnlyList<string>? topics, IReadOnlyList<string>? paths, string? source, Guid? sourceRef, string? reason, out LessonDraft draft)
    {
        draft = null!;
        var kind = LessonSource.Manual;
        if (!string.IsNullOrWhiteSpace(source) && (!Enum.TryParse(source, ignoreCase: true, out kind) || !Enum.IsDefined(kind)))
            return "source must be Manual, HumanCorrection, Decision, ReviewFinding or Evidence.";

        draft = LessonRules.Clean(new LessonDraft(rule ?? "", topics ?? [], paths ?? [], kind, sourceRef, reason ?? ""));
        var errors = LessonRules.Validate(draft);
        return errors.Count == 0 ? null : string.Join(" ", errors);
    }

    private static Task<IResult> AdminOnly(ClaimsPrincipal user, Func<Task<IResult>> action) =>
        ProjectAccess.IsAdmin(user) ? action() : Task.FromResult(Forbidden("Only an Admin can do that to a lesson."));

    private static async Task<IResult> ActAsync(UserManager<FactoryUser> users, Func<Task<LessonDetails>> action, bool created = false)
    {
        try
        {
            var lesson = await action();
            var view = View(lesson, Names(users, [lesson]));
            return created ? Results.Created($"/api/lessons/{lesson.Lesson.Id}", view) : Results.Ok(view);
        }
        catch (StoreNotFoundException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (StoreConflictException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
    }

    private static IResult Problem(string message) => Results.BadRequest(new { error = message });

    private static IResult Forbidden(string message) => Results.Json(new { error = message }, statusCode: StatusCodes.Status403Forbidden);

    /// <summary>The display names of everyone who wrote, created or approved these lessons.</summary>
    private static Dictionary<Guid, string> Names(UserManager<FactoryUser> users, IEnumerable<LessonDetails> lessons)
    {
        var ids = lessons
            .SelectMany(l => new[] { l.Lesson.CreatedBy, l.Current.AuthorId, l.Current.ApprovedBy, l.Approved?.AuthorId, l.Approved?.ApprovedBy })
            .OfType<Guid>().ToHashSet();
        return users.Users.Where(u => ids.Contains(u.Id)).ToList()
            .ToDictionary(u => u.Id, u => string.IsNullOrWhiteSpace(u.DisplayName) ? u.UserName! : u.DisplayName);
    }

    internal static object View(LessonDetails details, IReadOnlyDictionary<Guid, string> names)
    {
        var (lesson, current, approved) = details;
        return new
        {
            lesson.Id,
            lesson.ProjectId,
            lesson.Number,
            Status = lesson.Status.ToString(),
            lesson.Enabled,
            lesson.CurrentRevision,
            lesson.ApprovedRevision,
            // An Admin has an edit to approve; runs keep using the approved revision meanwhile.
            PendingEdit = lesson.HasPendingRevision,
            lesson.SupersededById,
            lesson.ProposedByJobId,
            CreatedBy = Name(names, lesson.CreatedBy),
            lesson.CreatedAt,
            lesson.UpdatedAt,
            Current = RevisionView(current, names),
            Approved = approved is null || approved.Revision == current.Revision ? null : RevisionView(approved, names),
        };
    }

    private static object RevisionView(LessonRevision revision, IReadOnlyDictionary<Guid, string> names) => new
    {
        revision.Revision,
        revision.Rule,
        Topics = JsonSerializer.Deserialize<List<string>>(revision.TopicsJson) ?? [],
        Paths = JsonSerializer.Deserialize<List<string>>(revision.PathsJson) ?? [],
        Source = revision.SourceKind.ToString(),
        revision.SourceRef,
        revision.SourceThreadId,
        revision.Reason,
        // Null for a reflection's proposal: an agent wrote it.
        Author = Name(names, revision.AuthorId),
        revision.CreatedAt,
        ApprovedBy = Name(names, revision.ApprovedBy),
        revision.ApprovedAt,
    };

    private static string? Name(IReadOnlyDictionary<Guid, string> names, Guid? id) => id is { } known ? names.GetValueOrDefault(known) : null;
}
