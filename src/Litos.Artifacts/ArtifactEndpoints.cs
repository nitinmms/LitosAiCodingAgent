namespace Litos.Artifacts;

/// <summary>
/// Serves published artifacts as pages. Unauthenticated, like share_file's /files/{token}: the
/// random artifact id is the credential, and the hosts that map this are loopback-only. Every
/// response carries ArtifactContentPolicy's headers, and the page is sent inline as text/html —
/// never with a download file name, which would make the browser save it instead of showing it.
/// </summary>
public static class ArtifactEndpoints
{
    public static IEndpointRouteBuilder MapArtifactEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/artifacts/{id}", (string id, ArtifactStore store, HttpResponse response, CancellationToken ct) =>
            ServeAsync(store, id, version: null, response, ct));

        app.MapGet("/artifacts/{id}/v/{version:int}", (string id, int version, ArtifactStore store, HttpResponse response, CancellationToken ct) =>
            ServeAsync(store, id, version, response, ct));

        return app;
    }

    private static async Task<IResult> ServeAsync(ArtifactStore store, string id, int? version, HttpResponse response, CancellationToken ct)
    {
        if (await store.TryGetAsync(id, version, ct) is not { } found)
            return Results.NotFound();

        ArtifactContentPolicy.Apply(response);
        return Results.File(found.FilePath, ArtifactContentPolicy.ContentType);
    }
}
