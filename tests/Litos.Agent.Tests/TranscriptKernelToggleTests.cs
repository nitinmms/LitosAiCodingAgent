using Litos.Agent.Session;
using Litos.Agent.Tests.Fakes;

namespace Litos.Agent.Tests;

/// <summary>
/// The kernel-mode toggle is per-chat-session, persisted state (ReadMe_PTCPersistentKernel.md
/// §5.3) — recorded as a "kernel_toggle" TranscriptEntry, replayed by Transcript.LoadAsync the
/// same way WorkingDirectory already is. Covers: default OFF for a brand-new session, survival
/// across /resume, and "latest flip wins" when a session's toggle changed more than once.
/// </summary>
public sealed class TranscriptKernelToggleTests
{
    private static readonly SessionOwner Owner = SessionOwner.Local;

    [Fact]
    public void CreateNew_DefaultsKernelModeToOff()
    {
        var transcript = Transcript.CreateNew("/repo");

        Assert.False(transcript.KernelModeEnabled);
    }

    [Fact]
    public void SetKernelModeEnabled_UpdatesTheInMemoryFlag()
    {
        var transcript = Transcript.CreateNew("/repo");

        transcript.SetKernelModeEnabled(true);

        Assert.True(transcript.KernelModeEnabled);
    }

    [Fact]
    public async Task LoadAsync_ReplaysAPersistedToggleFlip_SoAResumedSessionSeesItOn()
    {
        var store = new FakeTranscriptStore();
        await store.AppendAsync(Owner, "s1", TranscriptEntry.SessionHeader("/repo"), CancellationToken.None);
        await store.AppendAsync(Owner, "s1", TranscriptEntry.KernelToggle(true), CancellationToken.None);

        var transcript = await Transcript.LoadAsync(store, Owner, "s1", CancellationToken.None);

        Assert.True(transcript.KernelModeEnabled);
    }

    [Fact]
    public async Task LoadAsync_WithNoToggleEntryAtAll_DefaultsToOff()
    {
        var store = new FakeTranscriptStore();
        await store.AppendAsync(Owner, "s1", TranscriptEntry.SessionHeader("/repo"), CancellationToken.None);

        var transcript = await Transcript.LoadAsync(store, Owner, "s1", CancellationToken.None);

        Assert.False(transcript.KernelModeEnabled);
    }

    [Fact]
    public async Task LoadAsync_WithMultipleFlips_TheLatestOneWins()
    {
        var store = new FakeTranscriptStore();
        await store.AppendAsync(Owner, "s1", TranscriptEntry.SessionHeader("/repo"), CancellationToken.None);
        await store.AppendAsync(Owner, "s1", TranscriptEntry.KernelToggle(true), CancellationToken.None);
        await store.AppendAsync(Owner, "s1", TranscriptEntry.KernelToggle(false), CancellationToken.None);
        await store.AppendAsync(Owner, "s1", TranscriptEntry.KernelToggle(true), CancellationToken.None);

        var transcript = await Transcript.LoadAsync(store, Owner, "s1", CancellationToken.None);

        Assert.True(transcript.KernelModeEnabled);
    }
}
