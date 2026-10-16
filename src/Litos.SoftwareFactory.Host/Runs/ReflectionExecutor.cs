using System.Text.Json;
using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Briefs;
using Litos.SoftwareFactory.Core.Orchestration;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Store;

namespace Litos.SoftwareFactory.Host.Runs;

/// <summary>
/// A lesson reflection (ReadMe_LitosSoftwareFactory_V1.md §23, m4-architecture.md §5.2): one turn,
/// after acceptance, whose only tool is submit_lessons. It reads what its brief carries — what
/// people corrected, decided and found on the task — not the code, though its worker starts on the
/// project's reading copy as every worker needs a directory. It takes no lease and never changes
/// the task's state; its calls are charged to the task within the reflection allowance. It never
/// throws past here: a failure is the job's, recorded with Retry and Skip, and the task stays accepted.
/// </summary>
public sealed class ReflectionExecutor(
    IFactoryStore store, FactoryOptions options, ReadingCopies readingCopies, IWorkerLauncher launcher,
    IWorkerClientFactory clients, FactorySignals signals, IClock clock, Settings.FactorySettings settings, Settings.FactorySkills skills,
    ILogger<ReflectionExecutor> logger)
{
    /// <summary>A reflection is one call that submits; a few tool calls allow a retry of a refused submission.</summary>
    internal const int MaxToolCalls = 4;

    /// <summary>The address workers call back on; set with <see cref="RunExecutor.HostUrl"/>.</summary>
    public string HostUrl { get; set; } = "";

    public async Task ExecuteAsync(ClaimedRun claimed, ActiveRun active, CancellationToken hostStopping)
    {
        var (run, thread, project) = claimed;
        IDisposable? holding = null;
        ReadingWorker? worker = null;
        string? failure = null;
        try
        {
            using var timeout = new CancellationTokenSource(options.ChatTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(hostStopping, timeout.Token, active.StopToken);
            var ct = linked.Token;

            var details = await store.GetThreadAsync(thread.Id, ct) ?? throw new InvalidOperationException("The thread no longer exists.");
            var existing = (await store.ListRetrievableLessonsAsync(project.Id, ct)).Select(l => l.Rule).ToList();
            var brief = BriefComposer.Reflection(Context(details, existing));

            holding = await readingCopies.HoldAsync(project.Id, ct);
            var workspace = readingCopies.For(project);
            await workspace.EnsureClonedAsync(ct);
            worker = await ReadingWorker.StartAsync(launcher, clients, store, options, settings, skills, HostUrl, claimed, active, workspace.Path, ct);

            var sessionId = $"reflection-{run.Id:N}";
            var turnToken = active.BeginTurn(TurnKind.Reflection, TurnKind.Reflection, sessionId, ct, phase: "Reflection");
            var result = await worker.Client.RunTurnAsync(sessionId, TurnKind.Reflection, brief, MaxToolCalls, turnToken);

            if (active.Submission is LessonsSubmission submitted)
                await store.CompleteReflectionAsync(run.Id, submitted.Lessons, clock.UtcNow, CancellationToken.None);
            else if (hostStopping.IsCancellationRequested)
                failure = "the factory host stopped while it ran";
            else if (active.BudgetRefusal is { } refusal)
                failure = refusal;
            else
                failure = result.Error is { Length: > 0 } error ? error : "the model finished without calling submit_lessons";
        }
        catch (Exception ex) when (hostStopping.IsCancellationRequested && ex is OperationCanceledException or HttpRequestException or IOException)
        {
            failure = "the factory host stopped while it ran";
        }
        catch (OperationCanceledException)
        {
            failure = active.StopRequest != StopRequest.None ? "it was stopped" : $"it took longer than {options.ChatTimeout.TotalMinutes:0} minutes";
        }
        catch (Exception ex) when (ex is WorkspaceException or HttpRequestException or IOException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Reflection run {RunId} failed.", run.Id);
            failure = ex.Message;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Reflection run {RunId} failed in the host.", run.Id);
            failure = "the factory host could not run it";
        }
        finally
        {
            if (worker is not null)
                await worker.DisposeAsync();
            holding?.Dispose();

            if (failure is not null)
            {
                try
                {
                    await store.FailReflectionAsync(run.Id, failure, clock.UtcNow, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    // The liveness sweep finishes it: it is Running and no executor holds it.
                    logger.LogWarning(ex, "Reflection run {RunId} could not be recorded as failed.", run.Id);
                }
            }

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
                logger.LogWarning(ex, "Unreported usage of reflection run {RunId} could not be reconciled.", run.Id);
            }

            signals.EventsWritten();
        }
    }

    /// <summary>
    /// What the reflection reads: the task, and each thing a person said, decided or judged on it, with
    /// the id to cite. A person's messages stand for their corrections; answered decisions; the review
    /// findings of every run with the verdicts people gave; the last handoff's summary.
    /// </summary>
    internal static ReflectionContext Context(ThreadDetails details, IReadOnlyList<string> existingLessons)
    {
        var sources = new List<ReflectionSource>();
        sources.AddRange(details.Messages
            .Where(m => m.Author == MessageAuthor.User && m.Kind == MessageKind.Text)
            .OrderBy(m => m.Sequence).TakeLast(20)
            .Select(m => new ReflectionSource("HumanCorrection", m.Id, m.Text)));
        sources.AddRange(details.Decisions
            .Where(d => d.Status == DecisionStatus.Answered)
            .Select(d => new ReflectionSource("Decision", d.Id, $"{d.Question} — answered: {d.Answer}")));
        sources.AddRange(details.EarlierFindings.Concat(details.Findings)
            .Select(f => new ReflectionSource("ReviewFinding", f.Id,
                $"{f.Severity} at {f.File}{(f.Line is { } line ? $":{line}" : "")}: {f.Text} ({(f.Verdict is { } verdict ? $"a person judged it {verdict}" : "not judged")}, {f.Status})")));
        if (details.LatestHandoff is { } handoff
            && JsonSerializer.Deserialize<HandoffEvidence>(handoff.EvidenceJson, FactoryWire.Json) is { } evidence)
            sources.Add(new ReflectionSource("Evidence", handoff.Id, evidence.Summary));

        return new ReflectionContext(details.Project.Name, details.Thread.Title, details.TaskRequest, sources, existingLessons);
    }
}
