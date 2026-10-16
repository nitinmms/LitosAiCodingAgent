using Litos.SoftwareFactory.Core.Briefs;
using Litos.SoftwareFactory.Core.Ports;
using Litos.SoftwareFactory.Core.Store;

namespace Litos.SoftwareFactory.Host.Runs;

/// <summary>
/// A worker started on a reading copy for one read-only turn: a chat answer or a specification
/// (m2-architecture.md §5). Disposing it shuts the worker down.
/// </summary>
public sealed class ReadingWorker : IAsyncDisposable
{
    private readonly IWorkerHandle _handle;
    private readonly ActiveRun _active;

    private ReadingWorker(IWorkerHandle handle, IWorkerClient client, ActiveRun active)
    {
        _handle = handle;
        Client = client;
        _active = active;
    }

    public IWorkerClient Client { get; }

    /// <remarks>Its skills are the library, the reading copy's repository skills and the task's chosen
    /// skills as they are now; a read-only run records no capability snapshot and posts no note about them.</remarks>
    public static async Task<ReadingWorker> StartAsync(
        IWorkerLauncher launcher, IWorkerClientFactory clients, IFactoryStore store, FactoryOptions options, Settings.FactorySettings settings,
        Settings.FactorySkills skills, string hostUrl, ClaimedRun claimed, ActiveRun active, string workingCopy, CancellationToken ct)
    {
        var (run, thread, project) = claimed;
        var (revision, library, policy) = skills.Current();
        var chosen = skills.ForTask(thread);
        // A chat or a spec takes the lessons that match now, without a snapshot (m4-architecture.md §4.2).
        var lessons = Settings.FactorySkills.RunLessons(await skills.LessonsForAsync(claimed, ct));
        var prepared = await skills.PrepareAsync(run.Id, project.Id, workingCopy, revision, library, policy, chosen.Applied, ct, lessons);
        var handle = await launcher.LaunchAsync(
            new WorkerLaunch(
                run.Id.ToString("N"), workingCopy, thread.Provider, thread.Model,
                options.ContextLengthOf(thread), options.DataDirectory, hostUrl, active.Secret)
            {
                PtcEnabled = options.PtcOf(thread),
                ShellTimeoutSeconds = settings.Tools.ShellTimeoutSeconds,
                WebSearch = settings.WebSearch,
                SkillsPath = prepared.Path,
                TempDirectory = options.RunTempDirectory(run.Id),
            },
            ct);
        await store.SetRunWorkerAsync(run.Id, handle.ProcessId, handle.StartTime, CancellationToken.None);
        var client = clients.Create(handle.BaseAddress, active.Secret);
        active.Client = client;
        return new ReadingWorker(handle, client, active);
    }

    public async ValueTask DisposeAsync()
    {
        _active.Client = null;
        try
        {
            await Client.ShutdownAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
        }

        await _handle.DisposeAsync();
    }

    /// <summary>
    /// The thread before <paramref name="question"/>, for a brief whose session starts empty: what
    /// people said and what the factory reported, without status notes.
    /// </summary>
    public static IReadOnlyList<ChatLine> Conversation(ThreadDetails details, string question)
    {
        var messages = details.Messages.OrderBy(m => m.Sequence).ToList();

        // The question itself is the brief's own section, not part of the thread so far.
        var asked = messages.FindLastIndex(m => m.Author == MessageAuthor.User && m.Kind == MessageKind.Text && m.Text == question);
        if (asked >= 0)
            messages.RemoveRange(asked, messages.Count - asked);

        return [.. messages
            .Where(m => m.Kind is MessageKind.Text or MessageKind.Decision or MessageKind.DecisionAnswer or MessageKind.Handoff or MessageKind.Spec)
            .Select(m => new ChatLine(m.Author == MessageAuthor.User ? "Person" : "Factory", m.Text))];
    }
}
