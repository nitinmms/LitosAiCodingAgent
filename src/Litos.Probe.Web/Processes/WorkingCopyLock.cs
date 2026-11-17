using System.Security.Cryptography;
using System.Text;

namespace Litos.Probe.Web.Processes;

public sealed class WorkingCopyBusyException(string folder)
    : Exception($"An app is already running from {folder}. Probe.Web runs one app at a time per working copy.");

/// <summary>
/// One running app per working copy (README §8): a lock file in the temp folder, held open while
/// the app runs, so a second start in the same folder fails at once, from this process or
/// another. The lock lives outside the repository, so it never shows in git status.
/// </summary>
internal sealed class WorkingCopyLock : IDisposable
{
    private readonly FileStream _file;

    private WorkingCopyLock(FileStream file) => _file = file;

    public static WorkingCopyLock Acquire(string workingCopy)
    {
        var full = Path.GetFullPath(workingCopy).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var key = OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        var directory = Path.Combine(Path.GetTempPath(), "litos-probe-web", "locks");
        Directory.CreateDirectory(directory);

        try
        {
            var file = new FileStream(Path.Combine(directory, hash + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            return new WorkingCopyLock(file);
        }
        catch (IOException)
        {
            throw new WorkingCopyBusyException(full);
        }
    }

    public void Dispose() => _file.Dispose();
}
