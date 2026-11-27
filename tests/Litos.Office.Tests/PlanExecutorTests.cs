using Litos.Office.Data;
using Litos.Office.Exec;
using Litos.Office.Plan;

namespace Litos.Office.Tests;

/// <summary>
/// The deterministic executor (design §2 Exec/): the spec's §6 example and fresh-input fixture,
/// exact decimals, every operator, and the cases where it must stop rather than guess.
/// </summary>
public class PlanExecutorTests
{
    private static readonly FilterStep Completed =
        new("Status", FilterOperator.Equals, FilterValueType.Text, Value: "Completed", KnownValues: ["Completed", "Cancelled"]);

    private static readonly GroupAggregateStep SumByRegion =
        new(["Region"], [new AggregateSpec(AggregateFunction.Sum, "Total Sales", "Amount")]);

    private static readonly SortStep ByRegion = new([new SortKey("Region", SortDirection.Ascending)]);

    private static readonly WriteWorkbookStep WriteSummary = TestPlans.Write(("Region", "Region"), ("Total Sales", "Total Sales"));

    private static ExecResult Run(Table source, params StepSpec[] steps) =>
        PlanExecutor.Execute(TestPlans.Sales(steps), source);

    private static ExecResult RunWith(Table source, string[] required, params StepSpec[] steps)
    {
        var plan = TestPlans.Sales(steps);
        return PlanExecutor.Execute(plan with { Inputs = [plan.Inputs[0] with { RequiredColumns = required }] }, source);
    }

    [Fact]
    public void TheSpecExample_ExcludesCancelled_AndSumsByRegion()
    {
        var result = Run(Tables.SalesRaw(), Completed, SumByRegion, ByRegion, WriteSummary);

        Assert.True(result.Ok, string.Join("; ", result.Issues));
        Assert.Equal(["Region", "Total Sales"], result.Output.ColumnNames);
        Assert.Equal([["North", "400"], ["South", "200"]], Tables.Display(result.Output));
        Assert.Equal("Grand Total", result.GrandTotal![0].Text);
        Assert.Equal(600m, result.GrandTotal[1].Number);
        Assert.Equal(new RowAccounting(4, 3, 1), result.Accounting);
    }

    [Fact]
    public void TheFreshInput_GivesTheSpecsExpectedGroups()
    {
        // Spec §6: East 50, North 120, South 80; grand total 250; 3 included and 1 excluded.
        var result = Run(Tables.SalesFresh(), Completed, SumByRegion, ByRegion, WriteSummary);

        Assert.True(result.Ok);
        Assert.Equal([["East", "50"], ["North", "120"], ["South", "80"]], Tables.Display(result.Output));
        Assert.Equal(250m, result.GrandTotal![1].Number);
        Assert.Equal(new RowAccounting(4, 3, 1), result.Accounting);
    }

    [Fact]
    public void Sums_AreExactDecimals()
    {
        var source = Tables.Of(["Region", "Amount", "Status"], ["North", 0.1m, "Completed"], ["North", 0.2m, "Completed"]);

        Assert.Equal(0.3m, Run(source, Completed, SumByRegion, WriteSummary).Output.Rows[0][1].Number);
    }

    [Fact]
    public void AnUnknownFilterValue_StopsTheRun_NamingTheValueAndRows()
    {
        var source = Tables.Of(["Region", "Amount", "Status"],
            ["North", 10m, "Completed"], ["South", 5m, "Refunded"], ["East", 5m, "Refunded"]);

        var result = Run(source, Completed, SumByRegion, WriteSummary);

        var issue = Assert.Single(result.Issues);
        Assert.Contains("\"Refunded\"", issue.Message);
        Assert.Contains("2 data rows (2, 3)", issue.Message);
        Assert.Equal("steps[0]", issue.Path);
    }

    [Fact]
    public void ANonNumberInASummedColumn_StopsTheRun_NotTreatedAsZero()
    {
        var source = Tables.Of(["Region", "Amount", "Status"],
            ["North", 10m, "Completed"], ["North", "n/a", "Completed"], ["North", null, "Completed"]);

        var result = Run(source, Completed, SumByRegion, WriteSummary);

        Assert.Contains(result.Issues, i => i.Message.Contains("\"Amount\"") && i.Message.Contains("2 data rows"));
    }

    [Fact]
    public void ABlankGroupKey_StopsTheRun_UnlessTheSkillKeepsBlanks()
    {
        var source = Tables.Of(["Region", "Amount", "Status"], ["North", 10m, "Completed"], [null, 5m, "Completed"]);

        var strict = Run(source, Completed, SumByRegion, WriteSummary);
        Assert.Contains(strict.Issues, i => i.Message.Contains("\"Region\""));

        var keepPlan = TestPlans.Sales(Completed, SumByRegion, WriteSummary) with { Policies = PlanPolicies.Strict with { BlankKey = PolicyAction.Keep } };
        var kept = PlanExecutor.Execute(keepPlan, source);
        Assert.True(kept.Ok);
        Assert.Equal(2, kept.Output.Rows.Count);
    }

    [Fact]
    public void AMissingColumn_IsOneClearIssue_NotACrash()
    {
        var source = Tables.Of(["Region", "Amount"], ["North", 10m]);

        var result = Run(source, Completed, SumByRegion, WriteSummary);

        var issue = Assert.Single(result.Issues);
        Assert.Contains("\"Status\" is missing", issue.Message);
        Assert.Empty(result.Output.Rows);
    }

    public static TheoryData<FilterStep, string[]> Operators => new()
    {
        { new FilterStep("Status", FilterOperator.NotEquals, FilterValueType.Text, Value: "Cancelled"), ["a", "b", "d"] },
        { new FilterStep("Status", FilterOperator.In, FilterValueType.Text, Values: ["Completed", "Shipped"]), ["a", "b"] },
        { new FilterStep("Status", FilterOperator.NotIn, FilterValueType.Text, Values: ["Completed"]), ["b", "c", "d"] },
        { new FilterStep("Amount", FilterOperator.Gt, FilterValueType.Number, Value: "100"), ["b", "c"] },
        { new FilterStep("Amount", FilterOperator.Gte, FilterValueType.Number, Value: "100"), ["a", "b", "c"] },
        { new FilterStep("Amount", FilterOperator.Lt, FilterValueType.Number, Value: "100.5"), ["a", "d"] },
        { new FilterStep("Amount", FilterOperator.Lte, FilterValueType.Number, Value: "50"), ["d"] },
        { new FilterStep("Amount", FilterOperator.Equals, FilterValueType.Number, Value: "200.00"), ["b"] },
        { new FilterStep("Booked", FilterOperator.Gte, FilterValueType.Date, Value: "2026-11-15"), ["c", "d"] },
        { new FilterStep("Booked", FilterOperator.Lt, FilterValueType.Date, Value: "2026-11-15"), ["a", "b"] },
    };

    [Theory]
    [MemberData(nameof(Operators))]
    public void EveryOperator_KeepsTheRightRows(FilterStep filter, string[] expectedIds)
    {
        var source = Tables.Of(["Id", "Amount", "Status", "Booked"],
            ["a", 100m, "Completed", new DateTime(2026, 11, 1)],
            ["b", 200m, "Shipped", new DateTime(2026, 11, 14, 23, 0, 0)],
            ["c", 150m, "Cancelled", new DateTime(2026, 11, 15)],
            ["d", 50m, "Pending", new DateTime(2026, 12, 1)]);

        var result = RunWith(source, ["Id", "Amount", "Status", "Booked"], filter, TestPlans.Write(("Id", "Id")) with { IncludeGrandTotal = false });

        Assert.True(result.Ok, string.Join("; ", result.Issues));
        Assert.Equal(expectedIds, result.Output.Rows.Select(r => r[0].Text));
    }

    [Fact]
    public void BlankFilterCells_AreLeftOutByEquals_AndKeptByNotEquals_WithAWarning()
    {
        var source = Tables.Of(["Id", "Status"], ["a", "Completed"], ["b", null]);
        var write = TestPlans.Write(("Id", "Id")) with { IncludeGrandTotal = false };

        var equals = RunWith(source, ["Id", "Status"], new FilterStep("Status", FilterOperator.Equals, FilterValueType.Text, Value: "Completed"), write);
        var notEquals = RunWith(source, ["Id", "Status"], new FilterStep("Status", FilterOperator.NotEquals, FilterValueType.Text, Value: "Cancelled"), write);

        Assert.Equal(["a"], equals.Output.Rows.Select(r => r[0].Text));
        Assert.Equal(["a", "b"], notEquals.Output.Rows.Select(r => r[0].Text));
        Assert.Contains(notEquals.Warnings, w => w.Contains("kept"));
    }

    [Fact]
    public void TextComparison_IsExact_ButIgnoresSurroundingSpaces()
    {
        var source = Tables.Of(["Id", "Status"], ["a", " Completed "], ["b", "completed"]);

        var result = RunWith(source, ["Id", "Status"],
            new FilterStep("Status", FilterOperator.Equals, FilterValueType.Text, Value: "Completed"),
            TestPlans.Write(("Id", "Id")) with { IncludeGrandTotal = false });

        Assert.Equal(["a"], result.Output.Rows.Select(r => r[0].Text));
    }

    [Fact]
    public void ANumberFilterOnText_StopsTheRun()
    {
        var source = Tables.Of(["Id", "Amount"], ["a", 1m], ["b", "lots"]);

        var result = RunWith(source, ["Id", "Amount"],
            new FilterStep("Amount", FilterOperator.Gt, FilterValueType.Number, Value: "0"),
            TestPlans.Write(("Id", "Id")) with { IncludeGrandTotal = false });

        Assert.Contains(result.Issues, i => i.Message.Contains("should hold numbers"));
    }

    [Fact]
    public void Aggregates_CountAvgMinMax()
    {
        var group = new GroupAggregateStep(["Region"],
        [
            new AggregateSpec(AggregateFunction.Count, "Orders"),
            new AggregateSpec(AggregateFunction.Avg, "Average", "Amount"),
            new AggregateSpec(AggregateFunction.Min, "Smallest", "Amount"),
            new AggregateSpec(AggregateFunction.Max, "Largest", "Amount"),
        ]);

        var result = Run(Tables.SalesRaw(), group,
            TestPlans.Write(("Region", "Region"), ("Orders", "Orders"), ("Average", "Average"), ("Smallest", "Smallest"), ("Largest", "Largest")));

        Assert.True(result.Ok);
        Assert.Equal([["North", "3", "150", "50", "300"], ["South", "1", "200", "200", "200"]], Tables.Display(result.Output));
        // The grand total adds up counts, and leaves averages/min/max blank rather than summing them.
        Assert.Equal(4m, result.GrandTotal![1].Number);
        Assert.True(result.GrandTotal[2].IsEmpty);
    }

    [Fact]
    public void Averages_AreRoundedToTenDecimals()
    {
        var source = Tables.Of(["Region", "Amount", "Status"], ["N", 1m, "x"], ["N", 1m, "x"], ["N", 2m, "x"]);

        var result = Run(source, new GroupAggregateStep(["Region"], [new AggregateSpec(AggregateFunction.Avg, "Avg", "Amount")]),
            TestPlans.Write(("Region", "Region"), ("Avg", "Avg")));

        Assert.Equal(1.3333333333m, result.Output.Rows[0][1].Number);
    }

    [Fact]
    public void Groups_KeepFirstAppearanceOrder_WithoutASort()
    {
        var result = Run(Tables.SalesFresh(), Completed, SumByRegion, WriteSummary);

        Assert.Equal(["North", "South", "East"], result.Output.Rows.Select(r => r[0].Text));
    }

    [Fact]
    public void Sorting_IsStable_DescendingWorks_AndBlanksGoLast()
    {
        var source = Tables.Of(["Id", "Score"], ["a", 2m], ["b", null], ["c", 5m], ["d", 2m]);
        var write = TestPlans.Write(("Id", "Id")) with { IncludeGrandTotal = false };

        var ascending = RunWith(source, ["Id", "Score"], new SortStep([new SortKey("Score", SortDirection.Ascending)]), write);
        var descending = RunWith(source, ["Id", "Score"], new SortStep([new SortKey("Score", SortDirection.Descending)]), write);

        Assert.Equal(["a", "d", "c", "b"], ascending.Output.Rows.Select(r => r[0].Text));
        Assert.Equal(["c", "a", "d", "b"], descending.Output.Rows.Select(r => r[0].Text));
    }

    [Fact]
    public void DerivedColumns_Calculate_AndCanBeSummed()
    {
        var source = Tables.Of(["Region", "Qty", "Price"], ["N", 2m, 1.5m], ["N", 3m, 2m], ["S", 1m, 10m]);
        var derived = new DerivedColumnStep("Revenue", new DerivedExpr(DerivedFunction.Mul, [new Operand("Qty"), new Operand("Price")]));
        var group = new GroupAggregateStep(["Region"], [new AggregateSpec(AggregateFunction.Sum, "Revenue Total", "Revenue")]);

        var result = RunWith(source, ["Region", "Qty", "Price"], derived, group, TestPlans.Write(("Region", "Region"), ("Revenue Total", "Revenue")));

        Assert.True(result.Ok);
        Assert.Equal([["N", "9"], ["S", "10"]], Tables.Display(result.Output));
    }

    [Fact]
    public void DerivedColumns_RoundYearMonth_AndDivisionByZeroStops()
    {
        var source = Tables.Of(["Id", "A", "B", "When"], ["a", 10m, 3m, new DateTime(2026, 11, 3)], ["b", 1m, 0m, new DateTime(2025, 1, 9)]);
        string[] required = ["Id", "A", "B", "When"];
        var write = (string col) => TestPlans.Write(("Id", "Id"), (col, col)) with { IncludeGrandTotal = false };

        var rounded = RunWith(source, required,
            new DerivedColumnStep("R", new DerivedExpr(DerivedFunction.Round, [new Operand("A"), new Operand(Number: "0")])), write("R"));
        var year = RunWith(source, required, new DerivedColumnStep("Y", new DerivedExpr(DerivedFunction.Year, [new Operand("When")])), write("Y"));
        var month = RunWith(source, required, new DerivedColumnStep("M", new DerivedExpr(DerivedFunction.Month, [new Operand("When")])), write("M"));
        var divided = RunWith(source, required, new DerivedColumnStep("D", new DerivedExpr(DerivedFunction.Div, [new Operand("A"), new Operand("B")])), write("D"));

        Assert.Equal(["10", "1"], rounded.Output.Rows.Select(r => r[1].ToDisplay()));
        Assert.Equal(["2026", "2025"], year.Output.Rows.Select(r => r[1].ToDisplay()));
        Assert.Equal(["11", "1"], month.Output.Rows.Select(r => r[1].ToDisplay()));
        Assert.Contains(divided.Issues, i => i.Message.Contains("divides by zero") && i.Message.Contains("data row 2"));
    }

    [Fact]
    public void WithoutGrouping_TheGrandTotalSumsNumberColumns()
    {
        var source = Tables.Of(["Region", "Amount", "Status"], ["N", 1.5m, "Completed"], ["S", 2m, "Completed"]);

        var result = Run(source, Completed, TestPlans.Write(("Region", "Region"), ("Amount", "Amount")));

        Assert.Equal(3.5m, result.GrandTotal![1].Number);
        Assert.Equal("Grand Total", result.GrandTotal[0].Text);
    }

    [Fact]
    public void AnEmptyResult_IsAWarning()
    {
        var source = Tables.Of(["Region", "Amount", "Status"], ["N", 1m, "Cancelled"]);

        var result = Run(source, Completed, SumByRegion, WriteSummary);

        Assert.True(result.Ok);
        Assert.Empty(result.Output.Rows);
        Assert.Contains(result.Warnings, w => w.Contains("empty"));
    }

    [Fact]
    public void ColumnsAreRenamedAndReordered_ByTheWriteStep()
    {
        var result = Run(Tables.SalesRaw(), Completed, SumByRegion,
            TestPlans.Write(("Total Sales", "Sales (₹)"), ("Region", "Area")));

        Assert.Equal(["Sales (₹)", "Area"], result.Output.ColumnNames);
        Assert.Equal(["400", "North"], result.Output.Rows[0].Select(c => c.ToDisplay()));
    }

    [Fact]
    public void Execution_HonoursCancellation()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            PlanExecutor.Execute(TestPlans.Sales(Completed, SumByRegion, WriteSummary), Tables.SalesRaw(), cancelled.Token));
    }
}
