namespace Litos.Office;

/// <summary>
/// Hard limits on what Litos will open (spec §11: limit ZIP expansion, XML size, rows, memory).
/// Defaults suit a monthly report workbook; tests pass tiny limits to prove each one bites.
/// </summary>
public sealed record OfficeLimits(
    long MaxFileBytes = 25L * 1024 * 1024,
    long MaxUncompressedBytes = 200L * 1024 * 1024,
    int MaxZipEntries = 2000,
    int MaxCompressionRatio = 100,
    int MaxRows = 200_000,
    int MaxColumns = 100,
    int MaxCellChars = 32_767)
{
    public static OfficeLimits Default { get; } = new();
}

public enum FindingSeverity { Warning, Error }

/// <summary>Something a person needs to know about a file, in a sentence they can act on.</summary>
public sealed record Finding(FindingSeverity Severity, string Code, string Message)
{
    public static Finding Error(string code, string message) => new(FindingSeverity.Error, code, message);

    public static Finding Warning(string code, string message) => new(FindingSeverity.Warning, code, message);
}

/// <summary>A file Litos can't use, with the reason a person can act on.</summary>
public sealed class OfficeFileException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}
