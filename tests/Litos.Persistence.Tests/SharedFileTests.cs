using Litos.Agent.Messages;
using Litos.Agent.Session;

namespace Litos.Persistence.Tests;

/// <summary>
/// A factory review failed with "the process cannot access the file ... transcript.jsonl because
/// it is being used by another process": another handle held the file for a moment, and one failed
/// append failed the turn. Sharing violations are a Windows behaviour, so the tests that provoke
/// one run only there.
/// </summary>
[Collection(nameof(SharedFileTests))] // they change the shared retry delays
public sealed class SharedFileTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("litos-shared-file-").FullName;
    private readonly TimeSpan[] _delays = SharedFile.RetryDelays;

    public void Dispose()
    {
        SharedFile.RetryDelays = _delays;
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string File(string name = "transcript.jsonl") => Path.Combine(_root, name);

    [Fact]
    public async Task AnAppendWaitsOut_AFileHeldExclusivelyForAMoment()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var path = File();
        await System.IO.File.WriteAllTextAsync(path, "first\n");

        var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        var released = Task.Run(async () =>
        {
            await Task.Delay(120);
            await holder.DisposeAsync();
        });

        await SharedFile.AppendAsync(path, "second\n", CancellationToken.None);
        await released;

        Assert.Equal(["first", "second"], await SharedFile.ReadAllLinesAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task AFileHeldLongerThanTheRetries_StillFails()
    {
        if (!OperatingSystem.IsWindows())
            return;
        SharedFile.RetryDelays = [TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(5)];
        var path = File();
        await System.IO.File.WriteAllTextAsync(path, "first\n");

        using var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        var ex = await Assert.ThrowsAsync<IOException>(() => SharedFile.AppendAsync(path, "second\n", CancellationToken.None));
        Assert.True(SharedFile.IsSharingViolation(ex));
    }

    [Fact]
    public async Task AReaderPartWayThrough_DoesNotBlockAnAppend()
    {
        var path = File();
        await System.IO.File.WriteAllLinesAsync(path, ["one", "two", "three"]);

        var seen = new List<string>();
        await foreach (var line in SharedFile.ReadLinesAsync(path, CancellationToken.None))
        {
            seen.Add(line);
            if (seen.Count == 1)
                await SharedFile.AppendAsync(path, "four\n", CancellationToken.None);
        }

        Assert.Equal(["one", "two", "three"], seen[..3]);
        Assert.Equal(["one", "two", "three", "four"], SharedFile.ReadAllLines(path));
    }

    [Fact]
    public async Task TheTranscriptStore_AppendsWhileAnotherHandleReadsTheFile()
    {
        var store = new JsonlTranscriptStore(_root);
        await store.AppendAsync(SessionOwner.Local, "s", TranscriptEntry.FromMessage(ChatMessage.User("hello")), CancellationToken.None);
        var path = Directory.EnumerateFiles(_root, "transcript.jsonl", SearchOption.AllDirectories).Single();

        // A reader that allows others to write, as a scanner or an editor might hold it.
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            await store.AppendAsync(SessionOwner.Local, "s", TranscriptEntry.FromMessage(ChatMessage.User("again")), CancellationToken.None);

        var entries = new List<TranscriptEntry>();
        await foreach (var entry in store.ReadAsync(SessionOwner.Local, "s", CancellationToken.None))
            entries.Add(entry);
        Assert.Equal(2, entries.Count);
    }
}
