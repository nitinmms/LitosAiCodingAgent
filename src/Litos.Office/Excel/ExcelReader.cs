using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Litos.Office.Data;

namespace Litos.Office.Excel;

/// <summary>
/// Reads one plain table from a workbook that already passed <see cref="WorkbookGate"/>: a single
/// header row of unique names, then data. What Litos can't read faithfully is refused with a
/// reason rather than half-read — merged cells in the table, blank or repeated headers, several
/// sheets with data and no hint which one. Formulas are never evaluated: a formula cell is read as
/// the value Excel last saved for it.
/// </summary>
public static partial class ExcelReader
{
    /// <summary>A label that marks a totals row ("Total", "Grand Total", "Grand total:").</summary>
    [GeneratedRegex(@"^\s*(grand\s+)?totals?\s*:?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    internal static partial Regex TotalLabel();

    public static LoadedSheet Read(byte[] bytes, string fileName, string? sheetHint = null, OfficeLimits? limits = null, CancellationToken ct = default)
    {
        limits ??= OfficeLimits.Default;
        var name = Path.GetFileName(fileName);
        var gate = WorkbookGate.Check(bytes, fileName, limits);
        if (!gate.Ok)
            throw new OfficeFileException(gate.Findings[0].Code, gate.Findings[0].Message);

        XLWorkbook workbook;
        try
        {
            workbook = new XLWorkbook(new MemoryStream(bytes, writable: false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new OfficeFileException("unreadable", $"{name} couldn't be read as an Excel workbook: {ex.Message}", ex);
        }

        using (workbook)
        {
            var sheet = ChooseSheet(workbook, name, sheetHint);
            var findings = new List<Finding>();
            var sheetData = ReadTable(sheet, name, limits, findings, ct);
            return new LoadedSheet(sheetData.Table, sheetData.Style, findings, Sha256Of(bytes));
        }
    }

    public static string Sha256Of(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static IXLWorksheet ChooseSheet(XLWorkbook workbook, string name, string? hint)
    {
        if (!string.IsNullOrWhiteSpace(hint))
        {
            return workbook.Worksheets.FirstOrDefault(s => string.Equals(s.Name, hint, StringComparison.Ordinal))
                ?? throw new OfficeFileException("sheet_missing",
                    $"{name} has no sheet named \"{hint}\". Its sheets are: {string.Join(", ", workbook.Worksheets.Select(s => s.Name))}.");
        }

        var withData = workbook.Worksheets.Where(s => s.RangeUsed(XLCellsUsedOptions.Contents) is not null).ToList();
        return withData.Count switch
        {
            0 => throw new OfficeFileException("no_data", $"{name} has no data on any sheet."),
            1 => withData[0],
            _ => throw new OfficeFileException("several_sheets",
                $"{name} has data on several sheets ({string.Join(", ", withData.Select(s => s.Name))}). Litos learns from one sheet at a time; say which one."),
        };
    }

    private static (Table Table, SheetStyle Style) ReadTable(IXLWorksheet sheet, string name, OfficeLimits limits, List<Finding> findings, CancellationToken ct)
    {
        var used = sheet.RangeUsed(XLCellsUsedOptions.Contents)
            ?? throw new OfficeFileException("no_data", $"The sheet \"{sheet.Name}\" in {name} is empty.");

        var firstRow = used.FirstRow().RowNumber();
        var lastRow = used.LastRow().RowNumber();
        var firstCol = used.FirstColumn().ColumnNumber();
        var lastCol = used.LastColumn().ColumnNumber();
        var columnCount = lastCol - firstCol + 1;

        if (columnCount > limits.MaxColumns)
            throw new OfficeFileException("too_many_columns", $"The sheet \"{sheet.Name}\" in {name} has {columnCount} columns; Litos reads at most {limits.MaxColumns}.");
        if (lastRow - firstRow > limits.MaxRows)
            throw new OfficeFileException("too_many_rows", $"The sheet \"{sheet.Name}\" in {name} has more than {limits.MaxRows:N0} rows, the most Litos reads.");

        if (sheet.MergedRanges.Any(m => m.Intersects(used)))
            throw new OfficeFileException("merged_cells",
                $"The table on \"{sheet.Name}\" in {name} has merged cells. Litos reads plain tables only; unmerge the cells and save again.");

        // Header: the first used row; every name present and unique (ignoring case, so "Amount"
        // and "amount" can't both exist and be confused later).
        var headers = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var c = firstCol; c <= lastCol; c++)
        {
            var header = Clip(CellOf(sheet.Cell(firstRow, c)).ToDisplay().Trim(), 200);
            if (header.Length == 0)
                throw new OfficeFileException("blank_header",
                    $"Column {XLHelper.GetColumnLetterFromNumber(c)} of the table on \"{sheet.Name}\" in {name} has no heading. Every column needs a heading in the first row.");
            if (!seen.Add(header))
                throw new OfficeFileException("duplicate_header",
                    $"The table on \"{sheet.Name}\" in {name} has two columns headed \"{header}\". Headings must be different.");
            headers.Add(header);
        }

        var rows = new List<CellValue[]>();
        var rowNumbers = new List<int>();
        var emptyRows = 0;
        var formulaWithoutValue = 0;
        var errorCells = 0;
        for (var r = firstRow + 1; r <= lastRow; r++)
        {
            if ((r - firstRow) % 1000 == 0)
                ct.ThrowIfCancellationRequested();

            var row = new CellValue[columnCount];
            var any = false;
            for (var c = firstCol; c <= lastCol; c++)
            {
                var cell = sheet.Cell(r, c);
                // Never touch cell.Value on a formula cell: ClosedXML evaluates the formula to
                // produce it. The cached result is what Excel last saved, and all Litos reads.
                var raw = RawOf(cell);
                if (cell.HasFormula && raw.IsBlank)
                    formulaWithoutValue++;
                if (raw.IsError)
                    errorCells++;
                var value = ToCellValue(raw);
                if (value.Kind == CellKind.Text && value.Text!.Length > limits.MaxCellChars)
                    value = CellValue.OfText(value.Text[..limits.MaxCellChars]);
                row[c - firstCol] = value;
                any |= !value.IsEmpty;
            }

            if (!any)
            {
                emptyRows++;
                continue;
            }
            rows.Add(row);
            rowNumbers.Add(r);
        }

        if (emptyRows > 0)
            findings.Add(Finding.Warning("empty_rows", $"{emptyRows} empty row(s) inside the table were ignored."));
        if (formulaWithoutValue > 0)
            findings.Add(Finding.Warning("formula_without_value",
                $"{formulaWithoutValue} formula cell(s) have no saved result, so they read as empty. Open {name} in Excel and save it once to store the results."));
        if (errorCells > 0)
            findings.Add(Finding.Warning("error_cells", $"{errorCells} cell(s) contain Excel errors such as #N/A or #DIV/0!."));

        var table = Table.From(headers, rows);
        foreach (var (column, index) in table.Columns.Select((c, i) => (c, i)))
        {
            var numericText = table.ColumnValues(index)
                .Count(v => v.Kind == CellKind.Text && decimal.TryParse(v.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out _));
            if (numericText > 0 && column.Type is ColumnType.Text or ColumnType.Mixed)
                findings.Add(Finding.Warning("numbers_as_text",
                    $"Column \"{column.Name}\" has {numericText} number(s) stored as text. Litos treats them as text, not numbers."));
        }

        var hasTotal = rows.Count > 0 && IsTotalRow(rows[^1]);
        var style = new SheetStyle(
            sheet.Name,
            firstRow,
            firstCol,
            Enumerable.Range(firstCol, columnCount).Select(c => Snapshot(sheet.Cell(firstRow, c))).ToList(),
            Enumerable.Range(firstCol, columnCount).Select(c => Snapshot(sheet.Cell(rowNumbers.Count > 0 ? rowNumbers[0] : firstRow + 1, c))).ToList(),
            hasTotal ? Enumerable.Range(firstCol, columnCount).Select(c => Snapshot(sheet.Cell(rowNumbers[^1], c))).ToList() : null,
            Enumerable.Range(firstCol, columnCount).Select(c => sheet.Column(c).Width).ToList(),
            sheet.SheetView.SplitRow,
            sheet.AutoFilter.IsEnabled,
            hasTotal);

        return (table, style);
    }

    /// <summary>A row whose first non-empty cell is a "Total" label.</summary>
    internal static bool IsTotalRow(CellValue[] row)
    {
        var first = row.FirstOrDefault(c => !c.IsEmpty);
        return first.Kind == CellKind.Text && TotalLabel().IsMatch(first.Text!);
    }

    /// <summary>The stored value: for a formula, the cached result, never an evaluation.</summary>
    private static XLCellValue RawOf(IXLCell cell) => cell.HasFormula ? cell.CachedValue : cell.Value;

    private static CellValue CellOf(IXLCell cell) => ToCellValue(RawOf(cell));

    /// <summary>A stored value as Litos understands it. An Excel error reads as its text (#N/A),
    /// which a check later refuses as a number.</summary>
    private static CellValue ToCellValue(XLCellValue value)
    {
        return value.Type switch
        {
            XLDataType.Blank => CellValue.Empty,
            XLDataType.Text => value.GetText().Length == 0 ? CellValue.Empty : CellValue.OfText(value.GetText()),
            XLDataType.Number => ToDecimal(value.GetNumber()),
            XLDataType.DateTime => CellValue.OfDate(value.GetDateTime()),
            XLDataType.Boolean => CellValue.OfBool(value.GetBoolean()),
            XLDataType.TimeSpan => CellValue.OfText(value.GetTimeSpan().ToString("c", CultureInfo.InvariantCulture)),
            XLDataType.Error => CellValue.OfText(value.ToString(CultureInfo.InvariantCulture)),
            _ => CellValue.OfText(value.ToString(CultureInfo.InvariantCulture)),
        };
    }

    /// <summary>Excel stores numbers as doubles; converting through decimal keeps the 15
    /// significant digits Excel shows, so 0.1 + 0.2 sums to 0.3 later.</summary>
    private static CellValue ToDecimal(double number) =>
        double.IsFinite(number) && Math.Abs(number) < 7.9e27
            ? CellValue.OfNumber(Convert.ToDecimal(number))
            : CellValue.OfText(number.ToString(CultureInfo.InvariantCulture));

    private static CellStyleSnapshot Snapshot(IXLCell cell)
    {
        var style = cell.Style;
        return new CellStyleSnapshot(
            style.NumberFormat.Format ?? "",
            style.Font.Bold,
            style.Font.Italic,
            Hex(style.Font.FontColor),
            Hex(style.Fill.BackgroundColor),
            style.Alignment.Horizontal == XLAlignmentHorizontalValues.General ? null : style.Alignment.Horizontal.ToString(),
            style.Border.TopBorder != XLBorderStyleValues.None || style.Border.BottomBorder != XLBorderStyleValues.None
                || style.Border.LeftBorder != XLBorderStyleValues.None || style.Border.RightBorder != XLBorderStyleValues.None);
    }

    private static string? Hex(XLColor color)
    {
        try
        {
            return color.HasValue && color.ColorType == XLColorType.Color
                ? color.Color.ToArgb().ToString("X8", CultureInfo.InvariantCulture)
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max];
}
