using System.IO.Pipelines;
using System.Text;

namespace Litos.SoftwareFactory.Tray.Tests;

public sealed class SseReaderTests
{
    private static async Task<List<SseEvent>> ReadAllAsync(string text)
    {
        var events = new List<SseEvent>();
        await foreach (var evt in SseReader.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), TimeSpan.FromSeconds(5), default))
            events.Add(evt);
        return events;
    }

    [Fact]
    public async Task ReadsTheBoardStreamsFraming()
    {
        var events = await ReadAllAsync("id: 7\nevent: thread\ndata: {\"id\":1}\n\n: keep-alive\n\nid: 9\n\n");

        Assert.Equal([new SseEvent("7", "thread", "{\"id\":1}"), new SseEvent("9", "message", null)], events);
    }

    [Fact]
    public async Task JoinsDataLines_AndTakesFieldsWithoutASpace()
    {
        var events = await ReadAllAsync("event:note\ndata:one\ndata: two\n\n");

        Assert.Equal([new SseEvent(null, "note", "one\ntwo")], events);
    }

    [Fact]
    public async Task IgnoresUnknownFields_AndABlockLeftUnfinished()
    {
        var events = await ReadAllAsync("retry: 1000\nfoo: bar\n\ndata: half");

        Assert.Empty(events);
    }

    [Fact]
    public async Task AStreamThatSendsNothing_TimesOut()
    {
        var pipe = new Pipe();

        var timedOut = await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await foreach (var _ in SseReader.ReadAsync(pipe.Reader.AsStream(), TimeSpan.FromMilliseconds(200), default))
            {
            }
        });

        Assert.Contains("sent nothing", timedOut.Message);
    }

    [Fact]
    public async Task KeepAlives_KeepItAlive()
    {
        var pipe = new Pipe();
        var reading = Task.Run(async () =>
        {
            var events = new List<SseEvent>();
            await foreach (var evt in SseReader.ReadAsync(pipe.Reader.AsStream(), TimeSpan.FromMilliseconds(300), default))
                events.Add(evt);
            return events;
        });

        for (var i = 0; i < 5; i++)
        {
            await Task.Delay(150);
            await pipe.Writer.WriteAsync(": keep-alive\n\n"u8.ToArray());
        }

        await pipe.Writer.WriteAsync("data: done\n\n"u8.ToArray());
        await pipe.Writer.CompleteAsync();

        Assert.Equal([new SseEvent(null, "message", "done")], await reading.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Cancelling_IsNotATimeout()
    {
        var pipe = new Pipe();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in SseReader.ReadAsync(pipe.Reader.AsStream(), TimeSpan.FromSeconds(30), cancel.Token))
            {
            }
        });
    }
}
