using Litos.SoftwareFactory.Core.Store;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Litos.SoftwareFactory.Infrastructure.Persistence;

/// <summary>A factory account. M1 seeds one Admin; invitations and membership arrive in M2.</summary>
public sealed class FactoryUser : IdentityUser<Guid>
{
    public string? DisplayName { get; set; }

    public bool Disabled { get; set; }
}

/// <summary>
/// The factory's own state database, <c>litos_factory</c> (ReadMe_LitosSoftwareFactory_V1.md
/// §20). It is never a target application's database, and its connection string never reaches a
/// worker. EF Core migrations are the single migration authority for this schema.
/// </summary>
public sealed class FactoryDbContext(DbContextOptions<FactoryDbContext> options)
    : IdentityDbContext<FactoryUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<TaskThread> Threads => Set<TaskThread>();
    public DbSet<ThreadMessage> Messages => Set<ThreadMessage>();
    public DbSet<Specification> Specifications => Set<Specification>();
    public DbSet<TaskRun> Runs => Set<TaskRun>();
    public DbSet<Decision> Decisions => Set<Decision>();
    public DbSet<UsageEntry> Usage => Set<UsageEntry>();
    public DbSet<VerificationRecord> Verifications => Set<VerificationRecord>();
    public DbSet<ReviewFindingRecord> ReviewFindings => Set<ReviewFindingRecord>();
    public DbSet<HandoffRecord> Handoffs => Set<HandoffRecord>();
    public DbSet<WorkspaceLease> Leases => Set<WorkspaceLease>();
    public DbSet<OutboxEvent> Outbox => Set<OutboxEvent>();
    public DbSet<Invitation> Invitations => Set<Invitation>();
    public DbSet<ProjectMember> ProjectMembers => Set<ProjectMember>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configuration)
    {
        // Enums are stored by name: a row is readable on its own, and adding a member can never
        // silently renumber the ones stored before it.
        configuration.Properties<Enum>().HaveConversion<string>().HaveMaxLength(40);

        // SQLite (used by the fast tests) has no timestamp-with-offset type and cannot order by
        // one; a UTC tick count sorts correctly. PostgreSQL keeps timestamptz.
        if (Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true)
            configuration.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        base.OnModelCreating(model);
        var postgres = Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;

        // Bounded structured payloads are JSONB on PostgreSQL; large logs, patches and reports
        // stay in the factory data directory.
        void Json<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<T> property)
        {
            if (postgres)
                property.HasColumnType("jsonb");
        }

        model.Entity<Project>(e =>
        {
            e.ToTable("projects");
            e.Property(p => p.Name).HasMaxLength(200);
            e.Property(p => p.GitHubOwner).HasMaxLength(100);
            e.Property(p => p.GitHubRepository).HasMaxLength(100);
            e.Property(p => p.DefaultBranch).HasMaxLength(250);
            Json(e.Property(p => p.VerificationProfileJson));
            e.HasIndex(p => new { p.GitHubOwner, p.GitHubRepository }).IsUnique();
        });

        model.Entity<TaskThread>(e =>
        {
            e.ToTable("task_threads");
            e.Property(t => t.Title).HasMaxLength(300);
            e.Property(t => t.TypeLabel).HasMaxLength(40);
            e.Property(t => t.SessionId).HasMaxLength(100);
            e.Property(t => t.Provider).HasMaxLength(60);
            e.Property(t => t.Model).HasMaxLength(200);
            e.Property(t => t.Branch).HasMaxLength(250);
            e.HasOne<Project>().WithMany().HasForeignKey(t => t.ProjectId).OnDelete(DeleteBehavior.Restrict);
            // Board queries and the coordinator's search for queued work.
            e.HasIndex(t => new { t.ProjectId, t.Stage, t.State });
            e.HasIndex(t => t.State);
            // The board's owner filter and "awaiting you".
            e.HasIndex(t => t.OwnerId);
        });

        model.Entity<ThreadMessage>(e =>
        {
            e.ToTable("messages");
            e.Property(m => m.DispatchKey).HasMaxLength(100);
            Json(e.Property(m => m.PayloadJson));
            e.HasOne<TaskThread>().WithMany().HasForeignKey(m => m.ThreadId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(m => new { m.ThreadId, m.Sequence }).IsUnique();
            e.HasIndex(m => m.DispatchKey).IsUnique();
        });

        model.Entity<Specification>(e =>
        {
            e.ToTable("specifications");
            Json(e.Property(s => s.AcceptanceCriteriaJson));
            Json(e.Property(s => s.AffectedAreasJson));
            Json(e.Property(s => s.OpenQuestionsJson));
            e.HasOne<TaskThread>().WithMany().HasForeignKey(s => s.ThreadId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(s => new { s.ThreadId, s.Revision }).IsUnique();
        });

        model.Entity<TaskRun>(e =>
        {
            e.ToTable("task_runs");
            e.Property(r => r.PromptRevision).HasMaxLength(40);
            e.Property(r => r.BaselineCommit).HasMaxLength(64);
            e.Property(r => r.HeadCommit).HasMaxLength(64);
            e.Property(r => r.ReviewSessionId).HasMaxLength(100);
            Json(e.Property(r => r.StateJson));
            Json(e.Property(r => r.WorkspaceSnapshotJson));
            e.HasOne<TaskThread>().WithMany().HasForeignKey(r => r.ThreadId).OnDelete(DeleteBehavior.Cascade);
            // One active run per thread: a second one cannot be inserted while the first is live. A
            // chat run answering a message is not the task's work, so it never blocks one.
            e.HasIndex(r => r.ThreadId).IsUnique().HasFilter("\"Status\" <> 'Finished' AND \"Kind\" <> 'Chat'").HasDatabaseName("ix_task_runs_one_active_per_thread");
            e.HasIndex(r => new { r.Status, r.QueuedAt });
        });

        model.Entity<Decision>(e =>
        {
            e.ToTable("decisions");
            Json(e.Property(d => d.OptionsJson));
            e.HasOne<TaskRun>().WithMany().HasForeignKey(d => d.RunId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(d => new { d.ThreadId, d.Status });
        });

        model.Entity<UsageEntry>(e =>
        {
            e.ToTable("usage_entries");
            e.Property(u => u.RequestKey).HasMaxLength(100);
            e.Property(u => u.Provider).HasMaxLength(60);
            e.Property(u => u.Model).HasMaxLength(200);
            e.Property(u => u.Phase).HasMaxLength(40);
            e.Property(u => u.ServedBy).HasMaxLength(100);
            e.HasOne<TaskThread>().WithMany().HasForeignKey(u => u.ThreadId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(u => u.RequestKey).IsUnique();
            e.HasIndex(u => new { u.UserId, u.CreatedAt });
        });

        model.Entity<VerificationRecord>(e =>
        {
            e.ToTable("verifications");
            e.Property(v => v.Commit).HasMaxLength(64);
            Json(e.Property(v => v.OutcomeJson));
            e.HasOne<Project>().WithMany().HasForeignKey(v => v.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(v => new { v.ProjectId, v.Kind, v.Commit, v.ProfileRevision });
            e.HasIndex(v => v.RunId);
        });

        model.Entity<ReviewFindingRecord>(e =>
        {
            e.ToTable("review_findings");
            e.Property(f => f.File).HasMaxLength(1000);
            e.HasOne<TaskRun>().WithMany().HasForeignKey(f => f.RunId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<HandoffRecord>(e =>
        {
            e.ToTable("handoffs");
            e.Property(h => h.Branch).HasMaxLength(250);
            e.Property(h => h.CommitSha).HasMaxLength(64);
            Json(e.Property(h => h.EvidenceJson));
            e.HasOne<TaskRun>().WithMany().HasForeignKey(h => h.RunId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(h => h.ThreadId);
        });

        model.Entity<WorkspaceLease>(e =>
        {
            e.ToTable("workspace_leases");
            e.Property(l => l.LockIdentity).HasMaxLength(400);
            // A live lease per lock identity: the row exists exactly while the lock is held.
            e.HasIndex(l => l.LockIdentity).IsUnique();
            e.HasIndex(l => l.ThreadId);
        });

        model.Entity<OutboxEvent>(e =>
        {
            e.ToTable("outbox_events");
            e.HasKey(o => o.Sequence);
            e.Property(o => o.Sequence).ValueGeneratedOnAdd();
            e.Property(o => o.Type).HasMaxLength(40);
            Json(e.Property(o => o.PayloadJson));
            e.HasIndex(o => new { o.ThreadId, o.Sequence });
        });

        model.Entity<Invitation>(e =>
        {
            e.ToTable("invitations");
            e.Property(i => i.UserName).HasMaxLength(100);
            e.Property(i => i.Email).HasMaxLength(256);
            e.Property(i => i.TokenHash).HasMaxLength(64);
            Json(e.Property(i => i.ProjectIdsJson));
            e.HasIndex(i => i.TokenHash).IsUnique();
            e.HasIndex(i => i.UserName);
        });

        model.Entity<ProjectMember>(e =>
        {
            e.ToTable("project_members");
            e.HasKey(m => new { m.ProjectId, m.UserId });
            e.HasOne<Project>().WithMany().HasForeignKey(m => m.ProjectId).OnDelete(DeleteBehavior.Cascade);
            // Which projects a user belongs to: asked on every project-scoped call.
            e.HasIndex(m => m.UserId);
        });

        model.Entity<AuditEvent>(e =>
        {
            e.ToTable("audit_events");
            e.Property(a => a.Action).HasMaxLength(80);
            e.Property(a => a.TargetType).HasMaxLength(40);
            Json(e.Property(a => a.DetailsJson));
            e.HasIndex(a => new { a.ProjectId, a.CreatedAt });
            e.HasIndex(a => a.CreatedAt);
        });
    }
}
