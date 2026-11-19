using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Litos.Agent.Messages;

namespace Litos.SoftwareFactory.Core.Orchestration;

/// <summary>
/// Whether a work turn is still changing the working copy (Probe.Web README §11.3 finding 2). On
/// the W2.7 check a Make-testable turn made 29 calls and a feature turn 28 without writing a
/// file to the repository, building throw-away projects in their scratch folders instead, until
/// the task's budget ran out. The host only learnt that the turn changed nothing once it ended,
/// and it never ended. The guard counts, from the conversation the gateway already holds, the
/// model calls since the last one that wrote to the working copy: past one limit the turn is told
/// to make the change or ask, past another it is stopped.
/// </summary>
public static partial class ProgressGuard
{
    /// <summary>What the guard asks of a turn after the call just made.</summary>
    public enum Verdict
    {
        Continue,
        Note,
        Stop,
    }

    /// <summary>
    /// The model calls (assistant messages with at least one tool call) since the last one that
    /// changed the working copy, counting the reply just received. Calls before the conversation's
    /// first one are not seen, so a compacted conversation counts from its summary: the guard can
    /// only be late, never early.
    /// </summary>
    public static int CallsSinceLastWrite(IEnumerable<ChatMessage> conversation)
    {
        var since = 0;
        foreach (var message in conversation)
        {
            if (message.Role != Role.Assistant)
                continue;
            var uses = message.Content.OfType<ToolUseBlock>().ToList();
            if (uses.Count == 0)
                continue;
            since = uses.Any(Writes) ? 0 : since + 1;
        }

        return since;
    }

    /// <summary>What to do now, given the calls since the last write and whether the note was already sent this turn.</summary>
    public static Verdict Judge(int callsSinceWrite, bool noteSent, RunLimits limits)
    {
        if (!limits.RunGuards)
            return Verdict.Continue;
        if (callsSinceWrite >= limits.ProgressStopAfterCalls)
            return Verdict.Stop;
        if (callsSinceWrite >= limits.ProgressNoteAfterCalls && !noteSent)
            return Verdict.Note;
        return Verdict.Continue;
    }

    /// <summary>
    /// Whether a tool call changes the working copy: the file tools, and kernel code that calls
    /// them or writes files itself. A write into the turn's scratch folder is not a change to the
    /// repository: that is where the stalled turns built their experiments.
    /// </summary>
    internal static bool Writes(ToolUseBlock use)
    {
        if (use.ToolName is "write_file" or "edit_file")
            return !MentionsScratch(Arguments(use));

        foreach (var line in Arguments(use).Split('\n'))
        {
            if (MentionsScratch(line))
                continue;
            if (FileToolCall().IsMatch(line) || DirectFileWrite().IsMatch(line) || GitChange().IsMatch(line))
                return true;
        }

        return false;
    }

    private static string Arguments(ToolUseBlock use)
    {
        if (use.Arguments.ValueKind != JsonValueKind.Object)
            return "";
        var text = "";
        foreach (var property in use.Arguments.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
                text += "\n" + property.Value.GetString();
        }

        return text;
    }

    private static bool MentionsScratch(string text) => text.Contains("SCRATCH_DIR", StringComparison.Ordinal);

    /// <summary>What the turn is told the first time it passes the note limit.</summary>
    public static string Note(int callsSinceWrite, RunLimits limits) =>
        FormattableString.Invariant($"Progress note from the factory. This turn has made {callsSinceWrite} model calls without changing a file in the working copy ")
        + "(writes to your scratch folder do not count). Stop exploring now. Make the change the task asks for in the repository, "
        + "then run the tests. If you cannot make it because something is unclear or missing, call request_decision with the question; "
        + FormattableString.Invariant($"if you believe the work is already done, call submit_work. After {limits.ProgressStopAfterCalls} calls without a change the factory stops this turn.");

    /// <summary>Why the turn was stopped, as the thread and the resume brief say it.</summary>
    public static string StopReason(int callsSinceWrite) =>
        FormattableString.Invariant($"The turn made {callsSinceWrite} model calls without changing a file in the working copy, so the factory stopped it. ")
        + "Resume to continue, or send a message with what it should do.";

    /// <summary>The thread's line when the note is sent.</summary>
    public static string ThreadLine(int callsSinceWrite) =>
        FormattableString.Invariant($"Progress note sent to the agent: {callsSinceWrite} model calls without changing a file in the working copy.");

    [GeneratedRegex(@"\b(write_file|edit_file)\s*\(")]
    private static partial Regex FileToolCall();

    [GeneratedRegex(@"\bFile\.(WriteAllText|WriteAllLines|WriteAllBytes|AppendAllText|AppendAllLines|Delete|Move|Copy)(Async)?\s*\(|\bDirectory\.(Delete|Move)\s*\(")]
    private static partial Regex DirectFileWrite();

    [GeneratedRegex(@"\bgit\s+(rm|mv|apply|restore|checkout\s+--)\b")]
    private static partial Regex GitChange();
}

/// <summary>
/// A work turn told, once, that its task's budget is nearly spent (Probe.Web README §11.3 finding
/// 11). On the W2.7 check a turn that had already fixed what it was asked kept exploring until
/// the budget ran out, so nothing was committed and a person had to raise the budget to see a
/// change that was ready. The note asks it to finish with what it has.
/// </summary>
public static class BudgetNote
{
    /// <summary>
    /// About how many more calls the task's budget has room for at the cost of the last one, or
    /// null when the task has no cap or the call cost nothing.
    /// </summary>
    public static long? CallsLeft(long? taskRemainingAfter, long lastCallCharged) =>
        taskRemainingAfter is { } remaining && lastCallCharged > 0 ? Math.Max(0, remaining) / lastCallCharged : null;

    /// <summary>Whether the note is due: guards on, not yet sent this turn, and few calls left.</summary>
    public static bool IsDue(long? callsLeft, bool sent, RunLimits limits) =>
        limits.RunGuards && !sent && callsLeft is { } left && left <= limits.BudgetLowCallsLeft;

    public static string Note(long remaining, long callsLeft) =>
        string.Create(CultureInfo.InvariantCulture, $"Budget note from the factory. The task's budget has about {remaining:N0} tokens left, room for about {callsLeft} more calls at the cost of the last one. ")
        + "Finish now with what you have: complete the edit you are on, run the tests and any check the brief names once, in one script, "
        + "and call submit_work, saying in it what is not done. Do not start a new investigation. A run that stops for budget before it submits leaves nothing for the tester to see.";

    public static string ThreadLine(long remaining, long callsLeft) =>
        string.Create(CultureInfo.InvariantCulture, $"Budget note sent to the agent: about {remaining:N0} tokens left, room for about {callsLeft} more calls; it was asked to finish and submit.");
}
