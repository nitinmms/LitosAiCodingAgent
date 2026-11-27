using System.Globalization;
using Litos.Office.Data;
using Litos.Office.Plan;

namespace Litos.Office.Exec;

public enum ValidationLevel { Pass, Warn, Fail }

public sealed record ValidationItem(string Id, ValidationLevel Level, string Message, string? Detail = null);

public sealed record ValidationReport(IReadOnlyList<ValidationItem> Items)
{
    public bool HasFailures => Items.Any(i => i.Level == ValidationLevel.Fail);

    public ValidationItem? FirstFailure => Items.FirstOrDefault(i => i.Level == ValidationLevel.Fail);
}

/// <summary>
/// The checks a run must pass before its output is published (design §2 ValidationRunner,
/// spec §6 "validation"). The totals are recomputed here by a separate, deliberately simple loop
/// that shares no code with <see cref="PlanExecutor"/>, so a bug in the executor can't confirm
/// itself. A failure here means nothing is published.
/// </summary>
public static class ValidationRunner
{
    public const string RequiredColumns = "required_columns";
    public const string ValidNumbers = "valid_numbers";
    public const string KnownValues = "known_values";
    public const string BlankKeys = "blank_keys";
    public const string RowAccountingRule = "row_accounting";
    public const string SumReconciliation = "sum_reconciliation";
    public const string NoDuplicateGroups = "no_duplicate_groups";
    public const string OutputNotEmpty = "output_not_empty";
    public const string Roundtrip = "roundtrip";
    public const string Execution = "execution";

    public static IReadOnlyList<string> KnownRuleIds { get; } =
        [RequiredColumns, ValidNumbers, KnownValues, BlankKeys, RowAccountingRule, SumReconciliation, NoDuplicateGroups, OutputNotEmpty, Roundtrip];

    /// <summary>The checks every plan gets, from its own steps (design §2 PlanAssembler).</summary>
    public static ValidationSpec DefaultFor(WorkflowPlan plan)
    {
        var rules = new List<ValidationRule> { new(RequiredColumns) };
        if (NumericColumns(plan).Count > 0)
            rules.Add(new ValidationRule(ValidNumbers));
        foreach (var filter in plan.Steps.OfType<FilterStep>().Where(f => f.KnownValues is not null))
            rules.Add(new ValidationRule(KnownValues, filter.Column));
        if (plan.Steps.OfType<GroupAggregateStep>().Any())
        {
            rules.Add(new ValidationRule(BlankKeys));
            rules.Add(new ValidationRule(NoDuplicateGroups));
        }
        rules.Add(new ValidationRule(RowAccountingRule));
        rules.Add(new ValidationRule(SumReconciliation));
        rules.Add(new ValidationRule(OutputNotEmpty));
        rules.Add(new ValidationRule(Roundtrip));
        return new ValidationSpec(1, rules);
    }

    /// <summary>Runs <paramref name="spec"/> over a run's source and result. <paramref name="writtenBack"/>
    /// is the output re-read from the temporary file, when there is one; without it the
    /// round-trip check is not run.</summary>
    public static ValidationReport Run(WorkflowPlan plan, ValidationSpec spec, Table source, ExecResult result, Table? writtenBack = null)
    {
        var items = new List<ValidationItem>();
        foreach (var issue in result.Issues)
            items.Add(new ValidationItem(Execution, ValidationLevel.Fail, issue.Message, issue.Path));
        foreach (var warning in result.Warnings)
            items.Add(new ValidationItem(Execution, ValidationLevel.Warn, warning));

        var missing = plan.Inputs[0].RequiredColumns.Where(c => source.IndexOf(c) < 0).ToList();
        foreach (var rule in spec.Rules)
        {
            switch (rule.Id)
            {
                case RequiredColumns:
                    items.Add(missing.Count == 0
                        ? Pass(RequiredColumns, "All the columns the skill needs are there.")
                        : Fail(RequiredColumns, $"Column {string.Join(", ", missing.Select(m => $"\"{m}\""))} is missing. The file layout changed since this skill was learned."));
                    break;
                case ValidNumbers when missing.Count == 0:
                    items.Add(CheckNumbers(plan, source));
                    break;
                case KnownValues when missing.Count == 0 && rule.Column is not null:
                    items.Add(CheckKnown(plan, source, rule.Column));
                    break;
                case BlankKeys when missing.Count == 0:
                    items.Add(CheckBlankKeys(plan, source));
                    break;
                case RowAccountingRule when missing.Count == 0:
                    items.Add(CheckAccounting(plan, source, result));
                    break;
                case SumReconciliation when missing.Count == 0:
                    items.AddRange(CheckSums(plan, source, result));
                    break;
                case NoDuplicateGroups:
                    items.Add(CheckDuplicates(plan, result));
                    break;
                case OutputNotEmpty:
                    items.Add(result.Output.Rows.Count > 0
                        ? Pass(OutputNotEmpty, $"The report has {result.Output.Rows.Count} row(s).")
                        : new ValidationItem(OutputNotEmpty, ValidationLevel.Warn, "The report has no rows."));
                    break;
                case Roundtrip when writtenBack is not null:
                    items.Add(CheckRoundtrip(result, writtenBack));
                    break;
                case RequiredColumns or ValidNumbers or KnownValues or BlankKeys or RowAccountingRule or SumReconciliation or NoDuplicateGroups or OutputNotEmpty or Roundtrip:
                    break;
                default:
                    items.Add(Fail(rule.Id, $"Unknown check \"{rule.Id}\"; the skill's validation file may have been edited."));
                    break;
            }
        }
        return new ValidationReport(items);
    }

    // ---- independent row evaluation ------------------------------------------------------------

    /// <summary>Which source rows the plan's filters keep, worked out without PlanExecutor.
    /// Derived-column filters can't be evaluated here; null means "not independently checkable".</summary>
    private static bool[]? IncludedRows(WorkflowPlan plan, Table source)
    {
        var filters = new List<(int Index, FilterStep Step)>();
        foreach (var step in plan.Steps)
        {
            if (step is GroupAggregateStep)
                break;
            if (step is FilterStep filter)
            {
                var index = source.IndexOf(filter.Column);
                if (index < 0)
                    return null;
                filters.Add((index, filter));
            }
        }

        var included = new bool[source.Rows.Count];
        for (var r = 0; r < source.Rows.Count; r++)
        {
            var keep = true;
            foreach (var (index, filter) in filters)
                keep &= Keeps(filter, source.Rows[r][index]);
            included[r] = keep;
        }
        return included;
    }

    private static bool Keeps(FilterStep filter, CellValue cell)
    {
        var negated = filter.Operator is FilterOperator.NotEquals or FilterOperator.NotIn;
        if (cell.IsEmpty)
            return negated;

        var literals = filter.Values ?? [filter.Value!];
        int Compare(string literal) => filter.ValueType switch
        {
            FilterValueType.Number when cell.Kind == CellKind.Number =>
                cell.Number!.Value.CompareTo(decimal.Parse(literal, NumberStyles.Number, CultureInfo.InvariantCulture)),
            FilterValueType.Date when cell.Kind == CellKind.Date =>
                cell.Date!.Value.Date.CompareTo(DateTime.ParseExact(literal, "yyyy-MM-dd", CultureInfo.InvariantCulture)),
            FilterValueType.Text => string.CompareOrdinal(cell.ToDisplay().Trim(), literal.Trim()) == 0 ? 0 : 1,
            _ => int.MinValue,
        };

        var comparisons = literals.Select(Compare).ToList();
        if (comparisons.Contains(int.MinValue))
            return false;
        return filter.Operator switch
        {
            FilterOperator.Equals or FilterOperator.In => comparisons.Any(c => c == 0),
            FilterOperator.NotEquals or FilterOperator.NotIn => comparisons.All(c => c != 0),
            FilterOperator.Gt => comparisons[0] > 0,
            FilterOperator.Gte => comparisons[0] >= 0,
            FilterOperator.Lt => comparisons[0] < 0,
            FilterOperator.Lte => comparisons[0] <= 0,
            _ => false,
        };
    }

    // ---- the checks -----------------------------------------------------------------------------

    private static ValidationItem CheckNumbers(WorkflowPlan plan, Table source)
    {
        var bad = new List<string>();
        foreach (var column in NumericColumns(plan))
        {
            var index = source.IndexOf(column);
            if (index < 0)
                continue;
            var count = source.Rows.Count(r => r[index].Kind != CellKind.Number);
            if (count > 0)
                bad.Add($"\"{column}\" ({count} blank or non-number cell(s))");
        }
        return bad.Count == 0
            ? Pass(ValidNumbers, "Every number the skill adds up is a number.")
            : Fail(ValidNumbers, $"Some cells that should be numbers aren't: {string.Join("; ", bad)}. Litos doesn't count them as zero.");
    }

    private static ValidationItem CheckKnown(WorkflowPlan plan, Table source, string column)
    {
        var filter = plan.Steps.OfType<FilterStep>().FirstOrDefault(f => f.Column == column && f.KnownValues is not null);
        var index = source.IndexOf(column);
        if (filter is null || index < 0)
            return Pass(KnownValues, $"No known-value list for \"{column}\".");
        var known = filter.KnownValues!.Select(v => v.Trim()).ToHashSet(StringComparer.Ordinal);
        var unknown = source.Rows.Select(r => r[index]).Where(c => !c.IsEmpty)
            .Select(c => c.ToDisplay().Trim()).Where(v => !known.Contains(v))
            .GroupBy(v => v, StringComparer.Ordinal).ToList();
        return unknown.Count == 0
            ? Pass(KnownValues, $"Every \"{column}\" value is one the skill was taught about.")
            : Fail(KnownValues, $"\"{column}\" has value(s) the skill wasn't taught about: {string.Join(", ", unknown.Take(5).Select(g => $"\"{Clip(g.Key)}\" ({g.Count()})"))}.");
    }

    private static ValidationItem CheckBlankKeys(WorkflowPlan plan, Table source)
    {
        var group = plan.Steps.OfType<GroupAggregateStep>().FirstOrDefault();
        var included = IncludedRows(plan, source);
        if (group is null)
            return Pass(BlankKeys, "No grouping.");
        if (included is null)
            return new ValidationItem(BlankKeys, ValidationLevel.Warn, "Blank group keys couldn't be checked independently.");
        var indexes = group.GroupBy.Select(source.IndexOf).ToArray();
        if (indexes.Any(i => i < 0))
            return new ValidationItem(BlankKeys, ValidationLevel.Warn, "Blank group keys couldn't be checked independently.");
        var blanks = source.Rows.Where((r, i) => included[i] && indexes.Any(k => r[k].IsEmpty)).Count();
        if (blanks == 0)
            return Pass(BlankKeys, "Every included row has a group.");
        return plan.Policies.BlankKey == PolicyAction.Keep
            ? new ValidationItem(BlankKeys, ValidationLevel.Warn, $"{blanks} row(s) have a blank group and were grouped as blank, as this skill was told to.")
            : Fail(BlankKeys, $"{blanks} row(s) have a blank {string.Join(" / ", group.GroupBy)}.");
    }

    private static ValidationItem CheckAccounting(WorkflowPlan plan, Table source, ExecResult result)
    {
        var accounting = result.Accounting;
        if (accounting.Included + accounting.Excluded != accounting.SourceRows || accounting.SourceRows != source.Rows.Count)
            return Fail(RowAccountingRule, $"Rows don't add up: {accounting.Included} included + {accounting.Excluded} excluded ≠ {source.Rows.Count} in the file.");
        var included = IncludedRows(plan, source);
        if (included is not null && !plan.Steps.OfType<DerivedColumnStep>().Any())
        {
            // "Included" is counted after the filters and before grouping, so rows with a blank group
            // key are still in it (the blank-key check reports those).
            var independent = included.Count(i => i);
            if (independent != accounting.Included)
                return Fail(RowAccountingRule, $"The run included {accounting.Included} row(s), but checking the rules again gives {independent}.");
        }
        return Pass(RowAccountingRule, $"{accounting.Included} row(s) included + {accounting.Excluded} excluded = {accounting.SourceRows} in the file.");
    }

    private static IEnumerable<ValidationItem> CheckSums(WorkflowPlan plan, Table source, ExecResult result)
    {
        var group = plan.Steps.OfType<GroupAggregateStep>().FirstOrDefault();
        var write = plan.Steps.OfType<WriteWorkbookStep>().Single();
        var included = IncludedRows(plan, source);
        var checkedAny = false;

        if (group is not null)
        {
            foreach (var aggregate in group.Aggregates.Where(a => a.Function == AggregateFunction.Sum))
            {
                var outputIndex = write.Columns.ToList().FindIndex(c => c.From == aggregate.As);
                var sourceIndex = source.IndexOf(aggregate.Column!);
                if (outputIndex < 0)
                    continue;
                if (included is null || sourceIndex < 0)
                {
                    yield return new ValidationItem(SumReconciliation, ValidationLevel.Warn, $"\"{aggregate.As}\" is worked out from a calculated column, so it couldn't be checked independently.");
                    continue;
                }
                checkedAny = true;
                var expected = 0m;
                for (var r = 0; r < source.Rows.Count; r++)
                    if (included[r] && source.Rows[r][sourceIndex].Kind == CellKind.Number)
                        expected += source.Rows[r][sourceIndex].Number!.Value;
                var reported = result.Output.Rows.Sum(r => r[outputIndex].Number ?? 0m);
                var total = result.GrandTotal?[outputIndex].Number;
                if (reported != expected)
                    yield return Fail(SumReconciliation, $"\"{write.Columns[outputIndex].As}\" adds up to {Num(reported)}, but the included rows of \"{aggregate.Column}\" add up to {Num(expected)}.");
                else if (total is not null && total != expected)
                    yield return Fail(SumReconciliation, $"The grand total of \"{write.Columns[outputIndex].As}\" is {Num(total.Value)}, but it should be {Num(expected)}.");
                else
                    yield return Pass(SumReconciliation, $"\"{write.Columns[outputIndex].As}\" totals {Num(expected)}, matching the included rows.");
            }

            foreach (var aggregate in group.Aggregates.Where(a => a.Function == AggregateFunction.Count && a.Column is null))
            {
                var outputIndex = write.Columns.ToList().FindIndex(c => c.From == aggregate.As);
                if (outputIndex < 0 || included is null)
                    continue;
                checkedAny = true;
                var reported = result.Output.Rows.Sum(r => r[outputIndex].Number ?? 0m);
                var expected = included.Count(i => i);
                yield return reported == expected
                    ? Pass(SumReconciliation, $"\"{write.Columns[outputIndex].As}\" counts {expected} row(s), matching the included rows.")
                    : Fail(SumReconciliation, $"\"{write.Columns[outputIndex].As}\" counts {Num(reported)} row(s), but {expected} rows were included.");
            }
        }
        else if (result.GrandTotal is { } grandTotal)
        {
            for (var c = 0; c < write.Columns.Count; c++)
            {
                if (grandTotal[c].Kind != CellKind.Number)
                    continue;
                checkedAny = true;
                var sum = result.Output.Rows.Sum(r => r[c].Number ?? 0m);
                yield return sum == grandTotal[c].Number
                    ? Pass(SumReconciliation, $"The grand total of \"{write.Columns[c].As}\" matches its rows.")
                    : Fail(SumReconciliation, $"The grand total of \"{write.Columns[c].As}\" is {Num(grandTotal[c].Number!.Value)}, but its rows add up to {Num(sum)}.");
            }
        }

        if (!checkedAny)
            yield return Pass(SumReconciliation, "There are no totals to reconcile.");
    }

    private static ValidationItem CheckDuplicates(WorkflowPlan plan, ExecResult result)
    {
        var group = plan.Steps.OfType<GroupAggregateStep>().FirstOrDefault();
        var write = plan.Steps.OfType<WriteWorkbookStep>().Single();
        if (group is null)
            return Pass(NoDuplicateGroups, "No grouping.");
        var keyIndexes = group.GroupBy.Select(k => write.Columns.ToList().FindIndex(c => c.From == k)).Where(i => i >= 0).ToArray();
        if (keyIndexes.Length != group.GroupBy.Count)
            return Pass(NoDuplicateGroups, "The group columns aren't all in the report, so duplicates can't be checked there.");
        var duplicates = result.Output.Rows
            .GroupBy(r => string.Join('\u001F', keyIndexes.Select(i => r[i].ToDisplay().Trim())), StringComparer.Ordinal)
            .Where(g => g.Count() > 1).Select(g => g.Key.Replace('\u001F', '/')).ToList();
        return duplicates.Count == 0
            ? Pass(NoDuplicateGroups, "Each group appears once.")
            : Fail(NoDuplicateGroups, $"Group(s) appear more than once: {string.Join(", ", duplicates.Take(5).Select(Clip))}.");
    }

    private static ValidationItem CheckRoundtrip(ExecResult result, Table writtenBack)
    {
        var expectedRows = result.Output.Rows.ToList();
        if (result.GrandTotal is not null)
            expectedRows.Add(result.GrandTotal);
        if (!writtenBack.ColumnNames.SequenceEqual(result.Output.ColumnNames, StringComparer.Ordinal))
            return Fail(Roundtrip, "The saved report's headings don't match what was written.");
        if (writtenBack.Rows.Count != expectedRows.Count)
            return Fail(Roundtrip, $"The saved report has {writtenBack.Rows.Count} row(s), but {expectedRows.Count} were written.");
        for (var r = 0; r < expectedRows.Count; r++)
            for (var c = 0; c < expectedRows[r].Length; c++)
                if (!SameValue(expectedRows[r][c], writtenBack.Rows[r][c]))
                    return Fail(Roundtrip, $"Row {r + 1}, column \"{result.Output.Columns[c].Name}\" was written as {expectedRows[r][c].ToDisplay()} but reads back as {writtenBack.Rows[r][c].ToDisplay()}.");
        return Pass(Roundtrip, "The saved report reads back exactly as written.");
    }

    private static bool SameValue(CellValue written, CellValue read)
    {
        if (written.IsEmpty || read.IsEmpty)
            return written.IsEmpty == read.IsEmpty;
        if (written.Kind == CellKind.Number && read.Kind == CellKind.Number)
            return Math.Abs(written.Number!.Value - read.Number!.Value) <= Math.Abs(written.Number.Value) * 1e-14m + 1e-14m;
        return written.Kind == read.Kind && written.ToDisplay() == read.ToDisplay();
    }

    /// <summary>Source columns the plan treats as numbers: summed/averaged inputs, number filters
    /// and derived-column operands.</summary>
    private static List<string> NumericColumns(WorkflowPlan plan)
    {
        var columns = new List<string>();
        var derived = plan.Steps.OfType<DerivedColumnStep>().Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var step in plan.Steps)
        {
            switch (step)
            {
                case GroupAggregateStep group:
                    columns.AddRange(group.Aggregates.Where(a => a.Function != AggregateFunction.Count && a.Column is not null).Select(a => a.Column!));
                    break;
                case FilterStep { ValueType: FilterValueType.Number } filter:
                    columns.Add(filter.Column);
                    break;
                case DerivedColumnStep { Expr.Fn: not (DerivedFunction.Year or DerivedFunction.Month) } d:
                    columns.AddRange(d.Expr.Args.Where(a => a.Column is not null).Select(a => a.Column!));
                    break;
            }
        }
        return columns.Where(c => !derived.Contains(c)).Distinct(StringComparer.Ordinal).ToList();
    }

    private static ValidationItem Pass(string id, string message) => new(id, ValidationLevel.Pass, message);

    private static ValidationItem Fail(string id, string message) => new(id, ValidationLevel.Fail, message);

    private static string Num(decimal value) => value.ToString("0.##########", CultureInfo.InvariantCulture);

    private static string Clip(string text) => text.Length <= 60 ? text : text[..59] + "…";
}
