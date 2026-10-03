using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Orchestration;

namespace Litos.SoftwareFactory.Core.Tests.Orchestration;

public sealed class DecisionSignalsTests
{
    /// <summary>What agents actually wrote, or would, when they decided a breaking change alone.
    /// The first two are F6's limitations, word for word.</summary>
    [Theory]
    [InlineData("On-disk format version bumped from 1 to 2; files written by version 1 (pre-TTL) are rejected with InvalidDatabaseFileException because a put record gained an 8-byte expiry field.")]
    [InlineData("On-disk format version bumped 1 -> 2; version 1 files are rejected with InvalidDatabaseFileException because a put record gained an 8-byte expiry field.")]
    [InlineData("Version 1 files cannot be read by this version.")]
    [InlineData("Existing databases will no longer open.")]
    [InlineData("Drafts saved before this change are not loaded.")]
    [InlineData("Older drafts are discarded when the app starts.")]
    [InlineData("This is a breaking change for callers of Export().")]
    [InlineData("The new format is not backward compatible.")]
    [InlineData("Existing files must be migrated by hand.")]
    [InlineData("Upgrading may cause data loss for v1 users.")]
    // F6's second run, word for word: it kept version 1 readable but let older readers truncate.
    [InlineData("The on-disk format stays version 1 and adds operation kind 4. Files written by this version are not readable by earlier library versions (an older reader treats the first expiry frame as a corrupted tail and truncates from there); existing version-1 files written without TTL still load unchanged.")]
    [InlineData("Files written by this version are not readable by earlier library versions.")]
    [InlineData("An older reader truncates the file at the first new record.")]
    [InlineData("The new records are not compatible with previous versions of the library.")]
    public void ABreakForWhatExists_IsRecognised(string limitation) => Assert.True(DecisionSignals.IsBreaking(limitation), limitation);

    /// <summary>Ordinary limitations of the new work, which must not cost a refused submission.</summary>
    [Theory]
    [InlineData("Large exports are not streamed.")]
    [InlineData("Negative TTLs are rejected with ArgumentOutOfRangeException.")]
    [InlineData("Offline mode is not supported.")]
    [InlineData("Expiry is checked against DateTime.UtcNow; expired means now >= expiry.")]
    [InlineData("Not tried on Safari.")]
    [InlineData("The UI test is not run in CI.")]
    [InlineData("Manual testing only for the Excel import.")]
    [InlineData("Compaction is not triggered automatically.")]
    [InlineData("A torn final write is truncated on open, as before.")]
    [InlineData("The export file is not readable while it is being written.")]
    public void AnOrdinaryLimitation_IsNot(string limitation) => Assert.False(DecisionSignals.IsBreaking(limitation), limitation);

    [Fact]
    public void BreakingLimitation_ReturnsTheFirstOneThatBreaks()
    {
        var work = new WorkSubmission("Added expiry.", [], [], ["Large exports are not streamed.", "Version 1 files cannot be read.", "Existing databases will no longer open."], []);

        Assert.Equal("Version 1 files cannot be read.", DecisionSignals.BreakingLimitation(work));
        Assert.Null(DecisionSignals.BreakingLimitation(work with { KnownLimitations = ["Large exports are not streamed."] }));
    }

    [Fact]
    public void TheRefusal_QuotesTheLimitation_AndOffersBothWaysOut()
    {
        var refusal = DecisionSignals.Refusal("Version 1 files cannot be read.");

        Assert.Contains("\"Version 1 files cannot be read.\"", refusal);
        Assert.Contains("make the change keep it working", refusal);
        Assert.Contains("call `request_decision`", refusal);
        Assert.Contains("call `submit_work` again unchanged and it will be recorded", refusal);
    }
}
