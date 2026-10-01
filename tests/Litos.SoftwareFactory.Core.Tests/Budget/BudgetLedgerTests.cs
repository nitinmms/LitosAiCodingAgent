using Litos.Agent.Streaming;
using Litos.SoftwareFactory.Core.Budget;

namespace Litos.SoftwareFactory.Core.Tests.Budget;

public class BudgetLedgerTests
{
    private static readonly BudgetPolicy Policy = new() { OutputAllowanceTokens = 4_000, Margin = 0.10 };

    /// <summary>The worked example in ReadMe_LitosSoftwareFactory_V1.md §9.2.</summary>
    [Fact]
    public void Admit_BlueprintExample_RefusesBeforeSpendingAnything()
    {
        var budget = new BudgetSnapshot(TaskCap: 100_000, TaskUsed: 80_000, TaskReserved: 0);

        var decision = BudgetLedger.Admit(budget, estimatedInputTokens: 15_000, Policy);

        var refused = Assert.IsType<Refused>(decision);
        Assert.Equal(RefusalReason.TaskBudget, refused.Reason);
        Assert.Equal(20_900, refused.Needed);   // (15,000 + 4,000) + 10%
        Assert.Equal(20_000, refused.Remaining);
    }

    [Fact]
    public void ReservationFor_IsInputPlusOutputAllowancePlusMargin()
    {
        Assert.Equal(20_900, BudgetLedger.ReservationFor(15_000, Policy));
    }

    [Fact]
    public void ReservationFor_RoundsUp_SoAFractionalTokenIsNeverUnderReserved()
    {
        var policy = new BudgetPolicy { OutputAllowanceTokens = 0, Margin = 0.10 };

        Assert.Equal(2, BudgetLedger.ReservationFor(1, policy)); // 1.1 → 2
    }

    [Fact]
    public void ReservationFor_NegativeEstimate_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BudgetLedger.ReservationFor(-1, Policy));
    }

    [Fact]
    public void Admit_ExactlyFits_IsAdmitted()
    {
        var budget = new BudgetSnapshot(TaskCap: 100_900, TaskUsed: 80_000, TaskReserved: 0);

        var admitted = Assert.IsType<Admitted>(BudgetLedger.Admit(budget, 15_000, Policy));

        Assert.Equal(20_900, admitted.Reserved);
        Assert.Equal(4_000, admitted.MaxOutputTokens); // the output cap the call is sent with
    }

    [Fact]
    public void Admit_OneTokenShort_IsRefused()
    {
        var budget = new BudgetSnapshot(TaskCap: 100_899, TaskUsed: 80_000, TaskReserved: 0);

        Assert.IsType<Refused>(BudgetLedger.Admit(budget, 15_000, Policy));
    }

    [Fact]
    public void Admit_OpenReservationsCountAgainstTheAllowance()
    {
        // Two calls in flight for one task must not each see the full remainder.
        var budget = new BudgetSnapshot(TaskCap: 100_000, TaskUsed: 50_000, TaskReserved: 30_000);

        var refused = Assert.IsType<Refused>(BudgetLedger.Admit(budget, 15_000, Policy));

        Assert.Equal(20_000, refused.Remaining);
    }

    [Fact]
    public void Admit_NoCap_IsAlwaysAdmitted()
    {
        var budget = new BudgetSnapshot(TaskCap: null, TaskUsed: 5_000_000, TaskReserved: 0);

        Assert.IsType<Admitted>(BudgetLedger.Admit(budget, 900_000, Policy));
        Assert.Null(budget.TaskRemaining);
    }

    [Fact]
    public void Admit_FitsTheTaskButNotTheUsersQuota_IsRefusedForQuota()
    {
        var budget = new BudgetSnapshot(
            TaskCap: 500_000, TaskUsed: 0, TaskReserved: 0, QuotaCap: 1_000_000, QuotaUsed: 990_000, QuotaReserved: 0);

        var refused = Assert.IsType<Refused>(BudgetLedger.Admit(budget, 15_000, Policy));

        Assert.Equal(RefusalReason.UserQuota, refused.Reason);
        Assert.Equal(10_000, refused.Remaining);
    }

    [Fact]
    public void Admit_FitsNeither_IsReportedAgainstTheTask()
    {
        var budget = new BudgetSnapshot(
            TaskCap: 10_000, TaskUsed: 0, TaskReserved: 0, QuotaCap: 10_000, QuotaUsed: 0, QuotaReserved: 0);

        Assert.Equal(RefusalReason.TaskBudget, Assert.IsType<Refused>(BudgetLedger.Admit(budget, 15_000, Policy)).Reason);
    }

    [Fact]
    public void Admit_QuotaReservationsCount()
    {
        var budget = new BudgetSnapshot(null, 0, 0, QuotaCap: 100_000, QuotaUsed: 50_000, QuotaReserved: 40_000);

        Assert.Equal(RefusalReason.UserQuota, Assert.IsType<Refused>(BudgetLedger.Admit(budget, 15_000, Policy)).Reason);
    }

    [Fact]
    public void Remaining_NeverGoesNegative_AfterAnOverrun()
    {
        var budget = new BudgetSnapshot(TaskCap: 100_000, TaskUsed: 104_000, TaskReserved: 0);

        Assert.Equal(0, budget.TaskRemaining);
        Assert.IsType<Refused>(BudgetLedger.Admit(budget, 1, Policy));
    }

    [Fact]
    public void Reserve_AddsToBothTheTaskAndTheQuota()
    {
        var budget = new BudgetSnapshot(100_000, 10_000, 0, QuotaCap: 500_000, QuotaUsed: 20_000, QuotaReserved: 1_000);

        var reserved = BudgetLedger.Reserve(budget, 20_900);

        Assert.Equal(20_900, reserved.TaskReserved);
        Assert.Equal(21_900, reserved.QuotaReserved);
        Assert.Equal(10_000, reserved.TaskUsed); // nothing is "used" until settled
    }

    [Fact]
    public void ChargeFor_CountsCachedInput_AndDoesNotAddReasoningTwice()
    {
        // Input 200 + cache creation 3,000 + cache read 12,000 + output 900. The 640 reasoning
        // tokens are already inside the 900.
        var usage = new UsageInfo(200, 900, 3_000, 12_000, ReasoningTokens: 640);

        Assert.Equal(16_100, BudgetLedger.ChargeFor(usage));
    }

    [Fact]
    public void Settle_ReplacesTheReservationWithActualUsage_AndReleasesTheRest()
    {
        var budget = BudgetLedger.Reserve(new BudgetSnapshot(100_000, 80_000, 0, 500_000, 80_000, 0), 18_000);

        var settled = BudgetLedger.Settle(budget, reserved: 18_000, charge: 15_500);

        Assert.Equal(95_500, settled.TaskUsed);
        Assert.Equal(0, settled.TaskReserved);
        Assert.Equal(4_500, settled.TaskRemaining);
        Assert.Equal(95_500, settled.QuotaUsed);
        Assert.Equal(0, settled.QuotaReserved);
    }

    [Fact]
    public void Settle_UsageAboveTheReservation_IsChargedInFull()
    {
        var budget = BudgetLedger.Reserve(new BudgetSnapshot(100_000, 0, 0), 10_000);

        var settled = BudgetLedger.Settle(budget, reserved: 10_000, charge: 12_345);

        Assert.Equal(12_345, settled.TaskUsed);
        Assert.Equal(0, settled.TaskReserved);
    }

    [Fact]
    public void Settle_LeavesOtherOpenReservationsInPlace()
    {
        var budget = BudgetLedger.Reserve(BudgetLedger.Reserve(new BudgetSnapshot(100_000, 0, 0), 10_000), 7_000);

        var settled = BudgetLedger.Settle(budget, reserved: 10_000, charge: 8_000);

        Assert.Equal(7_000, settled.TaskReserved);
        Assert.Equal(8_000, settled.TaskUsed);
    }

    /// <summary>§9.2 step 7 and acceptance scenario 8: a call with unknown usage cannot silently
    /// refund the allowance.</summary>
    [Fact]
    public void KeepUnknown_KeepsTheReservationCharged()
    {
        var budget = BudgetLedger.Reserve(new BudgetSnapshot(100_000, 80_000, 0), 18_000);

        var after = BudgetLedger.KeepUnknown(budget);

        Assert.Equal(18_000, after.TaskReserved);
        Assert.Equal(2_000, after.TaskRemaining);
        Assert.IsType<Refused>(BudgetLedger.Admit(after, 1_000, Policy));
    }

    [Fact]
    public void Release_ReturnsTheWholeReservation_WhenTheCallWasNeverSent()
    {
        var before = new BudgetSnapshot(100_000, 80_000, 0, 500_000, 80_000, 0);

        var after = BudgetLedger.Release(BudgetLedger.Reserve(before, 18_000), 18_000);

        Assert.Equal(before, after);
    }

    [Fact]
    public void SettlementCharge_ReportedUsage_IsChargedAsReported()
    {
        Assert.Equal(1_500, BudgetLedger.SettlementCharge(new UsageInfo(1_000, 500), estimatedInputTokens: 9_999));
    }

    /// <summary>§9.4: a local server that reports no usage is charged the host's own estimate,
    /// never nothing.</summary>
    [Fact]
    public void SettlementCharge_ProviderReportedNothing_ChargesTheEstimate()
    {
        var unreported = new UsageInfo(0, 0);

        Assert.True(BudgetLedger.IsUnreported(unreported));
        Assert.Equal(9_999, BudgetLedger.SettlementCharge(unreported, estimatedInputTokens: 9_999));
    }

    [Fact]
    public void IsUnreported_FullyCachedInput_IsStillReportedUsage()
    {
        // InputTokens alone is zero on a fully cached request; that is not "no usage".
        Assert.False(BudgetLedger.IsUnreported(new UsageInfo(0, 0, CacheReadInputTokens: 12_000)));
        Assert.False(BudgetLedger.IsUnreported(new UsageInfo(0, 5)));
    }

    /// <summary>Acceptance scenario 7: raising a cap preserves prior usage.</summary>
    [Fact]
    public void WithTaskCap_ChangesTheMaximum_NotTheHistory()
    {
        var paused = new BudgetSnapshot(TaskCap: 100_000, TaskUsed: 98_000, TaskReserved: 0);

        var raised = BudgetLedger.WithTaskCap(paused, 200_000);

        Assert.Equal(98_000, raised.TaskUsed);
        Assert.Equal(102_000, raised.TaskRemaining);
        Assert.IsType<Admitted>(BudgetLedger.Admit(raised, 15_000, Policy));
    }

    [Fact]
    public void WithTaskCap_Null_RemovesTheCap()
    {
        var raised = BudgetLedger.WithTaskCap(new BudgetSnapshot(100_000, 98_000, 0), null);

        Assert.Null(raised.TaskRemaining);
    }

    [Fact]
    public void FullCycle_AdmitReserveSettle_NeverExceedsTheCapWhenEstimatesHold()
    {
        var budget = new BudgetSnapshot(TaskCap: 60_000, TaskUsed: 0, TaskReserved: 0);
        var calls = 0;

        while (BudgetLedger.Admit(budget, 10_000, Policy) is Admitted admitted)
        {
            budget = BudgetLedger.Reserve(budget, admitted.Reserved);
            budget = BudgetLedger.Settle(budget, admitted.Reserved, BudgetLedger.ChargeFor(new UsageInfo(10_000, 3_000)));
            calls++;
        }

        Assert.Equal(4, calls);                 // 4 × 13,000 = 52,000; a fifth needs 15,400 of the 8,000 left
        Assert.Equal(52_000, budget.TaskUsed);
        Assert.True(budget.TaskUsed <= budget.TaskCap);
    }

    [Fact]
    public void Policy_Defaults()
    {
        var policy = new BudgetPolicy();

        Assert.Equal(0.10, policy.Margin);
        Assert.Equal(8_192, policy.OutputAllowanceTokens);
    }

    [Fact]
    public void UsageStatus_HasTheThreeLedgerStates()
    {
        Assert.Equal([UsageStatus.Reserved, UsageStatus.Settled, UsageStatus.Unknown], Enum.GetValues<UsageStatus>());
    }
}
