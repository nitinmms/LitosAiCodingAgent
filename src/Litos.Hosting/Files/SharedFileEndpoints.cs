using Litos.Agent.Tools;

namespace Litos.Hosting.Files;

/// <summary>
/// share_file's registration and its /files/{token} download route, shared by the loopback faces
/// (Litos.VsCodeHost, Litos.Gui). Mirrors Litos.Artifacts' AddLitosArtifacts/MapArtifactEndpoints
/// pair: register the tool before Build(), map the route after.
/// </summary>
public static class SharedFileEndpoints
{
    /// <summary>Registers <see cref="SharedFileStore"/> and <see cref="ShareFileTool"/>.</summary>
    /// <param name="baseUrl">The host's own loopback base URL, read lazily per tool call.</param>
    /// <param name="rootDirectory">Null for ~/.litos/shared-files; tests pass a temp directory.</param>
    public static IServiceCollection AddLitosSharedFiles(this IServiceCollection services, Func<string> baseUrl, string? rootDirectory = null)
    {
        services.AddSingleton(_ => new SharedFileStore(rootDirectory));
        services.AddSingleton<ITool>(sp => new ShareFileTool(sp.GetRequiredService<SharedFileStore>(), baseUrl));
        return services;
    }

    public static IEndpointRouteBuilder MapSharedFileEndpoints(this IEndpointRouteBuilder app)
    {
        // Unauthenticated, same as Litos.Api's own — the token itself is the sole credential. The
        // hosts mapping this are loopback-only, so the exposure surface is already just "processes
        // on this machine", same as every other endpoint they serve.
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
