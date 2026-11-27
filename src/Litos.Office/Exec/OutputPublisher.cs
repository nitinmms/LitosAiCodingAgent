using System.Globalization;
using System.Text;

namespace Litos.Office.Exec;

/// <summary>
/// Puts a validated report on disk (design §2 OutputPublisher, spec §11: temporary output, publish
/// only after validation, never overwrite). The bytes go to a hidden temporary file in the target
/// folder and are moved into place in one step, so nothing ever sees a half-written report; an
/// existing file is never replaced — the new one gets " (2)", " (3)"…
/// </summary>
public static class OutputPublisher
{
    private const int MaxAttempts = 100;

    /// <summary>The report's file name from the plan's pattern: {date} is yyyy-MM-dd, {period} is
    /// yyyy-MM, {input} is the input file's name without extension. Unsafe characters become "_".</summary>
    public static string NameFor(string pattern, DateTimeOffset now, string inputFileName)
    {
        var name = pattern
            .Replace("{date}", now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{period}", now.ToString("yyyy-MM", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{input}", Path.GetFileNameWithoutExtension(inputFileName), StringComparison.Ordinal);

        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var safe = new StringBuilder(name.Length);
        foreach (var ch in name)
            safe.Append(invalid.Contains(ch) || char.IsControl(ch) ? '_' : ch);

        var stem = Path.GetFileNameWithoutExtension(safe.ToString()).Trim().Trim('.');
        if (stem.Length == 0)
            stem = "report";
        if (stem.Length > 120)
            stem = stem[..120];
        return stem + ".xlsx";
    }

    /// <summary>Writes <paramref name="bytes"/> as <paramref name="fileName"/> in
    /// <paramref name="outputDir"/> without replacing anything, and returns the path used.</summary>
    public static string Publish(byte[] bytes, string outputDir, string fileName)
    {
        Directory.CreateDirectory(outputDir);
        var temp = Path.Combine(outputDir, $".{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temp, bytes);
            var stem = Path.GetFileNameWithoutExtension(fileName);
            var extension = Path.GetExtension(fileName);
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                var candidate = Path.Combine(outputDir, attempt == 1 ? fileName : $"{stem} ({attempt}){extension}");
                if (File.Exists(candidate))
                    continue;
                try
                {
                    File.Move(temp, candidate, overwrite: false);
                    return candidate;
                }
                catch (IOException) when (File.Exists(candidate))
                {
                    // Another writer took the name between the check and the move: try the next one.
                }
            }
            throw new IOException($"Couldn't find a free name for {fileName} in {outputDir}.");
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }
}
