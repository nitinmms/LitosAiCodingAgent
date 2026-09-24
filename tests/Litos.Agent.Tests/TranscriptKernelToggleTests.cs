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

    /// <summary>
    /// The exact shape that shipped broken: PTC toggled ON before the session's first message, so
    /// the ONLY entry on disk is the kernel_toggle and no SessionHeader exists yet. Every other
    /// test in this file writes a SessionHeader first, which is precisely why none of them caught
    /// it — the toggle replayed fine, and the caller then threw the transcript away.
    /// </summary>
    [Fact]
    public async Task LoadAsync_ToggleWrittenBeforeAnySessionHeader_StillReplaysAsOn()
    {
        var store = new FakeTranscriptStore();
        await store.AppendAsync(Owner, "s1", TranscriptEntry.KernelToggle(true), CancellationToken.None);

        var transcript = await Transcript.LoadAsync(store, Owner, "s1", CancellationToken.None);

        Assert.True(transcript.KernelModeEnabled);
        Assert.Null(transcript.WorkingDirectory);
    }

    /// <summary>
    /// Pins the actual fix. A turn starting on such a session has to supply the missing working
    /// directory, and the way it does that must not cost the toggle: replacing the transcript with
    /// Transcript.CreateNew(cwd) — what AgentWorker did — resets KernelModeEnabled to its false
    /// default, so PTC read as OFF on the very turn the user had just enabled it for. Observed live:
    /// a session whose kernel_toggle was written 42s before the first turn ran the full tool
    /// registry (shell/edit_file/write_file) and never once called run_kernel_code.
    /// </summary>
    [Fact]
    public async Task SetWorkingDirectory_FillsTheGapWithoutDiscardingTheToggle()
    {
        var store = new FakeTranscriptStore();
        await store.AppendAsync(Owner, "s1", TranscriptEntry.KernelToggle(true), CancellationToken.None);
        var transcript = await Transcript.LoadAsync(store, Owner, "s1", CancellationToken.None);

        transcript.SetWorkingDirectory("/repo");

        Assert.Equal("/repo", transcript.WorkingDirectory);
        Assert.True(transcript.KernelModeEnabled, "filling in a missing working directory must not reset the kernel toggle");
    }
}
