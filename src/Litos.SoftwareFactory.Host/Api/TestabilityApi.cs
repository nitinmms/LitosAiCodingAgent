using System.Security.Claims;
using Litos.Probe.Web.Core.Contract;
using Litos.SoftwareFactory.Core.Settings;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Core.Verification;
using Litos.SoftwareFactory.Host.Auth;
using Litos.SoftwareFactory.Host.Settings;
using Litos.SoftwareFactory.Host.Testability;

namespace Litos.SoftwareFactory.Host.Api;

/// <summary>
/// A project's testability endpoints (testability-architecture.md §9, README_Litos.Probe.Web.md §11 W2):
/// its role secrets, set and cleared by anyone who can see the project but never read back, and
/// the browser checks' files.
/// </summary>
public static class TestabilityApi
{
    public static IEndpointRouteBuilder MapFactoryTestability(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        var project = api.MapGroup("/projects/{id:guid}").RequireProjectAccess(ProjectScoped.Project);

        // What is set, by name, and what the project's contract still needs. Never a value.
        project.MapGet("/secrets", async (Guid id, IFactoryStore store, FactorySettings settings, CancellationToken ct) =>
        {
            if (await store.GetProjectAsync(id, ct) is not { } found)
                return Results.NotFound();

            var prefix = SecretNames.ProjectPrefix(id);
            var set = settings.Secrets
                .Where(s => s.Name.StartsWith(prefix, StringComparison.Ordinal))
                .Select(s => new { Name = s.Name[prefix.Length..], s.SetAt, s.SetBy })
                .ToList();
            return Results.Ok(new { Secrets = set, Missing = MissingSecrets(found, settings) });
        });

        project.MapPut("/secrets/{name}", async (Guid id, string name, SetSecretRequest request, ClaimsPrincipal user, FactorySettings settings, CancellationToken ct) =>
        {
            if (!SecretNames.IsValidProjectSecretName(name))
                return Results.BadRequest(new { error = $"\"{name}\" is not a secret name: use letters, digits and '_', as in TEST_ADMIN_PASS." });
            if (string.IsNullOrWhiteSpace(request.Value))
                return Results.BadRequest(new { error = "The value is empty. To remove a secret, clear it." });

            // Not trimmed: a password may begin or end with a space on purpose.
            await settings.SetSecretAsync(SecretNames.Project(id, name), request.Value, user.UserId(), ct);
            return Results.NoContent();
        });

        project.MapDelete("/secrets/{name}", async (Guid id, string name, ClaimsPrincipal user, FactorySettings settings, CancellationToken ct) =>
            SecretNames.IsValidProjectSecretName(name) && await settings.ClearSecretAsync(SecretNames.Project(id, name), user.UserId(), ct)
                ? Results.NoContent()
                : Results.NotFound(new { error = "That secret is not set." }));

        // One file from the browser checks. A screenshot is sent as an image so the app can show
        // it; a trace, log or email as bytes to save, never to render.
        api.MapGet("/run-artifacts/{id:guid}", async (Guid id, IFactoryStore store, RunArtifactFiles files, HttpResponse response, CancellationToken ct) =>
        {
            if (await store.GetRunArtifactAsync(id, ct) is not { } artifact || !File.Exists(files.PathOf(artifact)))
                return Results.NotFound();

            response.Headers.XContentTypeOptions = "nosniff";
            response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
            var image = artifact.Kind == RunArtifactKind.Screenshot && artifact.MediaType.StartsWith("image/", StringComparison.Ordinal);
            return Results.File(files.PathOf(artifact), image ? artifact.MediaType : "application/octet-stream",
                image ? null : artifact.FileName, enableRangeProcessing: false);
        }).RequireProjectAccess(ProjectScoped.RunArtifact);

        return app;
    }

    /// <summary>The <c>{secret:}</c> names the project's contract uses that are not set yet.</summary>
    public static IReadOnlyList<string> MissingSecrets(Project project, FactorySettings settings) =>
        VerificationProfile.Parse(project.VerificationProfileJson).WebApp() is { } contract
            ? MissingSecrets(project.Id, contract, settings)
            : [];

    public static IReadOnlyList<string> MissingSecrets(Guid projectId, WebAppContract contract, FactorySettings settings) =>
        [.. ContractValidator.SecretNames(contract).Where(n => !settings.IsSet(SecretNames.Project(projectId, n)))];
}
