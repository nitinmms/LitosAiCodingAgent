using Litos.Agent.Session;
using Litos.VsCodeHost.Turns;

namespace Litos.VsCodeHost.Tests.Turns;

/// <summary>
/// Covers ContextEndpoints.ToWireUsage — the mapping GET /sessions/{id}/context/usage applies to
/// ContextUsage.Compute's result before sending it to the VS Code extension. Compute returns null
/// for a session that's never had a turn run (no transcript.LastUsage yet), which used to surface
/// as a bare "Context usage unavailable" placeholder even though the model's real contextLength
/// was already known (EnsureModelResolvedAsync runs before this mapping) — this pins the fix:
/// a null snapshot maps to an explicit zero-usage reading against the real contextLength, not to
/// omitting the number entirely.
/// </summary>
public sealed class ContextEndpointsTests
{
    [Fact]
    public void ToWireUsage_NullSnapshot_ReportsZeroUsageAgainstTheKnownContextLength()
    {
        var wire = ContextEndpoints.ToWireUsage(snapshot: null, contextLength: 22_016);

        Assert.Equal(0, wire.UsedTokens);
        Assert.Equal(22_016, wire.ContextLength);
        Assert.Equal(0, wire.Fraction);
        Assert.Equal(ContextUsageLevel.Normal.ToString(), wire.Level);
        Assert.False(wire.IsStale);
    }

    [Fact]
    public void ToWireUsage_RealSnapshot_PassesItThroughUnchanged()
    {
        var snapshot = new ContextUsageSnapshot(UsedTokens: 8_347, ContextLength: 22_016, Fraction: 0.379, Level: ContextUsageLevel.Warning, IsStale: false);

        var wire = ContextEndpoints.ToWireUsage(snapshot, contextLength: 22_016);

        Assert.Equal(8_347, wire.UsedTokens);
        Assert.Equal(22_016, wire.ContextLength);
        Assert.Equal(0.379, wire.Fraction);
        Assert.Equal(ContextUsageLevel.Warning.ToString(), wire.Level);
        Assert.False(wire.IsStale);
    }

    [Fact]
    public void ToWireUsage_RealSnapshot_CarriesIsStaleThrough()
    {
        var snapshot = new ContextUsageSnapshot(UsedTokens: 1_000, ContextLength: 22_016, Fraction: 0.05, Level: ContextUsageLevel.Normal, IsStale: true);

        var wire = ContextEndpoints.ToWireUsage(snapshot, contextLength: 22_016);

        Assert.True(wire.IsStale);
    }
}
