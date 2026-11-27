using System.IO.Compression;
using System.Xml;

namespace Litos.Office.Excel;

public sealed record GateResult(bool Ok, IReadOnlyList<Finding> Findings)
{
    public string? FirstError => Findings.FirstOrDefault(f => f.Severity == FindingSeverity.Error)?.Message;
}

/// <summary>
/// The check every workbook passes before ClosedXML parses it (spec §11): only plain .xlsx, no
/// macros, no encryption, no external links or embedded objects, and no ZIP that expands far
/// beyond its size. It reads the package with <see cref="ZipArchive"/> only, never extracts to
/// disk, and reads XML with DTDs prohibited, so a hostile file is refused before it can cost
/// anything. Every refusal is a sentence naming the file and what to do.
/// </summary>
public static class WorkbookGate
{
    private static readonly byte[] OleMagic = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    /// <summary>Entries this large are checked for an excessive compression ratio; small parts
    /// (a sharedStrings full of repeats) legitimately compress very well.</summary>
    private const long RatioCheckFloor = 1024 * 1024;

    /// <summary>The most of one XML part this gate reads to inspect it.</summary>
    private const int MaxInspectedXmlBytes = 4 * 1024 * 1024;

    public static GateResult Check(byte[] bytes, string fileName, OfficeLimits? limits = null)
    {
        limits ??= OfficeLimits.Default;
        var findings = new List<Finding>();
        var name = Path.GetFileName(fileName);

        if (!string.Equals(Path.GetExtension(fileName), ".xlsx", StringComparison.OrdinalIgnoreCase))
            return Refuse(findings, "not_xlsx",
                $"{name} isn't an .xlsx workbook. Litos only reads Excel .xlsx files (not .xlsm, .xls or .csv) for now.");

        if (bytes.LongLength > limits.MaxFileBytes)
            return Refuse(findings, "too_large",
                $"{name} is {bytes.LongLength / (1024 * 1024)} MB, over the {limits.MaxFileBytes / (1024 * 1024)} MB limit for a workbook Litos learns from or runs on.");

        if (bytes.AsSpan().StartsWith(OleMagic))
            return Refuse(findings, "encrypted_or_legacy",
                $"{name} is password-protected or saved in the old Excel format. Open it in Excel, remove the password, and save it as a normal .xlsx.");

        ZipArchive zip;
        try
        {
            zip = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
        }
        catch (InvalidDataException)
        {
            return Refuse(findings, "not_a_workbook", $"{name} isn't a valid Excel workbook (the file may be damaged).");
        }

        using (zip)
        {
            if (zip.Entries.Count > limits.MaxZipEntries)
                return Refuse(findings, "too_many_parts", $"{name} has an unusually large number of internal parts, so Litos won't open it.");

            long total = 0;
            var hasWorkbook = false;
            foreach (var entry in zip.Entries)
            {
                var path = entry.FullName.Replace('\\', '/');
                total += entry.Length;
                if (total > limits.MaxUncompressedBytes)
                    return Refuse(findings, "expands_too_far", $"{name} expands to far more data than its size suggests, so Litos won't open it.");
                if (entry.Length > RatioCheckFloor && entry.Length / Math.Max(entry.CompressedLength, 1) > limits.MaxCompressionRatio)
                    return Refuse(findings, "expands_too_far", $"{name} expands to far more data than its size suggests, so Litos won't open it.");

                if (path.Equals("xl/workbook.xml", StringComparison.OrdinalIgnoreCase))
                    hasWorkbook = true;
                else if (path.Equals("EncryptedPackage", StringComparison.OrdinalIgnoreCase))
                    return Refuse(findings, "encrypted_or_legacy",
                        $"{name} is password-protected. Open it in Excel, remove the password, and save it again.");
                else if (path.Equals("xl/vbaProject.bin", StringComparison.OrdinalIgnoreCase))
                    return Refuse(findings, "macros", $"{name} contains macros. Litos only reads workbooks without macros; save a copy as a plain .xlsx.");
                else if (path.StartsWith("xl/externalLinks/", StringComparison.OrdinalIgnoreCase))
                    return Refuse(findings, "external_links",
                        $"{name} links to other workbooks. Litos doesn't follow external links; break the links in Excel (Data → Edit Links) and save again.");
                else if (path.StartsWith("xl/embeddings/", StringComparison.OrdinalIgnoreCase)
                         || path.StartsWith("xl/activeX/", StringComparison.OrdinalIgnoreCase)
                         || path.StartsWith("xl/ctrlProps/", StringComparison.OrdinalIgnoreCase))
                    return Refuse(findings, "embedded_objects",
                        $"{name} contains embedded objects or controls, which Litos doesn't open. Save a copy without them.");
            }

            if (!hasWorkbook)
                return Refuse(findings, "not_a_workbook", $"{name} isn't a valid Excel workbook (it has no workbook part).");

            if (zip.GetEntry("[Content_Types].xml") is { } types
                && ReadBounded(types) is { } typesBytes
                && System.Text.Encoding.UTF8.GetString(typesBytes).Contains("macroEnabled", StringComparison.OrdinalIgnoreCase))
                return Refuse(findings, "macros", $"{name} is a macro-enabled workbook. Litos only reads workbooks without macros.");

            foreach (var rels in zip.Entries.Where(e => e.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)))
            {
                var xml = ReadBounded(rels);
                if (xml is null)
                    return Refuse(findings, "not_a_workbook", $"{name} has a damaged internal part, so Litos won't open it.");
                if (HasExternalRelationship(xml, out var unreadable))
                    return Refuse(findings, "external_links",
                        $"{name} refers to files or addresses outside the workbook. Litos doesn't follow external links; remove them and save again.");
                if (unreadable)
                    return Refuse(findings, "not_a_workbook", $"{name} has a damaged internal part, so Litos won't open it.");
            }
        }

        return new GateResult(true, findings);
    }

    /// <summary>A relationship that points outside the package and is not an ordinary hyperlink
    /// (a hyperlink in a cell is just text to Litos and is never followed).</summary>
    private static bool HasExternalRelationship(byte[] xml, out bool unreadable)
    {
        unreadable = false;
        try
        {
            // From bytes, not a string: Office parts start with a UTF-8 byte-order mark, which the
            // reader handles itself but rejects as text before the declaration.
            using var reader = XmlReader.Create(new MemoryStream(xml, writable: false), SafeXml);
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Relationship")
                    continue;
                var external = string.Equals(reader.GetAttribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase);
                var type = reader.GetAttribute("Type") ?? "";
                if (external && !type.EndsWith("/hyperlink", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        catch (XmlException)
        {
            unreadable = true;
            return false;
        }
    }

    internal static readonly XmlReaderSettings SafeXml = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = MaxInspectedXmlBytes,
    };

    /// <summary>Reads at most <see cref="MaxInspectedXmlBytes"/> of a part, counting the bytes
    /// actually produced rather than trusting the declared size. Null when it is larger.</summary>
    private static byte[]? ReadBounded(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > MaxInspectedXmlBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static GateResult Refuse(List<Finding> findings, string code, string message)
    {
        findings.Add(Finding.Error(code, message));
        return new GateResult(false, findings);
    }
}
