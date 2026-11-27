using Litos.Office.Data;
using Litos.Office.Plan;

namespace Litos.Office.Tests;

/// <summary>workflow.json is what a routine runs unattended, so parsing is strict: unknown
/// operations, fields and enum values are errors (design §2 PlanJson).</summary>
public class PlanJsonTests
{
    [Fact]
    public void ThePlan_RoundTrips()
    {
        var plan = TestPlans.Sales();

        Assert.True(PlanJson.TryParse(PlanJson.Serialize(plan), out WorkflowPlan? parsed, out var error), error);
        Assert.Equal(PlanJson.Serialize(plan), PlanJson.Serialize(parsed!));
        Assert.IsType<FilterStep>(parsed!.Steps[0]);
        Assert.IsType<WriteWorkbookStep>(parsed.Steps[^1]);
    }

    [Fact]
    public void TheJson_UsesTheOpDiscriminator_AndCamelCaseEnums()
    {
        var json = PlanJson.Serialize(TestPlans.Sales());

        Assert.Contains("\"op\": \"filter\"", json);
        Assert.Contains("\"op\": \"groupAggregate\"", json);
        Assert.Contains("\"operator\": \"equals\"", json);
        Assert.Contains("\"function\": \"sum\"", json);
    }

    [Fact]
    public void TheSpecsOwnExampleShape_Parses()
    {
        const string json = """
            {
              "schemaVersion": 1, "skillId": "monthly-regional-sales", "version": 1,
              "inputs": [{"id":"sales","format":"xlsx","sheet":"Sales","requiredColumns":["Region","Amount","Status"]}],
              "steps": [
                {"op":"filter","column":"Status","operator":"equals","valueType":"text","value":"Completed"},
                {"op":"groupAggregate","groupBy":["Region"],"aggregates":[{"column":"Amount","function":"sum","as":"Total Sales"}]},
                {"op":"sort","keys":[{"column":"Region","direction":"ascending"}]},
                {"op":"writeWorkbook","template":"report-template.xlsx","sheet":"Summary","columns":[{"from":"Region","as":"Region"},{"from":"Total Sales","as":"Total Sales"}],"includeGrandTotal":true}
              ],
              "output": {"format":"xlsx","fileNamePattern":"Sales_Report_{period}.xlsx","overwrite":false},
              "policies": {"unknownValue":"fail","blankKey":"fail","invalidNumber":"fail"}
            }
            """;

        Assert.True(PlanJson.TryParse(json, out WorkflowPlan? plan, out var error), error);
        Assert.Empty(PlanValidator.Validate(plan!));
    }

    [Fact]
    public void AnUnknownOperation_IsRefused()
    {
        var json = PlanJson.Serialize(TestPlans.Sales()).Replace("\"op\": \"sort\"", "\"op\": \"shell\"", StringComparison.Ordinal);

        Assert.False(PlanJson.TryParse(json, out WorkflowPlan? _, out var error));
        Assert.Contains("steps", error);
    }

    [Fact]
    public void AnUnknownField_IsRefused()
    {
        var json = PlanJson.Serialize(TestPlans.Sales()).Replace("\"version\": 1,", "\"version\": 1, \"command\": \"rm -rf\",", StringComparison.Ordinal);

        Assert.False(PlanJson.TryParse(json, out WorkflowPlan? _, out _));
    }

    [Fact]
    public void AnUnknownEnumValue_OrANumericEnum_IsRefused()
    {
        var plan = PlanJson.Serialize(TestPlans.Sales());

        Assert.False(PlanJson.TryParse(plan.Replace("\"function\": \"sum\"", "\"function\": \"exec\"", StringComparison.Ordinal), out WorkflowPlan? _, out _));
        Assert.False(PlanJson.TryParse(plan.Replace("\"function\": \"sum\"", "\"function\": 0", StringComparison.Ordinal), out WorkflowPlan? _, out _));
    }

    [Fact]
    public void AMissingRequiredField_IsRefused()
    {
        var json = PlanJson.Serialize(TestPlans.Sales()).Replace("\"skillId\": \"monthly-regional-sales\",", "", StringComparison.Ordinal);

        Assert.False(PlanJson.TryParse(json, out WorkflowPlan? _, out _));
    }

    [Fact]
    public void GarbageAndOversizedFiles_AreRefused_WithASentence()
    {
        Assert.False(PlanJson.TryParse("not json", out WorkflowPlan? _, out var garbage));
        Assert.StartsWith("The workflow file isn't valid", garbage);

        Assert.False(PlanJson.TryParse(new string(' ', PlanJson.MaxPlanBytes + 1), out WorkflowPlan? _, out var big));
        Assert.Contains("larger than", big);

        Assert.False(PlanJson.TryParse("null", out WorkflowPlan? _, out _));
    }

    [Fact]
    public void ValidationSpec_RoundTrips()
    {
        var spec = new ValidationSpec(1, [new ValidationRule("required_columns"), new ValidationRule("known_values", "Status")]);

        Assert.True(PlanJson.TryParse(PlanJson.Serialize(spec), out ValidationSpec? parsed, out _));
        Assert.Equal("Status", parsed!.Rules[1].Column);
    }
}

/// <summary>One test per validator rule (design §4).</summary>
public class PlanValidatorTests
{
    private static IReadOnlyList<PlanIssue> Validate(WorkflowPlan plan, SchemaContext? context = null) => PlanValidator.Validate(plan, context);

    private static void AssertIssue(WorkflowPlan plan, string pathPart, SchemaContext? context = null)
    {
        var issues = Validate(plan, context);
        Assert.True(issues.Any(i => i.Path.Contains(pathPart, StringComparison.Ordinal)),
            $"Expected an issue at '{pathPart}', got: {string.Join("; ", issues)}");
    }

    private static WorkflowPlan With(params StepSpec[] steps) => TestPlans.Sales(steps);

    private static readonly FilterStep Completed =
        new("Status", FilterOperator.Equals, FilterValueType.Text, Value: "Completed");

    private static readonly WriteWorkbookStep WriteRaw = TestPlans.Write(("Region", "Region"), ("Amount", "Amount"));

    [Fact]
    public void TheSalesPlan_IsValid()
    {
        Assert.Empty(Validate(TestPlans.Sales()));
    }

    [Fact]
    public void Header_SchemaVersionSkillIdAndVersion()
    {
        AssertIssue(TestPlans.Sales() with { SchemaVersion = 2 }, "schemaVersion");
        AssertIssue(TestPlans.Sales() with { SkillId = "Bad Name" }, "skillId");
        AssertIssue(TestPlans.Sales() with { Version = 0 }, "version");
    }

    [Fact]
    public void Inputs_ExactlyOneXlsx_WithValidSheetAndColumns()
    {
        var input = TestPlans.Sales().Inputs[0];
        AssertIssue(TestPlans.Sales() with { Inputs = [] }, "inputs");
        AssertIssue(TestPlans.Sales() with { Inputs = [input, input] }, "inputs");
        AssertIssue(TestPlans.Sales() with { Inputs = [input with { Format = "csv" }] }, "inputs[0].format");
        AssertIssue(TestPlans.Sales() with { Inputs = [input with { Sheet = "Bad/Name" }] }, "inputs[0].sheet");
        AssertIssue(TestPlans.Sales() with { Inputs = [input with { RequiredColumns = [] }] }, "inputs[0].requiredColumns");
        AssertIssue(TestPlans.Sales() with { Inputs = [input with { RequiredColumns = ["Region", "Region", "Amount", "Status"] }] }, "inputs[0].requiredColumns");
    }

    [Fact]
    public void Steps_EndWithExactlyOneWrite()
    {
        AssertIssue(With(Completed), "steps[0]");
        AssertIssue(With(WriteRaw, Completed), "steps[0]");
        AssertIssue(TestPlans.Sales() with { Steps = [] }, "steps");
        AssertIssue(With([.. Enumerable.Repeat<StepSpec>(Completed, 12), WriteRaw]), "steps");
    }

    [Fact]
    public void OnlyOneGroupingStep()
    {
        var group = new GroupAggregateStep(["Region"], [new AggregateSpec(AggregateFunction.Count, "N")]);

        AssertIssue(With(group, new GroupAggregateStep(["Region"], [new AggregateSpec(AggregateFunction.Count, "M")]), TestPlans.Write(("Region", "Region"))), "steps[1]");
    }

    [Fact]
    public void ColumnReferences_FollowTheRunningSchema()
    {
        AssertIssue(With(Completed with { Column = "Colour" }, WriteRaw), "steps[0].column");
        // After grouping, only the keys and aggregates exist: Amount is gone.
        AssertIssue(With(new GroupAggregateStep(["Region"], [new AggregateSpec(AggregateFunction.Sum, "Total", "Amount")]), WriteRaw), "steps[1].columns[1].from");
        AssertIssue(With(new SortStep([new SortKey("Nope", SortDirection.Ascending)]), WriteRaw), "steps[0].keys");
    }

    [Fact]
    public void Filters_OperatorAndValueMustAgree()
    {
        AssertIssue(With(Completed with { Operator = FilterOperator.Gt }, WriteRaw), "steps[0].operator");
        AssertIssue(With(new FilterStep("Amount", FilterOperator.Gt, FilterValueType.Number, Value: "lots"), WriteRaw), "steps[0]");
        AssertIssue(With(new FilterStep("Status", FilterOperator.Gt, FilterValueType.Date, Value: "03/11/2026"), WriteRaw), "steps[0]");
        AssertIssue(With(Completed with { Value = null }, WriteRaw), "steps[0]");
        AssertIssue(With(Completed with { Operator = FilterOperator.In }, WriteRaw), "steps[0]");
        AssertIssue(With(Completed with { Values = ["a"] }, WriteRaw), "steps[0]");
        AssertIssue(With(new FilterStep("Status", FilterOperator.In, FilterValueType.Text, Values: [.. Enumerable.Range(0, 201).Select(i => $"v{i}")]), WriteRaw), "steps[0].values");
        AssertIssue(With(Completed with { Value = new string('x', 201) }, WriteRaw), "steps[0]");
    }

    [Fact]
    public void Filters_WithASchema_TypesMustMatch()
    {
        var context = new SchemaContext("Sales", new Dictionary<string, ColumnType>
        {
            ["Region"] = ColumnType.Text, ["Amount"] = ColumnType.Number, ["Status"] = ColumnType.Text,
        });

        AssertIssue(With(new FilterStep("Region", FilterOperator.Gt, FilterValueType.Number, Value: "5"), WriteRaw), "steps[0].valueType", context);
        Assert.Empty(Validate(With(new FilterStep("Amount", FilterOperator.Gte, FilterValueType.Number, Value: "120.50"), WriteRaw), context));
    }

    [Fact]
    public void KnownValues_OnlyOnEqualityFilters()
    {
        AssertIssue(With(new FilterStep("Amount", FilterOperator.Gt, FilterValueType.Number, Value: "5", KnownValues: ["1"]), WriteRaw), "steps[0].knownValues");
    }

    [Fact]
    public void Names_AreBounded_Unique_AndNeverFormulaLike()
    {
        AssertIssue(With(TestPlans.Write(("Region", "=HYPERLINK(\"x\")"))), "steps[0].columns[0].as");
        AssertIssue(With(TestPlans.Write(("Region", "+cmd"))), "steps[0].columns[0].as");
        AssertIssue(With(TestPlans.Write(("Region", new string('n', 65)))), "steps[0].columns[0].as");
        AssertIssue(With(TestPlans.Write(("Region", "Same"), ("Amount", "Same"))), "steps[0].columns[1].as");
        AssertIssue(With(new DerivedColumnStep("Amount", new DerivedExpr(DerivedFunction.Add, [new Operand("Amount"), new Operand(Number: "1")])), WriteRaw), "steps[0].name");
    }

    [Fact]
    public void Aggregates_NeedNumberColumns_ExceptCount()
    {
        var context = new SchemaContext(null, new Dictionary<string, ColumnType>
        {
            ["Region"] = ColumnType.Text, ["Amount"] = ColumnType.Number, ["Status"] = ColumnType.Text,
        });

        AssertIssue(With(new GroupAggregateStep(["Region"], [new AggregateSpec(AggregateFunction.Sum, "S", "Status")]), TestPlans.Write(("Region", "Region"))), "steps[0].aggregates[0].column", context);
        AssertIssue(With(new GroupAggregateStep(["Region"], [new AggregateSpec(AggregateFunction.Sum, "S")]), TestPlans.Write(("Region", "Region"))), "steps[0].aggregates[0].column");
        Assert.Empty(Validate(With(new GroupAggregateStep(["Region"], [new AggregateSpec(AggregateFunction.Count, "Orders")]), TestPlans.Write(("Region", "Region"), ("Orders", "Orders"))), context));
        AssertIssue(With(new GroupAggregateStep([], [new AggregateSpec(AggregateFunction.Count, "N")]), WriteRaw), "steps[0].groupBy");
        AssertIssue(With(new GroupAggregateStep(["Region"], [new AggregateSpec(AggregateFunction.Count, "Region")]), WriteRaw), "steps[0].aggregates[0].as");
    }

    [Fact]
    public void Derived_ArgumentCountsTypesAndConstants()
    {
        StepSpec Derived(DerivedFunction fn, params Operand[] args) => new DerivedColumnStep("New", new DerivedExpr(fn, args));

        AssertIssue(With(Derived(DerivedFunction.Add, new Operand("Amount")), WriteRaw), "steps[0].expr.args");
        AssertIssue(With(Derived(DerivedFunction.Div, new Operand("Amount"), new Operand(Number: "0")), WriteRaw), "steps[0].expr.args[1]");
        AssertIssue(With(Derived(DerivedFunction.Add, new Operand("Amount", "1"), new Operand(Number: "1")), WriteRaw), "steps[0].expr.args[0]");
        AssertIssue(With(Derived(DerivedFunction.Round, new Operand("Amount"), new Operand(Number: "2.5")), WriteRaw), "steps[0].expr.args[1]");
        AssertIssue(With(Derived(DerivedFunction.Round, new Operand("Amount"), new Operand("Amount")), WriteRaw), "steps[0].expr.args[1]");
        Assert.Empty(Validate(With(Derived(DerivedFunction.Mul, new Operand("Amount"), new Operand(Number: "1.18")), TestPlans.Write(("Region", "Region"), ("New", "With Tax")))));
    }

    [Fact]
    public void Write_TemplateSheetAndColumns()
    {
        AssertIssue(With(WriteRaw with { Template = "../../secrets.xlsx" }), "steps[0].template");
        AssertIssue(With(WriteRaw with { Template = "C:\\x\\report-template.xlsx" }), "steps[0].template");
        AssertIssue(With(WriteRaw with { Sheet = "a:b" }), "steps[0].sheet");
        AssertIssue(With(WriteRaw with { Columns = [] }), "steps[0].columns");
        AssertIssue(With(WriteRaw with { GrandTotalLabel = new string('t', 65) }), "steps[0].grandTotalLabel");
    }

    [Theory]
    [InlineData("report.csv")]
    [InlineData("../report.xlsx")]
    [InlineData("C:\\temp\\report.xlsx")]
    [InlineData("reports/report.xlsx")]
    [InlineData("report_{user}.xlsx")]
    [InlineData("")]
    public void Output_FileNamePatternIsSafe(string pattern)
    {
        AssertIssue(TestPlans.Sales() with { Output = new PlanOutput("xlsx", pattern, false) }, "output.fileNamePattern");
    }

    [Fact]
    public void Output_NeverOverwrites_AndPatternsMayUseTheThreeTokens()
    {
        AssertIssue(TestPlans.Sales() with { Output = new PlanOutput("xlsx", "r.xlsx", true) }, "output.overwrite");
        Assert.Empty(Validate(TestPlans.Sales() with { Output = new PlanOutput("xlsx", "Report {period} {date} {input}.xlsx", false) }));
    }

    [Fact]
    public void Policies_UnknownValuesAndBadNumbersAlwaysStop_BlankKeysMayBeKept()
    {
        AssertIssue(TestPlans.Sales() with { Policies = PlanPolicies.Strict with { UnknownValue = PolicyAction.Keep } }, "policies.unknownValue");
        AssertIssue(TestPlans.Sales() with { Policies = PlanPolicies.Strict with { InvalidNumber = PolicyAction.Keep } }, "policies.invalidNumber");
        Assert.Empty(Validate(TestPlans.Sales() with { Policies = PlanPolicies.Strict with { BlankKey = PolicyAction.Keep } }));
    }

    [Fact]
    public void WithASchema_RequiredColumnsAndSheetMustExist()
    {
        var context = new SchemaContext("Orders", new Dictionary<string, ColumnType> { ["Region"] = ColumnType.Text, ["Amount"] = ColumnType.Number });

        var issues = Validate(TestPlans.Sales(), context);

        Assert.Contains(issues, i => i.Message.Contains("\"Status\""));
        Assert.Contains(issues, i => i.Path == "inputs[0].sheet");
    }
}

public class SkillNamesTests
{
    [Theory]
    [InlineData("Monthly Regional Sales!", "monthly-regional-sales")]
    [InlineData("  2026 report ", "report")]
    [InlineData("Ünïcode only", "n-code-only")]
    [InlineData("!!!", "learned-workflow")]
    public void From_MakesValidNames(string text, string expected)
    {
        Assert.Equal(expected, SkillNames.From(text));
        Assert.True(SkillNames.IsValid(SkillNames.From(text)));
    }

    [Fact]
    public void From_CapsLength()
    {
        Assert.True(SkillNames.From(new string('a', 100)).Length <= SkillNames.MaxLength);
    }

    [Theory]
    [InlineData("ok-name", true)]
    [InlineData("a1", true)]
    [InlineData("1abc", false)]
    [InlineData("abc-", false)]
    [InlineData("Abc", false)]
    [InlineData("", false)]
    public void IsValid_MatchesTheWorkflowStoreRule(string name, bool valid)
    {
        Assert.Equal(valid, SkillNames.IsValid(name));
    }
}
