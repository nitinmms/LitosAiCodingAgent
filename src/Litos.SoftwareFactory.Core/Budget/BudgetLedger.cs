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

    /// <summary>An Unknown call, reconciled once nothing could report its usage any more: it was
    /// charged the host's own input estimate and the rest of its reservation was released.</summary>
    Estimated,
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
    /// <summary>
    /// The bounded output allowance reserved for, and imposed on, every call. A reasoning
    /// model's thinking counts as output, so this has to leave room for the reply after it: at
    /// 8,192 the first real run spent the whole allowance reasoning and returned nothing.
    /// </summary>
    public int OutputAllowanceTokens { get; init; } = 32_768;

    /// <summary>Head-room over the estimate, covering estimator error.</summary>
    public double Margin { get; init; } = 0.10;

    /// <summary>
    /// How much of a token read from the provider's prompt cache counts against the budget. An
    /// agent resends its whole conversation on every call, so at full weight a budget measures
    /// how many calls a task took rather than how much work it did: the first real run spent 88%
    /// of a million tokens re-reading its own cache. 0.10 is close to what providers charge for
    /// a cache read. 1 counts cached input in full; tokens written to the cache always do.
    /// </summary>
    public double CachedInputWeight { get; init; } = 0.10;

    /// <summary>
    /// The least output room a call must be left with to be worth sending. A call is admitted
    /// when its input and this much output fit; its output limit is then whatever remains, up
    /// to the allowance. Below this a reply would be cut off before it said anything.
    /// </summary>
    public int MinimumOutputTokens { get; init; } = 4_096;

    /// <summary>How long after a call the provider's prompt cache is assumed to still hold that
    /// call's input. Providers keep it for about five minutes.</summary>
    public TimeSpan CacheWindow { get; init; } = TimeSpan.FromMinutes(4);
}

/// <summary>
/// The reserve/settle arithmetic of §9.2. Pure: the host reads a <see cref="BudgetSnapshot"/>
/// under the thread's budget-row lock, asks these functions what to do, and writes the result
/// back in the same transaction.
/// </summary>
public static class BudgetLedger
{
    /// <summary>
    /// What a call's input is expected to be charged: the part of the estimate the provider is
    /// expected to serve from its cache at the cached weight, the rest in full. This is what a
    /// reservation is built on, so that a call is reserved for about what it will cost — at full
    /// weight a call that cost 6,000 tokens was holding 55,000.
    /// </summary>
    public static long ExpectedInputCharge(long estimatedInputTokens, long expectedCachedTokens, double cachedInputWeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(estimatedInputTokens);
        if (cachedInputWeight is < 0 or > 1 || double.IsNaN(cachedInputWeight))
            throw new ArgumentOutOfRangeException(nameof(cachedInputWeight), cachedInputWeight, "The weight is a fraction between 0 and 1.");

        var cached = Math.Clamp(expectedCachedTokens, 0, estimatedInputTokens);
        return estimatedInputTokens - cached + Scale(cached, cachedInputWeight);
    }

    /// <summary>Tokens times a factor, rounded up. In decimal: in binary floating point 2,980 × 1.1
    /// comes out a hair above 3,278 and would round up to 3,279.</summary>
    private static long Scale(long tokens, double factor) => (long)Math.Ceiling(tokens * (decimal)factor);

    /// <summary>What is held for a call's input: its expected charge plus the margin for
    /// estimator error, rounded up so a fractional token is never under-reserved.</summary>
    public static long InputReservation(long expectedInputCharge, BudgetPolicy policy)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedInputCharge);
        return Scale(expectedInputCharge, 1 + policy.Margin);
    }

    /// <summary>
    /// The most a call can reserve: its input reservation plus the whole output allowance. The
    /// output limit is exact — the provider enforces it — so it carries no margin.
    /// </summary>
    public static long ReservationFor(long expectedInputCharge, BudgetPolicy policy) =>
        InputReservation(expectedInputCharge, policy) + policy.OutputAllowanceTokens;

    /// <summary>
    /// Decides whether a call may be sent. The task's allowance is checked before the user's
    /// quota, so a call that fits neither is reported against the task — the cap the user can
    /// raise from the thread.
    /// </summary>
    public static AdmissionDecision Admit(BudgetSnapshot budget, long expectedInputCharge, BudgetPolicy policy)
    {
        // A call is worth sending when its input and a minimum of output fit. It need not have
        // room for the whole output allowance: its output limit is set to what remains, so it
        // cannot spend past the cap either way.
        var input = InputReservation(expectedInputCharge, policy);
        var needed = input + Math.Min(policy.MinimumOutputTokens, policy.OutputAllowanceTokens);

        if (budget.TaskRemaining is { } taskRemaining && needed > taskRemaining)
            return new Refused(RefusalReason.TaskBudget, needed, taskRemaining);
        if (budget.QuotaRemaining is { } quotaRemaining && needed > quotaRemaining)
            return new Refused(RefusalReason.UserQuota, needed, quotaRemaining);

        long output = policy.OutputAllowanceTokens;
        if (budget.TaskRemaining is { } task)
            output = Math.Min(output, task - input);
        if (budget.QuotaRemaining is { } quota)
            output = Math.Min(output, quota - input);

        return new Admitted(input + output, (int)output);
    }

    /// <summary>The snapshot once an admitted call's reservation is recorded.</summary>
    public static BudgetSnapshot Reserve(BudgetSnapshot budget, long reserved) => budget with
    {
        TaskReserved = budget.TaskReserved + reserved,
        QuotaReserved = budget.QuotaReserved + reserved,
    };

    /// <summary>
    /// Task tokens for one call: provider-reported input plus output. Input is every token the
    /// provider reports (input, cache creation and cache read are separate counts), with the
    /// tokens read from the cache counted at <paramref name="cachedInputWeight"/> and rounded up.
    /// Reasoning tokens are already inside OutputTokens and are not added again.
    /// </summary>
    public static long ChargeFor(UsageInfo usage, double cachedInputWeight) =>
        InputCharge(usage, cachedInputWeight) + usage.OutputTokens;

    private static long InputCharge(UsageInfo usage, double cachedInputWeight)
    {
        if (cachedInputWeight is < 0 or > 1 || double.IsNaN(cachedInputWeight))
            throw new ArgumentOutOfRangeException(nameof(cachedInputWeight), cachedInputWeight, "The weight is a fraction between 0 and 1.");

        long uncached = usage.InputTokens + usage.CacheCreationInputTokens;
        return uncached + Scale(usage.CacheReadInputTokens, cachedInputWeight);
    }

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
    /// <param name="cachedInputWeight">BudgetPolicy.CachedInputWeight. It applies to reported
    /// cache reads only: an estimate cannot know what will be served from the cache, so input
    /// charged from the estimate is charged in full.</param>
    public static long SettlementCharge(UsageInfo usage, long estimatedInputTokens, long estimatedOutputTokens, double cachedInputWeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(estimatedInputTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(estimatedOutputTokens);

        long input = usage.TotalInputTokens > 0 ? InputCharge(usage, cachedInputWeight) : estimatedInputTokens;
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
    /// What a call with unknown usage is charged when it is reconciled — once the run that made
    /// it has stopped, so nothing can report its usage any more. The request was sent, so its
    /// input is charged, in full, from the estimate it was admitted on (the host cannot know
    /// what the cache served). Its output never reached the host and cannot be estimated, so
    /// the output allowance and the margin are released. Never more than was reserved.
    /// </summary>
    public static long ReconciledCharge(long estimatedInputTokens, long reserved)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(estimatedInputTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(reserved);
        return Math.Min(estimatedInputTokens, reserved);
    }

    /// <summary>
    /// The call was never sent (refused upstream, or failed before the request left the host), so
    /// it cannot have incurred usage and its reservation is released in full.
    /// </summary>
    public static BudgetSnapshot Release(BudgetSnapshot budget, long reserved) => Settle(budget, reserved, charge: 0);

    /// <summary>Raising a cap changes the maximum, not the accounting history.</summary>
    public static BudgetSnapshot WithTaskCap(BudgetSnapshot budget, long? cap) => budget with { TaskCap = cap };
}
