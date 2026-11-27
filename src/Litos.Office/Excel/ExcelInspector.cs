using System.Globalization;
using System.Text;
using Litos.Office.Data;

namespace Litos.Office.Excel;

public sealed record ValueCount(string Value, int Count);

public sealed record ColumnSummary(
    string Name,
    ColumnType Type,
    int Distinct,
    int Blanks,
    IReadOnlyList<ValueCount> TopValues,
    string? Min,
    string? Max);

/// <summary>
/// A bounded description of a table: what may be shown to a model in place of the file (spec §5:
/// minimise data sent, never upload whole files). Every string in it has been through
/// <see cref="UntrustedText.Sanitize"/>.
/// </summary>
public sealed record SheetSummary(
    string SheetName,
    int RowCount,
    IReadOnlyList<ColumnSummary> Columns,
    IReadOnlyList<IReadOnlyList<string>> SampleRows,
    bool HasTotalRow)
{
    /// <summary>The summary as compact text, cell-derived parts unwrapped (the caller wraps the
    /// whole block as untrusted).</summary>
    public string ToText()
    {
        var text = new StringBuilder();
        text.Append("Sheet: ").Append(UntrustedText.Sanitize(SheetName, 31)).Append("; rows: ").Append(RowCount.ToString(CultureInfo.InvariantCulture));
        if (HasTotalRow)
            text.Append(" (last row is a total row)");
        text.Append('\n');
        foreach (var column in Columns)
        {
            text.Append("- ").Append(column.Name).Append(" [").Append(column.Type).Append("] distinct ")
                .Append(column.Distinct.ToString(CultureInfo.InvariantCulture)).Append(", blank ").Append(column.Blanks.ToString(CultureInfo.InvariantCulture));
            if (column.Min is not null)
                text.Append(", min ").Append(column.Min).Append(", max ").Append(column.Max);
            if (column.TopValues.Count > 0)
                text.Append(", values: ").Append(string.Join(" | ", column.TopValues.Select(v => $"{v.Value} ({v.Count})")));
            text.Append('\n');
        }
        if (SampleRows.Count > 0)
        {
            text.Append("Sample rows:\n");
            foreach (var row in SampleRows)
                text.Append("  ").Append(string.Join(" | ", row)).Append('\n');
        }
        return text.ToString();
    }
}

public sealed record SummaryOptions(int MaxTopValues = 8, int MaxValueChars = 40, int MaxSampleRows = 5, int MaxDistinctForValues = 50);

public static class ExcelInspector
{
    public static SheetSummary Summarize(Table table, SheetStyle style, SummaryOptions? options = null)
    {
        options ??= new SummaryOptions();
        var columns = new List<ColumnSummary>();
        for (var i = 0; i < table.Columns.Count; i++)
        {
            var column = table.Columns[i];
            var values = table.ColumnValues(i).ToList();
            var blanks = values.Count(v => v.IsEmpty);
            var groups = values.Where(v => !v.IsEmpty)
                .GroupBy(v => v.ToDisplay(), StringComparer.Ordinal)
                .Select(g => (Value: g.Key, Count: g.Count()))
                .ToList();

            // Listing values is useful for categories (Region, Status); for an Amount or an Id
            // column it would just leak rows, so only low-cardinality text columns get a list.
            var top = column.Type is ColumnType.Text or ColumnType.Bool or ColumnType.Mixed && groups.Count <= options.MaxDistinctForValues
                ? groups.OrderByDescending(g => g.Count).ThenBy(g => g.Value, StringComparer.Ordinal)
                    .Take(options.MaxTopValues)
                    .Select(g => new ValueCount(UntrustedText.Sanitize(g.Value, options.MaxValueChars), g.Count))
                    .ToList()
                : [];

            string? min = null, max = null;
            if (column.Type == ColumnType.Number)
            {
                var numbers = values.Where(v => v.Kind == CellKind.Number).Select(v => v.Number!.Value).ToList();
                if (numbers.Count > 0)
                {
                    min = numbers.Min().ToString(CultureInfo.InvariantCulture);
                    max = numbers.Max().ToString(CultureInfo.InvariantCulture);
                }
            }
            else if (column.Type == ColumnType.Date)
            {
                var dates = values.Where(v => v.Kind == CellKind.Date).Select(v => v.Date!.Value).ToList();
                if (dates.Count > 0)
                {
                    min = dates.Min().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    max = dates.Max().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                }
            }

            columns.Add(new ColumnSummary(UntrustedText.Sanitize(column.Name, 64), column.Type, groups.Count, blanks, top, min, max));
        }

        var samples = table.Rows.Take(options.MaxSampleRows)
            .Select(r => (IReadOnlyList<string>)r.Select(c => UntrustedText.Sanitize(c.ToDisplay(), options.MaxValueChars)).ToList())
            .ToList();

        return new SheetSummary(style.SheetName, table.Rows.Count, columns, samples, style.HasTotalRow);
    }
}
