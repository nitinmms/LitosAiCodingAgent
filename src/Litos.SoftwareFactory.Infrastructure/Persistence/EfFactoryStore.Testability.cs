using Litos.SoftwareFactory.Core.Store;
using Microsoft.EntityFrameworkCore;

namespace Litos.SoftwareFactory.Infrastructure.Persistence;

/// <summary>Testability checks (testability-architecture.md §6.1): one row per check, the newest wins.</summary>
public sealed partial class EfFactoryStore
{
    public async Task SaveTestabilityCheckAsync(TestabilityCheck check, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        if (!await db.Projects.AnyAsync(p => p.Id == check.ProjectId, ct))
            throw new StoreNotFoundException($"Project {check.ProjectId} was not found.");

        if (check.Id == Guid.Empty)
            check.Id = Guid.NewGuid();
        db.TestabilityChecks.Add(check);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<TestabilityCheck>> LatestTestabilityChecksAsync(CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        // Chosen without their reports first; sorted here, as SQLite cannot order by a DateTimeOffset.
        var candidates = await db.TestabilityChecks.AsNoTracking().Select(c => new { c.Id, c.ProjectId, c.CreatedAt }).ToListAsync(ct);
        var newest = candidates.GroupBy(c => c.ProjectId).Select(g => g.MaxBy(c => c.CreatedAt)!.Id).ToList();
        return await db.TestabilityChecks.AsNoTracking().Where(c => newest.Contains(c.Id)).ToListAsync(ct);
    }

    public async Task<TestabilityCheck?> LatestTestabilityCheckAsync(Guid projectId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var candidates = await db.TestabilityChecks.AsNoTracking().Where(c => c.ProjectId == projectId).Select(c => new { c.Id, c.CreatedAt }).ToListAsync(ct);
        if (candidates.Count == 0)
            return null;
        var newest = candidates.MaxBy(c => c.CreatedAt)!.Id;
        return await db.TestabilityChecks.AsNoTracking().FirstAsync(c => c.Id == newest, ct);
    }
}
