using Litos.Office.Data;

namespace Litos.Office.Tests;

/// <summary>In-memory tables for executor and inference tests: no workbook needed.</summary>
internal static class Tables
{
    public static Table Of(string[] headers, params object?[][] rows) =>
        Table.From(headers, rows.Select(r => r.Select(Cell).ToArray()).ToList());

    public static CellValue Cell(object? value) => value switch
    {
        null => CellValue.Empty,
        string s => CellValue.OfText(s),
        decimal d => CellValue.OfNumber(d),
        int i => CellValue.OfNumber(i),
        DateTime t => CellValue.OfDate(t),
        bool b => CellValue.OfBool(b),
        _ => throw new ArgumentException($"Unsupported cell {value.GetType()}"),
    };

    /// <summary>Spec §6: the original workbook.</summary>
    public static Table SalesRaw() => Of(["Region", "Amount", "Status"],
        ["North", 100m, "Completed"],
        ["South", 200m, "Completed"],
        ["North", 50m, "Cancelled"],
        ["North", 300m, "Completed"]);

    /// <summary>Spec §6: the fresh-input test fixture.</summary>
    public static Table SalesFresh() => Of(["Region", "Amount", "Status"],
        ["North", 120m, "Completed"],
        ["South", 80m, "Completed"],
        ["South", 40m, "Cancelled"],
        ["East", 50m, "Completed"]);

    public static string[][] Display(Table table) =>
        table.Rows.Select(r => r.Select(c => c.ToDisplay()).ToArray()).ToArray();
}
