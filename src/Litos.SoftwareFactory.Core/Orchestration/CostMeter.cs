using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Litos.Agent.Messages;

namespace Litos.SoftwareFactory.Core.Orchestration;

/// <summary>What one kind of tool output adds to a conversation, in tokens (characters ÷ 4).</summary>
public sealed record ContextShare(string Name, long Tokens);

/// <summary>When the last cost note was sent in a turn, and how many context thresholds it has reported.</summary>
public sealed record CostMeterState(int LastNoteAtCall = 0, int ThresholdsReported = 0);

/// <summary>
/// A live cost note for an implementation turn. Every model call re-sends the whole conversation,
/// so a long turn's calls cost more and more: on R3 the context grew from 6,000 to 115,000 tokens
/// over 69 calls, and 61% of the implementation was that context re-read. Brief instructions to
/// read less and batch more (prompt revision m1.9) did not change behaviour; the agent had never
/// been shown what its own calls cost. At set points the turn is told, with its real figures,
/// what a call now costs and what is filling the context.
///
/// The note is appended to the conversation like any steer, so nothing earlier changes and the
/// provider's prompt cache is unaffected.
/// </summary>
public static partial class CostMeter
{
    /// <summary>
    /// Whether a note is due after this call, and the state after it. A note is due when the
    /// context first passes each of <see cref="RunLimits.CostNoteContextThresholds"/>, or after
    /// <see cref="RunLimits.CostNoteEveryCalls"/> calls since the last one; never within
    /// <see cref="RunLimits.CostNoteMinGapCalls"/> calls of the last.
    /// </summary>
    public static bool IsDue(int turnCalls, long contextTokens, CostMeterState state, RunLimits limits, out CostMeterState next)
    {
        next = state;
        if (!limits.CostNotes)
            return false;
        if (state.LastNoteAtCall > 0 && turnCalls - state.LastNoteAtCall < limits.CostNoteMinGapCalls)
            return false;

        var crossed = limits.CostNoteContextThresholds.Count(t => contextTokens >= t);
        if (crossed > state.ThresholdsReported || turnCalls - state.LastNoteAtCall >= limits.CostNoteEveryCalls)
        {
            next = new CostMeterState(turnCalls, Math.Max(crossed, state.ThresholdsReported));
            return true;
        }

        return false;
    }

    /// <summary>What the tool output in the conversation is, largest first: whole files printed
    /// from kernel code, test runs, other commands, the file tools' results, and the rest.</summary>
    public static IReadOnlyList<ContextShare> Composition(IReadOnlyList<ChatMessage> messages)
    {
        var kindOf = new Dictionary<string, string>();
        foreach (var block in messages.SelectMany(m => m.Content).OfType<ToolUseBlock>())
            kindOf[block.CallId] = Classify(block);

        var totals = new Dictionary<string, long>();
        foreach (var result in messages.SelectMany(m => m.Content).OfType<ToolResultBlock>())
        {
            var kind = kindOf.GetValueOrDefault(result.CallId, Other);
            totals[kind] = totals.GetValueOrDefault(kind) + result.Text.Length / 4;
        }

        return [.. totals.Select(t => new ContextShare(t.Key, t.Value)).OrderByDescending(s => s.Tokens)];
    }

    public const string WholeFiles = "whole files printed from kernel code";
    public const string TestRuns = "test-run output";
    public const string Commands = "other command output";
    public const string FileTools = "read_file and search results";
    public const string Other = "other tool output";

    internal static string Classify(ToolUseBlock use)
    {
        var text = use.ToolName;
        if (use.Arguments.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in use.Arguments.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                    text += "\n" + property.Value.GetString();
            }
        }

        if (WholeFileRead().IsMatch(text))
            return WholeFiles;
        if (TestCommand().IsMatch(text))
            return TestRuns;
        if (use.ToolName == "shell" || ShellCall().IsMatch(text))
            return Commands;
        if (use.ToolName is "read_file" or "search_code" or "list_directory" || FileToolCall().IsMatch(text))
            return FileTools;
        return Other;
    }

    /// <summary>The note sent to the turn: its real figures, then what to do differently.</summary>
    public static string Note(int turnCalls, long contextTokens, long lastCallCharged, IReadOnlyList<ContextShare> composition)
    {
        var shares = composition.Where(s => s.Tokens >= 1_000).Take(3)
            .Select(s => string.Create(CultureInfo.InvariantCulture, $"{s.Name}, about {s.Tokens:N0} tokens"))
            .ToList();
        var filling = shares.Count > 0 ? $" What is filling it: {string.Join("; ", shares)}." : "";

        return FormattableString.Invariant($"Cost note from the factory. This is information, not a request to stop. This turn has made {turnCalls} model calls. ")
            + FormattableString.Invariant($"Every call re-sends the whole conversation, now about {contextTokens:N0} tokens, so the last call cost about {lastCallCharged:N0} tokens ")
            + $"even with the cache, and each call costs more than the one before.{filling} "
            + "Everything printed stays in the conversation and is paid for again on every later call. To spend less from here: "
            + "read only the lines you need (read_file with a line range, or print a few lines, not whole files), "
            + "and put your next edits and the test run in one script instead of one small step per call. Carry on with the task.";
    }

    /// <summary>The shorter line the thread shows when a note is sent.</summary>
    public static string ThreadLine(int turnCalls, long contextTokens, IReadOnlyList<ContextShare> composition)
    {
        var top = composition.FirstOrDefault(s => s.Tokens >= 1_000);
        return FormattableString.Invariant($"Cost note sent to the agent after {turnCalls} model calls: its context is about {contextTokens:N0} tokens")
            + (top is null ? "." : FormattableString.Invariant($", the largest part {top.Name} (about {top.Tokens:N0})."));
    }

    [GeneratedRegex(@"\bFile\.(ReadAllText|ReadAllLines|ReadLines|OpenText|ReadAllBytes)\b|\bnew\s+StreamReader\(")]
    private static partial Regex WholeFileRead();

    [GeneratedRegex(@"\b(dotnet(\.exe)?\s+test|vitest|jest|mocha|(npm|pnpm|yarn|bun)(\s+run)?\s+test|pytest|go\s+test|cargo\s+test|mvn\b[^\n]*\btest|gradlew?\b[^\n]*\btest)\b", RegexOptions.IgnoreCase)]
    private static partial Regex TestCommand();

    [GeneratedRegex(@"\bshell\s*\(")]
    private static partial Regex ShellCall();

    [GeneratedRegex(@"\b(read_file|search_code|list_directory)\s*\(")]
    private static partial Regex FileToolCall();
}
