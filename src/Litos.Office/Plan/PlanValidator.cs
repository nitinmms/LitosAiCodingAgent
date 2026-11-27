using System.Globalization;
using Litos.Office.Data;

namespace Litos.Office.Plan;

/// <summary>A problem with a plan: where it is ("steps[2].column") and what is wrong.</summary>
public sealed record PlanIssue(string Path, string Message)
{
    public override string ToString() => $"{Path}: {Message}";
}

/// <summary>The real sheet a plan is about to run on (test or scheduled run).</summary>
public sealed record SchemaContext(string? SheetName, IReadOnlyDictionary<string, ColumnType> Columns)
{
    public static SchemaContext Of(Table table, string? sheetName) =>
        new(sheetName, table.Columns.ToDictionary(c => c.Name, c => c.Type, StringComparer.Ordinal));
}

/// <summary>
/// Everything a plan must satisfy before it is saved or run (design §4 "Validator rules"). Run after
/// assembly, again on every load for a scheduled run (so a hand-edited workflow.json can't slip
/// through), and with a <see cref="SchemaContext"/> against the real input file. Column references
/// are checked against a running schema: what exists at that step, after earlier steps changed it.
/// </summary>
public static class PlanValidator
{
    public const int MaxSteps = 12;
    public const int MaxRequiredColumns = 100;
    public const int MaxFilterValues = 200;
    public const int MaxValueChars = 200;
    public const int MaxNameChars = 64;
    public const string TemplateFileName = "report-template.xlsx";
    private static readonly string[] AllowedTokens = ["{date}", "{period}", "{input}"];

    public static IReadOnlyList<PlanIssue> Validate(WorkflowPlan plan, SchemaContext? context = null)
    {
        var issues = new List<PlanIssue>();
        void Issue(string path, string message) => issues.Add(new PlanIssue(path, message));

        if (plan.SchemaVersion != WorkflowPlan.CurrentSchemaVersion)
            Issue("schemaVersion", $"Only schema version {WorkflowPlan.CurrentSchemaVersion} is supported.");
        if (!SkillNames.IsValid(plan.SkillId))
            Issue("skillId", "Use lower-case letters, digits and hyphens, starting with a letter.");
        if (plan.Version < 1)
            Issue("version", "The version must be 1 or more.");

        // ---- input ----
        var schema = new Dictionary<string, ColumnType?>(StringComparer.Ordinal);
        if (plan.Inputs is not { Count: 1 })
        {
            Issue("inputs", "A plan has exactly one input workbook in this version.");
        }
        else
        {
            var input = plan.Inputs[0];
            if (input.Format != "xlsx")
                Issue("inputs[0].format", "Only xlsx inputs are supported.");
            if (input.Sheet is not null && !IsValidSheetName(input.Sheet))
                Issue("inputs[0].sheet", "That isn't a valid Excel sheet name.");
            if (input.RequiredColumns is not { Count: > 0 })
                Issue("inputs[0].requiredColumns", "List the columns the input must have.");
            else if (input.RequiredColumns.Count > MaxRequiredColumns)
                Issue("inputs[0].requiredColumns", $"At most {MaxRequiredColumns} columns.");
            else
            {
                if (input.RequiredColumns.Distinct(StringComparer.Ordinal).Count() != input.RequiredColumns.Count)
                    Issue("inputs[0].requiredColumns", "A column is listed twice.");
                foreach (var column in input.RequiredColumns)
                {
                    if (string.IsNullOrWhiteSpace(column))
                    {
                        Issue("inputs[0].requiredColumns", "A required column name is blank.");
                        continue;
                    }
                    ColumnType? type = null;
                    if (context is not null)
                    {
                        if (context.Columns.TryGetValue(column, out var real))
                            type = real;
                        else
                            Issue("inputs[0].requiredColumns", $"The input has no column \"{column}\".");
                    }
                    schema[column] = type;
                }
            }

            if (context is not null && input.Sheet is not null && context.SheetName is not null
                && !string.Equals(input.Sheet, context.SheetName, StringComparison.Ordinal))
                Issue("inputs[0].sheet", $"The input sheet is \"{context.SheetName}\", not \"{input.Sheet}\".");
        }

        // ---- steps ----
        if (plan.Steps is not { Count: > 0 })
        {
            Issue("steps", "A plan needs at least one step.");
        }
        else
        {
            if (plan.Steps.Count > MaxSteps)
                Issue("steps", $"At most {MaxSteps} steps.");
            var groupSeen = false;
            for (var i = 0; i < plan.Steps.Count; i++)
            {
                var path = $"steps[{i}]";
                var step = plan.Steps[i];
                var isLast = i == plan.Steps.Count - 1;
                if (step is WriteWorkbookStep != isLast)
                    Issue(path, isLast ? "The last step must write the workbook." : "Only the last step may write the workbook.");

                switch (step)
                {
                    case FilterStep filter:
                        CheckFilter(filter, path, schema, Issue);
                        break;
                    case DerivedColumnStep derived:
                        CheckDerived(derived, path, schema, Issue);
                        break;
                    case GroupAggregateStep group:
                        if (groupSeen)
                            Issue(path, "Only one grouping step is allowed.");
                        groupSeen = true;
                        CheckGroup(group, path, schema, Issue);
                        break;
                    case SortStep sort:
                        CheckSort(sort, path, schema, Issue);
                        break;
                    case WriteWorkbookStep write:
                        CheckWrite(write, path, schema, Issue);
                        break;
                    default:
                        Issue(path, "Unknown step.");
                        break;
                }
            }
        }

        // ---- output and policies ----
        if (plan.Output is null)
            Issue("output", "The output is missing.");
        else
        {
            if (plan.Output.Format != "xlsx")
                Issue("output.format", "Only xlsx output is supported.");
            if (plan.Output.Overwrite)
                Issue("output.overwrite", "Overwriting existing files is not allowed.");
            CheckFileNamePattern(plan.Output.FileNamePattern, Issue);
        }

        if (plan.Policies is null)
            Issue("policies", "The policies are missing.");
        else
        {
            if (plan.Policies.UnknownValue != PolicyAction.Fail)
                Issue("policies.unknownValue", "An unknown value must stop the run in this version.");
            if (plan.Policies.InvalidNumber != PolicyAction.Fail)
                Issue("policies.invalidNumber", "A value that isn't a number must stop the run in this version.");
        }

        return issues;
    }

    private static void CheckFilter(FilterStep filter, string path, Dictionary<string, ColumnType?> schema, Action<string, string> issue)
    {
        if (!Exists(filter.Column, schema, $"{path}.column", issue))
            return;
        var type = schema[filter.Column];
        var isList = filter.Operator is FilterOperator.In or FilterOperator.NotIn;
        var isComparison = filter.Operator is FilterOperator.Gt or FilterOperator.Gte or FilterOperator.Lt or FilterOperator.Lte;

        if (isComparison && filter.ValueType == FilterValueType.Text)
            issue($"{path}.operator", "Greater/less-than comparisons need a number or date value.");
        if (filter.ValueType == FilterValueType.Number && type is not null and not ColumnType.Number)
            issue($"{path}.valueType", $"\"{filter.Column}\" isn't a number column.");
        if (filter.ValueType == FilterValueType.Date && type is not null and not ColumnType.Date)
            issue($"{path}.valueType", $"\"{filter.Column}\" isn't a date column.");

        var literals = isList ? filter.Values : filter.Value is null ? null : [filter.Value];
        if (isList && filter.Value is not null || !isList && filter.Values is not null)
            issue(path, isList ? "Use \"values\" (a list) with in/notIn." : "Use \"value\" (one value) with this operator.");
        if (literals is not { Count: > 0 })
            issue(path, "The filter has no value.");
        else
        {
            if (literals.Count > MaxFilterValues)
                issue($"{path}.values", $"At most {MaxFilterValues} values.");
            foreach (var literal in literals)
            {
                if (literal is null || literal.Length > MaxValueChars)
                    issue(path, $"Each value must be at most {MaxValueChars} characters.");
                else if (filter.ValueType == FilterValueType.Number && !TryNumber(literal, out _))
                    issue(path, $"\"{literal}\" isn't a number.");
                else if (filter.ValueType == FilterValueType.Date && !TryDate(literal, out _))
                    issue(path, $"\"{literal}\" isn't a date in yyyy-MM-dd form.");
            }
        }

        if (filter.KnownValues is not null)
        {
            if (filter.Operator is not (FilterOperator.Equals or FilterOperator.NotEquals or FilterOperator.In or FilterOperator.NotIn))
                issue($"{path}.knownValues", "Known values apply only to equals/notEquals/in/notIn filters.");
            if (filter.KnownValues.Count > MaxFilterValues || filter.KnownValues.Any(v => v is null || v.Length > MaxValueChars))
                issue($"{path}.knownValues", $"At most {MaxFilterValues} known values, each at most {MaxValueChars} characters.");
        }
    }

    private static void CheckDerived(DerivedColumnStep derived, string path, Dictionary<string, ColumnType?> schema, Action<string, string> issue)
    {
        if (!CheckNewName(derived.Name, $"{path}.name", schema, issue))
            return;
        var args = derived.Expr?.Args ?? [];
        var expected = derived.Expr?.Fn switch
        {
            DerivedFunction.Add or DerivedFunction.Sub or DerivedFunction.Mul or DerivedFunction.Div or DerivedFunction.Round => 2,
            DerivedFunction.Year or DerivedFunction.Month => 1,
            _ => -1,
        };
        if (expected < 0)
            issue($"{path}.expr", "Unknown function.");
        else if (args.Count != expected)
            issue($"{path}.expr.args", $"This function takes {expected} argument(s).");

        for (var a = 0; a < args.Count; a++)
        {
            var arg = args[a];
            var argPath = $"{path}.expr.args[{a}]";
            if ((arg.Column is null) == (arg.Number is null))
            {
                issue(argPath, "Give either a column or a number.");
                continue;
            }
            if (arg.Column is not null)
            {
                if (Exists(arg.Column, schema, argPath, issue))
                {
                    var wanted = derived.Expr!.Fn is DerivedFunction.Year or DerivedFunction.Month ? ColumnType.Date : ColumnType.Number;
                    if (schema[arg.Column] is { } t && t != wanted)
                        issue(argPath, $"\"{arg.Column}\" isn't a {wanted.ToString().ToLowerInvariant()} column.");
                }
            }
            else if (!TryNumber(arg.Number!, out var number))
                issue(argPath, $"\"{arg.Number}\" isn't a number.");
            else if (derived.Expr!.Fn == DerivedFunction.Div && a == 1 && number == 0)
                issue(argPath, "Dividing by zero isn't allowed.");
            else if (derived.Expr!.Fn == DerivedFunction.Round && a == 1 && (number < 0 || number > 10 || number != decimal.Truncate(number)))
                issue(argPath, "Round to a whole number of decimals between 0 and 10.");
        }

        if (derived.Expr?.Fn == DerivedFunction.Round && args.Count == 2 && args[1].Column is not null)
            issue($"{path}.expr.args[1]", "Round to a fixed number of decimals, not a column.");

        schema[derived.Name] = ColumnType.Number;
    }

    private static void CheckGroup(GroupAggregateStep group, string path, Dictionary<string, ColumnType?> schema, Action<string, string> issue)
    {
        var keys = group.GroupBy ?? [];
        if (keys.Count == 0)
            issue($"{path}.groupBy", "Group by at least one column.");
        foreach (var key in keys)
            Exists(key, schema, $"{path}.groupBy", issue);
        if (keys.Distinct(StringComparer.Ordinal).Count() != keys.Count)
            issue($"{path}.groupBy", "A column is listed twice.");

        var after = keys.Where(schema.ContainsKey).ToDictionary(k => k, k => schema[k], StringComparer.Ordinal);
        var aggregates = group.Aggregates ?? [];
        if (aggregates.Count == 0)
            issue($"{path}.aggregates", "Add at least one total, count or average.");
        for (var a = 0; a < aggregates.Count; a++)
        {
            var aggregate = aggregates[a];
            var aPath = $"{path}.aggregates[{a}]";
            if (aggregate.Function == AggregateFunction.Count)
            {
                if (aggregate.Column is not null)
                    Exists(aggregate.Column, schema, $"{aPath}.column", issue);
            }
            else if (aggregate.Column is null)
                issue($"{aPath}.column", "Say which column to total.");
            else if (Exists(aggregate.Column, schema, $"{aPath}.column", issue) && schema[aggregate.Column] is { } t && t != ColumnType.Number)
                issue($"{aPath}.column", $"\"{aggregate.Column}\" isn't a number column.");

            if (IsValidName(aggregate.As, $"{aPath}.as", issue))
            {
                if (after.ContainsKey(aggregate.As))
                    issue($"{aPath}.as", $"\"{aggregate.As}\" is already a column name.");
                after[aggregate.As] = ColumnType.Number;
            }
        }

        schema.Clear();
        foreach (var (name, type) in after)
            schema[name] = type;
    }

    private static void CheckSort(SortStep sort, string path, Dictionary<string, ColumnType?> schema, Action<string, string> issue)
    {
        if (sort.Keys is not { Count: > 0 })
        {
            issue($"{path}.keys", "Sort by at least one column.");
            return;
        }
        foreach (var key in sort.Keys)
            Exists(key.Column, schema, $"{path}.keys", issue);
    }

    private static void CheckWrite(WriteWorkbookStep write, string path, Dictionary<string, ColumnType?> schema, Action<string, string> issue)
    {
        if (write.Template != TemplateFileName)
            issue($"{path}.template", $"The template must be the skill's own \"{TemplateFileName}\".");
        if (!IsValidSheetName(write.Sheet))
            issue($"{path}.sheet", "That isn't a valid Excel sheet name.");
        if (write.Columns is not { Count: > 0 })
            issue($"{path}.columns", "Choose at least one column for the report.");
        else
        {
            var headings = new HashSet<string>(StringComparer.Ordinal);
            for (var c = 0; c < write.Columns.Count; c++)
            {
                var map = write.Columns[c];
                Exists(map.From, schema, $"{path}.columns[{c}].from", issue);
                if (IsValidName(map.As, $"{path}.columns[{c}].as", issue) && !headings.Add(map.As))
                    issue($"{path}.columns[{c}].as", $"Two report columns are headed \"{map.As}\".");
            }
        }
        if (write.GrandTotalLabel is { } label && (label.Length > MaxNameChars || label.Any(char.IsControl)))
            issue($"{path}.grandTotalLabel", $"The total label must be at most {MaxNameChars} characters.");
    }

    private static void CheckFileNamePattern(string? pattern, Action<string, string> issue)
    {
        const string path = "output.fileNamePattern";
        if (string.IsNullOrWhiteSpace(pattern))
        {
            issue(path, "Give the report a file name.");
            return;
        }
        if (pattern.Length > 100)
            issue(path, "At most 100 characters.");
        if (!pattern.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            issue(path, "The file name must end in .xlsx.");
        if (pattern.IndexOfAny(['/', '\\', ':']) >= 0 || pattern.Contains("..", StringComparison.Ordinal))
            issue(path, "The file name can't contain a folder or '..'.");
        var stripped = AllowedTokens.Aggregate(pattern, (p, t) => p.Replace(t, "", StringComparison.Ordinal));
        if (stripped.IndexOfAny(['{', '}']) >= 0)
            issue(path, "Only {date}, {period} and {input} can be used in the file name.");
    }

    private static bool Exists(string? column, Dictionary<string, ColumnType?> schema, string path, Action<string, string> issue)
    {
        if (column is not null && schema.ContainsKey(column))
            return true;
        issue(path, $"There is no column \"{column}\" at this step.");
        return false;
    }

    private static bool CheckNewName(string? name, string path, Dictionary<string, ColumnType?> schema, Action<string, string> issue)
    {
        if (!IsValidName(name, path, issue))
            return false;
        if (schema.ContainsKey(name!))
        {
            issue(path, $"\"{name}\" is already a column name.");
            return false;
        }
        return true;
    }

    /// <summary>A heading or new column name: 1–64 characters, no control characters, and never
    /// starting like a formula.</summary>
    private static bool IsValidName(string? name, string path, Action<string, string> issue)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameChars || name.Any(char.IsControl))
        {
            issue(path, $"Names must be 1–{MaxNameChars} characters.");
            return false;
        }
        if (name[0] is '=' or '+' or '-' or '@')
        {
            issue(path, "A name can't start with = + - or @.");
            return false;
        }
        return true;
    }

    internal static bool IsValidSheetName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 31 && name.IndexOfAny(['[', ']', ':', '*', '?', '/', '\\']) < 0
        && !name.StartsWith('\'') && !name.EndsWith('\'');

    public static bool TryNumber(string text, out decimal value) =>
        decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value);

    public static bool TryDate(string text, out DateTime value) =>
        DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
}
