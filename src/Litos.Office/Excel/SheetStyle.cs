namespace Litos.Office.Excel;

/// <summary>What a cell looks like, as plain facts: enough to describe formatting in a summary and
/// to compare an output against its template. Colours are ARGB hex, or null for theme/automatic.</summary>
public sealed record CellStyleSnapshot(
    string NumberFormat,
    bool Bold,
    bool Italic,
    string? FontColor,
    string? FillColor,
    string? HorizontalAlignment,
    bool HasBorder);

/// <summary>
/// The layout of the table on its sheet. Rows and columns are 1-based worksheet positions, so the
/// template extractor and writer can address the same cells the reader saw.
/// </summary>
public sealed record SheetStyle(
    string SheetName,
    int HeaderRow,
    int FirstColumn,
    IReadOnlyList<CellStyleSnapshot> HeaderStyles,
    IReadOnlyList<CellStyleSnapshot> DataStyles,
    IReadOnlyList<CellStyleSnapshot>? TotalRowStyles,
    IReadOnlyList<double> ColumnWidths,
    int FrozenRows,
    bool HasAutoFilter,
    bool HasTotalRow);

/// <summary>A sheet read into a <see cref="Data.Table"/>, with its layout and anything a person
/// should know about how it was read.</summary>
public sealed record LoadedSheet(
    Data.Table Table,
    SheetStyle Style,
    IReadOnlyList<Finding> Findings,
    string Sha256);
