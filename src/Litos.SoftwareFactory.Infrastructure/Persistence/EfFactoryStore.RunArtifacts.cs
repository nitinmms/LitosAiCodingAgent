using Litos.SoftwareFactory.Core.Store;
using Microsoft.EntityFrameworkCore;

namespace Litos.SoftwareFactory.Infrastructure.Persistence;

/// <summary>
/// The browser checks' files (README_Litos.Probe.Web.md §7): screenshots, traces, logs, reports
/// and captured emails. The bytes are the host's to keep; the store holds what describes them.
/// </summary>
public sealed partial class EfFactoryStore
{
    public async Task<RunArtifact> AddRunArtifactAsync(RunArtifact artifact, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        if (!await db.Projects.AnyAsync(p => p.Id == artifact.ProjectId, ct))
            throw new StoreNotFoundException($"Project {artifact.ProjectId} was not found.");

        if (artifact.Id == Guid.Empty)
            artifact.Id = Guid.NewGuid();
        db.RunArtifacts.Add(artifact);
        await db.SaveChangesAsync(ct);
        return artifact;
    }

    public async Task<RunArtifact?> GetRunArtifactAsync(Guid artifactId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.RunArtifacts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == artifactId, ct);
    }

    public async Task<IReadOnlyList<RunArtifact>> ListRunArtifactsAsync(Guid runId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var rows = await db.RunArtifacts.AsNoTracking().Where(a => a.RunId == runId).ToListAsync(ct);
        // Sorted here: SQLite cannot order by a DateTimeOffset.
        return [.. rows.OrderBy(a => a.CreatedAt).ThenBy(a => a.FileName, StringComparer.Ordinal)];
    }
}
