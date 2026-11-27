using ClosedXML.Excel;

namespace Litos.Office.Excel;

/// <summary>Where the report's table sits in the template, so the writer fills the same cells.
/// Stored in manifest.json beside the template.</summary>
public sealed record TemplateLayout(
    string SheetName,
    int HeaderRow,
    int FirstColumn,
    int Columns,
    bool HasTotalPrototype);

/// <summary>
/// Turns the completed example into the skill's formatting template (design §2): one sheet with the
/// header row (styles, widths, frozen panes), a prototype data row and, when the example had one, a
/// prototype totals row — formats only. No example value below the header survives: the template
/// is stored with the skill, and the example itself is not.
/// </summary>
public static class TemplateExtractor
{
    public static (byte[] Template, TemplateLayout Layout) Extract(byte[] exampleBytes, SheetStyle style)
    {
        using var example = new XLWorkbook(new MemoryStream(exampleBytes, writable: false));
        var source = example.Worksheet(style.SheetName);
        var columns = style.ColumnWidths.Count;

        using var template = new XLWorkbook();
        var sheet = template.AddWorksheet(style.SheetName);

        for (var c = 0; c < columns; c++)
        {
            var column = style.FirstColumn + c;
            var header = sheet.Cell(style.HeaderRow, column);
            header.Value = source.Cell(style.HeaderRow, column).CachedOrValueText();
            header.Style = source.Cell(style.HeaderRow, column).Style;

            sheet.Cell(style.HeaderRow + 1, column).Style = source.Cell(style.HeaderRow + 1, column).Style;
            if (style.HasTotalRow)
                sheet.Cell(style.HeaderRow + 2, column).Style = source.Cell(LastDataRow(source, style), column).Style;

            sheet.Column(column).Width = style.ColumnWidths[c];
        }

        sheet.Row(style.HeaderRow).Height = source.Row(style.HeaderRow).Height;
        if (style.FrozenRows > 0)
            sheet.SheetView.FreezeRows(style.FrozenRows);

        using var stream = new MemoryStream();
        template.SaveAs(stream);
        return (stream.ToArray(), new TemplateLayout(style.SheetName, style.HeaderRow, style.FirstColumn, columns, style.HasTotalRow));
    }

    private static int LastDataRow(IXLWorksheet sheet, SheetStyle style) =>
        sheet.RangeUsed(XLCellsUsedOptions.Contents)?.LastRow().RowNumber() ?? style.HeaderRow + 1;

    /// <summary>A header cell's text without evaluating a formula.</summary>
    private static string CachedOrValueText(this IXLCell cell)
    {
        var value = cell.HasFormula ? cell.CachedValue : cell.Value;
        return value.IsBlank ? "" : value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
