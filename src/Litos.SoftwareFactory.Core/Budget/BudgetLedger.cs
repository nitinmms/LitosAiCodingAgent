using Litos.Agent.Streaming;

namespace Litos.SoftwareFactory.Core.Budget;

/// <summary>Where a usage reservation stands (ReadMe_LitosSoftwareFactory_V1.md §9.2).</summary>
public enum UsageStatus
{
    /// <summary>Admitted and sent; the provider has not reported usage yet.</summary>
    Reserved,

    /// <summary>The provider reported usage and the reservation was replaced by it.</summary>
    Settled,

    /// <summary>The call ended without reported usage (timeout, cancellation, provider omitted
    /// it). The reservation stays charged until reconciled — it is never silently refunded.</summary>
    Unknown,
}

/// <summary>
/// A task's allowance and its user's quota at one instant. Null caps mean "no cap". Used is
/// settled usage; Reserved is the sum of reservations not yet settled, including Unknown ones.
/// </summary>
public sealed record BudgetSnapshot(
    long? TaskCap, long TaskUsed, long TaskReserved,
    long? QuotaCap = null, long QuotaUsed = 0, long QuotaReserved = 0)
{
    /// <summary>What the task may still commit to; null when it has no cap. Never negative.</summary>
    public long? TaskRemaining => TaskCap is null ? null : Math.Max(0, TaskCap.Value - TaskUsed - TaskReserved);

    public long? QuotaRemaining => QuotaCap is null ? null : Math.Max(0, QuotaCap.Value - QuotaUsed - QuotaReserved);
}

public enum RefusalReason
{
    TaskBudget,
    UserQuota,
}

public abstract record AdmissionDecision;

/// <summary>The call may be sent, with the provider's output limit set to MaxOutputTokens so
/// the response cannot exceed the reservation.</summary>
public sealed record Admitted(long Reserved, int MaxOutputTokens) : AdmissionDecision;

/// <summary>The call must not be sent: it does not fit. Nothing has been spent.</summary>
public sealed record Refused(RefusalReason Reason, long Needed, long Remaining) : AdmissionDecision;

public sealed record BudgetPolicy
{
    /// <summary>The bounded output allowance reserved for, and imposed on, every call.</summary>
    public int OutputAllowanceTokens { get; init; } = 8_192;

    /// <summary>Head-room over the estimate, covering estimator error.</summary>
    public double Margin { get; init; } = 0.10;
}

/// <summary>
/// The reserve/settle arithmetic of §9.2. Pure: the host reads a <see cref="BudgetSnapshot"/>
/// under the thread's budget-row lock, asks these functions what to do, and writes the result
/// back in the same transaction.
/// </summary>
public static class BudgetLedger
{
    /// <summary>
    /// What a call must reserve: the estimated input plus the output allowance, plus the margin
    /// on both, rounded up so a fractional token is never under-reserved.
    /// </summary>
    public static long ReservationFor(long estimatedInputTokens, BudgetPolicy policy)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(estimatedInputTokens);
        return (long)Math.Ceiling((estimatedInputTokens + policy.OutputAllowanceTokens) * (1 + policy.Margin));
    }

    /// <summary>
    /// Decides whether a call may be sent. The task's allowance is checked before the user's
    /// quota, so a call that fits neither is reported against the task — the cap the user can
    /// raise from the thread.
    /// </summary>
    public static AdmissionDecision Admit(BudgetSnapshot budget, long estimatedInputTokens, BudgetPolicy policy)
    {
        var needed = ReservationFor(estimatedInputTokens, policy);

        if (budget.TaskRemaining is { } taskRemaining && needed > taskRemaining)
            return new Refused(RefusalReason.TaskBudget, needed, taskRemaining);
        if (budget.QuotaRemaining is { } quotaRemaining && needed > quotaRemaining)
            return new Refused(RefusalReason.UserQuota, needed, quotaRemaining);

        return new Admitted(needed, policy.OutputAllowanceTokens);
    }

    /// <summary>The snapshot once an admitted call's reservation is recorded.</summary>
    public static BudgetSnapshot Reserve(BudgetSnapshot budget, long reserved) => budget with
    {
        TaskReserved = budget.TaskReserved + reserved,
        QuotaReserved = budget.QuotaReserved + reserved,
    };

    /// <summary>
    /// Task tokens for one call: provider-reported input plus output, with cached input counted
    /// (TotalInputTokens sums input, cache creation and cache read, which providers report as
    /// separate counts). Reasoning tokens are already inside OutputTokens and are not added again.
    /// </summary>
    public static long ChargeFor(UsageInfo usage) => (long)usage.TotalInputTokens + usage.OutputTokens;

    /// <summary>True when a provider reported no usage at all — which a local server does when it
    /// omits usage, and which must not be mistaken for a free call.</summary>
    public static bool IsUnreported(UsageInfo usage) => usage.TotalInputTokens == 0 && usage.OutputTokens == 0;

    /// <summary>
    /// What to charge when a call completes. Reported usage is charged as reported, even when it
    /// exceeds the reservation. Whatever the provider did not report is charged from the host's
    /// own estimate instead (§9.4) — and that is decided for input and output separately, because
    /// a server can report one and omit the other. A completed call always had some input, so a
    /// reported input of zero means "not reported", never "free"; the same holds for output
    /// whenever the host can see the reply was not empty.
    /// </summary>
    /// <param name="estimatedInputTokens">The pre-send estimate the call was admitted on.</param>
    /// <param name="estimatedOutputTokens">The host's estimate of the reply it relayed — see
    /// RequestEstimator.EstimateOutputTokens. Zero for a reply with no content.</param>
    public static long SettlementCharge(UsageInfo usage, long estimatedInputTokens, long estimatedOutputTokens)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(estimatedInputTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(estimatedOutputTokens);

        long input = usage.TotalInputTokens > 0 ? usage.TotalInputTokens : estimatedInputTokens;
        long output = usage.OutputTokens > 0 ? usage.OutputTokens : estimatedOutputTokens;
        return input + output;
    }

    /// <summary>The snapshot once a reservation is replaced by its actual charge.</summary>
    public static BudgetSnapshot Settle(BudgetSnapshot budget, long reserved, long charge) => budget with
    {
        TaskReserved = Math.Max(0, budget.TaskReserved - reserved),
        TaskUsed = budget.TaskUsed + charge,
        QuotaReserved = Math.Max(0, budget.QuotaReserved - reserved),
        QuotaUsed = budget.QuotaUsed + charge,
    };

    /// <summary>
    /// A call ended with unknown usage. The snapshot is deliberately unchanged: the reservation
    /// stays charged until it is reconciled.
    /// </summary>
    public static BudgetSnapshot KeepUnknown(BudgetSnapshot budget) => budget;

    /// <summary>
    /// The call was never sent (refused upstream, or failed before the request left the host), so
    /// it cannot have incurred usage and its reservation is released in full.
    /// </summary>
    public static BudgetSnapshot Release(BudgetSnapshot budget, long reserved) => Settle(budget, reserved, charge: 0);

    /// <summary>Raising a cap changes the maximum, not the accounting history.</summary>
    public static BudgetSnapshot WithTaskCap(BudgetSnapshot budget, long? cap) => budget with { TaskCap = cap };
}
