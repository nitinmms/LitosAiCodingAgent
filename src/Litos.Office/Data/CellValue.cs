using System.Globalization;

namespace Litos.Office.Data;

public enum CellKind { Empty, Text, Number, Date, Bool }

/// <summary>
/// One cell as Litos understands it. Numbers are <see cref="decimal"/>, so money adds up exactly;
/// text is never turned into a number or date on the way in (a "number stored as text" is reported
/// by the reader, not quietly coerced), and formatting is invariant-culture throughout, because
/// Litos.Gui publishes with InvariantGlobalization.
/// </summary>
public readonly record struct CellValue(CellKind Kind, string? Text = null, decimal? Number = null, DateTime? Date = null, bool? Bool = null)
{
    public static CellValue Empty => default;

    public static CellValue OfText(string text) => new(CellKind.Text, Text: text);

    public static CellValue OfNumber(decimal number) => new(CellKind.Number, Number: number);

    public static CellValue OfDate(DateTime date) => new(CellKind.Date, Date: date);

    public static CellValue OfBool(bool value) => new(CellKind.Bool, Bool: value);

    public bool IsEmpty => Kind == CellKind.Empty || (Kind == CellKind.Text && string.IsNullOrWhiteSpace(Text));

    /// <summary>How the value reads to a person, and how it is compared as a group key.</summary>
    public string ToDisplay() => Kind switch
    {
        CellKind.Text => Text ?? "",
        // Without trailing zeros, so 9.0 and 9 read (and group) the same: decimals keep their scale
        // through arithmetic (1.5 × 2 = 3.0). A custom format, not "G29", which can go scientific.
        CellKind.Number => Number!.Value.ToString("0.############################", CultureInfo.InvariantCulture),
        CellKind.Date => Date!.Value.TimeOfDay == TimeSpan.Zero
            ? Date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : Date.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        CellKind.Bool => Bool!.Value ? "TRUE" : "FALSE",
        _ => "",
    };

    public override string ToString() => $"{Kind}:{ToDisplay()}";
}
