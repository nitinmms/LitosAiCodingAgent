using System.Security.Claims;
using System.Text.Json;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Store;
using Litos.SoftwareFactory.Core.Verification;
using Litos.SoftwareFactory.Host.Auth;
using Litos.SoftwareFactory.Host.Runs;
using Litos.SoftwareFactory.Infrastructure.GitHub;
using Litos.SoftwareFactory.Infrastructure.Verification;

namespace Litos.SoftwareFactory.Host.Api;

public sealed record RegisterProjectRequest(
    string GitHubUrl, string? Name = null, string DefaultBranch = "main", string Preset = VerificationPresets.DotNet,
    double? CoverageThresholdPercent = null, bool PullRequestEnabled = true, string? ProfileJson = null);

public sealed record CreateThreadRequest(Guid ProjectId, string Title, string TypeLabel = "feature", long? BudgetCap = null);

/// <summary>MessageId is the client's own id for the message: the dispatch idempotency key.</summary>
public sealed record PostMessageRequest(string MessageId, string Text);

public sealed record AnswerDecisionRequest(string Answer);

public sealed record SetBudgetRequest(long? Cap);

/// <summary>What the host can do to a run that is executing right now.</summary>
public interface IRunControl
{
    /// <summary>Asks the thread's running turn to stop. False when nothing is running for it.</summary>
    bool RequestStop(Guid threadId, StopRequest request);

    /// <summary>Delivers a follow-up instruction to the thread's running turn.</summary>
    Task SteerAsync(Guid threadId, string text, CancellationToken ct);
}

/// <summary>
/// Assignment is a leading @factory mention (ReadMe_LitosSoftwareFactory_V1.md §5). Only a
/// mention at the very start of a user's own message delegates: one inside quoted text, a code
/// block or the middle of a sentence never does.
/// </summary>
public static class FactoryMention
{
    private const string Mention = "@factory";

    public static bool TryParse(string? text, out string request)
    {
        request = "";
        var trimmed = text?.TrimStart() ?? "";
        if (!trimmed.StartsWith(Mention, StringComparison.OrdinalIgnoreCase))
            return false;

        // "@factoryfoo" is a different word, not a mention.
        var rest = trimmed[Mention.Length..];
        if (rest.Length > 0 && !char.IsWhiteSpace(rest[0]))
            return false;

        request = rest.Trim();
        return request.Length > 0;
    }
}

public static class FactoryApi
{
    private static readonly string[] TaskTypes = ["bug", "feature", "refactor", "chore"];

    public static IEndpointRouteBuilder MapFactoryApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();

        api.MapGet("/settings", (FactoryOptions options) => Results.Ok(new
        {
            options.Provider,
            options.Model,
            options.DefaultBudget,
            options.PtcEnabled,
            // What fraction of a cached input token counts against a task's budget (§9).
            options.Budget.CachedInputWeight,
            Presets = VerificationPresets.Names,
            TaskTypes,
            PromptRevision = Core.Briefs.BriefComposer.Revision,
        }));

        // ---- Projects ----

        api.MapGet("/projects", async (IFactoryStore store, CancellationToken ct) =>
            Results.Ok((await store.ListProjectsAsync(ct)).Select(ProjectView)));

        api.MapPost("/projects", async (RegisterProjectRequest request, ClaimsPrincipal user, IFactoryStore store, IClock clock, CancellationToken ct) =>
        {
            if (!GitHubRepository.TryParse(request.GitHubUrl ?? "", out var repository))
                return Problem("gitHubUrl must be a GitHub repository URL, such as https://github.com/owner/repo.");
            if (string.IsNullOrWhiteSpace(request.DefaultBranch))
                return Problem("defaultBranch is required.");

            VerificationProfile profile;
            try
            {
                profile = request.ProfileJson is { Length: > 0 } json ? VerificationProfile.Parse(json) : VerificationPresets.Load(request.Preset);
            }
            catch (Exception ex) when (ex is ArgumentException or JsonException)
            {
                return Problem(ex.Message);
            }

            if (request.CoverageThresholdPercent is { } threshold)
                profile = profile with { Coverage = new CoverageRule(threshold) };

            var problems = profile.Validate();
            if (problems.Count > 0)
                return Problem("The verification profile is not usable: " + string.Join(" ", problems));

            try
            {
                var project = await store.AddProjectAsync(new Project
                {
                    Name = string.IsNullOrWhiteSpace(request.Name) ? repository!.Name : request.Name.Trim(),
                    GitHubOwner = repository!.Owner,
                    GitHubRepository = repository.Name,
                    DefaultBranch = request.DefaultBranch.Trim(),
                    PullRequestEnabled = request.PullRequestEnabled,
                    VerificationProfileJson = profile.ToJson(),
                    CreatedBy = user.UserId(),
                    CreatedAt = clock.UtcNow,
                }, ct);
                return Results.Created($"/api/projects/{project.Id}", ProjectView(project));
            }
            catch (StoreConflictException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        }).RequireAuthorization(FactoryRoles.Admin);

        // ---- Threads ----

        api.MapGet("/threads", async (Guid? projectId, IFactoryStore store, CancellationToken ct) =>
            Results.Ok((await store.ListThreadsAsync(projectId, ct)).Select(ThreadView)));

        api.MapPost("/threads", async (CreateThreadRequest request, ClaimsPrincipal user, IFactoryStore store, FactoryOptions options, IClock clock, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Title))
                return Problem("title is required.");
            if (!TaskTypes.Contains(request.TypeLabel))
                return Problem($"typeLabel must be one of: {string.Join(", ", TaskTypes)}.");
            if (request.BudgetCap is <= 0)
                return Problem("budgetCap must be positive.");

            try
            {
                var now = clock.UtcNow;
                var thread = await store.AddThreadAsync(new TaskThread
                {
                    ProjectId = request.ProjectId,
                    OwnerId = user.UserId(),
                    Title = request.Title.Trim(),
                    TypeLabel = request.TypeLabel,
                    BudgetCap = request.BudgetCap ?? options.DefaultBudget,
                    SessionId = Guid.NewGuid().ToString("N"),
                    Provider = options.Provider,
                    Model = options.Model,
                    CreatedAt = now,
                    UpdatedAt = now,
                }, ct);
                return Results.Created($"/api/threads/{thread.Id}", ThreadView(thread));
            }
            catch (StoreNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        });

        api.MapGet("/threads/{id:guid}", async (Guid id, IFactoryStore store, CancellationToken ct) =>
        {
            // Read before the thread itself: anything written in between is then replayed by the
            // event stream, never missed.
            var cursor = await store.LastEventSequenceAsync(id, ct);
            return await store.GetThreadAsync(id, ct) is { } details ? Results.Ok(DetailsView(details, cursor)) : Results.NotFound();
        });

        // The task's model calls, oldest first: what each reserved and what it was charged (§9).
        api.MapGet("/threads/{id:guid}/usage", async (Guid id, IFactoryStore store, CancellationToken ct) =>
        {
            if (await store.GetThreadAsync(id, ct) is null)
                return Results.NotFound();

            return Results.Ok((await store.ListUsageAsync(id, ct)).Select(u => new
            {
                u.Id,
                u.RunId,
                u.Model,
                u.EstimatedInput,
                u.Reserved,
                u.ActualInput,
                u.ActualCachedInput,
                u.ActualOutput,
                u.ActualReasoning,
                u.Charged,
                Status = u.Status.ToString(),
                // What the call was for: Implement, Rework, Repair, Review, LightReview, Nudge.
                u.Phase,
                u.CreatedAt,
                u.SettledAt,
            }));
        });

        api.MapPost("/threads/{id:guid}/messages", async (
            Guid id, PostMessageRequest request, ClaimsPrincipal user, IFactoryStore store, IRunControl runs, FactorySignals signals,
            IClock clock, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.MessageId))
                return Problem("messageId is required: it makes a retried request safe.");
            // M1 has no chat before delegation: every message is an assignment or a follow-up.
            if (!FactoryMention.TryParse(request.Text, out var text))
                return Problem("Start the message with @factory followed by what you want done.");

            DispatchResult result;
            try
            {
                result = await store.DispatchAsync(id, user.UserId(), request.MessageId, text, clock.UtcNow, ct);
            }
            catch (StoreNotFoundException)
            {
                return Results.NotFound();
            }

            signals.EventsWritten();
            switch (result.Outcome)
            {
                case DispatchOutcome.Rejected:
                    return Results.Conflict(new { error = result.Reason, thread = ThreadView(result.Thread) });
                case DispatchOutcome.Queued:
                    signals.WorkQueued();
                    break;
                case DispatchOutcome.FollowUp:
                    // Steering reaches the agent at a safe tool boundary; the message is already recorded.
                    await runs.SteerAsync(id, text, ct);
                    break;
            }

            return Results.Accepted(value: new { outcome = result.Outcome.ToString(), result.RunId, thread = ThreadView(result.Thread) });
        });

        api.MapPost("/threads/{id:guid}/budget", (Guid id, SetBudgetRequest request, IFactoryStore store, FactorySignals signals, IClock clock, CancellationToken ct) =>
            request.Cap is <= 0
                ? Task.FromResult(Problem("cap must be positive, or null for no cap."))
                : ActAsync(signals, () => store.SetBudgetCapAsync(id, request.Cap, clock.UtcNow, ct)));

        api.MapPost("/threads/{id:guid}/accept", (Guid id, ClaimsPrincipal user, IFactoryStore store, FactorySignals signals, IClock clock, CancellationToken ct) =>
            ActAsync(signals, () => store.ApplyUserActionAsync(id, user.UserId(), LifecycleTrigger.Accept, clock.UtcNow, ct)));

        api.MapPost("/threads/{id:guid}/pause", (Guid id, ClaimsPrincipal user, IFactoryStore store, IRunControl runs, FactorySignals signals, IClock clock, CancellationToken ct) =>
            StopAsync(id, user, store, runs, signals, clock, StopRequest.Pause, LifecycleTrigger.Pause, ct));

        api.MapPost("/threads/{id:guid}/cancel", (Guid id, ClaimsPrincipal user, IFactoryStore store, IRunControl runs, FactorySignals signals, IClock clock, CancellationToken ct) =>
            StopAsync(id, user, store, runs, signals, clock, StopRequest.Cancel, LifecycleTrigger.Cancel, ct));

        // Takes back a change request made after a handoff: the rework run is dropped, what it
        // had edited is discarded, and the task waits for testing on its last handoff again.
        api.MapPost("/threads/{id:guid}/withdraw", async (
            Guid id, ClaimsPrincipal user, IFactoryStore store, IWorkspaceProvider workspaces, FactorySignals signals, IClock clock,
            ILoggerFactory loggers, CancellationToken ct) =>
        {
            WithdrawResult result;
            try
            {
                result = await store.WithdrawChangesAsync(id, user.UserId(), clock.UtcNow, ct);
            }
            catch (StoreNotFoundException)
            {
                return Results.NotFound();
            }
            catch (StoreConflictException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }

            // Only while this thread still holds the repository: then the edits in the working
            // copy are the withdrawn run's, and no other task can be using it.
            if (result is { HeldLease: true, Thread.Branch: { Length: > 0 } branch })
            {
                try
                {
                    // Not the request's token: a browser that goes away must not leave the
                    // working copy half cleaned with the lease still held.
                    await workspaces.For(result.Project).DiscardUncommittedChangesAsync(branch, result.Project.DefaultBranch, CancellationToken.None);
                }
                catch (Exception ex) when (ex is WorkspaceException or InvalidOperationException or IOException)
                {
                    loggers.CreateLogger("Litos.SoftwareFactory.Host.Withdraw").LogWarning(ex, "The withdrawn run's edits could not be discarded for thread {ThreadId}.", id);
                    await store.AddFactoryMessageAsync(
                        id, MessageKind.Status,
                        $"The edits the withdrawn change request had made could not be discarded: {ex.Message} They will be set aside, not deleted, when the next task starts on this repository.",
                        null, clock.UtcNow, CancellationToken.None);
                }
                finally
                {
                    await store.ReleaseLeaseAsync(id, CancellationToken.None);
                }
            }

            signals.EventsWritten();
            signals.WorkQueued(); // the repository is free for a task that was waiting on it
            return Results.Ok(ThreadView(result.Thread));
        });

        // Where the task's pull request stands on GitHub now: merging and closing happen there.
        api.MapGet("/threads/{id:guid}/pull-request", async (Guid id, IFactoryStore store, PullRequestStatus status, CancellationToken ct) =>
        {
            if (await store.GetThreadAsync(id, ct) is not { } details)
                return Results.NotFound();
            if (details.Thread.PullRequestNumber is not { } number)
                return Results.NotFound(new { error = "This task has no pull request." });

            var state = await status.GetAsync(details.Project.GitHubOwner, details.Project.GitHubRepository, number, ct);
            return Results.Ok(new
            {
                Number = number,
                Url = details.Thread.PullRequestUrl,
                // "Unknown" when the host has no GitHub token or GitHub did not answer.
                State = state?.ToString() ?? "Unknown",
            });
        });

        api.MapPost("/threads/{id:guid}/resume", async (Guid id, ClaimsPrincipal user, IFactoryStore store, FactorySignals signals, IClock clock, CancellationToken ct) =>
        {
            if (await store.GetThreadAsync(id, ct) is not { } details)
                return Results.NotFound();

            // "Resume" is one button; which transition it is depends on why the task stopped.
            LifecycleTrigger? trigger = details.Thread.State switch
            {
                LifecycleState.PausedUser => LifecycleTrigger.Resume,
                LifecycleState.PausedBudget => LifecycleTrigger.RaiseBudgetAndResume,
                LifecycleState.Blocked => LifecycleTrigger.ResolveBlocker,
                LifecycleState.Interrupted => LifecycleTrigger.Recover,
                _ => null,
            };
            if (trigger is null)
                return Results.Conflict(new { error = $"A task that is {details.Thread.State} has nothing to resume." });

            var result = await ActAsync(signals, () => store.ApplyUserActionAsync(id, user.UserId(), trigger.Value, clock.UtcNow, ct));
            signals.WorkQueued();
            return result;
        });

        api.MapPost("/decisions/{id:guid}/answer", async (Guid id, AnswerDecisionRequest request, ClaimsPrincipal user, IFactoryStore store, FactorySignals signals, IClock clock, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Answer))
                return Problem("answer is required.");

            var result = await ActAsync(signals, () => store.AnswerDecisionAsync(id, user.UserId(), request.Answer.Trim(), clock.UtcNow, ct));
            signals.WorkQueued();
            return result;
        });

        return app;
    }

    /// <summary>Pause and cancel: a running task is stopped through its worker; a waiting one is
    /// changed in the store directly.</summary>
    private static async Task<IResult> StopAsync(
        Guid id, ClaimsPrincipal user, IFactoryStore store, IRunControl runs, FactorySignals signals, IClock clock,
        StopRequest request, LifecycleTrigger trigger, CancellationToken ct)
    {
        if (await store.GetThreadAsync(id, ct) is not { } details)
            return Results.NotFound();

        if (details.Thread.State == LifecycleState.Running)
        {
            return runs.RequestStop(id, request)
                ? Results.Accepted(value: new { thread = ThreadView(details.Thread), stopping = true })
                : Results.Conflict(new { error = "The task is marked as running, but no run is active in this host. Recover it after a restart." });
        }

        return await ActAsync(signals, () => store.ApplyUserActionAsync(id, user.UserId(), trigger, clock.UtcNow, ct));
    }

    private static async Task<IResult> ActAsync(FactorySignals signals, Func<Task<TaskThread>> action)
    {
        try
        {
            var thread = await action();
            signals.EventsWritten();
            return Results.Ok(ThreadView(thread));
        }
        catch (StoreNotFoundException)
        {
            return Results.NotFound();
        }
        catch (StoreConflictException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
    }

    private static IResult Problem(string message) => Results.BadRequest(new { error = message });

    private static object ProjectView(Project project) => new
    {
        project.Id,
        project.Name,
        GitHub = $"{project.GitHubOwner}/{project.GitHubRepository}",
        project.DefaultBranch,
        project.PullRequestEnabled,
        project.ProfileRevision,
        CoverageThresholdPercent = VerificationProfile.Parse(project.VerificationProfileJson).Coverage?.ChangedLinesThresholdPercent,
        project.CreatedAt,
    };

    internal static object ThreadView(TaskThread thread) => new
    {
        thread.Id,
        thread.ProjectId,
        thread.Title,
        thread.TypeLabel,
        Stage = thread.Stage.ToString(),
        State = thread.State.ToString(),
        thread.StateReason,
        thread.BudgetCap,
        thread.TokensUsed,
        thread.TokensReserved,
        thread.Branch,
        thread.PullRequestNumber,
        thread.PullRequestUrl,
        thread.Provider,
        thread.Model,
        // Every provider in M1 honours the output cap and reports usage, so budgets are strict.
        BudgetPrecision = "strict",
        thread.Revision,
        thread.CreatedAt,
        thread.UpdatedAt,
    };

    private static object DetailsView(ThreadDetails details, long eventCursor) => new
    {
        // Pass to GET .../events?after= to hear everything that happened after this snapshot.
        EventCursor = eventCursor,
        Thread = ThreadView(details.Thread),
        Project = ProjectView(details.Project),
        Messages = details.Messages.Select(m => new
        {
            m.Id,
            m.Sequence,
            Author = m.Author.ToString(),
            Kind = m.Kind.ToString(),
            m.Text,
            m.DecisionId,
            Payload = Parse(m.PayloadJson),
            m.CreatedAt,
        }),
        Decisions = details.Decisions.Select(d => new
        {
            d.Id,
            d.Question,
            d.WhyItBlocks,
            Options = Parse(d.OptionsJson),
            d.Recommendation,
            d.Impact,
            Status = d.Status.ToString(),
            d.Answer,
            d.CreatedAt,
            d.AnsweredAt,
        }),
        Run = details.LatestRun is null ? null : new
        {
            details.LatestRun.Id,
            Kind = details.LatestRun.Kind.ToString(),
            Status = details.LatestRun.Status.ToString(),
            StopReason = details.LatestRun.StopReason?.ToString(),
            details.LatestRun.BaselineCommit,
            details.LatestRun.HeadCommit,
            details.LatestRun.PromptRevision,
        },
        Verification = details.LatestVerification is null ? null : new
        {
            Build = details.LatestVerification.Build.ToString(),
            UnitTests = details.LatestVerification.UnitTests.ToString(),
            Coverage = details.LatestVerification.Coverage.ToString(),
            details.LatestVerification.PassedCount,
            details.LatestVerification.FailedCount,
            details.LatestVerification.SkippedCount,
            details.LatestVerification.ChangedLineCoveragePercent,
        },
        Findings = details.Findings.Select(f => new { Severity = f.Severity.ToString(), f.File, f.Line, f.Text, Status = f.Status.ToString() }),
        Handoff = details.LatestHandoff is null ? null : new
        {
            details.LatestHandoff.Branch,
            details.LatestHandoff.CommitSha,
            details.LatestHandoff.PullRequestNumber,
            details.LatestHandoff.PullRequestUrl,
            Evidence = Parse(details.LatestHandoff.EvidenceJson),
        },
    };

    private static JsonElement? Parse(string? json) => string.IsNullOrEmpty(json) ? null : JsonDocument.Parse(json).RootElement.Clone();
}
