namespace Litos.VsCodeHost.Files;

/// <summary>Local copy of Litos.Api's Files/FilesEndpoints.cs, unchanged.</summary>
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

        return app;
    }
}
