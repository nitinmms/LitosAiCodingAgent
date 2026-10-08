using System.Collections.Concurrent;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Store;

namespace Litos.SoftwareFactory.Host.Runs;

/// <summary>
/// The projects' reading copies, which chat and spec runs read (m2-architecture.md §5), and the
/// one lock per copy that makes them take turns: each run checks out the branch it reads and
/// holds the copy until it is done, so no run reads a copy another has moved.
/// </summary>
public sealed class ReadingCopies(IWorkspaceProvider workspaces)
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public IWorkspace For(Project project) => workspaces.ReadingCopyFor(project);

    /// <summary>Waits for the project's reading copy, and holds it until disposed.</summary>
    public async Task<IDisposable> HoldAsync(Guid projectId, CancellationToken ct)
    {
        var gate = _locks.GetOrAdd(projectId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        return new Held(gate);
    }

    private sealed class Held(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                gate.Release();
        }
    }
}
