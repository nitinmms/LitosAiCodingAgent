using System.Text.Json.Serialization;

namespace Litos.Office.Plan;

/// <summary>
/// The executable form of a learned skill (design §4, spec §6 "typed workflow plan"). It is data,
/// not code: a fixed list of operations that <see cref="Exec.PlanExecutor"/> knows how to run, and
/// nothing a model writes can add a new kind of step. Litos assembles a plan from candidates it
/// computed itself; the model only chooses among them.
/// </summary>
public sealed record WorkflowPlan(
    int SchemaVersion,
    string SkillId,
    int Version,
    IReadOnlyList<PlanInput> Inputs,
    IReadOnlyList<StepSpec> Steps,
    PlanOutput Output,
    PlanPolicies Policies)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record PlanInput(string Id, string Format, string? Sheet, IReadOnlyList<string> RequiredColumns);

[JsonPolymorphic(
    TypeDiscriminatorPropertyName = "op",
    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization,
    IgnoreUnrecognizedTypeDiscriminators = false)]
[JsonDerivedType(typeof(FilterStep), "filter")]
[JsonDerivedType(typeof(GroupAggregateStep), "groupAggregate")]
[JsonDerivedType(typeof(SortStep), "sort")]
[JsonDerivedType(typeof(DerivedColumnStep), "derivedColumn")]
[JsonDerivedType(typeof(WriteWorkbookStep), "writeWorkbook")]
public abstract record StepSpec;

public enum FilterOperator { Equals, NotEquals, In, NotIn, Gt, Gte, Lt, Lte }

public enum FilterValueType { Text, Number, Date }

/// <summary>Keeps the rows that match. <paramref name="Value"/> and <paramref name="Values"/> are
/// strings in JSON — numbers as invariant decimals ("120.50"), dates as yyyy-MM-dd — so nothing
/// round-trips through a float. <paramref name="KnownValues"/> are the values this column held when
/// the skill was learned: a run meeting a new one stops rather than guessing which side it falls on.</summary>
public sealed record FilterStep(
    string Column,
    FilterOperator Operator,
    FilterValueType ValueType,
    string? Value = null,
    IReadOnlyList<string>? Values = null,
    IReadOnlyList<string>? KnownValues = null) : StepSpec;

public enum AggregateFunction { Sum, Count, Avg, Min, Max }

public sealed record AggregateSpec(AggregateFunction Function, string As, string? Column = null);

public sealed record GroupAggregateStep(IReadOnlyList<string> GroupBy, IReadOnlyList<AggregateSpec> Aggregates) : StepSpec;

public enum SortDirection { Ascending, Descending }

public sealed record SortKey(string Column, SortDirection Direction);

public sealed record SortStep(IReadOnlyList<SortKey> Keys) : StepSpec;

public enum DerivedFunction { Add, Sub, Mul, Div, Round, Year, Month }

/// <summary>Exactly one of <paramref name="Column"/> or <paramref name="Number"/>.</summary>
public sealed record Operand(string? Column = null, string? Number = null);

public sealed record DerivedExpr(DerivedFunction Fn, IReadOnlyList<Operand> Args);

public sealed record DerivedColumnStep(string Name, DerivedExpr Expr) : StepSpec;

public sealed record ColumnMap(string From, string As);

/// <summary>The last step, always: which columns go into the report, in which order and under which
/// headings, onto which sheet of the stored template, with or without a grand-total row.</summary>
public sealed record WriteWorkbookStep(
    string Template,
    string Sheet,
    IReadOnlyList<ColumnMap> Columns,
    bool IncludeGrandTotal,
    string? GrandTotalLabel = null) : StepSpec;

public sealed record PlanOutput(string Format, string FileNamePattern, bool Overwrite);

public enum PolicyAction { Fail, Keep }

/// <summary>What a run does on data the skill wasn't taught about. In v1 an unknown filter value
/// and a non-number in a number column always stop the run; a blank group key may be kept as its
/// own group if the user chose that.</summary>
public sealed record PlanPolicies(PolicyAction UnknownValue, PolicyAction BlankKey, PolicyAction InvalidNumber)
{
    public static PlanPolicies Strict { get; } = new(PolicyAction.Fail, PolicyAction.Fail, PolicyAction.Fail);
}

/// <summary>The checks a run must pass before its output is published (validation.json).</summary>
public sealed record ValidationSpec(int SchemaVersion, IReadOnlyList<ValidationRule> Rules);

public sealed record ValidationRule(string Id, string? Column = null);
