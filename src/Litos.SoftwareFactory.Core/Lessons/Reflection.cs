namespace Litos.SoftwareFactory.Core.Lessons;

/// <summary>Where a lesson reflection stands (ReadMe_LitosSoftwareFactory_V1.md §23).</summary>
public enum ReflectionStatus
{
    /// <summary>Queued: its run waits for a slot.</summary>
    Pending,
    Running,

    /// <summary>The accepted task had less left than the allowance; Retry once its cap is raised, or Skip.</summary>
    DeferredBudget,
    Completed,
    Failed,
    Skipped,
}

/// <summary>
/// One lesson reflection (m4-architecture.md §5.1): created when a task is accepted, unique per
/// accepted handoff, so a repeated acceptance or a retry never makes a second. Its proposals become
/// Suggested lessons in the transaction that completes it, so a retry cannot duplicate them.
/// </summary>
public sealed class ReflectionJob
{
    public Guid Id { get; set; }
    public Guid ThreadId { get; set; }

    /// <summary>The handoff that was accepted; null for a task accepted with none.</summary>
    public Guid? HandoffId { get; set; }

    /// <summary>The reflection run of the latest attempt; null while deferred or skipped before one ran.</summary>
    public Guid? RunId { get; set; }

    public ReflectionStatus Status { get; set; } = ReflectionStatus.Pending;
    public int Attempts { get; set; }
    public string? LastError { get; set; }

    /// <summary>What became of each proposal, as JSON: the lessons stored, and those refused with why.</summary>
    public string? OutcomeJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>What became of a reflection's proposals.</summary>
/// <param name="Lessons">The numbers of the lessons stored as suggestions.</param>
/// <param name="Refused">Each refused proposal's rule, and why.</param>
public sealed record ReflectionOutcome(IReadOnlyList<long> Lessons, IReadOnlyList<RefusedProposal> Refused);

public sealed record RefusedProposal(string Rule, string Why);
