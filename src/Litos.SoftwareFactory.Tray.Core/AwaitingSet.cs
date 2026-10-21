namespace Litos.SoftwareFactory.Tray;

/// <summary>What one change did to the set of tasks awaiting the user.</summary>
/// <param name="Changed">The list shown in the menu changed: a task joined, left, or was updated while waiting.</param>
/// <param name="Arrived">The task has just started waiting on the user: the moment to ring.</param>
public readonly record struct AwaitingChange(bool Changed, bool Arrived);

/// <summary>
/// The user's tasks that await them, kept up to date from the event stream: the C# form of the web
/// app's <c>newlyAwaiting</c> (domain/attention.ts). A task rings once, when it starts waiting.
/// Tasks already waiting when the set is first filled, other people's tasks, and further changes
/// to a task already waiting do not ring; a task that stops waiting and later waits again does.
/// </summary>
public sealed class AwaitingSet
{
    private readonly Dictionary<Guid, TrayTask> _waiting = [];

    /// <summary>Starts from the task list, without ringing for anything in it.</summary>
    public AwaitingSet(Guid userId, IEnumerable<TrayTask> tasks)
    {
        UserId = userId;
        foreach (var task in tasks.Where(t => t.AwaitsUser(userId)))
            _waiting[task.Id] = task;
    }

    public Guid UserId { get; }

    public int Count => _waiting.Count;

    /// <summary>The tasks awaiting the user, the most recently changed first, as the menu lists them.</summary>
    public IReadOnlyList<TrayTask> Tasks => [.. _waiting.Values.OrderByDescending(t => t.UpdatedAt).ThenBy(t => t.Title, StringComparer.CurrentCulture)];

    public bool Contains(Guid taskId) => _waiting.ContainsKey(taskId);

    /// <summary>Takes in a task's latest view.</summary>
    public AwaitingChange Apply(TrayTask task)
    {
        if (task.AwaitsUser(UserId))
        {
            var arrived = !_waiting.ContainsKey(task.Id);
            _waiting[task.Id] = task;
            return new AwaitingChange(true, arrived);
        }

        return new AwaitingChange(_waiting.Remove(task.Id), false);
    }
}
