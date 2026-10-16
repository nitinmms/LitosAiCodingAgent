using Litos.SoftwareFactory.Core.Lessons;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Orchestration;

namespace Litos.SoftwareFactory.Core.Metrics;

/// <summary>A run marked Running, with what the host needs to tell whether it is really being worked on.</summary>
/// <param name="LastCheckpointAt">When it last saved a step; a long turn writes none, so this alone is not liveness.</param>
public sealed record RunningRun(
    Guid RunId, Guid ThreadId, string Title, RunKind Kind, DateTimeOffset? StartedAt, DateTimeOffset? LastCheckpointAt,
    int? WorkerProcessId, DateTimeOffset? WorkerStartTime);

/// <summary>Usage entries still reserved or unknown after their run ended: tokens held that no run will settle.</summary>
public sealed record UnsettledUsage(int Count, long Tokens, DateTimeOffset? Oldest);

public sealed record HandoffFailure(Guid ThreadId, string Title, DateTimeOffset At);

public sealed record PendingDecision(Guid ThreadId, string Title, string Question, DateTimeOffset At);

/// <summary>One person's use against the quotas: what is charged and what is reserved, today and this month (UTC).</summary>
public sealed record UserUsage(Guid UserId, long DayUsed, long DayReserved, long MonthUsed, long MonthReserved);

/// <summary>
/// What the operational counters are read from (ReadMe_LitosSoftwareFactory_V1.md §24,
/// m4-architecture.md §7): rows the factory keeps, read for Admins, never stored for the purpose.
/// </summary>
public sealed record OperationsData(
    IReadOnlyDictionary<LifecycleState, int> TasksByState,
    IReadOnlyList<RunningRun> Running,
    UnsettledUsage Unsettled,
    IReadOnlyList<HandoffFailure> HandoffFailures,
    IReadOnlyDictionary<ReflectionStatus, int> Reflections,
    PendingDecision? OldestDecision,
    int OpenDecisions,
    IReadOnlyList<UserUsage> Users);
