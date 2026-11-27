using Litos.Office.Plan;

namespace Litos.Office.Tests;

/// <summary>The spec's §6 plan, and small variations of it, for tests.</summary>
internal static class TestPlans
{
    public static WorkflowPlan Sales(params StepSpec[] steps) => new(
        WorkflowPlan.CurrentSchemaVersion,
        "monthly-regional-sales",
        1,
        [new PlanInput("sales", "xlsx", "Sales", ["Region", "Amount", "Status"])],
        steps.Length > 0 ? steps : SalesSteps(),
        new PlanOutput("xlsx", "Sales_Report_{period}.xlsx", false),
        PlanPolicies.Strict);

    public static StepSpec[] SalesSteps() =>
    [
        new FilterStep("Status", FilterOperator.Equals, FilterValueType.Text, Value: "Completed", KnownValues: ["Completed", "Cancelled"]),
        new GroupAggregateStep(["Region"], [new AggregateSpec(AggregateFunction.Sum, "Total Sales", "Amount")]),
        new SortStep([new SortKey("Region", SortDirection.Ascending)]),
        Write(("Region", "Region"), ("Total Sales", "Total Sales")),
    ];

    public static WriteWorkbookStep Write(params (string From, string As)[] columns) =>
        new(PlanValidator.TemplateFileName, "Summary", columns.Select(c => new ColumnMap(c.From, c.As)).ToList(), true, "Grand Total");
}
