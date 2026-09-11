using Litos.Agent.Session;
using Litos.Agent.Tools;

namespace Litos.VsCodeHost.Turns;

/// <summary>
/// Backs the VS Code extension's status-bar context meter and its click-through breakdown panel —
/// the same two-tier ContextUsage/ContextBreakdown split Litos.Gui's status bar and "View Context"
/// modal already use (see MainWindow.axaml.cs's RefreshContextUsage/ShowContextBreakdownAsync).
/// Both Litos.Agent.Session types are already face-agnostic; this only wires them onto HTTP.
/// </summary>
public static class ContextEndpoints
{
    public static IEndpointRouteBuilder MapContextEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/sessions/{id}/context/usage", async (
            string id, AgentWorker worker, Litos.Agent.Session.ITranscriptStore store, CancellationToken ct) =>
        {
            var transcript = await Transcript.LoadAsync(store, SessionOwner.Local, id, ct);
            // Resolves on demand rather than only reporting whatever a prior turn already
            // resolved — otherwise the status row reads "Context usage unavailable" until the
            // first turn runs, even on a freshly opened panel where ListModelsAsync already has
            // everything needed to answer (see EnsureModelResolvedAsync's own doc comment).
            await worker.EnsureModelResolvedAsync(ct);
            var contextLength = worker.ContextLength;
            if (contextLength is null)
                return Results.Ok((object?)null);

            var snapshot = ContextUsage.Compute(transcript, contextLength.Value);
            return Results.Ok(ToWireUsage(snapshot, contextLength.Value));
        });

        app.MapGet("/sessions/{id}/working-directory", async (
            string id, Litos.Agent.Session.ITranscriptStore store, CancellationToken ct) =>
        {
            var transcript = await Transcript.LoadAsync(store, SessionOwner.Local, id, ct);
            return Results.Ok(new { workingDirectory = transcript.WorkingDirectory });
        });

        app.MapGet("/sessions/{id}/context/breakdown", async (
            string id, AgentWorker worker, Litos.Agent.Session.ITranscriptStore store,
            ISystemPromptProvider systemPromptProvider, ToolRegistryFactory toolRegistryFactory, CancellationToken ct) =>
        {
            var transcript = await Transcript.LoadAsync(store, SessionOwner.Local, id, ct);
            await worker.EnsureModelResolvedAsync(ct);
            var toolRegistry = toolRegistryFactory.Create();
            var systemPrompt = await systemPromptProvider.BuildAsync(toolRegistry, transcript.WorkingDirectory, ct);
            var snapshot = ContextBreakdown.Compute(transcript, systemPrompt, toolRegistry.Schemas);

            return Results.Ok(new
            {
                totalEstimatedTokens = snapshot.TotalEstimatedTokens,
                lastRealUsageTokens = snapshot.LastRealUsageTokens,
                contextLength = worker.ContextLength,
                entries = snapshot.Entries.Select(e => new
                {
                    category = e.Category.ToString(),
                    label = e.Label,
                    estimatedTokens = e.EstimatedTokens,
                    subItems = e.SubItems?.Select(s => new { label = s.Label, estimatedTokens = s.EstimatedTokens }),
                }),
            });
        });

        return app;
    }

    /// <summary>
    /// Internal (not private) so ContextEndpointsTests can pin this mapping directly rather than
    /// spinning up a real HTTP host just to exercise it. ContextUsage.Compute returns null when
    /// the transcript has no real usage yet (CompactionPlanner.EstimatedTokensUsed needs
    /// transcript.LastUsage, only ever set by a completed turn) — true for a session that's never
    /// sent a message, not just an error state. contextLength is already known by the time this is
    /// called (EnsureModelResolvedAsync has already run), so "0 used" is the accurate answer here,
    /// not "unavailable": a freshly opened panel should show e.g. "0% of context (0 / 22,016
    /// tokens)" rather than a bare placeholder, since nothing about that number is actually
    /// unknown — only the real per-turn usage is, and it's correctly reported as zero.
    /// </summary>
    internal static ContextUsageWire ToWireUsage(ContextUsageSnapshot? snapshot, int contextLength) => new(
        UsedTokens: snapshot?.UsedTokens ?? 0,
        ContextLength: snapshot?.ContextLength ?? contextLength,
        Fraction: snapshot?.Fraction ?? 0,
        Level: (snapshot?.Level ?? ContextUsageLevel.Normal).ToString(),
        IsStale: snapshot?.IsStale ?? false);
}

public sealed record ContextUsageWire(int UsedTokens, int ContextLength, double Fraction, string Level, bool IsStale);
