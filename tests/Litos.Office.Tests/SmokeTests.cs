using ClosedXML.Excel;

namespace Litos.Office.Tests;

/// <summary>
/// Step 0 of docs/LearningBuzzies_ExcelDesign.md: ClosedXML creates, saves and reads an .xlsx on
/// net10.0 without Office installed, in memory, without measuring fonts (no AdjustToContents).
/// </summary>
public class SmokeTests
{
    [Fact]
    public void ClosedXml_WritesAndReadsAWorkbook_InMemory()
    {
        byte[] bytes;
        using (var workbook = new XLWorkbook())
        {
            var sheet = workbook.AddWorksheet("Sales");
            sheet.Cell(1, 1).Value = "Region";
            sheet.Cell(1, 2).Value = "Amount";
            sheet.Cell(2, 1).Value = "North";
            sheet.Cell(2, 2).Value = 100.25m;
            sheet.Column(1).Width = 18;
            using var stream = new MemoryStream();
            workbook.SaveAs(stream);
            bytes = stream.ToArray();
        }

        using var reread = new XLWorkbook(new MemoryStream(bytes));
        var read = reread.Worksheet("Sales");
        Assert.Equal("Region", read.Cell(1, 1).GetText());
        Assert.Equal(100.25m, (decimal)read.Cell(2, 2).GetDouble(), 10);
        Assert.Equal(18, read.Column(1).Width, 3);
    }
}
