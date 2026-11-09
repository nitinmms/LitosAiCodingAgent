using System.Security.Claims;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Queue;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Host.Auth;
using Litos.SoftwareFactory.Host.Runs;

namespace Litos.SoftwareFactory.Host.Api;

/// <param name="Placement">first, after, end or swap. A Member uses swap, with another of their own tasks.</param>
/// <param name="OtherThreadId">The task to go after, or to swap with.</param>
/// <param name="Revision">The task's revision as the person saw it; a different one is a conflict.</param>
public sealed record QueuePositionRequest(string Placement, Guid? OtherThreadId = null, int? Revision = null);

public sealed record AddBlockerRequest(Guid BlockerThreadId);

/// <summary>
/// A project's run order and "Blocked by" links (docs/software-factory/run-order-architecture.md).
/// Seeing them needs the project; who may change them is the store's rule: an Admin arranges any
/// task, anyone else only their own, and a Member moves a task only by swapping it with another of
/// their own.
/// </summary>
public static class RunOrderApi
{
    public static IEndpointRouteBuilder MapFactoryRunOrder(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        var projectRoutes = api.MapGroup("/projects/{id:guid}").RequireProjectAccess(ProjectScoped.Project);
        var threadRoutes = api.MapGroup("/threads/{id:guid}").RequireProjectAccess(ProjectScoped.Thread);

        projectRoutes.MapGet("/run-order", async (Guid id, IFactoryStore store, CancellationToken ct) =>
            Results.Ok(RunOrderView(await store.GetRunOrderAsync(id, ct))));

        threadRoutes.MapGet("/links", async (Guid id, IFactoryStore store, CancellationToken ct) =>
        {
            var links = await store.GetThreadLinksAsync(id, ct);
            return Results.Ok(new { WaitsFor = links.WaitsFor.Select(LinkView), WaitedOnBy = links.WaitedOnBy.Select(LinkView) });
        });

        threadRoutes.MapPost("/queue-position", (
            Guid id, QueuePositionRequest request, ClaimsPrincipal user, IFactoryStore store, FactorySignals signals, IClock clock, CancellationToken ct) =>
        {
            if (ParsePlacement(request, out var refused) is not { } placement)
                return Task.FromResult(Problem(refused!));
            return ChangeAsync(signals, () => store.MoveInQueueAsync(id, user.UserId(), ProjectAccess.IsAdmin(user), placement, request.Revision, clock.UtcNow, ct));
        });

        threadRoutes.MapPost("/blockers", (
            Guid id, AddBlockerRequest request, ClaimsPrincipal user, IFactoryStore store, FactorySignals signals, IClock clock, CancellationToken ct) =>
            ChangeAsync(signals, () => store.AddBlockerAsync(id, request.BlockerThreadId, user.UserId(), ProjectAccess.IsAdmin(user), clock.UtcNow, ct)));

        // "Release now".
        threadRoutes.MapDelete("/blockers/{blockerId:guid}", (
            Guid id, Guid blockerId, ClaimsPrincipal user, IFactoryStore store, FactorySignals signals, IClock clock, CancellationToken ct) =>
            ChangeAsync(signals, () => store.ReleaseBlockerAsync(id, blockerId, user.UserId(), ProjectAccess.IsAdmin(user), clock.UtcNow, ct)));

        // "Mark merged", for work the factory cannot see merged. Anyone on the project may say it; it is audited.
        threadRoutes.MapPost("/merged", (Guid id, ClaimsPrincipal user, IFactoryStore store, FactorySignals signals, IClock clock, CancellationToken ct) =>
            ChangeAsync(signals, () => store.MarkMergedAsync(id, user.UserId(), clock.UtcNow, ct)));

        return app;
    }

    /// <summary>A change to the order or the links: the coordinator looks again, since a task may now start.</summary>
    private static async Task<IResult> ChangeAsync(FactorySignals signals, Func<Task<TaskThread>> change)
    {
        try
        {
            var thread = await change();
            signals.EventsWritten();
            signals.WorkQueued();
            return Results.Ok(FactoryApi.ThreadView(thread));
        }
        catch (StoreNotFoundException)
        {
            return Results.NotFound();
        }
        catch (StoreConflictException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
        catch (StoreForbiddenException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
        }
    }

    internal static Placement? ParsePlacement(QueuePositionRequest request, out string? refused)
    {
        refused = null;
        switch (request.Placement?.Trim().ToLowerInvariant())
        {
            case "first":
                return Placement.First;
            case "end":
                return Placement.End;
            case "after" when request.OtherThreadId is { } after:
                return Placement.After(after);
            case "swap" when request.OtherThreadId is { } other:
                return Placement.SwapWith(other);
            case "after" or "swap":
                refused = "otherThreadId is required to place a task after another, or to swap it with another.";
                return null;
            default:
                refused = "placement must be one of: first, after, end, swap.";
                return null;
        }
    }

    private static IResult Problem(string message) => Results.BadRequest(new { error = message });

    internal static object RunOrderView(ProjectRunOrder order) => new
    {
        Holder = order.Holder is null ? null : FactoryApi.ThreadView(order.Holder),
        Queue = order.Queue.Select(e => new
        {
            Thread = FactoryApi.ThreadView(e.Thread),
            WaitsFor = e.WaitsFor.Select(LinkView),
            e.WaitedOnBy,
            e.IsRework,
        }),
    };

    /// <summary>A link, seen from the task at one end: the task at the other end, and where its merge stands.</summary>
    internal static object LinkView(BlockerLink link) => new
    {
        ThreadId = link.Other.Id,
        link.Other.Title,
        link.Other.OwnerId,
        State = link.Other.State.ToString(),
        Turn = TurnLabels.For(link.Other.State, link.Other.Stage, link.Other.BlockerNeedsYou).ToString(),
        link.Other.PullRequestNumber,
        link.Other.PullRequestUrl,
        link.Other.PullRequestMergedAt,
        link.Other.PullRequestClosedAt,
        Since = link.Link.CreatedAt,
        link.Link.ReleasedAt,
        ReleaseReason = link.Link.ReleaseReason?.ToString(),
    };
}
