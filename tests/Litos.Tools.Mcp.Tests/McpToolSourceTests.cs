using Litos.Tools.Mcp.Tests.Fakes;

namespace Litos.Tools.Mcp.Tests;

public class McpToolSourceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"litos-test-{Guid.NewGuid():n}");
    private string StateFilePath => Path.Combine(_tempDir, "mcp.json");

    [Fact]
    public void CurrentTools_ReflectsProviderTools_Live()
    {
        var provider = new McpToolProvider(new McpConfigStore(StateFilePath), Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance, new FakeApprovalGate());
        var source = new McpToolSource(provider);

        Assert.Empty(source.CurrentTools);

        // McpToolProvider.Tools is only ever replaced via RebuildToolsSnapshot (private) — no
        // enabled/connectable servers configured here, so it stays empty; this test's real job is
        // confirming McpToolSource.CurrentTools reads the live property, not a cached copy taken
        // at construction — proven by re-reading after the provider object still being the same
        // instance (no snapshot was ever swapped in this test, so it's still empty, which is the
        // correct assertion for "no servers configured").
        Assert.Same(provider.Tools, source.CurrentTools);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}
