using Litos.Probe.Web;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Settings;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Host.Settings;

namespace Litos.SoftwareFactory.Host.Testability;

/// <summary>
/// Where the browser checks' files are kept: the data folder's run-artifacts/&lt;project&gt;/&lt;id&gt;,
/// never the database or wwwroot. The M4 backup copies the folder, like attachments.
/// </summary>
public sealed class RunArtifactFiles(FactoryOptions options)
{
    public string Root => Path.Combine(options.DataDirectory, "run-artifacts");

    public string PathOf(Guid projectId, Guid artifactId) => Path.Combine(Root, projectId.ToString("n"), artifactId.ToString("n"));

    public string PathOf(RunArtifact artifact) => PathOf(artifact.ProjectId, artifact.Id);
}

/// <summary>
/// Litos.Probe.Web's <see cref="IArtifactSink"/> for the factory (README_Litos.Probe.Web.md §7):
/// each file goes to <see cref="RunArtifactFiles"/> and a <c>run_artifacts</c> row, and its id is
/// the row's, which the download endpoint serves with project-access checks.
/// </summary>
public sealed class FactoryArtifactSink(IFactoryStore store, RunArtifactFiles files, IClock clock, Guid projectId, Guid? runId) : IArtifactSink
{
    public async Task<string> SaveAsync(ArtifactKind kind, string fileName, Stream content, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var path = files.PathOf(projectId, id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        long size;
        await using (var file = File.Create(path))
        {
            await content.CopyToAsync(file, ct);
            size = file.Length;
        }

        try
        {
            await store.AddRunArtifactAsync(new RunArtifact
            {
                Id = id,
                ProjectId = projectId,
                RunId = runId,
                Kind = KindOf(kind, fileName),
                FileName = Path.GetFileName(fileName)[..Math.Min(Path.GetFileName(fileName).Length, RunArtifact.MaxFileNameLength)],
                MediaType = MediaTypeOf(fileName),
                SizeBytes = size,
                CreatedAt = clock.UtcNow,
            }, ct);
        }
        catch
        {
            File.Delete(path);
            throw;
        }

        return id.ToString();
    }

    internal static RunArtifactKind KindOf(ArtifactKind kind, string fileName) => kind switch
    {
        ArtifactKind.Screenshot => RunArtifactKind.Screenshot,
        ArtifactKind.Trace => RunArtifactKind.Trace,
        ArtifactKind.Report => RunArtifactKind.Report,
        _ when fileName.EndsWith(".eml", StringComparison.OrdinalIgnoreCase) => RunArtifactKind.Email,
        _ => RunArtifactKind.Log,
    };

    internal static string MediaTypeOf(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".zip" => "application/zip",
        ".json" => "application/json",
        ".eml" => "message/rfc822",
        _ => "text/plain",
    };
}

/// <summary>
/// <c>{secret:NAME}</c> for one project: its <c>project:&lt;id&gt;:NAME</c> secret
/// (testability-architecture.md §5). Read only when Probe.Web builds an app process's environment
/// or fills a sign-in form, so a role password never reaches a brief, a worker or a log.
/// </summary>
public sealed class ProjectSecretResolver(FactorySettings settings, Guid projectId) : ISecretResolver
{
    public string? Resolve(string name) =>
        SecretNames.IsValidProjectSecretName(name) ? settings.Secret(SecretNames.Project(projectId, name)) : null;
}
