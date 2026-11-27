using System.IO.Compression;
using System.Text;
using ClosedXML.Excel;

namespace Litos.Office.Tests;

/// <summary>
/// Workbooks built in code, so every fixture is visible in the test that uses it and no real
/// customer data is ever checked in. Hostile files are made by editing a clean package's ZIP.
/// </summary>
internal static class XlsxFixtures
{
    public static byte[] Build(Action<XLWorkbook> fill)
    {
        using var workbook = new XLWorkbook();
        fill(workbook);
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    /// <summary>One sheet: headers in row 1, then rows. A cell value of null is left empty.</summary>
    public static byte[] Sheet(string sheetName, string[] headers, params object?[][] rows) =>
        Build(workbook => Fill(workbook.AddWorksheet(sheetName), 1, 1, headers, rows));

    public static void Fill(IXLWorksheet sheet, int row, int column, string[] headers, object?[][] rows)
    {
        for (var c = 0; c < headers.Length; c++)
            sheet.Cell(row, column + c).Value = headers[c];
        for (var r = 0; r < rows.Length; r++)
            for (var c = 0; c < rows[r].Length; c++)
                Set(sheet.Cell(row + 1 + r, column + c), rows[r][c]);
    }

    public static void Set(IXLCell cell, object? value)
    {
        switch (value)
        {
            case null: break;
            case string s: cell.Value = s; break;
            case decimal d: cell.Value = d; break;
            case int i: cell.Value = i; break;
            case double x: cell.Value = x; break;
            case DateTime t: cell.Value = t; break;
            case bool b: cell.Value = b; break;
            default: throw new ArgumentException($"Unsupported fixture value {value.GetType()}");
        }
    }

    /// <summary>The spec's §6 original workbook.</summary>
    public static byte[] SalesRaw() => Sheet("Sales", ["Region", "Amount", "Status"],
        ["North", 100m, "Completed"],
        ["South", 200m, "Completed"],
        ["North", 50m, "Cancelled"],
        ["North", 300m, "Completed"]);

    public static byte[] Clean() => Sheet("Data", ["A", "B"], ["x", 1m]);

    /// <summary>A clean package with entries added or replaced.</summary>
    public static byte[] WithEntries(byte[] package, params (string Name, byte[] Content)[] entries)
    {
        using var output = new MemoryStream();
        using (var source = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read))
        using (var target = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var replaced = entries.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in source.Entries.Where(e => !replaced.Contains(e.FullName)))
            {
                using var from = entry.Open();
                using var to = target.CreateEntry(entry.FullName).Open();
                from.CopyTo(to);
            }
            foreach (var (name, content) in entries)
            {
                using var to = target.CreateEntry(name, CompressionLevel.Optimal).Open();
                to.Write(content);
            }
        }
        return output.ToArray();
    }

    public static byte[] WithEntry(byte[] package, string name, string content) =>
        WithEntries(package, (name, Encoding.UTF8.GetBytes(content)));

    public static string EntryText(byte[] package, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        using var reader = new StreamReader(zip.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }

    /// <summary>A ZIP that is not a workbook at all.</summary>
    public static byte[] ZipOf(params (string Name, byte[] Content)[] entries)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in entries)
            {
                using var to = zip.CreateEntry(name).Open();
                to.Write(content);
            }
        return output.ToArray();
    }
}
