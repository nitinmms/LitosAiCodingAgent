using ClosedXML.Excel;
using Litos.Office.Data;

namespace Litos.Office.Excel;

/// <summary>
/// Writes a report into a copy of the skill's template (design §2 ExcelWriter). Values only: no
/// formula is ever written, and text that Excel would read as a formula (starting with = + - @, a
/// tab or a carriage return) is stored with Excel's quote prefix, so a cell from the input saying
/// "=HYPERLINK(...)" is shown, not run (spec §11, formula injection). Formatting comes from the
/// template's prototype rows; nothing measures fonts, so output is the same on every machine.
/// </summary>
public static class ExcelWriter
{
    public static byte[] Write(byte[] templateBytes, TemplateLayout layout, string sheetName, Table output, CellValue[]? grandTotal)
    {
        using var source = new XLWorkbook(new MemoryStream(templateBytes, writable: false));
        using var target = new XLWorkbook(new MemoryStream(templateBytes, writable: false));
        var prototype = source.Worksheet(layout.SheetName);
        var sheet = target.Worksheet(layout.SheetName);
        if (sheet.Name != sheetName)
            sheet.Name = sheetName;

        var headerRow = layout.HeaderRow;
        var dataRow = headerRow + 1;
        var totalProtoRow = layout.HasTotalPrototype ? headerRow + 2 : dataRow;
        var count = output.Columns.Count;

        // Prototype rows are templates for formats, not content: clear them, then lay the real rows
        // over the same cells using styles read from the untouched copy.
        sheet.Row(dataRow).Clear();
        if (layout.HasTotalPrototype)
            sheet.Row(totalProtoRow).Clear();

        for (var c = 0; c < count; c++)
        {
            var column = layout.FirstColumn + c;
            var from = layout.FirstColumn + Math.Min(c, Math.Max(layout.Columns - 1, 0));
            var header = sheet.Cell(headerRow, column);
            header.Style = prototype.Cell(headerRow, from).Style;
            SetText(header, output.Columns[c].Name);
            if (c >= layout.Columns)
                sheet.Column(column).Width = prototype.Column(from).Width;
        }
        for (var c = count; c < layout.Columns; c++)
            sheet.Cell(headerRow, layout.FirstColumn + c).Clear();

        for (var r = 0; r < output.Rows.Count; r++)
            WriteRow(sheet, prototype, layout, dataRow + r, dataRow, output.Rows[r], bold: false);

        if (grandTotal is not null)
            WriteRow(sheet, prototype, layout, dataRow + output.Rows.Count, totalProtoRow, grandTotal, bold: !layout.HasTotalPrototype);

        using var stream = new MemoryStream();
        target.SaveAs(stream);
        return stream.ToArray();
    }

    private static void WriteRow(IXLWorksheet sheet, IXLWorksheet prototype, TemplateLayout layout, int row, int styleRow, CellValue[] cells, bool bold)
    {
        for (var c = 0; c < cells.Length; c++)
        {
            var from = layout.FirstColumn + Math.Min(c, Math.Max(layout.Columns - 1, 0));
            var cell = sheet.Cell(row, layout.FirstColumn + c);
            cell.Style = prototype.Cell(styleRow, from).Style;
            if (bold)
                cell.Style.Font.Bold = true;

            var value = cells[c];
            switch (value.Kind)
            {
                case CellKind.Number:
                    cell.Value = (double)value.Number!.Value;
                    break;
                case CellKind.Date:
                    cell.Value = value.Date!.Value;
                    if (cell.Style.NumberFormat.NumberFormatId == 0 && string.IsNullOrEmpty(cell.Style.NumberFormat.Format))
                        cell.Style.NumberFormat.Format = "yyyy-mm-dd";
                    break;
                case CellKind.Bool:
                    cell.Value = value.Bool!.Value;
                    break;
                case CellKind.Text:
                    SetText(cell, value.Text!);
                    break;
                default:
                    cell.Value = Blank.Value;
                    break;
            }
        }
    }

    /// <summary>Text, never a formula; formula-looking text gets the quote prefix.</summary>
    internal static void SetText(IXLCell cell, string text)
    {
        cell.Value = text;
        cell.Style.IncludeQuotePrefix = LooksLikeFormula(text);
    }

    internal static bool LooksLikeFormula(string text) => text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r';
}
