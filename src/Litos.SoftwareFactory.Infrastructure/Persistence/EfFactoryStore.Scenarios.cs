using Litos.SoftwareFactory.Core.Store;
using Microsoft.EntityFrameworkCore;

namespace Litos.SoftwareFactory.Infrastructure.Persistence;

/// <summary>Project scenarios (README_Litos.Probe.Web.md W3.6): immutable revisions, one approved per scenario.</summary>
public sealed partial class EfFactoryStore
{
    public async Task<ProjectScenario> AddScenarioRevisionAsync(
        Guid projectId, string scenarioId, Func<int, string> withRevision, string? title, string? criterion, Guid? createdBy, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        if (!await db.Projects.AnyAsync(p => p.Id == projectId, ct))
            throw new StoreNotFoundException($"Project {projectId} was not found.");

        // Two saves at once may pick the same number: the unique index refuses the second, which tries again.
        for (var attempt = 0; ; attempt++)
        {
            var highest = await db.ProjectScenarios.Where(s => s.ProjectId == projectId && s.ScenarioId == scenarioId)
                .Select(s => (int?)s.Revision).MaxAsync(ct) ?? 0;
            var row = new ProjectScenario
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                ScenarioId = scenarioId,
                Revision = highest + 1,
                ScenarioJson = withRevision(highest + 1),
                Title = title,
                Criterion = criterion,
                Status = ScenarioStatus.Draft,
                CreatedBy = createdBy,
                CreatedAt = now,
            };
            db.ProjectScenarios.Add(row);
            try
            {
                await db.SaveChangesAsync(ct);
                return row;
            }
            catch (DbUpdateException) when (attempt < 3)
            {
                db.ChangeTracker.Clear();
            }
        }
    }

    public async Task<IReadOnlyList<ProjectScenario>> ListScenariosAsync(Guid projectId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var rows = await db.ProjectScenarios.AsNoTracking().Where(s => s.ProjectId == projectId).ToListAsync(ct);
        return [.. rows.OrderBy(s => s.ScenarioId, StringComparer.Ordinal).ThenByDescending(s => s.Revision)];
    }

    public async Task<IReadOnlyList<ProjectScenario>> ApprovedScenariosAsync(Guid projectId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var rows = await db.ProjectScenarios.AsNoTracking()
            .Where(s => s.ProjectId == projectId && s.Status == ScenarioStatus.Approved).ToListAsync(ct);
        return [.. rows.OrderBy(s => s.ScenarioId, StringComparer.Ordinal)];
    }

    public async Task<ProjectScenario> ApproveScenarioAsync(Guid projectId, string scenarioId, int revision, Guid? approvedBy, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var revisions = await db.ProjectScenarios.Where(s => s.ProjectId == projectId && s.ScenarioId == scenarioId).ToListAsync(ct);
        var target = revisions.FirstOrDefault(s => s.Revision == revision)
            ?? throw new StoreNotFoundException($"Scenario {scenarioId} has no revision {revision}.");
        if (target.Status != ScenarioStatus.Draft)
            throw new StoreConflictException($"Revision {revision} of {scenarioId} is {target.Status.ToString().ToLowerInvariant()}, not a draft: only a draft can be approved.");

        foreach (var earlier in revisions.Where(s => s.Status == ScenarioStatus.Approved))
            earlier.Status = ScenarioStatus.Superseded;
        target.Status = ScenarioStatus.Approved;
        target.ApprovedBy = approvedBy;
        target.ApprovedAt = now;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return target;
    }

    public async Task<int> RetireScenarioAsync(Guid projectId, string scenarioId, Guid? retiredBy, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var revisions = await db.ProjectScenarios.Where(s => s.ProjectId == projectId && s.ScenarioId == scenarioId).ToListAsync(ct);
        if (revisions.Count == 0)
            throw new StoreNotFoundException($"The project has no scenario {scenarioId}.");

        var live = revisions.Where(s => s.Status != ScenarioStatus.Retired).ToList();
        foreach (var row in live)
        {
            row.Status = ScenarioStatus.Retired;
            row.RetiredBy = retiredBy;
            row.RetiredAt = now;
        }

        await db.SaveChangesAsync(ct);
        return live.Count;
    }
}
