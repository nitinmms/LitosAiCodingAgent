using System.Text;
using System.Text.Json;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Store;
using Microsoft.EntityFrameworkCore;

namespace Litos.SoftwareFactory.Infrastructure.Persistence;

/// <summary>
/// Pages a chat answer published on a thread (ReadMe_ArtifactsFeature.md §11.5): an artifact per
/// page, a row per version, and nothing deleted while the thread lasts. Every write is scoped to
/// the publishing run's own thread, so a run can only ever add to its own thread's pages.
/// </summary>
public sealed partial class EfFactoryStore
{
    public async Task<(ThreadArtifact Artifact, bool Created)> PublishArtifactAsync(
        Guid runId, Guid? artifactId, string title, string html, long maxBytesPerThread, DateTimeOffset now, CancellationToken ct)
    {
        await using var write = await BeginWriteAsync(ct);
        var db = write.Db;
        var run = await db.Runs.FirstOrDefaultAsync(r => r.Id == runId, ct)
            ?? throw new StoreNotFoundException("The run does not exist.");
        // The thread's row lock makes two publishes at once count each other, and number in turn.
        var thread = await LockThreadAsync(db, run.ThreadId, ct);

        var size = Encoding.UTF8.GetByteCount(html);
        var held = await (
            from v in db.ArtifactVersions
            join a in db.Artifacts on v.ArtifactId equals a.Id
            where a.ThreadId == thread.Id
            select (long?)v.SizeBytes).SumAsync(ct) ?? 0;
        if (held + size > maxBytesPerThread)
            throw new StoreConflictException(
                $"This thread's pages would come to {Megabytes(held + size)}, over its limit of {Megabytes(maxBytesPerThread)}.");

        ThreadArtifact artifact;
        var created = artifactId is null;
        if (artifactId is { } id)
        {
            // Another thread's artifact is "not found", not "not yours": its existence isn't the run's to learn.
            artifact = await db.Artifacts.FirstOrDefaultAsync(a => a.Id == id && a.ThreadId == thread.Id, ct)
                ?? throw new StoreNotFoundException("There is no artifact with that id on this thread.");
            artifact.CurrentVersion++;
            artifact.Title = title;
            artifact.UpdatedAt = now;
        }
        else
        {
            artifact = new ThreadArtifact
            {
                Id = Guid.NewGuid(),
                ThreadId = thread.Id,
                ProjectId = thread.ProjectId,
                Title = title,
                CurrentVersion = 1,
                CreatedBy = run.RequestedBy,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Artifacts.Add(artifact);
        }

        db.ArtifactVersions.Add(new ThreadArtifactVersion
        {
            ArtifactId = artifact.Id,
            Version = artifact.CurrentVersion,
            Html = html,
            SizeBytes = size,
            RunId = run.Id,
            CreatedAt = now,
        });
        await write.CommitAsync(ct);
        return (artifact, created);
    }

    public async Task<ThreadArtifact?> GetArtifactAsync(Guid artifactId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.Artifacts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == artifactId, ct);
    }

    public async Task<ThreadArtifactVersion?> GetArtifactVersionAsync(Guid artifactId, int? version, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var wanted = version ?? await db.Artifacts.Where(a => a.Id == artifactId).Select(a => (int?)a.CurrentVersion).FirstOrDefaultAsync(ct);
        return wanted is null
            ? null
            : await db.ArtifactVersions.AsNoTracking().FirstOrDefaultAsync(v => v.ArtifactId == artifactId && v.Version == wanted, ct);
    }

    public async Task<IReadOnlyList<ThreadArtifact>> ListArtifactsAsync(Guid threadId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var rows = await db.Artifacts.AsNoTracking().Where(a => a.ThreadId == threadId).ToListAsync(ct);
        // Sorted here: SQLite cannot order by a DateTimeOffset.
        return [.. rows.OrderBy(a => a.CreatedAt).ThenBy(a => a.Id)];
    }

    /// <summary>The versions a run published, as the payload of the answer that shows them; null when it published none.</summary>
    private static async Task<string?> PublishedArtifactsPayloadAsync(FactoryDbContext db, Guid runId, CancellationToken ct)
    {
        var versions = await db.ArtifactVersions.Where(v => v.RunId == runId)
            .Select(v => new { v.ArtifactId, v.Version, v.CreatedAt })
            .ToListAsync(ct);
        if (versions.Count == 0)
            return null;

        // In the order the answer first published each page (sorted here: SQLite cannot order by a
        // DateTimeOffset); a page published twice in one answer is shown once, at its last version.
        var published = versions
            .GroupBy(v => v.ArtifactId)
            .OrderBy(g => g.Min(v => v.CreatedAt)).ThenBy(g => g.Min(v => v.Version))
            .Select(g => new PublishedArtifact(g.Key, g.Max(v => v.Version)))
            .ToList();
        return JsonSerializer.Serialize(new MessageArtifacts(published), FactoryWire.Json);
    }
}
