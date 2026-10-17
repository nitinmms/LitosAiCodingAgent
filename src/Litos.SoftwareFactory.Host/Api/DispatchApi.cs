using System.Security.Claims;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Settings;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Host.Auth;
using Litos.SoftwareFactory.Host.Runs;
using Litos.SoftwareFactory.Host.Settings;

namespace Litos.SoftwareFactory.Host.Api;

/// <param name="Paused">Whether nothing new may start.</param>
/// <param name="Reason">Why, shown to everyone while paused; at most <see cref="DispatchSettings.MaxReasonChars"/>.</param>
public sealed record DispatchRequest(bool Paused, string? Reason);

/// <summary>
/// Pausing dispatch (m4-architecture.md §8.1), for a backup or any other quiet moment. Everyone may
/// see whether it is paused, for the app's banner; only Admins change it and see what is still working.
/// </summary>
public static class DispatchApi
{
    public static IEndpointRouteBuilder MapFactoryDispatch(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/dispatch", (FactorySettings settings) =>
        {
            var dispatch = settings.Dispatch;
            return Results.Ok(new { dispatch.Paused, Reason = dispatch.Paused ? dispatch.Reason : null, Since = dispatch.Paused ? dispatch.ChangedAt : null });
        }).RequireAuthorization();

        var admin = app.MapGroup("/api/admin/dispatch").RequireAuthorization(FactoryRoles.Admin);

        admin.MapGet("", async (FactorySettings settings, IFactoryStore store, RunRegistry registry, CancellationToken ct) =>
            Results.Ok(await ViewAsync(settings.Dispatch, store, registry, ct)));

        admin.MapPost("", async (
            DispatchRequest request, ClaimsPrincipal user, FactorySettings settings, IFactoryStore store, RunRegistry registry,
            FactorySignals signals, IClock clock, CancellationToken ct) =>
        {
            var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
            if (reason?.Length > DispatchSettings.MaxReasonChars)
                return Results.BadRequest(new { error = $"The reason is longer than {DispatchSettings.MaxReasonChars} characters." });

            var (current, revision) = settings.ReadWithRevision<DispatchSettings>(SettingsSections.Dispatch);
            // Asking for what is already so changes nothing, and is not recorded. The caller learns
            // whether it was paused already, so a backup resumes only a pause it made itself.
            if (current.Paused != request.Paused || (request.Paused && current.Reason != reason))
            {
                var next = new DispatchSettings { Paused = request.Paused, Reason = request.Paused ? reason : null, ChangedBy = user.UserId(), ChangedAt = clock.UtcNow };
                try
                {
                    await settings.SaveAsync(SettingsSections.Dispatch, next, revision, user.UserId(), ct);
                }
                catch (StoreConflictException ex)
                {
                    return Results.Conflict(new { error = ex.Message });
                }

                // The coordinator looks again now: to start what waited, or to tell the queue why it waits.
                signals.WorkQueued();
            }

            var view = await ViewAsync(settings.Dispatch, store, registry, ct);
            return Results.Ok(new { WasPaused = current.Paused, view.Paused, view.Reason, view.ChangedBy, view.ChangedAt, view.Active });
        });

        return app;
    }

    /// <summary>
    /// The runs still working: those marked Running, and any this host still holds an executor for
    /// (one tearing down after it stopped). A backup waits until there are none.
    /// </summary>
    private static async Task<DispatchView> ViewAsync(DispatchSettings dispatch, IFactoryStore store, RunRegistry registry, CancellationToken ct)
    {
        var running = await store.ListRunningAsync(ct);
        var active = running.Select(r => new ActiveRun(r.Run.Id, r.Thread.Id, r.Thread.Title, r.Run.Kind.ToString())).ToList();
        active.AddRange(registry.RunIds.Where(id => active.All(a => a.RunId != id)).Select(id => new ActiveRun(id, null, null, null)));
        return new DispatchView(dispatch.Paused, dispatch.Reason, dispatch.ChangedBy, dispatch.ChangedAt, active);
    }

    private sealed record ActiveRun(Guid RunId, Guid? ThreadId, string? Title, string? Kind);

    private sealed record DispatchView(bool Paused, string? Reason, Guid? ChangedBy, DateTimeOffset? ChangedAt, IReadOnlyList<ActiveRun> Active);
}
