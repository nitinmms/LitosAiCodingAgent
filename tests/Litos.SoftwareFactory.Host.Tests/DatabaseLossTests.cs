using System.Reflection;
using System.Runtime.ExceptionServices;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Host.Runs;
using Litos.SoftwareFactory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>
/// Acceptance scenario 20 (ReadMe_LitosSoftwareFactory_V1.md §14 rule 5): while the database
/// cannot be reached, the coordinator starts nothing, and it starts what waits once the database
/// is back, with no restart.
/// </summary>
public sealed class DatabaseLossTests
{
    /// <summary>The real store, behind a switch that makes every call fail as a lost database would.</summary>
    public class LosableStore : DispatchProxy
    {
        public IFactoryStore Inner { get; set; } = null!;

        public volatile bool Lost;

        public int RefusedCalls;

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (Lost)
            {
                Interlocked.Increment(ref RefusedCalls);
                throw new InvalidOperationException("The factory database cannot be reached.");
            }

            try
            {
                return method!.Invoke(Inner, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }

        public static IFactoryStore Wrap(IFactoryStore inner)
        {
            var store = Create<IFactoryStore, LosableStore>();
            ((LosableStore)(object)store).Inner = inner;
            return store;
        }
    }

    [Fact]
    public async Task WhileTheDatabaseIsLost_NothingStarts_AndOnceItIsBack_TheQueuedTaskRuns()
    {
        await using var host = await TestHost.StartAsync(services: s =>
            s.AddSingleton(sp => LosableStore.Wrap(new EfFactoryStore(sp.GetRequiredService<IDbContextFactory<FactoryDbContext>>()))));
        var store = (LosableStore)(object)host.Store;
        var threadId = await host.CreateThreadAsync(await host.RegisterProjectAsync());

        // Queued at the moment the database goes: written straight to it, as the coordinator cannot be.
        store.Lost = true;
        var queued = await store.Inner.DispatchAsync(threadId, await host.AdminIdAsync(), "lost-1", "Add CSV export for Orders.", DateTimeOffset.UtcNow, default);
        host.App.Services.GetRequiredService<FactorySignals>().WorkQueued();

        // The coordinator keeps trying, and fails each time, without starting anything.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (Volatile.Read(ref store.RefusedCalls) < 3 && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        Assert.True(store.RefusedCalls >= 3, "The coordinator did not try to claim while the database was lost.");
        Assert.Empty(host.Workers.Workers);
        var waiting = (await store.Inner.GetThreadAsync(threadId, default))!;
        Assert.Equal((LifecycleState.Queued, RunStatus.Queued), (waiting.Thread.State, waiting.LatestRun!.Status));
        Assert.Equal(queued.RunId, waiting.LatestRun.Id);

        store.Lost = false;

        await host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);
    }
}
