using System.Collections.Concurrent;
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
    IFactoryStore store, FactoryOptions options, IWorkspaceProvider workspaces, IWorkerLauncher launcher,
    IWorkerClientFactory clients, FactorySignals signals, IClock clock, ILogger<ChatExecutor> logger)
{
    internal const string Failed = "Litos could not answer that. Send the message again.";
    internal const string NoAnswer = "Litos read the code but did not write an answer. Ask again, perhaps more narrowly.";
    internal const string Stopped = "Litos stopped before answering, because the factory host was stopping. Send the message again.";

    /// <summary>One reader per project's reading copy: two answers on the same project take turns,
    /// since each checks out the branch it reads.</summary>
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _readingCopies = new();

    /// <summary>The address workers call back on; set with <see cref="RunExecutor.HostUrl"/>.</summary>
    public string HostUrl { get; set; } = "";

    public async Task ExecuteAsync(ClaimedRun claimed, ActiveRun active, CancellationToken hostStopping)
    {
        var (run, thread, project) = claimed;
        IWorkerHandle? handle = null;
        IWorkerClient? client = null;
        string? reply = null;
        string? failure = null;
        var readingCopy = _readingCopies.GetOrAdd(project.Id, _ => new SemaphoreSlim(1, 1));
        var holding = false;
        try
        {
            using var timeout = new CancellationTokenSource(options.ChatTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(hostStopping, timeout.Token, active.StopToken);
            var ct = linked.Token;

            await readingCopy.WaitAsync(ct);
            holding = true;

            var details = await store.GetThreadAsync(thread.Id, ct) ?? throw new InvalidOperationException("The thread no longer exists.");
            var workspace = workspaces.ReadingCopyFor(project);
            var branch = await CheckOutAsync(workspace, details, ct);

            handle = await launcher.LaunchAsync(
                new WorkerLaunch(
                    run.Id.ToString("N"), workspace.Path, thread.Provider, thread.Model,
                    options.ContextLength, options.DataDirectory, HostUrl, active.Secret)
                {
                    PtcEnabled = options.PtcEnabled,
                    TempDirectory = options.RunTempDirectory(run.Id),
                },
                ct);
            await store.SetRunWorkerAsync(run.Id, handle.ProcessId, handle.StartTime, CancellationToken.None);
            client = clients.Create(handle.BaseAddress, active.Secret);
            active.Client = client;

            var sessionId = $"chat-{run.Id:N}";
            var brief = BriefComposer.Chat(new ChatContext(
                project.Name, branch, thread.Title, details.LatestRun?.Request, Conversation(details, run), run.Request));
            var turnToken = active.BeginTurn(TurnKind.Chat, TurnKind.Chat, sessionId, ct, phase: "Chat");
            var result = await client.RunTurnAsync(sessionId, TurnKind.Chat, brief, options.ChatMaxToolCalls, turnToken);

            if (!string.IsNullOrWhiteSpace(result.Reply))
                reply = result.Reply;
            else if (active.BudgetRefusal is { } refusal)
                failure = $"Litos stopped before answering: {refusal}";
            else
                failure = result.Error is { Length: > 0 } error ? $"Litos could not answer that: {error}" : NoAnswer;
        }
        catch (OperationCanceledException) when (hostStopping.IsCancellationRequested)
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
            active.Client = null;
            if (client is not null)
                await TryAsync(() => client.ShutdownAsync(CancellationToken.None));
            if (handle is not null)
                await handle.DisposeAsync();
            if (holding)
                readingCopy.Release();

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
    internal static IReadOnlyList<ChatLine> Conversation(ThreadDetails details, TaskRun run)
    {
        var messages = details.Messages.OrderBy(m => m.Sequence).ToList();

        // The question itself is the brief's own section, not part of the thread so far.
        var question = messages.FindLastIndex(m => m.Author == MessageAuthor.User && m.Kind == MessageKind.Text && m.Text == run.Request);
        if (question >= 0)
            messages.RemoveRange(question, messages.Count - question);

        return [.. messages
            .Where(m => m.Kind is MessageKind.Text or MessageKind.Decision or MessageKind.DecisionAnswer or MessageKind.Handoff)
            .Select(m => new ChatLine(m.Author == MessageAuthor.User ? "Person" : "Factory", m.Text))];
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
}
