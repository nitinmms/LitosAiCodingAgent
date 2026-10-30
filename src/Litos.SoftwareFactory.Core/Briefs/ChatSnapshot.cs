using System.Globalization;
using System.Text;
using System.Text.Json;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Orchestration;
using Litos.SoftwareFactory.Core.Store;

namespace Litos.SoftwareFactory.Core.Briefs;

/// <summary>One tool call of a work turn, as the host saw it stream past.</summary>
public sealed record ToolActivity(string Description, DateTimeOffset At);

/// <summary>What a task's run is doing at this moment, from the host's memory: only a run with
/// a turn in progress has any.</summary>
public sealed record RunActivity(string Turn, DateTimeOffset TurnStartedAt, int ModelCalls, long TokensThisTurn, IReadOnlyList<ToolActivity> RecentTools);

/// <summary>
/// Where a task stands, written out for a chat answer (m2-architecture.md §5.3): the state and why,
/// the run and what it is doing now, the budget, decisions, evidence, the factory's own notes, and
/// what the person can do next. Built only from records the host already holds, so a question
/// about the task's state costs no extra call; every list is capped.
/// </summary>
public static class ChatSnapshot
{
    public const int AnsweredDecisions = 3;
    public const int OpenFindings = 8;
    public const int Notes = 8;
    public const int Limitations = 5;

    public static string Describe(ThreadDetails details, RunActivity? activity, DateTimeOffset now)
    {
        var thread = details.Thread;
        var text = new StringBuilder();

        text.Append(CultureInfo.InvariantCulture, $"**State:** {StateName(thread.State)}");
        if (thread.StateReason is { Length: > 0 } reason)
            text.Append(CultureInfo.InvariantCulture, $" ({OneLine(reason)})");
        text.Append(CultureInfo.InvariantCulture, $". Stage: {thread.Stage}.\n{Meaning(thread.State)}\n");

        if (details.LatestRun is { } run)
        {
            text.Append(CultureInfo.InvariantCulture, $"\n**Latest run:** {run.Kind}, {run.Status}");
            if (run.StopReason is { } stop)
                text.Append(CultureInfo.InvariantCulture, $", stopped: {stop}");
            text.Append(".\n");
        }

        if (activity is not null)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"\n**Right now:** the {activity.Turn} turn, started {Ago(activity.TurnStartedAt, now)}; {activity.ModelCalls} model call(s) and {activity.TokensThisTurn:N0} tokens so far in this turn.\n");
            if (activity.RecentTools.Count > 0)
            {
                text.Append("Its latest tool calls, newest last:\n");
                foreach (var tool in activity.RecentTools)
                    text.Append(CultureInfo.InvariantCulture, $"- {OneLine(tool.Description)} ({Ago(tool.At, now)})\n");
            }
        }

        text.Append('\n').Append(Budget(thread)).Append('\n');
        Decisions(details.Decisions, text);

        if (details.LatestSpec is { } spec)
            text.Append(CultureInfo.InvariantCulture, $"\n**Specification:** revision {spec.Revision}, {(spec.ApprovedAt is null ? "waiting for approval" : "approved")}.\n");

        if (details.LatestVerification is { } verification)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"\n**Latest verification** ({verification.Kind}): build {verification.Build}, unit tests {verification.UnitTests} ({verification.PassedCount} passed, {verification.FailedCount} failed, {verification.SkippedCount} skipped)");
            if (verification.ChangedLineCoveragePercent is { } coverage)
                text.Append(CultureInfo.InvariantCulture, $", changed-line coverage {coverage:0.#}%");
            text.Append(".\n");
        }

        Findings(details, text);
        Handoff(details.LatestHandoff, text);
        Steers(details.Messages, text);
        FactoryNotes(details.Messages, text);

        text.Append('\n').Append(Reading(details)).Append('\n');
        text.Append(CultureInfo.InvariantCulture, $"\n**What the person can do now:** {NextSteps(thread.State)}\n");
        return text.ToString().TrimEnd();
    }

    private static string StateName(LifecycleState state) => state switch
    {
        LifecycleState.AwaitingDecision => "awaiting a decision",
        LifecycleState.PausedBudget => "paused for budget",
        LifecycleState.PausedUser => "paused",
        LifecycleState.AwaitingHumanTesting => "handed off, awaiting human testing",
        _ => state.ToString().ToLowerInvariant(),
    };

    /// <summary>What the state means, in a sentence the answer can lean on.</summary>
    private static string Meaning(LifecycleState state) => state switch
    {
        LifecycleState.Draft => "Nothing has been delegated yet; no work has run.",
        LifecycleState.Queued => "The work is waiting to start or continue; the reason above says what it waits for.",
        LifecycleState.Running => "An agent is working on the task now.",
        LifecycleState.AwaitingDecision => "The run stopped to ask the person a question; it continues once the decision is answered.",
        LifecycleState.PausedBudget => "The run stopped because the task's token budget is used up; a person must raise the budget and resume it.",
        LifecycleState.PausedUser => "A person paused the run; it continues from where it stopped when resumed.",
        LifecycleState.Blocked => "The run stopped on a problem it could not get past; the reason above says what. A person resumes it once that is fixed.",
        LifecycleState.AwaitingHumanTesting => "The work was handed off for the person to test; nothing runs until they accept it or request changes.",
        LifecycleState.Accepted => "The person accepted the work; the task is finished.",
        LifecycleState.Interrupted => "The factory host stopped while the run was working; a person recovers it to continue.",
        LifecycleState.Cancelled => "The task was cancelled; nothing more runs on it.",
        _ => "",
    };

    private static string NextSteps(LifecycleState state) => state switch
    {
        LifecycleState.Draft => "start the work with `@factory <what to do>`, or ask for a specification first with `@factory spec <what for>`.",
        LifecycleState.Queued or LifecycleState.Running => "guide the agent with `@factory <guidance>` (it reaches the agent at its next safe point), or Pause or Cancel the task.",
        LifecycleState.AwaitingDecision => "answer the open decision on its card in the thread.",
        LifecycleState.PausedBudget => "raise the task's budget, then Resume.",
        LifecycleState.PausedUser => "Resume the task, or Cancel it.",
        LifecycleState.Blocked => "fix what blocked it, then Resume.",
        LifecycleState.AwaitingHumanTesting => "test the branch, then Accept, or use Request changes on the handoff card. An `@factory` message does not hand the task back.",
        LifecycleState.Accepted or LifecycleState.Cancelled => "start a new thread for further work.",
        LifecycleState.Interrupted => "Recover the task to continue it.",
        _ => "nothing.",
    };

    private static string Budget(TaskThread thread)
    {
        if (thread.BudgetCap is not { } cap)
            return string.Create(CultureInfo.InvariantCulture, $"**Budget:** {thread.TokensUsed:N0} tokens used, {thread.TokensReserved:N0} reserved for calls in flight, no cap.");
        var left = Math.Max(0, cap - thread.TokensUsed - thread.TokensReserved);
        return string.Create(CultureInfo.InvariantCulture,
            $"**Budget:** {thread.TokensUsed:N0} of {cap:N0} tokens used, {thread.TokensReserved:N0} reserved for calls in flight, {left:N0} left.");
    }

    private static void Decisions(IReadOnlyList<Decision> decisions, StringBuilder text)
    {
        foreach (var open in decisions.Where(d => d.Status == DecisionStatus.Open))
        {
            text.Append(CultureInfo.InvariantCulture, $"\n**Open decision:** {OneLine(open.Question)}\nWhy it blocks: {OneLine(open.WhyItBlocks)}\n");
            var options = Options(open.OptionsJson);
            if (options.Count > 0)
                text.Append("Options: ").Append(string.Join("; ", options.Select(OneLine))).Append('\n');
            if (open.Recommendation is { Length: > 0 } recommendation)
                text.Append(CultureInfo.InvariantCulture, $"Recommended: {OneLine(recommendation)}\n");
        }

        var answered = decisions.Where(d => d.Status != DecisionStatus.Open && d.Answer is not null).TakeLast(AnsweredDecisions).ToList();
        if (answered.Count > 0)
        {
            text.Append("\n**Decisions answered:**\n");
            foreach (var decision in answered)
                text.Append(CultureInfo.InvariantCulture, $"- {OneLine(decision.Question)} → {OneLine(decision.Answer!)}\n");
        }
    }

    private static IReadOnlyList<string> Options(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void Findings(ThreadDetails details, StringBuilder text)
    {
        if (details.Findings.Count == 0 && details.EarlierFindings.Count == 0)
            return;

        var blocking = details.Findings.Count(f => f.Severity == FindingSeverity.Blocking);
        text.Append(CultureInfo.InvariantCulture,
            $"\n**Review findings** (latest run): {blocking} blocking, {details.Findings.Count - blocking} minor; {details.Findings.Count(f => f.Status == FindingStatus.Open)} open.");
        if (details.EarlierFindings.Count > 0)
            text.Append(CultureInfo.InvariantCulture, $" Earlier runs had {details.EarlierFindings.Count} more.");
        text.Append('\n');

        foreach (var finding in details.Findings
            .Where(f => f.Status == FindingStatus.Open)
            .OrderBy(f => f.Severity == FindingSeverity.Blocking ? 0 : 1)
            .Take(OpenFindings))
        {
            var where = finding.Line is { } line ? $"{finding.File}:{line}" : finding.File;
            text.Append(CultureInfo.InvariantCulture, $"- {finding.Severity} at `{where}`: {OneLine(finding.Text)}\n");
        }
    }

    private static void Handoff(HandoffRecord? handoff, StringBuilder text)
    {
        if (handoff is null)
            return;

        text.Append(CultureInfo.InvariantCulture, $"\n**Last handoff:** branch `{handoff.Branch}`");
        if (handoff.CommitSha is { Length: > 0 } sha)
            text.Append(CultureInfo.InvariantCulture, $", commit {sha[..Math.Min(7, sha.Length)]}");
        if (handoff.PullRequestNumber is { } pr)
            text.Append(CultureInfo.InvariantCulture, $", draft PR #{pr}");
        text.Append(".\n");

        HandoffEvidence? evidence;
        try
        {
            evidence = JsonSerializer.Deserialize<HandoffEvidence>(handoff.EvidenceJson, FactoryWire.Json);
        }
        catch (JsonException)
        {
            return;
        }

        // Evidence written by an older host may lack fields its record now declares non-null.
        if (evidence?.Build is null)
            return;
        if (evidence.Summary is { Length: > 0 } summary)
            text.Append(CultureInfo.InvariantCulture, $"Summary: {OneLine(summary)}\n");
        text.Append(CultureInfo.InvariantCulture, $"Evidence: build {evidence.Build}, unit tests {evidence.UnitTests} ({evidence.TestsPassed} passed, {evidence.TestsFailed} failed), review {evidence.Review}.\n");
        if (evidence.KnownLimitations is { Count: > 0 } known)
        {
            text.Append("Known limitations:\n");
            foreach (var limitation in known.Take(Limitations))
                text.Append(CultureInfo.InvariantCulture, $"- {OneLine(limitation)}\n");
        }
    }

    private static void Steers(IReadOnlyList<ThreadMessage> messages, StringBuilder text)
    {
        var held = messages.Count(m => SteerPayload.Read(m.PayloadJson) == SteerStatus.Held);
        if (held > 0)
            text.Append(CultureInfo.InvariantCulture, $"\n**Waiting for the agent:** {held} @factory message(s), which go to it when its next work turn starts.\n");
    }

    /// <summary>The factory's own progress notes; a chat's conversation leaves them out.</summary>
    private static void FactoryNotes(IReadOnlyList<ThreadMessage> messages, StringBuilder text)
    {
        var notes = messages.Where(m => m.Kind == MessageKind.Status).TakeLast(Notes).ToList();
        if (notes.Count == 0)
            return;

        text.Append("\n**Recent factory notes**, oldest first:\n");
        foreach (var note in notes)
            text.Append(CultureInfo.InvariantCulture, $"- {OneLine(note.Text)}\n");
    }

    /// <summary>
    /// What the reading copy can show. Work is pushed only at a handoff, so the edits of a run
    /// that has not handed off are in its own working copy, out of the chat's reach.
    /// </summary>
    private static string Reading(ThreadDetails details)
    {
        var run = details.LatestRun;
        var unseen = run is { Kind: RunKind.Implement or RunKind.Rework }
            && run.StopReason != StopReason.Withdrawn
            && details.LatestHandoff?.RunId != run.Id;
        if (!unseen)
            return details.LatestHandoff is null
                ? "**The code you can read:** the default branch. No work has been handed off yet."
                : "**The code you can read:** the task branch as last handed off.";

        return details.LatestHandoff is null
            ? "**The code you can read** does not include this task's work: the run's edits are in its own working copy and have not been pushed. Do not say something is missing from the code when the run may have added it; say what you cannot see."
            : "**The code you can read** is the task branch as last handed off. The current run's edits since then are in its own working copy and have not been pushed; say what you cannot see rather than calling something missing.";
    }

    private static string Ago(DateTimeOffset at, DateTimeOffset now)
    {
        var elapsed = now - at;
        if (elapsed < TimeSpan.FromMinutes(1))
            return $"{Math.Max(0, (int)elapsed.TotalSeconds)} s ago";
        if (elapsed < TimeSpan.FromHours(1))
            return $"{(int)elapsed.TotalMinutes} min ago";
        return string.Create(CultureInfo.InvariantCulture, $"{elapsed.TotalHours:0.#} h ago");
    }

    /// <summary>Shorter than a brief's bullets: a snapshot has many lines, each one fact.</summary>
    private static string OneLine(string text) => BriefComposer.OneLine(text, MaxLine);

    /// <summary>The most characters a line of the snapshot quotes from a message, finding or decision.</summary>
    public const int MaxLine = 300;
}
