using ClosedXML.Excel;
using Litos.Office.Data;
using Litos.Office.Excel;

namespace Litos.Office.Tests;

/// <summary>
/// Reading one plain table (design §2, ExcelReader): typed cells, decimals that add up, the sheet
/// choice rules, and refusals for layouts Litos would otherwise misread.
/// </summary>
public class ExcelReaderTests
{
    private static LoadedSheet Read(byte[] bytes, string? sheet = null, OfficeLimits? limits = null, CancellationToken ct = default) =>
        ExcelReader.Read(bytes, "sales.xlsx", sheet, limits, ct);

    private static string RefusalCode(Action read) => Assert.Throws<OfficeFileException>(read).Code;

    [Fact]
    public void ReadsHeadersAndTypedValues()
    {
        var date = new DateTime(2026, 11, 3);
        var bytes = XlsxFixtures.Sheet("Sales", ["Region", "Amount", "Booked", "Paid"],
            ["North", 100.5m, date, true],
            ["South", 200m, date.AddDays(1), false]);

        var loaded = Read(bytes);

        Assert.Equal(["Region", "Amount", "Booked", "Paid"], loaded.Table.ColumnNames);
        Assert.Equal([ColumnType.Text, ColumnType.Number, ColumnType.Date, ColumnType.Bool], loaded.Table.Columns.Select(c => c.Type));
        Assert.Equal(2, loaded.Table.Rows.Count);
        Assert.Equal(CellValue.OfText("North"), loaded.Table.Rows[0][0]);
        Assert.Equal(100.5m, loaded.Table.Rows[0][1].Number);
        Assert.Equal(date, loaded.Table.Rows[0][2].Date);
        Assert.True(loaded.Table.Rows[0][3].Bool);
        Assert.Equal("Sales", loaded.Style.SheetName);
    }

    [Fact]
    public void Decimals_AreExact_SoMoneyAddsUp()
    {
        var loaded = Read(XlsxFixtures.Sheet("S", ["Amount"], [0.1], [0.2]));

        Assert.Equal(0.3m, loaded.Table.Rows.Sum(r => r[0].Number!.Value));
    }

    [Fact]
    public void TheHashIsOfTheBytesRead()
    {
        var bytes = XlsxFixtures.SalesRaw();

        Assert.Equal(ExcelReader.Sha256Of(bytes), Read(bytes).Sha256);
        Assert.Equal(64, Read(bytes).Sha256.Length);
    }

    [Fact]
    public void ATableNotAtA1_IsReadFromWhereItStarts()
    {
        var bytes = XlsxFixtures.Build(w =>
            XlsxFixtures.Fill(w.AddWorksheet("Report"), 3, 2, ["Region", "Amount"], [["North", 10m], ["South", 20m]]));

        var loaded = Read(bytes);

        Assert.Equal(3, loaded.Style.HeaderRow);
        Assert.Equal(2, loaded.Style.FirstColumn);
        Assert.Equal(["Region", "Amount"], loaded.Table.ColumnNames);
        Assert.Equal(2, loaded.Table.Rows.Count);
    }

    [Fact]
    public void TheOnlySheetWithData_IsChosen_WhenNoneIsNamed()
    {
        var bytes = XlsxFixtures.Build(w =>
        {
            w.AddWorksheet("Cover");
            XlsxFixtures.Fill(w.AddWorksheet("Data"), 1, 1, ["A"], [["x"]]);
        });

        Assert.Equal("Data", Read(bytes).Style.SheetName);
    }

    [Fact]
    public void SeveralSheetsWithData_AndNoHint_AreRefused_NamingThem()
    {
        var bytes = XlsxFixtures.Build(w =>
        {
            XlsxFixtures.Fill(w.AddWorksheet("Jan"), 1, 1, ["A"], [["x"]]);
            XlsxFixtures.Fill(w.AddWorksheet("Feb"), 1, 1, ["A"], [["y"]]);
        });

        var ex = Assert.Throws<OfficeFileException>(() => Read(bytes));
        Assert.Equal("several_sheets", ex.Code);
        Assert.Contains("Jan", ex.Message);
        Assert.Contains("Feb", ex.Message);
    }

    [Fact]
    public void ASheetHint_PicksThatSheet_AndAMissingOneIsRefused()
    {
        var bytes = XlsxFixtures.Build(w =>
        {
            XlsxFixtures.Fill(w.AddWorksheet("Jan"), 1, 1, ["A"], [["x"]]);
            XlsxFixtures.Fill(w.AddWorksheet("Feb"), 1, 1, ["A"], [["y"]]);
        });

        Assert.Equal("y", Read(bytes, "Feb").Table.Rows[0][0].Text);
        Assert.Equal("sheet_missing", RefusalCode(() => Read(bytes, "Mar")));
    }

    [Fact]
    public void AWorkbookWithNoData_IsRefused()
    {
        Assert.Equal("no_data", RefusalCode(() => Read(XlsxFixtures.Build(w => w.AddWorksheet("Empty")))));
    }

    [Fact]
    public void ABlankHeading_IsRefused()
    {
        var bytes = XlsxFixtures.Build(w =>
        {
            var s = w.AddWorksheet("S");
            s.Cell(1, 1).Value = "Region";
            s.Cell(1, 3).Value = "Amount";
            s.Cell(2, 1).Value = "North";
            s.Cell(2, 2).Value = 5;
            s.Cell(2, 3).Value = 10;
        });

        Assert.Equal("blank_header", RefusalCode(() => Read(bytes)));
    }

    [Fact]
    public void DuplicateHeadings_IgnoringCase_AreRefused()
    {
        Assert.Equal("duplicate_header", RefusalCode(() => Read(XlsxFixtures.Sheet("S", ["Amount", "amount"], [1m, 2m]))));
    }

    [Fact]
    public void MergedCellsInTheTable_AreRefused()
    {
        var bytes = XlsxFixtures.Build(w =>
        {
            var s = w.AddWorksheet("S");
            XlsxFixtures.Fill(s, 1, 1, ["Region", "Amount"], [["North", 1m], ["South", 2m]]);
            s.Range("A2:A3").Merge();
        });

        Assert.Equal("merged_cells", RefusalCode(() => Read(bytes)));
    }

    [Fact]
    public void EmptyRowsInsideTheTable_AreSkipped_WithAWarning()
    {
        var bytes = XlsxFixtures.Build(w =>
        {
            var s = w.AddWorksheet("S");
            XlsxFixtures.Fill(s, 1, 1, ["A"], [["x"]]);
            s.Cell(4, 1).Value = "y";
        });

        var loaded = Read(bytes);

        Assert.Equal(["x", "y"], loaded.Table.Rows.Select(r => r[0].Text));
        Assert.Contains(loaded.Findings, f => f.Code == "empty_rows");
    }

    [Fact]
    public void ShortRows_ArePaddedWithEmptyCells()
    {
        var loaded = Read(XlsxFixtures.Sheet("S", ["A", "B"], ["x", 1m], ["y"]));

        Assert.True(loaded.Table.Rows[1][1].IsEmpty);
        Assert.Equal(2, loaded.Table.Rows[1].Length);
    }

    [Fact]
    public void NumbersStoredAsText_AreKeptAsText_AndReported()
    {
        var loaded = Read(XlsxFixtures.Sheet("S", ["Amount"], ["100"], ["200"]));

        Assert.Equal(CellKind.Text, loaded.Table.Rows[0][0].Kind);
        Assert.Contains(loaded.Findings, f => f.Code == "numbers_as_text" && f.Message.Contains("Amount"));
    }

    [Fact]
    public void AFormula_IsNeverEvaluated_OnlyItsSavedResultIsRead()
    {
        // ClosedXML saves a formula without a computed result unless asked to evaluate; if the
        // reader evaluated formulas, this would read 3.
        var bytes = XlsxFixtures.Build(w =>
        {
            var s = w.AddWorksheet("S");
            s.Cell(1, 1).Value = "Total";
            s.Cell(2, 1).FormulaA1 = "1+2";
        });

        var loaded = Read(bytes);

        Assert.True(loaded.Table.Rows.Count == 0 || loaded.Table.Rows[0][0].IsEmpty);
        Assert.Contains(loaded.Findings, f => f.Code == "formula_without_value");
    }

    [Fact]
    public void ExcelErrorCells_AreReported()
    {
        var bytes = XlsxFixtures.Build(w =>
        {
            var s = w.AddWorksheet("S");
            s.Cell(1, 1).Value = "Amount";
            s.Cell(2, 1).Value = XLError.DivisionByZero;
            s.Cell(3, 1).Value = 4;
        });

        Assert.Contains(Read(bytes).Findings, f => f.Code == "error_cells");
    }

    [Theory]
    [InlineData("Total")]
    [InlineData("Grand Total")]
    [InlineData("grand total:")]
    [InlineData(" TOTALS ")]
    public void ATotalsRowAtTheEnd_IsDetected(string label)
    {
        var loaded = Read(XlsxFixtures.Sheet("S", ["Region", "Sales"], ["North", 400m], ["South", 200m], [label, 600m]));

        Assert.True(loaded.Style.HasTotalRow);
        Assert.NotNull(loaded.Style.TotalRowStyles);
    }

    [Fact]
    public void ARegionCalledTotalCare_IsNotATotalsRow()
    {
        Assert.False(Read(XlsxFixtures.Sheet("S", ["Region", "Sales"], ["Total Care", 10m])).Style.HasTotalRow);
    }

    [Fact]
    public void TooManyRows_OrColumns_AreRefused_NotTruncated()
    {
        var bytes = XlsxFixtures.Sheet("S", ["A", "B", "C"], [1m, 2m, 3m], [4m, 5m, 6m], [7m, 8m, 9m]);

        Assert.Equal("too_many_rows", RefusalCode(() => Read(bytes, limits: new OfficeLimits(MaxRows: 2))));
        Assert.Equal("too_many_columns", RefusalCode(() => Read(bytes, limits: new OfficeLimits(MaxColumns: 2))));
    }

    [Fact]
    public void ReadingHonoursCancellation()
    {
        var rows = Enumerable.Range(0, 2500).Select(i => new object?[] { i }).ToArray();
        var bytes = XlsxFixtures.Sheet("S", ["N"], rows);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() => Read(bytes, ct: cancelled.Token));
    }

    [Fact]
    public void AGateRefusal_SurfacesAsAReadRefusal()
    {
        var bytes = XlsxFixtures.WithEntries(XlsxFixtures.Clean(), ("xl/vbaProject.bin", [1]));

        Assert.Equal("macros", RefusalCode(() => Read(bytes)));
    }

    [Fact]
    public void Styles_AreCaptured_ForTheTemplate()
    {
        var bytes = XlsxFixtures.Build(w =>
        {
            var s = w.AddWorksheet("Summary");
            XlsxFixtures.Fill(s, 1, 1, ["Region", "Total Sales"], [["North", 400m], ["Grand Total", 400m]]);
            s.Row(1).Style.Font.Bold = true;
            s.Cell(2, 2).Style.NumberFormat.Format = "#,##0.00";
            s.Column(1).Width = 22;
            s.SheetView.FreezeRows(1);
        });

        var style = Read(bytes).Style;

        Assert.True(style.HeaderStyles[0].Bold);
        Assert.Equal("#,##0.00", style.DataStyles[1].NumberFormat);
        Assert.Equal(22, style.ColumnWidths[0], 3);
        Assert.Equal(1, style.FrozenRows);
        Assert.True(style.HasTotalRow);
    }
}
