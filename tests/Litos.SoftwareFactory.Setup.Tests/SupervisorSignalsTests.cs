namespace Litos.SoftwareFactory.Setup.Tests;

/// <summary>One supervisor per user, and the stop signal litos-factory stop sends it.</summary>
public sealed class SupervisorSignalsTests
{
    [Fact]
    public void OneSupervisorPerUser()
    {
        var config = Directory.CreateTempSubdirectory("litos-setup-signals-").FullName;
        try
        {
            var paths = new FactoryPaths(@"C:\install", config, @"C:\local");
            Assert.False(SupervisorSignals.IsRunning(paths));

            using (var first = SupervisorSignals.TryAcquire(paths))
            {
                Assert.NotNull(first);
                Assert.True(SupervisorSignals.IsRunning(paths));
                Assert.Null(SupervisorSignals.TryAcquire(paths));
            }

            Assert.False(SupervisorSignals.IsRunning(paths));
        }
        finally
        {
            Directory.Delete(config, recursive: true);
        }
    }

    [Fact]
    public async Task Signal_StopsTheSupervisor_AndWaitUntilGoneSeesItLeave()
    {
        var config = Directory.CreateTempSubdirectory("litos-setup-stop-").FullName;
        try
        {
            var paths = new FactoryPaths(@"C:\install", config, @"C:\local");
            var signals = SupervisorSignals.TryAcquire(paths)!;
            // As the supervisor does: its loop ends on the stop, and then the signals are disposed.
            signals.Stopping.Register(() => Task.Run(signals.Dispose));

            Assert.True(SupervisorSignals.Signal(paths));
            Assert.True(await SupervisorSignals.WaitUntilGoneAsync(paths, TimeSpan.FromSeconds(10), CancellationToken.None));
            Assert.False(SupervisorSignals.IsRunning(paths));
        }
        finally
        {
            Directory.Delete(config, recursive: true);
        }
    }

    [Fact]
    public void AStopSentBeforeASupervisorStarted_DoesNotStopIt()
    {
        var config = Directory.CreateTempSubdirectory("litos-setup-early-").FullName;
        try
        {
            var paths = new FactoryPaths(@"C:\install", config, @"C:\local");
            using (var earlier = SupervisorSignals.TryAcquire(paths)!)
                Assert.True(SupervisorSignals.Signal(paths));

            using var next = SupervisorSignals.TryAcquire(paths)!;

            Assert.False(next.Stopping.IsCancellationRequested);
        }
        finally
        {
            Directory.Delete(config, recursive: true);
        }
    }
}
