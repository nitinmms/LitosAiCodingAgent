namespace Litos.SoftwareFactory.Host.Runs;

/// <summary>
/// Bounds how many verifications run at once across all runs (FACTORY_VERIFY_CONCURRENCY). The
/// slot cap bounds runs, not the machine: two builds and test suites at once compete for CPU and
/// memory, and tests that bind a fixed port collide. A run's verify step waits its turn instead
/// (docs/software-factory/m2-architecture.md §3.4).
/// </summary>
public sealed class VerificationGate(FactoryOptions options)
{
    private readonly SemaphoreSlim _permits = new(options.VerifyConcurrency, options.VerifyConcurrency);

    /// <summary>
    /// Takes a permit, waiting for one if all are in use; <paramref name="onWait"/> is called once,
    /// before waiting. Dispose the result to give the permit back. Cancelling the wait takes nothing.
    /// </summary>
    public async Task<IDisposable> EnterAsync(Func<Task>? onWait, CancellationToken ct)
    {
        if (!await _permits.WaitAsync(0, ct))
        {
            if (onWait is not null)
                await onWait();
            await _permits.WaitAsync(ct);
        }

        return new Permit(_permits);
    }

    private sealed class Permit(SemaphoreSlim permits) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                permits.Release();
        }
    }
}
