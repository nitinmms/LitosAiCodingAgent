namespace Litos.Office.Data;

public enum ColumnType { Empty, Text, Number, Date, Bool, Mixed }

public sealed record Column(string Name, ColumnType Type);

/// <summary>
/// A plain table: one header row of unique names, then rows of cells. Every row has exactly one
/// cell per column (short rows are padded with empty cells), so code downstream never checks
/// lengths.
/// </summary>
public sealed class Table
{
    public Table(IReadOnlyList<Column> columns, IReadOnlyList<CellValue[]> rows)
    {
        Columns = columns;
        Rows = rows.Select(r => r.Length == columns.Count ? r : Pad(r, columns.Count)).ToList();
    }

    public IReadOnlyList<Column> Columns { get; }

    public IReadOnlyList<CellValue[]> Rows { get; }

    public IEnumerable<string> ColumnNames => Columns.Select(c => c.Name);

    /// <summary>Exact, case-sensitive match: what a plan and a run use.</summary>
    public int IndexOf(string name)
    {
        for (var i = 0; i < Columns.Count; i++)
            if (string.Equals(Columns[i].Name, name, StringComparison.Ordinal))
                return i;
        return -1;
    }

    /// <summary>Case-insensitive match: only for inference, where "total sales" and "Total Sales"
    /// in two files mean the same thing.</summary>
    public int IndexOfIgnoreCase(string name)
    {
        var exact = IndexOf(name);
        if (exact >= 0)
            return exact;
        for (var i = 0; i < Columns.Count; i++)
            if (string.Equals(Columns[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    public IEnumerable<CellValue> ColumnValues(int index) => Rows.Select(r => r[index]);

    /// <summary>The single type of a column's non-empty cells, or Mixed.</summary>
    public static ColumnType TypeOf(IEnumerable<CellValue> cells)
    {
        CellKind? seen = null;
        foreach (var cell in cells)
        {
            if (cell.IsEmpty)
                continue;
            if (seen is null)
                seen = cell.Kind;
            else if (seen != cell.Kind)
                return ColumnType.Mixed;
        }

        return seen switch
        {
            null => ColumnType.Empty,
            CellKind.Text => ColumnType.Text,
            CellKind.Number => ColumnType.Number,
            CellKind.Date => ColumnType.Date,
            CellKind.Bool => ColumnType.Bool,
            _ => ColumnType.Mixed,
        };
    }

    /// <summary>Builds a table from rows, inferring each column's type.</summary>
    public static Table From(IReadOnlyList<string> names, IReadOnlyList<CellValue[]> rows)
    {
        var columns = names.Select((name, i) =>
            new Column(name, TypeOf(rows.Select(r => i < r.Length ? r[i] : CellValue.Empty)))).ToList();
        return new Table(columns, rows);
    }

    private static CellValue[] Pad(CellValue[] row, int count)
    {
        var padded = new CellValue[count];
        Array.Copy(row, padded, Math.Min(row.Length, count));
        return padded;
    }
}
