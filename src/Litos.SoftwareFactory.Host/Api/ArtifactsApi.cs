using System.Text;
using System.Text.Json;
using Litos.Artifacts;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Host.Auth;
using Litos.SoftwareFactory.Host.Runs;

namespace Litos.SoftwareFactory.Host.Api;

/// <summary>
/// Pages a chat answer publishes on its thread (ReadMe_ArtifactsFeature.md §11): the worker's
/// callbacks that publish, list and read them, and the browser route that serves one.
///
/// A page is agent-authored, and the agent reads untrusted repositories, so it is treated as
/// hostile. It is served on the factory's own origin, beside the API and its session cookie, with
/// Litos.Artifacts' policy: the CSP <c>sandbox</c> gives the page an opaque origin, so anything it
/// sends is cross-site — the SameSite=Strict cookie stays home — and <c>connect-src 'none'</c>
/// stops it sending at all.
/// </summary>
public static class ArtifactsApi
{
    /// <summary>The most one version may be, as UTF-8.</summary>
    public const int MaxPageBytes = 1024 * 1024;

    /// <summary>The most all of a thread's versions together may be.</summary>
    public const long MaxBytesPerThread = 20L * 1024 * 1024;

    /// <summary>How much of a page a read returns to the model.</summary>
    public const int MaxReadChars = 100_000;

    public static IEndpointRouteBuilder MapFactoryArtifacts(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();

        api.MapGet("/artifacts/{id:guid}", (Guid id, IFactoryStore store, HttpResponse response, CancellationToken ct) =>
            ServeAsync(store, id, version: null, response, ct)).RequireProjectAccess(ProjectScoped.Artifact);

        api.MapGet("/artifacts/{id:guid}/v/{version:int}", (Guid id, int version, IFactoryStore store, HttpResponse response, CancellationToken ct) =>
            ServeAsync(store, id, version, response, ct)).RequireProjectAccess(ProjectScoped.Artifact);

        // The worker's side: a chat run's own thread only (§11.4), authenticated like every callback.
        var runs = app.MapGroup("/internal/runs/{runId}/artifacts").AllowAnonymous();

        runs.MapPost("", async (string runId, HttpRequest request, RunRegistry registry, IFactoryStore store, IClock clock, CancellationToken ct) =>
        {
            if (ChatRun(runId, request, registry) is not { } active)
                return Results.Unauthorized();

            ArtifactPublishRequest? body;
            try
            {
                body = await request.ReadFromJsonAsync<ArtifactPublishRequest>(FactoryWire.Json, ct);
            }
            catch (JsonException)
            {
                body = null;
            }

            if (body is null || string.IsNullOrWhiteSpace(body.Html))
                return Json(ArtifactPublishResponse.Refused("The 'html' argument is required: the whole page, as one HTML document."));
            var size = Encoding.UTF8.GetByteCount(body.Html);
            if (size > MaxPageBytes)
                return Json(ArtifactPublishResponse.Refused(
                    $"The page is {size / 1024} KB, over the {MaxPageBytes / 1024} KB limit for one page. Make it smaller: less embedded data, no inlined images."));

            var title = ArtifactTitles.Clean(body.Title) ?? ArtifactTitles.FromHtml(body.Html) ?? "Untitled page";
            try
            {
                var (artifact, created) = await store.PublishArtifactAsync(
                    active.RunId, body.ArtifactId, title, body.Html, MaxBytesPerThread, clock.UtcNow, ct);
                return Json(new ArtifactPublishResponse(true, null, Summary(artifact), created));
            }
            catch (StoreNotFoundException)
            {
                return Json(ArtifactPublishResponse.Refused(
                    $"There is no artifact with id {body.ArtifactId} on this thread. Leave id out to publish a new one, or call list."));
            }
            catch (StoreConflictException ex)
            {
                return Json(ArtifactPublishResponse.Refused(ex.Message));
            }
        });

        runs.MapGet("", async (string runId, HttpRequest request, RunRegistry registry, IFactoryStore store, CancellationToken ct) =>
            ChatRun(runId, request, registry) is { } active
                ? Results.Json((await store.ListArtifactsAsync(active.ThreadId, ct)).Select(Summary).ToList(), FactoryWire.Json)
                : Results.Unauthorized());

        runs.MapGet("/{artifactId:guid}", async (
            string runId, Guid artifactId, int? version, HttpRequest request, RunRegistry registry, IFactoryStore store, CancellationToken ct) =>
        {
            if (ChatRun(runId, request, registry) is not { } active)
                return Results.Unauthorized();

            // Another thread's artifact reads as missing, like one that never existed.
            if (await store.GetArtifactAsync(artifactId, ct) is not { } artifact || artifact.ThreadId != active.ThreadId)
                return Results.Json(new ArtifactReadResponse(null, version ?? 0, null), FactoryWire.Json);

            var found = await store.GetArtifactVersionAsync(artifactId, version, ct);
            var html = found?.Html is { } text && text.Length > MaxReadChars
                ? text[..MaxReadChars] + $"\n[truncated: showing {MaxReadChars} of {text.Length} characters]"
                : found?.Html;
            return Results.Json(new ArtifactReadResponse(Summary(artifact), found?.Version ?? version ?? artifact.CurrentVersion, html), FactoryWire.Json);
        });

        return app;
    }

    /// <summary>
    /// The pages a message published, for the thread view: each at the version that message
    /// published, beside the newest, so an earlier answer's card can say a newer one exists.
    /// </summary>
    public static IReadOnlyList<object> LinksFor(string? payloadJson, IReadOnlyDictionary<Guid, ThreadArtifact> artifacts)
    {
        if (string.IsNullOrEmpty(payloadJson) || !payloadJson.Contains("\"artifacts\"", StringComparison.Ordinal))
            return [];

        MessageArtifacts? published;
        try
        {
            published = JsonSerializer.Deserialize<MessageArtifacts>(payloadJson, FactoryWire.Json);
        }
        catch (JsonException)
        {
            return [];
        }

        return [.. (published?.Artifacts ?? [])
            .Where(p => artifacts.ContainsKey(p.Id))
            .Select(p => (object)new
            {
                p.Id,
                artifacts[p.Id].Title,
                p.Version,
                LatestVersion = artifacts[p.Id].CurrentVersion,
                Url = $"/api/artifacts/{p.Id}/v/{p.Version}",
                LatestUrl = $"/api/artifacts/{p.Id}",
            })];
    }

    private static async Task<IResult> ServeAsync(IFactoryStore store, Guid id, int? version, HttpResponse response, CancellationToken ct)
    {
        if (await store.GetArtifactVersionAsync(id, version, ct) is not { } found)
            return Results.NotFound();

        ArtifactContentPolicy.Apply(response);
        return Results.Content(found.Html, ArtifactContentPolicy.ContentType);
    }

    /// <summary>The run, when the request carries its secret and it is a chat run: only a chat answer publishes pages.</summary>
    private static ActiveRun? ChatRun(string runId, HttpRequest request, RunRegistry registry) =>
        WorkerCallbacks.Authenticate(runId, request, registry) is { IsChat: true } active ? active : null;

    private static ArtifactSummary Summary(ThreadArtifact a) => new(a.Id, a.Title, a.CurrentVersion);

    private static IResult Json(ArtifactPublishResponse response) => Results.Json(response, FactoryWire.Json);
}
