namespace Litos.SoftwareFactory.Core.Settings;

/// <summary>
/// Files and images as task input (attachments-architecture.md §1): on by default for anyone who
/// may post to a thread, with limits beyond the 20 MB per file every Litos face uses. Turning
/// uploads off stops new ones; files already attached stay readable and still reach runs.
/// </summary>
public sealed record AttachmentSettings
{
    public bool Enabled { get; init; } = true;

    public int MaxFilesPerMessage { get; init; } = 5;

    public long MaxBytesPerThread { get; init; } = 50L * 1024 * 1024;

    public const int MaxFilesLimit = 20;

    /// <summary>A thread may hold at most this much, however an Admin sets the limit.</summary>
    public const long MaxBytesPerThreadLimit = 1024L * 1024 * 1024;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (MaxFilesPerMessage is < 1 or > MaxFilesLimit)
            errors.Add($"Files per message must be between 1 and {MaxFilesLimit}.");
        if (MaxBytesPerThread < Contracts.AttachmentTypes.MaxFileBytes || MaxBytesPerThread > MaxBytesPerThreadLimit)
            errors.Add($"The total per thread must be between 20 MB and {MaxBytesPerThreadLimit / (1024 * 1024)} MB.");
        return errors;
    }
}
