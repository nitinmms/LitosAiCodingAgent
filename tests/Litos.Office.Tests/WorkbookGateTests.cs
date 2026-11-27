using System.Text;
using Litos.Office.Excel;

namespace Litos.Office.Tests;

/// <summary>
/// The gate every workbook passes before it is parsed (spec §11: reject macro-enabled, encrypted
/// files, dangerous relationships and external links; limit ZIP expansion). Each refusal has a
/// stable code and a sentence that names the file.
/// </summary>
public class WorkbookGateTests
{
    private static GateResult Check(byte[] bytes, string name = "report.xlsx", OfficeLimits? limits = null) =>
        WorkbookGate.Check(bytes, name, limits);

    private static void AssertRefused(GateResult result, string code)
    {
        Assert.False(result.Ok);
        var error = Assert.Single(result.Findings);
        Assert.Equal(code, error.Code);
        Assert.Equal(FindingSeverity.Error, error.Severity);
        Assert.Contains("report.xlsx", error.Message);
    }

    [Fact]
    public void ACleanWorkbook_Passes()
    {
        var result = Check(XlsxFixtures.Clean());

        Assert.True(result.Ok);
        Assert.Empty(result.Findings);
    }

    [Theory]
    [InlineData("report.xlsm")]
    [InlineData("report.xls")]
    [InlineData("report.csv")]
    [InlineData("report")]
    public void AnythingButXlsx_IsRefusedByExtension(string name)
    {
        var result = Check(XlsxFixtures.Clean(), name);

        Assert.False(result.Ok);
        Assert.Equal("not_xlsx", result.Findings[0].Code);
    }

    [Fact]
    public void TheExtensionCheck_IgnoresCase()
    {
        Assert.True(Check(XlsxFixtures.Clean(), "REPORT.XLSX").Ok);
    }

    [Fact]
    public void AFileOverTheSizeLimit_IsRefused()
    {
        AssertRefused(Check(XlsxFixtures.Clean(), limits: new OfficeLimits(MaxFileBytes: 100)), "too_large");
    }

    [Fact]
    public void AnEncryptedOrLegacyCompoundFile_IsRefused()
    {
        byte[] ole = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0];

        AssertRefused(Check(ole), "encrypted_or_legacy");
    }

    [Fact]
    public void BytesThatAreNotAZip_AreRefused()
    {
        AssertRefused(Check(Encoding.UTF8.GetBytes("Region,Amount\nNorth,100")), "not_a_workbook");
    }

    [Fact]
    public void AZipWithNoWorkbookPart_IsRefused()
    {
        AssertRefused(Check(XlsxFixtures.ZipOf(("readme.txt", "hi"u8.ToArray()))), "not_a_workbook");
    }

    [Fact]
    public void AVbaProject_IsRefusedAsMacros()
    {
        var bytes = XlsxFixtures.WithEntries(XlsxFixtures.Clean(), ("xl/vbaProject.bin", [1, 2, 3]));

        AssertRefused(Check(bytes), "macros");
    }

    [Fact]
    public void AMacroEnabledContentType_IsRefusedAsMacros()
    {
        var clean = XlsxFixtures.Clean();
        var types = XlsxFixtures.EntryText(clean, "[Content_Types].xml")
            .Replace("spreadsheetml.sheet.main+xml", "sheet.macroEnabled.main+xml", StringComparison.Ordinal);

        AssertRefused(Check(XlsxFixtures.WithEntry(clean, "[Content_Types].xml", types)), "macros");
    }

    [Fact]
    public void AnEncryptedPackageEntry_IsRefused()
    {
        var bytes = XlsxFixtures.WithEntries(XlsxFixtures.Clean(), ("EncryptedPackage", [9, 9, 9]));

        AssertRefused(Check(bytes), "encrypted_or_legacy");
    }

    [Fact]
    public void AnExternalLinkPart_IsRefused()
    {
        var bytes = XlsxFixtures.WithEntry(XlsxFixtures.Clean(), "xl/externalLinks/externalLink1.xml", "<externalLink/>");

        AssertRefused(Check(bytes), "external_links");
    }

    [Theory]
    [InlineData("xl/embeddings/oleObject1.bin")]
    [InlineData("xl/activeX/activeX1.xml")]
    [InlineData("xl/ctrlProps/ctrlProp1.xml")]
    public void EmbeddedObjectsAndControls_AreRefused(string part)
    {
        var bytes = XlsxFixtures.WithEntries(XlsxFixtures.Clean(), (part, [1]));

        AssertRefused(Check(bytes), "embedded_objects");
    }

    [Fact]
    public void AnExternalNonHyperlinkRelationship_IsRefused()
    {
        const string rels = """
            <?xml version="1.0" encoding="UTF-8"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId9" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/oleObject"
                            Target="file:///C:/Windows/System32/calc.exe" TargetMode="External"/>
            </Relationships>
            """;

        AssertRefused(Check(XlsxFixtures.WithEntry(XlsxFixtures.Clean(), "xl/worksheets/_rels/sheet9.xml.rels", rels)), "external_links");
    }

    [Fact]
    public void AnOrdinaryHyperlinkInACell_IsAllowed()
    {
        var bytes = XlsxFixtures.Build(workbook =>
        {
            var sheet = workbook.AddWorksheet("Data");
            sheet.Cell(1, 1).Value = "Link";
            sheet.Cell(2, 1).Value = "Site";
            sheet.Cell(2, 1).SetHyperlink(new ClosedXML.Excel.XLHyperlink("https://example.com"));
        });

        Assert.True(Check(bytes).Ok);
    }

    [Fact]
    public void ARelationshipsPartWithADtd_IsRefusedAsDamaged()
    {
        const string billionLaughs = """
            <?xml version="1.0"?>
            <!DOCTYPE lolz [<!ENTITY lol "lol"><!ENTITY lol2 "&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;">]>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">&lol2;</Relationships>
            """;

        AssertRefused(Check(XlsxFixtures.WithEntry(XlsxFixtures.Clean(), "xl/_rels/evil.xml.rels", billionLaughs)), "not_a_workbook");
    }

    [Fact]
    public void TooManyParts_IsRefused()
    {
        AssertRefused(Check(XlsxFixtures.Clean(), limits: new OfficeLimits(MaxZipEntries: 3)), "too_many_parts");
    }

    [Fact]
    public void ExpandingBeyondTheTotalLimit_IsRefused()
    {
        AssertRefused(Check(XlsxFixtures.Clean(), limits: new OfficeLimits(MaxUncompressedBytes: 500)), "expands_too_far");
    }

    [Fact]
    public void ALargePartWithAnExtremeCompressionRatio_IsRefused()
    {
        // 8 MB of zeros compresses to a few KB: the shape of a ZIP bomb.
        var bytes = XlsxFixtures.WithEntries(XlsxFixtures.Clean(), ("xl/media/pad.bin", new byte[8 * 1024 * 1024]));

        AssertRefused(Check(bytes), "expands_too_far");
    }
}
