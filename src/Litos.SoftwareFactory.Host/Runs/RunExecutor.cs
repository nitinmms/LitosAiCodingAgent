using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Briefs;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Orchestration;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Core.Verification;

namespace Litos.SoftwareFactory.Host.Runs;

/// <summary>Gives the working copy for a project; the host owns one per project.</summary>
public interface IWorkspaceProvider
{
    IWorkspace For(Project project);

    /// <summary>
    /// The project's reading copy: a clone of its own that chat reads (m2-architecture.md §5), so
    /// answering a question never touches, or waits for, a task's working copy.
    /// </summary>
    IWorkspace ReadingCopyFor(Project project);
}

public interface IWorkerClientFactory
{
    IWorkerClient Create(Uri baseAddress, string secret);
}

/// <summary>Who a user is, for the commit's Co-authored-by trailer.</summary>
public interface IUserDirectory
{
    Task<string?> CoAuthorAsync(Guid userId, CancellationToken ct);
}

/// <summary>
/// Executes one claimed run: asks the orchestrator what to do next, does it through the ports,
/// and reports what happened — until the orchestrator says stop
/// (ReadMe_LitosSoftwareFactory_V1.md §8.5). The orchestrator decides; this class only acts.
/// Every step's state is saved before the next begins, so a restarted host resumes from its own
/// records.
/// </summary>
public sealed class RunExecutor(
    IFactoryStore store, FactoryOptions options, IWorkspaceProvider workspaces, IVerifier verifier,
    IWorkerLauncher launcher, IWorkerClientFactory clients, IUserDirectory users, FactorySignals signals, IClock clock,
    ILogger<RunExecutor> logger, IGitHub? gitHub = null, VerificationGate? verificationGate = null)
{
    private readonly RunOrchestrator _orchestrator = new(options.Limits);
    private readonly VerificationGate _verificationGate = verificationGate ?? new VerificationGate(options);

    /// <summary>Executes a claimed run until it stops. <see cref="RunSupervisor"/> holds the run
    /// in the registry around this, and releases it only once this has returned.</summary>
    public async Task ExecuteAsync(ClaimedRun claimed, ActiveRun active, CancellationToken hostStopping)
    {
        var (run, _, project) = claimed;
        var session = new WorkerSession(this, active, claimed);
        try
        {
            var state = run.StateJson is { Length: > 0 } json ? RunStateJson.Deserialize(json) : RunOrchestrator.NewRun(run.Kind);
            StepOutcome outcome = run.Entry switch
            {
                RunEntry.DecisionAnswered => new DecisionAnswered(run.EntryAnswer ?? ""),
                // A run that never got as far as its first checkpoint simply starts. Any other
                // resumed run continues: from its stop, or — when it was interrupted mid-step
                // and so has no stop — from the step that was under way.
                RunEntry.Resume when state.Phase != RunPhase.NotStarted => new Resumed(),
                _ => new RunStarted(),
            };

            var context = new RunData(claimed, workspaces.For(project), VerificationProfile.Parse(project.VerificationProfileJson));
            while (true)
            {
                var transition = _orchestrator.Next(state, outcome, clock.UtcNow);
                state = transition.State;

                if (transition.Step is StopStep stop)
                {
                    await StopAsync(context, state, stop);
                    return;
                }

                await store.SaveCheckpointAsync(run.Id, RunStateJson.Serialize(state), state.Stage, clock.UtcNow, CancellationToken.None, context.Snapshot);
                signals.EventsWritten();

                // A stop asked for between steps is taken before the next one starts.
                if (active.StopRequest != StopRequest.None)
                {
                    outcome = new StepStopped(active.StopRequest == StopRequest.Cancel);
                    continue;
                }

                outcome = await GuardedStepAsync(active, hostStopping, step => transition.Step switch
                {
                    PreflightStep => PreflightAsync(context, state, step, hostStopping),
                    StartTurnStep turn => RunTurnAsync(context, session, state, turn, step, hostStopping),
                    VerifyStep => VerifyAsync(context, state, step),
                    HandoffStep => HandoffAsync(context, state, step),
                    _ => throw new InvalidOperationException($"Unknown step {transition.Step.GetType().Name}."),
                });

                // The working copy as the step left it, saved with the next checkpoint or the stop,
                // so a resumed run can say what changed while it was stopped (§16).
                context.Snapshot = await SnapshotAsync(context) ?? context.Snapshot;
            }
        }
        catch (OperationCanceledException) when (hostStopping.IsCancellationRequested)
        {
            // The host is shutting down. The run stays marked Running; the next start finds it
            // and marks it Interrupted, which is the truth.
            logger.LogWarning("Run {RunId} was in progress when the host stopped.", run.Id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Run {RunId} failed in the host.", run.Id);
            await TryBlockAsync(run.Id, $"The factory host failed while running this task: {ex.Message}");
        }
        finally
        {
            await session.DisposeAsync();

            // The run's temporary files are its own, and nothing that used them is running now.
            try
            {
                Directory.Delete(options.RunTempDirectory(run.Id), recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            // The worker is gone, so a call of this run whose usage was never reported never
            // will be. Its reservation is settled now rather than held against the task.
            try
            {
                await store.ReconcileUsageAsync(run.Id, clock.UtcNow, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Unreported usage of run {RunId} could not be reconciled.", run.Id);
            }
        }
    }

    /// <summary>
    /// Runs one step so that a person's pause or cancel stops it where it is, not only at the end
    /// of the next model turn. Nothing a stopped step produced is recorded (§16: an interrupted
    /// test run is never evidence), and the orchestrator resumes the run into that step again.
    /// The host shutting down is not a stop: it still propagates.
    /// </summary>
    private static async Task<StepOutcome> GuardedStepAsync(
        ActiveRun active, CancellationToken hostStopping, Func<CancellationToken, Task<StepOutcome>> step)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(hostStopping, active.StopToken);
        try
        {
            return await step(linked.Token);
        }
        catch (OperationCanceledException) when (!hostStopping.IsCancellationRequested && active.StopRequest != StopRequest.None)
        {
            return new StepStopped(active.StopRequest == StopRequest.Cancel);
        }
    }

    /// <summary>The fixed inputs of one run, gathered once.</summary>
    private sealed record RunData(ClaimedRun Claimed, IWorkspace Workspace, VerificationProfile Profile)
    {
        public TaskRun Run => Claimed.Run;

        public TaskThread Thread => Claimed.Thread;

        public Project Project => Claimed.Project;

        /// <summary>The task branch and its base, set by preflight.</summary>
        public string Branch { get; set; } = Claimed.Thread.Branch ?? "";

        public string BaseCommit { get; set; } = Claimed.Thread.BaseCommit ?? "";

        public string? ReviewSessionId { get; set; } = Claimed.Run.ReviewSessionId;

        public VerificationOutcome? Baseline { get; set; }

        /// <summary>What the handoff recorded, for the handoff message the stop posts.</summary>
        public HandoffEvidence? Handoff { get; set; }

        /// <summary>The working copy after the last step (a WorkspaceSnapshot as JSON); null
        /// keeps the one the run was claimed with.</summary>
        public string? Snapshot { get; set; }

        /// <summary>Set once preflight has compared the working copy with the last checkpoint.</summary>
        public bool ResumeChecked { get; set; }

        /// <summary>How the working copy differed from the last checkpoint when the run resumed,
        /// for the resume brief; null when it did not.</summary>
        public string? WorkspaceDrift { get; set; }
    }

    /// <summary>The working copy now, as JSON; null when it cannot be read (before the first clone).</summary>
    private async Task<string?> SnapshotAsync(RunData data)
    {
        try
        {
            return JsonSerializer.Serialize(await data.Workspace.SnapshotAsync(CancellationToken.None), FactoryWire.Json);
        }
        catch (Exception ex) when (ex is WorkspaceException or IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "The working copy of run {RunId} could not be snapshotted.", data.Run.Id);
            return null;
        }
    }

    /// <summary>
    /// The resume check (§16): compares the working copy with the snapshot the run last recorded
    /// and says what differs, in the thread and in the resume brief. The run continues either way.
    /// A run interrupted in the middle of a step lists that step's own edits too, since the last
    /// snapshot was taken before it; the wording says what changed, not who changed it.
    /// </summary>
    private async Task CheckResumeAsync(RunData data, CancellationToken ct)
    {
        data.ResumeChecked = true;
        if (data.Run.WorkspaceSnapshotJson is not { Length: > 0 } json)
            return;

        WorkspaceDrift drift;
        try
        {
            var before = JsonSerializer.Deserialize<WorkspaceSnapshot>(json, FactoryWire.Json);
            if (before is null)
                return;
            drift = WorkspaceDrift.Compare(before, await data.Workspace.SnapshotAsync(ct));
        }
        catch (Exception ex) when (ex is JsonException or WorkspaceException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Run {RunId} could not compare its working copy with its last checkpoint.", data.Run.Id);
            return;
        }

        if (drift.IsEmpty)
            return;

        data.WorkspaceDrift = drift.Describe();
        await NoteAsync(data, "Resuming. The working copy differs from where this run last recorded it:\n" + data.WorkspaceDrift);
    }

    // ---- Preflight ----

    /// <param name="ct">Cancelled by a person's stop as well as by the host stopping.</param>
    /// <param name="hostStopping">For the first clone only: a clone cut short leaves a working
    /// copy that would later look complete, so a person's stop waits for it to finish.</param>
    private async Task<StepOutcome> PreflightAsync(RunData data, RunState state, CancellationToken ct, CancellationToken hostStopping)
    {
        try
        {
            var workspace = data.Workspace;
            await workspace.EnsureClonedAsync(hostStopping);
            await workspace.FetchAsync(ct);
            await SetAsideLeftoversAsync(data, ct);

            if (string.IsNullOrEmpty(data.Branch))
            {
                data.Branch = BranchName(data.Thread);
                data.BaseCommit = await workspace.CreateTaskBranchAsync(data.Branch, data.Project.DefaultBranch, ct);
                // The branch exists now: record it even if a stop has just been asked for, or a
                // resumed run would try to create it again.
                await store.SetThreadBranchAsync(data.Thread.Id, data.Branch, data.BaseCommit, CancellationToken.None);
                await store.SetRunCommitsAsync(data.Run.Id, data.BaseCommit, null, null, CancellationToken.None);
                await NoteAsync(data,
                    $"Assigned. Project: {data.Project.Name} (github.com/{data.Project.GitHubOwner}/{data.Project.GitHubRepository}), " +
                    $"branch {data.Branch} from {data.Project.DefaultBranch}. I will make the change, write unit tests and run the verification profile. " +
                    "Application testing will be yours.");
            }
            else
            {
                // A resumed or rework run: back on its own branch. Uncommitted work from the
                // stopped run is still there and is kept.
                var status = await workspace.GetStatusAsync(ct);
                if (status.Branch != data.Branch)
                    await workspace.CheckoutAsync(data.Branch, ct);
                await store.SetRunCommitsAsync(data.Run.Id, data.BaseCommit, null, null, ct);
                if (!data.ResumeChecked)
                    await CheckResumeAsync(data, ct);
            }

            data.Baseline = await BaselineAsync(data, state, ct);
            return new PreflightCompleted(true);
        }
        catch (WorkspaceException ex)
        {
            return new PreflightCompleted(false, ex.Message);
        }
    }

    /// <summary>
    /// A task that was cancelled, or abandoned after a failure, leaves its uncommitted edits in
    /// the project's one working copy, on its own branch. They are not this run's, and this run
    /// holds the repository, so they cannot be a running task's either: they are set aside —
    /// kept in the stash, never deleted — rather than blocking every later task on the
    /// repository. A run's own uncommitted work, on its own branch, is left exactly as it is.
    /// </summary>
    private async Task SetAsideLeftoversAsync(RunData data, CancellationToken ct)
    {
        var status = await data.Workspace.GetStatusAsync(ct);
        if (status.IsClean || status.Branch == data.Branch)
            return;

        var label = $"Left on {status.Branch} by an earlier task; set aside {clock.UtcNow:yyyy-MM-dd HH:mm} UTC for \"{data.Thread.Title}\"";
        if (await data.Workspace.SetAsideUncommittedChangesAsync(label, new CommitIdentity(options.CommitName, options.CommitEmail), ct))
        {
            await NoteAsync(data,
                $"Set aside {status.ChangedPaths.Count} uncommitted path(s) an earlier task left on {status.Branch} (first: {status.ChangedPaths[0]}). " +
                "They are kept in the working copy's stash, not deleted.");
        }
    }

    /// <summary>factory/&lt;shortThreadId&gt;-&lt;slug&gt; (§6.1).</summary>
    internal static string BranchName(TaskThread thread)
    {
        var slug = new StringBuilder();
        foreach (var c in thread.Title.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
                slug.Append(c);
            else if (slug.Length > 0 && slug[^1] != '-')
                slug.Append('-');
            if (slug.Length >= 40)
                break;
        }

        var name = slug.ToString().Trim('-');
        return $"factory/{thread.Id.ToString("N")[..4]}-{(name.Length == 0 ? "task" : name)}";
    }

    /// <summary>
    /// The profile's result on the base commit, so pre-existing failures are known and not
    /// blamed on the task. It is cached per project, commit and profile revision; it can only be
    /// measured while the working copy still is the base commit, which is at branch creation.
    /// </summary>
    private async Task<VerificationOutcome?> BaselineAsync(RunData data, RunState state, CancellationToken ct)
    {
        if (await store.FindBaselineAsync(data.Project.Id, data.BaseCommit, data.Project.ProfileRevision, ct) is { } cached)
            return JsonSerializer.Deserialize<VerificationOutcome>(cached.OutcomeJson, FactoryWire.Json);
        if (state.Baseline is not null)
            return state.Baseline;

        var status = await data.Workspace.GetStatusAsync(ct);
        if (status.HeadCommit != data.BaseCommit || !status.IsClean)
            return null;

        await NoteAsync(data, "Running the verification profile on the base commit, to know what already fails.");
        var outcome = await VerifyGatedAsync(
            data, new VerificationRequest(data.Workspace.Path, data.Profile, ChangedFiles: null, Path.Combine(options.RunDirectory(data.Run.Id), "baseline")), ct);
        await store.SaveVerificationAsync(Record(data, VerificationKind.Baseline, data.BaseCommit, outcome, runId: null), ct);
        return outcome;
    }

    // ---- Agent turns ----

    /// <param name="ct">For the work before the turn (starting the worker, compaction, the brief),
    /// which a person's stop cuts short. The turn itself is stopped through
    /// <see cref="ActiveRun.BeginTurn"/>, and reports the stop as its end.</param>
    private async Task<StepOutcome> RunTurnAsync(
        RunData data, WorkerSession session, RunState state, StartTurnStep step, CancellationToken ct, CancellationToken hostStopping)
    {
        var active = session.Active;
        var client = await session.ClientAsync(ct);

        var sessionId = step.Session switch
        {
            SessionScope.Review => await ReviewSessionAsync(data),
            SessionScope.Scan => ScanSession(data),
            _ => data.Thread.SessionId,
        };

        // Compaction before a large turn (§8.6): a repair or rework turn that would start on a
        // context past the engine's compaction trigger is compacted first, keeping what the
        // task still needs.
        int? sessionInput = active.Baselines.TryGetValue(sessionId, out var baseline) ? baseline.TotalInputTokens : null;
        if (ContextPolicy.ShouldCompactBefore(step, sessionInput, options.ContextLength))
        {
            try
            {
                if (await client.CompactAsync(sessionId, BriefComposer.CompactionInstruction(), ct))
                    active.Baselines.TryRemove(sessionId, out _);
            }
            catch (HttpRequestException ex)
            {
                return new TurnEnded(TurnEndReason.Faulted, Detail: $"Compaction before the turn failed: {ex.Message}");
            }
        }

        var (context, diff) = await BriefContextAsync(data, step, ct);
        var (phase, allowance) = await ReviewAllowanceAsync(data, state, step, diff, ct);
        if (phase == NoReviewPhase)
            return new ReviewNotNeeded();
        if (phase == "LightReview")
            context = context with { ReviewDepth = ReviewDepth.Light };

        var brief = BriefComposer.Compose(step, context, state with { Baseline = data.Baseline ?? state.Baseline }, options.Limits);
        var before = await FingerprintAsync(data, ct);

        using var timeout = new CancellationTokenSource(options.Limits.TurnTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(hostStopping, timeout.Token);
        // Once the task has had a decision, in this run or an earlier one, a limitation may say
        // exactly what the person chose; it is not sent back as a question.
        var hadDecision = state.Decisions.Count > 0 || state.DecisionsAsked > 0
            || (await store.GetThreadAsync(data.Thread.Id, ct))?.Decisions.Count > 0;
        var turnToken = active.BeginTurn(
            step.Kind, state.WorkTurn, sessionId, linked.Token, phase, allowance, askAboutBreakingLimitations: !hadDecision);

        TurnStreamResult? result = null;
        try
        {
            result = await client.RunTurnAsync(sessionId, step.Kind, brief, allowance?.MaxToolCalls ?? options.Limits.MaxToolCallsPerTurn, turnToken);
        }
        catch (OperationCanceledException) when (!hostStopping.IsCancellationRequested)
        {
            // The turn was stopped on purpose: a pause, a cancel, a recorded decision, or the
            // time limit. Closing the stream stops the worker's turn; this makes sure of it.
            await TryAsync(() => client.CancelAsync(sessionId, CancellationToken.None));
        }
        catch (HttpRequestException ex)
        {
            result = new TurnStreamResult(false, 0, $"The worker could not be reached: {ex.Message}");
        }
        catch (IOException ex)
        {
            // The turn's stream broke off: the worker process died while the turn was running.
            // That is the turn's failure, and the run can be resumed with a new worker; it is
            // not the host failing.
            logger.LogWarning(ex, "The worker for run {RunId} stopped while a turn was running.", data.Run.Id);
            result = new TurnStreamResult(false, 0, "The worker process stopped unexpectedly while the turn was running. The edits made so far are kept; resuming starts a new worker.");
        }

        var submission = active.Submission;
        if (submission is ReviewSubmission review)
            await store.SaveFindingsAsync(data.Run.Id, review.Findings, CancellationToken.None);
        if (submission is PlanSubmission plan && state.Rechecking)
        {
            var (still, settled) = DecisionPolicy.Recheck(state.PendingQuestions, plan, [.. state.Decisions.Select(d => d.Answer)]);
            await NoteAsync(data, DecisionPolicy.DescribeRecheck(settled.Count, still.Count));
        }
        else if (submission is PlanSubmission scanned)
        {
            await NoteAsync(data, DecisionPolicy.Decide(scanned, Math.Max(0, options.Limits.MaxDecisions - state.DecisionsAsked)).Describe());
        }

        if (active.StopRequest == StopRequest.Cancel)
            return new TurnEnded(TurnEndReason.Cancelled);
        if (active.StopRequest == StopRequest.Pause)
            return new TurnEnded(TurnEndReason.PausedByUser);
        if (active.BudgetRefusal is { } refusal)
            return new TurnEnded(TurnEndReason.BudgetRefused, Detail: refusal);
        if (timeout.IsCancellationRequested && submission is null)
            return new TurnEnded(TurnEndReason.TimeLimit);

        var filesChanged = before != await FingerprintAsync(data, hostStopping);
        if (submission is null && result?.Error is { } error)
        {
            if (error.StartsWith("The turn exceeded", StringComparison.Ordinal))
                return new TurnEnded(TurnEndReason.ToolCallLimit, FilesChanged: filesChanged);
            return error.Contains(Gateway.ModelGateway.CutOffPrefix, StringComparison.Ordinal)
                ? new TurnEnded(TurnEndReason.OutputLimit, FilesChanged: filesChanged, Detail: error, LightReview: phase == "LightReview")
                : new TurnEnded(TurnEndReason.Faulted, FilesChanged: filesChanged, Detail: error);
        }

        return new TurnEnded(TurnEndReason.Completed, submission, filesChanged);
    }

    /// <summary>The run's decision-scan session: fresh, and the same for a nudge or a resume of the
    /// scan, so it needs no record of its own.</summary>
    private static string ScanSession(RunData data) => $"scan-{data.Run.Id:N}";

    private async Task<string> ReviewSessionAsync(RunData data)
    {
        if (data.ReviewSessionId is null)
        {
            // Always a fresh session, so the reviewer never sees the thread's conversation.
            data.ReviewSessionId = $"review-{Guid.NewGuid():N}";
            await store.SetRunCommitsAsync(data.Run.Id, null, null, data.ReviewSessionId, CancellationToken.None);
        }

        return data.ReviewSessionId;
    }

    /// <summary>
    /// For a review turn, how deep it goes and what it may spend (ReviewPlanner, TurnAllowance);
    /// for any other turn, its phase and no allowance. A first review is planned from the diff
    /// and the evidence, and the thread says which it got and why. A reminder gets a small
    /// allowance, since it has already looked; a resumed review continues as a full one.
    /// </summary>
    /// <summary>The phase ReviewAllowanceAsync returns for a change that needs no review: no turn runs.</summary>
    private const string NoReviewPhase = "NoReview";

    private async Task<(string Phase, TurnAllowance? Allowance)> ReviewAllowanceAsync(
        RunData data, RunState state, StartTurnStep step, WorkspaceDiff? diff, CancellationToken ct)
    {
        if (state.WorkTurn == TurnKind.Scan)
            return ("Scan", step.Brief is BriefKind.Nudge or BriefKind.ScanRecheck ? TurnAllowance.ForScanNudge(options.Limits) : TurnAllowance.ForScan(options.Limits));
        if (state.WorkTurn != TurnKind.Review)
            return (step.Kind.ToString(), null);
        if (step.Brief == BriefKind.Nudge)
            return ("Review", TurnAllowance.ForReviewNudge(options.Limits));

        // What this run spent on the work under review: its implement, rework, repair and
        // work-nudge turns. Earlier runs of the task are not this review's concern.
        var implementation = (await store.ListUsageAsync(data.Thread.Id, ct))
            .Where(u => u.RunId == data.Run.Id && u.Phase is nameof(TurnKind.Implement) or nameof(TurnKind.Rework) or nameof(TurnKind.Repair) or nameof(TurnKind.Nudge))
            .Sum(u => u.Charged);

        // A review resumed after a stop is not planned again: it continues, as a full review.
        if (step.Brief != BriefKind.Review || diff is null)
            return ("Review", TurnAllowance.ForReview(ReviewDepth.Full, implementation, options.Limits));

        var plan = ReviewPlanner.Plan(new ReviewInputs(diff.Files, diff.Patch, state.LastVerification, state.LastSubmission), options.Limits);
        await NoteAsync(data, plan.Describe());
        return plan.Depth switch
        {
            ReviewDepth.None => (NoReviewPhase, null),
            ReviewDepth.Light => ("LightReview", TurnAllowance.ForReview(ReviewDepth.Light, implementation, options.Limits)),
            _ => ("Review", TurnAllowance.ForReview(ReviewDepth.Full, implementation, options.Limits)),
        };
    }

    private async Task<(RunContext Context, WorkspaceDiff? Diff)> BriefContextAsync(RunData data, StartTurnStep step, CancellationToken ct)
    {
        var details = await store.GetThreadAsync(data.Thread.Id, ct);
        var firstRequest = details?.Messages.FirstOrDefault(m => m.Author == MessageAuthor.User && m.Kind == MessageKind.Text)?.Text ?? data.Run.Request;

        var context = new RunContext(data.Project.Name, data.Branch, data.Project.DefaultBranch, data.Run.Kind == RunKind.Rework ? firstRequest : data.Run.Request)
        {
            VerificationSummary = string.Join("\n", data.Profile.Steps.SelectMany(s => s.Commands().Select(c => $"- {s.Name}: {c.Command.Display()}"))),
            CoverageThresholdPercent = data.Profile.Coverage?.ChangedLinesThresholdPercent,
            WorkspaceDrift = data.WorkspaceDrift,
        };

        WorkspaceDiff? reviewDiff = null;
        if (step.Brief is BriefKind.Rework or BriefKind.Review)
        {
            // The review of a rework run covers only the rework: what was handed off before has
            // been reviewed, and reviewing the whole task again on every round was the largest
            // cost of the first real runs.
            var reviewedThrough = step.Brief == BriefKind.Review && data.Run.Kind == RunKind.Rework
                ? details?.LatestHandoff?.CommitSha
                : null;
            var diff = await data.Workspace.DiffAsync(string.IsNullOrEmpty(reviewedThrough) ? data.BaseCommit : reviewedThrough, ct);
            if (step.Brief == BriefKind.Review)
                reviewDiff = diff;
            context = context with
            {
                ChangedFiles = [.. diff.Files.Select(f => f.Path)],
                Diff = diff.Patch,
                ChangedLineCount = diff.ChangedLineCount,
                ReviewedThroughCommit = string.IsNullOrEmpty(reviewedThrough) ? null : reviewedThrough,
                TesterFeedback = string.IsNullOrEmpty(reviewedThrough) ? null : data.Run.Request,
            };
        }

        if (step.Brief == BriefKind.Rework)
        {
            string? previous = null;
            if (details?.LatestHandoff is { } handoff)
                previous = JsonSerializer.Deserialize<HandoffEvidence>(handoff.EvidenceJson, FactoryWire.Json)?.Summary;
            context = context with { TesterFeedback = data.Run.Request, PreviousHandoffSummary = previous };
        }

        return (context, reviewDiff);
    }

    /// <summary>A hash of everything that differs from the base commit, to tell whether a turn
    /// changed any file.</summary>
    private static async Task<string> FingerprintAsync(RunData data, CancellationToken ct)
    {
        try
        {
            var diff = await data.Workspace.DiffAsync(data.BaseCommit, ct);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(diff.Patch)));
        }
        catch (WorkspaceException)
        {
            return "";
        }
    }

    // ---- Verify ----

    /// <summary>
    /// Runs the verifier once a verification permit is free, in the run's own temporary
    /// directory. Waiting for a permit is said in the thread, and a stop cancels the wait.
    /// </summary>
    private async Task<VerificationOutcome> VerifyGatedAsync(RunData data, VerificationRequest request, CancellationToken ct)
    {
        using var permit = await _verificationGate.EnterAsync(
            () => NoteAsync(data, "Waiting for another task's verification to finish before running this one's."), ct);
        return await verifier.VerifyAsync(request with { TempDirectory = options.RunTempDirectory(data.Run.Id) }, ct);
    }

    /// <param name="ct">Cancelled by a person's stop: the commands' process trees are killed and
    /// nothing is recorded, since an interrupted run is never evidence (§16).</param>
    private async Task<StepOutcome> VerifyAsync(RunData data, RunState state, CancellationToken ct)
    {
        var diff = await data.Workspace.DiffAsync(data.BaseCommit, ct);
        var outcome = await VerifyGatedAsync(
            data, new VerificationRequest(data.Workspace.Path, data.Profile, diff.Files, Path.Combine(options.RunDirectory(data.Run.Id), $"verify-{clock.UtcNow:HHmmss}")), ct);

        var status = await data.Workspace.GetStatusAsync(ct);
        await store.SaveVerificationAsync(Record(data, VerificationKind.Run, status.HeadCommit, outcome, data.Run.Id), ct);

        var baseline = data.Baseline ?? state.Baseline;
        var newFailures = outcome.NewFailures(baseline).Count;
        var coverage = outcome.ChangedLines is { } changed ? $"changed-line coverage {changed.Percent:0.#}%" : $"coverage {outcome.Coverage}";
        await NoteAsync(data,
            $"Verification: build {outcome.Build}; unit tests {outcome.UnitTests} ({outcome.PassedCount} passed, {outcome.FailedCount} failed" +
            (newFailures != outcome.FailedCount ? $", {outcome.FailedCount - newFailures} of them already failing before this task" : "") +
            $"); {coverage}.");

        return new Verified(outcome, baseline);
    }

    private static VerificationRecord Record(RunData data, VerificationKind kind, string commit, VerificationOutcome outcome, Guid? runId) => new()
    {
        ProjectId = data.Project.Id,
        RunId = runId,
        Kind = kind,
        Commit = commit,
        ProfileRevision = data.Project.ProfileRevision,
        Build = outcome.Build,
        UnitTests = outcome.UnitTests,
        Coverage = outcome.Coverage,
        PassedCount = outcome.PassedCount,
        FailedCount = outcome.FailedCount,
        SkippedCount = outcome.SkippedCount,
        ChangedLineCoveragePercent = outcome.ChangedLines?.Percent,
        OutcomeJson = JsonSerializer.Serialize(outcome, FactoryWire.Json),
        CreatedAt = DateTimeOffset.UtcNow,
    };

    // ---- Handoff ----

    /// <summary>
    /// Performed by the host, not the agent (§6.1): commit as the factory identity with a
    /// Co-authored-by trailer for the requesting user, push the branch, and open or update the
    /// draft PR. Never a force-push, never the default branch, never a merge.
    /// </summary>
    private async Task<StepOutcome> HandoffAsync(RunData data, RunState state, CancellationToken ct)
    {
        try
        {
            var workspace = data.Workspace;
            var diff = await workspace.DiffAsync(data.BaseCommit, ct);
            var summary = state.LastSubmission?.Summary;
            var message = string.IsNullOrWhiteSpace(summary) ? data.Thread.Title : $"{data.Thread.Title}\n\n{summary.Trim()}";

            await workspace.CommitAllAsync(
                message, new CommitIdentity(options.CommitName, options.CommitEmail), await users.CoAuthorAsync(data.Run.RequestedBy, ct), ct);
            var head = (await workspace.GetStatusAsync(ct)).HeadCommit;
            await workspace.PushAsync(data.Branch, data.Project.DefaultBranch, ct);

            // From here the branch is pushed, so the rest of the handoff is finished even if a
            // stop has been asked for: a pushed branch always gets its handoff record.

            // The handoff is the pushed branch. A draft PR that cannot be opened is reported in
            // the handoff, not treated as the handoff failing.
            PullRequestRef? pullRequest = null;
            var notes = new List<string>();
            if (data.Project.PullRequestEnabled && gitHub is not null)
            {
                try
                {
                    pullRequest = await gitHub.CreateOrUpdateDraftPullRequestAsync(
                        new PullRequestDraft(
                            data.Project.GitHubOwner, data.Project.GitHubRepository, data.Branch, data.Project.DefaultBranch,
                            data.Thread.Title, summary ?? data.Thread.Title),
                        CancellationToken.None);
                }
                catch (Exception ex) when (ex is HttpRequestException or Infrastructure.GitHub.GitHubException)
                {
                    logger.LogWarning(ex, "The pull request for run {RunId} could not be created or updated.", data.Run.Id);

                    // A rework pushes to a branch whose pull request already exists. Failing to
                    // refresh its description does not make it go away: the handoff still names it.
                    if (data.Thread is { PullRequestNumber: { } number, PullRequestUrl: { Length: > 0 } url })
                    {
                        pullRequest = new PullRequestRef(number, url);
                        notes.Add($"The pull request's description could not be updated: {ex.Message}");
                    }
                    else
                    {
                        notes.Add($"The draft PR could not be opened: {ex.Message}");
                    }
                }
            }

            var thread = (await store.GetThreadAsync(data.Thread.Id, CancellationToken.None))!.Thread;
            var evidence = HandoffComposer.Evidence(state, new HandoffFacts(data.Branch, head, pullRequest?.Number, pullRequest?.Url)
            {
                CoverageThresholdPercent = data.Profile.Coverage?.ChangedLinesThresholdPercent,
                TokensUsed = thread.TokensUsed,
                BudgetCap = thread.BudgetCap,
                ChangedFiles = [.. diff.Files.Select(f => f.Path)],
                Notes = notes,
            });

            await store.SetRunCommitsAsync(data.Run.Id, null, head, null, CancellationToken.None);
            if (state.Review == ReviewStatus.FindingsFixed)
                await store.SetFindingsStatusAsync(data.Run.Id, FindingSeverity.Blocking, FindingStatus.Fixed, CancellationToken.None);
            await store.SaveHandoffAsync(new HandoffRecord
            {
                RunId = data.Run.Id,
                ThreadId = data.Thread.Id,
                Branch = data.Branch,
                CommitSha = head,
                PullRequestNumber = pullRequest?.Number,
                PullRequestUrl = pullRequest?.Url,
                EvidenceJson = JsonSerializer.Serialize(evidence, FactoryWire.Json),
                CreatedAt = clock.UtcNow,
            }, CancellationToken.None);
            data.Handoff = evidence;
            return new HandoffCompleted(true);
        }
        catch (WorkspaceException ex)
        {
            // Local commits are kept; the push is retried on Resume (§16).
            return new HandoffCompleted(false, ex.Message);
        }
    }

    // ---- Stop ----

    private async Task StopAsync(RunData data, RunState state, StopStep stop)
    {
        var command = new StopRunCommand(data.Run.Id, stop.Trigger, stop.Reason, stop.Message)
        {
            StateJson = RunStateJson.Serialize(state),
            Stage = state.Stage,
            WorkspaceSnapshotJson = data.Snapshot,
        };

        if (stop.Reason == StopReason.HandedOff && data.Handoff is { } evidence)
        {
            command = command with
            {
                Message = HandoffComposer.Text(evidence),
                MessageKind = MessageKind.Handoff,
                MessagePayloadJson = JsonSerializer.Serialize(evidence, FactoryWire.Json),
            };
        }
        else if (stop.Trigger == LifecycleTrigger.RequestDecision && state.OpenDecision is { } decision)
        {
            var opened = await store.OpenDecisionAsync(data.Run.Id, decision, clock.UtcNow, CancellationToken.None);
            command = command with
            {
                Message = $"Decision needed: {decision.Question} No further edits will run until you answer.",
                MessageKind = MessageKind.Decision,
                DecisionId = opened.Id,
                MessagePayloadJson = JsonSerializer.Serialize(
                    new { opened.Id, decision.Question, decision.WhyItBlocks, decision.Options, decision.Recommendation, decision.Impact }, FactoryWire.Json),
            };
        }

        await store.StopRunAsync(command, clock.UtcNow, CancellationToken.None);
    }

    private async Task TryBlockAsync(Guid runId, string message)
    {
        try
        {
            await store.StopRunAsync(new StopRunCommand(runId, LifecycleTrigger.Block, StopReason.TurnFaulted, message), clock.UtcNow, CancellationToken.None);
        }
        catch (Exception ex) when (ex is StoreConflictException or StoreNotFoundException)
        {
            logger.LogWarning(ex, "Run {RunId} could not be marked blocked.", runId);
        }
    }

    private async Task NoteAsync(RunData data, string text)
    {
        await store.AddFactoryMessageAsync(data.Thread.Id, MessageKind.Status, text, null, clock.UtcNow, CancellationToken.None);
        signals.EventsWritten();
    }

    private static async Task TryAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
        }
    }

    private async Task<(IWorkerHandle Handle, IWorkerClient Client)> StartWorkerAsync(ActiveRun active, ClaimedRun claimed, CancellationToken ct)
    {
        var handle = await launcher.LaunchAsync(
            new WorkerLaunch(
                claimed.Run.Id.ToString("N"), workspaces.For(claimed.Project).Path, claimed.Thread.Provider, claimed.Thread.Model,
                options.ContextLength, options.DataDirectory, HostUrl, active.Secret)
            {
                PtcEnabled = options.PtcEnabled,
                TempDirectory = options.RunTempDirectory(claimed.Run.Id),
            },
            ct);
        await store.SetRunWorkerAsync(claimed.Run.Id, handle.ProcessId, handle.StartTime, CancellationToken.None);
        return (handle, clients.Create(handle.BaseAddress, active.Secret));
    }

    /// <summary>The run's worker, started when the first turn needs it and stopped when the run ends.</summary>
    private sealed class WorkerSession(RunExecutor executor, ActiveRun active, ClaimedRun claimed) : IAsyncDisposable
    {
        private IWorkerHandle? _handle;
        private IWorkerClient? _client;

        public ActiveRun Active => active;

        public async Task<IWorkerClient> ClientAsync(CancellationToken ct)
        {
            if (_client is not null && _handle is { HasExited: false })
                return _client;

            (_handle, _client) = await executor.StartWorkerAsync(active, claimed, ct);
            active.Client = _client;
            return _client;
        }

        public async ValueTask DisposeAsync()
        {
            active.Client = null;
            if (_client is not null)
                await TryAsync(() => _client.ShutdownAsync(CancellationToken.None));
            if (_handle is not null)
                await _handle.DisposeAsync();
        }
    }

    /// <summary>The address workers call back on; set once the host is listening.</summary>
    public string HostUrl { get; set; } = "";
}
