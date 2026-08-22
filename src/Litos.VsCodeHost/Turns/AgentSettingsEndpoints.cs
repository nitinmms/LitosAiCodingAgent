using Litos.Agent.Session;

namespace Litos.VsCodeHost.Turns;

/// <summary>
/// /provider and /model parity — Litos.Api's AgentWorker already has this exact surface
/// (SwitchProviderAsync/SetModel/ListModelsAsync/AvailableProviders/ProviderName/Model), but it's
/// only ever driven from the Blazor Settings.razor page over SignalR, never JSON — these are the
/// first JSON endpoints for it in this codebase. Process-wide (not per-session) semantics, matching
/// Litos.Api/Litos.Gui: switching the provider/model affects the *next* turn on any session, not
/// just the one that issued the command.
/// </summary>
public static class AgentSettingsEndpoints
{
    public static IEndpointRouteBuilder MapAgentSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/settings", (AgentWorker worker) => Results.Ok(new
        {
            providerName = worker.ProviderName,
            model = worker.Model,
            availableProviders = worker.AvailableProviders,
            contextLength = worker.ContextLength,
        }));

        app.MapGet("/settings/models", async (string provider, AgentWorker worker, CancellationToken ct) =>
        {
            var models = await worker.ListModelsAsync(provider, ct);
            return Results.Ok(models);
        });

        app.MapPost("/settings/provider", async (SwitchProviderRequest request, AgentWorker worker, CancellationToken ct) =>
        {
            await worker.SwitchProviderAsync(request.Provider, ct);
            return Results.Ok(new { providerName = worker.ProviderName, model = worker.Model });
        });

        app.MapPost("/settings/model", async (SetModelRequest request, AgentWorker worker, CancellationToken ct) =>
        {
            // Looks up the picked model's ContextLength via the same ListModelsAsync the picker
            // itself already called (GET /settings/models) — an extra network round-trip here,
            // but SetModel's signature stays a plain cache write rather than needing its own
            // provider-resolution logic duplicated from SwitchProviderAsync.
            var models = await worker.ListModelsAsync(worker.ProviderName, ct);
            var contextLength = models.FirstOrDefault(m => m.Id == request.Model)?.ContextLength;
            worker.SetModel(request.Model, contextLength);
            return Results.Ok(new { providerName = worker.ProviderName, model = worker.Model });
        });

        return app;
    }
}

public sealed record SwitchProviderRequest(string Provider);

public sealed record SetModelRequest(string Model);

/// <summary>
/// /branch and /compact — ITranscriptStore.BranchAsync and Compactor.ForceCompactAsync both
/// already exist (used by Litos.Gui's own MainWindow.axaml.cs) but neither is reachable over HTTP
/// on any face today, including Litos.Api.
/// </summary>
public static class SessionActionsEndpoints
{
    public static IEndpointRouteBuilder MapSessionActionsEndpoints(this IEndpointRouteBuilder app)
    {
        // Candidate branch points: every User-authored transcript entry that isn't a tool-result
        // echo — same filter as Litos.Gui's MainWindow.axaml.cs FindBranchPoints, moved server-side
        // since the webview has no direct ITranscriptStore access.
        app.MapGet("/sessions/{id}/branch-points", async (string id, ITranscriptStore store, CancellationToken ct) =>
        {
            var points = new List<object>();
            var index = 0;
            await foreach (var entry in store.ReadAsync(SessionOwner.Local, id, ct))
            {
                if (entry.Message is { Role: Litos.Agent.Messages.Role.User } msg &&
                    !msg.Content.OfType<Litos.Agent.Messages.ToolResultBlock>().Any())
                {
                    var text = string.Concat(msg.Content.OfType<Litos.Agent.Messages.TextBlock>().Select(b => b.Text));
                    points.Add(new { entryIndex = index, timestamp = entry.Timestamp, text });
                }
                index++;
            }
            return Results.Ok(points);
        });

        app.MapPost("/sessions/{id}/branch", async (string id, BranchRequest request, ITranscriptStore store, CancellationToken ct) =>
        {
            // +1: BranchAsync's uptoEntryIndex is exclusive ("keep the first N entries"), but
            // branching from a picked message means that message itself stays in the new session
            // — same +1 Litos.Gui's own HandleBranchAsync applies.
            var newSessionId = await store.BranchAsync(SessionOwner.Local, id, request.EntryIndex + 1, ct);
            return Results.Ok(new { newSessionId });
        });

        app.MapPost("/sessions/{id}/compact", async (string id, AgentWorker worker, ITranscriptStore store, Litos.Agent.Session.Compactor compactor, CancellationToken ct) =>
        {
            var transcript = await Transcript.LoadAsync(store, SessionOwner.Local, id, ct);
            var provider = worker.ResolveActiveProvider();
            var compacted = await compactor.ForceCompactAsync(transcript, provider, worker.Model ?? "", ct);
            if (!compacted)
                return Results.Ok(new { compacted = false });

            // JSONL is append-only — the original messages stay on disk; only the synthetic
            // summary (now transcript.Messages[0] after ApplyCompaction) is appended, mirroring
            // both AgentLoop.RunTurnAsync's own automatic-compaction persistence and Litos.Gui's
            // /compact handler exactly. Transcript.LoadAsync treats the summary as a checkpoint on
            // replay, so a resume-after-compact correctly starts from the summary rather than
            // replaying the original messages too — see LoadAsync's remarks.
            await store.AppendAsync(SessionOwner.Local, id, TranscriptEntry.FromMessage(transcript.Messages[0]), ct);
            return Results.Ok(new { compacted = true });
        });

        return app;
    }
}

public sealed record BranchRequest(int EntryIndex);

/// <summary>/reflect — Reflector.ReflectAsync already exists (Litos.Gui's HandleReflectAsync) but
/// is never reachable over HTTP on any face. Unlike Litos.Api (a container, where "the project" is
/// a less meaningful concept), this host runs on the user's real machine/workspace, so writing
/// AGENTS.md to the actual open folder is straightforward and arguably a better fit here than for
/// Litos.Api. The write step is client-driven, not done here — the extension already has direct
/// filesystem access via the VS Code API and a real editor to show the diff in (vscode.diff, see
/// ReadMe_VsCodeExtension.md's design decisions), so this endpoint only produces the proposed text;
/// it never writes to disk itself, unlike Litos.Gui's ReflectWindow which owns both steps.</summary>
public static class ReflectEndpoints
{
    public static IEndpointRouteBuilder MapReflectEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/sessions/{id}/reflect", async (
            string id, ReflectRequest request, AgentWorker worker, ITranscriptStore store, Litos.Agent.Session.Reflector reflector, CancellationToken ct) =>
        {
            var transcript = await Transcript.LoadAsync(store, SessionOwner.Local, id, ct);
            if (transcript.Messages.Count == 0)
                return Results.BadRequest("Nothing to reflect on yet — send a message first.");

            var provider = worker.ResolveActiveProvider();
            var proposed = await reflector.ReflectAsync(transcript.Messages, request.ExistingAgentsMd, provider, worker.Model ?? "", ct);
            return Results.Ok(new { proposed });
        });

        return app;
    }
}

public sealed record ReflectRequest(string? ExistingAgentsMd);
