using Litos.Agent.Session;

namespace Litos.VsCodeHost.Files;

/// <summary>/sessions/{id}/mentions (no Litos.Api equivalent — that face has no "@"-mention UI to
/// back). share_file's /files/{token} route lives in Litos.Hosting's SharedFileEndpoints, shared
/// with Litos.Gui.</summary>
public static class FilesEndpoints
{
    public static IEndpointRouteBuilder MapMentionEndpoints(this IEndpointRouteBuilder app)
    {
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
