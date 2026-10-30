using System.Text.Json;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Briefs;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Orchestration;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Core.Verification;

namespace Litos.SoftwareFactory.Core.Tests.Briefs;

public class ChatSnapshotTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private static readonly Project Project = new()
    {
        Id = Guid.NewGuid(), Name = "filedb-sharp", GitHubOwner = "o", GitHubRepository = "filedb-sharp", DefaultBranch = "main", VerificationProfileJson = "{}",
    };

    private static TaskThread Thread(LifecycleState state, string? reason = null, long? cap = 600_000) => new()
    {
        Id = Guid.NewGuid(), ProjectId = Project.Id, Title = "Per-table size limits", SessionId = "s", Provider = "anthropic", Model = "m",
        State = state, StateReason = reason, Stage = Stage.Implement, BudgetCap = cap, TokensUsed = 412_380, TokensReserved = 18_000,
    };

    private static TaskRun Run(Guid threadId, RunKind kind = RunKind.Implement, RunStatus status = RunStatus.Running, StopReason? stop = null) => new()
    {
        Id = Guid.NewGuid(), ThreadId = threadId, Kind = kind, Status = status, StopReason = stop, Request = "Add limits.", PromptRevision = "m2.10",
    };

    private static ThreadMessage Message(Guid threadId, MessageKind kind, string text, long sequence, string? payload = null) => new()
    {
        Id = Guid.NewGuid(), ThreadId = threadId, Kind = kind, Author = kind == MessageKind.Status ? MessageAuthor.Factory : MessageAuthor.User,
        Text = text, Sequence = sequence, PayloadJson = payload, CreatedAt = Now,
    };

    private static ThreadDetails Details(
        TaskThread thread, TaskRun? run = null, IReadOnlyList<ThreadMessage>? messages = null, IReadOnlyList<Decision>? decisions = null,
        HandoffRecord? handoff = null, VerificationRecord? verification = null, IReadOnlyList<ReviewFindingRecord>? findings = null) =>
        new(thread, Project, messages ?? [], decisions ?? [], run, handoff, verification, findings ?? []);

    private static string Describe(ThreadDetails details, RunActivity? activity = null) => ChatSnapshot.Describe(details, activity, Now);

    [Theory]
    [InlineData(LifecycleState.Draft, "Nothing has been delegated yet", "`@factory <what to do>`")]
    [InlineData(LifecycleState.Running, "An agent is working on the task now", "`@factory <guidance>`")]
    [InlineData(LifecycleState.AwaitingDecision, "stopped to ask the person a question", "answer the open decision on its card")]
    [InlineData(LifecycleState.PausedBudget, "token budget is used up", "raise the task's budget, then Resume")]
    [InlineData(LifecycleState.Blocked, "could not get past", "fix what blocked it, then Resume")]
    [InlineData(LifecycleState.AwaitingHumanTesting, "handed off for the person to test", "Request changes on the handoff card")]
    [InlineData(LifecycleState.Interrupted, "factory host stopped", "Recover the task")]
    [InlineData(LifecycleState.Cancelled, "cancelled", "start a new thread")]
    public void EachState_SaysWhatItMeans_AndWhatThePersonCanDo(LifecycleState state, string meaning, string next)
    {
        var text = Describe(Details(Thread(state)));

        Assert.Contains(meaning, text);
        Assert.Contains($"**What the person can do now:** ", text);
        Assert.Contains(next, text);
    }

    [Fact]
    public void TheStateReason_IsGiven_WithTheStage()
    {
        var text = Describe(Details(Thread(LifecycleState.Blocked, "Baseline build failed: restore could not reach the feed.")));

        Assert.StartsWith("**State:** blocked (Baseline build failed: restore could not reach the feed.). Stage: Implement.", text);
    }

    [Fact]
    public void TheBudget_GivesUsedReservedCapAndWhatIsLeft()
    {
        var text = Describe(Details(Thread(LifecycleState.Running)));

        Assert.Contains("**Budget:** 412,380 of 600,000 tokens used, 18,000 reserved for calls in flight, 169,620 left.", text);
    }

    [Fact]
    public void TheBudget_WithNoCap_SaysSo()
    {
        var text = Describe(Details(Thread(LifecycleState.Running, cap: null)));

        Assert.Contains("412,380 tokens used, 18,000 reserved for calls in flight, no cap.", text);
    }

    [Fact]
    public void TheLatestRun_ItsStatus_AndWhyItStopped()
    {
        var thread = Thread(LifecycleState.PausedBudget);

        var text = Describe(Details(thread, Run(thread.Id, status: RunStatus.Suspended, stop: StopReason.BudgetExhausted)));

        Assert.Contains("**Latest run:** Implement, Suspended, stopped: BudgetExhausted.", text);
    }

    [Fact]
    public void WhatTheRunIsDoingNow_WithItsLatestToolCalls()
    {
        var thread = Thread(LifecycleState.Running);
        var activity = new RunActivity("Implement", Now.AddMinutes(-4), 7, 52_000,
            [new ToolActivity("Run dotnet test", Now.AddMinutes(-2)), new ToolActivity("Read coverage/report.xml", Now.AddSeconds(-40))]);

        var text = Describe(Details(thread, Run(thread.Id)), activity);

        Assert.Contains("**Right now:** the Implement turn, started 4 min ago; 7 model call(s) and 52,000 tokens so far in this turn.", text);
        Assert.Contains("- Run dotnet test (2 min ago)\n- Read coverage/report.xml (40 s ago)", text);
    }

    [Fact]
    public void NoActivity_NoRightNowSection()
    {
        Assert.DoesNotContain("**Right now:**", Describe(Details(Thread(LifecycleState.Queued))));
    }

    [Fact]
    public void AnOpenDecision_InFull_AndOnlyTheNewestAnsweredOnes()
    {
        var thread = Thread(LifecycleState.AwaitingDecision);
        var answered = Enumerable.Range(1, ChatSnapshot.AnsweredDecisions + 2).Select(i => Decision(thread.Id, $"Question {i}?", DecisionStatus.Answered, $"Answer {i}"));
        var open = Decision(thread.Id, "Count index pages too?", DecisionStatus.Open, null);

        var text = Describe(Details(thread, decisions: [.. answered, open]));

        Assert.Contains("**Open decision:** Count index pages too?\nWhy it blocks: The limit's meaning depends on it.\nOptions: Data pages only; Data and index pages\nRecommended: Data pages only", text);
        Assert.DoesNotContain("Question 2?", text);
        Assert.Contains("- Question 3? → Answer 3", text);
        Assert.Contains($"- Question {ChatSnapshot.AnsweredDecisions + 2}? → Answer {ChatSnapshot.AnsweredDecisions + 2}", text);
    }

    private static Decision Decision(Guid threadId, string question, DecisionStatus status, string? answer) => new()
    {
        Id = Guid.NewGuid(), ThreadId = threadId, RunId = Guid.NewGuid(), Question = question, WhyItBlocks = "The limit's meaning depends on it.",
        OptionsJson = """["Data pages only","Data and index pages"]""", Recommendation = "Data pages only", Status = status, Answer = answer, CreatedAt = Now,
    };

    [Fact]
    public void Verification_AndOpenFindings_BlockingFirst_Capped()
    {
        var thread = Thread(LifecycleState.Running);
        var run = Run(thread.Id);
        var verification = new VerificationRecord
        {
            Id = Guid.NewGuid(), ProjectId = Project.Id, RunId = run.Id, Kind = VerificationKind.Run, Commit = "abc", Build = BuildStatus.Passed,
            UnitTests = UnitTestStatus.Failed, PassedCount = 140, FailedCount = 2, SkippedCount = 1, ChangedLineCoveragePercent = 91.2, OutcomeJson = "{}",
        };
        var findings = Enumerable.Range(1, ChatSnapshot.OpenFindings + 2)
            .Select(i => Finding(run.Id, FindingSeverity.Minor, $"minor {i}"))
            .Append(Finding(run.Id, FindingSeverity.Blocking, "Limit not checked on update", line: 42))
            .Append(Finding(run.Id, FindingSeverity.Minor, "fixed already", status: FindingStatus.Fixed))
            .ToList();

        var text = Describe(Details(thread, run, verification: verification, findings: findings));

        Assert.Contains("build Passed, unit tests Failed (140 passed, 2 failed, 1 skipped), changed-line coverage 91.2%.", text);
        Assert.Contains($"**Review findings** (latest run): 1 blocking, {ChatSnapshot.OpenFindings + 3} minor; {ChatSnapshot.OpenFindings + 3} open.", text);
        var blocking = text.IndexOf("- Blocking at `src/Table.cs:42`: Limit not checked on update", StringComparison.Ordinal);
        Assert.True(blocking >= 0 && blocking < text.IndexOf("minor 1", StringComparison.Ordinal), text);
        Assert.DoesNotContain("fixed already", text);
        Assert.Equal(ChatSnapshot.OpenFindings, text.Split('\n').Count(l => l.StartsWith("- Blocking at", StringComparison.Ordinal) || l.StartsWith("- Minor at", StringComparison.Ordinal)));
    }

    private static ReviewFindingRecord Finding(Guid runId, FindingSeverity severity, string text, int? line = null, FindingStatus status = FindingStatus.Open) => new()
    {
        Id = Guid.NewGuid(), RunId = runId, Severity = severity, File = "src/Table.cs", Line = line, Text = text, Status = status,
    };

    [Fact]
    public void TheHandoff_WithItsEvidence_AndAFewKnownLimitations()
    {
        var thread = Thread(LifecycleState.AwaitingHumanTesting);
        var run = Run(thread.Id, status: RunStatus.Finished, stop: StopReason.HandedOff);
        var evidence = new HandoffEvidence(
            "Adds per-table size limits.", "factory/limits", "0123456789", 24, null, "Passed", "Passed", 142, 0, 0, 6, [], "Measured", 91, 80, [], "Passed",
            [], [], [], [.. Enumerable.Range(1, ChatSnapshot.Limitations + 1).Select(i => $"limitation {i}")], [], [], [], [], 400_000, 600_000);
        var handoff = new HandoffRecord
        {
            Id = Guid.NewGuid(), RunId = run.Id, ThreadId = thread.Id, Branch = "factory/limits", CommitSha = "0123456789", PullRequestNumber = 24,
            EvidenceJson = JsonSerializer.Serialize(evidence, FactoryWire.Json),
        };

        var text = Describe(Details(thread, run, handoff: handoff));

        Assert.Contains("**Last handoff:** branch `factory/limits`, commit 0123456, draft PR #24.", text);
        Assert.Contains("Summary: Adds per-table size limits.", text);
        Assert.Contains("Evidence: build Passed, unit tests Passed (142 passed, 0 failed), review Passed.", text);
        Assert.Contains($"- limitation {ChatSnapshot.Limitations}", text);
        Assert.DoesNotContain($"limitation {ChatSnapshot.Limitations + 1}", text);
        Assert.Contains("the task branch as last handed off", text);
    }

    [Fact]
    public void BrokenHandoffEvidence_IsLeftOut_NotAnError()
    {
        var thread = Thread(LifecycleState.AwaitingHumanTesting);
        var run = Run(thread.Id, status: RunStatus.Finished, stop: StopReason.HandedOff);
        var handoff = new HandoffRecord { Id = Guid.NewGuid(), RunId = run.Id, ThreadId = thread.Id, Branch = "factory/limits", EvidenceJson = "not json" };

        var text = Describe(Details(thread, run, handoff: handoff));

        Assert.Contains("**Last handoff:** branch `factory/limits`.", text);
        Assert.DoesNotContain("Summary:", text);
    }

    [Fact]
    public void OnlyTheNewestFactoryNotes_AreGiven_InOrder()
    {
        var thread = Thread(LifecycleState.Running);
        var messages = Enumerable.Range(1, ChatSnapshot.Notes + 2).Select(i => Message(thread.Id, MessageKind.Status, $"note {i}", i))
            .Append(Message(thread.Id, MessageKind.Text, "a person's message", 99))
            .ToList();

        var text = Describe(Details(thread, messages: messages));

        Assert.DoesNotContain("note 2\n", text);
        Assert.Contains("- note 3\n", text);
        Assert.Contains($"- note {ChatSnapshot.Notes + 2}", text);
        Assert.DoesNotContain("a person's message", text);
    }

    [Fact]
    public void HeldMessages_AreCounted()
    {
        var thread = Thread(LifecycleState.Queued);
        var messages = new[]
        {
            Message(thread.Id, MessageKind.Text, "use PageSize", 1, SteerPayload.For(SteerStatus.Held)),
            Message(thread.Id, MessageKind.Text, "earlier", 2, SteerPayload.For(SteerStatus.Delivered)),
        };

        Assert.Contains("**Waiting for the agent:** 1 @factory message(s)", Describe(Details(thread, messages: messages)));
    }

    [Theory]
    [InlineData(RunKind.Implement, RunStatus.Running, null, false, true)]
    [InlineData(RunKind.Rework, RunStatus.Running, null, true, true)]
    [InlineData(RunKind.Implement, RunStatus.Suspended, StopReason.TurnFaulted, false, true)]
    [InlineData(RunKind.Implement, RunStatus.Finished, StopReason.HandedOff, true, false)]
    [InlineData(RunKind.Rework, RunStatus.Finished, StopReason.Withdrawn, true, false)]
    public void TheCodeTheChatCanRead_SaysWhenTheRunsWorkIsNotInIt(RunKind kind, RunStatus status, StopReason? stop, bool handedOffBefore, bool unseen)
    {
        var thread = Thread(LifecycleState.Running);
        var run = Run(thread.Id, kind, status, stop);
        // The handoff is this run's when it handed off, else an earlier run's.
        var handoffRun = stop == StopReason.HandedOff ? run.Id : Guid.NewGuid();
        var handoff = handedOffBefore ? new HandoffRecord { Id = Guid.NewGuid(), RunId = handoffRun, ThreadId = thread.Id, Branch = "factory/x", EvidenceJson = "{}" } : null;

        var text = Describe(Details(thread, run, handoff: handoff));

        Assert.Equal(unseen, text.Contains("have not been pushed", StringComparison.Ordinal));
    }

    [Fact]
    public void ADraft_ReadsTheDefaultBranch_WithNothingHandedOff()
    {
        Assert.Contains("the default branch. No work has been handed off yet.", Describe(Details(Thread(LifecycleState.Draft))));
    }

    [Theory]
    [InlineData(LifecycleState.Queued, "waiting to start or continue", "`@factory <guidance>`")]
    [InlineData(LifecycleState.PausedUser, "A person paused the run", "Resume the task, or Cancel it")]
    [InlineData(LifecycleState.Accepted, "accepted the work", "start a new thread")]
    public void TheOtherStates_SayWhatTheyMean_Too(LifecycleState state, string meaning, string next)
    {
        var text = Describe(Details(Thread(state)));

        Assert.Contains(meaning, text);
        Assert.Contains(next, text);
    }

    [Theory]
    [InlineData(false, "revision 2, waiting for approval")]
    [InlineData(true, "revision 2, approved")]
    public void TheSpecification_SaysWhetherItIsApproved(bool approved, string expected)
    {
        var thread = Thread(LifecycleState.Draft);
        var spec = new Specification
        {
            Id = Guid.NewGuid(), ThreadId = thread.Id, Revision = 2, Summary = "s", AcceptanceCriteriaJson = "[]", ApprovedAt = approved ? Now : null,
        };

        Assert.Contains($"**Specification:** {expected}.", Describe(Details(thread) with { LatestSpec = spec }));
    }

    [Fact]
    public void EarlierRunsFindings_AreCounted()
    {
        var thread = Thread(LifecycleState.Running);
        var run = Run(thread.Id);
        var details = Details(thread, run, findings: [Finding(run.Id, FindingSeverity.Minor, "now")])
            with { EarlierFindings = [Finding(Guid.NewGuid(), FindingSeverity.Blocking, "before"), Finding(Guid.NewGuid(), FindingSeverity.Minor, "before")] };

        Assert.Contains("Earlier runs had 2 more.", Describe(details));
    }

    [Fact]
    public void ALongTurn_IsCountedInHours()
    {
        var thread = Thread(LifecycleState.Running);
        var activity = new RunActivity("Implement", Now.AddMinutes(-90), 1, 1, []);

        Assert.Contains("started 1.5 h ago", Describe(Details(thread, Run(thread.Id)), activity));
    }

    [Fact]
    public void ALongStateReason_IsCut_ToOneLine()
    {
        var reason = "First line.\n" + new string('x', ChatSnapshot.MaxLine * 2);

        var text = Describe(Details(Thread(LifecycleState.Blocked, reason)));

        var state = text.Split('\n')[0];
        Assert.Contains("First line. xxx", state);
        Assert.Contains("…", state);
        Assert.True(state.Length < ChatSnapshot.MaxLine + 80, state);
    }

    [Fact]
    public void ADecisionWhoseOptionsCannotBeRead_IsStillGiven_WithoutThem()
    {
        var thread = Thread(LifecycleState.AwaitingDecision);
        var decision = Decision(thread.Id, "Count index pages too?", DecisionStatus.Open, null);
        decision.OptionsJson = "not json";

        var text = Describe(Details(thread, decisions: [decision]));

        Assert.Contains("**Open decision:** Count index pages too?", text);
        Assert.DoesNotContain("Options:", text);
    }

    [Fact]
    public void HandoffEvidenceFromAnOlderHost_WithFieldsMissing_IsLeftOut_NotAnError()
    {
        var thread = Thread(LifecycleState.AwaitingHumanTesting);
        var run = Run(thread.Id, status: RunStatus.Finished, stop: StopReason.HandedOff);
        var handoff = new HandoffRecord { Id = Guid.NewGuid(), RunId = run.Id, ThreadId = thread.Id, Branch = "factory/x", EvidenceJson = """{"summary":"Old."}""" };

        var text = Describe(Details(thread, run, handoff: handoff));

        Assert.Contains("**Last handoff:** branch `factory/x`.", text);
        Assert.DoesNotContain("Evidence:", text);
    }

    [Fact]
    public void Describing_IsDeterministic()
    {
        var thread = Thread(LifecycleState.Running);
        var details = Details(thread, Run(thread.Id));

        Assert.Equal(Describe(details), Describe(details));
    }
}
