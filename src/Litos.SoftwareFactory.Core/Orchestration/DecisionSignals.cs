using System.Text.RegularExpressions;
using Litos.SoftwareFactory.Contracts;

namespace Litos.SoftwareFactory.Core.Orchestration;

/// <summary>
/// Recognises a decision the agent made alone and reported as a limitation. On F6 (document
/// expiry) the agent changed the file format so that existing databases could no longer be read,
/// wrote that into the README, and listed it under known limitations in every one of five
/// submit_work attempts. It never called request_decision. The rework that followed cost about
/// as much as the first implementation and ran the task out of budget.
///
/// Deliberately narrow: only a limitation saying that something which exists today stops working.
/// A false match costs one model call, because the host refuses a submission for this only once
/// per turn; a miss costs a rework round.
/// </summary>
public static partial class DecisionSignals
{
    /// <summary>The first known limitation that reads as breaking something existing, or null.</summary>
    public static string? BreakingLimitation(WorkSubmission submission) =>
        submission.KnownLimitations.FirstOrDefault(IsBreaking);

    /// <summary>
    /// Either an unambiguous phrase ("no longer", "breaking change", "not backward compatible"),
    /// or something stopping working ("are rejected", "cannot be read") said of something that
    /// exists ("version 1 files", "existing databases"). The second needs both halves: "negative
    /// TTLs are rejected" and "offline mode is not supported" describe the new work, not a break.
    /// </summary>
    public static bool IsBreaking(string limitation) =>
        Unambiguous().IsMatch(limitation) || ((StopsWorking().IsMatch(limitation) || Damages().IsMatch(limitation)) && Existing().IsMatch(limitation));

    /// <summary>What the agent is told when its submission is refused for this.</summary>
    public static string Refusal(string limitation) =>
        $"A known limitation says the change stops something that exists today from working: \"{limitation}\" "
        + "That is a choice for a person, not a limitation to report. Either make the change keep it working, or call "
        + "`request_decision` with the options (for example: keep supporting what exists, convert it, or drop it), your "
        + "recommendation and what each affects, and stop. If you are sure nothing existing stops working, call `submit_work` "
        + "again unchanged and it will be recorded.";

    [GeneratedRegex(
        @"\bno longer\b|\bbreaking change\b|\bbreaks? (existing|older|old|current|previous)\b|\bdata loss\b"
        + @"|\bnot backwards?[- ]compatib|\bbackwards?[- ]incompatib|\bincompatible with (existing|older|old|previous|earlier|current)\b"
        + @"|\bmust be (migrated|recreated|regenerated|rebuilt)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex Unambiguous();

    [GeneratedRegex(
        @"\b(cannot|can't|will not|won't|are not|is not|aren't|isn't|are|is|will be|get|gets)\b[^.;]{0,40}?\b(read|opened|loaded|parsed|supported|accepted|rejected|refused|unreadable|lost|discarded|ignored|readable|loadable|openable|usable|compatible)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex StopsWorking();

    // F6's second run: "an older reader treats the first expiry frame as a corrupted tail and truncates from there".
    [GeneratedRegex(@"\b(truncat\w*|corrupt\w*|overwrit\w*|silently (drop|discard|delet)\w*)", RegexOptions.IgnoreCase)]
    private static partial Regex Damages();

    [GeneratedRegex(
        @"\b(existing|older|old|previous|earlier|legacy|prior|pre-[\w-]+|version[- ]?\d+|v\d+)\b|\b(saved|written|created|stored|made) (before|earlier|previously)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex Existing();
}
