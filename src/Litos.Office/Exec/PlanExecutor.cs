using System.Globalization;
using Litos.Office.Data;
using Litos.Office.Plan;

namespace Litos.Office.Exec;

public sealed record RowAccounting(int SourceRows, int Included, int Excluded);

/// <summary>
/// A run's result before anything is written. <paramref name="Issues"/> are reasons the output must
/// not be published (an unknown value, a non-number, a blank key); <paramref name="Warnings"/> are
/// worth showing but don't block. <paramref name="GrandTotal"/> is the totals row, when the plan
/// asks for one: sums of the summed and counted columns, the label in the first text column.
/// </summary>
public sealed record ExecResult(
    Table Output,
    CellValue[]? GrandTotal,
    RowAccounting Accounting,
    IReadOnlyList<PlanIssue> Issues,
    IReadOnlyList<string> Warnings)
{
    public bool Ok => Issues.Count == 0;
}

/// <summary>
/// Runs a validated plan over a table (design §2 Exec/). Deterministic and exact: decimal
/// arithmetic, ordinal text comparison, stable sorting, first-appearance group order. Data the plan
/// wasn't taught about is never quietly absorbed — a new filter value, a non-number where a number
/// is needed, a blank group key, a division by zero each become an issue that stops publication.
/// </summary>
public static class PlanExecutor
{
    private const int MaxRowsNamed = 5;

    public static ExecResult Execute(WorkflowPlan plan, Table source, CancellationToken ct = default)
    {
        var issues = new List<PlanIssue>();
        var warnings = new List<string>();
        var names = source.Columns.Select(c => c.Name).ToList();

        // A plan only runs over an input that has every column it was taught on; otherwise the
        // file layout changed, and that is the whole answer.
        var missing = plan.Inputs[0].RequiredColumns.Where(c => !names.Contains(c, StringComparer.Ordinal)).ToList();
        if (missing.Count > 0)
        {
            var headings = plan.Steps.OfType<WriteWorkbookStep>().Single().Columns.Select(c => c.As).ToList();
            issues.Add(new PlanIssue("inputs[0].requiredColumns",
                $"Column {string.Join(", ", missing.Select(m => $"\"{m}\""))} is missing. The file layout changed since this skill was learned."));
            return new ExecResult(Table.From(headings, []), null,
                new RowAccounting(source.Rows.Count, 0, source.Rows.Count), issues, warnings);
        }

        var rows = source.Rows.Select((r, i) => new WorkRow(r.ToArray(), i)).ToList();
        var sumColumns = new HashSet<string>(StringComparer.Ordinal);
        var included = -1;
        var grouped = false;

        for (var s = 0; s < plan.Steps.Count; s++)
        {
            ct.ThrowIfCancellationRequested();
            var path = $"steps[{s}]";
            switch (plan.Steps[s])
            {
                case FilterStep filter:
                    rows = Filter(filter, path, names, rows, issues, warnings);
                    break;
                case DerivedColumnStep derived:
                    rows = Derive(derived, path, names, rows, issues);
                    names.Add(derived.Name);
                    break;
                case GroupAggregateStep group:
                    included = rows.Count;
                    (names, rows) = Group(group, plan.Policies, path, names, rows, issues, sumColumns);
                    grouped = true;
                    break;
                case SortStep sort:
                    rows = Sort(sort, names, rows);
                    break;
                case WriteWorkbookStep:
                    break;
            }
        }

        if (included < 0)
            included = rows.Count;

        var write = plan.Steps.OfType<WriteWorkbookStep>().Single();
        var indexes = write.Columns.Select(c => names.IndexOf(c.From)).ToArray();
        var outputRows = rows.Select(r => indexes.Select(i => r.Cells[i]).ToArray()).ToList();
        var output = Table.From(write.Columns.Select(c => c.As).ToList(), outputRows);

        CellValue[]? total = null;
        if (write.IncludeGrandTotal)
        {
            total = new CellValue[write.Columns.Count];
            var labelled = false;
            for (var c = 0; c < write.Columns.Count; c++)
            {
                var from = write.Columns[c].From;
                var totalled = grouped ? sumColumns.Contains(from) : output.Columns[c].Type == ColumnType.Number;
                if (totalled)
                    total[c] = CellValue.OfNumber(outputRows.Sum(r => r[c].Number ?? 0m));
                else if (!labelled && output.Columns[c].Type is ColumnType.Text or ColumnType.Empty)
                {
                    total[c] = CellValue.OfText(string.IsNullOrWhiteSpace(write.GrandTotalLabel) ? "Grand Total" : write.GrandTotalLabel);
                    labelled = true;
                }
            }
        }

        if (outputRows.Count == 0)
            warnings.Add("No rows were left after the rules were applied, so the report is empty.");

        return new ExecResult(output, total, new RowAccounting(source.Rows.Count, included, source.Rows.Count - included), issues, warnings);
    }

    // ---- filter ---------------------------------------------------------------------------------

    private static List<WorkRow> Filter(FilterStep filter, string path, List<string> names, List<WorkRow> rows, List<PlanIssue> issues, List<string> warnings)
    {
        var index = names.IndexOf(filter.Column);
        var known = filter.KnownValues?.Select(v => v.Trim()).ToHashSet(StringComparer.Ordinal);
        var unknown = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var invalid = new List<int>();
        var blanks = 0;
        var kept = new List<WorkRow>();

        foreach (var row in rows)
        {
            var cell = row.Cells[index];
            if (cell.IsEmpty)
            {
                blanks++;
                if (filter.Operator is FilterOperator.NotEquals or FilterOperator.NotIn)
                    kept.Add(row);
                continue;
            }

            if (known is not null && !known.Contains(cell.ToDisplay().Trim()))
            {
                var key = cell.ToDisplay().Trim();
                if (!unknown.TryGetValue(key, out var list))
                    unknown[key] = list = [];
                list.Add(row.SourceIndex);
            }

            bool? matches = filter.ValueType switch
            {
                FilterValueType.Text => MatchText(filter, cell.ToDisplay().Trim()),
                FilterValueType.Number => cell.Kind == CellKind.Number ? MatchOrdered(filter, cell.Number!.Value, PlanValidator.TryNumber) : null,
                FilterValueType.Date => cell.Kind == CellKind.Date ? MatchOrdered(filter, cell.Date!.Value.Date, PlanValidator.TryDate) : null,
                _ => null,
            };

            if (matches is null)
                invalid.Add(row.SourceIndex);
            else if (matches.Value)
                kept.Add(row);
        }

        foreach (var (value, at) in unknown)
            issues.Add(new PlanIssue(path, $"\"{filter.Column}\" has a value this skill wasn't taught about: \"{Clip(value)}\" ({Rows(at)}). " +
                "Litos stopped rather than guess whether to include it; teach the skill again with an example that has it."));
        if (invalid.Count > 0)
            issues.Add(new PlanIssue(path, $"\"{filter.Column}\" should hold {(filter.ValueType == FilterValueType.Date ? "dates" : "numbers")}, but {Rows(invalid)} don't."));
        if (blanks > 0)
            warnings.Add($"{blanks} row(s) have no \"{filter.Column}\"; they were {(filter.Operator is FilterOperator.NotEquals or FilterOperator.NotIn ? "kept" : "left out")}.");
        return kept;
    }

    private static bool MatchText(FilterStep filter, string text) => filter.Operator switch
    {
        FilterOperator.Equals => string.Equals(text, filter.Value!.Trim(), StringComparison.Ordinal),
        FilterOperator.NotEquals => !string.Equals(text, filter.Value!.Trim(), StringComparison.Ordinal),
        FilterOperator.In => filter.Values!.Any(v => string.Equals(text, v.Trim(), StringComparison.Ordinal)),
        FilterOperator.NotIn => !filter.Values!.Any(v => string.Equals(text, v.Trim(), StringComparison.Ordinal)),
        _ => false,
    };

    private delegate bool TryParse<T>(string text, out T value);

    private static bool MatchOrdered<T>(FilterStep filter, T actual, TryParse<T> parse) where T : IComparable<T>
    {
        if (filter.Operator is FilterOperator.In or FilterOperator.NotIn)
        {
            var any = filter.Values!.Any(v => parse(v, out var x) && actual.CompareTo(x) == 0);
            return filter.Operator == FilterOperator.In ? any : !any;
        }

        parse(filter.Value!, out var wanted);
        var compared = actual.CompareTo(wanted);
        return filter.Operator switch
        {
            FilterOperator.Equals => compared == 0,
            FilterOperator.NotEquals => compared != 0,
            FilterOperator.Gt => compared > 0,
            FilterOperator.Gte => compared >= 0,
            FilterOperator.Lt => compared < 0,
            FilterOperator.Lte => compared <= 0,
            _ => false,
        };
    }

    // ---- derived column -------------------------------------------------------------------------

    private static List<WorkRow> Derive(DerivedColumnStep derived, string path, List<string> names, List<WorkRow> rows, List<PlanIssue> issues)
    {
        var bad = new List<int>();
        var divByZero = new List<int>();
        var result = new List<WorkRow>(rows.Count);
        foreach (var row in rows)
        {
            CellValue value;
            try
            {
                value = Evaluate(derived.Expr, names, row.Cells, out var problem);
                if (problem == DeriveProblem.NotANumber)
                    bad.Add(row.SourceIndex);
                else if (problem == DeriveProblem.DivideByZero)
                    divByZero.Add(row.SourceIndex);
            }
            catch (OverflowException)
            {
                value = CellValue.Empty;
                bad.Add(row.SourceIndex);
            }
            result.Add(row with { Cells = [.. row.Cells, value] });
        }

        if (bad.Count > 0)
            issues.Add(new PlanIssue(path, $"\"{derived.Name}\" couldn't be worked out for {Rows(bad)}: a value is missing, isn't a number, or is too large."));
        if (divByZero.Count > 0)
            issues.Add(new PlanIssue(path, $"\"{derived.Name}\" divides by zero in {Rows(divByZero)}."));
        return result;
    }

    private enum DeriveProblem { None, NotANumber, DivideByZero }

    private static CellValue Evaluate(DerivedExpr expr, List<string> names, CellValue[] cells, out DeriveProblem problem)
    {
        problem = DeriveProblem.None;
        if (expr.Fn is DerivedFunction.Year or DerivedFunction.Month)
        {
            var cell = cells[names.IndexOf(expr.Args[0].Column!)];
            if (cell.Kind != CellKind.Date)
            {
                problem = DeriveProblem.NotANumber;
                return CellValue.Empty;
            }
            return CellValue.OfNumber(expr.Fn == DerivedFunction.Year ? cell.Date!.Value.Year : cell.Date!.Value.Month);
        }

        var operands = new decimal[expr.Args.Count];
        for (var i = 0; i < operands.Length; i++)
        {
            var arg = expr.Args[i];
            if (arg.Number is not null)
                operands[i] = decimal.Parse(arg.Number, NumberStyles.Number, CultureInfo.InvariantCulture);
            else if (cells[names.IndexOf(arg.Column!)] is { Kind: CellKind.Number } number)
                operands[i] = number.Number!.Value;
            else
            {
                problem = DeriveProblem.NotANumber;
                return CellValue.Empty;
            }
        }

        if (expr.Fn == DerivedFunction.Div && operands[1] == 0)
        {
            problem = DeriveProblem.DivideByZero;
            return CellValue.Empty;
        }

        return CellValue.OfNumber(expr.Fn switch
        {
            DerivedFunction.Add => operands[0] + operands[1],
            DerivedFunction.Sub => operands[0] - operands[1],
            DerivedFunction.Mul => operands[0] * operands[1],
            DerivedFunction.Div => operands[0] / operands[1],
            DerivedFunction.Round => Math.Round(operands[0], (int)operands[1], MidpointRounding.AwayFromZero),
            _ => 0m,
        });
    }

    // ---- group ----------------------------------------------------------------------------------

    private static (List<string> Names, List<WorkRow> Rows) Group(
        GroupAggregateStep group, PlanPolicies policies, string path, List<string> names, List<WorkRow> rows,
        List<PlanIssue> issues, HashSet<string> sumColumns)
    {
        var keyIndexes = group.GroupBy.Select(g => names.IndexOf(g)).ToArray();
        var groups = new Dictionary<string, List<WorkRow>>(StringComparer.Ordinal);
        var order = new List<string>();
        var blankKeys = new List<int>();

        foreach (var row in rows)
        {
            if (keyIndexes.Any(k => row.Cells[k].IsEmpty))
            {
                blankKeys.Add(row.SourceIndex);
                if (policies.BlankKey == PolicyAction.Fail)
                    continue;
            }
            var key = string.Join('\u001F', keyIndexes.Select(k => row.Cells[k].ToDisplay().Trim()));
            if (!groups.TryGetValue(key, out var members))
            {
                groups[key] = members = [];
                order.Add(key);
            }
            members.Add(row);
        }

        if (blankKeys.Count > 0 && policies.BlankKey == PolicyAction.Fail)
            issues.Add(new PlanIssue(path, $"{Rows(blankKeys)} have no {string.Join(" / ", group.GroupBy.Select(g => $"\"{g}\""))}, so they can't be grouped."));

        var invalid = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var outputRows = new List<WorkRow>(order.Count);
        foreach (var key in order)
        {
            var members = groups[key];
            var cells = keyIndexes.Select(k => members[0].Cells[k]).ToList();
            foreach (var aggregate in group.Aggregates)
                cells.Add(Aggregate(aggregate, names, members, invalid));
            outputRows.Add(new WorkRow([.. cells], members[0].SourceIndex));
        }

        foreach (var (column, at) in invalid)
            issues.Add(new PlanIssue(path, $"\"{column}\" should hold numbers, but {Rows(at)} are blank or not numbers."));

        foreach (var aggregate in group.Aggregates.Where(a => a.Function is AggregateFunction.Sum or AggregateFunction.Count))
            sumColumns.Add(aggregate.As);

        return ([.. group.GroupBy, .. group.Aggregates.Select(a => a.As)], outputRows);
    }

    private static CellValue Aggregate(AggregateSpec aggregate, List<string> names, List<WorkRow> members, Dictionary<string, List<int>> invalid)
    {
        if (aggregate.Function == AggregateFunction.Count)
        {
            return CellValue.OfNumber(aggregate.Column is null
                ? members.Count
                : members.Count(m => !m.Cells[names.IndexOf(aggregate.Column)].IsEmpty));
        }

        var index = names.IndexOf(aggregate.Column!);
        var numbers = new List<decimal>(members.Count);
        foreach (var member in members)
        {
            var cell = member.Cells[index];
            if (cell.Kind == CellKind.Number)
                numbers.Add(cell.Number!.Value);
            else
            {
                if (!invalid.TryGetValue(aggregate.Column!, out var list))
                    invalid[aggregate.Column!] = list = [];
                list.Add(member.SourceIndex);
            }
        }

        if (numbers.Count == 0)
            return CellValue.Empty;
        return CellValue.OfNumber(aggregate.Function switch
        {
            AggregateFunction.Sum => numbers.Sum(),
            AggregateFunction.Avg => Math.Round(numbers.Sum() / numbers.Count, 10, MidpointRounding.AwayFromZero),
            AggregateFunction.Min => numbers.Min(),
            AggregateFunction.Max => numbers.Max(),
            _ => 0m,
        });
    }

    // ---- sort -----------------------------------------------------------------------------------

    private static List<WorkRow> Sort(SortStep sort, List<string> names, List<WorkRow> rows)
    {
        IOrderedEnumerable<WorkRow>? ordered = null;
        foreach (var key in sort.Keys)
        {
            var index = names.IndexOf(key.Column);
            var comparer = key.Direction == SortDirection.Ascending ? CellComparer.Ascending : CellComparer.Descending;
            ordered = ordered is null
                ? rows.OrderBy(r => r.Cells[index], comparer)
                : ordered.ThenBy(r => r.Cells[index], comparer);
        }
        return ordered?.ToList() ?? rows;
    }

    private static string Rows(IReadOnlyCollection<int> sourceIndexes)
    {
        var named = string.Join(", ", sourceIndexes.Take(MaxRowsNamed).Select(i => (i + 1).ToString(CultureInfo.InvariantCulture)));
        var more = sourceIndexes.Count > MaxRowsNamed ? $" and {sourceIndexes.Count - MaxRowsNamed} more" : "";
        return sourceIndexes.Count == 1 ? $"data row {named}" : $"{sourceIndexes.Count} data rows ({named}{more})";
    }

    private static string Clip(string text) => text.Length <= 60 ? text : text[..59] + "…";

    private sealed record WorkRow(CellValue[] Cells, int SourceIndex);
}

/// <summary>Sorting order: empty cells last in either direction; numbers and dates by value;
/// text ordinally; different kinds by kind, so the order is total and stable.</summary>
internal sealed class CellComparer(bool descending) : IComparer<CellValue>
{
    public static readonly CellComparer Ascending = new(false);
    public static readonly CellComparer Descending = new(true);

    public int Compare(CellValue x, CellValue y)
    {
        if (x.IsEmpty || y.IsEmpty)
            return x.IsEmpty == y.IsEmpty ? 0 : x.IsEmpty ? 1 : -1;
        var result = x.Kind != y.Kind
            ? x.Kind.CompareTo(y.Kind)
            : x.Kind switch
            {
                CellKind.Number => x.Number!.Value.CompareTo(y.Number!.Value),
                CellKind.Date => x.Date!.Value.CompareTo(y.Date!.Value),
                CellKind.Bool => x.Bool!.Value.CompareTo(y.Bool!.Value),
                _ => string.CompareOrdinal(x.Text, y.Text),
            };
        return descending ? -result : result;
    }
}
