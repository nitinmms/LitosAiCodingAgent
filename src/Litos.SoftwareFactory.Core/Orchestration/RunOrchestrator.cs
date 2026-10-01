using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Verification;

namespace Litos.SoftwareFactory.Core.Orchestration;

/// <summary>
/// The run sequence of ReadMe_LitosSoftwareFactory_V1.md §8.5 as a resumable step machine:
/// given where a run is and what just happened, <see cref="Next"/> says what the host does next.
///
/// It is a pure function. It runs no commands, starts no turns and reads no clock; the host
/// performs each step through its ports and reports the outcome back. Every stage transition is
/// therefore decided from the host's own records — a completion tool's payload, an exit code, a
/// parsed report — and never from the agent's prose.
/// </summary>
public sealed class RunOrchestrator(RunLimits? limits = null)
{
    private const string BuildFailure = "<build>";
    private const string NoReportFailure = "<no test report>";

    public RunLimits Limits { get; } = limits ?? new RunLimits();

    public static RunState NewRun(RunKind kind) => new(kind);

    /// <param name="now">The current time, supplied by the host's clock.</param>
    /// <exception cref="InvalidOperationException">The outcome does not belong to the step the
    /// run is on — a host bug, not a run failure.</exception>
    public RunTransition Next(RunState state, StepOutcome outcome, DateTimeOffset now)
    {
        var transition = outcome switch
        {
            RunStarted => OnStarted(state, now),
            Resumed => OnResumed(state, now),
            PreflightCompleted preflight => OnPreflight(state, preflight),
            TurnEnded turn => OnTurnEnded(state, turn),
            DecisionAnswered answer => OnDecisionAnswered(state, answer, now),
            Verified verified => OnVerified(state, verified),
            HandoffCompleted handoff => OnHandoff(state, handoff),
            _ => throw new InvalidOperationException($"Unknown step outcome {outcome.GetType().Name}."),
        };

        // The run time limit is checked whenever the run is about to do more work, so a run can
        // overshoot it only by the step already in flight.
        if (transition.Step is not StopStep && now - transition.State.ActiveSince > Limits.RunTimeout)
        {
            return Stop(
                transition.State, LifecycleTrigger.Block, StopReason.RunTimeLimit,
                $"The run passed its time limit of {Describe(Limits.RunTimeout)}.", resumePoint: transition.Step);
        }

        return transition;
    }

    private static RunTransition OnStarted(RunState state, DateTimeOffset now)
    {
        Require(state.Phase == RunPhase.NotStarted, state, "start");

        RunStep first = state.Kind == RunKind.Rework
            ? new StartTurnStep(TurnKind.Rework, BriefKind.Rework)
            : new StartTurnStep(TurnKind.Implement, BriefKind.Run);
        return Preflight(state with { ActiveSince = now }, first);
    }

    private static RunTransition OnResumed(RunState state, DateTimeOffset now)
    {
        Require(state.Phase == RunPhase.Stopped && state.ResumePoint is not null, state, "resume");

        // A blocker a person has resolved earns a fresh allowance: the run would otherwise stop
        // again at once on a limit it had already reached. Pauses keep their counts.
        var afterBlock = state.LastStop?.Trigger == LifecycleTrigger.Block;
        var resumed = state with
        {
            ActiveSince = now,
            NudgeUsed = false,
            RepairCyclesUsed = afterBlock ? 0 : state.RepairCyclesUsed,
            FailuresBeforeRepair = afterBlock ? null : state.FailuresBeforeRepair,
        };
        return Preflight(resumed, state.ResumePoint!);
    }

    private static RunTransition OnDecisionAnswered(RunState state, DecisionAnswered answer, DateTimeOffset now)
    {
        Require(state.Phase == RunPhase.Stopped && state.OpenDecision is not null, state, "answer a decision");

        var answered = state with
        {
            ActiveSince = now,
            NudgeUsed = false,
            Decisions = [.. state.Decisions, new AnsweredDecision(state.OpenDecision!.Question, answer.Answer)],
            OpenDecision = null,
        };
        return Preflight(answered, new StartTurnStep(state.WorkTurn, BriefKind.DecisionAnswer));
    }

    private static RunTransition Preflight(RunState state, RunStep afterPreflight) =>
        new(state with { Phase = RunPhase.Preflight, AfterPreflight = afterPreflight, ResumePoint = null }, new PreflightStep());

    private RunTransition OnPreflight(RunState state, PreflightCompleted preflight)
    {
        Require(state.Phase == RunPhase.Preflight && state.AfterPreflight is not null, state, "finish preflight");

        if (!preflight.Succeeded)
        {
            return Stop(
                state, LifecycleTrigger.Block, StopReason.PreflightFailed,
                preflight.Failure ?? "Preflight failed.", resumePoint: state.AfterPreflight);
        }

        return Enter(state, state.AfterPreflight!);
    }

    private RunTransition OnTurnEnded(RunState state, TurnEnded turn)
    {
        Require(state.Phase == RunPhase.Turn, state, "end a turn");

        var resumeTurn = new StartTurnStep(state.WorkTurn, BriefKind.Resume, SessionOf(state));
        switch (turn.Reason)
        {
            case TurnEndReason.BudgetRefused:
                return Stop(
                    state, LifecycleTrigger.ExhaustBudget, StopReason.BudgetExhausted,
                    turn.Detail ?? "The next model call does not fit the task's token allowance.", resumeTurn);
            case TurnEndReason.PausedByUser:
                return Stop(state, LifecycleTrigger.Pause, StopReason.PausedByUser, "Paused.", resumeTurn);
            case TurnEndReason.Cancelled:
                return Stop(state, LifecycleTrigger.Cancel, StopReason.Cancelled, "Cancelled. Edits and the branch are kept.", resumePoint: null);
            case TurnEndReason.Faulted:
                return Stop(state, LifecycleTrigger.Block, StopReason.TurnFaulted, turn.Detail ?? "The turn failed.", resumeTurn);
            case TurnEndReason.ToolCallLimit:
                return Stop(
                    state, LifecycleTrigger.Block, StopReason.ToolCallLimit,
                    $"The turn made more than {Limits.MaxToolCallsPerTurn} tool calls.", resumeTurn);
            case TurnEndReason.TimeLimit:
                return Stop(
                    state, LifecycleTrigger.Block, StopReason.TurnTimeLimit,
                    $"The turn passed its time limit of {Describe(Limits.TurnTimeout)}.", resumeTurn);
        }

        return state.WorkTurn == TurnKind.Review
            ? OnReviewTurnCompleted(state, turn)
            : OnWorkTurnCompleted(state, turn);
    }

    private RunTransition OnWorkTurnCompleted(RunState state, TurnEnded turn)
    {
        switch (turn.Submission)
        {
            case WorkSubmission work:
                return Enter(state with { LastSubmission = work }, new VerifyStep());

            case DecisionSubmission decision when state.DecisionsAsked < Limits.MaxDecisions:
                return Stop(
                    state with { DecisionsAsked = state.DecisionsAsked + 1, OpenDecision = decision },
                    LifecycleTrigger.RequestDecision, StopReason.DecisionNeeded, decision.Question, resumePoint: null);

            case DecisionSubmission decision:
                // Past the limit the factory proceeds on the agent's own recommendation and
                // records that. This uses the turn's one automatic follow-up, so an agent that
                // only ever asks cannot loop.
                if (state.NudgeUsed)
                    break;
                var disclosure = $"Not asked (decision limit of {Limits.MaxDecisions} reached); proceeded on the agent's recommendation: {decision.Question}";
                return Enter(
                    state with { Disclosures = [.. state.Disclosures, disclosure] },
                    new StartTurnStep(TurnKind.Nudge, BriefKind.ProceedOnRecommendation));
        }

        // No usable completion call.
        if (!turn.FilesChanged && !state.NudgeUsed)
        {
            return Stop(
                state, LifecycleTrigger.Block, StopReason.NoProgress,
                "The turn changed no files and did not call submit_work or request_decision.",
                new StartTurnStep(state.WorkTurn, BriefKind.Resume));
        }

        return NudgeOrBlock(state, "submit_work or request_decision");
    }

    private RunTransition OnReviewTurnCompleted(RunState state, TurnEnded turn)
    {
        if (turn.Submission is not ReviewSubmission review)
            return NudgeOrBlock(state, "submit_review");

        var blocking = review.Findings.Any(f => f.Severity == FindingSeverity.Blocking);
        var reviewed = state with
        {
            ReviewCompleted = true,
            Findings = review.Findings,
            Review = review.Findings.Count == 0 ? ReviewStatus.Clean : ReviewStatus.FindingsOpen,
        };

        if (!blocking)
            return Enter(reviewed, new HandoffStep());

        if (reviewed.RepairCyclesUsed >= Limits.MaxRepairCycles)
        {
            return Enter(
                reviewed with
                {
                    Disclosures = [.. reviewed.Disclosures, "Blocking review findings were not addressed: no repair cycles were left."],
                },
                new HandoffStep());
        }

        // Blocking findings lead to one repair turn and one more Verify. The review itself is
        // never repeated, which bounds its cost.
        return Enter(
            reviewed with { RepairCyclesUsed = reviewed.RepairCyclesUsed + 1, ReviewRepairPending = true, FailuresBeforeRepair = null },
            new StartTurnStep(TurnKind.Repair, BriefKind.Repair));
    }

    private RunTransition NudgeOrBlock(RunState state, string expectedTools)
    {
        if (!state.NudgeUsed)
            return Enter(state, new StartTurnStep(TurnKind.Nudge, BriefKind.Nudge, SessionOf(state)));

        return Stop(
            state, LifecycleTrigger.Block, StopReason.NoCompletionCall,
            $"The turn, and the reminder after it, both ended without calling {expectedTools}.",
            new StartTurnStep(state.WorkTurn, BriefKind.Resume, SessionOf(state)));
    }

    private RunTransition OnVerified(RunState state, Verified verified)
    {
        Require(state.Phase == RunPhase.Verify, state, "finish verification");

        var outcome = verified.Outcome;
        var verifiedState = state with { LastVerification = outcome, Baseline = verified.Baseline ?? state.Baseline };

        if (outcome.Build == BuildStatus.Unavailable || outcome.UnitTests == UnitTestStatus.Unavailable)
        {
            var detail = outcome.Problems.Count > 0 ? " " + string.Join(" ", outcome.Problems) : "";
            return Stop(
                verifiedState, LifecycleTrigger.Block, StopReason.VerificationUnavailable,
                "The verification commands could not be run." + detail, new VerifyStep());
        }

        var failures = HardFailures(outcome, verifiedState.Baseline);
        var coverageShort = outcome.Coverage == CoverageStatus.BelowThreshold;

        if (failures.Count == 0 && !coverageShort)
            return AfterPassingVerification(verifiedState);

        // "Repair made no difference": the same failures before and after a repair turn.
        if (failures.Count > 0 && state.FailuresBeforeRepair is { } before && before.SetEquals(failures))
        {
            return Stop(
                verifiedState, LifecycleTrigger.Block, StopReason.RepairMadeNoDifference,
                $"The repair made no difference: {DescribeFailures(failures)} still failing.", new VerifyStep());
        }

        if (verifiedState.RepairCyclesUsed < Limits.MaxRepairCycles)
        {
            return Enter(
                verifiedState with
                {
                    RepairCyclesUsed = verifiedState.RepairCyclesUsed + 1,
                    FailuresBeforeRepair = failures.Count > 0 ? failures : null,
                },
                new StartTurnStep(TurnKind.Repair, BriefKind.Repair));
        }

        if (failures.Count > 0)
        {
            return Stop(
                verifiedState, LifecycleTrigger.Block, StopReason.RepairCyclesExhausted,
                $"Still failing after {Limits.MaxRepairCycles} repair cycles: {DescribeFailures(failures)}.", new VerifyStep());
        }

        // Only coverage is short, and persistently so: that is disclosed, not blocked (§10.2).
        var percent = outcome.ChangedLines?.Percent;
        var coverageNote = percent is null
            ? "Changed-line coverage is below the project threshold."
            : $"Changed-line coverage is {percent:0.#}%, below the project threshold, after {Limits.MaxRepairCycles} repair cycles.";
        return AfterPassingVerification(verifiedState with { Disclosures = [.. verifiedState.Disclosures, coverageNote] });
    }

    private RunTransition AfterPassingVerification(RunState state)
    {
        var passed = state with { FailuresBeforeRepair = null };
        if (passed.ReviewRepairPending)
            passed = passed with { ReviewRepairPending = false, Review = ReviewStatus.FindingsFixed };

        return passed.ReviewCompleted
            ? Enter(passed, new HandoffStep())
            : Enter(passed, new StartTurnStep(TurnKind.Review, BriefKind.Review, SessionScope.Review));
    }

    private static RunTransition OnHandoff(RunState state, HandoffCompleted handoff)
    {
        Require(state.Phase == RunPhase.Handoff, state, "finish the handoff");

        return handoff.Succeeded
            ? Stop(state, LifecycleTrigger.Handoff, StopReason.HandedOff, "Ready for human testing.", resumePoint: null)
            : Stop(
                state, LifecycleTrigger.Block, StopReason.HandoffFailed,
                handoff.Failure ?? "The handoff failed.", new HandoffStep());
    }

    /// <summary>
    /// What makes a verification the task's failure: a failed build, a test command that produced
    /// no report, or a test that fails now and did not fail on the base commit.
    /// </summary>
    internal static IReadOnlySet<string> HardFailures(VerificationOutcome outcome, VerificationOutcome? baseline)
    {
        var failures = new HashSet<string>(outcome.NewFailures(baseline).Select(t => t.Name));
        if (outcome.Build == BuildStatus.Failed)
            failures.Add(BuildFailure);
        if (outcome.UnitTests == UnitTestStatus.Failed && outcome.FailedCount == 0)
            failures.Add(NoReportFailure);
        return failures;
    }

    private static string DescribeFailures(IReadOnlySet<string> failures)
    {
        var parts = new List<string>();
        if (failures.Contains(BuildFailure))
            parts.Add("the build");
        if (failures.Contains(NoReportFailure))
            parts.Add("a test command with no report");
        var tests = failures.Count(f => f != BuildFailure && f != NoReportFailure);
        if (tests > 0)
            parts.Add(tests == 1 ? "1 test" : $"{tests} tests");
        return string.Join(" and ", parts);
    }

    private static RunTransition Enter(RunState state, RunStep step) => step switch
    {
        StartTurnStep { Kind: TurnKind.Nudge } turn =>
            new(state with { Phase = RunPhase.Turn, CurrentTurn = TurnKind.Nudge, NudgeUsed = true }, turn),
        StartTurnStep turn =>
            new(state with { Phase = RunPhase.Turn, CurrentTurn = turn.Kind, WorkTurn = turn.Kind, NudgeUsed = false }, turn),
        VerifyStep => new(state with { Phase = RunPhase.Verify }, step),
        HandoffStep => new(state with { Phase = RunPhase.Handoff }, step),
        _ => throw new InvalidOperationException($"A run cannot enter {step.GetType().Name}."),
    };

    private static RunTransition Stop(
        RunState state, LifecycleTrigger trigger, StopReason reason, string message, RunStep? resumePoint)
    {
        var stop = new StopStep(trigger, reason, message);
        return new(state with { Phase = RunPhase.Stopped, LastStop = stop, ResumePoint = resumePoint }, stop);
    }

    /// <summary>A nudge or a resume continues whichever conversation the work turn was in.</summary>
    private static SessionScope SessionOf(RunState state) =>
        state.WorkTurn == TurnKind.Review ? SessionScope.Review : SessionScope.Thread;

    private static void Require(bool condition, RunState state, string action)
    {
        if (!condition)
            throw new InvalidOperationException($"A run in phase {state.Phase} cannot {action}.");
    }

    private static string Describe(TimeSpan span) =>
        span.TotalHours >= 1 && span.Minutes == 0 ? $"{span.TotalHours:0} hours" : $"{span.TotalMinutes:0} minutes";
}
