using System.Net;
using System.Text.Json;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Host.Api;

namespace Litos.SoftwareFactory.Host.Tests;

/// <summary>The operational counters (m4-architecture.md §7): for Admins only.</summary>
public sealed class CountersApiTests : IAsyncLifetime
{
    private TestHost _host = null!;

    public async Task InitializeAsync() => _host = await TestHost.StartAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task AnAdmin_SeesTheCounters_AfterATaskHasRunAndStopped()
    {
        var threadId = await _host.CreateThreadAsync(await _host.RegisterProjectAsync());
        await _host.DelegateAsync(threadId);
        await _host.WaitForStateAsync(threadId, LifecycleState.AwaitingHumanTesting);

        var counters = await _host.GetAsync("api/admin/counters");

        Assert.Equal((0, 0), (counters.GetProperty("tasks").GetProperty("queued").GetInt32(), counters.GetProperty("tasks").GetProperty("running").GetInt32()));
        Assert.Empty(counters.GetProperty("runs").EnumerateArray());
        Assert.Equal(0, counters.GetProperty("unsettled").GetProperty("count").GetInt32());
        Assert.Equal((7, 0), (counters.GetProperty("pushFailures").GetProperty("days").GetInt32(), counters.GetProperty("pushFailures").GetProperty("count").GetInt32()));
        Assert.Equal(0, counters.GetProperty("openDecisions").GetInt32());
        Assert.Equal(JsonValueKind.Null, counters.GetProperty("oldestDecision").ValueKind);
        Assert.Equal(0, counters.GetProperty("reflections").GetProperty("failed").GetInt32());
        Assert.True(counters.GetProperty("eventStreams").GetProperty("open").GetInt32() >= 0);
    }

    [Fact]
    public async Task AMember_CannotSeeTheCounters()
    {
        await _host.CreateMemberAsync("maria", await _host.RegisterProjectAsync());
        var maria = await _host.SignedInAsync("maria");

        using var refused = await maria.GetAsync("api/admin/counters");

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);
    }

    [Fact]
    public void EventStreams_CountWhatIsOpen_AndHowLongTheSlowestHasBeenBehind()
    {
        var clock = new Clock();
        var streams = new EventStreams(clock);
        Assert.Equal((0, TimeSpan.Zero), streams.Snapshot());

        using var first = streams.Open();
        var second = streams.Open();
        clock.UtcNow = clock.UtcNow.AddSeconds(40);
        second.CaughtUp();

        Assert.Equal((2, TimeSpan.FromSeconds(40)), streams.Snapshot());
        first.CaughtUp();
        Assert.Equal((2, TimeSpan.Zero), streams.Snapshot());
        second.Dispose();
        Assert.Equal(1, streams.Snapshot().Open);
    }
}
