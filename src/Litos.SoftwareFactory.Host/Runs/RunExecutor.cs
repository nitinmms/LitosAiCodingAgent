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
    IFactoryStore store, RunRegistry registry, FactoryOptions options, IWorkspaceProvider workspaces, IVerifier verifier,
    IWorkerLauncher launcher, IWorkerClientFactory clients, IUserDirectory users, FactorySignals signals, IClock clock,
    ILogger<RunExecutor> logger, IGitHub? gitHub = null)
{
    private readonly RunOrchestrator _orchestrator = new(options.Limits);

    public async Task ExecuteAsync(ClaimedRun claimed, CancellationToken hostStopping)
    {
        var (run, thread, project) = claimed;
        var active = registry.Add(new ActiveRun(run.Id, thread.Id, run.RequestedBy, thread.Provider, thread.Model));
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

                await store.SaveCheckpointAsync(run.Id, RunStateJson.Serialize(state), state.Stage, clock.UtcNow, CancellationToken.None);
                signals.EventsWritten();

                outcome = transition.Step switch
                {
                    PreflightStep => await PreflightAsync(context, state, hostStopping),
                    StartTurnStep turn => await RunTurnAsync(context, session, state, turn, hostStopping),
                    VerifyStep => await VerifyAsync(context, state, hostStopping),
                    HandoffStep => await HandoffAsync(context, state, hostStopping),
                    _ => throw new InvalidOperationException($"Unknown step {transition.Step.GetType().Name}."),
                };
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
            registry.Remove(run.Id);
            signals.EventsWritten();
            signals.WorkQueued();
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
    }

    // ---- Preflight ----

    private async Task<StepOutcome> PreflightAsync(RunData data, RunState state, CancellationToken ct)
    {
        try
        {
            var workspace = data.Workspace;
            await workspace.EnsureClonedAsync(ct);
            await workspace.FetchAsync(ct);

            if (string.IsNullOrEmpty(data.Branch))
            {
                data.Branch = BranchName(data.Thread);
                data.BaseCommit = await workspace.CreateTaskBranchAsync(data.Branch, data.Project.DefaultBranch, ct);
                await store.SetThreadBranchAsync(data.Thread.Id, data.Branch, data.BaseCommit, ct);
                await store.SetRunCommitsAsync(data.Run.Id, data.BaseCommit, null, null, ct);
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
            }

            data.Baseline = await BaselineAsync(data, state, ct);
            return new PreflightCompleted(true);
        }
        catch (WorkspaceException ex)
        {
            return new PreflightCompleted(false, ex.Message);
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
        var outcome = await verifier.VerifyAsync(
            new VerificationRequest(data.Workspace.Path, data.Profile, ChangedFiles: null, Path.Combine(options.RunDirectory(data.Run.Id), "baseline")), ct);
        await store.SaveVerificationAsync(Record(data, VerificationKind.Baseline, data.BaseCommit, outcome, runId: null), ct);
        return outcome;
    }

    // ---- Agent turns ----

    private async Task<StepOutcome> RunTurnAsync(RunData data, WorkerSession session, RunState state, StartTurnStep step, CancellationToken hostStopping)
    {
        var active = session.Active;
        var client = await session.ClientAsync(hostStopping);

        var sessionId = step.Session == SessionScope.Review ? await ReviewSessionAsync(data) : data.Thread.SessionId;

        // Compaction before a large turn (§8.6): a repair or rework turn that would start on a
        // nearly full context is compacted first, keeping what the task still needs.
        if (step.Session == SessionScope.Thread && active.Baselines.TryGetValue(sessionId, out var baseline)
            && ContextPolicy.ShouldCompactBefore(step.Kind, (double)baseline.TotalInputTokens / options.ContextLength))
        {
            try
            {
                if (await client.CompactAsync(sessionId, BriefComposer.CompactionInstruction(), hostStopping))
                    active.Baselines.TryRemove(sessionId, out _);
            }
            catch (HttpRequestException ex)
            {
                return new TurnEnded(TurnEndReason.Faulted, Detail: $"Compaction before the turn failed: {ex.Message}");
            }
        }

        var brief = BriefComposer.Compose(step, await BriefContextAsync(data, step, hostStopping), state with { Baseline = data.Baseline ?? state.Baseline }, options.Limits);
        var before = await FingerprintAsync(data, hostStopping);

        using var timeout = new CancellationTokenSource(options.Limits.TurnTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(hostStopping, timeout.Token);
        var turnToken = active.BeginTurn(step.Kind, state.WorkTurn, sessionId, linked.Token);

        TurnStreamResult? result = null;
        try
        {
            result = await client.RunTurnAsync(sessionId, step.Kind, brief, options.Limits.MaxToolCallsPerTurn, turnToken);
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

        var submission = active.Submission;
        if (submission is ReviewSubmission review)
            await store.SaveFindingsAsync(data.Run.Id, review.Findings, CancellationToken.None);

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
            return error.StartsWith("The turn exceeded", StringComparison.Ordinal)
                ? new TurnEnded(TurnEndReason.ToolCallLimit, FilesChanged: filesChanged)
                : new TurnEnded(TurnEndReason.Faulted, FilesChanged: filesChanged, Detail: error);
        }

        return new TurnEnded(TurnEndReason.Completed, submission, filesChanged);
    }

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

    private async Task<RunContext> BriefContextAsync(RunData data, StartTurnStep step, CancellationToken ct)
    {
        var details = await store.GetThreadAsync(data.Thread.Id, ct);
        var firstRequest = details?.Messages.FirstOrDefault(m => m.Author == MessageAuthor.User && m.Kind == MessageKind.Text)?.Text ?? data.Run.Request;

        var context = new RunContext(data.Project.Name, data.Branch, data.Project.DefaultBranch, data.Run.Kind == RunKind.Rework ? firstRequest : data.Run.Request)
        {
            VerificationSummary = string.Join("\n", data.Profile.Steps.SelectMany(s => s.Commands().Select(c => $"- {s.Name}: {c.Command.Display()}"))),
            CoverageThresholdPercent = data.Profile.Coverage?.ChangedLinesThresholdPercent,
        };

        if (step.Brief is BriefKind.Rework or BriefKind.Review)
        {
            var diff = await data.Workspace.DiffAsync(data.BaseCommit, ct);
            context = context with
            {
                ChangedFiles = [.. diff.Files.Select(f => f.Path)],
                Diff = diff.Patch,
                ChangedLineCount = diff.ChangedLineCount,
            };
        }

        if (step.Brief == BriefKind.Rework)
        {
            string? previous = null;
            if (details?.LatestHandoff is { } handoff)
                previous = JsonSerializer.Deserialize<HandoffEvidence>(handoff.EvidenceJson, FactoryWire.Json)?.Summary;
            context = context with { TesterFeedback = data.Run.Request, PreviousHandoffSummary = previous };
        }

        return context;
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

    private async Task<StepOutcome> VerifyAsync(RunData data, RunState state, CancellationToken ct)
    {
        var diff = await data.Workspace.DiffAsync(data.BaseCommit, ct);
        var outcome = await verifier.VerifyAsync(
            new VerificationRequest(data.Workspace.Path, data.Profile, diff.Files, Path.Combine(options.RunDirectory(data.Run.Id), $"verify-{clock.UtcNow:HHmmss}")), ct);

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
                        ct);
                }
                catch (Exception ex) when (ex is HttpRequestException or Infrastructure.GitHub.GitHubException)
                {
                    notes.Add($"The draft PR could not be opened: {ex.Message}");
                }
            }

            var thread = (await store.GetThreadAsync(data.Thread.Id, ct))!.Thread;
            var evidence = HandoffComposer.Evidence(state, new HandoffFacts(data.Branch, head, pullRequest?.Number, pullRequest?.Url)
            {
                CoverageThresholdPercent = data.Profile.Coverage?.ChangedLinesThresholdPercent,
                TokensUsed = thread.TokensUsed,
                BudgetCap = thread.BudgetCap,
                ChangedFiles = [.. diff.Files.Select(f => f.Path)],
                Notes = notes,
            });

            await store.SetRunCommitsAsync(data.Run.Id, null, head, null, ct);
            if (state.Review == ReviewStatus.FindingsFixed)
                await store.SetFindingsStatusAsync(data.Run.Id, FindingSeverity.Blocking, FindingStatus.Fixed, ct);
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
            }, ct);
            _handoffs[data.Run.Id] = evidence;
            return new HandoffCompleted(true);
        }
        catch (WorkspaceException ex)
        {
            // Local commits are kept; the push is retried on Resume (§16).
            return new HandoffCompleted(false, ex.Message);
        }
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, HandoffEvidence> _handoffs = new();

    // ---- Stop ----

    private async Task StopAsync(RunData data, RunState state, StopStep stop)
    {
        var command = new StopRunCommand(data.Run.Id, stop.Trigger, stop.Reason, stop.Message)
        {
            StateJson = RunStateJson.Serialize(state),
            Stage = state.Stage,
        };

        if (stop.Reason == StopReason.HandedOff && _handoffs.TryRemove(data.Run.Id, out var evidence))
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
