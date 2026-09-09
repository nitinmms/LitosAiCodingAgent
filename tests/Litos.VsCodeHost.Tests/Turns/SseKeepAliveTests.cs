using System.Threading.Channels;
using Litos.Agent.Messages;
using Litos.Agent.Streaming;
using Litos.Tools.Shell;
using Litos.VsCodeHost.Approvals;
using Litos.VsCodeHost.Turns;

namespace Litos.VsCodeHost.Tests.Turns;

/// <summary>
/// Covers TurnsEndpoints.ToSseData's keep-alive: without it, the /turns response puts no bytes on
/// the wire between real AgentEvents, so a quiet-but-healthy turn (a slow tool call, or a local
/// model still working on its first token) reads as a dead connection to every idle watchdog in
/// front of it. Two have already been hit for real — Kestrel's own MinResponseDataRate (disabled
/// in Program.cs because of it) and the VS Code extension's Node-side fetch, whose undici default
/// aborts after 300s without body data. A short interval is passed throughout so these finish in
/// milliseconds rather than sitting through the real one.
/// </summary>
public sealed class SseKeepAliveTests
{
    private static readonly TimeSpan ShortInterval = TimeSpan.FromMilliseconds(50);

    private static ChannelReader<AgentEvent> EventChannel(params AgentEvent[] events)
    {
        var channel = Channel.CreateUnbounded<AgentEvent>();
        foreach (var evt in events)
            channel.Writer.TryWrite(evt);
        channel.Writer.TryComplete();
        return channel.Reader;
    }

    [Fact]
    public async Task ToSseData_QuietStream_EmitsKeepAlives_UntilARealEventArrives()
    {
        // A turn that produces nothing for a while, then finally speaks — the shape of every
        // slow local-model turn this exists for.
        var events = Channel.CreateUnbounded<AgentEvent>();
        var relay = new PendingApprovalRelay(new PendingApprovalStore());

        var emitted = new List<string>();
        // Drained to completion rather than broken out of early: ToSseData's finally awaits its
        // event pump, which only ends when the channel completes or ct cancels — exactly what
        // production does (AgentWorker completes the channel; ASP.NET cancels ct on disconnect).
        var drain = Task.Run(async () =>
        {
            await foreach (var json in TurnsEndpoints.ToSseData("s1", events.Reader, relay, CancellationToken.None, ShortInterval))
                lock (emitted) emitted.Add(json);
        });

        // Stay silent for several keep-alive intervals before saying anything real.
        await Task.Delay(ShortInterval * 6);
        int keepAlivesDuringSilence;
        lock (emitted) keepAlivesDuringSilence = emitted.Count(e => e == TurnsEndpoints.KeepAlivePayload);

        events.Writer.TryWrite(new TextDelta("finally"));
        events.Writer.TryComplete();
        await drain.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(keepAlivesDuringSilence > 0, "expected at least one keep-alive while the stream was quiet");
        Assert.Contains(emitted, e => e.Contains("\"Text\""));
    }

    [Fact]
    public async Task ToSseData_RealEvents_PassThroughUnchanged_AndAreNotReplacedByKeepAlives()
    {
        var relay = new PendingApprovalRelay(new PendingApprovalStore());
        var reader = EventChannel(new TextDelta("one"), new TextDelta("two"));

        var emitted = new List<string>();
        await foreach (var json in TurnsEndpoints.ToSseData("s1", reader, relay, CancellationToken.None, ShortInterval))
            emitted.Add(json);

        // Events already queued are drained without ever going idle, so no keep-alive should
        // appear at all — and the payloads themselves must be untouched.
        Assert.DoesNotContain(TurnsEndpoints.KeepAlivePayload, emitted);
        Assert.Equal(2, emitted.Count);
        Assert.All(emitted, e => Assert.Contains("\"Text\"", e));
    }

    [Fact]
    public async Task ToSseData_CompletesWhenTheTurnEnds_RatherThanKeepingAliveForever()
    {
        // The keep-alive timer must not outlive the turn: once the agent-event channel completes
        // and drains, this stream has to end, or the response would never close.
        var relay = new PendingApprovalRelay(new PendingApprovalStore());
        var reader = EventChannel(new TextDelta("only"));

        var emitted = new List<string>();
        var drain = Task.Run(async () =>
        {
            await foreach (var json in TurnsEndpoints.ToSseData("s1", reader, relay, CancellationToken.None, ShortInterval))
                emitted.Add(json);
        });

        await drain.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(emitted, e => e.Contains("\"Text\""));
    }
}
