using System.Runtime.CompilerServices;
using System.Text;

namespace Litos.Persistence;

/// <summary>
/// File access for transcripts that tolerates another handle on the same file. A factory review
/// failed with "the process cannot access the file ... because it is being used by another
/// process" while appending one entry to its transcript: something outside Litos (on Windows,
/// typically antivirus or the search indexer scanning a file just written) held it for a moment,
/// and one failed append failed the whole turn. File.AppendAllText and File.ReadLines also open
/// with sharing that refuses a concurrent writer or reader. Here every open shares read, write and
/// delete, and an append that still meets a sharing violation is retried briefly.
/// </summary>
public static class SharedFile
{
    private const FileShare Share = FileShare.ReadWrite | FileShare.Delete;

    /// <summary>Waits between attempts at an append the file is briefly locked against; tests shorten it.</summary>
    internal static TimeSpan[] RetryDelays { get; set; } =
        [TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500)];

    public static async Task AppendAsync(string path, string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, Share, bufferSize: 4096, useAsync: true);
                await stream.WriteAsync(bytes, ct);
                return;
            }
            catch (IOException ex) when (IsSharingViolation(ex) && attempt < RetryDelays.Length)
            {
                await Task.Delay(RetryDelays[attempt], ct);
            }
        }
    }

    public static async IAsyncEnumerable<string> ReadLinesAsync(string path, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var stream = OpenRead(path);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct) is { } line)
            yield return line;
    }

    public static string[] ReadAllLines(string path)
    {
        using var stream = OpenRead(path);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
            lines.Add(line);
        return [.. lines];
    }

    public static async Task<string[]> ReadAllLinesAsync(string path, CancellationToken ct)
    {
        var lines = new List<string>();
        await foreach (var line in ReadLinesAsync(path, ct))
            lines.Add(line);
        return [.. lines];
    }

    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, Share, bufferSize: 4096, useAsync: true);

    /// <summary>ERROR_SHARING_VIOLATION (32) or ERROR_LOCK_VIOLATION (33): another handle holds
    /// the file. On other systems this does not arise from sharing modes.</summary>
    internal static bool IsSharingViolation(IOException ex) => (ex.HResult & 0xFFFF) is 32 or 33;
}
