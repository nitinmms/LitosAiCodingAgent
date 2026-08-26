using Litos.Agent.Session;

namespace Litos.VsCodeHost.Files;

/// <summary>Local copy of Litos.Api's Files/FilesEndpoints.cs, plus /sessions/{id}/mentions (no
/// Litos.Api equivalent — that face has no "@"-mention UI to back).</summary>
public static class FilesEndpoints
{
    public static IEndpointRouteBuilder MapFilesEndpoints(this IEndpointRouteBuilder app)
    {
        // Unauthenticated, same as Litos.Api's own — the token itself is the sole credential. This
        // process is loopback-only regardless (Program.cs binds http://127.0.0.1:0), so the
        // exposure surface is already just "processes on this machine", same as every other
        // endpoint this host serves.
        app.MapGet("/files/{token}", async (string token, SharedFileStore store, CancellationToken ct) =>
        {
            var result = await store.TryGetAsync(token, ct);
            if (result is null)
                return Results.NotFound();

            var (meta, filePath) = result.Value;
            return Results.File(filePath, meta.ContentType ?? "application/octet-stream", meta.FileName);
        });

        // Backs webviewContent.ts's live "@"-mention dropdown — mirrors Litos.Gui's
        // FileMentionIndex/MentionPopup, rebuilt fresh per request (see FileMentionIndex's own
        // remarks on why this host doesn't cache the index the way Gui's single-session process
        // does). query is the partial token typed after "@" (possibly empty, for the initial list).
        //
        // fallbackWorkingDirectory is required, not optional: the session's own transcript has no
        // WorkingDirectory until its FIRST turn actually completes (AgentWorker sets it), so a
        // brand-new panel — the single most common time someone reaches for "@" to attach a file
        // in their very first message — always hit this endpoint with a transcript-less session
        // and got an empty list back. extension.ts already resolves the intended directory the
        // same way for /attach mention resolution (getWorkingDirectory, falling back to
        // sharedHost.cwd) and passes it here explicitly rather than this endpoint re-deriving a
        // value that provably isn't there yet.
        app.MapGet("/sessions/{id}/mentions", async (string id, string? query, string fallbackWorkingDirectory, ITranscriptStore store, CancellationToken ct) =>
        {
            var transcript = await Transcript.LoadAsync(store, SessionOwner.Local, id, ct);
            var workingDirectory = transcript.WorkingDirectory ?? fallbackWorkingDirectory;

            var index = FileMentionIndex.Build(workingDirectory);
            var matches = FileMentionIndex.Filter(index, query ?? "");
            return Results.Ok(matches);
        });

        return app;
    }
}
