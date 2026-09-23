using Litos.Agent.Session;

namespace Litos.VsCodeHost.Turns;

/// <summary>
/// Backs the extension's /ptc toggle (Programmatic Tool Calling — the user-facing name for kernel
/// mode, ReadMe_PTCPersistentKernel.md).
///
/// The toggle is per-chat-session and persisted in the transcript as a "kernel_toggle" entry
/// (§5.3), not a process-wide or config-file setting: it is read back by /resume, and a second
/// session does not inherit another's choice. That is why these are /sessions/{id}/... routes
/// rather than something under /settings, where provider/model live.
/// </summary>
public static class KernelEndpoints
{
    public static IEndpointRouteBuilder MapKernelEndpoints(this IEndpointRouteBuilder app)
    {
        // `available` is what lets the webview distinguish "PTC is off" from "PTC cannot be turned
        // on in this build" — the latter happens when Litos.Kernel.Host was not bundled alongside
        // the host binary (see Program.cs's BuildKernelSessionManager). Without it the toggle would
        // silently do nothing when flipped.
        app.MapGet("/sessions/{id}/ptc", async (string id, AgentWorker worker, CancellationToken ct) =>
            Results.Ok(new
            {
                enabled = await worker.GetKernelModeEnabledAsync(SessionOwner.Local, id, ct),
                available = worker.IsKernelModeAvailable,
            }));

        app.MapPost("/sessions/{id}/ptc", async (string id, SetPtcRequest request, AgentWorker worker, CancellationToken ct) =>
        {
            if (request.Enabled && !worker.IsKernelModeAvailable)
                return Results.BadRequest(new { error = "Programmatic Tool Calling is not available in this build — the kernel host binary is missing." });

            await worker.SetKernelModeEnabledAsync(SessionOwner.Local, id, request.Enabled, ct);
            return Results.Ok(new { enabled = request.Enabled, available = worker.IsKernelModeAvailable });
        });

        // Escape hatch for a wedged interpreter, independent of session lifecycle and compaction
        // (§4.4's reset-trigger table). Safe to call when PTC is off or no kernel was ever started:
        // KernelSessionManager.ResetAsync no-ops on an unknown session id.
        app.MapPost("/sessions/{id}/ptc/reset", async (string id, AgentWorker worker, CancellationToken ct) =>
        {
            await worker.ResetKernelAsync(id, ct);
            return Results.Ok(new { reset = true });
        });

        return app;
    }

    public sealed record SetPtcRequest(bool Enabled);
}
