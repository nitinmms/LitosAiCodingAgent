namespace Litos.Api.Files;

public static class FilesEndpoints
{
    public static IEndpointRouteBuilder MapFilesEndpoints(this IEndpointRouteBuilder app)
    {
        // Deliberately unauthenticated — the token itself is the sole credential, the same
        // "possession of an opaque string" model TelegramPairing's codes use, and the one other
        // exception to this API's blanket AdminOrUser policy besides app.UseStaticFiles().
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
