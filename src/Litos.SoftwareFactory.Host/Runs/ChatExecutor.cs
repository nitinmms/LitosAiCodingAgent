using Litos.SoftwareFactory.Contracts;
using Litos.SoftwareFactory.Core.Briefs;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Store;

namespace Litos.SoftwareFactory.Host.Runs;

/// <summary>
/// Answers a plain message (docs/software-factory/m2-architecture.md §5): one read-only turn, on
/// the project's reading copy, whose last message is posted to the thread. A chat run never
/// changes the task's state, never takes the repository's lease, and is charged to its own
/// budget, not the task's.
/// </summary>
public sealed class ChatExecutor(
    IFactoryStore store, FactoryOptions options, ReadingCopies readingCopies, IWorkerLauncher launcher,
    IWorkerClientFactory clients, FactorySignals signals, IClock clock, Settings.FactorySettings settings, ILogger<ChatExecutor> logger)
{
    internal const string Failed = "Litos could not answer that. Send the message again.";
    internal const string NoAnswer = "Litos read the code but did not write an answer. Ask again, perhaps more narrowly.";
    internal const string Stopped = "Litos stopped before answering, because the factory host was stopping. Send the message again.";

    /// <summary>The address workers call back on; set with <see cref="RunExecutor.HostUrl"/>.</summary>
    public string HostUrl { get; set; } = "";

    public async Task ExecuteAsync(ClaimedRun claimed, ActiveRun active, CancellationToken hostStopping)
    {
        var (run, thread, project) = claimed;
        IDisposable? holding = null;
        ReadingWorker? worker = null;
        string? reply = null;
        string? failure = null;
        var progress = new ChatProgressTracker(run.Id, run.StartedAt ?? clock.UtcNow);
        using var stopReporting = new CancellationTokenSource();
        var reporting = ReportProgressAsync(progress, stopReporting.Token);
        try
        {
            using var timeout = new CancellationTokenSource(options.ChatTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(hostStopping, timeout.Token, active.StopToken);
            var ct = linked.Token;

            // Each answer checks out the branch it reads, so answers on one project take turns.
            holding = await readingCopies.HoldAsync(project.Id, ct);

            var details = await store.GetThreadAsync(thread.Id, ct) ?? throw new InvalidOperationException("The thread no longer exists.");
            var workspace = readingCopies.For(project);
            var branch = await CheckOutAsync(workspace, details, ct);
            progress.Set(ChatProgressTracker.Starting);

            worker = await ReadingWorker.StartAsync(launcher, clients, store, options, settings, HostUrl, claimed, active, workspace.Path, ct);

            var sessionId = $"chat-{run.Id:N}";
            var brief = BriefComposer.Chat(new ChatContext(
                project.Name, branch, thread.Title, details.TaskRequest, Conversation(details, run), run.Request));
            var turnToken = active.BeginTurn(TurnKind.Chat, TurnKind.Chat, sessionId, ct, phase: "Chat");
            progress.Set(ChatProgressTracker.Thinking);
            var result = await worker.Client.RunTurnAsync(sessionId, TurnKind.Chat, brief, options.ChatMaxToolCalls, turnToken, progress.Apply);

            if (!string.IsNullOrWhiteSpace(result.Reply))
                reply = result.Reply;
            else if (hostStopping.IsCancellationRequested)
                failure = Stopped; // the turn's error is the host going, not the question
            else if (active.BudgetRefusal is { } refusal)
                failure = $"Litos stopped before answering: {refusal}";
            else
                failure = result.Error is { Length: > 0 } error ? $"Litos could not answer that: {error}" : NoAnswer;
        }
        catch (Exception ex) when (hostStopping.IsCancellationRequested && ex is OperationCanceledException or HttpRequestException or IOException)
        {
            failure = Stopped;
        }
        catch (OperationCanceledException)
        {
            failure = active.StopRequest != StopRequest.None
                ? "The answer was stopped."
                : $"Litos took longer than {options.ChatTimeout.TotalMinutes:0} minutes to answer, so it was stopped. Ask again, perhaps more narrowly.";
        }
        catch (Exception ex) when (ex is WorkspaceException or HttpRequestException or IOException)
        {
            logger.LogWarning(ex, "Chat run {RunId} failed.", run.Id);
            failure = $"Litos could not answer that: {ex.Message}";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Chat run {RunId} failed in the host.", run.Id);
            failure = Failed;
        }
        finally
        {
            // Before the reply: the store also refuses a report on a finished run.
            await stopReporting.CancelAsync();
            await reporting;

            if (worker is not null)
                await worker.DisposeAsync();
            holding?.Dispose();

            try
            {
                await store.FinishChatRunAsync(run.Id, reply, failure, clock.UtcNow, CancellationToken.None);
            }
            catch (Exception ex)
            {
                // The liveness sweep finishes it: it is Running and no executor holds it.
                logger.LogWarning(ex, "Chat run {RunId} could not be finished.", run.Id);
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
                logger.LogWarning(ex, "Unreported usage of chat run {RunId} could not be reconciled.", run.Id);
            }

            signals.EventsWritten();
        }
    }

    /// <summary>
    /// Tells the thread what the answer is doing: at once, then whenever it has changed, at most
    /// once per <see cref="FactoryOptions.ChatProgressInterval"/>. A report that cannot be written
    /// is skipped; the answer itself does not depend on it.
    /// </summary>
    private async Task ReportProgressAsync(ChatProgressTracker progress, CancellationToken ct)
    {
        var reported = -1;
        try
        {
            while (true)
            {
                var version = progress.Version;
                if (version != reported)
                {
                    try
                    {
                        await store.AnnounceChatProgressAsync(progress.Snapshot, clock.UtcNow, ct);
                        signals.EventsWritten();
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogDebug(ex, "Progress of chat run {RunId} could not be reported.", progress.Snapshot.RunId);
                    }

                    reported = version;
                }

                await Task.Delay(options.ChatProgressInterval, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Puts the reading copy where the question is about: the task's branch once it has been
    /// handed off, else the default branch. A task branch that is gone from the remote (deleted
    /// after its merge) falls back to the default branch. Returns where it is, as the brief says it.
    /// </summary>
    private static async Task<string> CheckOutAsync(IWorkspace workspace, ThreadDetails details, CancellationToken ct)
    {
        await workspace.EnsureClonedAsync(ct);
        await workspace.FetchAsync(ct);

        var defaultBranch = details.Project.DefaultBranch;
        if (details.LatestHandoff is not null && details.Thread.Branch is { Length: > 0 } taskBranch)
        {
            try
            {
                await workspace.CheckoutForReadingAsync(taskBranch, ct);
                return $"the task branch `{taskBranch}`, as last handed off";
            }
            catch (WorkspaceException)
            {
                await workspace.CheckoutForReadingAsync(defaultBranch, ct);
                return $"the default branch `{defaultBranch}` (the task branch `{taskBranch}` is no longer on the remote)";
            }
        }

        await workspace.CheckoutForReadingAsync(defaultBranch, ct);
        return $"the default branch `{defaultBranch}`";
    }

    /// <summary>The thread before the question: what people said and what the factory reported.</summary>
    internal static IReadOnlyList<ChatLine> Conversation(ThreadDetails details, TaskRun run) => ReadingWorker.Conversation(details, run.Request);
}
