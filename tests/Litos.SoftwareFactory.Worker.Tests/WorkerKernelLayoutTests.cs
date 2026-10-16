using System.Text.Json;

namespace Litos.SoftwareFactory.Worker.Tests;

/// <summary>
/// Regression coverage for m3-architecture.md §12: a worker run from its build output found no
/// Litos.Kernel.Host beside it, so KernelHostLocator built one while the worker started — longer
/// than the host's 30 s port handshake whenever another run's kernel held the build's files (F4
/// was blocked three times). The worker now references the kernel project so it is built beside
/// the worker. This test project references Litos.Kernel.Host itself, so finding the exe in this
/// output directory proves nothing; the worker's own deps.json names its project dependencies.
/// </summary>
public class WorkerKernelLayoutTests
{
    [Fact]
    public void TheWorkerDependsOnTheKernelHost_SoItIsBuiltBesideTheWorker()
    {
        var depsPath = Path.Combine(AppContext.BaseDirectory, "Litos.SoftwareFactory.Worker.deps.json");
        using var deps = JsonDocument.Parse(File.ReadAllText(depsPath));

        var worker = deps.RootElement.GetProperty("targets").EnumerateObject().Single().Value
            .EnumerateObject().Single(library => library.Name.StartsWith("Litos.SoftwareFactory.Worker/", StringComparison.Ordinal)).Value;

        Assert.True(
            worker.GetProperty("dependencies").TryGetProperty("Litos.Kernel.Host", out _),
            "Litos.SoftwareFactory.Worker must reference Litos.Kernel.Host, or a worker builds the kernel while it starts.");
    }
}
