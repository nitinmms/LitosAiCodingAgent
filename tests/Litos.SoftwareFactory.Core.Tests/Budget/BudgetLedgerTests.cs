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

        var decision = BudgetLedger.Admit(budget, expectedInputCharge: 15_000, Policy);

        var refused = Assert.IsType<Refused>(decision);
        Assert.Equal(RefusalReason.TaskBudget, refused.Reason);
        Assert.Equal(20_500, refused.Needed);   // 15,000 + 10%, and 4,000 of output
        Assert.Equal(20_000, refused.Remaining);
    }

    [Fact]
    public void ReservationFor_IsInputPlusItsMargin_PlusTheOutputAllowance()
    {
        // The margin covers estimator error, so it is on the input only: the output limit is
        // enforced by the provider and cannot be exceeded.
        Assert.Equal(16_500, BudgetLedger.InputReservation(15_000, Policy));
        Assert.Equal(20_500, BudgetLedger.ReservationFor(15_000, Policy));
    }

    // ---- A call is reserved for about what it will cost ----
    //
    // In the first real run a call that cost about 6,000 tokens held 55,000: its input was
    // reserved in full although nine tenths of it came from the cache at a tenth of the price,
    // and the whole output allowance had to fit although replies were a few hundred tokens.

    private static readonly BudgetPolicy Real = new() { OutputAllowanceTokens = 32_768, MinimumOutputTokens = 4_096, Margin = 0.10 };

    [Fact]
    public void ExpectedInputCharge_CountsTheExpectedCachedPartAtTheWeight()
    {
        // 24,000 estimated, of which the previous call's 23,000 should come from the cache.
        Assert.Equal(1_000 + 2_300, BudgetLedger.ExpectedInputCharge(24_000, 23_000, 0.10));
        Assert.Equal(24_000, BudgetLedger.ExpectedInputCharge(24_000, 0, 0.10));
        Assert.Equal(24_000, BudgetLedger.ExpectedInputCharge(24_000, 23_000, 1));
        Assert.Equal(1_000, BudgetLedger.ExpectedInputCharge(24_000, 23_000, 0));
    }

    [Theory]
    [InlineData(24_000, 30_000, 2_400)]   // cannot expect more from the cache than the request holds
    [InlineData(24_000, -5, 24_000)]
    [InlineData(0, 100, 0)]
    [InlineData(11, 11, 2)]               // the cached share rounds up
    public void ExpectedInputCharge_ClampsTheCachedPartToTheEstimate(long estimate, long cached, long expected) =>
        Assert.Equal(expected, BudgetLedger.ExpectedInputCharge(estimate, cached, 0.10));

    [Fact]
    public void ExpectedInputCharge_BadArguments_AreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BudgetLedger.ExpectedInputCharge(-1, 0, 0.10));
        Assert.Throws<ArgumentOutOfRangeException>(() => BudgetLedger.ExpectedInputCharge(1, 0, 1.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => BudgetLedger.ExpectedInputCharge(1, 0, -0.1));
    }

    [Fact]
    public void Admit_WithRoomToSpare_ReservesTheInputAndTheWholeOutputAllowance()
    {
        var budget = new BudgetSnapshot(TaskCap: 300_000, TaskUsed: 100_000, TaskReserved: 0);

        var admitted = Assert.IsType<Admitted>(BudgetLedger.Admit(budget, 10_000, Real));

        Assert.Equal(32_768, admitted.MaxOutputTokens);
        Assert.Equal(11_000 + 32_768, admitted.Reserved);
    }

    /// <summary>The call does not need room for the whole output allowance: it is sent with its
    /// output limited to what remains, so it still cannot spend past the cap.</summary>
    [Fact]
    public void Admit_WithLessThanTheOutputAllowanceLeft_IsStillAdmitted_WithItsOutputLimitedToWhatRemains()
    {
        var budget = new BudgetSnapshot(TaskCap: 300_000, TaskUsed: 280_000, TaskReserved: 0);

        var admitted = Assert.IsType<Admitted>(BudgetLedger.Admit(budget, 10_000, Real));

        Assert.Equal(9_000, admitted.MaxOutputTokens);     // 20,000 left less 11,000 for the input
        Assert.Equal(20_000, admitted.Reserved);           // everything that remains, and no more
        Assert.Equal(0, BudgetLedger.Reserve(budget, admitted.Reserved).TaskRemaining);
    }

    [Fact]
    public void Admit_NeedsRoomForTheInputAndTheMinimumOutput()
    {
        // 11,000 for the input and 4,096 of output: 15,096.
        var enough = new BudgetSnapshot(TaskCap: 15_096, TaskUsed: 0, TaskReserved: 0);
        var short1 = new BudgetSnapshot(TaskCap: 15_095, TaskUsed: 0, TaskReserved: 0);

        Assert.Equal(4_096, Assert.IsType<Admitted>(BudgetLedger.Admit(enough, 10_000, Real)).MaxOutputTokens);
        var refused = Assert.IsType<Refused>(BudgetLedger.Admit(short1, 10_000, Real));
        Assert.Equal((15_096L, 15_095L), (refused.Needed, refused.Remaining));
    }

    [Fact]
    public void Admit_TheTighterOfTheTaskAndTheQuota_LimitsTheOutput()
    {
        var budget = new BudgetSnapshot(
            TaskCap: 300_000, TaskUsed: 0, TaskReserved: 0, QuotaCap: 1_000_000, QuotaUsed: 975_000, QuotaReserved: 0);

        var admitted = Assert.IsType<Admitted>(BudgetLedger.Admit(budget, 10_000, Real));

        Assert.Equal(14_000, admitted.MaxOutputTokens); // the quota has 25,000 left
        Assert.Equal(25_000, admitted.Reserved);
    }

    [Fact]
    public void Admit_NoCap_GetsTheWholeOutputAllowance()
    {
        var admitted = Assert.IsType<Admitted>(BudgetLedger.Admit(new BudgetSnapshot(null, 9_000_000, 0), 10_000, Real));

        Assert.Equal(32_768, admitted.MaxOutputTokens);
    }

    /// <summary>The first real run's call 83: 23,500 estimated, 22,800 of it expected from the
    /// cache. It reserved 58,689 and cost 6,505.</summary>
    [Fact]
    public void Admit_AMostlyCachedCall_HoldsItsLikelyCost_NotTenTimesIt()
    {
        var input = BudgetLedger.ExpectedInputCharge(23_500, 22_800, 0.10);

        Assert.Equal(700 + 2_280, input);
        Assert.Equal(3_278, BudgetLedger.InputReservation(input, Real));
        // With 10,000 left the old rule paused the task; now the call goes ahead.
        var admitted = Assert.IsType<Admitted>(BudgetLedger.Admit(new BudgetSnapshot(300_000, 290_000, 0), input, Real));
        Assert.Equal(10_000 - 3_278, admitted.MaxOutputTokens);
    }

    [Fact]
    public void Policy_MinimumOutputAboveTheAllowance_UsesTheAllowance()
    {
        var policy = new BudgetPolicy { OutputAllowanceTokens = 1_000, MinimumOutputTokens = 4_096, Margin = 0 };

        var admitted = Assert.IsType<Admitted>(BudgetLedger.Admit(new BudgetSnapshot(1_100, 0, 0), 100, policy));

        Assert.Equal(1_000, admitted.MaxOutputTokens);
    }

    // ---- Reconciling a call whose usage will never be reported ----

    [Fact]
    public void ReconciledCharge_IsTheInputEstimate_AndReleasesTheOutputItNeverSaw()
    {
        // The stuck call of the first real run: 23,900 estimated input, 52,577 reserved.
        Assert.Equal(23_900, BudgetLedger.ReconciledCharge(estimatedInputTokens: 23_900, reserved: 52_577));

        var budget = BudgetLedger.Reserve(new BudgetSnapshot(1_400_000, 1_283_335, 0), 52_577);
        var after = BudgetLedger.Settle(budget, 52_577, BudgetLedger.ReconciledCharge(23_900, 52_577));
        Assert.Equal((1_307_235L, 0L), (after.TaskUsed, after.TaskReserved));
    }

    [Fact]
    public void ReconciledCharge_IsNeverMoreThanWasReserved()
    {
        // Input reserved at the cached rate: the full estimate is more than was ever held.
        Assert.Equal(3_278, BudgetLedger.ReconciledCharge(estimatedInputTokens: 23_500, reserved: 3_278));
        Assert.Equal(0, BudgetLedger.ReconciledCharge(0, 500));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    public void ReconciledCharge_NegativeFigures_AreRejected(long estimate, long reserved) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => BudgetLedger.ReconciledCharge(estimate, reserved));

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
        var budget = new BudgetSnapshot(TaskCap: 100_500, TaskUsed: 80_000, TaskReserved: 0);

        var admitted = Assert.IsType<Admitted>(BudgetLedger.Admit(budget, 15_000, Policy));

        Assert.Equal(20_500, admitted.Reserved);
        Assert.Equal(4_000, admitted.MaxOutputTokens); // the output cap the call is sent with
    }

    [Fact]
    public void Admit_OneTokenShort_IsRefused()
    {
        var budget = new BudgetSnapshot(TaskCap: 100_499, TaskUsed: 80_000, TaskReserved: 0);

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

        Assert.Equal(16_100, BudgetLedger.ChargeFor(usage, cachedInputWeight: 1));
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
        Assert.Equal(1_500, BudgetLedger.SettlementCharge(new UsageInfo(1_000, 500), estimatedInputTokens: 9_999, estimatedOutputTokens: 7_777, cachedInputWeight: 1));
    }

    [Fact]
    public void SettlementCharge_ReportedUsage_CountsCachedInput()
    {
        var usage = new UsageInfo(200, 900, 3_000, 12_000);

        Assert.Equal(16_100, BudgetLedger.SettlementCharge(usage, estimatedInputTokens: 1, estimatedOutputTokens: 1, cachedInputWeight: 1));
    }

    /// <summary>§9.4: a local server that reports no usage is charged the host's own estimate
    /// for both the request and the reply — never nothing, and never the request alone.</summary>
    [Fact]
    public void SettlementCharge_ProviderReportedNothing_ChargesTheEstimateForInputAndOutput()
    {
        var unreported = new UsageInfo(0, 0);

        Assert.True(BudgetLedger.IsUnreported(unreported));
        Assert.Equal(9_999 + 1_200, BudgetLedger.SettlementCharge(unreported, estimatedInputTokens: 9_999, estimatedOutputTokens: 1_200, cachedInputWeight: 1));
    }

    /// <summary>A server that reports completion tokens but no prompt tokens must still pay for
    /// its prompt.</summary>
    [Fact]
    public void SettlementCharge_OnlyOutputReported_ChargesTheInputEstimateToo()
    {
        var outputOnly = new UsageInfo(0, 500);

        Assert.Equal(9_999 + 500, BudgetLedger.SettlementCharge(outputOnly, estimatedInputTokens: 9_999, estimatedOutputTokens: 1_200, cachedInputWeight: 1));
    }

    [Fact]
    public void SettlementCharge_OnlyInputReported_ChargesTheOutputEstimateToo()
    {
        var inputOnly = new UsageInfo(8_000, 0);

        Assert.Equal(8_000 + 1_200, BudgetLedger.SettlementCharge(inputOnly, estimatedInputTokens: 9_999, estimatedOutputTokens: 1_200, cachedInputWeight: 1));
    }

    /// <summary>A fully cached request reports zero uncached input; the cached counts are its
    /// input, and they are what is charged — not the estimate.</summary>
    [Fact]
    public void SettlementCharge_FullyCachedInput_IsReportedInput_NotReplacedByTheEstimate()
    {
        var cached = new UsageInfo(0, 300, CacheReadInputTokens: 12_000);

        Assert.Equal(12_300, BudgetLedger.SettlementCharge(cached, estimatedInputTokens: 99_999, estimatedOutputTokens: 1, cachedInputWeight: 1));
    }

    [Fact]
    public void SettlementCharge_EmptyReplyAndNoReportedOutput_ChargesNoOutput()
    {
        Assert.Equal(8_000, BudgetLedger.SettlementCharge(new UsageInfo(8_000, 0), estimatedInputTokens: 9_999, estimatedOutputTokens: 0, cachedInputWeight: 1));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    public void SettlementCharge_NegativeEstimate_IsRejected(long input, long output)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BudgetLedger.SettlementCharge(new UsageInfo(1, 1), input, output, cachedInputWeight: 1));
    }

    /// <summary>The whole path a local server takes: admitted, reserved, settled with no usage
    /// reported. The allowance must go down by what the call plausibly cost.</summary>
    [Fact]
    public void UnreportedCall_SettlesAgainstTheAllowance_SoRepeatedCallsCannotRunForFree()
    {
        var budget = new BudgetSnapshot(TaskCap: 60_000, TaskUsed: 0, TaskReserved: 0);
        var calls = 0;

        while (BudgetLedger.Admit(budget, 10_000, Policy) is Admitted admitted)
        {
            budget = BudgetLedger.Reserve(budget, admitted.Reserved);
            var charge = BudgetLedger.SettlementCharge(new UsageInfo(0, 0), estimatedInputTokens: 10_000, estimatedOutputTokens: 3_000, cachedInputWeight: 1);
            budget = BudgetLedger.Settle(budget, admitted.Reserved, charge);
            calls++;
        }

        Assert.Equal(4, calls);
        Assert.Equal(52_000, budget.TaskUsed);
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
            budget = BudgetLedger.Settle(budget, admitted.Reserved, BudgetLedger.ChargeFor(new UsageInfo(10_000, 3_000), cachedInputWeight: 1));
            calls++;
        }

        Assert.Equal(4, calls);                 // 4 × 13,000 = 52,000; a fifth needs 15,400 of the 8,000 left
        Assert.Equal(52_000, budget.TaskUsed);
        Assert.True(budget.TaskUsed <= budget.TaskCap);
    }

    // ---- Cached input counts at a discount ----

    [Fact]
    public void ChargeFor_CountsCacheReadsAtTheWeight_AndEverythingElseInFull()
    {
        // 200 new, 3,000 written to the cache, 12,000 read from it, 900 output.
        var usage = new UsageInfo(200, 900, 3_000, 12_000);

        Assert.Equal(200 + 3_000 + 1_200 + 900, BudgetLedger.ChargeFor(usage, cachedInputWeight: 0.10));
        Assert.Equal(200 + 3_000 + 6_000 + 900, BudgetLedger.ChargeFor(usage, cachedInputWeight: 0.5));
        Assert.Equal(200 + 3_000 + 900, BudgetLedger.ChargeFor(usage, cachedInputWeight: 0));
        Assert.Equal(16_100, BudgetLedger.ChargeFor(usage, cachedInputWeight: 1));
    }

    [Theory]
    [InlineData(1, 1)]      // a fraction of a token is still a token: never rounded down to free
    [InlineData(9, 1)]
    [InlineData(10, 1)]
    [InlineData(11, 2)]
    [InlineData(22_784, 2_279)]
    public void ChargeFor_RoundsTheCachedShareUp(int cacheRead, long expected) =>
        Assert.Equal(expected, BudgetLedger.ChargeFor(new UsageInfo(0, 0, CacheReadInputTokens: cacheRead), cachedInputWeight: 0.10));

    [Fact]
    public void ChargeFor_NoCachedInput_IsTheSameAtAnyWeight()
    {
        var usage = new UsageInfo(8_000, 500);

        Assert.Equal(8_500, BudgetLedger.ChargeFor(usage, cachedInputWeight: 0.10));
        Assert.Equal(8_500, BudgetLedger.ChargeFor(usage, cachedInputWeight: 1));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    public void ChargeFor_AWeightOutsideZeroToOne_IsRejected(double weight)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BudgetLedger.ChargeFor(new UsageInfo(1, 1), weight));
        Assert.Throws<ArgumentOutOfRangeException>(() => BudgetLedger.SettlementCharge(new UsageInfo(1, 1), 1, 1, weight));
    }

    [Fact]
    public void SettlementCharge_DiscountsReportedCacheReads()
    {
        var usage = new UsageInfo(783, 134, 0, 22_784);

        Assert.Equal(783 + 2_279 + 134, BudgetLedger.SettlementCharge(usage, 23_000, 100, cachedInputWeight: 0.10));
    }

    /// <summary>A fully cached request is still reported input: it is discounted, not replaced
    /// by the estimate.</summary>
    [Fact]
    public void SettlementCharge_FullyCachedInput_IsDiscounted_NotReplacedByTheEstimate()
    {
        var cached = new UsageInfo(0, 300, CacheReadInputTokens: 12_000);

        Assert.Equal(1_200 + 300, BudgetLedger.SettlementCharge(cached, 99_999, 1, cachedInputWeight: 0.10));
    }

    /// <summary>An estimate cannot know what the cache will serve, so input the provider did not
    /// report is charged in full.</summary>
    [Fact]
    public void SettlementCharge_InputChargedFromTheEstimate_IsNotDiscounted()
    {
        Assert.Equal(9_999 + 500, BudgetLedger.SettlementCharge(new UsageInfo(0, 500), 9_999, 1_200, cachedInputWeight: 0.10));
    }

    /// <summary>The first real run in aggregate: 60,038 new input, 877,952 read from the cache,
    /// 55,617 output. In full that was 993,607; discounted it is about a fifth of that.</summary>
    [Fact]
    public void ChargeFor_TheFirstRealRunsTotals()
    {
        var run = new UsageInfo(60_038, 55_617, 0, 877_952);

        Assert.Equal(993_607, BudgetLedger.ChargeFor(run, cachedInputWeight: 1));
        Assert.Equal(203_451, BudgetLedger.ChargeFor(run, cachedInputWeight: 0.10));
    }

    [Fact]
    public void Policy_Defaults()
    {
        var policy = new BudgetPolicy();

        Assert.Equal(0.10, policy.Margin);
        Assert.Equal(0.10, policy.CachedInputWeight);
        Assert.Equal(4_096, policy.MinimumOutputTokens);
        Assert.Equal(TimeSpan.FromMinutes(4), policy.CacheWindow);
        // Room for a reasoning model's thinking and its reply: 8,192 was not enough in practice.
        Assert.Equal(32_768, policy.OutputAllowanceTokens);
    }

    [Fact]
    public void UsageStatus_HasTheLedgerStates_InAnOrderThatMustNotChange()
    {
        // Stored by number: a new state goes at the end.
        Assert.Equal([UsageStatus.Reserved, UsageStatus.Settled, UsageStatus.Unknown, UsageStatus.Estimated], Enum.GetValues<UsageStatus>());
    }
}
