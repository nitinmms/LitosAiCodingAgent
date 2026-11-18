using System.Security.Claims;
using Litos.Probe.Web.Core.Contract;
using Litos.SoftwareFactory.Core.Settings;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Core.Testability;
using Litos.SoftwareFactory.Core.Verification;
using Litos.SoftwareFactory.Host.Auth;
using Litos.SoftwareFactory.Host.Runs;
using Litos.SoftwareFactory.Host.Settings;
using Litos.SoftwareFactory.Host.Testability;

namespace Litos.SoftwareFactory.Host.Api;

/// <summary>
/// A project's testability endpoints (testability-architecture.md §9, README_Litos.Probe.Web.md §11 W2):
/// its role secrets, set and cleared by anyone who can see the project but never read back, and
/// the browser checks' files.
/// </summary>
/// <param name="Commit">The default branch's head the reviewed preview was read at.</param>
public sealed record AdoptRequest(string? Commit);

public static class TestabilityApi
{
    public static IEndpointRouteBuilder MapFactoryTestability(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        var project = api.MapGroup("/projects/{id:guid}").RequireProjectAccess(ProjectScoped.Project);

        // Where the project stands: the latest check, or that one is under way.
        project.MapGet("/testability", async (Guid id, IFactoryStore store, FactorySettings settings, TestabilityChecks checks, CancellationToken ct) =>
            await store.GetProjectAsync(id, ct) is { } found
                ? Results.Ok(View(found, await store.LatestTestabilityCheckAsync(id, ct), checks.IsChecking(id), settings, detailed: true))
                : Results.NotFound());

        // "Check again": it runs the project's code (a dry run), so it is a deliberate press, not a refresh.
        project.MapPost("/testability/check", async (Guid id, IFactoryStore store, TestabilityChecks checks, CancellationToken ct) =>
        {
            if (await store.GetProjectAsync(id, ct) is null)
                return Results.NotFound();
            checks.CheckNow(id);
            return Results.Accepted($"/api/projects/{id}/testability");
        });

        // What adopting the default branch's litos.test.yaml would change, before anyone does it.
        project.MapGet("/testability/adopt", async (Guid id, IFactoryStore store, ContractAdoption adoption, CancellationToken ct) =>
        {
            if (await store.GetProjectAsync(id, ct) is not { } found)
                return Results.NotFound();
            var preview = await adoption.PreviewAsync(found, ct);
            return Results.Ok(new
            {
                preview.Commit,
                preview.Found,
                preview.Problems,
                preview.MissingSecrets,
                preview.Changes,
                preview.CanAdopt,
                Diff = preview.Diff.Select(l => new { Kind = l.Kind.ToString(), l.Text }),
            });
        });

        // Adoption: anyone on the project (README §13.2, decided 2026-11-18), audited, and only
        // the commit they saw, so a push in between is never adopted unseen. The Gate baseline
        // on the default branch follows at once.
        project.MapPost("/testability/adopt", async (Guid id, AdoptRequest request, ClaimsPrincipal user, IFactoryStore store, ContractAdoption adoption,
            ProjectBaselines baselines, TestabilityChecks checks, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Commit))
                return Results.BadRequest(new { error = "commit is required: adopt the contract you reviewed." });
            if (await store.GetProjectAsync(id, ct) is not { } found)
                return Results.NotFound();
            try
            {
                var adopted = await adoption.AdoptAsync(found, request.Commit.Trim(), user.UserId(), ct);
                baselines.Start(id);
                checks.CheckNow(id);
                return Results.Ok(new { adopted.ProfileRevision });
            }
            catch (AdoptionRefusedException ex)
            {
                return ex.Conflict ? Results.Conflict(new { error = ex.Message }) : Results.BadRequest(new { error = ex.Message });
            }
        });

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

    /// <summary>
    /// A project's testability for the app: the Projects page's column takes the summary, the
    /// Testability panel the <paramref name="detailed"/> view with gaps, the contract on the
    /// default branch and the dry run's checks with their screenshots.
    /// </summary>
    public static object View(Project project, TestabilityCheck? latest, bool checking, FactorySettings settings, bool detailed)
    {
        var report = latest is null ? null : TestabilityReport.FromJson(latest.ReportJson);
        var adopted = VerificationProfile.Parse(project.VerificationProfileJson).WebApp() is not null;
        var status = checking ? TestabilityStatus.Checking : latest?.Status ?? TestabilityStatus.NotChecked;
        if (!detailed)
            return new { Status = status.ToString(), CheckedAt = latest?.CreatedAt, report?.Stack, Adopted = adopted };

        var run = report?.Run;
        return new
        {
            Status = status.ToString(),
            CheckedAt = latest?.CreatedAt,
            latest?.Commit,
            report?.Stack,
            Adopted = adopted,
            project.ProfileRevision,
            Stale = latest is not null && latest.ProfileRevision != project.ProfileRevision,
            report?.Problem,
            Gaps = report?.Gaps ?? [],
            Candidates = report?.Candidates ?? [],
            Contract = report?.Contract is { } c ? new { c.Path, c.Commit, c.Valid, c.Problems } : null,
            Run = run is null ? null : new
            {
                Kind = run.Kind == Probe.Web.Core.Reports.ReportKind.DryRun ? "dry-run" : run.Kind.ToString().ToLowerInvariant(),
                Outcome = run.Outcome.ToString(),
                Checks = run.Checks.Select(k => new
                {
                    k.Id,
                    Kind = k.Kind.ToString(),
                    Status = k.Status.ToString(),
                    Classification = k.Classification?.ToString(),
                    k.Message,
                    k.Route,
                    Screenshots = k.Evidence.Screenshots.Select(ArtifactUrl).ToList(),
                }),
                run.Lint,
                run.Usage.WallSeconds,
                Emails = run.Emails.Select(e => new { e.From, e.To, e.Subject, Url = e.Artifact is null ? null : ArtifactUrl(e.Artifact) }),
            },
            MissingSecrets = MissingSecrets(project, settings),
        };
    }

    /// <summary>Where the app downloads a run artifact, by the id Probe.Web's report holds.</summary>
    public static string ArtifactUrl(string id) => $"/api/run-artifacts/{id}";

    /// <summary>The <c>{secret:}</c> names the project's contract uses that are not set yet.</summary>
    public static IReadOnlyList<string> MissingSecrets(Project project, FactorySettings settings) =>
        VerificationProfile.Parse(project.VerificationProfileJson).WebApp() is { } contract
            ? MissingSecrets(project.Id, contract, settings)
            : [];

    public static IReadOnlyList<string> MissingSecrets(Guid projectId, WebAppContract contract, FactorySettings settings) =>
        [.. ContractValidator.SecretNames(contract).Where(n => !settings.IsSet(SecretNames.Project(projectId, n)))];
}
