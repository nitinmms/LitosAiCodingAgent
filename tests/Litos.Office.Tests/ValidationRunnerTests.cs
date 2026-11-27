using Litos.Office.Data;
using Litos.Office.Exec;
using Litos.Office.Plan;

namespace Litos.Office.Tests;

/// <summary>
/// The publish gate (design §2 ValidationRunner). Each rule passes on a correct run and fails on a
/// broken one; "independence" tests corrupt the executor's result on purpose and expect the checks
/// to catch it, because the checks recompute from the source rather than trusting the executor.
/// </summary>
public class ValidationRunnerTests
{
    private static readonly WorkflowPlan Plan = TestPlans.Sales();

    private static ValidationReport Validate(Table source, ExecResult? result = null, Table? writtenBack = null) =>
        ValidationRunner.Run(Plan, ValidationRunner.DefaultFor(Plan), source, result ?? PlanExecutor.Execute(Plan, source), writtenBack);

    private static ValidationItem Item(ValidationReport report, string id, ValidationLevel level) =>
        Assert.Single(report.Items, i => i.Id == id && i.Level == level);

    [Fact]
    public void ACorrectRun_PassesEveryCheck()
    {
        var report = Validate(Tables.SalesRaw());

        Assert.False(report.HasFailures, string.Join("; ", report.Items.Where(i => i.Level == ValidationLevel.Fail).Select(i => i.Message)));
        Assert.Contains(report.Items, i => i.Id == ValidationRunner.SumReconciliation && i.Message.Contains("600"));
        Assert.Contains(report.Items, i => i.Id == ValidationRunner.RowAccountingRule && i.Message.Contains("3 row(s) included + 1 excluded = 4"));
    }

    [Fact]
    public void DefaultFor_DerivesTheChecksFromThePlan()
    {
        var ids = ValidationRunner.DefaultFor(Plan).Rules.Select(r => r.Id).ToList();

        Assert.Equal(
        [
            ValidationRunner.RequiredColumns, ValidationRunner.ValidNumbers, ValidationRunner.KnownValues,
            ValidationRunner.BlankKeys, ValidationRunner.NoDuplicateGroups, ValidationRunner.RowAccountingRule,
            ValidationRunner.SumReconciliation, ValidationRunner.OutputNotEmpty, ValidationRunner.Roundtrip,
        ], ids);
        Assert.Equal("Status", ValidationRunner.DefaultFor(Plan).Rules.Single(r => r.Id == ValidationRunner.KnownValues).Column);
    }

    [Fact]
    public void AMissingColumn_FailsWithTheLayoutMessage()
    {
        var report = Validate(Tables.Of(["Region", "Amount"], ["North", 1m]));

        Assert.Contains("\"Status\" is missing", Item(report, ValidationRunner.RequiredColumns, ValidationLevel.Fail).Message);
    }

    [Fact]
    public void AnUnknownStatus_Fails_KnownValues()
    {
        var report = Validate(Tables.Of(["Region", "Amount", "Status"], ["North", 1m, "Completed"], ["North", 1m, "Refunded"]));

        Assert.Contains("\"Refunded\" (1)", Item(report, ValidationRunner.KnownValues, ValidationLevel.Fail).Message);
        Assert.True(report.HasFailures);
    }

    [Fact]
    public void ANonNumberAmount_Fails_ValidNumbers()
    {
        var report = Validate(Tables.Of(["Region", "Amount", "Status"], ["North", "TBC", "Completed"]));

        Assert.Contains("\"Amount\"", Item(report, ValidationRunner.ValidNumbers, ValidationLevel.Fail).Message);
    }

    [Fact]
    public void ABlankRegion_Fails_BlankKeys()
    {
        var report = Validate(Tables.Of(["Region", "Amount", "Status"], [null, 1m, "Completed"], ["North", 1m, "Completed"]));

        Item(report, ValidationRunner.BlankKeys, ValidationLevel.Fail);
    }

    [Fact]
    public void ACorruptedTotal_IsCaught_BecauseTheCheckRecomputesFromTheSource()
    {
        var honest = PlanExecutor.Execute(Plan, Tables.SalesRaw());
        var corrupted = honest with
        {
            Output = Tables.Of(["Region", "Total Sales"], ["North", 450m], ["South", 200m]),
        };

        var report = Validate(Tables.SalesRaw(), corrupted);

        Assert.Contains("650", Item(report, ValidationRunner.SumReconciliation, ValidationLevel.Fail).Message);
    }

    [Fact]
    public void ACorruptedGrandTotal_IsCaught()
    {
        var honest = PlanExecutor.Execute(Plan, Tables.SalesRaw());
        var corrupted = honest with { GrandTotal = [CellValue.OfText("Grand Total"), CellValue.OfNumber(601m)] };

        Assert.Contains("grand total", Item(Validate(Tables.SalesRaw(), corrupted), ValidationRunner.SumReconciliation, ValidationLevel.Fail).Message);
    }

    [Fact]
    public void CorruptedRowAccounting_IsCaught()
    {
        var honest = PlanExecutor.Execute(Plan, Tables.SalesRaw());

        var wrongSum = Validate(Tables.SalesRaw(), honest with { Accounting = new RowAccounting(4, 3, 2) });
        var wrongCount = Validate(Tables.SalesRaw(), honest with { Accounting = new RowAccounting(4, 4, 0) });

        Item(wrongSum, ValidationRunner.RowAccountingRule, ValidationLevel.Fail);
        Assert.Contains("checking the rules again gives 3", Item(wrongCount, ValidationRunner.RowAccountingRule, ValidationLevel.Fail).Message);
    }

    [Fact]
    public void DuplicateGroups_AreCaught()
    {
        var honest = PlanExecutor.Execute(Plan, Tables.SalesRaw());
        var corrupted = honest with { Output = Tables.Of(["Region", "Total Sales"], ["North", 400m], ["North", 0m], ["South", 200m]) };

        Assert.Contains("North", Item(Validate(Tables.SalesRaw(), corrupted), ValidationRunner.NoDuplicateGroups, ValidationLevel.Fail).Message);
    }

    [Fact]
    public void ExecutorIssues_AreFailures_AndWarningsAreWarnings()
    {
        var report = Validate(Tables.Of(["Region", "Amount", "Status"], ["North", 1m, "Cancelled"]));

        Assert.False(report.HasFailures);
        Assert.Contains(report.Items, i => i.Id == ValidationRunner.OutputNotEmpty && i.Level == ValidationLevel.Warn);
    }

    [Fact]
    public void Roundtrip_PassesWhenTheFileReadsBackTheSame_AndFailsOtherwise()
    {
        var result = PlanExecutor.Execute(Plan, Tables.SalesRaw());
        var same = Tables.Of(["Region", "Total Sales"], ["North", 400m], ["South", 200m], ["Grand Total", 600m]);
        var different = Tables.Of(["Region", "Total Sales"], ["North", 400m], ["South", 201m], ["Grand Total", 600m]);
        var shorter = Tables.Of(["Region", "Total Sales"], ["North", 400m]);

        Item(Validate(Tables.SalesRaw(), result, same), ValidationRunner.Roundtrip, ValidationLevel.Pass);
        Assert.Contains("201", Item(Validate(Tables.SalesRaw(), result, different), ValidationRunner.Roundtrip, ValidationLevel.Fail).Message);
        Item(Validate(Tables.SalesRaw(), result, shorter), ValidationRunner.Roundtrip, ValidationLevel.Fail);
    }

    [Fact]
    public void AnEditedValidationFile_WithAnUnknownCheck_Fails()
    {
        var spec = new ValidationSpec(1, [new ValidationRule("trust_me")]);

        var report = ValidationRunner.Run(Plan, spec, Tables.SalesRaw(), PlanExecutor.Execute(Plan, Tables.SalesRaw()));

        Assert.Contains("trust_me", Item(report, "trust_me", ValidationLevel.Fail).Message);
    }

    [Fact]
    public void ACountWithoutAColumn_IsReconciledAgainstIncludedRows()
    {
        var plan = TestPlans.Sales(
            new FilterStep("Status", FilterOperator.Equals, FilterValueType.Text, Value: "Completed"),
            new GroupAggregateStep(["Region"], [new AggregateSpec(AggregateFunction.Count, "Orders")]),
            TestPlans.Write(("Region", "Region"), ("Orders", "Orders")));
        var result = PlanExecutor.Execute(plan, Tables.SalesRaw());

        var report = ValidationRunner.Run(plan, ValidationRunner.DefaultFor(plan), Tables.SalesRaw(), result);

        Assert.Contains("counts 3 row(s)", Item(report, ValidationRunner.SumReconciliation, ValidationLevel.Pass).Message);
    }
}
