namespace Litos.Hosting;

/// <summary>
/// Decides the working directory a turn runs in, given the one its transcript recorded (null
/// for a session that has not recorded one yet).
/// </summary>
public interface IWorkingDirectoryResolver
{
    string Resolve(string? transcriptWorkingDirectory);
}

/// <summary>The transcript's own directory, else the process's current directory —
/// Litos.VsCodeHost's behaviour, where a resumed session keeps the folder it started in.</summary>
public sealed class TranscriptWorkingDirectoryResolver : IWorkingDirectoryResolver
{
    public string Resolve(string? transcriptWorkingDirectory) =>
        transcriptWorkingDirectory ?? Directory.GetCurrentDirectory();
}

/// <summary>Always the process's current directory, whatever the transcript recorded. The
/// software factory launches each worker inside the run's working copy, and a session resumed by
/// a later run must follow that run's directory, not the one an earlier run recorded.</summary>
public sealed class ProcessWorkingDirectoryResolver : IWorkingDirectoryResolver
{
    public string Resolve(string? transcriptWorkingDirectory) => Directory.GetCurrentDirectory();
}
