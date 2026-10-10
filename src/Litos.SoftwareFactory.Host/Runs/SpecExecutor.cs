using System.Text.Json;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Briefs;
using Litos.SoftwareFactory.Core.Lifecycle;
using Litos.SoftwareFactory.Core.Orchestration;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Store;

namespace Litos.SoftwareFactory.Host.Runs;

/// <summary>
/// Writes a specification (docs/software-factory/m2-architecture.md §5): one read-only turn on the
/// project's reading copy, at the default branch, that finishes with submit_spec. Its proposal is
/// recorded as the next revision and the task is a draft again, waiting for a person.
///
/// It is task work: it is charged to the task's budget, and stops as any run does. A pause or a
/// cancel, a budget refused, a failure: each leaves the task in that state, and resuming runs
/// the spec turn again from the start. It takes no repository lease.
/// </summary>
public sealed class SpecExecutor(
    IFactoryStore store, FactoryOptions options, ReadingCopies readingCopies, IWorkerLauncher launcher,
    IWorkerClientFactory clients, FactorySignals signals, IClock clock, Settings.FactorySettings settings, ILogger<SpecExecutor> logger)
{
    internal const string NoSpec = "The spec turn ended without proposing a specification. Resume to try again, or ask with @factory spec in other words.";

    /// <summary>The address workers call back on; set with <see cref="RunExecutor.HostUrl"/>.</summary>
    public string HostUrl { get; set; } = "";

    public async Task ExecuteAsync(ClaimedRun claimed, ActiveRun active, CancellationToken hostStopping)
    {
        var (run, thread, project) = claimed;
        IDisposable? holding = null;
        ReadingWorker? worker = null;
        try
        {
            using var timeout = new CancellationTokenSource(options.SpecTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(hostStopping, timeout.Token, active.StopToken);
            var ct = linked.Token;

            holding = await readingCopies.HoldAsync(project.Id, ct);
            var details = await store.GetThreadAsync(thread.Id, ct) ?? throw new InvalidOperationException("The thread no longer exists.");

            // A draft has no branch of its own yet: the specification is written against the default branch.
            var workspace = readingCopies.For(project);
            await workspace.EnsureClonedAsync(ct);
            await workspace.FetchAsync(ct);
            await workspace.CheckoutForReadingAsync(project.DefaultBranch, ct);

            worker = await ReadingWorker.StartAsync(launcher, clients, store, options, settings, HostUrl, claimed, active, workspace.Path, ct);

            var sessionId = $"spec-{run.Id:N}";
            var brief = BriefComposer.Spec(new SpecContext(
                project.Name, $"the default branch `{project.DefaultBranch}`", thread.Title, run.Request,
                Draft(details.LatestSpec), ReadingWorker.Conversation(details, $"spec {run.Request}")));
            var turnToken = active.BeginTurn(TurnKind.Spec, TurnKind.Spec, sessionId, ct, phase: "Spec");
            var result = await worker.Client.RunTurnAsync(sessionId, TurnKind.Spec, brief, options.SpecMaxToolCalls, turnToken);

            // A turn that failed while the host was stopping failed because it was (RunExecutor
            // does the same); a proposal that arrived is still recorded.
            if (result.Error is not null && active.Submission is null)
                hostStopping.ThrowIfCancellationRequested();
            await FinishAsync(claimed, active, result.Error);
        }
        catch (Exception ex) when (hostStopping.IsCancellationRequested && ex is OperationCanceledException or HttpRequestException or IOException)
        {
            // The run stays marked Running; the next start marks it Interrupted, which is the truth.
            logger.LogWarning("Spec run {RunId} was in progress when the host stopped.", run.Id);
        }
        catch (OperationCanceledException)
        {
            await FinishAsync(
                claimed, active,
                active.StopRequest == StopRequest.None ? $"The spec turn took longer than {options.SpecTimeout.TotalMinutes:0} minutes." : null);
        }
        catch (Exception ex) when (ex is WorkspaceException or HttpRequestException or IOException)
        {
            logger.LogWarning(ex, "Spec run {RunId} failed.", run.Id);
            await FinishAsync(claimed, active, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Spec run {RunId} failed in the host.", run.Id);
            await FinishAsync(claimed, active, $"The factory host failed while writing the specification: {ex.Message}");
        }
        finally
        {
            if (worker is not null)
                await worker.DisposeAsync();
            holding?.Dispose();

            try
            {
                Directory.Delete(options.RunTempDirectory(run.Id), recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            try
            {
                await store.ReconcileUsageAsync(run.Id, clock.UtcNow, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Unreported usage of spec run {RunId} could not be reconciled.", run.Id);
            }

            signals.EventsWritten();
        }
    }

    /// <summary>
    /// Records how the turn ended: its proposal, or the stop. A proposal that arrived is kept even
    /// when a stop was asked for at the same moment, since the work it stood for is done.
    /// </summary>
    private async Task FinishAsync(ClaimedRun claimed, ActiveRun active, string? error)
    {
        var runId = claimed.Run.Id;
        try
        {
            if (active.Submission is SpecSubmission proposal)
            {
                var spec = await store.ProposeSpecAsync(runId, proposal, clock.UtcNow, CancellationToken.None);
                logger.LogInformation("Spec run {RunId} proposed revision {Revision}.", runId, spec.Revision);
                return;
            }

            var (trigger, reason, message) = active.StopRequest switch
            {
                StopRequest.Cancel => (LifecycleTrigger.Cancel, StopReason.Cancelled, "Cancelled while the specification was being written."),
                StopRequest.Pause => (LifecycleTrigger.Pause, StopReason.PausedByUser, "Paused while the specification was being written. Resume to write it again."),
                _ when active.BudgetRefusal is { } refusal => (LifecycleTrigger.ExhaustBudget, StopReason.BudgetExhausted, refusal),
                _ => (LifecycleTrigger.Block, StopReason.NoCompletionCall, error is { Length: > 0 } ? $"{NoSpec} {error}" : NoSpec),
            };
            await store.StopRunAsync(
                new StopRunCommand(runId, trigger, reason, message) { Stage = Stage.Spec, ReleaseLease = true }, clock.UtcNow, CancellationToken.None);
        }
        catch (Exception ex) when (ex is StoreConflictException or StoreNotFoundException)
        {
            // The liveness sweep interrupts it: it is Running and, once this returns, no executor holds it.
            logger.LogWarning(ex, "Spec run {RunId} could not be finished.", runId);
        }
    }

    /// <summary>The newest revision as the brief shows it, when the spec is being revised.</summary>
    internal static SpecDraft? Draft(Specification? spec) => spec is null ? null : new SpecDraft(
        spec.Revision, spec.Summary, List(spec.AcceptanceCriteriaJson), List(spec.AffectedAreasJson), spec.TestPlan, List(spec.OpenQuestionsJson));

    internal static IReadOnlyList<string> List(string json) =>
        string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<string>>(json, FactoryWire.Json) ?? [];
}
