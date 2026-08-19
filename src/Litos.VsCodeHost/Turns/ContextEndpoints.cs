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
            var contextLength = worker.ContextLength;
            if (contextLength is null)
                return Results.Ok((object?)null);

            var snapshot = ContextUsage.Compute(transcript, contextLength.Value);
            return Results.Ok(snapshot is null ? null : new
            {
                usedTokens = snapshot.UsedTokens,
                contextLength = snapshot.ContextLength,
                fraction = snapshot.Fraction,
                level = snapshot.Level.ToString(),
            });
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
}
